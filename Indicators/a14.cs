using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using SharpDX;
using SharpDX.DirectWrite;

namespace NinjaTrader.NinjaScript.Indicators
{
    // a14 – VWAP touch and ATR range monitor
    public class a14 : Indicator
    {
        private const int ATRPeriod = 14;
        private const float LabelWidth = 90f;

        private a1 vwap;
        private ATR atr;
        private Series<bool> touchedSeries;
        private Series<bool> preAtrSeries;
        private Series<bool> atrSeries;

        [NinjaScriptProperty]
        [Display(Name = "ATRmultiplier", Order = 0, GroupName = "Parameters")]
        public double ATRmultiplier { get; set; } = 0.65;

        [NinjaScriptProperty]
        [Display(Name = "vwaptouchtolerance", Order = 1, GroupName = "Parameters")]
        public int VWAPTouchTolerance { get; set; } = 3;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "a14";
                Calculate               = Calculate.OnEachTick;
                IsOverlay               = true;
                DisplayInDataBox        = false;
                DrawOnPricePanel        = false;
                PaintPriceMarkers       = false;
            }
            else if (State == State.Configure)
            {
                vwap = a1(true, true, false, false, false, DateTime.Today, "00:00");
                atr  = ATR(ATRPeriod);
            }
            else if (State == State.DataLoaded)
            {
                touchedSeries = new Series<bool>(this);
                preAtrSeries  = new Series<bool>(this);
                atrSeries     = new Series<bool>(this);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < ATRPeriod)
                return;

            if (IsFirstTickOfBar)
            {
                touchedSeries[0] = false;
                preAtrSeries[0] = false;
                atrSeries[0]    = false;
            }

            double tolerance = TickSize * VWAPTouchTolerance;
            bool hit = false;

            double val;
            val = vwap.Values[0][0]; // weekly VWAP
            if (!double.IsNaN(val) && High[0] >= val - tolerance && Low[0] <= val + tolerance)
                hit = true;
            val = vwap.Values[1][0];
            if (!double.IsNaN(val) && High[0] >= val - tolerance && Low[0] <= val + tolerance)
                hit = true;
            val = vwap.Values[2][0];
            if (!double.IsNaN(val) && High[0] >= val - tolerance && Low[0] <= val + tolerance)
                hit = true;
            val = vwap.Values[3][0];
            if (!double.IsNaN(val) && High[0] >= val - tolerance && Low[0] <= val + tolerance)
                hit = true;
            val = vwap.Values[4][0];
            if (!double.IsNaN(val) && High[0] >= val - tolerance && Low[0] <= val + tolerance)
                hit = true;
            val = vwap.Values[5][0];
            if (!double.IsNaN(val) && High[0] >= val - tolerance && Low[0] <= val + tolerance)
                hit = true;
            val = vwap.Values[6][0];
            if (!double.IsNaN(val) && High[0] >= val - tolerance && Low[0] <= val + tolerance)
                hit = true;

            if (hit)
                touchedSeries[0] = true;

            double barRange = High[0] - Low[0];
            double atrVal   = atr[0];

            if (barRange >= 0.7 * atrVal)
                preAtrSeries[0] = true;
            if (barRange >= ATRmultiplier * atrVal)
                atrSeries[0] = true;
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (ChartBars == null || RenderTarget == null)
                return;

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;
            float barWidth = (float)chartControl.GetBarPaintWidth(ChartBars);

            const float boxHeight = 12f;
            float offset = 5f;

            using (var fmt = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", 10))
            using (var textBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Color.Black))
            using (var fillBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color(0.8f, 0.8f, 0.8f, 1f)))
            using (var borderBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Color.Black))
            {
                for (int i = firstBar; i <= lastBar; i++)
                {
                    float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                    float xLeft   = xCenter - barWidth / 2f;
                    float xLabel  = xLeft - LabelWidth;
                    float baseY   = (float)chartScale.GetYByValue(Highs[BarsInProgress].GetValueAt(i));
                    float yTop    = baseY - offset - boxHeight * 3;

                    DrawBoxWithLabel(touchedSeries.GetValueAt(i), "vwap toco", xLeft, xLabel, yTop, barWidth, boxHeight, fmt, textBrush, fillBrush, borderBrush);
                    DrawBoxWithLabel(preAtrSeries.GetValueAt(i), "preATRmultiplier", xLeft, xLabel, yTop + boxHeight, barWidth, boxHeight, fmt, textBrush, fillBrush, borderBrush);
                    DrawBoxWithLabel(atrSeries.GetValueAt(i), "ATRmultiplier", xLeft, xLabel, yTop + 2 * boxHeight, barWidth, boxHeight, fmt, textBrush, fillBrush, borderBrush);
                }
            }
        }

        private void DrawBoxWithLabel(bool filled, string label, float xLeft, float xLabel, float yTop, float width, float height, TextFormat fmt,
            SharpDX.Direct2D1.SolidColorBrush textBrush, SharpDX.Direct2D1.SolidColorBrush fillBrush, SharpDX.Direct2D1.SolidColorBrush borderBrush)
        {
            var rect = new RectangleF(xLeft, yTop, width, height);
            RenderTarget.DrawRectangle(rect, borderBrush, 1f);
            if (filled)
                RenderTarget.FillRectangle(rect, fillBrush);

            using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, label, fmt, LabelWidth, height))
            {
                var m = layout.Metrics;
                float tx = xLabel + (LabelWidth - m.Width) / 2f;
                float ty = yTop + (height - m.Height) / 2f;
                RenderTarget.DrawTextLayout(new Vector2(tx, ty), layout, textBrush);
            }
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private a14[] cachea14;
        public a14 a14(double aTRmultiplier, int vWAPTouchTolerance)
        {
            return a14(Input, aTRmultiplier, vWAPTouchTolerance);
        }

        public a14 a14(ISeries<double> input, double aTRmultiplier, int vWAPTouchTolerance)
        {
            if (cachea14 != null)
                for (int idx = 0; idx < cachea14.Length; idx++)
                    if (cachea14[idx] != null && cachea14[idx].ATRmultiplier == aTRmultiplier && cachea14[idx].VWAPTouchTolerance == vWAPTouchTolerance && cachea14[idx].EqualsInput(input))
                        return cachea14[idx];
            return CacheIndicator<a14>(new a14(){ ATRmultiplier = aTRmultiplier, VWAPTouchTolerance = vWAPTouchTolerance }, input, ref cachea14);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.a14 a14(double aTRmultiplier, int vWAPTouchTolerance)
        {
            return indicator.a14(Input, aTRmultiplier, vWAPTouchTolerance);
        }

        public Indicators.a14 a14(ISeries<double> input , double aTRmultiplier, int vWAPTouchTolerance)
        {
            return indicator.a14(input, aTRmultiplier, vWAPTouchTolerance);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.a14 a14(double aTRmultiplier, int vWAPTouchTolerance)
        {
            return indicator.a14(Input, aTRmultiplier, vWAPTouchTolerance);
        }

        public Indicators.a14 a14(ISeries<double> input , double aTRmultiplier, int vWAPTouchTolerance)
        {
            return indicator.a14(input, aTRmultiplier, vWAPTouchTolerance);
        }
    }
}

#endregion
