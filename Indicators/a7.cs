// a7.cs – Footprint Order-Flow con Imbalance Bid/Ask diagonal, Stack Imbalance y Rectángulos de zona
// ----------------------------------------------------------------------
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Xml.Serialization;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a7 : Indicator
    {
        private class PriceVolume
        {
            public double BidVolume { get; set; }
            public double AskVolume { get; set; }
            public bool IsBidImbalance { get; set; }
            public bool IsAskImbalance { get; set; }
        }

        // -------- NUEVO: clase para zonas de imbalance ------------------------
        private class ImbalanceZone
        {
            public int BarNumber;   // Donde se formó la zona
            public double PriceHigh;
            public double PriceLow;
            public bool Active = true;
            public string Direction; // "supply" o "demand"
        }

        private Dictionary<int, SortedDictionary<double, PriceVolume>> barPriceData;
        private double bestBid = 0.0;
        private double bestAsk = double.MaxValue;

        private SharpDX.Direct2D1.SolidColorBrush brushGeneral;
        private SharpDX.Direct2D1.SolidColorBrush brushImbalance;
        private TextFormat textFormat;
        private TextFormat textFormatLarge;

        private float offsetXBid = 10f;
        private float offsetXAsk = 50f;
        private float offsetY = 0f;

        private bool lastBackgroundWhite;

        // Totales por barra
        private List<double> barDelta;
        private List<double> barVolume;
        private List<double> barCumDelta;

        // --------- PROPIEDAD ZONAS IMBALANCE ----------
        private List<ImbalanceZone> imbalanceZones;

        // --------- PROPERTIES -----------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Font Size", Order = 0, GroupName = "Parameters")]
        public int FontSizeProp { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Background White", Description = "Tick if your chart background is white. Default = off (black)", Order = 1, GroupName = "Parameters")]
        public bool BackgroundWhite { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Imbalance %", Description = "Imbalance ratio (e.g. 300 for 300%)", Order = 2, GroupName = "Parameters")]
        public int ImbalancePercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stack Imbalance", Description = "Number of consecutive imbalances to form a stack", Order = 3, GroupName = "Parameters")]
        public int StackImbalance { get; set; }

        // --------- STATE MACHINE -------------------------------------------
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "a7";
                Description = "Footprint Bid/Ask per price level + Imbalance + Stack Imbalance + Imbalance Zones";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = false;
                DrawHorizontalGridLines = false;
                DrawVerticalGridLines = false;
                PaintPriceMarkers = false;

                FontSizeProp = 12;
                BackgroundWhite = false;
                ImbalancePercent = 300;
                StackImbalance = 3;
            }
            else if (State == State.Configure)
            {
                barPriceData = new Dictionary<int, SortedDictionary<double, PriceVolume>>();
                imbalanceZones = new List<ImbalanceZone>();
                barDelta = new List<double>();
                barVolume = new List<double>();
                barCumDelta = new List<double>();
            }
            else if (State == State.DataLoaded)
            {
                BuildBrushes();
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSizeProp);
                textFormatLarge = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSizeProp * 3);
                lastBackgroundWhite = BackgroundWhite;
            }
            else if (State == State.Terminated)
            {
                DisposeGraphics();
            }
        }

        private void DisposeGraphics()
        {
            brushGeneral?.Dispose();
            brushImbalance?.Dispose();
            textFormat?.Dispose();
            textFormatLarge?.Dispose();
        }

        private void BuildBrushes()
        {
            brushGeneral?.Dispose();
            brushImbalance?.Dispose();

            Color4 cText = BackgroundWhite ? new Color4(0, 0, 0, 1f)
                                           : new Color4(1, 1, 1, 1f);

            // Azul para imbalances (iOS blue: #007AFF)
            Color4 cImb = new Color4(0.0f, 0.48f, 1.0f, 1f);

            if (RenderTarget != null)
            {
                brushGeneral = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, cText);
                brushImbalance = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, cImb);
            }
        }

        // --------- MARKET DATA HANDLING -------------------------------------
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (e.Position != 0) return;
            if (e.MarketDataType == MarketDataType.Bid)
                bestBid = e.Price;
            else if (e.MarketDataType == MarketDataType.Ask)
                bestAsk = e.Price;
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0 || e.MarketDataType != MarketDataType.Last)
                return;

            int barIndex = CurrentBar;
            double price = e.Price;
            double vol = e.Volume;

            if (!barPriceData.ContainsKey(barIndex))
                barPriceData[barIndex] = new SortedDictionary<double, PriceVolume>();
            if (!barPriceData[barIndex].ContainsKey(price))
                barPriceData[barIndex][price] = new PriceVolume();

            var pv = barPriceData[barIndex][price];
            if (price >= bestAsk)
                pv.AskVolume += vol;
            else if (price <= bestBid)
                pv.BidVolume += vol;
            else
            {
                pv.BidVolume += vol / 2.0;
                pv.AskVolume += vol / 2.0;
            }
        }

        // --------- BAR UPDATE: manejar borrado de zonas ----------------------
        protected override void OnBarUpdate()
        {
            // Revisar si el precio cruza alguna zona
            foreach (var zone in imbalanceZones)
            {
                if (!zone.Active) continue;
                // Deactivate supply once price moves four ticks above the zone
                if (zone.Direction == "supply" && Close[0] > zone.PriceHigh + TickSize * 4)
                    zone.Active = false;
                // Deactivate demand once price drops four ticks below the zone
                if (zone.Direction == "demand" && Close[0] < zone.PriceLow - TickSize * 4)
                    zone.Active = false;
            }

            if (CurrentBar >= 1 && CurrentBar > barDelta.Count)
            {
                int barIdx = CurrentBar - 1;
                double d = 0.0, v = 0.0;
                if (barPriceData.ContainsKey(barIdx))
                {
                    foreach (var pv in barPriceData[barIdx].Values)
                    {
                        d += pv.AskVolume - pv.BidVolume;
                        v += pv.AskVolume + pv.BidVolume;
                    }
                }

                double prev = barIdx > 0 ? barCumDelta[barIdx - 1] : 0.0;
                barDelta.Add(d);
                barVolume.Add(v);
                barCumDelta.Add(prev + d);
            }
        }

        // --------- RENDER ---------------------------------------------------
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (ChartBars == null || RenderTarget == null)
                return;

            if (lastBackgroundWhite != BackgroundWhite)
            {
                BuildBrushes();
                lastBackgroundWhite = BackgroundWhite;
            }

            if (Math.Abs(textFormat.FontSize - FontSizeProp) > 0.1f)
            {
                textFormat.Dispose();
                textFormatLarge.Dispose();
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSizeProp);
                textFormatLarge = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSizeProp * 3);
            }

            int firstBar = ChartBars.FromIndex;
            int lastBar = Math.Min(ChartBars.ToIndex, CurrentBar);

            // -------- DIBUJAR RECTÁNGULOS DE ZONAS ACTIVAS --------------
            foreach (var zone in imbalanceZones)
            {
                if (!zone.Active) continue;

                float xLeft = ChartControl.GetXByBarIndex(ChartBars, zone.BarNumber);
                float xRight = ChartControl.CanvasRight;
                float yTop = (float)chartScale.GetYByValue(zone.PriceHigh);
                float yBot = (float)chartScale.GetYByValue(zone.PriceLow);

                var rect = new RectangleF(xLeft, Math.Min(yTop, yBot), xRight - xLeft, Math.Abs(yBot - yTop));

                Color4 rectColor = zone.Direction == "supply"
                    ? new Color4(0.9f, 0.23f, 0.23f, 0.23f)   // rojo suave supply
                    : new Color4(0.2f, 0.9f, 0.2f, 0.17f);    // verde transparente demand

                using (var fillBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, rectColor))
                    RenderTarget.FillRectangle(rect, fillBrush);

                using (var borderBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, rectColor))
                    RenderTarget.DrawRectangle(rect, borderBrush, 1.0f);
            }

            // -------- FOOTPRINT + IMBALANCES COMO ANTES ----------------
            for (int i = firstBar; i <= lastBar; i++)
            {
                float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                float xBid = xCenter + offsetXBid;
                float xAsk = xCenter + offsetXAsk;

                if (!barPriceData.ContainsKey(i)) continue;
                var levels = barPriceData[i];

                // Reset imbalance flags
                foreach (var pv in levels.Values)
                {
                    pv.IsBidImbalance = false;
                    pv.IsAskImbalance = false;
                }

                // ----------- IMBALANCE DIAGONAL -------------------------
                var priceLevels = levels.Keys.OrderByDescending(p => p).ToList();
                double imbalanceRatio = ImbalancePercent / 100.0;

                // Scan imbalances y marcar flags
                for (int idx = 0; idx < priceLevels.Count - 1; idx++)
                {
                    double price = priceLevels[idx];
                    double priceBelow = priceLevels[idx + 1];

                    var pv = levels[price];
                    var pvBelow = levels[priceBelow];

                    // Bid imbalance: Bid vs Ask abajo
                    if (pv.BidVolume >= 2 && pvBelow.AskVolume >= 2 &&
                        pv.BidVolume >= pvBelow.AskVolume * imbalanceRatio)
                    {
                        pv.IsBidImbalance = true;
                    }

                    // Ask imbalance: Ask vs Bid arriba
                    if (pv.AskVolume >= 2 && pvBelow.BidVolume >= 2 &&
                        pv.AskVolume >= pvBelow.BidVolume * imbalanceRatio)
                    {
                        pv.IsAskImbalance = true;
                    }
                }

                // ----------- STACK IMBALANCE DETECTION ------------------------------
                // Identifica secuencias de imbalances
                List<int> bidImbIdx = priceLevels
                    .Select((p, idx) => levels[p].IsBidImbalance ? idx : -1)
                    .Where(idx => idx >= 0).ToList();
                List<int> askImbIdx = priceLevels
                    .Select((p, idx) => levels[p].IsAskImbalance ? idx : -1)
                    .Where(idx => idx >= 0).ToList();

                // Busca stacks bid (demand, abajo) y ask (supply, arriba)
                List<(int start, int end)> bidStacks = FindStacks(bidImbIdx, StackImbalance);
                List<(int start, int end)> askStacks = FindStacks(askImbIdx, StackImbalance);

                // --------- REGISTRA ZONAS NUEVAS SI NO EXISTEN YA EN EL BAR ----------
                // Bid stack (zona de demand, verde)
                foreach (var stack in bidStacks)
                {
                    // Usa priceLevels para convertir idx a precio
                    double priceHigh = priceLevels[stack.start];
                    double priceLow = priceLevels[stack.end];

                    // Evita duplicar zonas para mismo rango en el mismo bar
                    if (!imbalanceZones.Any(z => z.BarNumber == i && z.PriceHigh == priceHigh && z.PriceLow == priceLow && z.Direction == "demand")
                        && Bars.GetClose(i) >= priceLow - TickSize * 4)
                    {
                        imbalanceZones.Add(new ImbalanceZone
                        {
                            BarNumber = i,
                            PriceHigh = priceHigh,
                            PriceLow = priceLow,
                            Active = true,
                            Direction = "demand"
                        });
                    }
                }

                // Ask stack (zona de supply, rojo)
                foreach (var stack in askStacks)
                {
                    double priceHigh = priceLevels[stack.start];
                    double priceLow = priceLevels[stack.end];

                    if (!imbalanceZones.Any(z => z.BarNumber == i && z.PriceHigh == priceHigh && z.PriceLow == priceLow && z.Direction == "supply")
                        && Bars.GetClose(i) <= priceHigh + TickSize * 4)
                    {
                        imbalanceZones.Add(new ImbalanceZone
                        {
                            BarNumber = i,
                            PriceHigh = priceHigh,
                            PriceLow = priceLow,
                            Active = true,
                            Direction = "supply"
                        });
                    }
                }

                // ----------- FOOTPRINT Y NÚMEROS COMO ANTES --------------------------
                for (int idx = 0; idx < priceLevels.Count; idx++)
                {
                    double price = priceLevels[idx];
                    float yPos = (float)chartScale.GetYByValue(price) + offsetY;
                    var pv = levels[price];

                    // Dibuja Bid
                    if (pv.BidVolume > 0)
                    {
                        string s = pv.BidVolume.ToString("0");
                        var colorBrush = pv.IsBidImbalance ? brushImbalance : brushGeneral;
                        using (var layoutB = new TextLayout(Core.Globals.DirectWriteFactory, s, textFormat, 100, 20))
                            RenderTarget.DrawTextLayout(new Vector2(xBid, yPos), layoutB, colorBrush);
                    }

                    // Dibuja Ask
                    if (pv.AskVolume > 0)
                    {
                        string s = pv.AskVolume.ToString("0");
                        var colorBrush = pv.IsAskImbalance ? brushImbalance : brushGeneral;
                        using (var layoutA = new TextLayout(Core.Globals.DirectWriteFactory, s, textFormat, 100, 20))
                            RenderTarget.DrawTextLayout(new Vector2(xAsk, yPos), layoutA, colorBrush);
                    }
                }

                // ----- Delta, Cumulative Delta y Volumen para la barra -----
                double dBar = 0.0, vBar = 0.0;
                foreach (var pvSum in levels.Values)
                {
                    dBar += pvSum.AskVolume - pvSum.BidVolume;
                    vBar += pvSum.AskVolume + pvSum.BidVolume;
                }

                double cBar = i < barCumDelta.Count
                    ? barCumDelta[i]
                    : ((i > 0 && i - 1 < barCumDelta.Count ? barCumDelta[i - 1] : 0.0) + dBar);

                float rowH = textFormatLarge.FontSize + 2f;
                float baseY = ChartPanel.Y + ChartPanel.H - rowH * 3f - 4f;

                // Fondo dinámico para Delta
                float intensity = Math.Min(1f, (float)Math.Abs(dBar) / 1000f);
                var deltaColor = dBar >= 0 ? new Color4(0f, 1f, 0f, 0.5f + 0.5f * intensity)
                                           : new Color4(1f, 0f, 0f, 0.5f + 0.5f * intensity);

                float cellW = 60f;
                var rectDelta = new RectangleF(xCenter - cellW / 2, baseY, cellW, rowH);
                using (var bg = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, deltaColor))
                    RenderTarget.FillRectangle(rectDelta, bg);
                using (var border = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, deltaColor))
                    RenderTarget.DrawRectangle(rectDelta, border, 1f);

                string sDelta = dBar.ToString("0");
                using (var tlDelta = new TextLayout(Core.Globals.DirectWriteFactory, sDelta, textFormatLarge, cellW, rowH))
                    RenderTarget.DrawTextLayout(new Vector2(rectDelta.X, rectDelta.Y), tlDelta, brushGeneral);

                var rectCum = new RectangleF(xCenter - cellW / 2, baseY + rowH, cellW, rowH);
                using (var border2 = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, brushGeneral.Color))
                    RenderTarget.DrawRectangle(rectCum, border2, 1f);
                string sCum = cBar.ToString("0");
                using (var tlCum = new TextLayout(Core.Globals.DirectWriteFactory, sCum, textFormatLarge, cellW, rowH))
                    RenderTarget.DrawTextLayout(new Vector2(rectCum.X, rectCum.Y), tlCum, brushGeneral);

                var rectVol = new RectangleF(xCenter - cellW / 2, baseY + rowH * 2, cellW, rowH);
                using (var border3 = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, brushGeneral.Color))
                    RenderTarget.DrawRectangle(rectVol, border3, 1f);
                string sVol = vBar.ToString("0");
                using (var tlVol = new TextLayout(Core.Globals.DirectWriteFactory, sVol, textFormatLarge, cellW, rowH))
                    RenderTarget.DrawTextLayout(new Vector2(rectVol.X, rectVol.Y), tlVol, brushGeneral);
            }
        }

        /// <summary>
        /// Encuentra stacks de índices consecutivos de tamaño mínimo stackMin.
        /// Devuelve tuplas (start, end).
        /// </summary>
        private static List<(int start, int end)> FindStacks(List<int> indices, int stackMin)
        {
            var stacks = new List<(int start, int end)>();
            if (indices.Count < stackMin) return stacks;
            int count = 1, start = indices[0];
            for (int i = 1; i < indices.Count; i++)
            {
                if (indices[i] == indices[i - 1] + 1)
                {
                    count++;
                }
                else
                {
                    if (count >= stackMin)
                        stacks.Add((start, indices[i - 1]));
                    start = indices[i];
                    count = 1;
                }
            }
            if (count >= stackMin)
                stacks.Add((start, indices[indices.Count - 1]));
            return stacks;
        }
    }
}


#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private a7[] cachea7;
		public a7 a7(int fontSizeProp, bool backgroundWhite, int imbalancePercent, int stackImbalance)
		{
			return a7(Input, fontSizeProp, backgroundWhite, imbalancePercent, stackImbalance);
		}

		public a7 a7(ISeries<double> input, int fontSizeProp, bool backgroundWhite, int imbalancePercent, int stackImbalance)
		{
			if (cachea7 != null)
				for (int idx = 0; idx < cachea7.Length; idx++)
					if (cachea7[idx] != null && cachea7[idx].FontSizeProp == fontSizeProp && cachea7[idx].BackgroundWhite == backgroundWhite && cachea7[idx].ImbalancePercent == imbalancePercent && cachea7[idx].StackImbalance == stackImbalance && cachea7[idx].EqualsInput(input))
						return cachea7[idx];
			return CacheIndicator<a7>(new a7(){ FontSizeProp = fontSizeProp, BackgroundWhite = backgroundWhite, ImbalancePercent = imbalancePercent, StackImbalance = stackImbalance }, input, ref cachea7);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.a7 a7(int fontSizeProp, bool backgroundWhite, int imbalancePercent, int stackImbalance)
		{
			return indicator.a7(Input, fontSizeProp, backgroundWhite, imbalancePercent, stackImbalance);
		}

		public Indicators.a7 a7(ISeries<double> input , int fontSizeProp, bool backgroundWhite, int imbalancePercent, int stackImbalance)
		{
			return indicator.a7(input, fontSizeProp, backgroundWhite, imbalancePercent, stackImbalance);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.a7 a7(int fontSizeProp, bool backgroundWhite, int imbalancePercent, int stackImbalance)
		{
			return indicator.a7(Input, fontSizeProp, backgroundWhite, imbalancePercent, stackImbalance);
		}

		public Indicators.a7 a7(ISeries<double> input , int fontSizeProp, bool backgroundWhite, int imbalancePercent, int stackImbalance)
		{
			return indicator.a7(input, fontSizeProp, backgroundWhite, imbalancePercent, stackImbalance);
		}
	}
}

#endregion
