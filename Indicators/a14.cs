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
    // Banda test indicator
    public class a14 : Indicator
    {
        // === PARAMETERS ===
        [NinjaScriptProperty]
        [Display(Name = "ATRmultiplier", Order = 0, GroupName = "Parameters")]
        public double ATRmultiplier { get; set; } = 0.65;

        [NinjaScriptProperty]
        [Display(Name = "VWAPTouchTolerance", Order = 1, GroupName = "Parameters")]
        public int VWAPTouchTolerance { get; set; } = 3;

        [NinjaScriptProperty]
        [Display(Name = "Show Weekly VWAP", Order = 10, GroupName = "Weekly VWAP")]
        public bool ShowWeekly { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show Bands 1 (±1σ)", Order = 11, GroupName = "Weekly VWAP")]
        public bool ShowWeeklyBands1 { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show Bands 2 (±2σ)", Order = 12, GroupName = "Weekly VWAP")]
        public bool ShowWeeklyBands2 { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "Show Session VWAP", Order = 20, GroupName = "Session VWAP")]
        public bool ShowSession { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "Show Anchored VWAP", Order = 30, GroupName = "Anchored VWAP")]
        public bool ShowAnchored { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "Anchor Date", Order = 31, GroupName = "Anchored VWAP")]
        public DateTime AnchorDate { get; set; } = DateTime.Today;

        [NinjaScriptProperty]
        [Display(Name = "Anchor Time (HH:mm)", Order = 32, GroupName = "Anchored VWAP")]
        [RegularExpression("^([01]?[0-9]|2[0-3]):[0-5][0-9]$", ErrorMessage = "Formato HH:mm")]
        public string AnchorTime { get; set; } = "00:00";

        // === PRIVATE STATE ===
        private a1 vwap;
        private ATR atr;
        private Dictionary<int,bool> touchMap;
        private Dictionary<int,bool> preMap;
        private Dictionary<int,bool> finalMap;

        private SharpDX.Direct2D1.SolidColorBrush brushGeneral;
        private TextFormat textFormat;

        private const int ATRPeriod = 14;
        private const float rectHeight = 14f;
        private const float offsetPx = 5f;
        private const float labelWidth = 90f;
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name             = "a14";
                Description      = "Band test with ATR filter";
                Calculate        = Calculate.OnEachTick;
                IsOverlay        = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
            }
            else if (State == State.Configure)
            {
                touchMap = new Dictionary<int,bool>();
                preMap   = new Dictionary<int,bool>();
                finalMap = new Dictionary<int,bool>();
            }
            else if (State == State.DataLoaded)
            {
                vwap = a1(ShowWeekly, ShowWeeklyBands1, ShowWeeklyBands2, ShowSession, ShowAnchored, AnchorDate, AnchorTime);
                atr  = ATR(ATRPeriod);
                BuildBrushes();
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12);
            }
            else if (State == State.Terminated)
            {
                brushGeneral?.Dispose();
                textFormat?.Dispose();
            }
        }

        private void BuildBrushes()
        {
            brushGeneral?.Dispose();
            if (RenderTarget != null)
                brushGeneral = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0,0,0,1f));
        }
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1)
                return;

            if (IsFirstTickOfBar && CurrentBar > 0)
            {
                int prev = CurrentBar - 1;
                double rangePrev = High[1] - Low[1];
                double atrPrev   = atr[1];
                if (rangePrev >= ATRmultiplier * atrPrev)
                    finalMap[prev] = true;
            }

            if (!touchMap.ContainsKey(CurrentBar))
            {
                touchMap[CurrentBar] = false;
                preMap[CurrentBar]   = false;
                finalMap[CurrentBar] = false;
            }

            double range = High[0] - Low[0];
            double atrVal = atr[0];

            if (!touchMap[CurrentBar] && CheckTouch())
                touchMap[CurrentBar] = true;

            if (!preMap[CurrentBar] && range >= ATRmultiplier * atrVal)
                preMap[CurrentBar] = true;
        }

        private bool CheckTouch()
        {
            double tol = Instrument.MasterInstrument.TickSize * VWAPTouchTolerance;
            double h = High[0];
            double l = Low[0];
            bool t = false;

            if (ShowWeekly)
            {
                double c = vwap.Values[0][0];
                t |= h >= c - tol && l <= c + tol;
                if (ShowWeeklyBands1)
                {
                    double u1 = vwap.Values[1][0];
                    double d1 = vwap.Values[2][0];
                    t |= h >= u1 - tol && l <= u1 + tol;
                    t |= h >= d1 - tol && l <= d1 + tol;
                    if (ShowWeeklyBands2)
                    {
                        double u2 = vwap.Values[3][0];
                        double d2 = vwap.Values[4][0];
                        t |= h >= u2 - tol && l <= u2 + tol;
                        t |= h >= d2 - tol && l <= d2 + tol;
                    }
                }
            }
            if (ShowSession)
            {
                double s = vwap.Values[5][0];
                t |= h >= s - tol && l <= s + tol;
            }
            if (ShowAnchored)
            {
                double a = vwap.Values[6][0];
                t |= h >= a - tol && l <= a + tol;
            }
            return t;
        }
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (ChartBars == null || RenderTarget == null)
                return;

            BuildBrushes();

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;
            float barWidth = (float)chartControl.GetBarPaintWidth(ChartBars);

            float xFirst = chartControl.GetXByBarIndex(ChartBars, firstBar) - barWidth / 2f;
            float labelX = xFirst - labelWidth;

            using (var fmt = textFormat)
            {
                DrawRowLabels(fmt, labelX, chartScale, firstBar);

                for (int i = firstBar; i <= lastBar; i++)
                {
                    float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                    float xLeft   = xCenter - barWidth / 2f;

                    float yHigh = (float)chartScale.GetYByValue(High.GetValueAt(i));
                    float yTop  = yHigh - offsetPx - rectHeight * 3;

                    DrawCell(touchMap.ContainsKey(i) && touchMap[i],  xLeft, yTop,                 barWidth, rectHeight);
                    DrawCell(preMap.ContainsKey(i)   && preMap[i],    xLeft, yTop + rectHeight,     barWidth, rectHeight);
                    DrawCell(finalMap.ContainsKey(i) && finalMap[i],  xLeft, yTop + rectHeight * 2, barWidth, rectHeight);
                }
            }
        }

        private void DrawRowLabels(TextFormat fmt, float xLeft, ChartScale scale, int barIndex)
        {
            double high = High.GetValueAt(barIndex);
            float yHigh = (float)scale.GetYByValue(high);
            float yTop  = yHigh - offsetPx - rectHeight * 3;

            string[] labels = { "vwap toco", "preATRmultiplier", "ATRmultiplier" };
            for (int i = 0; i < labels.Length; i++)
            {
                using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, labels[i], fmt, labelWidth, rectHeight))
                {
                    var m = layout.Metrics;
                    float tx = xLeft + (labelWidth - m.Width) / 2f;
                    float ty = yTop + i * rectHeight + (rectHeight - m.Height) / 2f;
                    RenderTarget.DrawTextLayout(new Vector2(tx, ty), layout, brushGeneral);
                }
            }
        }

        private void DrawCell(bool state, float xLeft, float yTop, float width, float height)
        {
            var rect = new RectangleF(xLeft, yTop, width, height);
            if (state)
            {
                using (var fillBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(0.7f,0.7f,0.7f,0.5f)))
                    RenderTarget.FillRectangle(rect, fillBrush);
            }
            RenderTarget.DrawRectangle(rect, brushGeneral, 1f);
        }
    }
}
#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private a14[] cachea14;
        public a14 a14(double aTRmultiplier, int vWAPTouchTolerance, bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showAnchored, DateTime anchorDate, string anchorTime)
        {
            return a14(Input, aTRmultiplier, vWAPTouchTolerance, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showAnchored, anchorDate, anchorTime);
        }

        public a14 a14(ISeries<double> input, double aTRmultiplier, int vWAPTouchTolerance, bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showAnchored, DateTime anchorDate, string anchorTime)
        {
            if (cachea14 != null)
                for (int idx = 0; idx < cachea14.Length; idx++)
                    if (cachea14[idx] != null && cachea14[idx].ATRmultiplier == aTRmultiplier && cachea14[idx].VWAPTouchTolerance == vWAPTouchTolerance && cachea14[idx].ShowWeekly == showWeekly && cachea14[idx].ShowWeeklyBands1 == showWeeklyBands1 && cachea14[idx].ShowWeeklyBands2 == showWeeklyBands2 && cachea14[idx].ShowSession == showSession && cachea14[idx].ShowAnchored == showAnchored && cachea14[idx].AnchorDate == anchorDate && cachea14[idx].AnchorTime == anchorTime && cachea14[idx].EqualsInput(input))
                        return cachea14[idx];
            return CacheIndicator<a14>(new a14(){ ATRmultiplier = aTRmultiplier, VWAPTouchTolerance = vWAPTouchTolerance, ShowWeekly = showWeekly, ShowWeeklyBands1 = showWeeklyBands1, ShowWeeklyBands2 = showWeeklyBands2, ShowSession = showSession, ShowAnchored = showAnchored, AnchorDate = anchorDate, AnchorTime = anchorTime }, input, ref cachea14);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.a14 a14(double aTRmultiplier, int vWAPTouchTolerance, bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showAnchored, DateTime anchorDate, string anchorTime)
        {
            return indicator.a14(Input, aTRmultiplier, vWAPTouchTolerance, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showAnchored, anchorDate, anchorTime);
        }

        public Indicators.a14 a14(ISeries<double> input , double aTRmultiplier, int vWAPTouchTolerance, bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showAnchored, DateTime anchorDate, string anchorTime)
        {
            return indicator.a14(input, aTRmultiplier, vWAPTouchTolerance, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showAnchored, anchorDate, anchorTime);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.a14 a14(double aTRmultiplier, int vWAPTouchTolerance, bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showAnchored, DateTime anchorDate, string anchorTime)
        {
            return indicator.a14(Input, aTRmultiplier, vWAPTouchTolerance, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showAnchored, anchorDate, anchorTime);
        }

        public Indicators.a14 a14(ISeries<double> input , double aTRmultiplier, int vWAPTouchTolerance, bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showAnchored, DateTime anchorDate, string anchorTime)
        {
            return indicator.a14(input, aTRmultiplier, vWAPTouchTolerance, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showAnchored, anchorDate, anchorTime);
        }
    }
}

#endregion
