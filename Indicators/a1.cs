// -----------------------------------------------------------------------------
//  a1.cs  –  Triple VWAP Indicator (Weekly + Session + Anchored) for NinjaTrader 8
//  v1.6.2 • Cambio principal: cálculo solo al cierre de vela (Calculate.OnBarClose)
// -----------------------------------------------------------------------------
//  • Weekly VWAP (azul + bandas verdes)
//  • Session VWAP (azul claro, sin bandas)
//  • Anchored VWAP (mostaza, sin bandas)
// -----------------------------------------------------------------------------

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;                 // Brushes
using NinjaTrader.Data;
using NinjaTrader.Gui.NinjaScript;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a1 : Indicator
    {
        // ───────── PARAMETERS ─────────
        // Weekly VWAP
        [NinjaScriptProperty]
        [Display(Name = "Show Weekly VWAP", Order = 0, GroupName = "Weekly VWAP")]
        public bool ShowWeekly { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show Bands 1 (±1σ)", Order = 1, GroupName = "Weekly VWAP")]
        public bool ShowWeeklyBands1 { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name = "Show Bands 2 (±2σ)", Order = 2, GroupName = "Weekly VWAP")]
        public bool ShowWeeklyBands2 { get; set; } = false;

        // Session VWAP
        [NinjaScriptProperty]
        [Display(Name = "Show Session VWAP", Order = 0, GroupName = "Session VWAP")]
        public bool ShowSession { get; set; } = false;

        // Anchored VWAP
        [NinjaScriptProperty]
        [Display(Name = "Show Anchored VWAP", Order = 0, GroupName = "Anchored VWAP")]
        public bool ShowAnchored { get; set; } = false;

        [NinjaScriptProperty]
        [Display(Name = "Anchor Date", Order = 1, GroupName = "Anchored VWAP")]
        public DateTime AnchorDate { get; set; } = DateTime.Today;

        [NinjaScriptProperty]
        [Display(Name = "Anchor Time (HH:mm)", Order = 2, GroupName = "Anchored VWAP")]
        [RegularExpression("^([01]?[0-9]|2[0-3]):[0-5][0-9]$", ErrorMessage = "Formato HH:mm")]
        public string AnchorTime { get; set; } = "00:00";

        // ───────── PRIVATE STATE ─────────
        // Weekly accumulators
        private double wSumPV, wSumV, wSumVarPV;
        // Session accumulators
        private double sSumPV, sSumV;
        // Anchored accumulators
        private double aSumPV, aSumV;
        private bool   anchorActive;

        private DateTime AnchorDateTime => DateTime.TryParse(AnchorTime, out var t)
            ? AnchorDate.Date.Add(t.TimeOfDay)
            : AnchorDate.Date; // fallback 00:00

        // ───────── OnStateChange ─────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name             = "a1";            // nombre en la lista de indicadores
                IsOverlay        = true;

                // --- Cambio clave: calcular solo al cerrar cada vela ---
                Calculate        = Calculate.OnBarClose;

                DrawOnPricePanel = true;

                // Plots: 0-4 Weekly, 5 Session, 6 Anchored
                AddPlot(Brushes.Blue,       "Weekly VWAP");    // Values[0]
                AddPlot(Brushes.Green,      "+1σ");           // Values[1]
                AddPlot(Brushes.Green,      "-1σ");           // Values[2]
                AddPlot(Brushes.Green,      "+2σ");           // Values[3]
                AddPlot(Brushes.Green,      "-2σ");           // Values[4]

                AddPlot(Brushes.DeepSkyBlue, "Session VWAP");  // Values[5]
                AddPlot(Brushes.Goldenrod,  "Anchored VWAP");  // Values[6]
            }
        }

        // ───────── OnBarUpdate ─────────
        protected override void OnBarUpdate()
        {
            // ─── Reinicios ───
            if (Bars.IsFirstBarOfSession)
            {
                // reset diario (Session)
                sSumPV = sSumV = 0;
                if (Time[0].DayOfWeek == DayOfWeek.Sunday)          // ajusta si prefieres Monday
                {
                    // reset semanal (Weekly)
                    wSumPV = wSumV = wSumVarPV = 0;
                }
            }

            // ─── Cálculo OHLC4 y acumulaciones ───
            double ohlc4 = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;
            double vol   = Volume[0];

            // Weekly
            wSumPV    += ohlc4 * vol;
            wSumV     += vol;
            double wVWAP = wSumV == 0 ? ohlc4 : wSumPV / wSumV;
            wSumVarPV += vol * Math.Pow(ohlc4 - wVWAP, 2);
            double wSigma = wSumV == 0 ? 0 : Math.Sqrt(wSumVarPV / wSumV);

            // Session
            sSumPV += ohlc4 * vol;
            sSumV  += vol;
            double sVWAP = sSumV == 0 ? ohlc4 : sSumPV / sSumV;

            // Anchored
            DateTime anchorDT = AnchorDateTime;
            if (ShowAnchored)
            {
                if (!anchorActive && Time[0] >= anchorDT)
                {
                    aSumPV = aSumV = 0;
                    anchorActive = true;
                }

                if (anchorActive)
                {
                    aSumPV += ohlc4 * vol;
                    aSumV  += vol;
                }
            }

            double aVWAP = aSumV == 0 ? ohlc4 : aSumPV / aSumV;

            // ─── Ploteo Weekly ───
            if (ShowWeekly)
            {
                Values[0][0] = wVWAP;

                if (ShowWeeklyBands1)
                {
                    Values[1][0] = wVWAP + wSigma;
                    Values[2][0] = wVWAP - wSigma;

                    if (ShowWeeklyBands2)
                    {
                        Values[3][0] = wVWAP + 2 * wSigma;
                        Values[4][0] = wVWAP - 2 * wSigma;
                    }
                    else
                    {
                        Values[3][0] = double.NaN;
                        Values[4][0] = double.NaN;
                    }
                }
                else
                {
                    for (int i = 1; i <= 4; i++)
                        Values[i][0] = double.NaN;
                }
            }
            else
            {
                for (int i = 0; i <= 4; i++)
                    Values[i][0] = double.NaN;
            }

            // ─── Ploteo Session ───
            Values[5][0] = ShowSession ? sVWAP : double.NaN;

            // ─── Ploteo Anchored ───
            Values[6][0] = (ShowAnchored && anchorActive) ? aVWAP : double.NaN;
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private a1[] cachea1;
		public a1 a1(bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showAnchored, DateTime anchorDate, string anchorTime)
		{
			return a1(Input, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showAnchored, anchorDate, anchorTime);
		}

		public a1 a1(ISeries<double> input, bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showAnchored, DateTime anchorDate, string anchorTime)
		{
			if (cachea1 != null)
				for (int idx = 0; idx < cachea1.Length; idx++)
					if (cachea1[idx] != null && cachea1[idx].ShowWeekly == showWeekly && cachea1[idx].ShowWeeklyBands1 == showWeeklyBands1 && cachea1[idx].ShowWeeklyBands2 == showWeeklyBands2 && cachea1[idx].ShowSession == showSession && cachea1[idx].ShowAnchored == showAnchored && cachea1[idx].AnchorDate == anchorDate && cachea1[idx].AnchorTime == anchorTime && cachea1[idx].EqualsInput(input))
						return cachea1[idx];
			return CacheIndicator<a1>(new a1(){ ShowWeekly = showWeekly, ShowWeeklyBands1 = showWeeklyBands1, ShowWeeklyBands2 = showWeeklyBands2, ShowSession = showSession, ShowAnchored = showAnchored, AnchorDate = anchorDate, AnchorTime = anchorTime }, input, ref cachea1);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.a1 a1(bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showAnchored, DateTime anchorDate, string anchorTime)
		{
			return indicator.a1(Input, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showAnchored, anchorDate, anchorTime);
		}

		public Indicators.a1 a1(ISeries<double> input , bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showAnchored, DateTime anchorDate, string anchorTime)
		{
			return indicator.a1(input, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showAnchored, anchorDate, anchorTime);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.a1 a1(bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showAnchored, DateTime anchorDate, string anchorTime)
		{
			return indicator.a1(Input, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showAnchored, anchorDate, anchorTime);
		}

		public Indicators.a1 a1(ISeries<double> input , bool showWeekly, bool showWeeklyBands1, bool showWeeklyBands2, bool showSession, bool showAnchored, DateTime anchorDate, string anchorTime)
		{
			return indicator.a1(input, showWeekly, showWeeklyBands1, showWeeklyBands2, showSession, showAnchored, anchorDate, anchorTime);
		}
	}
}

#endregion
