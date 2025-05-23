using System;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Strategies;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class a10 : Strategy
    {
        #region Parameters
        [NinjaScriptProperty]
        public int DeltaThreshold { get; set; } = 300;

        [NinjaScriptProperty]
        public double ATRmultRebote { get; set; } = 0.7;

        [NinjaScriptProperty]
        public double ATRmultRuptura { get; set; } = 0.8;

        [NinjaScriptProperty]
        public int StopTicks { get; set; } = 100;

        [NinjaScriptProperty]
        public int PullbackTicks { get; set; } = 20;

        [NinjaScriptProperty]
        public bool RequireTickReplay { get; set; } = false;
        #endregion

        #region Private fields
        private ATR atr;
        private a1 weeklyVWAP;
        private a6 deltaInd;
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "a10";
                Description = "VWAP ±1σ + Tick rule Δ strategy";
                Calculate = Calculate.OnBarClose;
                IsOverlay = false;
                EntriesPerDirection = 1;
            }
            else if (State == State.Configure)
            {
                if (RequireTickReplay && !Bars.IsTickReplay)
                    throw new Exception("► Debe activarse Tick Replay para esta estrategia ←");

                AddDataSeries(BarsPeriodType.Minute, 1);
            }
            else if (State == State.DataLoaded)
            {
                atr = ATR(14);
                weeklyVWAP = a1(true, true, false, false, false, DateTime.Today, "00:00");
                deltaInd = a6(12);

                AddChartIndicator(weeklyVWAP);
                AddChartIndicator(deltaInd);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBars[0] < 20)
                return;

            if (double.IsNaN(deltaInd.DeltaSeries[0]) || double.IsNaN(deltaInd.DeltaSeries[1]))
                return;

            double vwap   = weeklyVWAP.Values[0][0];
            double upper1 = weeklyVWAP.Values[1][0];
            double lower1 = weeklyVWAP.Values[2][0];

            if (double.IsNaN(vwap) || double.IsNaN(upper1) || double.IsNaN(lower1))
                return;

            double barDelta = deltaInd.DeltaSeries[0];

            double barRange = High[0] - Low[0];
            double atrVal   = atr[0];

            if (double.IsNaN(atrVal))
                return;

            bool touchUpper = Low[0] <= upper1 && High[0] >= upper1;
            bool touchLower = Low[0] <= lower1 && High[0] >= lower1;
            bool rangeOKReb = barRange >= ATRmultRebote * atrVal;
            bool deltaFlip = Math.Sign(barDelta) != Math.Sign(deltaInd.DeltaSeries[1]) &&
                             Math.Abs(barDelta) >= DeltaThreshold;

            if ((touchUpper || touchLower) && rangeOKReb && deltaFlip &&
                Close[0] > lower1 && Close[0] < upper1)
            {
                Direction dir = touchUpper ? Direction.Short : Direction.Long;
                EnterTrade("REBOTE", dir);
            }

            bool closeOutUp = Close[0] > upper1 && (Close[0] - upper1) >= 0.25 * barRange;
            bool closeOutDown = Close[0] < lower1 && (lower1 - Close[0]) >= 0.25 * barRange;
            bool rangeOKBreak = barRange >= ATRmultRuptura * atrVal;
            bool deltaStrong = Math.Abs(barDelta) >= DeltaThreshold &&
                               Math.Sign(barDelta) == Math.Sign(deltaInd.DeltaSeries[1]);

            if ((closeOutUp || closeOutDown) && rangeOKBreak && deltaStrong)
            {
                Direction dir = closeOutUp ? Direction.Long : Direction.Short;
                EnterTrade("RUPTURA", dir);
            }
        }

        private void EnterTrade(string tag, Direction dir)
        {
            if (Position.MarketPosition != MarketPosition.Flat)
                return;

            if (dir == Direction.Long)
                EnterLong(tag);
            else
                EnterShort(tag);

            double stopPrice = dir == Direction.Long
                ? Low[0] - StopTicks * TickSize
                : High[0] + StopTicks * TickSize;
            SetStopLoss(tag, CalculationMode.Price, stopPrice, false);
        }

        private enum Direction { Long, Short }
    }
}
