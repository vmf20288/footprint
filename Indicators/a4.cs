#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
#endregion

// ──────────────────────────────────────────────────────────────
//  Indicator a4  •  Supply/Demand zones + AOI + 4 price levels
// ──────────────────────────────────────────────────────────────
namespace NinjaTrader.NinjaScript.Indicators
{
    public class a4 : Indicator
    {
        // ───────────────  USER PARAMETERS  ───────────────
        [Range(0.01, 1.0)]
        [Display(Name = "Size vela base", Order = 1, GroupName = "Parameters",
            Description = "Máx. % que mide el cuerpo de la vela base respecto al cuerpo de la vela agresiva.")]
        [NinjaScriptProperty]
        public double SizeVelaBase { get; set; }

        [Range(0.01, 1.0)]
        [Display(Name = "Size wick vela base (AOI)", Order = 2, GroupName = "Parameters",
            Description = "Máx. % que mide la mecha AOI de la vela base respecto al cuerpo de la vela agresiva.")]
        [NinjaScriptProperty]
        public double SizeWickVelaBase { get; set; }

        [Range(0.01, 1.0)]
        [Display(Name = "Batalla en wick agresiva", Order = 3, GroupName = "Parameters",
            Description = "Máx. % que mide el wick de la vela agresiva (en su misma dirección) respecto a su cuerpo.")]
        [NinjaScriptProperty]
        public double BatallaWickAgresiva { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "# velas rompe", Order = 4, GroupName = "Parameters",
            Description = "Velas consecutivas que cierran fuera para eliminar zona.")]
        [NinjaScriptProperty]
        public int BreakCandlesNeeded { get; set; }

        [Display(Name = "Rota Option", Order = 5, GroupName = "Parameters",
            Description = "1 = elimina inmediato, 2 = requiere dos rompimientos tras reingreso.")]
        [NinjaScriptProperty]
        public string RotaOption { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Ticks Max Zona", Order = 6, GroupName = "Parameters",
            Description = "Altura máxima en ticks; > ⇒ no se crea zona.")]
        [NinjaScriptProperty]
        public int TicksMaxZona { get; set; }

        [Display(Name = "Background White", Order = 10, GroupName = "Appearance",
            Description = "Marca si tu gráfico tiene fondo blanco (líneas negras).")]
        [NinjaScriptProperty]
        public bool BackgroundWhite { get; set; }

        // ───────────────  INTERNAL STATE  ───────────────
        private List<ZoneInfo> zones;
        private List<LLLineInfo> llLines;
        private SolidColorBrush brushFill;
        private SolidColorBrush brushOutline;
        private StrokeStyle strokeStyleDotted;
        private SharpDX.DirectWrite.Factory textFactory;
        private SharpDX.DirectWrite.TextFormat textFormat;

        // ───────────────  LIFECYCLE  ───────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "a4 – Zonas Supply/Demand con parámetros renombrados.";
                Name        = "a4";
                Calculate   = Calculate.OnBarClose;
                IsOverlay   = true;

                SizeVelaBase        = 0.21;
                SizeWickVelaBase    = 0.32;
                BatallaWickAgresiva = 0.13;
                BreakCandlesNeeded  = 2;
                RotaOption          = "1";
                TicksMaxZona        = 300;
                BackgroundWhite     = false;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Minute, 60);
                AddDataSeries(BarsPeriodType.Minute, 30);
                AddDataSeries(BarsPeriodType.Minute, 15);
                AddDataSeries(BarsPeriodType.Minute, 45);
                AddDataSeries(BarsPeriodType.Minute, 90);
                AddDataSeries(BarsPeriodType.Minute, 120);
                AddDataSeries(BarsPeriodType.Minute, 180);
                AddDataSeries(BarsPeriodType.Minute, 240);
                AddDataSeries(BarsPeriodType.Minute, 10);
                AddDataSeries(BarsPeriodType.Minute, 5);

                zones   = new List<ZoneInfo>();
                llLines = new List<LLLineInfo>();
            }
            else if (State == State.DataLoaded)
            {
                textFactory = new SharpDX.DirectWrite.Factory();
                textFormat  = new SharpDX.DirectWrite.TextFormat(textFactory, "Arial", 12f);
            }
            else if (State == State.Terminated)
            {
                DisposeResources();
            }
        }

        // ───────────────  BAR-BY-BAR  ───────────────
        protected override void OnBarUpdate()
        {
            if (BarsInProgress == 0) return;
            if (CurrentBars[BarsInProgress] < 2) return;

            CheckCreateZone();
            CheckBreakZones();
        }

        // ───────────────  CREAR ZONA  ───────────────
        private void CheckCreateZone()
        {
            int bip = BarsInProgress;

            // Vela base
            double baseOpen   = Opens[bip][1];
            double baseClose  = Closes[bip][1];
            double baseHigh   = Highs[bip][1];
            double baseLow    = Lows[bip][1];
            DateTime baseTime = Times[bip][1];

            // Vela agresiva
            double nextOpen   = Opens[bip][0];
            double nextClose  = Closes[bip][0];
            double nextHigh   = Highs[bip][0];
            double nextLow    = Lows[bip][0];

            double baseBody = Math.Abs(baseClose - baseOpen);
            double nextBody = Math.Abs(nextClose - nextOpen);

            bool baseIsGreen = baseClose > baseOpen;
            bool baseIsRed   = baseClose < baseOpen;
            bool nextIsGreen = nextClose > nextOpen;
            bool nextIsRed   = nextClose < nextOpen;

            // SUPPLY
            if (baseIsGreen && nextIsRed)
            {
                double wickAOI  = baseOpen - baseLow;               
                double wickAgg  = Math.Max(nextClose - nextLow, 0);  // solo mecha inferior
                bool condBody   = baseBody < SizeVelaBase      * nextBody;
                bool condAOI    = wickAOI  <= SizeWickVelaBase * nextBody;
                bool condWickAg = (wickAgg / nextBody) <= BatallaWickAgresiva;

                if (condBody && condAOI && condWickAg)
                    CreateZone(baseTime, true, bip,
                               Math.Max(nextHigh, baseHigh), baseOpen, baseLow);
            }
            // DEMAND
            else if (baseIsRed && nextIsGreen)
            {
                double wickAOI  = baseHigh - baseOpen;              
                double wickAgg  = Math.Max(nextHigh - nextClose, 0); // solo mecha superior
                bool condBody   = baseBody < SizeVelaBase      * nextBody;
                bool condAOI    = wickAOI  <= SizeWickVelaBase * nextBody;
                bool condWickAg = (wickAgg / nextBody) <= BatallaWickAgresiva;

                if (condBody && condAOI && condWickAg)
                    CreateZone(baseTime, false, bip,
                               baseOpen, Math.Min(nextLow, baseLow), baseHigh);
            }
        }

        private void CreateZone(DateTime time, bool isSupply, int bip,
                                double topPrice, double bottomPrice, double aoi)
        {
            double zoneTicks = (topPrice - bottomPrice) / TickSize;
            if (zoneTicks > TicksMaxZona) return;

            zones.Add(new ZoneInfo(time, isSupply, bip,
                                   topPrice, bottomPrice, aoi));
            llLines.Add(new LLLineInfo(time, isSupply, aoi, bip));
        }

        // ───────────────  ROMPER ZONAS  ───────────────
        private void CheckBreakZones()
        {
            int bip = BarsInProgress;
            double closeCurrent = Closes[bip][0];

            for (int i = zones.Count - 1; i >= 0; i--)
            {
                ZoneInfo z = zones[i];
                if (z.DataSeries != bip)
                    continue;

                bool isOutside = z.IsSupply
                                  ? closeCurrent > z.TopPrice
                                  : closeCurrent < z.BottomPrice;

                if (RotaOption == "2")
                {
                    if (isOutside)
                    {
                        if (!z.HasBrokenOnce)
                            z.HasBrokenOnce = true;
                        else
                            RemoveZone(i);
                    }
                    else
                        z.HasBrokenOnce = false;
                }
                else // modo 1
                {
                    z.ConsecutiveBreaks = isOutside
                        ? z.ConsecutiveBreaks + 1
                        : 0;

                    if (z.ConsecutiveBreaks >= BreakCandlesNeeded)
                        RemoveZone(i);
                }
            }
        }

        private void RemoveZone(int idx)
        {
            ZoneInfo z = zones[idx];
            zones.RemoveAt(idx);

            for (int j = llLines.Count - 1; j >= 0; j--)
                if (llLines[j].Time       == z.Time
                 && llLines[j].IsSupply   == z.IsSupply
                 && llLines[j].DataSeries == z.DataSeries)
                    llLines.RemoveAt(j);
        }

        // ───────────────  RENDER  ───────────────
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            EnsureResources();

            float xRight = ChartPanel.X + ChartPanel.W;

            // Dibujar zonas
            foreach (ZoneInfo z in zones)
            {
                float yTop      = chartScale.GetYByValue(z.TopPrice);
                float yBottom   = chartScale.GetYByValue(z.BottomPrice);
                float rectTop   = Math.Min(yTop, yBottom);
                float rectBot   = Math.Max(yTop, yBottom);
                float height    = rectBot - rectTop;
                float xBase     = chartControl.GetXByTime(z.Time);
                float width     = xRight - xBase;
                var rect        = new RectangleF(xBase, rectTop, width, height);

                RenderTarget.FillRectangle(rect, brushFill);
                RenderTarget.DrawRectangle(rect, brushOutline, 1f);

                // Línea discontinua 50 %
                float midPrice = (float)z.Area2;
                float yMid     = chartScale.GetYByValue(midPrice);
                RenderTarget.DrawLine(new Vector2(xBase, yMid),
                                      new Vector2(xRight, yMid),
                                      brushOutline, 1f, strokeStyleDotted);

                // Línea base
                float yBaseOpen = chartScale.GetYByValue(z.IsSupply ? z.BottomPrice : z.TopPrice);
                RenderTarget.DrawLine(new Vector2(xBase, yBaseOpen),
                                      new Vector2(xRight, yBaseOpen),
                                      brushOutline, 1f);

                // Time-frame label
                if (textFactory != null && textFormat != null)
                {
                    string tf = bipToTf(z.DataSeries);
                    using var tl = new SharpDX.DirectWrite.TextLayout(textFactory, tf, textFormat, 50, textFormat.FontSize);
                    float tx = xRight - tl.Metrics.Width - 5;
                    float ty = z.IsSupply ? (yBaseOpen + 5) : (yBaseOpen - tl.Metrics.Height - 5);
                    RenderTarget.DrawTextLayout(new Vector2(tx, ty), tl, brushOutline);
                }
            }

            // Dibujar AOI
            foreach (LLLineInfo line in llLines)
            {
                float y        = chartScale.GetYByValue(line.Price);
                float xBase     = chartControl.GetXByTime(line.Time);
                float width     = xRight - xBase;
                var   p1        = new Vector2(xBase, y);
                var   p2        = new Vector2(xRight, y);

                RenderTarget.DrawLine(p1, p2, brushOutline, 2f);

                if (textFactory != null && textFormat != null)
                {
                    string lbl = "AOI " + bipToTf(line.DataSeries);
                    using var tl = new SharpDX.DirectWrite.TextLayout(textFactory, lbl, textFormat, 100, textFormat.FontSize);
                    float tx = xRight - tl.Metrics.Width - 5;
                    float ty = line.IsSupply ? (y + 5) : (y - tl.Metrics.Height - 5);
                    RenderTarget.DrawTextLayout(new Vector2(tx, ty), tl, brushOutline);
                }
            }
        }

        // ───────────────  RESOURCES  ───────────────
        private void EnsureResources()
        {
            if (brushFill == null)
                brushFill = new SolidColorBrush(RenderTarget, new Color(0.8f, 0.8f, 0.8f, 0.4f));

            Color c = BackgroundWhite
                      ? new Color(0f, 0f, 0f, 1f)
                      : new Color(1f, 1f, 1f, 1f);

            brushOutline?.Dispose();
            brushOutline = new SolidColorBrush(RenderTarget, c);

            if (strokeStyleDotted == null)
            {
                var props = new StrokeStyleProperties { DashStyle = DashStyle.Custom };
                strokeStyleDotted = new StrokeStyle(RenderTarget.Factory, props, new[] { 2f, 2f });
            }
        }

        private void DisposeResources()
        {
            brushFill?.Dispose();        brushFill = null;
            brushOutline?.Dispose();     brushOutline = null;
            strokeStyleDotted?.Dispose(); strokeStyleDotted = null;
            textFormat?.Dispose();       textFormat = null;
            textFactory?.Dispose();      textFactory = null;
        }

        // ───────────────  PUBLIC API  ───────────────
        public int GetZoneCount() => zones.Count;

        public bool TryGetZone(int index,
                               out bool   isSupply,
                               out double area1,
                               out double area2,
                               out double area3,
                               out double aoi,
                               out int    dataSeries)
        {
            if (index < 0 || index >= zones.Count)
            {
                isSupply = false; area1 = area2 = area3 = aoi = 0; dataSeries = 0;
                return false;
            }
            ZoneInfo z = zones[index];
            isSupply   = z.IsSupply;
            area1      = z.Area1;
            area2      = z.Area2;
            area3      = z.Area3;
            aoi        = z.AOI;
            dataSeries = z.DataSeries;
            return true;
        }

        private string bipToTf(int bip) => bip switch
        {
            1 => "60", 2 => "30", 3 => "15", 4 => "45",
            5 => "90", 6 => "120", 7 => "180", 8 => "240",
            9 => "10", 10 => "5",
            _ => BarsPeriod.Value.ToString()
        };

        // ───────────────  INTERNAL CLASSES  ───────────────
        private class ZoneInfo
        {
            public ZoneInfo(DateTime time, bool isSupply, int dataSeries,
                            double topPrice, double bottomPrice, double aoi)
            {
                Time        = time;
                IsSupply    = isSupply;
                DataSeries  = dataSeries;
                TopPrice    = topPrice;
                BottomPrice = bottomPrice;
                AOI         = aoi;
                Area1       = isSupply ? TopPrice    : BottomPrice;
                Area3       = isSupply ? BottomPrice : TopPrice;
                Area2       = (Area1 + Area3) / 2.0;
            }
            public DateTime Time;
            public bool     IsSupply;
            public int      DataSeries;
            public double   TopPrice;
            public double   BottomPrice;
            public double   AOI;
            public double   Area1;
            public double   Area2;
            public double   Area3;
            public int      ConsecutiveBreaks = 0;
            public bool     HasBrokenOnce     = false;
        }

        private class LLLineInfo
        {
            public LLLineInfo(DateTime time, bool isSupply, double price, int dataSeries)
            {
                Time       = time;
                IsSupply   = isSupply;
                Price      = price;
                DataSeries = dataSeries;
            }
            public DateTime Time;
            public bool     IsSupply;
            public double   Price;
            public int      DataSeries;
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private a4[] cachea4;
		public a4 a4(double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, string rotaOption, int ticksMaxZona, bool backgroundWhite)
		{
			return a4(Input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona, backgroundWhite);
		}

		public a4 a4(ISeries<double> input, double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, string rotaOption, int ticksMaxZona, bool backgroundWhite)
		{
			if (cachea4 != null)
				for (int idx = 0; idx < cachea4.Length; idx++)
					if (cachea4[idx] != null && cachea4[idx].SizeVelaBase == sizeVelaBase && cachea4[idx].SizeWickVelaBase == sizeWickVelaBase && cachea4[idx].BatallaWickAgresiva == batallaWickAgresiva && cachea4[idx].BreakCandlesNeeded == breakCandlesNeeded && cachea4[idx].RotaOption == rotaOption && cachea4[idx].TicksMaxZona == ticksMaxZona && cachea4[idx].BackgroundWhite == backgroundWhite && cachea4[idx].EqualsInput(input))
						return cachea4[idx];
			return CacheIndicator<a4>(new a4(){ SizeVelaBase = sizeVelaBase, SizeWickVelaBase = sizeWickVelaBase, BatallaWickAgresiva = batallaWickAgresiva, BreakCandlesNeeded = breakCandlesNeeded, RotaOption = rotaOption, TicksMaxZona = ticksMaxZona, BackgroundWhite = backgroundWhite }, input, ref cachea4);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.a4 a4(double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, string rotaOption, int ticksMaxZona, bool backgroundWhite)
		{
			return indicator.a4(Input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona, backgroundWhite);
		}

		public Indicators.a4 a4(ISeries<double> input , double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, string rotaOption, int ticksMaxZona, bool backgroundWhite)
		{
			return indicator.a4(input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona, backgroundWhite);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.a4 a4(double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, string rotaOption, int ticksMaxZona, bool backgroundWhite)
		{
			return indicator.a4(Input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona, backgroundWhite);
		}

		public Indicators.a4 a4(ISeries<double> input , double sizeVelaBase, double sizeWickVelaBase, double batallaWickAgresiva, int breakCandlesNeeded, string rotaOption, int ticksMaxZona, bool backgroundWhite)
		{
			return indicator.a4(input, sizeVelaBase, sizeWickVelaBase, batallaWickAgresiva, breakCandlesNeeded, rotaOption, ticksMaxZona, backgroundWhite);
		}
	}
}

#endregion
