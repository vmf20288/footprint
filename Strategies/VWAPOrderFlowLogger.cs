// -----------------------------------------------------------------------------
//  VWAPOrderFlowLogger.cs  –  v1.1-debug2  (OnPriceChange + prints siempre)
// -----------------------------------------------------------------------------

#region Usings
using System;
using System.IO;
using System.Linq;
using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class VWAPOrderFlowLogger : Strategy
    {
        //──────── PARAMS – VWAP
        [NinjaScriptProperty, Display(Name="ActiveBands", Order=0, GroupName="VWAP")]
        public string ActiveBands { get; set; } = "VWAP,+1σ,-1σ";

        [NinjaScriptProperty, Display(Name="Mode (Weekly | Anchored)", Order=1, GroupName="VWAP")]
        public string Mode { get; set; } = "Anchored";

        [NinjaScriptProperty, Display(Name="ProximityTicks", Order=2, GroupName="VWAP")]
        public int ProximityTicks { get; set; } = 4;

        [NinjaScriptProperty, Display(Name="MinReactionTicks", Order=3, GroupName="VWAP")]
        public int MinReactionTicks { get; set; } = 6;

        //──────── PARAMS – Order-Flow
        [NinjaScriptProperty, Display(Name="BigPrintContracts", Order=4, GroupName="Order-Flow")]
        public int BigPrintContracts { get; set; } = 20;

        [NinjaScriptProperty, Display(Name="ImbalanceThreshold (%)", Order=5, GroupName="Order-Flow")]
        public int ImbalanceThreshold { get; set; } = 300;

        [NinjaScriptProperty, Display(Name="MinBigPrints", Order=6, GroupName="Order-Flow")]
        public int MinBigPrints { get; set; } = 3;

        //──────── PARAMS – Export
        [NinjaScriptProperty, Display(Name="CsvFileName", Order=99, GroupName="Export")]
        public string CsvFileName { get; set; } = "VWAP_orderflow_log.csv";

        //──────── PRIVADOS
        private const int AnchorBars = 10;          // ← depuración rápida
        private double vwap, sigma;
        private double lastLogged = -1;
        private string lastBand   = string.Empty;

        private double bidVolBar, askVolBar;
        private int bigPrintsBar;
        private long deltaBar;
        private double lastPrice = double.NaN;

        private StreamWriter sw;
        private readonly string header =
            "Time,Instrument,Band,Label,Confirm,ImbalancePct,BigPrints,Delta,ReactionTicks";

        private string[] bandsActive;

        //──────────────────────────────────────────────
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name      = "VWAPOrderFlowLogger";
                Calculate = Calculate.OnPriceChange;   // ← clave
                IsOverlay = true;
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1); // Tick Replay requerido si quieres histórico
            }
            else if (State == State.DataLoaded)
            {
                bandsActive = ActiveBands.Replace(" ", "").Split(',');
                InitializeCsv();
            }
            else if (State == State.Terminated)
            {
                sw?.Flush();
                sw?.Dispose();
            }
        }

        //──────────────────────────────────────────────
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last) return;

            if (double.IsNaN(lastPrice)) lastPrice = e.Price;

            bool isBuy;
            if (e.Price > lastPrice)      isBuy = true;
            else if (e.Price < lastPrice) isBuy = false;
            else                          isBuy = deltaBar >= 0;

            if (isBuy) askVolBar += e.Volume;
            else       bidVolBar += e.Volume;

            deltaBar += isBuy ? e.Volume : -e.Volume;
            if (e.Volume >= BigPrintContracts) bigPrintsBar++;

            lastPrice = e.Price;

            Print($"DBG TICK {Time[0]:HH:mm:ss.fff}  bid={bidVolBar} ask={askVolBar} BP={bigPrintsBar}");
        }

        //──────────────────────────────────────────────
        protected override void OnBarUpdate()
        {
            // Imprime SIEMPRE que llegue una actualización de precio
            Print($"DBG BAR  {Time[0]:HH:mm:ss}  BiP={BarsInProgress} Close={Close[0]}");

            if (BarsInProgress != 0) return;
            if (CurrentBar < AnchorBars) return;

            CalcVWAPandSigma();

            string touchedBand;
            if (!IsFirstTouch(out touchedBand))
            { ResetAccumulators(); return; }

            double reactionTicks = Math.Abs(Close[0] - BandPrice(touchedBand)) / TickSize;
            double imbalancePct  = bidVolBar == 0 ? 0 :
                                   (askVolBar / bidVolBar) * 100.0;
            string label         = Close[0] > BandPrice(touchedBand) ? "Break" : "Bounce";

            bool confirm =
                imbalancePct >= ImbalanceThreshold &&
                bigPrintsBar >= MinBigPrints        &&
                reactionTicks >= MinReactionTicks   &&
                ((label == "Break"  && deltaBar > 0) ||
                 (label == "Bounce" && deltaBar < 0));

            string flag = confirm ? "1" : "0";

            sw.WriteLine($"{Time[0]:yyyy-MM-dd HH:mm:ss},{Instrument.FullName}," +
                         $"{touchedBand},{label},{flag},{imbalancePct:0}," +
                         $"{bigPrintsBar},{deltaBar},{reactionTicks:0}");
            sw.Flush();
            lastLogged = Time[0].ToOADate();
            lastBand   = touchedBand;

            Draw.Dot(this, $"VW{CurrentBar}", true, 0, Close[0],
                     confirm
                     ? (label == "Break" ? Brushes.Lime : Brushes.Red)
                     : Brushes.Gray);

            ResetAccumulators();
        }

        //──────────────────────────────────────────────
        private void CalcVWAPandSigma()
        {
            double sumPV=0, sumV=0, sumVarPV=0;
            int bars = Mode.Equals("Weekly", StringComparison.OrdinalIgnoreCase)
                     ? AnchorBars * 2 : AnchorBars;

            for (int i=0;i<bars;i++){ sumPV+=Close[i]*Volume[i]; sumV+=Volume[i]; }
            vwap = sumV==0? Close[0]: sumPV/sumV;

            for (int i=0;i<bars;i++)
                sumVarPV += Volume[i]*Math.Pow(Close[i]-vwap,2);
            sigma = sumV==0?0:Math.Sqrt(sumVarPV/sumV);
        }

        private double BandPrice(string band) => band switch
        {
            "+1σ" => vwap + sigma,
            "+2σ" => vwap + 2*sigma,
            "-1σ" => vwap - sigma,
            "-2σ" => vwap - 2*sigma,
            _     => vwap
        };

        private bool IsFirstTouch(out string touchedBand)
        {
            touchedBand = null;
            foreach (var b in bandsActive)
                if (Math.Abs(Close[0]-BandPrice(b))<=TickSize*ProximityTicks)
                { touchedBand=b; break; }
            if (touchedBand==null) return false;
            if (Time[0].ToOADate()==lastLogged && touchedBand==lastBand) return false;
            return true;
        }

        private void ResetAccumulators()
        {
            bidVolBar=askVolBar=0;
            bigPrintsBar=0;
            deltaBar=0;
        }

        private void InitializeCsv()
        {
            string path=Path.Combine(Core.Globals.UserDataDir,CsvFileName);
            var fs=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,
                                  FileShare.ReadWrite|FileShare.Delete);
            fs.Seek(0,SeekOrigin.End);
            sw=new StreamWriter(fs,Encoding.UTF8);
            if(fs.Length==0) sw.WriteLine(header);
            Print("CSV path: "+path);
        }
    }
}
