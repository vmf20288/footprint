// =============================================================================
//  Indicator – MNQ Weekly VWAP Bands + Custom Tick‑Rule Delta  ("a12")
//  Versión 0.1 – 2025‑05‑22
//  • Calcula Δ por barra con la regla tick‑rule (ver OnMarketData).
//  • Umbrales: DeltaThreshold = 300, ImbalanceThreshold = 300.
//  • Dibuja señales: 🟦 rebote   🟧 ruptura (pelota sobre vela) + bola superior.
//  • Muestra dos cuadros grandes bajo cada vela: Δ y Volumen.
//  • Usa indicador externo A1() para VWAP semanal central (azul) y ±1 σ (verde).
//  • Fondo blanco → sin Background property; bordes negros.
//  • NO genera órdenes (es Indicator, no Strategy).
// =============================================================================
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.Gui.Tools;           // SimpleFont

namespace NinjaTrader.NinjaScript.Indicators
{
    public class MNQ_VWAP_Bands_OF_a12 : Indicator
    {
        #region === INPUT PARAMETERS ===
        [NinjaScriptProperty]
        [Display(Name = "DeltaThreshold", Order = 0, GroupName = "Parameters")]
        public int DeltaThreshold { get; set; } = 300;

        [NinjaScriptProperty]
        [Display(Name = "ImbalanceThreshold", Order = 1, GroupName = "Parameters")]
        public int ImbalanceThreshold { get; set; } = 300;

        [NinjaScriptProperty]
        [Display(Name = "ATRmultRebote", Order = 2, GroupName = "Parameters")]
        public double ATRmultRebote { get; set; } = 0.7;

        [NinjaScriptProperty]
        [Display(Name = "ATRmultRuptura", Order = 3, GroupName = "Parameters")]
        public double ATRmultRuptura { get; set; } = 0.8;
        #endregion

        #region === PRIVATE FIELDS ===
        private A1 weeklyVWAP;
        private ATR atr;

        //  Tick‑rule delta accumulator & series
        private double deltaBar = 0.0;
        private double lastTradePrice = 0.0;
        private int    lastDirection  = 0;   // +1 / −1
        private Series<double> DeltaSeries;

        //  Visual helpers
        private Brush lastSignalBrush = Brushes.Transparent;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name              = "MNQ_VWAP_Bands_OF_a12";
                Description       = "Weekly VWAP ±1σ reaction signals with custom tick‑rule Delta.";
                Calculate         = Calculate.OnBarClose;
                IsOverlay         = true;
                DisplayInDataBox  = false;
                PaintPriceMarkers = false;
            }
            else if (State == State.DataLoaded)
            {
                weeklyVWAP  = a1(true, true, false, false, false, DateTime.Today, "00:00");
                atr         = ATR(14);
                DeltaSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
            }
        }

        // ============================================================
        //  Tick stream – build Delta
        // ============================================================
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0 || e.MarketDataType != MarketDataType.Last)
                return;

            double price = e.Price;
            double vol   = e.Volume;
            int sign;

            if      (price > lastTradePrice) sign =  1;
            else if (price < lastTradePrice) sign = -1;
            else                             sign = lastDirection;

            deltaBar      += sign * vol;
            lastTradePrice = price;
            if (sign != 0) lastDirection = sign;
        }

        // ============================================================
        //  Bar close – store Delta & evaluate signals
        // ============================================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 20) return;

            // Store delta
            DeltaSeries[0] = deltaBar;
            int barDelta = (int)deltaBar;
            deltaBar = 0.0;        // reset for next bar

            // VWAP bands
            double vwap   = weeklyVWAP.VWAP[0];
            double upper1 = weeklyVWAP.Upper1[0];
            double lower1 = weeklyVWAP.Lower1[0];

            double barRange = High[0] - Low[0];
            double atrVal   = atr[0];

            // Touch logic
            bool touchUpper   = High[0] >= upper1 && Close[0] <= upper1 + TickSize * 2;
            bool touchLower   = Low[0]  <= lower1 && Close[0] >= lower1 - TickSize * 2;
            bool touchCentral = (High[0] >= vwap && Low[0] <= vwap);

            // --------------------------- Rebote (blue) ------------------------
            bool deltaFlip = Math.Sign(barDelta) != Math.Sign(DeltaSeries[1]) && Math.Abs(barDelta) >= DeltaThreshold;
            bool possibleRebote = (touchUpper || touchLower || touchCentral) &&
                                   barRange >= ATRmultRebote * atrVal &&
                                   deltaFlip &&
                                   Close[0] < upper1 && Close[0] > lower1;

            // --------------------------- Ruptura (orange) ---------------------
            bool closeOutsideUpper = Close[0] > upper1 && (Close[0] - upper1) >= 0.25 * barRange;
            bool closeOutsideLower = Close[0] < lower1 && (lower1 - Close[0]) >= 0.25 * barRange;
            bool possibleRuptura = (closeOutsideUpper || closeOutsideLower) &&
                                     barRange >= ATRmultRuptura * atrVal &&
                                     Math.Abs(barDelta) >= DeltaThreshold;

            Brush signalBrush = null;
            if (possibleRebote)
                signalBrush = Brushes.SteelBlue;
            else if (possibleRuptura)
                signalBrush = Brushes.DarkOrange;

            if (signalBrush != null)
            {
                DrawPelotaOnBar(CurrentBar, signalBrush);
                lastSignalBrush = signalBrush;
            }

            // Draw Delta & Volume boxes under each bar
            DrawDeltaVolumeBoxes(CurrentBar, barDelta, Volume[0]);
        }

        // ============================================================
        //  Helper: Pelota on bar + top status ball
        // ============================================================
        private void DrawPelotaOnBar(int barIdx, Brush color)
        {
            double y = (High[barIdx] + Low[barIdx]) / 2;
            string tag = $"p_{barIdx}";
            Draw.Ellipse(this, tag, false, Time[barIdx], y, Time[barIdx], y, color, Brushes.Black, 10);

            // Top ball status
            double topY = Highs[BarsInProgress].GetValueAt(barIdx) * 1.02;
            Draw.Ellipse(this, "p_top", false, Time[barIdx], topY, Time[barIdx], topY, color, Brushes.Black, 15);
        }

        // ============================================================
        //  Helper: Delta & Volume boxes under each bar
        // ============================================================
        private void DrawDeltaVolumeBoxes(int barIdx, int delta, double vol)
        {
            Brush backDelta = delta >= 0 ? Brushes.LightGreen : Brushes.LightCoral;
            Brush textBrush = Brushes.Black;

            string dTag = $"d_{barIdx}";
            string vTag = $"v_{barIdx}";

            double yDelta = Low[barIdx] - 2 * TickSize;
            double yVol   = Low[barIdx] - 4 * TickSize;

            var dTxt = Draw.Text(this, dTag, delta.ToString(), Time[barIdx], yDelta, textBrush);
            dTxt.Font = new SimpleFont("Arial", 12);
            dTxt.AreaBrush = backDelta;
            dTxt.OutlineStroke = new Stroke(Brushes.Black, 1);

            var vTxt = Draw.Text(this, vTag, vol.ToString(), Time[barIdx], yVol, textBrush);
            vTxt.Font = new SimpleFont("Arial", 12);
            vTxt.AreaBrush = Brushes.White;
            vTxt.OutlineStroke = new Stroke(Brushes.Black, 1);
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private MNQ_VWAP_Bands_OF_a12[] cacheMNQ_VWAP_Bands_OF_a12;
		public MNQ_VWAP_Bands_OF_a12 MNQ_VWAP_Bands_OF_a12(int deltaThreshold, int imbalanceThreshold, double aTRmultRebote, double aTRmultRuptura)
		{
			return MNQ_VWAP_Bands_OF_a12(Input, deltaThreshold, imbalanceThreshold, aTRmultRebote, aTRmultRuptura);
		}

		public MNQ_VWAP_Bands_OF_a12 MNQ_VWAP_Bands_OF_a12(ISeries<double> input, int deltaThreshold, int imbalanceThreshold, double aTRmultRebote, double aTRmultRuptura)
		{
			if (cacheMNQ_VWAP_Bands_OF_a12 != null)
				for (int idx = 0; idx < cacheMNQ_VWAP_Bands_OF_a12.Length; idx++)
					if (cacheMNQ_VWAP_Bands_OF_a12[idx] != null && cacheMNQ_VWAP_Bands_OF_a12[idx].DeltaThreshold == deltaThreshold && cacheMNQ_VWAP_Bands_OF_a12[idx].ImbalanceThreshold == imbalanceThreshold && cacheMNQ_VWAP_Bands_OF_a12[idx].ATRmultRebote == aTRmultRebote && cacheMNQ_VWAP_Bands_OF_a12[idx].ATRmultRuptura == aTRmultRuptura && cacheMNQ_VWAP_Bands_OF_a12[idx].EqualsInput(input))
						return cacheMNQ_VWAP_Bands_OF_a12[idx];
			return CacheIndicator<MNQ_VWAP_Bands_OF_a12>(new MNQ_VWAP_Bands_OF_a12(){ DeltaThreshold = deltaThreshold, ImbalanceThreshold = imbalanceThreshold, ATRmultRebote = aTRmultRebote, ATRmultRuptura = aTRmultRuptura }, input, ref cacheMNQ_VWAP_Bands_OF_a12);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.MNQ_VWAP_Bands_OF_a12 MNQ_VWAP_Bands_OF_a12(int deltaThreshold, int imbalanceThreshold, double aTRmultRebote, double aTRmultRuptura)
		{
			return indicator.MNQ_VWAP_Bands_OF_a12(Input, deltaThreshold, imbalanceThreshold, aTRmultRebote, aTRmultRuptura);
		}

		public Indicators.MNQ_VWAP_Bands_OF_a12 MNQ_VWAP_Bands_OF_a12(ISeries<double> input , int deltaThreshold, int imbalanceThreshold, double aTRmultRebote, double aTRmultRuptura)
		{
			return indicator.MNQ_VWAP_Bands_OF_a12(input, deltaThreshold, imbalanceThreshold, aTRmultRebote, aTRmultRuptura);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.MNQ_VWAP_Bands_OF_a12 MNQ_VWAP_Bands_OF_a12(int deltaThreshold, int imbalanceThreshold, double aTRmultRebote, double aTRmultRuptura)
		{
			return indicator.MNQ_VWAP_Bands_OF_a12(Input, deltaThreshold, imbalanceThreshold, aTRmultRebote, aTRmultRuptura);
		}

		public Indicators.MNQ_VWAP_Bands_OF_a12 MNQ_VWAP_Bands_OF_a12(ISeries<double> input , int deltaThreshold, int imbalanceThreshold, double aTRmultRebote, double aTRmultRuptura)
		{
			return indicator.MNQ_VWAP_Bands_OF_a12(input, deltaThreshold, imbalanceThreshold, aTRmultRebote, aTRmultRuptura);
		}
	}
}

#endregion
