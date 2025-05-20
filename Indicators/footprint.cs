// a5.cs – Updated Footprint Order‑Flow indicator for NinjaTrader 8.1.4.2
// ----------------------------------------------------------------------
// Key additions (May 2025)
//  1. Added two more table rows under Delta → Cumulative Delta and Volume.
//  2. Session‑based cumulative delta is already calculated; now it is displayed.
//  3. Per‑bar volume captured and exposed via helper (GetVolume).  Volume row
//     always prints text in white with transparent background.
//  4. Dynamic colour intensity for Cumulative Delta, similar to Delta.
//  5. Adaptive layout: bottom margin & Y‑offsets recalculated for 3 rows.
//  6. Helper brushes rebuilt on theme change; new constant white brush.
//-----------------------------------------------------------------------
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Xml.Serialization;
using System.ComponentModel.DataAnnotations;   // <- gives access to [Display]
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a5 : Indicator
    {
        // ----- INTERNAL TYPES ------------------------------------------------
        private class PriceVolume
        {
            public double BidVolume { get; set; }
            public double AskVolume { get; set; }
        }

        // ----- FIELDS --------------------------------------------------------
        private Dictionary<int, Dictionary<double, PriceVolume>> barPriceData;
        private double bestBid = 0.0;
        private double bestAsk = double.MaxValue;

        private SharpDX.Direct2D1.SolidColorBrush brushGeneral;
        private SharpDX.Direct2D1.SolidColorBrush brushVolumeWhite;
        private TextFormat                           textFormat;

        private float offsetXBid = 10f;
        private float offsetXAsk = 50f;
        private float offsetY    = 0f;

        private float rectHeight   = 30f;
        private float bottomMargin = 40f;

        // Cumulative‑delta & volume plumbing ---------------------------------
        private Series<double> cumulativeDelta;
        private Dictionary<int, double> perBarDelta;     // Delta per bar
        private Dictionary<int, double> perBarCumDelta;  // Cum‑delta per bar
        private Dictionary<int, double> perBarVolume;    // Volume per bar

        // Track change for dynamic brush rebuild
        private bool lastBackgroundWhite;

        // ----- PROPERTIES ----------------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Font Size", Order = 0, GroupName = "Parameters")]
        public int FontSizeProp { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Background White", Description = "Tick if your chart background is white. Default = off (black)", Order = 1, GroupName = "Parameters")]
        public bool BackgroundWhite { get; set; }

        // Expose series & helpers -------------------------------------------
        [Browsable(false)]
        [XmlIgnore]
        public Series<double> CumulativeDeltaSeries => cumulativeDelta;

        public double GetDelta(int barsAgo)  => perBarDelta.ContainsKey(CurrentBar - barsAgo)  ? perBarDelta[CurrentBar - barsAgo]  : 0.0;
        public double GetVolume(int barsAgo) => perBarVolume.ContainsKey(CurrentBar - barsAgo) ? perBarVolume[CurrentBar - barsAgo] : 0.0;

        // ----- STATE MACHINE -------------------------------------------------
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "a5";
                Description             = "Footprint with Level 2, Delta, CumDelta & Volume display (session‑reset cumulative delta).";
                Calculate               = Calculate.OnEachTick;
                IsOverlay               = true;
                DisplayInDataBox        = false;
                DrawOnPricePanel        = false;
                DrawHorizontalGridLines = false;
                DrawVerticalGridLines   = false;
                PaintPriceMarkers       = false;

                FontSizeProp            = 12;
                BackgroundWhite         = false; // default assumes dark charts
            }
            else if (State == State.Configure)
            {
                barPriceData    = new Dictionary<int, Dictionary<double, PriceVolume>>();
                perBarDelta     = new Dictionary<int, double>();
                perBarCumDelta  = new Dictionary<int, double>();
                perBarVolume    = new Dictionary<int, double>();
                cumulativeDelta = new Series<double>(this, MaximumBarsLookBack.Infinite);
            }
            else if (State == State.DataLoaded)
            {
                BuildBrushes();
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSizeProp);
                lastBackgroundWhite = BackgroundWhite;
            }
            else if (State == State.Terminated)
            {
                DisposeGraphics();
            }
        }

        // ----- GRAPHICS HELPERS ---------------------------------------------
        private void DisposeGraphics()
        {
            brushGeneral?.Dispose();
            brushVolumeWhite?.Dispose();
            textFormat?.Dispose();
        }

        private void BuildBrushes()
        {
            // Dispose old brushes first
            brushGeneral?.Dispose();
            brushVolumeWhite?.Dispose();

            Color4 cText = BackgroundWhite ? new Color4(0, 0, 0, 1f)  // black text for light bg
                                           : new Color4(1, 1, 1, 1f); // white text for dark bg
            Color4 cVol  = new Color4(1, 1, 1, 1f);                    // Volume text always white

            if (RenderTarget != null)
            {
                brushGeneral     = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, cText);
                brushVolumeWhite = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, cVol);
            }
        }

        // ----- MARKET DATA HANDLING -----------------------------------------
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (e.Position != 0) return;            // only top of book
            if (e.MarketDataType == MarketDataType.Bid)
                bestBid = e.Price;
            else if (e.MarketDataType == MarketDataType.Ask)
                bestAsk = e.Price;
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0 || e.MarketDataType != MarketDataType.Last)
                return;

            int    barIndex = CurrentBar;
            double price    = e.Price;
            double vol      = e.Volume;

            if (!barPriceData.ContainsKey(barIndex))
                barPriceData[barIndex] = new Dictionary<double, PriceVolume>();
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

        // ----- BAR UPDATE ----------------------------------------------------
        protected override void OnBarUpdate()
        {
            if (!barPriceData.ContainsKey(CurrentBar))
                return;

            // Compute per‑bar Bid / Ask sums
            double sumAsk = 0, sumBid = 0;
            foreach (var pv in barPriceData[CurrentBar].Values)
            {
                sumAsk += pv.AskVolume;
                sumBid += pv.BidVolume;
            }
            double barDelta   = sumAsk - sumBid;
            double barVolume  = sumAsk + sumBid;
            perBarDelta[CurrentBar]  = barDelta;
            perBarVolume[CurrentBar] = barVolume;

            // Session‑reset cumulative delta
            bool newSession = Bars.IsFirstBarOfSession;
            if (newSession)
                cumulativeDelta[0] = barDelta;
            else
                cumulativeDelta[0] = cumulativeDelta[1] + barDelta;

            perBarCumDelta[CurrentBar] = cumulativeDelta[0];
        }

        // ----- RENDER --------------------------------------------------------
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (ChartBars == null || RenderTarget == null)
                return;

            // Re‑create graphics artefacts if property changed at run‑time
            if (lastBackgroundWhite != BackgroundWhite)
            {
                BuildBrushes();
                lastBackgroundWhite = BackgroundWhite;
            }

            // Adjust font size dynamically
            if (Math.Abs(textFormat.FontSize - FontSizeProp) > 0.1f)
            {
                textFormat.Dispose();
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSizeProp);
            }

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;
            float barWidth = (float)chartControl.GetBarPaintWidth(ChartBars);

            float yBottom = (float)chartScale.GetYByValue(chartScale.MinValue);
            const int rowCount = 3; // Delta, CumDelta, Volume
            float yTop    = yBottom - bottomMargin - rectHeight * rowCount;

            // Pre‑compute maxima for intensity scaling
            double maxAbsDelta    = 0.0;
            double maxAbsCumDelta = 0.0;
            for (int i = firstBar; i <= lastBar; i++)
            {
                if (perBarDelta.ContainsKey(i))
                    maxAbsDelta = Math.Max(maxAbsDelta, Math.Abs(perBarDelta[i]));
                if (perBarCumDelta.ContainsKey(i))
                    maxAbsCumDelta = Math.Max(maxAbsCumDelta, Math.Abs(perBarCumDelta[i]));
            }
            if (maxAbsDelta.Equals(0.0))    maxAbsDelta    = 1.0;
            if (maxAbsCumDelta.Equals(0.0)) maxAbsCumDelta = 1.0;

            int deltaFontSize = FontSizeProp + 8;
            using (var deltaFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", deltaFontSize))
            {
                for (int i = firstBar; i <= lastBar; i++)
                {
                    float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                    float xLeft   = xCenter - barWidth / 2f;

                    // -------------------- DELTA CELL -----------------------
                    DrawCell(perBarDelta, i, maxAbsDelta, xLeft, yTop,        barWidth, rectHeight, deltaFormat, brushGeneral);
                    // ---------------- CUM DELTA CELL -----------------------
                    DrawCell(perBarCumDelta, i, maxAbsCumDelta, xLeft, yTop + rectHeight, barWidth, rectHeight, deltaFormat, brushGeneral);
                    // ------------------ VOLUME CELL ------------------------
                    DrawVolumeCell(i, xLeft, yTop + rectHeight * 2, barWidth, rectHeight, deltaFormat);

                    // Bid / Ask per price level within candle (unchanged)
                    if (!barPriceData.ContainsKey(i)) continue;

                    float xBid = xCenter + offsetXBid;
                    float xAsk = xCenter + offsetXAsk;

                    foreach (var kvp in barPriceData[i].OrderByDescending(k => k.Key))
                    {
                        float yPos = (float)chartScale.GetYByValue(kvp.Key) + offsetY;
                        var   pv   = kvp.Value;

                        if (pv.BidVolume > 0)
                        {
                            string s = pv.BidVolume.ToString("0");
                            using (var layoutB = new TextLayout(Core.Globals.DirectWriteFactory, s, textFormat, 100, 20))
                                RenderTarget.DrawTextLayout(new Vector2(xBid, yPos), layoutB, brushGeneral);
                        }
                        if (pv.AskVolume > 0)
                        {
                            string s = pv.AskVolume.ToString("0");
                            using (var layoutA = new TextLayout(Core.Globals.DirectWriteFactory, s, textFormat, 100, 20))
                                RenderTarget.DrawTextLayout(new Vector2(xAsk, yPos), layoutA, brushGeneral);
                        }
                    }
                }
            }
        }

        // ----- CELL HELPERS --------------------------------------------------
        private void DrawCell(Dictionary<int, double> source, int barIndex, double maxAbs, float xLeft, float yTop, float width, float height, TextFormat fmt, SharpDX.Direct2D1.Brush textBrush)
        {
            double value = source.ContainsKey(barIndex) ? source[barIndex] : 0.0;
            float intensity = (float)(Math.Abs(value) / maxAbs);
            intensity = Math.Max(0.2f, Math.Min(1f, intensity));

            Color4 fillColor = value >= 0
                ? new Color4(0f, intensity, 0f, intensity)
                : new Color4(intensity, 0f, 0f, intensity);

            var rect = new RectangleF(xLeft, yTop, width, height);
            using (var fillBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, fillColor))
                RenderTarget.FillRectangle(rect, fillBrush);

            RenderTarget.DrawRectangle(rect, brushGeneral, 1f);

            string txt = value.ToString("0");
            using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, txt, fmt, width, height))
            {
                var m = layout.Metrics;
                float tx = xLeft + (width  - m.Width)  / 2f;
                float ty = yTop  + (height - m.Height) / 2f;
                RenderTarget.DrawTextLayout(new Vector2(tx, ty), layout, textBrush);
            }
        }

        private void DrawVolumeCell(int barIndex, float xLeft, float yTop, float width, float height, TextFormat fmt)
        {
            double vol = perBarVolume.ContainsKey(barIndex) ? perBarVolume[barIndex] : 0.0;

            // Transparent background – skip FillRectangle
            var rect = new RectangleF(xLeft, yTop, width, height);
            RenderTarget.DrawRectangle(rect, brushGeneral, 1f);

            string txt = vol.ToString("0");
            using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, txt, fmt, width, height))
            {
                var m = layout.Metrics;
                float tx = xLeft + (width  - m.Width)  / 2f;
                float ty = yTop  + (height - m.Height) / 2f;
                RenderTarget.DrawTextLayout(new Vector2(tx, ty), layout, brushVolumeWhite);
            }
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private a5[] cachea5;
		public a5 a5(int fontSizeProp, bool backgroundWhite)
		{
			return a5(Input, fontSizeProp, backgroundWhite);
		}

		public a5 a5(ISeries<double> input, int fontSizeProp, bool backgroundWhite)
		{
			if (cachea5 != null)
				for (int idx = 0; idx < cachea5.Length; idx++)
					if (cachea5[idx] != null && cachea5[idx].FontSizeProp == fontSizeProp && cachea5[idx].BackgroundWhite == backgroundWhite && cachea5[idx].EqualsInput(input))
						return cachea5[idx];
			return CacheIndicator<a5>(new a5(){ FontSizeProp = fontSizeProp, BackgroundWhite = backgroundWhite }, input, ref cachea5);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.a5 a5(int fontSizeProp, bool backgroundWhite)
		{
			return indicator.a5(Input, fontSizeProp, backgroundWhite);
		}

		public Indicators.a5 a5(ISeries<double> input , int fontSizeProp, bool backgroundWhite)
		{
			return indicator.a5(input, fontSizeProp, backgroundWhite);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.a5 a5(int fontSizeProp, bool backgroundWhite)
		{
			return indicator.a5(Input, fontSizeProp, backgroundWhite);
		}

		public Indicators.a5 a5(ISeries<double> input , int fontSizeProp, bool backgroundWhite)
		{
			return indicator.a5(input, fontSizeProp, backgroundWhite);
		}
	}
}

#endregion
