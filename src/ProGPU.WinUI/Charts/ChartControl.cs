using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Globalization;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Text;
using ProGPU.WinUI.Charts.Renderers;
using ProGPU.WinUI.Charts.Components;

namespace Microsoft.UI.Xaml.Controls
{
    using ProGPU.WinUI.Charts;

    public class ZoomRangeChangedEventArgs : EventArgs
    {
        public double Start { get; }
        public double End { get; }
        public object? SourceToken { get; }

        public ZoomRangeChangedEventArgs(double start, double end, object? sourceToken = null)
        {
            Start = start;
            End = end;
            SourceToken = sourceToken;
        }
    }

    public class CrosshairMovedEventArgs : EventArgs
    {
        public double? X { get; }
        public object? SourceToken { get; }

        public CrosshairMovedEventArgs(double? x, object? sourceToken = null)
        {
            X = x;
            SourceToken = sourceToken;
        }
    }

    public class ChartControl : Control
    {
        public static readonly DependencyProperty OptionsProperty =
            DependencyProperty.Register(
                nameof(Options),
                typeof(ChartGPUOptions),
                typeof(ChartControl),
                new PropertyMetadata(null, OnOptionsChanged));

        public ChartGPUOptions? Options
        {
            get => (ChartGPUOptions?)GetValue(OptionsProperty);
            set => SetValue(OptionsProperty, value);
        }

        // Interaction Zoom and Pan states [0.0, 100.0]
        private double _zoomStart = 0.0;
        private double _zoomEnd = 100.0;

        public double ZoomStart
        {
            get => _zoomStart;
            set { if (_zoomStart != value) { _zoomStart = value; Invalidate(); } }
        }

        public double ZoomEnd
        {
            get => _zoomEnd;
            set { if (_zoomEnd != value) { _zoomEnd = value; Invalidate(); } }
        }

        // Syncing Events
        public event EventHandler<ZoomRangeChangedEventArgs>? ZoomRangeChanged;
        public event EventHandler<CrosshairMovedEventArgs>? CrosshairMoved;

        // Pointer Dragging and Panning States
        private bool _isPanning = false;
        private Vector2 _lastPointerPos;
        private double _dragStartZoomStart = 0.0;
        private double _dragStartZoomEnd = 100.0;

        // Visual DataZoom Slider Dragging States
        private bool _isDraggingSliderLeft = false;
        private bool _isDraggingSliderRight = false;
        private bool _isDraggingSliderMiddle = false;

        // Interaction Hover states (Domain units)
        private double? _activeInteractionX = null;
        private Vector2 _lastHoverPos = Vector2.Zero;

        // Layout bounds (Logical CSS Pixels)
        private Rect _plotArea;
        private Rect _sliderArea;
        private Rect _sliderLeftHandle;
        private Rect _sliderRightHandle;

        public ChartControl()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            IsTabStop = true;
            Unloaded += (s, e) => DisposeGpuBuffers();
        }

        private void DisposeGpuBuffers()
        {
            if (Options?.Series == null) return;
            foreach (var series in Options.Series)
            {
                series.GpuBuffer?.Dispose();
                series.GpuBuffer = null;
            }
        }

        private static void OnOptionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (ChartControl)d;
            if (e.OldValue is ChartGPUOptions oldOptions && oldOptions.Series != null)
            {
                foreach (var series in oldOptions.Series)
                {
                    series.GpuBuffer?.Dispose();
                    series.GpuBuffer = null;
                }
            }

            if (e.NewValue is ChartGPUOptions options)
            {
                if (options.DataZoom != null && options.DataZoom.Count > 0)
                {
                    control._zoomStart = options.DataZoom[0].Start;
                    control._zoomEnd = options.DataZoom[0].End;
                }
                else
                {
                    control._zoomStart = 0.0;
                    control._zoomEnd = 100.0;
                }
            }
            control.Invalidate();
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            return availableSize;
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            Size = arrangeRect.Size;

            double width = arrangeRect.Width;
            double height = arrangeRect.Height;

            double left = 60.0;
            double right = 40.0;
            double top = 50.0;
            double bottom = 60.0;

            if (Options?.Grid != null)
            {
                if (Options.Grid.Left.HasValue) left = Options.Grid.Left.Value;
                if (Options.Grid.Right.HasValue) right = Options.Grid.Right.Value;
                if (Options.Grid.Top.HasValue) top = Options.Grid.Top.Value;
                if (Options.Grid.Bottom.HasValue) bottom = Options.Grid.Bottom.Value;
            }

            bool hasSlider = HasSliderZoom();
            double sliderHeight = hasSlider ? 30.0 : 0.0;
            double sliderMargin = hasSlider ? 15.0 : 0.0;

            double plotWidth = Math.Max(10.0, width - left - right);
            double plotHeight = Math.Max(10.0, height - top - bottom - sliderHeight - sliderMargin);

            _plotArea = new Rect((float)left, (float)top, (float)plotWidth, (float)plotHeight);

            if (hasSlider)
            {
                double sliderY = height - bottom - sliderHeight;
                _sliderArea = new Rect((float)left, (float)sliderY, (float)plotWidth, (float)sliderHeight);
                UpdateSliderHandleAreas();
            }
        }

        private void UpdateSliderHandleAreas()
        {
            if (!HasSliderZoom()) return;

            float x = _sliderArea.X;
            float w = _sliderArea.Width;
            float y = _sliderArea.Y;
            float h = _sliderArea.Height;

            float leftX = x + (float)(_zoomStart / 100.0 * w);
            float rightX = x + (float)(_zoomEnd / 100.0 * w);

            _sliderLeftHandle = new Rect(leftX - 6f, y, 12f, h);
            _sliderRightHandle = new Rect(rightX - 6f, y, 12f, h);
        }

        private bool HasSliderZoom()
        {
            if (Options?.DataZoom == null) return false;
            foreach (var z in Options.DataZoom)
            {
                if (z.Type.Equals("slider", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public void SetZoomRange(double start, double end, object? sourceToken = null)
        {
            _zoomStart = Math.Clamp(start, 0.0, end - 2.0);
            _zoomEnd = Math.Clamp(end, start + 2.0, 100.0);
            UpdateSliderHandleAreas();
            NotifyZoomChanged(sourceToken);
            Invalidate();
        }

        public void SetCrosshairX(double? x, object? sourceToken = null)
        {
            if (_activeInteractionX != x)
            {
                _activeInteractionX = x;
                CrosshairMoved?.Invoke(this, new CrosshairMovedEventArgs(x, sourceToken));
                Invalidate();
            }
        }

        #region Pointer Interactions

        public override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            var pos = e.Position;
            _lastPointerPos = pos;

            if (HasSliderZoom() && _sliderArea.Contains(pos))
            {
                if (_sliderLeftHandle.Contains(pos))
                {
                    _isDraggingSliderLeft = true;
                    e.Handled = true;
                }
                else if (_sliderRightHandle.Contains(pos))
                {
                    _isDraggingSliderRight = true;
                    e.Handled = true;
                }
                else if (pos.X >= _sliderLeftHandle.X && pos.X <= _sliderRightHandle.X + _sliderRightHandle.Width)
                {
                    _isDraggingSliderMiddle = true;
                    _dragStartZoomStart = _zoomStart;
                    _dragStartZoomEnd = _zoomEnd;
                    e.Handled = true;
                }
                else
                {
                    double pct = (pos.X - _sliderArea.X) / _sliderArea.Width * 100.0;
                    pct = Math.Clamp(pct, 0.0, 100.0);
                    if (Math.Abs(pct - _zoomStart) < Math.Abs(pct - _zoomEnd))
                    {
                        _zoomStart = Math.Min(pct, _zoomEnd - 2.0);
                    }
                    else
                    {
                        _zoomEnd = Math.Max(pct, _zoomStart + 2.0);
                    }
                    UpdateSliderHandleAreas();
                    NotifyZoomChanged(null);
                    Invalidate();
                    e.Handled = true;
                }
                return;
            }

            if (_plotArea.Contains(pos))
            {
                _isPanning = true;
                _dragStartZoomStart = _zoomStart;
                _dragStartZoomEnd = _zoomEnd;
                e.Handled = true;
            }

            base.OnPointerPressed(e);
        }

        public override void OnPointerMoved(PointerRoutedEventArgs e)
        {
            var pos = e.Position;
            _lastHoverPos = pos;

            if (_isDraggingSliderLeft)
            {
                double pct = (pos.X - _sliderArea.X) / _sliderArea.Width * 100.0;
                _zoomStart = Math.Clamp(pct, 0.0, _zoomEnd - 2.0);
                UpdateSliderHandleAreas();
                NotifyZoomChanged(null);
                Invalidate();
                e.Handled = true;
            }
            else if (_isDraggingSliderRight)
            {
                double pct = (pos.X - _sliderArea.X) / _sliderArea.Width * 100.0;
                _zoomEnd = Math.Clamp(pct, _zoomStart + 2.0, 100.0);
                UpdateSliderHandleAreas();
                NotifyZoomChanged(null);
                Invalidate();
                e.Handled = true;
            }
            else if (_isDraggingSliderMiddle)
            {
                double deltaPct = (pos.X - _lastPointerPos.X) / _sliderArea.Width * 100.0;
                double span = _dragStartZoomEnd - _dragStartZoomStart;
                double newStart = _dragStartZoomStart + deltaPct;
                double newEnd = newStart + span;

                if (newStart < 0.0)
                {
                    newStart = 0.0;
                    newEnd = span;
                }
                else if (newEnd > 100.0)
                {
                    newEnd = 100.0;
                    newStart = 100.0 - span;
                }

                _zoomStart = newStart;
                _zoomEnd = newEnd;
                UpdateSliderHandleAreas();
                NotifyZoomChanged(null);
                Invalidate();
                e.Handled = true;
            }
            else if (_isPanning)
            {
                double deltaPct = -(pos.X - _lastPointerPos.X) / _plotArea.Width * 100.0;
                double span = _dragStartZoomEnd - _dragStartZoomStart;
                double newStart = _zoomStart + deltaPct;
                double newEnd = newStart + span;

                if (newStart < 0.0)
                {
                    newStart = 0.0;
                    newEnd = span;
                }
                else if (newEnd > 100.0)
                {
                    newEnd = 100.0;
                    newStart = 100.0 - span;
                }

                _zoomStart = newStart;
                _zoomEnd = newEnd;
                UpdateSliderHandleAreas();
                NotifyZoomChanged(null);
                Invalidate();
                _lastPointerPos = pos;
                e.Handled = true;
            }
            else
            {
                if (_plotArea.Contains(pos))
                {
                    double invX = InvertX(pos.X);
                    if (_activeInteractionX != invX)
                    {
                        _activeInteractionX = invX;
                        CrosshairMoved?.Invoke(this, new CrosshairMovedEventArgs(invX, null));
                        Invalidate();
                    }
                }
                else
                {
                    if (_activeInteractionX.HasValue)
                    {
                        _activeInteractionX = null;
                        CrosshairMoved?.Invoke(this, new CrosshairMovedEventArgs(null, null));
                        Invalidate();
                    }
                }
            }

            base.OnPointerMoved(e);
        }

        public override void OnPointerReleased(PointerRoutedEventArgs e)
        {
            _isDraggingSliderLeft = false;
            _isDraggingSliderRight = false;
            _isDraggingSliderMiddle = false;
            _isPanning = false;

            base.OnPointerReleased(e);
        }

        public override void OnPointerWheelChanged(PointerRoutedEventArgs e)
        {
            var pos = e.Position;
            if (_plotArea.Contains(pos))
            {
                double zoomFactor = 0.05;
                double delta = e.WheelDelta > 0 ? -zoomFactor : zoomFactor;

                double mousePct = (pos.X - _plotArea.X) / _plotArea.Width;
                double currentSpan = _zoomEnd - _zoomStart;
                double newSpan = Math.Clamp(currentSpan * (1.0 + delta), 2.0, 100.0);

                double newStart = _zoomStart + (currentSpan - newSpan) * mousePct;
                double newEnd = newStart + newSpan;

                if (newStart < 0.0)
                {
                    newStart = 0.0;
                    newEnd = newSpan;
                }
                else if (newEnd > 100.0)
                {
                    newEnd = 100.0;
                    newStart = 100.0 - newSpan;
                }

                _zoomStart = newStart;
                _zoomEnd = newEnd;

                UpdateSliderHandleAreas();
                NotifyZoomChanged(null);
                Invalidate();
                e.Handled = true;
            }

            base.OnPointerWheelChanged(e);
        }

        private void NotifyZoomChanged(object? sourceToken)
        {
            if (Options?.DataZoom != null && Options.DataZoom.Count > 0)
            {
                Options.DataZoom[0].Start = _zoomStart;
                Options.DataZoom[0].End = _zoomEnd;
            }
            ZoomRangeChanged?.Invoke(this, new ZoomRangeChangedEventArgs(_zoomStart, _zoomEnd, sourceToken));
        }

        public void ResetZoom()
        {
            _zoomStart = 0.0;
            _zoomEnd = 100.0;
            NotifyZoomChanged(null);
            InvalidateArrange();
            Invalidate();
        }

        private double InvertX(float canvasX)
        {
            var bounds = GetGlobalBounds();
            double xMin = bounds.XMin;
            double xMax = bounds.XMax;

            double currentXMin = xMin + (_zoomStart / 100.0) * (xMax - xMin);
            double currentXMax = xMin + (_zoomEnd / 100.0) * (xMax - xMin);

            var xScale = new LinearScale().SetDomain(currentXMin, currentXMax).SetRange(_plotArea.X, _plotArea.X + _plotArea.Width);
            return xScale.Invert(canvasX);
        }

        #endregion

        #region Custom Render Steps

        public override void OnRender(DrawingContext context)
        {
            if (Options == null)
            {
                base.OnRender(context);
                return;
            }

            // DPI-aware snappings configuration
            double dpiScale = 1.0;
            float snLeft = (float)DpiSnapper.Snap(_plotArea.X, dpiScale);
            float snTop = (float)DpiSnapper.Snap(_plotArea.Y, dpiScale);
            float snWidth = (float)DpiSnapper.Snap(_plotArea.Width, dpiScale);
            float snHeight = (float)DpiSnapper.Snap(_plotArea.Height, dpiScale);

            var gridBack = ThemeManager.GetBrush("CardBackground");
            var gridBorder = ThemeManager.GetBrush("ControlBorder");
            var borderPen = new Pen(gridBorder, 1f);

            // 1. Grid backdrop
            context.DrawRectangle(gridBack, borderPen, new Rect(snLeft, snTop, snWidth, snHeight));

            // Compute bounds and scales
            var globalBounds = GetGlobalBounds();
            double xMin = globalBounds.XMin;
            double xMax = globalBounds.XMax;

            double currentXMin = xMin + (_zoomStart / 100.0) * (xMax - xMin);
            double currentXMax = xMin + (_zoomEnd / 100.0) * (xMax - xMin);

            // Y-Axes bounds mapping (Left vs Right)
            double yMinLeft = GetActiveYBounds(currentXMin, currentXMax, "y1").YMin;
            double yMaxLeft = GetActiveYBounds(currentXMin, currentXMax, "y1").YMax;

            double yMinRight = double.PositiveInfinity;
            double yMaxRight = double.NegativeInfinity;
            bool hasY2 = Options.YAxes != null && Options.YAxes.Count > 1;

            if (hasY2)
            {
                var y2Bounds = GetActiveYBounds(currentXMin, currentXMax, "y2");
                yMinRight = y2Bounds.YMin;
                yMaxRight = y2Bounds.YMax;
            }

            var xScale = new LinearScale().SetDomain(currentXMin, currentXMax).SetRange(snLeft, snLeft + snWidth);
            var yScaleLeft = new LinearScale().SetDomain(yMinLeft, yMaxLeft).SetRange(snTop + snHeight, snTop);
            
            LinearScale? yScaleRight = null;
            if (hasY2)
            {
                yScaleRight = new LinearScale().SetDomain(yMinRight, yMaxRight).SetRange(snTop + snHeight, snTop);
            }

            var defaultFont = Font ?? Microsoft.UI.Xaml.Controls.PopupService.DefaultFont;

            // 2. Draw Gridlines
            AxisGridRenderer.DrawGridlines(context, Options, xScale, yScaleLeft, _plotArea);

            // 3. Draw Axis ticks and text labels (Left Y-Axis + Right Y-Axis if present)
            if (defaultFont != null)
            {
                AxisGridRenderer.DrawAxes(context, Options, xScale, yScaleLeft, yScaleRight, defaultFont, _plotArea);
            }

            // 4. Draw Annotations (belowSeries layer)
            if (defaultFont != null)
            {
                AnnotationRenderer.Draw(context, Options, xScale, yScaleLeft, defaultFont, _plotArea, "belowSeries");
            }

            // 5. Draw Series
            context.PushClip(_plotArea);
            DrawSeries(context, xScale, yScaleLeft, yScaleRight);
            context.PopClip();

            // 6. Draw Annotations (aboveSeries layer)
            if (defaultFont != null)
            {
                AnnotationRenderer.Draw(context, Options, xScale, yScaleLeft, defaultFont, _plotArea, "aboveSeries");
            }

            // 7. Draw Visual DataZoom Slider Track
            if (HasSliderZoom())
            {
                DataZoomComponent.Draw(context, _sliderArea, _sliderLeftHandle, _sliderRightHandle, _zoomStart, _zoomEnd);
            }

            // 8. Draw Legend items list
            if (defaultFont != null)
            {
                LegendComponent.Draw(context, Options, defaultFont, new Rect(Vector2.Zero, Size));
            }

            // 9. Draw Active Interaction Overlay (Tooltip & Crosshair)
            if (defaultFont != null)
            {
                TooltipComponent.Draw(context, Options, xScale, yScaleLeft, defaultFont, _plotArea, _lastHoverPos, _activeInteractionX.HasValue);
            }

            base.OnRender(context);
        }

        private void DrawSeries(DrawingContext context, LinearScale xScale, LinearScale yScaleLeft, LinearScale? yScaleRight)
        {
            if (Options?.Series == null) return;

            var palette = Options.Palette ?? new string[] { "#0078D4", "#107C41", "#D83B01", "#A8003F", "#5C2D91" };
            int totalBars = GetBarSeriesCount();
            int barSeriesIdx = 0;

            for (int s = 0; s < Options.Series.Count; s++)
            {
                var series = Options.Series[s];
                if (!series.Visible) continue;

                var seriesColorStr = series.Color ?? palette[s % palette.Count];
                var seriesColorVal = ChartUtils.ParseCssColor(seriesColorStr);
                var brush = new SolidColorBrush(seriesColorVal);

                // Dynamically route Y Scale based on series yAxis binding ('y1' left or 'y2' right)
                bool isY2 = series.YAxis != null && series.YAxis.Equals("y2", StringComparison.OrdinalIgnoreCase) && yScaleRight != null;
                var currentYScale = isY2 ? yScaleRight! : yScaleLeft;

                if (series is LineSeriesConfig ls && ls.Data != null)
                {
                    LineRenderer.Draw(context, ls, xScale, currentYScale, brush, seriesColorVal, _plotArea);
                }
                else if (series is AreaSeriesConfig aes && aes.Data != null)
                {
                    LineRenderer.DrawArea(context, aes, xScale, currentYScale, brush, seriesColorVal, _plotArea);
                }
                else if (series is BarSeriesConfig bs && bs.Data != null)
                {
                    BarRenderer.Draw(context, bs, barSeriesIdx++, totalBars, xScale, currentYScale, brush, _plotArea);
                }
                else if (series is ScatterSeriesConfig scs && scs.Data != null)
                {
                    ScatterRenderer.Draw(context, scs, xScale, currentYScale, brush, _plotArea);
                }
                else if (series is CandlestickSeriesConfig csc && csc.Data != null)
                {
                    CandlestickRenderer.Draw(context, csc, xScale, currentYScale, _plotArea);
                }
                else if (series is PieSeriesConfig psc)
                {
                    PieRenderer.Draw(context, psc, _plotArea, palette);
                }
            }
        }

        private ChartBounds GetGlobalBounds()
        {
            double xMin = double.PositiveInfinity;
            double xMax = double.NegativeInfinity;

            if (Options?.Series == null || Options.Series.Count == 0)
            {
                return new ChartBounds(0, 1, 0, 1);
            }

            bool hasValidData = false;

            foreach (var s in Options.Series)
            {
                if (!s.Visible) continue;

                if (s is LineSeriesConfig ls && ls.Data != null)
                {
                    var b = ls.Data.ComputeRawBounds();
                    if (b.HasValue)
                    {
                        xMin = Math.Min(xMin, b.Value.XMin);
                        xMax = Math.Max(xMax, b.Value.XMax);
                        hasValidData = true;
                    }
                }
                else if (s is AreaSeriesConfig aes && aes.Data != null)
                {
                    var b = aes.Data.ComputeRawBounds();
                    if (b.HasValue)
                    {
                        xMin = Math.Min(xMin, b.Value.XMin);
                        xMax = Math.Max(xMax, b.Value.XMax);
                        hasValidData = true;
                    }
                }
                else if (s is BarSeriesConfig bs && bs.Data != null)
                {
                    var b = bs.Data.ComputeRawBounds();
                    if (b.HasValue)
                    {
                        xMin = Math.Min(xMin, b.Value.XMin);
                        xMax = Math.Max(xMax, b.Value.XMax);
                        hasValidData = true;
                    }
                }
                else if (s is ScatterSeriesConfig scs && scs.Data != null)
                {
                    var b = scs.Data.ComputeRawBounds();
                    if (b.HasValue)
                    {
                        xMin = Math.Min(xMin, b.Value.XMin);
                        xMax = Math.Max(xMax, b.Value.XMax);
                        hasValidData = true;
                    }
                }
                else if (s is CandlestickSeriesConfig csc && csc.Data != null && csc.Data.Count > 0)
                {
                    foreach (var item in csc.Data)
                    {
                        xMin = Math.Min(xMin, item.Timestamp);
                        xMax = Math.Max(xMax, item.Timestamp);
                        hasValidData = true;
                    }
                }
            }

            if (!hasValidData || xMin == double.PositiveInfinity)
            {
                return new ChartBounds(0.0, 100.0, 0.0, 100.0);
            }

            if (xMin == xMax) xMax = xMin + 1.0;
            return new ChartBounds(xMin, xMax, 0.0, 1.0);
        }

        private ChartBounds GetActiveYBounds(double currentXMin, double currentXMax, string? yAxisId)
        {
            double yMin = double.PositiveInfinity;
            double yMax = double.NegativeInfinity;

            if (Options?.Series == null || Options.Series.Count == 0)
            {
                return new ChartBounds(0, 1, 0, 1);
            }

            bool hasValidData = false;

            foreach (var s in Options.Series)
            {
                if (!s.Visible) continue;

                // Match Y-axis ID: If s.YAxis == "y2", it is bound to right. Match accordingly.
                bool isY2 = s.YAxis != null && s.YAxis.Equals("y2", StringComparison.OrdinalIgnoreCase);
                bool matchY2 = yAxisId != null && yAxisId.Equals("y2", StringComparison.OrdinalIgnoreCase);
                if (isY2 != matchY2) continue;

                var currentAxisConfig = matchY2 ? Options.YAxes?[1] : Options.YAxis;
                bool autoBoundsVisible = currentAxisConfig == null || 
                                         currentAxisConfig.AutoBounds.Equals("visible", StringComparison.OrdinalIgnoreCase);

                if (s is LineSeriesConfig ls && ls.Data != null)
                {
                    int count = ls.Data.PointCount;
                    for (int i = 0; i < count; i++)
                    {
                        double x = ls.Data.GetX(i);
                        double y = ls.Data.GetY(i);
                        if (!double.IsFinite(x) || !double.IsFinite(y)) continue;

                        if (!autoBoundsVisible || (x >= currentXMin && x <= currentXMax))
                        {
                            yMin = Math.Min(yMin, y);
                            yMax = Math.Max(yMax, y);
                            hasValidData = true;
                        }
                    }
                }
                else if (s is AreaSeriesConfig aes && aes.Data != null)
                {
                    int count = aes.Data.PointCount;
                    for (int i = 0; i < count; i++)
                    {
                        double x = aes.Data.GetX(i);
                        double y = aes.Data.GetY(i);
                        if (!double.IsFinite(x) || !double.IsFinite(y)) continue;

                        if (!autoBoundsVisible || (x >= currentXMin && x <= currentXMax))
                        {
                            yMin = Math.Min(yMin, y);
                            yMax = Math.Max(yMax, y);
                            hasValidData = true;
                        }
                    }
                }
                else if (s is BarSeriesConfig bs && bs.Data != null)
                {
                    int count = bs.Data.PointCount;
                    for (int i = 0; i < count; i++)
                    {
                        double x = bs.Data.GetX(i);
                        double y = bs.Data.GetY(i);
                        if (!double.IsFinite(x) || !double.IsFinite(y)) continue;

                        if (!autoBoundsVisible || (x >= currentXMin && x <= currentXMax))
                        {
                            yMin = Math.Min(yMin, y);
                            yMax = Math.Max(yMax, y);
                            hasValidData = true;
                        }
                    }
                }
                else if (s is ScatterSeriesConfig scs && scs.Data != null)
                {
                    int count = scs.Data.PointCount;
                    for (int i = 0; i < count; i++)
                    {
                        double x = scs.Data.GetX(i);
                        double y = scs.Data.GetY(i);
                        if (!double.IsFinite(x) || !double.IsFinite(y)) continue;

                        if (!autoBoundsVisible || (x >= currentXMin && x <= currentXMax))
                        {
                            yMin = Math.Min(yMin, y);
                            yMax = Math.Max(yMax, y);
                            hasValidData = true;
                        }
                    }
                }
                else if (s is CandlestickSeriesConfig csc && csc.Data != null)
                {
                    foreach (var item in csc.Data)
                    {
                        if (!autoBoundsVisible || (item.Timestamp >= currentXMin && item.Timestamp <= currentXMax))
                        {
                            yMin = Math.Min(yMin, item.Low);
                            yMax = Math.Max(yMax, item.High);
                            hasValidData = true;
                        }
                    }
                }
            }

            if (!hasValidData || yMin == double.PositiveInfinity)
            {
                // Fallback range check
                var selectedAxis = (yAxisId != null && yAxisId.Equals("y2", StringComparison.OrdinalIgnoreCase) && Options.YAxes != null && Options.YAxes.Count > 1) 
                    ? Options.YAxes[1] : Options.YAxis;

                double fallbackMin = selectedAxis?.Min ?? 0.0;
                double fallbackMax = selectedAxis?.Max ?? 100.0;
                return new ChartBounds(0.0, 1.0, fallbackMin, fallbackMax);
            }

            // Explicity user configurations override bounds
            var currentAxis = (yAxisId != null && yAxisId.Equals("y2", StringComparison.OrdinalIgnoreCase) && Options.YAxes != null && Options.YAxes.Count > 1) 
                ? Options.YAxes[1] : Options.YAxis;

            if (currentAxis?.Min.HasValue == true) yMin = currentAxis.Min.Value;
            if (currentAxis?.Max.HasValue == true) yMax = currentAxis.Max.Value;

            if (yMin == yMax) yMax = yMin + 1.0;
            return new ChartBounds(0.0, 1.0, yMin, yMax);
        }

        private int GetBarSeriesCount()
        {
            int c = 0;
            if (Options?.Series != null)
            {
                foreach (var s in Options.Series)
                {
                    if (s is BarSeriesConfig && s.Visible) c++;
                }
            }
            return c;
        }

        #endregion
    }
}
