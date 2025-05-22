using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.DirectWrite;
using System.Windows.Media;                 // Brushes

namespace NinjaTrader.NinjaScript.Indicators
{
    // Delta indicator using tick rule and volume
    public class a8 : Indicator
    {
        // --- fields ---------------------------------------------------------
        private double lastTradePrice = 0.0;
        private int lastDirection = 0;

        private Dictionary<int, double> delta2;  // tick rule
        private Dictionary<int, double> volume;

        private SharpDX.Direct2D1.SolidColorBrush brushGeneral;
        private SharpDX.Direct2D1.SolidColorBrush brushVolumeWhite;
        private TextFormat textFormat;
        private bool lastBackgroundWhite;

        private float rectHeight = 30f;
        private float bottomMargin = 50f;

        // ----- absorption fields -------------------------------------------
        private class VolBucket { public double BidVol; public double AskVol; }
        private SortedDictionary<double, VolBucket> priceMap;
        private Queue<double> baselineBuffer;
        private double baseline;
        private double lastBid = double.NaN;
        private double lastAsk = double.NaN;
        private double lastPrice = double.NaN;
        private bool   pendingPotential;
        private OrderDir pendingDir;
        private double pendingPrice;
        private double tickSize;

        private enum AbsState { Potential, Confirmed }
        private enum OrderDir { Long, Short }

        // --- properties -----------------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Font Size", Order = 0, GroupName = "Parameters")]
        public int FontSizeProp { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Background White", Order = 1, GroupName = "Parameters")]
        public bool BackgroundWhite { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Absorption M", Order = 2, GroupName = "Absorption")]
        public int AbsorptionM { get; set; } = 24;

        [NinjaScriptProperty]
        [Display(Name = "Absorption K", Order = 3, GroupName = "Absorption")]
        public double AbsorptionK { get; set; } = 3.0;

        [NinjaScriptProperty]
        [Display(Name = "Usar Confirmacion Delta Footprint", Order = 4, GroupName = "Absorption")]
        public bool UsarConfirmacionDeltaFootprint { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Cluster Span Ticks", Order = 5, GroupName = "Absorption")]
        public int ClusterSpanTicks { get; set; } = 2;

        [NinjaScriptProperty]
        [Display(Name = "Color Potencial", Order = 6, GroupName = "Absorption")]
        public Brush ColorPotencial { get; set; } = Brushes.Gray;

        // --- state machine --------------------------------------------------
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "a8";
                Description             = "Delta (tick rule) + volume + Absorption";
                Calculate               = Calculate.OnEachTick;
                IsOverlay               = true;
                DisplayInDataBox        = false;
                DrawOnPricePanel        = false;
                DrawHorizontalGridLines = false;
                DrawVerticalGridLines   = false;
                PaintPriceMarkers       = false;

                FontSizeProp            = 12;
                BackgroundWhite         = false;
            }
            else if (State == State.Configure)
            {
                if (!Bars.IsTickReplay)
                    throw new Exception("a8 requiere Tick Replay activado.");

                if (ClusterSpanTicks < 1) ClusterSpanTicks = 1;
                if (AbsorptionM < 2) AbsorptionM = 2;
                if (AbsorptionK <= 1) AbsorptionK = 2;

                delta2        = new Dictionary<int, double>();
                volume        = new Dictionary<int, double>();
                priceMap      = new SortedDictionary<double, VolBucket>();
                baselineBuffer= new Queue<double>();
                baseline      = 0.0;
                tickSize      = Bars.Instrument.MasterInstrument.TickSize;
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

        private void DisposeGraphics()
        {
            brushGeneral?.Dispose();
            brushVolumeWhite?.Dispose();
            textFormat?.Dispose();
        }

        private void BuildBrushes()
        {
            brushGeneral?.Dispose();
            brushVolumeWhite?.Dispose();

            Color4 cText = BackgroundWhite ? new Color4(0, 0, 0, 1f)
                                           : new Color4(1, 1, 1, 1f);
            Color4 cVol  = new Color4(1, 1, 1, 1f);

            if (RenderTarget != null)
            {
                brushGeneral     = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, cText);
                brushVolumeWhite = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, cVol);
            }
        }

        // --- market data ----------------------------------------------------
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0)
                return;

            if (e.MarketDataType == MarketDataType.Bid)
            {
                lastBid = e.Price;
                return;
            }
            if (e.MarketDataType == MarketDataType.Ask)
            {
                lastAsk = e.Price;
                return;
            }

            if (e.MarketDataType != MarketDataType.Last)
                return;

            int barIdx   = CurrentBar;
            double price = e.Price;
            double vol   = e.Volume;

            if (!delta2.ContainsKey(barIdx))
            {
                delta2[barIdx] = volume[barIdx] = 0.0;
            }

            // Method 2: tick rule for delta
            double sign2;
            if (price > lastTradePrice)
                sign2 = 1;
            else if (price < lastTradePrice)
                sign2 = -1;
            else
                sign2 = lastDirection;
            delta2[barIdx] += vol * sign2;
            if (sign2 != 0)
                lastDirection = sign2 > 0 ? 1 : -1;
            lastTradePrice = price;

            volume[barIdx] += vol;

            // Determine aggressor using bid/ask when available, fallback tick rule
            bool isBuy;
            if (!double.IsNaN(lastAsk) && price >= lastAsk)
                isBuy = true;
            else if (!double.IsNaN(lastBid) && price <= lastBid)
                isBuy = false;
            else if (!double.IsNaN(lastPrice))
                isBuy = price > lastPrice;
            else
                isBuy = true;
            lastPrice = price;

            if (!priceMap.TryGetValue(price, out var bucket))
                bucket = new VolBucket();
            if (isBuy) bucket.AskVol += vol; else bucket.BidVol += vol;
            priceMap[price] = bucket;

            double lowBound  = Low[0] - 2 * tickSize;
            double highBound = High[0] + 2 * tickSize;
            foreach (var key in new List<double>(priceMap.Keys))
                if (key < lowBound || key > highBound)
                    priceMap.Remove(key);

            DetectAbsorption(price);
        }

        protected override void OnBarUpdate()
        {
            double fpVol = 0.0;
            foreach (var pv in priceMap.Values)
                fpVol += pv.BidVol + pv.AskVol;

            baselineBuffer.Enqueue(fpVol);
            while (baselineBuffer.Count > AbsorptionM)
                baselineBuffer.Dequeue();

            double sum = 0.0;
            foreach (var v in baselineBuffer) sum += v;
            if (baselineBuffer.Count > 0) baseline = sum / baselineBuffer.Count;

            priceMap.Clear();
            lastBid = lastAsk = lastPrice = double.NaN;
            pendingPotential = false;
        }

        // --- render ---------------------------------------------------------
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
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSizeProp);
            }

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;
            float barWidth = (float)chartControl.GetBarPaintWidth(ChartBars);

            float yBottom = (float)chartScale.GetYByValue(chartScale.MinValue);
            const int rowCount = 2; // delta + volume
            float yTop    = yBottom - bottomMargin - rectHeight * rowCount;

            double max2 = 0.0;
            for (int i = firstBar; i <= lastBar; i++)
            {
                if (delta2.ContainsKey(i)) max2 = Math.Max(max2, Math.Abs(delta2[i]));
            }
            if (max2.Equals(0.0)) max2 = 1.0;

            int deltaFontSize = FontSizeProp + 8;
            using (var deltaFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", deltaFontSize))
            {
                for (int i = firstBar; i <= lastBar; i++)
                {
                    float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                    float xLeft   = xCenter - barWidth / 2f;

                    DrawCell(delta2, i, max2, xLeft, yTop,             barWidth, rectHeight, deltaFormat, brushGeneral);
                    DrawVolumeCell(i, xLeft, yTop + rectHeight,     barWidth, rectHeight, deltaFormat);
                }
            }
        }

        private void DrawCell(Dictionary<int, double> src, int barIndex, double maxAbs, float xLeft, float yTop, float width, float height, TextFormat fmt, SharpDX.Direct2D1.Brush textBrush)
        {
            double value = src.ContainsKey(barIndex) ? src[barIndex] : 0.0;
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
            double vol = volume.ContainsKey(barIndex) ? volume[barIndex] : 0.0;
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

        private void DetectAbsorption(double price)
        {
            double clusterBid = 0.0, clusterAsk = 0.0;
            foreach (var kv in priceMap)
            {
                if (Math.Abs(kv.Key - price) <= ClusterSpanTicks * tickSize)
                {
                    clusterBid += kv.Value.BidVol;
                    clusterAsk += kv.Value.AskVol;
                }
            }

            if (clusterBid >= AbsorptionK * baseline && clusterAsk >= AbsorptionK * baseline)
            {
                double ratio = clusterAsk / clusterBid;
                if (ratio >= 0.8 && ratio <= 1.25)
                {
                    OrderDir dir = clusterAsk >= clusterBid ? OrderDir.Long : OrderDir.Short;
                    double curDelta = delta2.ContainsKey(CurrentBar) ? delta2[CurrentBar] : 0.0;
                    bool deltaOk = (dir == OrderDir.Long && curDelta > 0) || (dir == OrderDir.Short && curDelta < 0);
                    AbsState st = AbsState.Confirmed;

                    if (UsarConfirmacionDeltaFootprint && !deltaOk)
                    {
                        st = AbsState.Potential;
                        pendingPotential = true;
                        pendingDir = dir;
                        pendingPrice = price;
                    }

                    TriggerAbsorption(st, dir, price);
                }
            }

            if (pendingPotential)
            {
                double curDelta = delta2.ContainsKey(CurrentBar) ? delta2[CurrentBar] : 0.0;
                bool deltaOk = (pendingDir == OrderDir.Long && curDelta > 0) || (pendingDir == OrderDir.Short && curDelta < 0);
                if (deltaOk)
                {
                    TriggerAbsorption(AbsState.Confirmed, pendingDir, pendingPrice);
                    pendingPotential = false;
                }
            }
        }

        private void TriggerAbsorption(AbsState st, OrderDir dir, double price)
        {
            string tag = st == AbsState.Confirmed ? $"ABS_C_{CurrentBar}" : $"ABS_P_{CurrentBar}";
            Brush col = st == AbsState.Potential ? ColorPotencial : (dir == OrderDir.Long ? Brushes.Lime : Brushes.Red);
            RemoveDrawObject("ABS_P_" + CurrentBar);
            Draw.Dot(this, tag, false, 0, price, col);
            Alert(tag, Priority.Medium, $"Absorption {(dir == OrderDir.Long ? "LONG" : "SHORT")} @ {price}", "Alert1.wav", 10, Brushes.White, Brushes.Black);
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
            private a8[] cachea8;
            public a8 a8(int fontSizeProp, bool backgroundWhite)
            {
                    return a8(Input, fontSizeProp, backgroundWhite);
            }

            public a8 a8(ISeries<double> input, int fontSizeProp, bool backgroundWhite)
            {
                    if (cachea8 != null)
                            for (int idx = 0; idx < cachea8.Length; idx++)
                                    if (cachea8[idx] != null && cachea8[idx].FontSizeProp == fontSizeProp && cachea8[idx].BackgroundWhite == backgroundWhite && cachea8[idx].EqualsInput(input))
                                            return cachea8[idx];
                    return CacheIndicator<a8>(new a8(){ FontSizeProp = fontSizeProp, BackgroundWhite = backgroundWhite }, input, ref cachea8);
            }
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
            public Indicators.a8 a8(int fontSizeProp, bool backgroundWhite)
            {
                    return indicator.a8(Input, fontSizeProp, backgroundWhite);
            }

            public Indicators.a8 a8(ISeries<double> input , int fontSizeProp, bool backgroundWhite)
            {
                    return indicator.a8(input, fontSizeProp, backgroundWhite);
            }
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
            public Indicators.a8 a8(int fontSizeProp, bool backgroundWhite)
            {
                    return indicator.a8(Input, fontSizeProp, backgroundWhite);
            }

            public Indicators.a8 a8(ISeries<double> input , int fontSizeProp, bool backgroundWhite)
            {
                    return indicator.a8(input, fontSizeProp, backgroundWhite);
            }
	}
}

#endregion

