// -----------------------------------------------------------------------------
//  A15_BigOrders.cs   – NinjaTrader 8.1 indicator (NQ version)
//  v3   2025-06-01
// -----------------------------------------------------------------------------
//  Objetivo
//  --------
//  • Detectar bloques grandes (big‑prints) ≥ QtyMinContracts  (default 10 NQ)
//  • Detectar clusters (mismo precio, ≤ 0.5 s)  – opcional
//  • Detectar icebergs  – opcional
//  • Dibujar tres secciones de cabecera sobre el gráfico con cuadros acumulativos:
//       Big‑Print  |  Cluster  |  Iceberg   (cada uno Bid / Ask)
//  • Pintar los números en rojo (Ask) / lime (Bid)  y los cuadros en gris.
//  • Marcar sobre la vela cada print individual con el número negro en negrita.
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
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class A15_BigOrders : Indicator
    {
        // ----------------------  Parametrización del usuario -----------------
        [Display(Name = "Qty mínimo contratos (NQ)", GroupName = "Parameters", Order = 0)]
        [Range(1, int.MaxValue)]
        public int QtyMinContracts { get; set; } = 10;

        [Display(Name = "Mostrar clusters", GroupName = "Parameters", Order = 1)]
        public bool ShowClusters { get; set; } = false;

        [Display(Name = "Mostrar icebergs", GroupName = "Parameters", Order = 2)]
        public bool ShowIcebergs { get; set; } = false;

        // ----------------------  Constantes internas (NQ) --------------------
        private const int    KeepBars        = 120;
        private const int    CleanInterval   = 20;
        private const double ClusterWindowMs = 500;
        private const int    MinVisibleQty   = 10;
        private const int    IcebergFactor   = 3;
        private const int    IcebergMinExec  = 50;
        private const int    IceWindowSec    = 30;

        // ----------------------  Series públicas -----------------------------
        [Browsable(false)] public Series<int>    BigSignal { get; private set; }
        [Browsable(false)] public Series<double> BigPrice  { get; private set; }
        [Browsable(false)] public Series<int>    BigVolume { get; private set; }
        [Browsable(false)] public Series<int>    HiddenSize{ get; private set; }

        // ----------------------  Acumuladores por vela -----------------------
        private long bpBid, bpAsk;    // big-print
        private long clBid, clAsk;    // cluster
        private long icBid, icAsk;    // iceberg

        // ----------------------  Estructuras internas ------------------------
        private class ClusterInfo
        {
            public DateTime First;
            public long     Vol;
            public bool     IsAsk;
        }
        private class IceTrack
        {
            public DateTime Start;
            public long     Exec;
            public long     MaxVis;
            public bool     IsAsk;
        }
        private readonly Dictionary<double, ClusterInfo> clusters = new();
        private readonly Dictionary<double, IceTrack>    ice      = new();

        // ----------------------  State --------------------------------------
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name             = "A15_BigOrders (NQ)";
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

        // ----------------------  Reseteo por nueva vela ----------------------
        private int lastBar = -1;
        private void ResetPerBar()
        {
            bpBid = bpAsk = clBid = clAsk = icBid = icAsk = 0;
        }

        // ----------------------  Tick handler (prints) -----------------------
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last) return;
            if (BarsInProgress != 0) return;

            if (CurrentBar != lastBar) { ResetPerBar(); lastBar = CurrentBar; }

            bool isAsk = e.Price >= GetCurrentAsk() - TickSize*0.5;
            double price = e.Price;
            long vol = (long)e.Volume;

            // ----- clustering
            ClusterInfo ci;
            if (!clusters.TryGetValue(price, out ci) ||
                (e.Time - ci.First).TotalMilliseconds > ClusterWindowMs)
            {
                ci = new ClusterInfo { First = e.Time, Vol = vol, IsAsk = isAsk };
                clusters[price] = ci;
            }
            else ci.Vol += vol;

            bool isCluster = ci.Vol >= QtyMinContracts && (ci.Vol > vol);
            if (ci.Vol >= QtyMinContracts)
            {
                DrawEvent(ci.First, price, ci.Vol, isAsk, 1, isCluster);
                clusters.Remove(price);
            }
        }

        // ----------------------  Depth handler (iceberg) ---------------------
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (!ShowIcebergs) return;
            if (e.Operation == Operation.Remove || e.MarketDataType == MarketDataType.Last) return;
            bool isAsk = e.MarketDataType == MarketDataType.Ask;
            double price = e.Price;
            int size = (int)e.Volume;

            if (size >= MinVisibleQty && !ice.ContainsKey(price))
                ice[price] = new IceTrack { Start = Time[0], MaxVis = size, Exec = 0, IsAsk = isAsk };
            else if (ice.TryGetValue(price, out var t))
            {
                if (size > t.MaxVis) t.MaxVis = size;
                if ((Time[0] - t.Start).TotalSeconds > IceWindowSec) ice.Remove(price);
            }
        }

        private void UpdateIceExecuted(double price, long vol, bool isAsk)
        {
            if (!ShowIcebergs) return;
            if (!ice.TryGetValue(price, out var t)) return;
            if (t.IsAsk != isAsk) return;
            t.Exec += vol;
            if (t.Exec >= IcebergMinExec && t.Exec >= IcebergFactor * t.MaxVis)
            {
                DrawEvent(t.Start, price, t.Exec, isAsk, 2, false);
                ice.Remove(price);
            }
        }

        // ----------------------  Dibujo principal ----------------------------
        private void DrawEvent(DateTime tickTime, double price, long vol, bool isAsk, int kind, bool clusterBorder)
        {
            int barsAgo = CurrentBar - Bars.GetBar(tickTime);
            int barsAgo = Bars.GetBar(tickTime);
            if (barsAgo < 0) return;

            // Actualizar acumuladores por vela
            switch(kind)
            {
                case 1: if(isAsk) bpAsk += vol; else bpBid += vol; break;
                case 2: if(isAsk) icAsk += vol; else icBid += vol; break;
            }
            if(kind==1 && clusterBorder) { if(isAsk) clAsk += vol; else clBid += vol; }

            // Dibujar número sobre la vela en negro y centrado
            string lbl = vol.ToString();
            var font = new SimpleFont("Arial", 12) { Bold = true };
            Draw.Text(this, $"EV_{CurrentBar}_{price}_{Environment.TickCount}", false, lbl,
                barsAgo, price, 0, Brushes.Black, font, TextAlignment.Center,
                Brushes.Transparent, Brushes.Transparent, 0);
            // Dibujar número sobre la vela
            string lbl = vol.ToString();
            Brush txtClr = isAsk ? Brushes.Red : Brushes.Lime;
            Draw.Text(this, $"EV_{CurrentBar}_{price}_{Environment.TickCount}", lbl, barsAgo, price, txtClr);

            // Mandar señal a series
            BigSignal[0] = isAsk ? kind : -kind;
            BigPrice[0]  = price;
            BigVolume[0] = (int)vol;
            if(kind==2) HiddenSize[0] = (int)vol;

            UpdateIceExecuted(price, vol, isAsk);

            if (CurrentBar > KeepBars && CurrentBar % CleanInterval == 0)
                RemoveDrawObjects();
        }

        // ----------------------  Cabecera fija sobre gráfico -----------------
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (ChartBars == null || RenderTarget == null)
                return;

            const float offY = 10f;
            const float offX = 60f;
            const float boxW = 60f;
            const float boxH = 22f;

            using var fmt = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", 12f);
            using var brushBid = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.Lime);
            using var brushAsk = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.Red);
            using var brushLabel = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.Black);
            const float boxH = 16f;

            using var fmt = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", 9f);
            using var brushBid = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.Lime);
            using var brushAsk = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.Red);
            using var brushLabel = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.WhiteSmoke);
            using var fillBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.DimGray);
            using var borderBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, SharpDX.Color.Black);

            void DrawLabel(string text, float y)
            {
                using var layout = new TextLayout(Core.Globals.DirectWriteFactory, text, fmt, offX - 5f, boxH);
                RenderTarget.DrawTextLayout(new Vector2(5f, y + (boxH - layout.Metrics.Height)/2f), layout, brushLabel);
            }

            void DrawBox(long val, float y, SharpDX.Direct2D1.SolidColorBrush txtBrush)
            {
                var rect = new RectangleF(offX, y, boxW, boxH);
                RenderTarget.DrawRectangle(rect, borderBrush, 1f);
                if (val > 0)
                    RenderTarget.FillRectangle(rect, fillBrush);
                if (val > 0)
                {
                    using var layout = new TextLayout(Core.Globals.DirectWriteFactory, val.ToString(), fmt, boxW, boxH);
                    float tx = offX + (boxW - layout.Metrics.Width)/2f;
                    float ty = y + (boxH - layout.Metrics.Height)/2f;
                    RenderTarget.DrawTextLayout(new Vector2(tx, ty), layout, txtBrush);
                }
            }

            DrawLabel($"BID {QtyMinContracts}", offY + 0 * (boxH + 2));
            DrawLabel($"ASK {QtyMinContracts}", offY + 1 * (boxH + 2));
            DrawLabel("BID CLUSTER",  offY + 2 * (boxH + 2));
            DrawLabel("ASK CLUSTER",  offY + 3 * (boxH + 2));
            DrawLabel("BID ICEBERG",  offY + 4 * (boxH + 2));
            DrawLabel("ASK ICEBERG",  offY + 5 * (boxH + 2));

            DrawBox(bpBid, offY + 0 * (boxH + 2), brushBid);
            DrawBox(bpAsk, offY + 1 * (boxH + 2), brushAsk);
            DrawBox(ShowClusters ? clBid : 0, offY + 2 * (boxH + 2), brushBid);
            DrawBox(ShowClusters ? clAsk : 0, offY + 3 * (boxH + 2), brushAsk);
            DrawBox(ShowIcebergs ? icBid : 0, offY + 4 * (boxH + 2), brushBid);
            DrawBox(ShowIcebergs ? icAsk : 0, offY + 5 * (boxH + 2), brushAsk);
        }
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
