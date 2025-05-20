using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;

namespace NinjaTrader.NinjaScript.Indicators
{
    public enum ResetMode { None, Session }
    public enum DivergenceMode { SlopeOnly, SlopeAndSwing }

    public class IND02_CumulativeDelta : Indicator
    {
        private Series<double> cumDelta;
        private EMA ema;
        private Series<int> divergenceSignal;
        private double runningDelta;
        private double barAskVol;
        private double barBidVol;
        private int lastBarIndexInUpdate;
        private bool presetApplied;

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Reset Mode", Order = 0, GroupName = "Parameters",
                 Description = "El acumulador se reinicia al iniciar sesión (Session) o nunca (None).")]
        public ResetMode ResetMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Look Back Velas", Order = 1, GroupName = "Parameters",
                 Description = "Número de barras usadas para comparar pendientes en la detección de divergencias.")]
        public int LookBackVelas { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA Period", Order = 2, GroupName = "Parameters",
                 Description = "Número de barras usadas para la Media Móvil Exponencial sobre Delta Acumulativo.")]
        public int SmoothingPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Divergence Mode", Order = 3, GroupName = "Parameters",
                 Description = "Método de divergencia: sólo pendiente (SlopeOnly) o pendiente + pivot (SlopeAndSwing).")]
        public DivergenceMode DivergenceMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Swing Bars", Order = 4, GroupName = "Parameters",
                 Description = "Número de barras laterales para confirmar un pivote swing en precio/EMA.")]
        public int SwingBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Quick Preset", Order = 5, GroupName = "Parameters",
                 Description = "Activa valores rápidos (5,3,SlopeOnly). Desmarca al modificar propiedades.")]
        public bool QuickPreset { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Signals", Order = 6, GroupName = "Parameters",
                 Description = "Muestra triángulos de señal gráfica; desactivar para usar solo DivergenceSignalSeries.")]
        public bool ShowSignals { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10000)]
        [Display(Name = "Min Bar Volume", Order = 7, GroupName = "Filters",
                 Description = "Volumen mínimo de contratos en la barra para validar señal.")]
        public int MinBarVolume { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "Slope Ticks Min", Order = 8, GroupName = "Filters",
                 Description = "Ticks mínimos de movimiento en precio para validar divergencia.")]
        public int SlopeTicksMin { get; set; }

        [NinjaScriptProperty]
        [Range(0, 50)]
        [Display(Name = "EMA Slope Ticks Min", Order = 9, GroupName = "Filters",
                 Description = "Ticks mínimos de movimiento en EMA para validar divergencia.")]
        public int EmaSlopeTicksMin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Imbalance Threshold", Order = 10, GroupName = "Filters",
                 Description = "Ratio mínimo de askVol/bidVol o bidVol/askVol para validar señal.")]
        public double ImbalanceThreshold { get; set; }

        [Browsable(false)]
        [XmlIgnore]
        public Series<int> DivergenceSignalSeries => divergenceSignal;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description      = "Divergencia Delta–Precio filtrada por volumen, pendiente y imbalance.";
                Name             = "IND02_CumulativeDelta";
                Calculate        = Calculate.OnEachTick;
                IsOverlay        = false;
                DisplayInDataBox = true;
                DrawOnPricePanel = false;

                AddPlot(Brushes.Orange, "EMA_CumDelta");

                ResetMode             = ResetMode.Session;
                SmoothingPeriod       = 8;
                LookBackVelas         = 6;
                DivergenceMode        = DivergenceMode.SlopeOnly;
                SwingBars             = 1;
                QuickPreset           = false;
                ShowSignals           = true;

                MinBarVolume          = 50;
                SlopeTicksMin         = 3;
                EmaSlopeTicksMin      = 2;
                ImbalanceThreshold    = 1.5;
            }
            else if (State == State.DataLoaded)
            {
                cumDelta         = new Series<double>(this);
                divergenceSignal = new Series<int>(this);
                ema              = EMA(cumDelta, SmoothingPeriod);

                barAskVol        = 0;
                barBidVol        = 0;
                lastBarIndexInUpdate = -1;
                presetApplied    = false;
            }
        }

        protected override void OnMarketData(MarketDataEventArgs args)
        {
            if (BarsInProgress != 0) return;

            // Track raw delta
            if (args.MarketDataType == MarketDataType.Ask)
            {
                runningDelta += args.Volume;
                barAskVol    += args.Volume;
            }
            else if (args.MarketDataType == MarketDataType.Bid)
            {
                runningDelta -= args.Volume;
                barBidVol    += args.Volume;
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0) return;

            // Reset bar-level volumes at new bar
            if (CurrentBar != lastBarIndexInUpdate)
            {
                barAskVol = barBidVol = 0;
                lastBarIndexInUpdate = CurrentBar;
            }

            // QuickPreset logic
            if (QuickPreset && !presetApplied)
            {
                SmoothingPeriod  = 5;
                LookBackVelas    = 3;
                DivergenceMode   = DivergenceMode.SlopeOnly;
                presetApplied    = true;
            }
            else if (QuickPreset && presetApplied)
            {
                if (SmoothingPeriod != 5 || LookBackVelas != 3 || DivergenceMode != DivergenceMode.SlopeOnly)
                {
                    QuickPreset   = false;
                    presetApplied = false;
                }
            }

            // Reset session delta
            if (CurrentBar > 0 && ResetMode == ResetMode.Session && Bars.IsFirstBarOfSession)
                runningDelta = 0;

            // Update series
            cumDelta[0]  = runningDelta;
            Values[0][0] = ema[0];

            int signal = 0;
            if (CurrentBar >= LookBackVelas)
            {
                double priceSlope = Close[0] - Close[LookBackVelas];
                double emaSlope   = ema[0]   - ema[LookBackVelas];
                bool slopeBearish = priceSlope > 0 && emaSlope < 0;
                bool slopeBullish = priceSlope < 0 && emaSlope > 0;

                if (DivergenceMode == DivergenceMode.SlopeOnly)
                {
                    if (slopeBearish) signal = -1;
                    else if (slopeBullish) signal = 1;
                }
                else // SlopeAndSwing
                {
                    bool swingBearish = IsSwingHighPrice(CurrentBar) && IsSwingHighEMA(CurrentBar) && slopeBearish;
                    bool swingBullish = IsSwingLowPrice(CurrentBar)  && IsSwingLowEMA(CurrentBar)  && slopeBullish;
                    if (swingBearish) signal = -1;
                    else if (swingBullish) signal = 1;
                }

                // Filtering by volume
                if (signal != 0 && Bars.GetVolume(CurrentBar) < MinBarVolume)
                    signal = 0;

                // Filtering by tick thresholds & imbalance
                if (signal == 1)
                {
                    if (priceSlope < TickSize * SlopeTicksMin || emaSlope < TickSize * EmaSlopeTicksMin)
                        signal = 0;
                    else
                    {
                        double ratio = barAskVol / (barBidVol == 0 ? 1 : barBidVol);
                        if (ratio < ImbalanceThreshold)
                            signal = 0;
                    }
                }
                else if (signal == -1)
                {
                    if (-priceSlope < TickSize * SlopeTicksMin || -emaSlope < TickSize * EmaSlopeTicksMin)
                        signal = 0;
                    else
                    {
                        double ratio2 = barBidVol / (barAskVol == 0 ? 1 : barAskVol);
                        if (ratio2 < ImbalanceThreshold)
                            signal = 0;
                    }
                }
            }

            divergenceSignal[0] = signal;

            // Draw
            if (ShowSignals && signal != 0)
            {
                double y      = Values[0][0];
                double offset = TickSize * 10;
                string tag    = (signal == 1 ? "bullDiv" : "bearDiv") + CurrentBar;
                if (signal == 1)
                    Draw.TriangleUp(this, tag, false, 0, y - offset, Brushes.Lime);
                else
                    Draw.TriangleDown(this, tag, false, 0, y + offset, Brushes.Red);
            }
        }

        #region Swing Detection
        private bool IsSwingHighPrice(int idx)
        {
            if (idx < SwingBars) return false;
            for (int i = idx - SwingBars; i < idx; i++) if (High[i] >= High[idx]) return false;
            return true;
        }
        private bool IsSwingHighEMA(int idx)
        {
            if (idx < SwingBars) return false;
            for (int i = idx - SwingBars; i < idx; i++) if (ema[i] >= ema[idx]) return false;
            return true;
        }
        private bool IsSwingLowPrice(int idx)
        {
            if (idx < SwingBars) return false;
            for (int i = idx - SwingBars; i < idx; i++) if (Low[i] <= Low[idx]) return false;
            return true;
        }
        private bool IsSwingLowEMA(int idx)
        {
            if (idx < SwingBars) return false;
            for (int i = idx - SwingBars; i < idx; i++) if (ema[i] <= ema[idx]) return false;
            return true;
        }
        #endregion
    }
}


#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private IND02_CumulativeDelta[] cacheIND02_CumulativeDelta;
		public IND02_CumulativeDelta IND02_CumulativeDelta(ResetMode resetMode, int lookBackVelas, int smoothingPeriod, DivergenceMode divergenceMode, int swingBars, bool quickPreset, bool showSignals, int minBarVolume, int slopeTicksMin, int emaSlopeTicksMin, double imbalanceThreshold)
		{
			return IND02_CumulativeDelta(Input, resetMode, lookBackVelas, smoothingPeriod, divergenceMode, swingBars, quickPreset, showSignals, minBarVolume, slopeTicksMin, emaSlopeTicksMin, imbalanceThreshold);
		}

		public IND02_CumulativeDelta IND02_CumulativeDelta(ISeries<double> input, ResetMode resetMode, int lookBackVelas, int smoothingPeriod, DivergenceMode divergenceMode, int swingBars, bool quickPreset, bool showSignals, int minBarVolume, int slopeTicksMin, int emaSlopeTicksMin, double imbalanceThreshold)
		{
			if (cacheIND02_CumulativeDelta != null)
				for (int idx = 0; idx < cacheIND02_CumulativeDelta.Length; idx++)
					if (cacheIND02_CumulativeDelta[idx] != null && cacheIND02_CumulativeDelta[idx].ResetMode == resetMode && cacheIND02_CumulativeDelta[idx].LookBackVelas == lookBackVelas && cacheIND02_CumulativeDelta[idx].SmoothingPeriod == smoothingPeriod && cacheIND02_CumulativeDelta[idx].DivergenceMode == divergenceMode && cacheIND02_CumulativeDelta[idx].SwingBars == swingBars && cacheIND02_CumulativeDelta[idx].QuickPreset == quickPreset && cacheIND02_CumulativeDelta[idx].ShowSignals == showSignals && cacheIND02_CumulativeDelta[idx].MinBarVolume == minBarVolume && cacheIND02_CumulativeDelta[idx].SlopeTicksMin == slopeTicksMin && cacheIND02_CumulativeDelta[idx].EmaSlopeTicksMin == emaSlopeTicksMin && cacheIND02_CumulativeDelta[idx].ImbalanceThreshold == imbalanceThreshold && cacheIND02_CumulativeDelta[idx].EqualsInput(input))
						return cacheIND02_CumulativeDelta[idx];
			return CacheIndicator<IND02_CumulativeDelta>(new IND02_CumulativeDelta(){ ResetMode = resetMode, LookBackVelas = lookBackVelas, SmoothingPeriod = smoothingPeriod, DivergenceMode = divergenceMode, SwingBars = swingBars, QuickPreset = quickPreset, ShowSignals = showSignals, MinBarVolume = minBarVolume, SlopeTicksMin = slopeTicksMin, EmaSlopeTicksMin = emaSlopeTicksMin, ImbalanceThreshold = imbalanceThreshold }, input, ref cacheIND02_CumulativeDelta);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.IND02_CumulativeDelta IND02_CumulativeDelta(ResetMode resetMode, int lookBackVelas, int smoothingPeriod, DivergenceMode divergenceMode, int swingBars, bool quickPreset, bool showSignals, int minBarVolume, int slopeTicksMin, int emaSlopeTicksMin, double imbalanceThreshold)
		{
			return indicator.IND02_CumulativeDelta(Input, resetMode, lookBackVelas, smoothingPeriod, divergenceMode, swingBars, quickPreset, showSignals, minBarVolume, slopeTicksMin, emaSlopeTicksMin, imbalanceThreshold);
		}

		public Indicators.IND02_CumulativeDelta IND02_CumulativeDelta(ISeries<double> input , ResetMode resetMode, int lookBackVelas, int smoothingPeriod, DivergenceMode divergenceMode, int swingBars, bool quickPreset, bool showSignals, int minBarVolume, int slopeTicksMin, int emaSlopeTicksMin, double imbalanceThreshold)
		{
			return indicator.IND02_CumulativeDelta(input, resetMode, lookBackVelas, smoothingPeriod, divergenceMode, swingBars, quickPreset, showSignals, minBarVolume, slopeTicksMin, emaSlopeTicksMin, imbalanceThreshold);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.IND02_CumulativeDelta IND02_CumulativeDelta(ResetMode resetMode, int lookBackVelas, int smoothingPeriod, DivergenceMode divergenceMode, int swingBars, bool quickPreset, bool showSignals, int minBarVolume, int slopeTicksMin, int emaSlopeTicksMin, double imbalanceThreshold)
		{
			return indicator.IND02_CumulativeDelta(Input, resetMode, lookBackVelas, smoothingPeriod, divergenceMode, swingBars, quickPreset, showSignals, minBarVolume, slopeTicksMin, emaSlopeTicksMin, imbalanceThreshold);
		}

		public Indicators.IND02_CumulativeDelta IND02_CumulativeDelta(ISeries<double> input , ResetMode resetMode, int lookBackVelas, int smoothingPeriod, DivergenceMode divergenceMode, int swingBars, bool quickPreset, bool showSignals, int minBarVolume, int slopeTicksMin, int emaSlopeTicksMin, double imbalanceThreshold)
		{
			return indicator.IND02_CumulativeDelta(input, resetMode, lookBackVelas, smoothingPeriod, divergenceMode, swingBars, quickPreset, showSignals, minBarVolume, slopeTicksMin, emaSlopeTicksMin, imbalanceThreshold);
		}
	}
}

#endregion
