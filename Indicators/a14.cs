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
    // a14 – VWAP-touch + ATR-range monitor (v2.2 mod)
    public class a14 : Indicator
    {
        // ─── CONST ─────────────────────────────────────────────────────
        private const int  ATRPeriod  = 14;
        private const float LabelWidth = 90f;

        // ─── PARAMETERS ─────────────────────────────────────────────────
        [NinjaScriptProperty]
        [Display(Name="ATRmultiplier", Order=0, GroupName="Parameters")]
        public double ATRmultiplier { get; set; } = 0.65;

        [NinjaScriptProperty]
        [Display(Name="VWAP touch tolerance (ticks)", Order=1, GroupName="Parameters")]
        public int VWAPTouchTolerance { get; set; } = 3;

        // — VWAP Session —
        [NinjaScriptProperty] [Display(Name="vwap central", Order=10, GroupName="VWAP – Sesión")]
        public bool ShowSessionCentral { get; set; } = true;
        [NinjaScriptProperty] [Display(Name="vwap Band 1", Order=11, GroupName="VWAP – Sesión")]
        public bool ShowSessionBand1   { get; set; } = true;
        [NinjaScriptProperty] [Display(Name="vwap Band 2", Order=12, GroupName="VWAP – Sesión")]
        public bool ShowSessionBand2   { get; set; } = true;

        // — VWAP Weekly —
        [NinjaScriptProperty] [Display(Name="Weekly vwap central", Order=20, GroupName="VWAP – Weekly")]
        public bool ShowWeeklyCentral  { get; set; } = true;
        [NinjaScriptProperty] [Display(Name="Weekly vwap banda 1", Order=21, GroupName="VWAP – Weekly")]
        public bool ShowWeeklyBand1    { get; set; } = true;
        [NinjaScriptProperty] [Display(Name="Weekly vwap banda 2", Order=22, GroupName="VWAP – Weekly")]
        public bool ShowWeeklyBand2    { get; set; } = true;

        // — VWAP Anchored —
        [NinjaScriptProperty] [Display(Name="Anchored vwap", Order=30, GroupName="VWAP – Anchored")]
        public bool ShowAnchored       { get; set; } = false;
        [NinjaScriptProperty] [Display(Name="fecha", Order=31, GroupName="VWAP – Anchored")]
        public DateTime AnchorDate     { get; set; } = DateTime.Today;
        [NinjaScriptProperty] [Display(Name="hora (HH:mm)", Order=32, GroupName="VWAP – Anchored")]
        [RegularExpression("^([01]?[0-9]|2[0-3]):[0-5][0-9]$", ErrorMessage="Formato HH:mm")]
        public string AnchorTime       { get; set; } = "00:00";

        // ─── PRIVATE FIELDS ─────────────────────────────────────────────
        private a1 vwap;
        private ATR atr;
        private Series<bool> touchedSeries;
        private Series<bool> atrSeries;
        private Series<int>  plotHit;         // índice plot tocado, -1 = ninguno

        private readonly string[] plotName =
        {
            "Weekly", "+1σ W", "-1σ W", "+2σ W", "-2σ W",
            "Session", "Anchored",
            "+1σ S", "-1σ S", "+2σ S", "-2σ S"
        };

        // ─── OnStateChange ─────────────────────────────────────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name              = "a14";
                Calculate         = Calculate.OnEachTick;   // intrabar
                IsOverlay         = true;
                DisplayInDataBox  = false;
                DrawOnPricePanel  = false;
                PaintPriceMarkers = false;
            }
            else if (State == State.Configure)
            {
                vwap = a1(
                    ShowWeeklyCentral, ShowWeeklyBand1, ShowWeeklyBand2,
                    ShowSessionCentral, ShowSessionBand1, ShowSessionBand2,
                    ShowAnchored, AnchorDate, AnchorTime);

                atr = ATR(ATRPeriod);
            }
            else if (State == State.DataLoaded)
            {
                touchedSeries = new Series<bool>(this);
                atrSeries     = new Series<bool>(this);
                plotHit       = new Series<int>(this);
            }
        }

        // ─── OnBarUpdate ───────────────────────────────────────────────
        protected override void OnBarUpdate()
        {
            if (CurrentBar < ATRPeriod) return;

            if (IsFirstTickOfBar)
            {
                touchedSeries[0] = false;
                atrSeries[0]     = false;
                plotHit[0]       = -1;
            }

            // — Detección de toque intrabar —
            double tolerance = TickSize * VWAPTouchTolerance;
            bool   hit    = false;
            int    hitIdx = -1;

            (int idx, bool enabled)[] plots =
            {
                (0, ShowWeeklyCentral),
                (1, ShowWeeklyBand1), (2, ShowWeeklyBand1),
                (3, ShowWeeklyBand2), (4, ShowWeeklyBand2),
                (5, ShowSessionCentral),
                (7, ShowSessionBand1), (8, ShowSessionBand1),
                (9, ShowSessionBand2), (10, ShowSessionBand2),
                (6, ShowAnchored)
            };

            foreach (var p in plots)
            {
                if (!p.enabled) continue;

                double val = vwap.Values[p.idx][0];
                if (!double.IsNaN(val)
                    && High[0] >= val - tolerance
                    && Low[0]  <= val + tolerance)
                {
                    hit    = true;
                    hitIdx = p.idx;
                    break;
                }
            }

            touchedSeries[0] = hit;
            plotHit[0]       = hitIdx;

            // — Rangos ATR —
            double barRange = High[0] - Low[0];
            double atrVal   = atr[0];
            if (barRange >= ATRmultiplier * atrVal) atrSeries[0] = true;
        }

        // ─── OnRender ─────────────────────────────────────────────────
        protected override void OnRender(ChartControl cc, ChartScale cs)
        {
            base.OnRender(cc, cs);
            if (ChartBars == null || RenderTarget == null) return;

            int first = ChartBars.FromIndex, last = ChartBars.ToIndex;
            float bw  = (float)cc.GetBarPaintWidth(ChartBars);
            const float h = 18f;  float off = 5f;

            using (var fmt = new TextFormat(Core.Globals.DirectWriteFactory,"Arial",9))
            using (var txt = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Color.Black))
            using (var fill= new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,new Color(0.8f,0.8f,0.8f,1f)))
            using (var brd = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, Color.Black))
            {
                for (int i = first; i <= last; i++)
                {
                    float xC   = cc.GetXByBarIndex(ChartBars,i);
                    float xL   = xC - bw/2f;
                    float xLbl = xL - LabelWidth;
                    float y0   = (float)cs.GetYByValue(Highs[BarsInProgress].GetValueAt(i)) - off - h*2;

                    string extra = plotHit.GetValueAt(i) >= 0 ? "\n" + plotName[plotHit.GetValueAt(i)] : "";

                    DrawBox("vwap touch" + extra, touchedSeries.GetValueAt(i), xL, xLbl, y0, bw, h, fmt, txt, fill, brd);
                    DrawBox("ATRmultiplier", touchedSeries.GetValueAt(i) && atrSeries.GetValueAt(i), xL, xLbl, y0 + h, bw, h, fmt, txt, fill, brd);
                }
            }
        }

        private void DrawBox(string label, bool filled, float xL, float xLbl, float y, float w, float h,
                             TextFormat fmt, SharpDX.Direct2D1.SolidColorBrush txtB,
                             SharpDX.Direct2D1.SolidColorBrush fillB,
                             SharpDX.Direct2D1.SolidColorBrush brdB)
        {
            var r = new RectangleF(xL, y, w, h);
            RenderTarget.DrawRectangle(r, brdB, 1f);
            if (filled) RenderTarget.FillRectangle(r, fillB);

            using (var tl = new TextLayout(Core.Globals.DirectWriteFactory, label, fmt, LabelWidth, h*2))
            {
                var m = tl.Metrics;
                float tx = xLbl + (LabelWidth - m.Width) / 2f;
                float ty = y    + (h - m.Height) / 2f;
                RenderTarget.DrawTextLayout(new Vector2(tx, ty), tl, txtB);
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
        public a14 a14(double aTRmultiplier, int vWAPTouchTolerance, bool showSessionCentral, bool showSessionBand1, bool showSessionBand2, bool showWeeklyCentral, bool showWeeklyBand1, bool showWeeklyBand2, bool showAnchored, DateTime anchorDate, string anchorTime)
        {
            return a14(Input, aTRmultiplier, vWAPTouchTolerance, showSessionCentral, showSessionBand1, showSessionBand2, showWeeklyCentral, showWeeklyBand1, showWeeklyBand2, showAnchored, anchorDate, anchorTime);
        }

        public a14 a14(ISeries<double> input, double aTRmultiplier, int vWAPTouchTolerance, bool showSessionCentral, bool showSessionBand1, bool showSessionBand2, bool showWeeklyCentral, bool showWeeklyBand1, bool showWeeklyBand2, bool showAnchored, DateTime anchorDate, string anchorTime)
        {
            if (cachea14 != null)
                for (int idx = 0; idx < cachea14.Length; idx++)
                    if (cachea14[idx] != null && cachea14[idx].ATRmultiplier == aTRmultiplier && cachea14[idx].VWAPTouchTolerance == vWAPTouchTolerance && cachea14[idx].ShowSessionCentral == showSessionCentral && cachea14[idx].ShowSessionBand1 == showSessionBand1 && cachea14[idx].ShowSessionBand2 == showSessionBand2 && cachea14[idx].ShowWeeklyCentral == showWeeklyCentral && cachea14[idx].ShowWeeklyBand1 == showWeeklyBand1 && cachea14[idx].ShowWeeklyBand2 == showWeeklyBand2 && cachea14[idx].ShowAnchored == showAnchored && cachea14[idx].AnchorDate == anchorDate && cachea14[idx].AnchorTime == anchorTime && cachea14[idx].EqualsInput(input))
                        return cachea14[idx];
            return CacheIndicator<a14>(new a14(){ ATRmultiplier = aTRmultiplier, VWAPTouchTolerance = vWAPTouchTolerance, ShowSessionCentral = showSessionCentral, ShowSessionBand1 = showSessionBand1, ShowSessionBand2 = showSessionBand2, ShowWeeklyCentral = showWeeklyCentral, ShowWeeklyBand1 = showWeeklyBand1, ShowWeeklyBand2 = showWeeklyBand2, ShowAnchored = showAnchored, AnchorDate = anchorDate, AnchorTime = anchorTime }, input, ref cachea14);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.a14 a14(double aTRmultiplier, int vWAPTouchTolerance, bool showSessionCentral, bool showSessionBand1, bool showSessionBand2, bool showWeeklyCentral, bool showWeeklyBand1, bool showWeeklyBand2, bool showAnchored, DateTime anchorDate, string anchorTime)
        {
            return indicator.a14(Input, aTRmultiplier, vWAPTouchTolerance, showSessionCentral, showSessionBand1, showSessionBand2, showWeeklyCentral, showWeeklyBand1, showWeeklyBand2, showAnchored, anchorDate, anchorTime);
        }

        public Indicators.a14 a14(ISeries<double> input , double aTRmultiplier, int vWAPTouchTolerance, bool showSessionCentral, bool showSessionBand1, bool showSessionBand2, bool showWeeklyCentral, bool showWeeklyBand1, bool showWeeklyBand2, bool showAnchored, DateTime anchorDate, string anchorTime)
        {
            return indicator.a14(input, aTRmultiplier, vWAPTouchTolerance, showSessionCentral, showSessionBand1, showSessionBand2, showWeeklyCentral, showWeeklyBand1, showWeeklyBand2, showAnchored, anchorDate, anchorTime);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.a14 a14(double aTRmultiplier, int vWAPTouchTolerance, bool showSessionCentral, bool showSessionBand1, bool showSessionBand2, bool showWeeklyCentral, bool showWeeklyBand1, bool showWeeklyBand2, bool showAnchored, DateTime anchorDate, string anchorTime)
        {
            return indicator.a14(Input, aTRmultiplier, vWAPTouchTolerance, showSessionCentral, showSessionBand1, showSessionBand2, showWeeklyCentral, showWeeklyBand1, showWeeklyBand2, showAnchored, anchorDate, anchorTime);
        }

        public Indicators.a14 a14(ISeries<double> input , double aTRmultiplier, int vWAPTouchTolerance, bool showSessionCentral, bool showSessionBand1, bool showSessionBand2, bool showWeeklyCentral, bool showWeeklyBand1, bool showWeeklyBand2, bool showAnchored, DateTime anchorDate, string anchorTime)
        {
            return indicator.a14(input, aTRmultiplier, vWAPTouchTolerance, showSessionCentral, showSessionBand1, showSessionBand2, showWeeklyCentral, showWeeklyBand1, showWeeklyBand2, showAnchored, anchorDate, anchorTime);
        }
    }
}

#endregion
