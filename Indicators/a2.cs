// -----------------------------------------------------------------------------
//  a2.cs – Order-Flow & Market-Depth logger for NinjaTrader 8
//  v0.5a – Completa código (se truncaban llaves) + BestBid/Ask Px & Sz + Delta agresor
// -----------------------------------------------------------------------------
//  Columnas CSV:
//  Time,Price,BestBidPx,BestBidSz,BestAskPx,BestAskSz,LastPrice,LastSize,Delta,LevelTag,IsTouch
// -----------------------------------------------------------------------------
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a2 : Indicator
    {
        // ───────── PARAMETERS ─────────
        [NinjaScriptProperty]
        [Display(Name = "Pre-Seconds", Order = 0, GroupName = "Recording")]
        public int PreSeconds { get; set; } = 5;

        [NinjaScriptProperty]
        [Display(Name = "Post-Seconds", Order = 1, GroupName = "Recording")]
        public int PostSeconds { get; set; } = 5;

        [NinjaScriptProperty]
        [Display(Name = "Tolerance (ticks)", Order = 2, GroupName = "Recording")]
        public int ToleranceTicks { get; set; } = 2;

        [NinjaScriptProperty]
        [Display(Name = "Log folder", Order = 3, GroupName = "Recording")]
        public string LogFolder { get; set; } = "Level2Logs";

        // ───────── STATE ─────────
        private a1 vwap;
        private a4 zones;

        private readonly Queue<DepthTick> preBuffer = new();
        private bool     isRecording;
        private DateTime recordUntil;
        private StreamWriter writer;

        // top-of-book caches
        private readonly SortedDictionary<double,long> bids = new(); // desc
        private readonly SortedDictionary<double,long> asks = new(); // asc

        private class DepthTick
        {
            public DateTime Time;
            public double Price;
            public double BestBidPx;
            public long   BestBidSz;
            public double BestAskPx;
            public long   BestAskSz;
            public double LastPrice;
            public long   LastSize;
            public long   Delta;
            public string LevelTag;
            public bool   IsTouch;
        }

        // ───────── LIFECYCLE ─────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "a2";
                Calculate = Calculate.OnEachTick;
                IsOverlay = false;
                IsSuspendedWhileInactive = true;
            }
            else if (State == State.Configure)
            {
                vwap  = a1(true, true, false, false, false, DateTime.Today, "00:00");
                zones = a4(0.21, 0.32, 0.13, 2, "1", 300, false);
            }
            else if (State == State.Terminated)
            {
                writer?.Close();
            }
        }

        // ───────── MARKET DEPTH ─────────
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            var dict = e.MarketDataType == MarketDataType.Bid ? bids : asks;
            switch (e.Operation)
            {
                case Operation.Add:
                case Operation.Update:
                    dict[e.Price] = e.Volume;
                    break;
                case Operation.Remove:
                    dict.Remove(e.Price);
                    break;
            }

            double bestBidPx = bids.Count > 0 ? bids.Last().Key  : 0;
            long   bestBidSz = bids.Count > 0 ? bids.Last().Value: 0;
            double bestAskPx = asks.Count > 0 ? asks.First().Key : 0;
            long   bestAskSz = asks.Count > 0 ? asks.First().Value: 0;

            RegisterTick(DateTime.Now, e.Price, bestBidPx, bestBidSz, bestAskPx, bestAskSz, 0, 0, 0);
        }

        // ───────── LAST TRADE ─────────
        protected override void OnMarketData(MarketDataEventArgs e)
        {
#if NT8_1
            if (e.UpdateType != MarketDataUpdateType.Last) return;
#else
            if (e.MarketDataType != MarketDataType.Last) return;
#endif
            double bestBidPx = bids.Count > 0 ? bids.Last().Key  : 0;
            long   bestBidSz = bids.Count > 0 ? bids.Last().Value: 0;
            double bestAskPx = asks.Count > 0 ? asks.First().Key : 0;
            long   bestAskSz = asks.Count > 0 ? asks.First().Value: 0;

            long delta = 0;
            if (bestAskPx > 0 && e.Price >= bestAskPx) delta =  e.Volume;
            else if (bestBidPx > 0 && e.Price <= bestBidPx) delta = -e.Volume;

            RegisterTick(e.Time, e.Price, bestBidPx, bestBidSz, bestAskPx, bestAskSz, e.Price, e.Volume, delta);
        }

        // ───────── CENTRAL LOGGER ─────────
        private void RegisterTick(DateTime time, double price, double bestBidPx, long bestBidSz, double bestAskPx, long bestAskSz, double lastPrice, long lastSize, long delta)
        {
            string lvlTag = CheckPriceAgainstLevels(price, out bool isTouch);

            if (lvlTag == null && !isRecording)
            {
                preBuffer.Enqueue(new DepthTick { Time = time, Price = price, BestBidPx = bestBidPx, BestBidSz = bestBidSz, BestAskPx = bestAskPx, BestAskSz = bestAskSz, LastPrice = lastPrice, LastSize = lastSize, Delta = delta, LevelTag = string.Empty, IsTouch = false });
                TrimBuffer(time);
                return;
            }

            if (lvlTag != null && !isRecording)
            {
                StartRecording(time);
                foreach (var t in preBuffer) WriteRow(t);
            }

            if (isRecording)
            {
                WriteRow(new DepthTick { Time = time, Price = price, BestBidPx = bestBidPx, BestBidSz = bestBidSz, BestAskPx = bestAskPx, BestAskSz = bestAskSz, LastPrice = lastPrice, LastSize = lastSize, Delta = delta, LevelTag = lvlTag ?? string.Empty, IsTouch = isTouch });

                if (time >= recordUntil) StopRecording();
            }
        }

        private void StartRecording(DateTime now)
        {
            isRecording = true;
            recordUntil = now.AddSeconds(PostSeconds);

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", LogFolder);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{Instrument.MasterInstrument.Name}_{now:yyyyMMdd}.csv");
            bool append = File.Exists(file);
            writer = new StreamWriter(file, append) { AutoFlush = true };
            if (!append)
                writer.WriteLine("Time,Price,BestBidPx,BestBidSz,BestAskPx,BestAskSz,LastPrice,LastSize,Delta,LevelTag,IsTouch");
        }

        private void StopRecording()
        {
            isRecording = false;
            preBuffer.Clear();
        }

        private void TrimBuffer(DateTime now)
        {
            while (preBuffer.Count > 0 && (now - preBuffer.Peek().Time).TotalSeconds > PreSeconds)
                preBuffer.Dequeue();
        }

        // ───────── UTILITIES ─────────
        private bool IsNear(double price, double level, double tickSize, out bool touch)
        {
            double diffTicks = Math.Abs(price - level) / tickSize;
            touch = diffTicks < 0.5;
            return diffTicks <= ToleranceTicks;
        }

        private string CheckPriceAgainstLevels(double price, out bool isTouch)
        {
            double tickSize = TickSize;
            isTouch = false;

            if (vwap != null)
            {
                double plus1 = vwap.Values[1][0];
                double minus1 = vwap.Values[2][0];
                if (IsNear(price, plus1, tickSize, out isTouch)) return "Weekly+1σ";
                if (IsNear(price, minus1, tickSize, out isTouch)) return "Weekly-1σ";
            }

            if (zones != null)
            {
                for (int i = 0; i < zones.GetZoneCount(); i++)
                    if (zones.TryGetZone(i, out bool _, out double area1, out double area2, out double area3, out double aoi, out int ds))
                    {
                        if (IsNear(price, aoi, tickSize, out isTouch))   return $"AOI_{ds}";
                        if (IsNear(price, area1, tickSize, out isTouch)) return $"Area1_{ds}";
                        if (IsNear(price, area2, tickSize, out isTouch)) return $"Area2_{ds}";
                        if (IsNear(price, area3, tickSize, out isTouch)) return $"Area3_{ds}";
                    }
            }
            return null;
        }

        // ───────── CSV WRITER ─────────
        private void WriteRow(DepthTick t)
        {
            writer?.WriteLine($"{t.Time:O},{t.Price},{t.BestBidPx},{t.BestBidSz},{t.BestAskPx},{t.BestAskSz},{t.LastPrice},{t.LastSize},{t.Delta},{t.LevelTag},{t.IsTouch}");
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private a2[] cachea2;
		public a2 a2(int preSeconds, int postSeconds, int toleranceTicks, string logFolder)
		{
			return a2(Input, preSeconds, postSeconds, toleranceTicks, logFolder);
		}

		public a2 a2(ISeries<double> input, int preSeconds, int postSeconds, int toleranceTicks, string logFolder)
		{
			if (cachea2 != null)
				for (int idx = 0; idx < cachea2.Length; idx++)
					if (cachea2[idx] != null && cachea2[idx].PreSeconds == preSeconds && cachea2[idx].PostSeconds == postSeconds && cachea2[idx].ToleranceTicks == toleranceTicks && cachea2[idx].LogFolder == logFolder && cachea2[idx].EqualsInput(input))
						return cachea2[idx];
			return CacheIndicator<a2>(new a2(){ PreSeconds = preSeconds, PostSeconds = postSeconds, ToleranceTicks = toleranceTicks, LogFolder = logFolder }, input, ref cachea2);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.a2 a2(int preSeconds, int postSeconds, int toleranceTicks, string logFolder)
		{
			return indicator.a2(Input, preSeconds, postSeconds, toleranceTicks, logFolder);
		}

		public Indicators.a2 a2(ISeries<double> input , int preSeconds, int postSeconds, int toleranceTicks, string logFolder)
		{
			return indicator.a2(input, preSeconds, postSeconds, toleranceTicks, logFolder);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.a2 a2(int preSeconds, int postSeconds, int toleranceTicks, string logFolder)
		{
			return indicator.a2(Input, preSeconds, postSeconds, toleranceTicks, logFolder);
		}

		public Indicators.a2 a2(ISeries<double> input , int preSeconds, int postSeconds, int toleranceTicks, string logFolder)
		{
			return indicator.a2(input, preSeconds, postSeconds, toleranceTicks, logFolder);
		}
	}
}

#endregion
