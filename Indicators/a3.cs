#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;                 // DisplayName / Category
using System.Linq;                           // ToArray()
using System.Windows.Media;                  // Brushes
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;

using SharpDX;                               // DirectX structs
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using WpfBrush  = System.Windows.Media.Brush;            // alias WPF
using WpfSolid  = System.Windows.Media.SolidColorBrush;  // alias WPF
using D2DBrush  = SharpDX.Direct2D1.Brush;               // alias D2D
using D2DSolid  = SharpDX.Direct2D1.SolidColorBrush;     // alias D2D
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// A3 – Base Order‑Flow Footprint (Bid/Ask map, Delta, CumDelta) – NinjaTrader 8.1.4+
    /// • Clasifica agresión usando Bid/Ask en tiempo real + tick rule fallback.
    /// • Crea footprint por vela (precio → (bidVol, askVol)).
    /// • Calcula Delta y Cumulative Delta con reinicio al inicio de cada sesión.
    /// </summary>
    public class A3 : Indicator
    {
        // ─────────────────── Campos internos ───────────────────
        private Dictionary<double, (long bid, long ask)> footprintCells;
        private long   cumulativeDelta;
        private double lastBid  = double.NaN;
        private double lastAsk  = double.NaN;
        private double lastPrice = double.NaN;   // último print

        // ─────────────────── Propiedades de usuario ───────────────────
        [NinjaScriptProperty]
        [DisplayName("Is White Background")]
        [Category("Visual")]
        public bool IsWhiteBackground { get; set; }

        // ─────────────────── Ciclo de vida ───────────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Base Order‑Flow Footprint: Bid/Ask, Delta y CumDelta";
                Name        = "A3";
                Calculate   = Calculate.OnEachTick;  // requiere Tick Replay manual
                IsOverlay   = false;                 // sub‑panel por defecto

                AddPlot(Brushes.Gray, "Delta");
                AddPlot(Brushes.Blue,  "CumDelta");
            }
            else if (State == State.Configure)
            {
                footprintCells  = new Dictionary<double, (long bid, long ask)>();
                cumulativeDelta = 0;
            }
        }

        // ─────────────────── Toma de datos ───────────────────
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            // Actualiza cotización viva
            if (e.MarketDataType == MarketDataType.Bid)
            {
                lastBid = e.Price;
                return;
            }
            if (e.MarketDataType == MarketDataType.Ask)
            {
                lastAsk = e.Price;
                return;
            }

            // Solo procesamos prints reales
            if (e.MarketDataType != MarketDataType.Last || e.Volume <= 0)
                return;

            // Determina agresor
            bool isBuyAggressor;
            if (!double.IsNaN(lastAsk) && e.Price >= lastAsk)          isBuyAggressor = true;
            else if (!double.IsNaN(lastBid) && e.Price <= lastBid)     isBuyAggressor = false;
            else if (!double.IsNaN(lastPrice))                         isBuyAggressor = e.Price > lastPrice; // tick rule
            else                                                      isBuyAggressor = true; // default primer tick

            lastPrice = e.Price;

            // Registra en el footprint
            if (!footprintCells.TryGetValue(e.Price, out var vols))
                vols = (0, 0);

            if (isBuyAggressor) vols.ask += e.Volume; else vols.bid += e.Volume;
            footprintCells[e.Price] = vols;
        }

        // ─────────────────── Cálculo por vela ───────────────────
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1)
                return;

            // reinicio al inicio de sesión
            if (Bars.IsFirstBarOfSession)
            {
                cumulativeDelta = 0;
            }

            long barDelta = 0;
            foreach (var kvp in footprintCells)
                barDelta += kvp.Value.ask - kvp.Value.bid;

            cumulativeDelta += barDelta;

            Values[0][0] = barDelta;        // Delta
            Values[1][0] = cumulativeDelta; // CumDelta

            footprintCells.Clear();          // comienza footprint de la siguiente vela
        }

        // ─────────────────── Dibujo ───────────────────
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (CurrentBar < 1 || footprintCells.Count == 0)
                return;

            int drawIdx = CurrentBar;
            if (drawIdx >= Bars.Count)
                return;

            double hi = High.GetValueAt(drawIdx);
            double lo = Low.GetValueAt(drawIdx);

            var chartBars = chartControl.BarsArray[0];
            float barMidX = chartControl.GetXByBarIndex(chartBars, drawIdx);
            float barWid  = chartControl.GetBarPaintWidth(chartBars);
            float cellH   = (chartScale.GetYByValue(hi) - chartScale.GetYByValue(lo)) / 10f;

            // brushes para relleno
            D2DBrush upBrush   = chartBars.Properties.ChartStyle.UpBrushDX;
            D2DBrush downBrush = chartBars.Properties.ChartStyle.DownBrushDX;

            // color de texto según user-pref
            WpfSolid txtColor = IsWhiteBackground
                ? new WpfSolid(System.Windows.Media.Colors.Black)
                : (chartControl.Properties.ChartText as WpfSolid);

            using (var txtBrushDx = new D2DSolid(
                       RenderTarget,
                       new Color4(txtColor.Color.R / 255f,
                                  txtColor.Color.G / 255f,
                                  txtColor.Color.B / 255f,
                                  1f)))
            using (var txtFormat = chartControl.Properties.LabelFont.ToDirectWriteTextFormat())
            {
                var cellsSnapshot = footprintCells.ToArray();   // copia segura
                foreach (var kvp in cellsSnapshot)
                {
                    float y = (float)chartScale.GetYByValue(kvp.Key);
                    var rect = new RectangleF(
                        barMidX - barWid / 2,
                        y - cellH / 2,
                        barWid,
                        cellH);

                    RenderTarget.FillRectangle(rect,
                        kvp.Value.ask > kvp.Value.bid ? upBrush : downBrush);

                    // Bid (izq)
                    var bidRect = new RectangleF(rect.Left + 2, rect.Top + 2,
                                                 rect.Width / 2 - 4, rect.Height - 4);
                    RenderTarget.DrawText(kvp.Value.bid.ToString(),
                                          txtFormat, bidRect, txtBrushDx);

                    // Ask (der)
                    var askRect = new RectangleF(rect.Left + rect.Width / 2 + 2, rect.Top + 2,
                                                 rect.Width / 2 - 4, rect.Height - 4);
                    RenderTarget.DrawText(kvp.Value.ask.ToString(),
                                          txtFormat, askRect, txtBrushDx);
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
		private A3[] cacheA3;
		public A3 A3(bool isWhiteBackground)
		{
			return A3(Input, isWhiteBackground);
		}

		public A3 A3(ISeries<double> input, bool isWhiteBackground)
		{
			if (cacheA3 != null)
				for (int idx = 0; idx < cacheA3.Length; idx++)
					if (cacheA3[idx] != null && cacheA3[idx].IsWhiteBackground == isWhiteBackground && cacheA3[idx].EqualsInput(input))
						return cacheA3[idx];
			return CacheIndicator<A3>(new A3(){ IsWhiteBackground = isWhiteBackground }, input, ref cacheA3);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.A3 A3(bool isWhiteBackground)
		{
			return indicator.A3(Input, isWhiteBackground);
		}

		public Indicators.A3 A3(ISeries<double> input , bool isWhiteBackground)
		{
			return indicator.A3(input, isWhiteBackground);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.A3 A3(bool isWhiteBackground)
		{
			return indicator.A3(Input, isWhiteBackground);
		}

		public Indicators.A3 A3(ISeries<double> input , bool isWhiteBackground)
		{
			return indicator.A3(input, isWhiteBackground);
		}
	}
}

#endregion
