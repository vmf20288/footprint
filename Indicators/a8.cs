// a8.cs  —  Delta + Absorption detector  (rev-2 – compile fixes)
// ------------------------------------------------------------
// • Keeps original delta2/volume rendering from a6 intact.
// • Adds real-time (Tick Replay) Absorption detection and dots.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using System.Windows.Media;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;   // <- Draw.Dot()
using SharpDX;
using SharpDX.DirectWrite;

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a8 : Indicator
    {
        // -----------------------------------------------------------------
        // === 1. Campos heredados de a6 (delta & volume) ==================
        private double lastTradePrice = 0.0;
        private int    lastDirection  = 0;
        private Dictionary<int,double> delta2;   // tick-rule delta por barra
        private Dictionary<int,double> volume;   // volumen total por barra

        // -----------------------------------------------------------------
        // === 2. Absorption fields =========================================
        private class VolBucket { public double Bid; public double Ask; }
        private Dictionary<double,VolBucket> priceMap;  // por precio dentro vela
        private Queue<double> baselineBuf;              // últimas M barras
        private double baseline;
        private double currentBid = double.NaN;
        private double currentAsk = double.NaN;

        // Timestamp para evitar spam de señales dentro de la misma barra
        private int    lastSignalBar = int.MinValue;

        // -----------------------------------------------------------------
        // === 3. Gráficos (heredados) =====================================
        private SharpDX.Direct2D1.SolidColorBrush brushGeneral;
        private SharpDX.Direct2D1.SolidColorBrush brushVolumeWhite;
        private TextFormat textFormat;
        private bool lastBackgroundWhite;
        private float rectHeight = 30f;
        private float bottomMargin = 50f;

        // -----------------------------------------------------------------
        // === 4. PROPIEDADES públicas =====================================
        [NinjaScriptProperty]
        [Display(Name = "Font Size", Order = 0, GroupName = "Panel A6")]
        public int FontSizeProp { get; set; } = 12;

        [NinjaScriptProperty]
        [Display(Name = "Background White", Order = 1, GroupName = "Panel A6")]
        public bool BackgroundWhite { get; set; } = false;

        // --- Absorption params -------------------------------------------
        [NinjaScriptProperty]
        [Display(Name="Absorption M promedio últimas barras", Order=10, GroupName="Absorption")]
        [Range(2, 999)]
        public int AbsM { get; set; } = 24;

        [NinjaScriptProperty]
        [Display(Name="Absorption K umbral Vol veces más M", Order=11, GroupName="Absorption")]
        [Range(1.0, 10.0)]
        public double AbsK { get; set; } = 3.0;

        [NinjaScriptProperty]
        [Display(Name="UsarConfirmaciónDeltaFootprint", Order=12, GroupName="Absorption")]
        public bool UseDeltaConfirm { get; set; } = true;

        [NinjaScriptProperty]
        [Display(Name="ClusterSpanTicks", Order=13, GroupName="Absorption")]
        [Range(1, 10)]
        public int ClusterSpanTicks { get; set; } = 2;

        [Browsable(false)] [XmlIgnore]
        public Series<double> AbsorptionSeries { get; private set; }

        // -----------------------------------------------------------------
        // === 5. Estados ===================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name          = "a8";
                Description   = "Delta + Absorption detector (Bid/Ask)";
                Calculate     = Calculate.OnEachTick;
                IsOverlay     = true;
                DrawOnPricePanel = false;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
            }
            else if (State == State.Configure)
            {
                // Enforce Tick Replay
                if (!Bars.IsTickReplay)
                    throw new Exception("Cargar el gráfico con Tick Replay activado para usar a8");

                delta2       = new Dictionary<int,double>();
                volume       = new Dictionary<int,double>();

                priceMap     = new Dictionary<double,VolBucket>();
                baselineBuf  = new Queue<double>();

                AbsorptionSeries = new Series<double>(this, MaximumBarsLookBack.Infinite);
            }
            else if (State == State.DataLoaded)
            {
                BuildBrushes();
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSizeProp);
                lastBackgroundWhite = BackgroundWhite;
            }
            else if (State == State.Terminated)
            {
                DisposeGraphics();
            }
        }

        private void DisposeGraphics()
        {
            brushGeneral?.Dispose();
            brushVolumeWhite?.Dispose();
            textFormat?.Dispose();
        }

        private void BuildBrushes()
        {
            brushGeneral?.Dispose();
            brushVolumeWhite?.Dispose();

            Color4 cText = BackgroundWhite ? new Color4(0, 0, 0, 1f)
                                           : new Color4(1, 1, 1, 1f);
            Color4 cVol  = new Color4(1, 1, 1, 1f);

            if (RenderTarget != null)
            {
                brushGeneral     = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, cText);
                brushVolumeWhite = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, cVol);
            }
        }

        // -----------------------------------------------------------------
        // === 6. MarketData ===============================================
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0)
                return;

            if (e.MarketDataType == MarketDataType.Bid)
            {
                currentBid = e.Price; return;
            }
            if (e.MarketDataType == MarketDataType.Ask)
            {
                currentAsk = e.Price; return;
            }
            if (e.MarketDataType != MarketDataType.Last)
                return;

            int    barIdx = CurrentBar;
            double price  = e.Price;
            double vol    = e.Volume;

            // ---------- Delta & volume original (a6) ---------------------
            if (!delta2.ContainsKey(barIdx))
            {
                delta2[barIdx] = volume[barIdx] = 0.0;
            }
            double sign;
            if (!double.IsNaN(currentAsk) && !double.IsNaN(currentBid))
            {
                sign = price >= currentAsk - TickSize*0.1 ? 1 : price <= currentBid + TickSize*0.1 ? -1 : 0;
            }
            else // fallback tick‑rule
            {
                if (price > lastTradePrice)      sign = 1;
                else if (price < lastTradePrice) sign = -1;
                else                             sign = lastDirection;
            }
            delta2[barIdx] += vol * sign;
            if (sign != 0)
                lastDirection = sign > 0 ? 1 : -1;
            lastTradePrice = price;
            volume[barIdx] += vol;

            // ---------- Absorption bucket --------------------------------
            if (!priceMap.TryGetValue(price, out var bk))
                bk = priceMap[price] = new VolBucket();
            if (sign >= 0) bk.Ask += vol; else bk.Bid += vol;

            DetectAbsorption(e.Time);   // pasa timestamp
        }

        // -----------------------------------------------------------------
        // === 7. Detección de Absorption ==================================
        private void DetectAbsorption(DateTime tickTime)
        {
            // Evita recalcular si ya señalamos en esta barra
            if (CurrentBar == lastSignalBar) return;

            double span = ClusterSpanTicks * TickSize;
            double clusterBid = 0.0, clusterAsk = 0.0;
            double clusterLow = double.MaxValue, clusterHigh = double.MinValue;

            foreach (var kvp in priceMap)
            {
                double p = kvp.Key;
                var    v = kvp.Value;
                // crea un único cluster alrededor del precio actual (simplificación)
                if (Math.Abs(p - lastTradePrice) <= span)
                {
                    clusterBid  += v.Bid;
                    clusterAsk  += v.Ask;
                    clusterLow   = Math.Min(clusterLow, p);
                    clusterHigh  = Math.Max(clusterHigh, p);
                }
            }

            if (clusterBid == 0 || clusterAsk == 0) return;

            bool heavyBid = clusterBid >= AbsK * baseline;
            bool heavyAsk = clusterAsk >= AbsK * baseline;
            if (!(heavyBid && heavyAsk)) return;

            double ratio = clusterBid / clusterAsk;
            if (ratio < 0.8 || ratio > 1.25) return;

            // Dirección tentativa: si precio actual rebota hacia arriba → long (buy absorbe sell)
            OrderDir dir = signOfFootprint() > 0 ? OrderDir.Long : OrderDir.Short;

            // Confirmación Delta si se requiere
            bool deltaOk = true;
            if (UseDeltaConfirm)
            {
                double fpDelta = delta2.ContainsKey(CurrentBar) ? delta2[CurrentBar] : 0;
                deltaOk = dir == OrderDir.Long ? fpDelta > 0 : fpDelta < 0;
            }
            AbsState st = deltaOk ? AbsState.Confirmed : AbsState.Potential;
            TriggerDot(st, dir, (clusterLow+clusterHigh)/2.0);
            lastSignalBar = CurrentBar;
        }

        private int signOfFootprint()
        {
            double fpDelta = delta2.ContainsKey(CurrentBar) ? delta2[CurrentBar] : 0;
            return fpDelta > 0 ? 1 : fpDelta < 0 ? -1 : 0;
        }

        // -----------------------------------------------------------------
        // === 8. Dot + alert ==============================================
        private void TriggerDot(AbsState st, OrderDir dir, double price)
        {
            string tag = st==AbsState.Confirmed ? "ABS_C_"+CurrentBar : "ABS_P_"+CurrentBar;
            Brush  col = st==AbsState.Potential ? Brushes.Gray : dir==OrderDir.Long ? Brushes.Lime : Brushes.Red;

            // Reemplaza dot potencial si es que existía
            if (st == AbsState.Confirmed)
                RemoveDrawObject("ABS_P_"+CurrentBar);

            Draw.Dot(this, tag, false, 0, price, col);
            Alert(tag, Priority.Medium,
                  $"Absorption {(dir==OrderDir.Long?"LONG":"SHORT")} @ {price:F2}",
                  "Alert1.wav", 10, Brushes.White, Brushes.Black);

            AbsorptionSeries[0] = st==AbsState.Confirmed ? (dir==OrderDir.Long?1:-1) : 0.5;
        }

        private enum AbsState { Potential, Confirmed }
        private enum OrderDir { Long, Short }

        // -----------------------------------------------------------------
        // === 9. OnBarUpdate (cierra vela) =================================
        protected override void OnBarUpdate()
        {
            if (CurrentBar < 2) return;

            // baseline update
            double fpVol = 0.0;
            foreach (var v in priceMap.Values) fpVol += v.Bid + v.Ask;
            baselineBuf.Enqueue(fpVol);
            if (baselineBuf.Count > AbsM) baselineBuf.Dequeue();
            baseline = 0.0; foreach (double v in baselineBuf) baseline += v; baseline /= baselineBuf.Count;

            priceMap.Clear();
            AbsorptionSeries[0] = 0;   // reset plot for new bar
        }

        // -----------------------------------------------------------------
        // === 10. Render original (delta + volume) =========================
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (ChartBars == null || RenderTarget == null)
                return;

            if (lastBackgroundWhite != BackgroundWhite)
            {
                BuildBrushes();
                lastBackgroundWhite = BackgroundWhite;
            }

            if (Math.Abs(textFormat.FontSize - FontSizeProp) > 0.1f)
            {
                textFormat.Dispose();
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSizeProp);
            }

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;
            float barWidth = (float)chartControl.GetBarPaintWidth(ChartBars);

            float yBottom = (float)chartScale.GetYByValue(chartScale.MinValue);
            const int rowCount = 2; // delta + volume
            float yTop    = yBottom - bottomMargin - rectHeight * rowCount;

            double max2 = 0.0;
            for (int i = firstBar; i <= lastBar; i++)
            {
                if (delta2.ContainsKey(i)) max2 = Math.Max(max2, Math.Abs(delta2[i]));
            }
            if (max2.Equals(0.0)) max2 = 1.0;

            int deltaFontSize = FontSizeProp + 8;
            using (var deltaFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", deltaFontSize))
            {
                for (int i = firstBar; i <= lastBar; i++)
                {
                    float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                    float xLeft   = xCenter - barWidth / 2f;

                    DrawCell(delta2, i, max2, xLeft, yTop,             barWidth, rectHeight, deltaFormat, brushGeneral);
                    DrawVolumeCell(i, xLeft, yTop + rectHeight,     barWidth, rectHeight, deltaFormat);
                }
            }
        }

        private void DrawCell(Dictionary<int, double> src, int barIndex, double maxAbs, float xLeft, float yTop, float width, float height, TextFormat fmt, SharpDX.Direct2D1.Brush textBrush)
        {
            double value = src.ContainsKey(barIndex) ? src[barIndex] : 0.0;
            float intensity = (float)(Math.Abs(value) / maxAbs);
            intensity = Math.Max(0.2f, Math.Min(1f, intensity));

            Color4 fillColor = value >= 0
                ? new Color4(0f, intensity, 0f, intensity)
                : new Color4(intensity, 0f, 0f, intensity);

            var rect = new RectangleF(xLeft, yTop, width, height);
            using (var fillBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, fillColor))
                RenderTarget.FillRectangle(rect, fillBrush);

            RenderTarget.DrawRectangle(rect, brushGeneral, 1f);

            string txt = value.ToString("0");
            using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, txt, fmt, width, height))
            {
                var m = layout.Metrics;
                float tx = xLeft + (width  - m.Width)  / 2f;
                float ty = yTop  + (height - m.Height) / 2f;
                RenderTarget.DrawTextLayout(new Vector2(tx, ty), layout, textBrush);
            }
        }

        private void DrawVolumeCell(int barIndex, float xLeft, float yTop, float width, float height, TextFormat fmt)
        {
            double vol = volume.ContainsKey(barIndex) ? volume[barIndex] : 0.0;
            var rect = new RectangleF(xLeft, yTop, width, height);
            RenderTarget.DrawRectangle(rect, brushGeneral, 1f);
            string txt = vol.ToString("0");
            using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, txt, fmt, width, height))
            {
                var m = layout.Metrics;
                float tx = xLeft + (width  - m.Width)  / 2f;
                float ty = yTop  + (height - m.Height) / 2f;
                RenderTarget.DrawTextLayout(new Vector2(tx, ty), layout, brushVolumeWhite);
            }
        }
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
{
private a8[] cachea8;
public a8 a8(int fontSizeProp, bool backgroundWhite, int absM, double absK, bool useDeltaConfirm, int clusterSpanTicks)
{
return a8(Input, fontSizeProp, backgroundWhite, absM, absK, useDeltaConfirm, clusterSpanTicks);
}

public a8 a8(ISeries<double> input, int fontSizeProp, bool backgroundWhite, int absM, double absK, bool useDeltaConfirm, int clusterSpanTicks)
{
if (cachea8 != null)
for (int idx = 0; idx < cachea8.Length; idx++)
if (cachea8[idx] != null && cachea8[idx].FontSizeProp == fontSizeProp && cachea8[idx].BackgroundWhite == backgroundWhite && cachea8[idx].AbsM == absM && cachea8[idx].AbsK == absK && cachea8[idx].UseDeltaConfirm == useDeltaConfirm && cachea8[idx].ClusterSpanTicks == clusterSpanTicks && cachea8[idx].EqualsInput(input))
return cachea8[idx];
return CacheIndicator<a8>(new a8(){ FontSizeProp = fontSizeProp, BackgroundWhite = backgroundWhite, AbsM = absM, AbsK = absK, UseDeltaConfirm = useDeltaConfirm, ClusterSpanTicks = clusterSpanTicks }, input, ref cachea8);
}
}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
{
public Indicators.a8 a8(int fontSizeProp, bool backgroundWhite, int absM, double absK, bool useDeltaConfirm, int clusterSpanTicks)
{
return indicator.a8(Input, fontSizeProp, backgroundWhite, absM, absK, useDeltaConfirm, clusterSpanTicks);
}

public Indicators.a8 a8(ISeries<double> input , int fontSizeProp, bool backgroundWhite, int absM, double absK, bool useDeltaConfirm, int clusterSpanTicks)
{
return indicator.a8(input, fontSizeProp, backgroundWhite, absM, absK, useDeltaConfirm, clusterSpanTicks);
}
}
}

namespace NinjaTrader.NinjaScript.Strategies
{
public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
{
public Indicators.a8 a8(int fontSizeProp, bool backgroundWhite, int absM, double absK, bool useDeltaConfirm, int clusterSpanTicks)
{
return indicator.a8(Input, fontSizeProp, backgroundWhite, absM, absK, useDeltaConfirm, clusterSpanTicks);
}

public Indicators.a8 a8(ISeries<double> input , int fontSizeProp, bool backgroundWhite, int absM, double absK, bool useDeltaConfirm, int clusterSpanTicks)
{
return indicator.a8(input, fontSizeProp, backgroundWhite, absM, absK, useDeltaConfirm, clusterSpanTicks);
}
}
}

#endregion
