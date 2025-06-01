// -----------------------------------------------------------------------------
//  A15_BigOrders.cs   (NinjaTrader 8.1 indicator)
// -----------------------------------------------------------------------------
//  Detects “big prints” ≥ QtyMinOrder (default 20 NQ ≈ 200 MNQ) and draws:
//    • A grey box with the volume above the candle high
//    • A grey box at the exact trade price
//  Text colour:  red (Ask) /  lime (Bid)
//  Exposes Series<int> BigSignal   (1 = Ask big‑print,  ‑1 = Bid big‑print)
//  Future phase‑2 will extend to iceberg detection.
// -----------------------------------------------------------------------------

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;

using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class A15_BigOrders : Indicator
    {
        // -----------------------------------------------------------  Parameters
        [Display(Name = "Qty Min Order (NQ)", GroupName = "Parameters", Order = 0)]
        [Range(1, int.MaxValue)]
        public int QtyMinOrder { get; set; } = 20;          // default 20 NQ

        // Public series so other indicators / strategies can read
        [Browsable(false)] public Series<int> BigSignal { get; private set; }

        // -----------------------------------------------------------  State
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "A15_BigOrders";
                Description             = "Detects big prints (>= QtyMinOrder) and marks them.";
                Calculate               = Calculate.OnEachTick;   // need tick granularity
                IsOverlay               = true;                   // draw over price panel
                DisplayInDataBox        = false;
            }
            else if (State == State.DataLoaded)
            {
                BigSignal = new Series<int>(this);
            }
        }

        // -----------------------------------------------------------  Tick handler
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last) return;  // only prints
            if (e.Volume < QtyMinOrder)          return;          // not big enough

            // Determine aggressor side (rough) via best bid/ask at that moment
            double bid = GetCurrentBid();
            double ask = GetCurrentAsk();
            bool   isAskAggressor = e.Price >= ask - TickSize * 0.5;   // in ask column

            Brush txtClr  = isAskAggressor ? Brushes.Red  : Brushes.Lime;
            int   signal  = isAskAggressor ? 1 : -1;

            // Candle index that contains this tick
            int barsAgo = Bars.GetBar(e.Time);
            if (barsAgo < 0) return;   // tick older than loaded data

            // ---------------- draw grey box above candle high
            double yPlot = High[barsAgo] + (TickSize * 2);
            string tagHi = $"BO_H_{CurrentBar}_{e.Time.Ticks}";

            Draw.Rectangle(this, tagHi, false, barsAgo, yPlot + TickSize,
                                            barsAgo, yPlot - TickSize,
                                            Brushes.DimGray, Brushes.DimGray, 50);
            Draw.Text(this, tagHi + "_txt", false,
                      e.Volume.ToString(), barsAgo, yPlot, txtClr);

            // ---------------- draw grey box at trade price level
            string tagPx = $"BO_P_{CurrentBar}_{e.Time.Ticks}";
            Draw.Rectangle(this, tagPx, false, barsAgo, e.Price + TickSize/2,
                                            barsAgo, e.Price - TickSize/2,
                                            Brushes.DimGray, Brushes.DimGray, 50);
            Draw.Text(this, tagPx + "_txt", false,
                      e.Volume.ToString(), barsAgo, e.Price, txtClr);

            // ---------------- expose signal for other scripts
            BigSignal[0] = signal;

            // ---------------- housekeeping: purge very old tags (keep last 400 bars)
            if (CurrentBar > 400)
                RemoveDrawObjects();
        }

        // -----------------------------------------------------------  Helper for other scripts
        public bool IsBigPrintUp   => BigSignal[0] ==  1;   // ask side
        public bool IsBigPrintDown => BigSignal[0] == -1;   // bid side
    }
}


#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private A15_BigOrders[] cacheA15_BigOrders;
        public A15_BigOrders A15_BigOrders()
        {
            return A15_BigOrders(Input);
        }

        public A15_BigOrders A15_BigOrders(ISeries<double> input)
        {
            if (cacheA15_BigOrders != null)
                for (int idx = 0; idx < cacheA15_BigOrders.Length; idx++)
                    if (cacheA15_BigOrders[idx] != null &&  cacheA15_BigOrders[idx].EqualsInput(input))
                        return cacheA15_BigOrders[idx];
            return CacheIndicator<A15_BigOrders>(new A15_BigOrders(), input, ref cacheA15_BigOrders);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.A15_BigOrders A15_BigOrders()
        {
            return indicator.A15_BigOrders(Input);
        }

        public Indicators.A15_BigOrders A15_BigOrders(ISeries<double> input )
        {
            return indicator.A15_BigOrders(input);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.A15_BigOrders A15_BigOrders()
        {
            return indicator.A15_BigOrders(Input);
        }

        public Indicators.A15_BigOrders A15_BigOrders(ISeries<double> input )
        {
            return indicator.A15_BigOrders(input);
        }
    }
}

#endregion
