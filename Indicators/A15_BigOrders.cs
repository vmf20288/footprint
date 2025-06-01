// -----------------------------------------------------------------------------
//  A15_BigOrders.cs   – NinjaTrader 8.1 indicator (MNQ‑optimised)
// -----------------------------------------------------------------------------
//  v2   2025‑06‑01
//  • Big‑print detection  (≥ QtyMinOrder, clustering 0.5 s)
//  • Iceberg detection    (Executed ≥ 3×MaxVisible  &  ≥ 150 MNQ  within 30 s)
//  • NEW:  clusters draw with **gold border**  to distinguish from single ticks
//  • Graphic: two grey rectangles per signal
//       – top‑of‑candle  (context overview)
//       – at trade price (exact level)
//       – text colour red/lime per side
//       – gold stroke when event was a *cluster*
// -----------------------------------------------------------------------------
//  Public Series
//       BigSignal  :  +1 big‑print ask   |  ‑1 big‑print bid
//                      +2 iceberg ask    |  ‑2 iceberg bid
//       BigPrice   :  price of last signal
//       BigVolume  :  volume of last signal
//       HiddenSize :  iceberg hidden portion (0 for big‑prints)
// -----------------------------------------------------------------------------
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;

using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class A15_BigOrders : Indicator
    {
        // -----------------------------  user parameter (editable)
        [Display(Name = "Qty Min Order (MNQ)", GroupName = "Parameters", Order = 0)]
        [Range(1, int.MaxValue)]
        public int QtyMinOrder { get; set; } = 200; // default 200 MNQ (≈20 NQ)

        // -----------------------------  fixed constants (MNQ‑tuned)
        private const int    KeepBars        = 120;
        private const int    CleanInterval   = 20;
        private const double ClusterWindowMs = 500;
        private const int    MinVisibleQty   = 50;
        private const int    IcebergFactor   = 3;
        private const int    IcebergMinExec  = 150;
        private const int    IceWindowSec    = 30;

        // -----------------------------  public output series
        [Browsable(false)] public Series<int>    BigSignal   { get; private set; }
        [Browsable(false)] public Series<double> BigPrice    { get; private set; }
        [Browsable(false)] public Series<int>    BigVolume   { get; private set; }
        [Browsable(false)] public Series<int>    HiddenSize  { get; private set; }

        // -----------------------------  internal structs
        private class Cluster
        {
            public DateTime FirstTime;
            public int      Volume;
            public bool     IsAsk;
        }
        private class IceTrack
        {
            public DateTime Start;
            public int      Executed;
            public int      MaxVisible;
            public bool     IsAsk;
        }

        private readonly Dictionary<double, Cluster>  clusters = new();
        private readonly Dictionary<double, IceTrack> ice      = new();

        // -----------------------------  State
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name             = "A15_BigOrders";
                Description      = "Big prints + iceberg detector (MNQ)";
                Calculate        = Calculate.OnEachTick;
                IsOverlay        = true;
                DisplayInDataBox = false;
            }
            else if (State == State.DataLoaded)
            {
                BigSignal  = new Series<int>(this);
                BigPrice   = new Series<double>(this);
                BigVolume  = new Series<int>(this);
                HiddenSize = new Series<int>(this);
            }
        }

        // -----------------------------  Tick handler (phase‑1  + clustering)
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last) return;

            bool isAsk = e.Price >= GetCurrentAsk() - TickSize * 0.5;
            double key  = e.Price;

            // create / update cluster
            if (!clusters.ContainsKey(key) ||
                (e.Time - clusters[key].FirstTime).TotalMilliseconds > ClusterWindowMs)
            {
                clusters[key] = new Cluster { FirstTime = e.Time, Volume = (int)e.Volume, IsAsk = isAsk };
            }
            else
            {
                clusters[key].Volume += (int)e.Volume;
            }

            // big‑print (single or clustered)
            if (clusters[key].Volume >= QtyMinOrder)
            {
                bool isCluster = (e.Time - clusters[key].FirstTime).TotalMilliseconds > 1; // >1 ms means grouped
                DrawBig(clusters[key].FirstTime, key, clusters[key].Volume, clusters[key].IsAsk, 1, isCluster);
                clusters.Remove(key);
            }
        }

        // -----------------------------  Depth handler (phase‑2  iceberg)
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (e.Operation == Operation.Remove || e.MarketDataType == MarketDataType.Last) return;

            bool isAsk  = e.MarketDataType == MarketDataType.Ask;
            double price = e.Price;
            int visible  = (int)e.Volume;

            if (visible >= MinVisibleQty && !ice.ContainsKey(price))
            {
                ice[price] = new IceTrack { Start = Time[0], MaxVisible = visible, Executed = 0, IsAsk = isAsk };
            }
            else if (ice.ContainsKey(price))
            {
                var t = ice[price];
                if (visible > t.MaxVisible) t.MaxVisible = visible;           // replenishment / stacking
                if ((Time[0] - t.Start).TotalSeconds > IceWindowSec) ice.Remove(price);
            }
        }

        // called from DrawBig to accumulate executed volume on price
        private void UpdateIceExecuted(double price, int volume, bool isAsk)
        {
            if (!ice.ContainsKey(price)) return;
            var t = ice[price];
            if (t.IsAsk != isAsk) return;

            t.Executed += volume;
            if (t.Executed >= IcebergMinExec && t.Executed >= IcebergFactor * t.MaxVisible)
            {
                DrawBig(Time[0], price, t.Executed, isAsk, 2, false, t.Executed - t.MaxVisible);
                ice.Remove(price);
            }
        }

        // -----------------------------  Draw helper
        private void DrawBig(DateTime firstTick, double price, int vol, bool isAsk, int kind, bool isCluster = false, int hidden = 0)
        {
            int barsAgo = Bars.GetBar(firstTick);
            if (barsAgo < 0) return;

            Brush txtClr   = isAsk ? Brushes.Red : Brushes.Lime;
            Brush boxBrush = Brushes.DimGray;
            Brush stroke   = isCluster && kind == 1 ? Brushes.Gold : Brushes.DimGray; // gold border for clusters

            string prefix = kind == 1 ? "BP" : "IC";
            string tagHi  = $"{prefix}H_{CurrentBar}_{firstTick.Ticks}";
            string tagPx  = $"{prefix}P_{CurrentBar}_{firstTick.Ticks}";

            double yHigh = High[barsAgo] + TickSize * 2;
            Draw.Rectangle(this, tagHi, false, barsAgo, yHigh + TickSize, barsAgo, yHigh - TickSize,
                           stroke, boxBrush, 50);
            Draw.Text(this, tagHi + "_t", vol.ToString(), barsAgo, yHigh, txtClr);

            Draw.Rectangle(this, tagPx, false, barsAgo, price + TickSize/2, barsAgo, price - TickSize/2,
                           stroke, boxBrush, 50);
            string txt = kind == 2 ? ($"{vol}\n❄ {hidden}") : vol.ToString();
            Draw.Text(this, tagPx + "_t", txt, barsAgo, price, txtClr);

            // expose series
            BigSignal[0]  = isAsk ? kind : -kind;
            BigPrice[0]   = price;
            BigVolume[0]  = vol;
            HiddenSize[0] = hidden;

            // feed iceberg tracker
            UpdateIceExecuted(price, vol, isAsk);

            // periodic cleanup
            if (CurrentBar > KeepBars && CurrentBar % CleanInterval == 0)
                RemoveDrawObjects();
        }

        // -----------------------------  helper bools (optional)
        public bool IsBigPrintUp   => BigSignal[0] ==  1;
        public bool IsBigPrintDown => BigSignal[0] == -1;
        public bool IsIceUp        => BigSignal[0] ==  2;
        public bool IsIceDown      => BigSignal[0] == -2;
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
