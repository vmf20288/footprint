using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.DirectWrite;
using System.Linq;
using System.Windows.Media;

namespace NinjaTrader.NinjaScript.Indicators
{
    // Simple delta comparison indicator with three calculation methods
    public class a6 : Indicator
    {
        // --- fields ---------------------------------------------------------
        private double bestBid = 0.0;
        private double bestAsk = double.MaxValue;
        private double lastTradePrice = 0.0;
        private int lastDirection = 0;

        private Dictionary<int, double> delta1;  // bid/ask classification
        private Dictionary<int, double> delta2;  // tick rule
        private Dictionary<int, double> delta3;  // midpoint rule
        private Dictionary<int, double> volume;

        private SharpDX.Direct2D1.SolidColorBrush brushGeneral;
        private SharpDX.Direct2D1.SolidColorBrush brushVolumeWhite;
        private TextFormat textFormat;
        private bool lastBackgroundWhite;

        private float rectHeight = 30f;
        private float bottomMargin = 50f;

        // absorption fields
        private struct VolBucket { public double BidVol; public double AskVol; }
        private Dictionary<double, VolBucket> priceMap;
        private Queue<double> baselineBuffer;
        private double baseline;
        private int    lastFootprintBar;
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
        [Display(Name = "Absorption M promedio últimas barras", Order = 2, GroupName = "Absorption")]
        [Range(2, int.MaxValue)]
        public int AbsM { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Absorption K umbral Vol veces más M", Order = 3, GroupName = "Absorption")]
        [Range(1.0, double.MaxValue)]
        public double AbsK { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "UsarConfirmaciónDeltaFootprint", Order = 4, GroupName = "Absorption")]
        public bool UsarConfirmacionDeltaFootprint { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ClusterSpanTicks", Order = 5, GroupName = "Absorption")]
        [Range(1, int.MaxValue)]
        public int ClusterSpanTicks { get; set; }

        [XmlIgnore]
        [Display(Name = "ColorPotencial", Order = 6, GroupName = "Absorption")]
        public Brush ColorPotencial { get; set; }

        [Browsable(false)]
        public string ColorPotencialSerializable { get { return Serialize.BrushToString(ColorPotencial); } set { ColorPotencial = Serialize.StringToBrush(value); } }

        // --- state machine --------------------------------------------------
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "a6";
                Description             = "Delta comparison – three methods with absorption";
                Calculate               = Calculate.OnEachTick;
                IsOverlay               = true;
                DisplayInDataBox        = false;
                DrawOnPricePanel        = false;
                DrawHorizontalGridLines = false;
                DrawVerticalGridLines   = false;
                PaintPriceMarkers       = false;

                FontSizeProp            = 12;
                BackgroundWhite         = false;

                AbsM                    = 24;
                AbsK                    = 3.0;
                UsarConfirmacionDeltaFootprint = true;
                ClusterSpanTicks        = 2;
                ColorPotencial          = Brushes.Gray;
            }
            else if (State == State.Configure)
            {
                delta1 = new Dictionary<int, double>();
                delta2 = new Dictionary<int, double>();
                delta3 = new Dictionary<int, double>();
                volume = new Dictionary<int, double>();
                priceMap       = new Dictionary<double, VolBucket>();
                baselineBuffer = new Queue<double>();
                baseline       = 0.0;
                tickSize       = TickSize;
                lastFootprintBar = CurrentBar;

                if (!Bars.IsTickReplay)
                    throw new Exception("El indicador 'a6' requiere cargar el gráfico con Tick Replay.");
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

            // new bar? finalize previous footprint
            if (CurrentBar != lastFootprintBar)
            {
                FinalizeFootprint();
                lastFootprintBar = CurrentBar;
            }

            int barIdx   = CurrentBar;
            double price = e.Price;
            double vol   = e.Volume;

            if (!delta1.ContainsKey(barIdx))
            {
                delta1[barIdx] = delta2[barIdx] = delta3[barIdx] = volume[barIdx] = 0.0;
            }

            int dirSign = 0;
            if (price >= bestAsk && bestAsk < double.MaxValue)
                dirSign = 1;
            else if (price <= bestBid && bestBid > 0)
                dirSign = -1;
            else
            {
                if (price > lastTradePrice)
                    dirSign = 1;
                else if (price < lastTradePrice)
                    dirSign = -1;
                else
                    dirSign = lastDirection;
            }

            // Method 1: classification using current best bid/ask
            if (dirSign == 1)
                delta1[barIdx] += vol;
            else if (dirSign == -1)
                delta1[barIdx] -= vol;

            // Method 2: tick rule
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

            // Method 3: midpoint between bid/ask
            double mid = (bestBid + bestAsk) / 2.0;
            delta3[barIdx] += vol * (price >= mid ? 1 : -1);

            volume[barIdx] += vol;

            // update footprint buckets
            if (!priceMap.TryGetValue(price, out VolBucket bucket))
                bucket = new VolBucket();
            if (dirSign == 1)
                bucket.AskVol += vol;
            else if (dirSign == -1)
                bucket.BidVol += vol;
            priceMap[price] = bucket;

            DetectAbsorption(price);
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
            const int rowCount = 4; // three deltas + volume
            float yTop    = yBottom - bottomMargin - rectHeight * rowCount;

            double max1 = 0.0, max2 = 0.0, max3 = 0.0;
            for (int i = firstBar; i <= lastBar; i++)
            {
                if (delta1.ContainsKey(i)) max1 = Math.Max(max1, Math.Abs(delta1[i]));
                if (delta2.ContainsKey(i)) max2 = Math.Max(max2, Math.Abs(delta2[i]));
                if (delta3.ContainsKey(i)) max3 = Math.Max(max3, Math.Abs(delta3[i]));
            }
            if (max1.Equals(0.0)) max1 = 1.0;
            if (max2.Equals(0.0)) max2 = 1.0;
            if (max3.Equals(0.0)) max3 = 1.0;

            int deltaFontSize = FontSizeProp + 8;
            using (var deltaFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", deltaFontSize))
            {
                for (int i = firstBar; i <= lastBar; i++)
                {
                    float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                    float xLeft   = xCenter - barWidth / 2f;

                    DrawCell(delta1, i, max1, xLeft, yTop,                 barWidth, rectHeight, deltaFormat, brushGeneral);
                    DrawCell(delta2, i, max2, xLeft, yTop + rectHeight,    barWidth, rectHeight, deltaFormat, brushGeneral);
                    DrawCell(delta3, i, max3, xLeft, yTop + rectHeight * 2, barWidth, rectHeight, deltaFormat, brushGeneral);
                    DrawVolumeCell(i, xLeft, yTop + rectHeight * 3,         barWidth, rectHeight, deltaFormat);
                }
            }
        }

        private void FinalizeFootprint()
        {
            double fpVol = 0.0;
            foreach (var kv in priceMap)
                fpVol += kv.Value.AskVol + kv.Value.BidVol;

            baselineBuffer.Enqueue(fpVol);
            while (baselineBuffer.Count > AbsM)
                baselineBuffer.Dequeue();
            baseline = baselineBuffer.Count > 0 ? baselineBuffer.Average() : 0.0;

            priceMap.Clear();
        }

        private void DetectAbsorption(double price)
        {
            if (baseline <= 0)
                return;

            double clusterBid = 0.0, clusterAsk = 0.0;
            double lowP = price, highP = price;
            for (int i = -ClusterSpanTicks; i <= ClusterSpanTicks; i++)
            {
                double p = price + i * tickSize;
                if (priceMap.TryGetValue(p, out VolBucket b))
                {
                    clusterBid += b.BidVol;
                    clusterAsk += b.AskVol;
                    if (p < lowP) lowP = p;
                    if (p > highP) highP = p;
                }
            }

            bool heavyBid = clusterBid >= AbsK * baseline;
            bool heavyAsk = clusterAsk >= AbsK * baseline;
            if (!heavyBid || !heavyAsk)
                return;
            if (clusterBid == 0 || clusterAsk == 0)
                return;

            double ratio = clusterBid / clusterAsk;
            if (ratio < 0.8 || ratio > 1.25)
                return;

            double fpDelta = 0.0;
            foreach (var kv in priceMap.Values)
                fpDelta += kv.AskVol - kv.BidVol;
            OrderDir dir = fpDelta >= 0 ? OrderDir.Long : OrderDir.Short;

            bool deltaOk = !UsarConfirmacionDeltaFootprint || (dir == OrderDir.Long ? fpDelta > 0 : fpDelta < 0);
            AbsState st = deltaOk ? AbsState.Confirmed : AbsState.Potential;

            double mid = (lowP + highP) / 2.0;
            TriggerAbsorption(st, dir, mid);
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
            string tag = (st == AbsState.Confirmed ? "ABS_C_" : "ABS_P_") + CurrentBar;
            Brush col = st == AbsState.Potential ? ColorPotencial
                      : dir == OrderDir.Long    ? Brushes.Lime
                      : Brushes.Red;
            RemoveDrawObject("ABS_P_" + CurrentBar);
            Draw.Dot(this, tag, false, 0, price, col);
            Alert(tag, Priority.Medium,
                  $"Absorption {(dir == OrderDir.Long ? "LONG" : "SHORT")} @ {price}",
                  "Alert1.wav", 10, Brushes.White, Brushes.Black);
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private a6[] cachea6;
        public a6 a6(int fontSizeProp, bool backgroundWhite)
        {
            return a6(Input, fontSizeProp, backgroundWhite);
        }

        public a6 a6(ISeries<double> input, int fontSizeProp, bool backgroundWhite)
        {
            if (cachea6 != null)
                for (int idx = 0; idx < cachea6.Length; idx++)
                    if (cachea6[idx] != null && cachea6[idx].FontSizeProp == fontSizeProp && cachea6[idx].BackgroundWhite == backgroundWhite && cachea6[idx].EqualsInput(input))
                        return cachea6[idx];
            return CacheIndicator<a6>(new a6(){ FontSizeProp = fontSizeProp, BackgroundWhite = backgroundWhite }, input, ref cachea6);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.a6 a6(int fontSizeProp, bool backgroundWhite)
        {
            return indicator.a6(Input, fontSizeProp, backgroundWhite);
        }

        public Indicators.a6 a6(ISeries<double> input , int fontSizeProp, bool backgroundWhite)
        {
            return indicator.a6(input, fontSizeProp, backgroundWhite);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.a6 a6(int fontSizeProp, bool backgroundWhite)
        {
            return indicator.a6(Input, fontSizeProp, backgroundWhite);
        }

        public Indicators.a6 a6(ISeries<double> input , int fontSizeProp, bool backgroundWhite)
        {
            return indicator.a6(input, fontSizeProp, backgroundWhite);
        }
    }
}

#endregion
