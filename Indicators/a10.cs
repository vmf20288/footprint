using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.DirectWrite;

namespace NinjaTrader.NinjaScript.Indicators
{
    // Combined weekly VWAP (±1σ) with tick-rule delta/volume display
    public class a10 : Indicator
    {
        // --- delta fields ---------------------------------------------------
        private double lastTradePrice = 0.0;
        private int lastDirection = 0;

        private Dictionary<int, double> delta2;
        private Dictionary<int, double> volume;

        private Series<double> deltaSeries;
        private Series<double> volumeSeries;

        // --- weekly VWAP fields ---------------------------------------------
        private double wSumPV, wSumV, wSumVarPV;

        // --- drawing --------------------------------------------------------
        private SharpDX.Direct2D1.SolidColorBrush brushGeneral;
        private SharpDX.Direct2D1.SolidColorBrush brushVolume;
        private TextFormat textFormat;

        private float rectHeight = 30f;
        private float bottomMargin = 50f;

        // --- properties -----------------------------------------------------
        [NinjaScriptProperty]
        [Display(Name = "Font Size", Order = 0, GroupName = "Parameters")]
        public int FontSizeProp { get; set; } = 12;

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
                Name = "a10";
                Description = "Weekly VWAP ±1σ with tick-rule delta";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = false;
                DrawHorizontalGridLines = false;
                DrawVerticalGridLines = false;
                PaintPriceMarkers = false;

                AddPlot(Brushes.Blue, "Weekly VWAP");   // Values[0]
                AddPlot(Brushes.Green, "+1σ");           // Values[1]
                AddPlot(Brushes.Green, "-1σ");           // Values[2]
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

        // --- bar update -----------------------------------------------------
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 0)
                return;

            deltaSeries[0] = delta2.ContainsKey(CurrentBar) ? delta2[CurrentBar] : double.NaN;
            volumeSeries[0] = volume.ContainsKey(CurrentBar) ? volume[CurrentBar] : double.NaN;

            // weekly VWAP computation (close of each bar)
            if (Bars.IsFirstBarOfSession && Time[0].DayOfWeek == DayOfWeek.Sunday)
            {
                wSumPV = wSumV = wSumVarPV = 0;
            }

            double ohlc4 = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;
            double vol = Volume[0];
            wSumPV    += ohlc4 * vol;
            wSumV     += vol;
            double wVWAP = wSumV == 0 ? ohlc4 : wSumPV / wSumV;
            wSumVarPV += vol * Math.Pow(ohlc4 - wVWAP, 2);
            double wSigma = wSumV == 0 ? 0 : Math.Sqrt(wSumVarPV / wSumV);

            Values[0][0] = wVWAP;
            Values[1][0] = wVWAP + wSigma;
            Values[2][0] = wVWAP - wSigma;
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

                    DrawCell(delta2, i, max2, xLeft, yTop, barWidth, rectHeight, deltaFormat, brushGeneral);
                    DrawVolumeCell(i, xLeft, yTop + rectHeight, barWidth, rectHeight, deltaFormat);
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
        private a10[] cachea10;
        public a10 a10(int fontSizeProp)
        {
            return a10(Input, fontSizeProp);
        }

        public a10 a10(ISeries<double> input, int fontSizeProp)
        {
            if (cachea10 != null)
                for (int idx = 0; idx < cachea10.Length; idx++)
                    if (cachea10[idx] != null && cachea10[idx].FontSizeProp == fontSizeProp && cachea10[idx].EqualsInput(input))
                        return cachea10[idx];
            return CacheIndicator<a10>(new a10(){ FontSizeProp = fontSizeProp }, input, ref cachea10);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.a10 a10(int fontSizeProp)
        {
            return indicator.a10(Input, fontSizeProp);
        }

        public Indicators.a10 a10(ISeries<double> input , int fontSizeProp)
        {
            return indicator.a10(input, fontSizeProp);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.a10 a10(int fontSizeProp)
        {
            return indicator.a10(Input, fontSizeProp);
        }

        public Indicators.a10 a10(ISeries<double> input , int fontSizeProp)
        {
            return indicator.a10(input, fontSizeProp);
        }
    }
}

#endregion

