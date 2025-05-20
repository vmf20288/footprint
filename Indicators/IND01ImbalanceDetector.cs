using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.Data;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;

namespace NinjaTrader.NinjaScript.Indicators
{
    // Enumeración para modo de dibujo
    public enum ImbalanceDrawMode { OnClose, RealTime }

    public class IND01ImbalanceDetector : Indicator
    {
        // Parámetros ajustables
        [NinjaScriptProperty]
        [Range(0.01, 1.0)]
        [Display(Name = "Threshold Ratio", Description = "Proporción mínima para considerar un imbalance (ej. 0.25 = 25%)", Order = 1, GroupName = "Parámetros")]
        public double ThresholdRatio { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Lookback Bars", Description = "Número de barras pasadas a considerar para el cálculo de imbalance", Order = 2, GroupName = "Parámetros")]
        public int LookbackBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Draw Mode", Description = "Define si dibuja al cierre de la barra o en tiempo real", Order = 3, GroupName = "Parámetros")]
        public ImbalanceDrawMode DrawModeParam { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Sound", Description = "Activa señal sonora al detectar imbalance", Order = 4, GroupName = "Parámetros")]
        public bool EnableSound { get; set; }

        // Buffers internos para lookback
        private Queue<double> volAskQueue;
        private Queue<double> volBidQueue;
        private int currentVolAsk;
        private int currentVolBid;
        private HashSet<int> drawnBars;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description      = "Detecta desequilibrios entre volumen de compra y venta y marca las barras que superan un umbral";
                Name             = "IND01ImbalanceDetector";
                Calculate        = Calculate.OnEachTick;
                IsOverlay        = true;
                DisplayInDataBox = true;

                // Valores por defecto
                ThresholdRatio   = 0.25;
                LookbackBars     = 1;
                DrawModeParam    = ImbalanceDrawMode.OnClose;
                EnableSound      = false;
            }
            else if (State == State.DataLoaded)
            {
                volAskQueue   = new Queue<double>(LookbackBars);
                volBidQueue   = new Queue<double>(LookbackBars);
                drawnBars     = new HashSet<int>();
                currentVolAsk = 0;
                currentVolBid = 0;
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (CurrentBar < 0 || e.MarketDataType != MarketDataType.Last)
                return;

            // Tick al precio de compra => agresor compra
            if (e.Price.ApproxCompare(GetCurrentBid()) == 0)
                currentVolAsk++;

            // Tick al precio de venta => agresor vende
            if (e.Price.ApproxCompare(GetCurrentAsk()) == 0)
                currentVolBid++;
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < LookbackBars)
                return;

            bool evaluate = false;
            int evalBarIndex = CurrentBar;

            if (DrawModeParam == ImbalanceDrawMode.OnClose)
            {
                if (!IsFirstTickOfBar)
                    return;
                evaluate     = true;
                evalBarIndex = CurrentBar - 1;
            }
            else // RealTime
            {
                evaluate     = true;
                evalBarIndex = CurrentBar;
            }

            if (evaluate && !drawnBars.Contains(evalBarIndex))
            {
                // Actualiza colas de lookback
                volAskQueue.Enqueue(currentVolAsk);
                volBidQueue.Enqueue(currentVolBid);
                if (volAskQueue.Count > LookbackBars) volAskQueue.Dequeue();
                if (volBidQueue.Count > LookbackBars) volBidQueue.Dequeue();

                // Suma volúmenes
                double sumAsk = 0, sumBid = 0;
                foreach (var v in volAskQueue) sumAsk += v;
                foreach (var v in volBidQueue) sumBid += v;

                double total = sumAsk + sumBid;
                if (total > 0)
                {
                    double imbalance = sumAsk - sumBid;
                    double ratio     = imbalance / total;

                    if (Math.Abs(ratio) >= ThresholdRatio)
                    {
                        var rectColor = ratio > 0 ? Brushes.Green : Brushes.Red;

                        // Dibuja rectángulo en la barra desequilibrada
                        Draw.Rectangle(this, "imbRect" + evalBarIndex,
                            false,
                            evalBarIndex, High[evalBarIndex],
                            evalBarIndex, Low[evalBarIndex],
                            rectColor, Brushes.Transparent, 2);

                        if (EnableSound)
                            PlaySound("Alert4.wav");

                        drawnBars.Add(evalBarIndex);
                    }
                }

                // Resetea contadores si trabajamos OnClose
                if (DrawModeParam == ImbalanceDrawMode.OnClose)
                {
                    currentVolAsk = 0;
                    currentVolBid = 0;
                }
            }
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private IND01ImbalanceDetector[] cacheIND01ImbalanceDetector;
		public IND01ImbalanceDetector IND01ImbalanceDetector(double thresholdRatio, int lookbackBars, ImbalanceDrawMode drawModeParam, bool enableSound)
		{
			return IND01ImbalanceDetector(Input, thresholdRatio, lookbackBars, drawModeParam, enableSound);
		}

		public IND01ImbalanceDetector IND01ImbalanceDetector(ISeries<double> input, double thresholdRatio, int lookbackBars, ImbalanceDrawMode drawModeParam, bool enableSound)
		{
			if (cacheIND01ImbalanceDetector != null)
				for (int idx = 0; idx < cacheIND01ImbalanceDetector.Length; idx++)
					if (cacheIND01ImbalanceDetector[idx] != null && cacheIND01ImbalanceDetector[idx].ThresholdRatio == thresholdRatio && cacheIND01ImbalanceDetector[idx].LookbackBars == lookbackBars && cacheIND01ImbalanceDetector[idx].DrawModeParam == drawModeParam && cacheIND01ImbalanceDetector[idx].EnableSound == enableSound && cacheIND01ImbalanceDetector[idx].EqualsInput(input))
						return cacheIND01ImbalanceDetector[idx];
			return CacheIndicator<IND01ImbalanceDetector>(new IND01ImbalanceDetector(){ ThresholdRatio = thresholdRatio, LookbackBars = lookbackBars, DrawModeParam = drawModeParam, EnableSound = enableSound }, input, ref cacheIND01ImbalanceDetector);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.IND01ImbalanceDetector IND01ImbalanceDetector(double thresholdRatio, int lookbackBars, ImbalanceDrawMode drawModeParam, bool enableSound)
		{
			return indicator.IND01ImbalanceDetector(Input, thresholdRatio, lookbackBars, drawModeParam, enableSound);
		}

		public Indicators.IND01ImbalanceDetector IND01ImbalanceDetector(ISeries<double> input , double thresholdRatio, int lookbackBars, ImbalanceDrawMode drawModeParam, bool enableSound)
		{
			return indicator.IND01ImbalanceDetector(input, thresholdRatio, lookbackBars, drawModeParam, enableSound);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.IND01ImbalanceDetector IND01ImbalanceDetector(double thresholdRatio, int lookbackBars, ImbalanceDrawMode drawModeParam, bool enableSound)
		{
			return indicator.IND01ImbalanceDetector(Input, thresholdRatio, lookbackBars, drawModeParam, enableSound);
		}

		public Indicators.IND01ImbalanceDetector IND01ImbalanceDetector(ISeries<double> input , double thresholdRatio, int lookbackBars, ImbalanceDrawMode drawModeParam, bool enableSound)
		{
			return indicator.IND01ImbalanceDetector(input, thresholdRatio, lookbackBars, drawModeParam, enableSound);
		}
	}
}

#endregion
