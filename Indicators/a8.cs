// a8.cs - Simple delta display indicator for NinjaTrader 8.1
// -----------------------------------------------------------
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.DirectWrite;
using System.Windows.Media;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class a8 : Indicator
    {
        private Dictionary<int, double> deltaPerBar;
        private double bestBid = double.NaN;
        private double bestAsk = double.NaN;
        private double lastPrice = double.NaN;
        private int    lastDirection;

        private SharpDX.Direct2D1.SolidColorBrush textBrush;
        private TextFormat textFormat;

        [NinjaScriptProperty]
        [Display(Name = "Font Size", Order = 0, GroupName = "Parameters")]
        public int FontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Lookback Bars", Order = 1, GroupName = "Parameters")]
        [Range(1, int.MaxValue)]
        public int Lookback { get; set; }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                    = "a8";
                Description             = "Per-bar delta display with coloured box";
                Calculate               = Calculate.OnEachTick;
                IsOverlay               = true;
                DisplayInDataBox        = false;
                DrawOnPricePanel        = false;
                DrawHorizontalGridLines = false;
                DrawVerticalGridLines   = false;
                PaintPriceMarkers       = false;
                FontSize                = 36;
                Lookback               = 500;
                BarsRequiredToPlot      = 0;
                AddPlot(Brushes.Transparent, "Delta");
            }
            else if (State == State.Configure)
            {
                deltaPerBar = new Dictionary<int, double>();
                lastDirection = 0;
            }
            else if (State == State.DataLoaded)
            {
                textBrush  = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(1f,1f,1f,1f));
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSize);
            }
            else if (State == State.Terminated)
            {
                textBrush?.Dispose();
                textFormat?.Dispose();
            }
        }

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (e.Position != 0) return;

            if (e.MarketDataType == MarketDataType.Bid)
                bestBid = e.Price;
            else if (e.MarketDataType == MarketDataType.Ask)
                bestAsk = e.Price;
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (BarsInProgress != 0)
                return;

            int bar = CurrentBar;
            if (!deltaPerBar.ContainsKey(bar))
                deltaPerBar[bar] = 0.0;

            switch (e.MarketDataType)
            {
                case MarketDataType.Ask:
                    deltaPerBar[bar] += e.Volume;
                    lastDirection = 1;
                    lastPrice = e.Price;
                    break;
                case MarketDataType.Bid:
                    deltaPerBar[bar] -= e.Volume;
                    lastDirection = -1;
                    lastPrice = e.Price;
                    break;
                case MarketDataType.Last:
                    int dir = 0;
                    if (!double.IsNaN(bestBid) && !double.IsNaN(bestAsk))
                    {
                        if (e.Price >= bestAsk) dir = 1;
                        else if (e.Price <= bestBid) dir = -1;
                    }

                    if (dir == 0 && !double.IsNaN(lastPrice))
                    {
                        if (e.Price > lastPrice) dir = 1;
                        else if (e.Price < lastPrice) dir = -1;
                        else dir = lastDirection;
                    }

                    if (dir != 0)
                    {
                        deltaPerBar[bar] += e.Volume * dir;
                        lastDirection = dir;
                    }
                    lastPrice = e.Price;
                    break;
            }

            Values[0][0] = deltaPerBar[bar];
        }

        protected override void OnBarUpdate()
        {
            if (!deltaPerBar.ContainsKey(CurrentBar))
                deltaPerBar[CurrentBar] = 0.0;

            Values[0][0] = deltaPerBar[CurrentBar];

            int prune = CurrentBar - Lookback - 1;
            if (prune >= 0)
                deltaPerBar.Remove(prune);
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (ChartBars == null || RenderTarget == null)
                return;

            if (Math.Abs(textFormat.FontSize - FontSize) > 0.1f)
            {
                textFormat.Dispose();
                textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSize);
            }

            int firstBar = ChartBars.FromIndex;
            int lastBar  = ChartBars.ToIndex;
            float barWidth = (float)chartControl.GetBarPaintWidth(ChartBars);

            float rectHeight   = 72f;
            float bottomMargin = 90f;
            float yBottom      = (float)chartScale.GetYByValue(chartScale.MinValue);
            float yTop         = yBottom - bottomMargin - rectHeight;

            double maxAbs = 0.0;
            for (int i = firstBar; i <= lastBar; i++)
            {
                if (deltaPerBar.ContainsKey(i))
                    maxAbs = Math.Max(maxAbs, Math.Abs(deltaPerBar[i]));
            }
            if (maxAbs.Equals(0.0)) maxAbs = 1.0;

            for (int i = firstBar; i <= lastBar; i++)
            {
                float xCenter = chartControl.GetXByBarIndex(ChartBars, i);
                float xLeft   = xCenter - barWidth / 2f;
                double value  = deltaPerBar.ContainsKey(i) ? deltaPerBar[i] : 0.0;
                float intensity = (float)(Math.Abs(value) / maxAbs);
                intensity = Math.Max(0.2f, Math.Min(1f, intensity));

                Color4 color = value >= 0 ? new Color4(0f, intensity, 0f, intensity)
                                          : new Color4(intensity, 0f, 0f, intensity);

                var rect = new RectangleF(xLeft, yTop, barWidth, rectHeight);
                using (var fillBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, color))
                    RenderTarget.FillRectangle(rect, fillBrush);

                RenderTarget.DrawRectangle(rect, textBrush, 1f);

                string txt = value.ToString("0");
                using (var layout = new TextLayout(Core.Globals.DirectWriteFactory, txt, textFormat, rect.Width, rect.Height))
                {
                    var m = layout.Metrics;
                    float tx = xLeft + (rect.Width  - m.Width)  / 2f;
                    float ty = yTop  + (rect.Height - m.Height) / 2f;
                    RenderTarget.DrawTextLayout(new Vector2(tx, ty), layout, textBrush);
                }
            }
        }

        [Browsable(false)]
        [XmlIgnore]
        public Series<double> Delta => Values[0];
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
        public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
        {
                private a8[] cachea8;
                public a8 a8(int fontSize, int lookback)
                {
                        return a8(Input, fontSize, lookback);
                }

                public a8 a8(ISeries<double> input, int fontSize, int lookback)
                {
                        if (cachea8 != null)
                                for (int idx = 0; idx < cachea8.Length; idx++)
                                        if (cachea8[idx] != null && cachea8[idx].FontSize == fontSize && cachea8[idx].Lookback == lookback && cachea8[idx].EqualsInput(input))
                                                return cachea8[idx];
                        return CacheIndicator<a8>(new a8(){ FontSize = fontSize, Lookback = lookback }, input, ref cachea8);
                }
        }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
        public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
        {
                public Indicators.a8 a8(int fontSize, int lookback)
                {
                        return indicator.a8(Input, fontSize, lookback);
                }

                public Indicators.a8 a8(ISeries<double> input , int fontSize, int lookback)
                {
                        return indicator.a8(input, fontSize, lookback);
                }
        }
}

namespace NinjaTrader.NinjaScript.Strategies
{
        public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
        {
                public Indicators.a8 a8(int fontSize, int lookback)
                {
                        return indicator.a8(Input, fontSize, lookback);
                }

                public Indicators.a8 a8(ISeries<double> input , int fontSize, int lookback)
                {
                        return indicator.a8(input, fontSize, lookback);
                }
        }
}

#endregion
