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

namespace NinjaTrader.NinjaScript.Indicators
{
    // Delta indicator using tick rule and volume
    public class a6 : Indicator
    {
        // --- fields ---------------------------------------------------------
        private double lastTradePrice = 0.0;
        private int lastDirection = 0;

        private Dictionary<int, double> delta2;  // tick rule
        private Dictionary<int, double> volume;

        private SharpDX.Direct2D1.SolidColorBrush brushGeneral;
        private SharpDX.Direct2D1.SolidColorBrush brushVolume;
        private TextFormat textFormat;

        private Series<double> deltaSeries;
        private Series<double> volumeSeries;

        private float rectHeight = 30f;
        private float bottomMargin = 50f;

        // --- properties -----------------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Font Size", Order = 0, GroupName = "Parameters")]
        public int FontSizeProp { get; set; }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> DeltaSeries => deltaSeries;

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> VolumeSeries => volumeSeries;


        // --- state machine --------------------------------------------------
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "a6";
                Description             = "Delta (tick rule) and volume";
                Calculate               = Calculate.OnEachTick;
                IsOverlay               = true;
                DisplayInDataBox        = false;
                DrawOnPricePanel        = false;
                DrawHorizontalGridLines = false;
                DrawVerticalGridLines   = false;
                PaintPriceMarkers       = false;

                FontSizeProp            = 12;
            }
            else if (State == State.Configure)
            {
                delta2 = new Dictionary<int, double>();
                volume = new Dictionary<int, double>();
            }
            else if (State == State.DataLoaded)
            {
                BuildBrushes();
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSizeProp);
                deltaSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
                volumeSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
                deltaSeries[0] = double.NaN;
                volumeSeries[0] = double.NaN;
            }
            else if (State == State.Terminated)
            {
                DisposeGraphics();
            }
        }

        private void DisposeGraphics()
        {
            brushGeneral?.Dispose();
            brushVolume?.Dispose();
            textFormat?.Dispose();
        }

        private void BuildBrushes()
        {
            brushGeneral?.Dispose();
            brushVolume?.Dispose();

            Color4 cText = new Color4(0, 0, 0, 1f);
            Color4 cVol  = new Color4(0, 0, 0, 1f);

            if (RenderTarget != null)
            {
                brushGeneral = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, cText);
                brushVolume  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, cVol);
            }
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

            volume[barIdx] += vol;
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 0)
                return;

            deltaSeries[0] = delta2.ContainsKey(CurrentBar) ? delta2[CurrentBar] : double.NaN;
            volumeSeries[0] = volume.ContainsKey(CurrentBar) ? volume[CurrentBar] : double.NaN;
        }

        // --- render ---------------------------------------------------------
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (ChartBars == null || RenderTarget == null)
                return;


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
            RenderTarget.DrawTextLayout(new Vector2(tx, ty), layout, brushVolume);
            }
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
                private a6[] cachea6;
                public a6 a6(int fontSizeProp)
                {
                        return a6(Input, fontSizeProp);
                }

                public a6 a6(ISeries<double> input, int fontSizeProp)
                {
                        if (cachea6 != null)
                                for (int idx = 0; idx < cachea6.Length; idx++)
                                        if (cachea6[idx] != null && cachea6[idx].FontSizeProp == fontSizeProp && cachea6[idx].EqualsInput(input))
                                                return cachea6[idx];
                        return CacheIndicator<a6>(new a6(){ FontSizeProp = fontSizeProp }, input, ref cachea6);
                }
        }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
                public Indicators.a6 a6(int fontSizeProp)
                {
                        return indicator.a6(Input, fontSizeProp);
                }

                public Indicators.a6 a6(ISeries<double> input , int fontSizeProp)
                {
                        return indicator.a6(input, fontSizeProp);
                }
        }
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
                public Indicators.a6 a6(int fontSizeProp)
                {
                        return indicator.a6(Input, fontSizeProp);
                }

                public Indicators.a6 a6(ISeries<double> input , int fontSizeProp)
                {
                        return indicator.a6(input, fontSizeProp);
                }
        }
}

#endregion

