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
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript.DrawingTools;

namespace NinjaTrader.NinjaScript.Indicators
{
    // Delta indicator using tick rule and volume
    // Delta indicator using tick rule and volume + Absorption detection
    public class a9 : Indicator
    {
        private enum AbsState { Potential, Confirmed }
        private enum OrderDir { Long, Short }
        // --- fields ---------------------------------------------------------
        private double lastTradePrice = 0.0;
        private int lastDirection = 0;

        private Dictionary<int, double> delta2;  // tick rule
        private Dictionary<int, double> volume;

        private double bestBid = 0.0;
        private double bestAsk = double.MaxValue;

        private class VolBucket
        {
            public double BidVol;
            public double AskVol;
        }

        private SortedDictionary<double, VolBucket> priceMap;
        private Queue<double> baselineBuffer;
        private double baseline = 1.0;

        private SharpDX.Direct2D1.SolidColorBrush brushGeneral;
        private SharpDX.Direct2D1.SolidColorBrush brushVolumeWhite;
        private TextFormat textFormat;
        private bool lastBackgroundWhite;

        private float rectHeight = 30f;
        private float bottomMargin = 50f;

        // --- properties -----------------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Font Size", Order = 0, GroupName = "Parameters")]
        public int FontSizeProp { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Background White", Order = 1, GroupName = "Parameters")]
        public bool BackgroundWhite { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Absorption M", Order = 2, GroupName = "Absorption")]
        [Range(2, int.MaxValue)]
        public int AbsorptionM { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Absorption K", Order = 3, GroupName = "Absorption")]
        [Range(1.1, double.MaxValue)]
        public double AbsorptionK { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Usar Confirmación Delta", Order = 4, GroupName = "Absorption")]
        public bool UsarConfirmacionDeltaFootprint { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cluster Span Ticks", Order = 5, GroupName = "Absorption")]
        [Range(1, int.MaxValue)]
        public int ClusterSpanTicks { get; set; }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Color Potencial", Order = 6, GroupName = "Absorption")]
        public Brush ColorPotencial { get; set; }

        [Browsable(false)]
        public string ColorPotencialSerializable
        {
            get { return Serialize.BrushToString(ColorPotencial); }
            set { ColorPotencial = Serialize.StringToBrush(value); }
        }

        // --- state machine --------------------------------------------------
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "a9";
                Description             = "Delta (tick rule) and volume with absorption";
                Calculate               = Calculate.OnEachTick;
                IsOverlay               = true;
                DisplayInDataBox        = false;
                DrawOnPricePanel        = false;
                DrawHorizontalGridLines = false;
                DrawVerticalGridLines   = false;
                PaintPriceMarkers       = false;

                FontSizeProp            = 12;
                BackgroundWhite         = false;
                AbsorptionM             = 24;
                AbsorptionK             = 3.0;
                UsarConfirmacionDeltaFootprint = true;
                ClusterSpanTicks        = 2;
                ColorPotencial          = Brushes.Gray;
            }
            else if (State == State.Configure)
            {
                if (!Bars.IsTickReplay)
                    throw new Exception("a9 requires Tick Replay enabled");

                delta2 = new Dictionary<int, double>();
                volume = new Dictionary<int, double>();
                priceMap = new SortedDictionary<double, VolBucket>();
                baselineBuffer = new Queue<double>();
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

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (e.Position != 0) return;
            if (e.MarketDataType == MarketDataType.Bid)
                bestBid = e.Price;
            else if (e.MarketDataType == MarketDataType.Ask)
                bestAsk = e.Price;
        }

        // --- market data ----------------------------------------------------
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0 || e.MarketDataType != MarketDataType.Last)
                return;

            int barIdx   = CurrentBar;
            double price = e.Price;
            double vol   = e.Volume;

            if (!delta2.ContainsKey(barIdx))
            {
                delta2[barIdx] = volume[barIdx] = 0.0;
            }

            // Clasificación de agresor: Bid/Ask o tick rule
            double sign2;
            if (price >= bestAsk)
                sign2 = 1;
            else if (price <= bestBid)
                sign2 = -1;
            else if (price > lastTradePrice)
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

            double rounded = Instrument.MasterInstrument.RoundToTickSize(price);
            if (rounded >= Low[0] - 2 * TickSize && rounded <= High[0] + 2 * TickSize)
            {
                if (!priceMap.TryGetValue(rounded, out var b))
                    priceMap[rounded] = b = new VolBucket();
                if (sign2 > 0) b.AskVol += vol; else if (sign2 < 0) b.BidVol += vol;
            }

            DetectAbsorption();
        }

        private void DetectAbsorption()
        {
            if (priceMap.Count == 0 || baseline <= 0)
                return;

            var prices = new List<double>(priceMap.Keys);
            prices.Sort();

            double clusterBid = 0.0;
            double clusterAsk = 0.0;
            List<double> cluster = new List<double>();
            double lastPrice = prices[0];
            void checkCluster()
            {
                if (cluster.Count == 0) return;
                bool heavyBid = clusterBid >= AbsorptionK * baseline;
                bool heavyAsk = clusterAsk >= AbsorptionK * baseline;
                double ratio = clusterAsk.ApproxCompare(0) == 0 ? 0 : clusterBid / clusterAsk;
                bool balanced = ratio >= 0.8 && ratio <= 1.25;
                if (heavyBid && heavyAsk && balanced)
                {
                    double mid = (cluster[0] + cluster[cluster.Count - 1]) / 2.0;
                    double delta = delta2.ContainsKey(CurrentBar) ? delta2[CurrentBar] : 0.0;
                    if (UsarConfirmacionDeltaFootprint)
                    {
                        if (delta > 0)
                            TriggerAbsorption(AbsState.Confirmed, OrderDir.Long, mid);
                        else if (delta < 0)
                            TriggerAbsorption(AbsState.Confirmed, OrderDir.Short, mid);
                        else
                            TriggerAbsorption(AbsState.Potential, OrderDir.Long, mid);
                    }
                    else
                    {
                        OrderDir dir = delta >= 0 ? OrderDir.Long : OrderDir.Short;
                        TriggerAbsorption(AbsState.Confirmed, dir, mid);
                    }
                }
            }

            cluster.Add(prices[0]);
            clusterBid = priceMap[prices[0]].BidVol;
            clusterAsk = priceMap[prices[0]].AskVol;
            for (int i = 1; i < prices.Count; i++)
            {
                double p = prices[i];
                if ((p - lastPrice) / TickSize <= ClusterSpanTicks)
                {
                    cluster.Add(p);
                    clusterBid += priceMap[p].BidVol;
                    clusterAsk += priceMap[p].AskVol;
                }
                else
                {
                    checkCluster();
                    cluster.Clear();
                    cluster.Add(p);
                    clusterBid = priceMap[p].BidVol;
                    clusterAsk = priceMap[p].AskVol;
                }
                lastPrice = p;
            }
            checkCluster();
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || !IsFirstTickOfBar || CurrentBar == 0)
                return;

            int prev = CurrentBar - 1;
            double volPrev = volume.ContainsKey(prev) ? volume[prev] : 0.0;
            baselineBuffer.Enqueue(volPrev);
            while (baselineBuffer.Count > AbsorptionM)
                baselineBuffer.Dequeue();
            baseline = 0.0;
            foreach (var v in baselineBuffer)
                baseline += v;
            baseline /= baselineBuffer.Count > 0 ? baselineBuffer.Count : 1;

            priceMap.Clear();
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

        private void TriggerAbsorption(AbsState st, OrderDir dir, double price)
        {
            string tag = st == AbsState.Confirmed ? "ABS_C_" + CurrentBar : "ABS_P_" + CurrentBar;
            Brush col = st == AbsState.Potential ? ColorPotencial
                        : dir == OrderDir.Long   ? Brushes.Lime
                                                 : Brushes.Red;
            RemoveDrawObject("ABS_P_" + CurrentBar);
            Draw.Dot(this, tag, false, 0, price, col);
            Alert(tag, Priority.Medium,
                  $"Absorption {(dir==OrderDir.Long?"LONG":"SHORT")} @ {price}",
                  "Alert1.wav", 10, Brushes.White, Brushes.Black);
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
                private a9[] cachea9;
                public a9 a9(int fontSizeProp, bool backgroundWhite)
                {
                        return a9(Input, fontSizeProp, backgroundWhite);
                }

                public a9 a9(ISeries<double> input, int fontSizeProp, bool backgroundWhite)
                {
                        if (cachea9 != null)
                                for (int idx = 0; idx < cachea9.Length; idx++)
                                        if (cachea9[idx] != null && cachea9[idx].FontSizeProp == fontSizeProp && cachea9[idx].BackgroundWhite == backgroundWhite && cachea9[idx].EqualsInput(input))
                                                return cachea9[idx];
                        return CacheIndicator<a9>(new a9(){ FontSizeProp = fontSizeProp, BackgroundWhite = backgroundWhite }, input, ref cachea9);
                }
        }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
                public Indicators.a9 a9(int fontSizeProp, bool backgroundWhite)
                {
                        return indicator.a9(Input, fontSizeProp, backgroundWhite);
                }

                public Indicators.a9 a9(ISeries<double> input , int fontSizeProp, bool backgroundWhite)
                {
                        return indicator.a9(input, fontSizeProp, backgroundWhite);
                }
        }
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
                public Indicators.a9 a9(int fontSizeProp, bool backgroundWhite)
                {
                        return indicator.a9(Input, fontSizeProp, backgroundWhite);
                }

                public Indicators.a9 a9(ISeries<double> input , int fontSizeProp, bool backgroundWhite)
                {
                        return indicator.a9(input, fontSizeProp, backgroundWhite);
                }
        }
}

#endregion

