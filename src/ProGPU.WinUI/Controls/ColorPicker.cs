using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Numerics;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Controls;

public class ColorChangedEventArgs : EventArgs
{
    public Vector4 OldColor { get; }
    public Vector4 NewColor { get; }

    public ColorChangedEventArgs(Vector4 oldColor, Vector4 newColor)
    {
        OldColor = oldColor;
        NewColor = newColor;
    }
}

public class ColorPicker : Control
{
    public static readonly DependencyProperty ColorProperty =
        DependencyProperty.Register(
            "Color",
            typeof(Vector4),
            typeof(ColorPicker),
            new PropertyMetadata(new Vector4(1f, 1f, 1f, 1f), OnColorChangedStatic));

    public Vector4 Color
    {
        get => (Vector4)(GetValue(ColorProperty) ?? new Vector4(1f, 1f, 1f, 1f));
        set => SetValue(ColorProperty, value);
    }

    public event EventHandler<ColorChangedEventArgs>? ColorChanged;

    private bool _isUpdating;
    private float _hue = 0f;
    private float _saturation = 1f;
    private float _value = 1f;
    private float _alpha = 1f;

    // Controls
    private Grid? _rootContainer;
    private ColorSpectrumPad? _spectrumPad;
    private HueSlider? _hueSlider;
    private AlphaSlider? _alphaSlider;
    private Border? _previewBorder;
    
    private TextBox? _hexInput;
    private TextBox? _rInput;
    private TextBox? _gInput;
    private TextBox? _bInput;
    private TextBox? _aInput;

    public ColorPicker()
    {
        WidthConstraint = 280f;
        HeightConstraint = 330f;
        
        InitializePicker();
        UpdatePickerFromColor(Color);
    }

    private static void OnColorChangedStatic(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ColorPicker picker)
        {
            var oldColor = (Vector4)(e.OldValue ?? new Vector4(1f, 1f, 1f, 1f));
            var newColor = (Vector4)(e.NewValue ?? new Vector4(1f, 1f, 1f, 1f));
            
            picker.OnColorChanged(oldColor, newColor);
        }
    }

    private void OnColorChanged(Vector4 oldColor, Vector4 newColor)
    {
        if (_isUpdating)
        {
            ColorChanged?.Invoke(this, new ColorChangedEventArgs(oldColor, newColor));
            return;
        }
        
        _isUpdating = true;
        try
        {
            UpdatePickerFromColor(newColor);
            ColorChanged?.Invoke(this, new ColorChangedEventArgs(oldColor, newColor));
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void InitializePicker()
    {
        _rootContainer = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Layout rows
        _rootContainer.RowDefinitions.Add(new GridLength(170f, GridUnitType.Absolute)); // SV Pad
        _rootContainer.RowDefinitions.Add(new GridLength(8f, GridUnitType.Absolute));   // Gap
        _rootContainer.RowDefinitions.Add(new GridLength(24f, GridUnitType.Absolute));  // Hue Slider
        _rootContainer.RowDefinitions.Add(new GridLength(8f, GridUnitType.Absolute));   // Gap
        _rootContainer.RowDefinitions.Add(new GridLength(24f, GridUnitType.Absolute));  // Alpha Slider
        _rootContainer.RowDefinitions.Add(new GridLength(12f, GridUnitType.Absolute));  // Gap
        _rootContainer.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Preview + Inputs

        // 1. Color Spectrum Pad
        _spectrumPad = new ColorSpectrumPad(this);
        _rootContainer.AddChild(_spectrumPad);
        Grid.SetRow(_spectrumPad, 0);

        // 2. Hue Slider
        _hueSlider = new HueSlider();
        _hueSlider.HorizontalAlignment = HorizontalAlignment.Stretch;
        _hueSlider.ValueChanged += (s, e) => {
            if (_isUpdating) return;
            _isUpdating = true;
            try
            {
                _hue = _hueSlider.Value;
                UpdateColorFromHsv();
                _spectrumPad.Invalidate();
                _alphaSlider?.Invalidate();
            }
            finally
            {
                _isUpdating = false;
            }
        };
        _rootContainer.AddChild(_hueSlider);
        Grid.SetRow(_hueSlider, 2);

        // 3. Alpha Slider
        _alphaSlider = new AlphaSlider(this);
        _alphaSlider.HorizontalAlignment = HorizontalAlignment.Stretch;
        _alphaSlider.ValueChanged += (s, e) => {
            if (_isUpdating) return;
            _isUpdating = true;
            try
            {
                _alpha = _alphaSlider.Value;
                UpdateColorFromHsv();
            }
            finally
            {
                _isUpdating = false;
            }
        };
        _rootContainer.AddChild(_alphaSlider);
        Grid.SetRow(_alphaSlider, 4);

        // 4. Preview and Numeric Inputs Grid
        var bottomGrid = new Grid();
        bottomGrid.ColumnDefinitions.Add(new GridLength(40f, GridUnitType.Absolute));  // Preview
        bottomGrid.ColumnDefinitions.Add(new GridLength(12f, GridUnitType.Absolute)); // Gap
        bottomGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));      // Inputs

        // Color Preview
        _previewBorder = new Border
        {
            CornerRadius = 6f,
            BorderThickness = new Thickness(1f),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        bottomGrid.AddChild(_previewBorder);
        Grid.SetColumn(_previewBorder, 0);

        // Inputs Grid (Hex, R, G, B, A)
        var inputsGrid = new Grid();
        inputsGrid.ColumnDefinitions.Add(new GridLength(65f, GridUnitType.Absolute));  // Hex
        inputsGrid.ColumnDefinitions.Add(new GridLength(6f, GridUnitType.Absolute));   // Gap
        inputsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // R
        inputsGrid.ColumnDefinitions.Add(new GridLength(4f, GridUnitType.Absolute));   // Gap
        inputsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // G
        inputsGrid.ColumnDefinitions.Add(new GridLength(4f, GridUnitType.Absolute));   // Gap
        inputsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // B
        inputsGrid.ColumnDefinitions.Add(new GridLength(4f, GridUnitType.Absolute));   // Gap
        inputsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // A

        inputsGrid.RowDefinitions.Add(new GridLength(18f, GridUnitType.Absolute)); // Label
        inputsGrid.RowDefinitions.Add(new GridLength(28f, GridUnitType.Absolute)); // Input Box

        // Helper to add label
        void AddLabel(string text, int col)
        {
            var tb = new RichTextBlock
            {
                FontSize = 9f,
                Foreground = new ThemeResourceBrush("TextSecondary"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            tb.Inlines.Add(new Run(text));
            inputsGrid.AddChild(tb);
            Grid.SetRow(tb, 0);
            Grid.SetColumn(tb, col);
        }

        AddLabel("HEX", 0);
        AddLabel("R", 2);
        AddLabel("G", 4);
        AddLabel("B", 6);
        AddLabel("A", 8);

        // TextBox setups
        _hexInput = new TextBox { FontSize = 10f, Padding = new Thickness(4, 2), HeightConstraint = 24f, CornerRadius = 4f };
        _hexInput.TextChanged += OnHexChanged;
        inputsGrid.AddChild(_hexInput);
        Grid.SetRow(_hexInput, 1);
        Grid.SetColumn(_hexInput, 0);

        TextBox CreateChannelBox(Action<string> onChanged)
        {
            var box = new TextBox { FontSize = 10f, Padding = new Thickness(4, 2), HeightConstraint = 24f, CornerRadius = 4f, HorizontalAlignment = HorizontalAlignment.Stretch };
            box.TextChanged += (s, e) => onChanged(box.Text);
            return box;
        }

        _rInput = CreateChannelBox(txt => OnChannelChanged(txt, 0));
        inputsGrid.AddChild(_rInput);
        Grid.SetRow(_rInput, 1);
        Grid.SetColumn(_rInput, 2);

        _gInput = CreateChannelBox(txt => OnChannelChanged(txt, 1));
        inputsGrid.AddChild(_gInput);
        Grid.SetRow(_gInput, 1);
        Grid.SetColumn(_gInput, 4);

        _bInput = CreateChannelBox(txt => OnChannelChanged(txt, 2));
        inputsGrid.AddChild(_bInput);
        Grid.SetRow(_bInput, 1);
        Grid.SetColumn(_bInput, 6);

        _aInput = CreateChannelBox(txt => OnChannelChanged(txt, 3));
        inputsGrid.AddChild(_aInput);
        Grid.SetRow(_aInput, 1);
        Grid.SetColumn(_aInput, 8);

        bottomGrid.AddChild(inputsGrid);
        Grid.SetColumn(inputsGrid, 2);

        _rootContainer.AddChild(bottomGrid);
        Grid.SetRow(bottomGrid, 6);

        AddChild(_rootContainer);
    }

    private void OnHexChanged(object? sender, EventArgs e)
    {
        if (_isUpdating || _hexInput == null) return;

        string txt = _hexInput.Text.Trim().TrimStart('#');
        if (txt.Length == 6 || txt.Length == 8)
        {
            try
            {
                if (txt.Length == 6) txt = "FF" + txt;
                uint rgba = Convert.ToUInt32(txt, 16);
                float a = ((rgba >> 24) & 0xFF) / 255.0f;
                float r = ((rgba >> 16) & 0xFF) / 255.0f;
                float g = ((rgba >> 8) & 0xFF) / 255.0f;
                float b = (rgba & 0xFF) / 255.0f;

                _isUpdating = true;
                try
                {
                    Color = new Vector4(r, g, b, a);
                    UpdatePickerFromColor(Color);
                }
                finally
                {
                    _isUpdating = false;
                }
            }
            catch { }
        }
    }

    private void OnChannelChanged(string text, int channel)
    {
        if (_isUpdating) return;

        if (int.TryParse(text, out int val))
        {
            val = Math.Clamp(val, 0, 255);
            float valF = val / 255.0f;

            _isUpdating = true;
            try
            {
                var cur = Color;
                if (channel == 0) cur.X = valF;
                else if (channel == 1) cur.Y = valF;
                else if (channel == 2) cur.Z = valF;
                else if (channel == 3) cur.W = valF;

                Color = cur;
                UpdatePickerFromColor(Color);
            }
            finally
            {
                _isUpdating = false;
            }
        }
    }

    private void UpdatePickerFromColor(Vector4 rgb)
    {
        var (h, s, v) = RgbToHsv(rgb);
        _hue = h;
        _saturation = s;
        _value = v;
        _alpha = rgb.W;

        if (_hueSlider != null) _hueSlider.Value = _hue;
        if (_alphaSlider != null) _alphaSlider.Value = _alpha;
        if (_previewBorder != null) _previewBorder.Background = new SolidColorBrush(rgb);

        // Update TextBoxes
        if (_hexInput != null)
        {
            uint r = (uint)Math.Clamp(rgb.X * 255f, 0, 255);
            uint g = (uint)Math.Clamp(rgb.Y * 255f, 0, 255);
            uint b = (uint)Math.Clamp(rgb.Z * 255f, 0, 255);
            uint a = (uint)Math.Clamp(rgb.W * 255f, 0, 255);
            _hexInput.Text = $"#{a:X2}{r:X2}{g:X2}{b:X2}";
        }

        if (_rInput != null) _rInput.Text = ((int)Math.Clamp(rgb.X * 255f, 0, 255)).ToString();
        if (_gInput != null) _gInput.Text = ((int)Math.Clamp(rgb.Y * 255f, 0, 255)).ToString();
        if (_bInput != null) _bInput.Text = ((int)Math.Clamp(rgb.Z * 255f, 0, 255)).ToString();
        if (_aInput != null) _aInput.Text = ((int)Math.Clamp(rgb.W * 255f, 0, 255)).ToString();

        _spectrumPad?.Invalidate();
        _alphaSlider?.Invalidate();
    }

    private void UpdateColorFromHsv()
    {
        var rgb = HsvToRgb(_hue, _saturation, _value, _alpha);
        Color = rgb;
        if (_previewBorder != null) _previewBorder.Background = new SolidColorBrush(rgb);

        // Update hex and channel texts
        _isUpdating = true;
        try
        {
            if (_hexInput != null)
            {
                uint r = (uint)Math.Clamp(rgb.X * 255f, 0, 255);
                uint g = (uint)Math.Clamp(rgb.Y * 255f, 0, 255);
                uint b = (uint)Math.Clamp(rgb.Z * 255f, 0, 255);
                uint a = (uint)Math.Clamp(rgb.W * 255f, 0, 255);
                _hexInput.Text = $"#{a:X2}{r:X2}{g:X2}{b:X2}";
            }

            if (_rInput != null) _rInput.Text = ((int)Math.Clamp(rgb.X * 255f, 0, 255)).ToString();
            if (_gInput != null) _gInput.Text = ((int)Math.Clamp(rgb.Y * 255f, 0, 255)).ToString();
            if (_bInput != null) _bInput.Text = ((int)Math.Clamp(rgb.Z * 255f, 0, 255)).ToString();
            if (_aInput != null) _aInput.Text = ((int)Math.Clamp(rgb.W * 255f, 0, 255)).ToString();
        }
        finally
        {
            _isUpdating = false;
        }
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (_rootContainer != null)
        {
            _rootContainer.Measure(availableSize);
            return _rootContainer.DesiredSize;
        }
        return base.MeasureOverride(availableSize);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        if (_rootContainer != null)
        {
            _rootContainer.Arrange(arrangeRect);
        }
        else
        {
            base.ArrangeOverride(arrangeRect);
        }
    }

    // Helper conversions
    public static Vector4 HsvToRgb(float h, float s, float v, float alpha = 1f)
    {
        float r = 0, g = 0, b = 0;
        if (s == 0)
        {
            r = g = b = v;
        }
        else
        {
            float sector = h / 60f;
            int i = (int)Math.Floor(sector);
            float f = sector - i;
            float p = v * (1f - s);
            float q = v * (1f - s * f);
            float t = v * (1f - s * (1f - f));
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                case 5: r = v; g = p; b = q; break;
            }
        }
        return new Vector4(r, g, b, alpha);
    }

    public static (float H, float S, float V) RgbToHsv(Vector4 rgb)
    {
        float r = rgb.X;
        float g = rgb.Y;
        float b = rgb.Z;
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float h = 0f;
        float s = max == 0f ? 0f : (max - min) / max;
        float v = max;
        float diff = max - min;
        if (diff > 0f)
        {
            if (max == r) h = (g - b) / diff + (g < b ? 6f : 0f);
            else if (max == g) h = (b - r) / diff + 2f;
            else h = (r - g) / diff + 4f;
            h *= 60f;
        }
        return (h, s, v);
    }

    // Custom Sub-components for premium rendering
    private class ColorSpectrumPad : FrameworkElement
    {
        private readonly ColorPicker _picker;
        private bool _isDragging;

        public float CornerRadius { get; set; } = 6f;

        public ColorSpectrumPad(ColorPicker picker)
        {
            _picker = picker;
        }

        public override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (IsEnabled)
            {
                _isDragging = true;
                InputSystem.CapturePointer(this);
                UpdateValueFromPos(e.Position);
                base.OnPointerPressed(e);
            }
        }

        public override void OnPointerReleased(PointerRoutedEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                InputSystem.ReleasePointerCapture();
            }
            base.OnPointerReleased(e);
        }

        public override void OnPointerCanceled(PointerRoutedEventArgs e)
        {
            _isDragging = false;
            base.OnPointerCanceled(e);
        }

        public override void OnPointerCaptureLost(PointerRoutedEventArgs e)
        {
            _isDragging = false;
            base.OnPointerCaptureLost(e);
        }

        public override void OnPointerMoved(PointerRoutedEventArgs e)
        {
            if (_isDragging && IsEnabled)
            {
                UpdateValueFromPos(e.Position);
            }
            base.OnPointerMoved(e);
        }

        private void UpdateValueFromPos(Vector2 localPos)
        {
            if (_picker._isUpdating) return;
            _picker._isUpdating = true;
            try
            {
                float s = localPos.X / Size.X;
                float v = 1.0f - (localPos.Y / Size.Y);

                _picker._saturation = Math.Clamp(s, 0f, 1f);
                _picker._value = Math.Clamp(v, 0f, 1f);
                _picker.UpdateColorFromHsv();
                Invalidate();
            }
            finally
            {
                _picker._isUpdating = false;
            }
        }

        public override void OnRender(DrawingContext context)
        {
            // Layer 1: Solid color background of the active pure Hue
            var pureColor = HsvToRgb(_picker._hue, 1f, 1f);
            context.DrawRoundedRectangle(new SolidColorBrush(pureColor), null, new Rect(Vector2.Zero, Size), CornerRadius);

            // Layer 2: Horizontal gradient white to transparent (adds Saturation)
            var stopsH = new GradientStop[] {
                new GradientStop(new Vector4(1f, 1f, 1f, 1f), 0f),
                new GradientStop(new Vector4(1f, 1f, 1f, 0f), 1f)
            };
            var brushH = new LinearGradientBrush(new Vector2(0, 0), new Vector2(Size.X, 0), stopsH);
            context.DrawRoundedRectangle(brushH, null, new Rect(Vector2.Zero, Size), CornerRadius);

            // Layer 3: Vertical gradient transparent to black (adds Value/Brightness)
            var stopsV = new GradientStop[] {
                new GradientStop(new Vector4(0f, 0f, 0f, 0f), 0f),
                new GradientStop(new Vector4(0f, 0f, 0f, 1f), 1f)
            };
            var brushV = new LinearGradientBrush(new Vector2(0, 0), new Vector2(0, Size.Y), stopsV);
            context.DrawRoundedRectangle(brushV, null, new Rect(Vector2.Zero, Size), CornerRadius);

            // Layer 4: Thin border around SV pad
            context.DrawRoundedRectangle(null, new Pen(ThemeManager.GetBrush("ControlBorder"), 1f), new Rect(Vector2.Zero, Size), CornerRadius);

            // Layer 5: SV selection circle pointer (inner white + outer dark stroke)
            float cx = _picker._saturation * Size.X;
            float cy = (1.0f - _picker._value) * Size.Y;
            var center = new Vector2(cx, cy);

            context.DrawCircle(null, new Pen(new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.5f)), 2f), center, 6f);
            context.DrawCircle(null, new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), 1.5f), center, 5f);
        }
    }

    private class HueSlider : Slider
    {
        public HueSlider()
        {
            Minimum = 0f;
            Maximum = 360f;
            HeightConstraint = 24f;
        }

        public override void OnRender(DrawingContext context)
        {
            float trackHeight = 8f;
            float yCenter = Size.Y / 2f;
            var trackRect = new Rect(8f, yCenter - trackHeight / 2f, Size.X - 16f, trackHeight);

            // Rainbow tracks
            var hueStops = new GradientStop[] {
                new GradientStop(new Vector4(1f, 0f, 0f, 1f), 0.0f),
                new GradientStop(new Vector4(1f, 1f, 0f, 1f), 0.17f),
                new GradientStop(new Vector4(0f, 1f, 0f, 1f), 0.33f),
                new GradientStop(new Vector4(0f, 1f, 1f, 1f), 0.5f),
                new GradientStop(new Vector4(0f, 0f, 1f, 1f), 0.67f),
                new GradientStop(new Vector4(1f, 0f, 1f, 1f), 0.83f),
                new GradientStop(new Vector4(1f, 0f, 0f, 1f), 1.0f)
            };

            var trackBrush = IsRightToLeftLayout
                ? new LinearGradientBrush(new Vector2(Size.X - 8f, 0f), new Vector2(8f, 0f), hueStops)
                : new LinearGradientBrush(new Vector2(8f, 0f), new Vector2(Size.X - 8f, 0f), hueStops);
            context.DrawRoundedRectangle(trackBrush, null, trackRect, 4f);
            context.DrawRoundedRectangle(null, new Pen(ThemeManager.GetBrush("ControlBorder"), 1f), trackRect, 4f);

            // Thumb
            float pct = Value / 360f;
            float thumbX = IsRightToLeftLayout
                ? Size.X - 8f - pct * (Size.X - 16f)
                : 8f + pct * (Size.X - 16f);
            var center = new Vector2(thumbX, yCenter);

            context.DrawCircle(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), new Pen(new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.4f)), 1.5f), center, 7f);
        }
    }

    private class AlphaSlider : Slider
    {
        private readonly ColorPicker _picker;

        public AlphaSlider(ColorPicker picker)
        {
            _picker = picker;
            Minimum = 0f;
            Maximum = 1f;
            HeightConstraint = 24f;
        }

        public override void OnRender(DrawingContext context)
        {
            float trackHeight = 8f;
            float yCenter = Size.Y / 2f;
            var trackRect = new Rect(8f, yCenter - trackHeight / 2f, Size.X - 16f, trackHeight);

            // Draw checkerboard first
            context.PushClip(trackRect);
            float boxSize = 4f;
            for (float tx = 8f; tx < Size.X - 8f; tx += boxSize)
            {
                for (float ty = yCenter - trackHeight / 2f; ty < yCenter + trackHeight / 2f; ty += boxSize)
                {
                    bool isGrey = ((int)(tx / boxSize) + (int)(ty / boxSize)) % 2 == 0;
                    var col = isGrey ? new Vector4(0.8f, 0.8f, 0.8f, 1f) : new Vector4(1f, 1f, 1f, 1f);
                    context.DrawRectangle(new SolidColorBrush(col), null, new Rect(tx, ty, boxSize, boxSize));
                }
            }
            context.PopClip();

            // Overlay solid hue color with alpha gradient
            var pureColor = HsvToRgb(_picker._hue, _picker._saturation, _picker._value, 1f);
            var alphaStops = new GradientStop[] {
                new GradientStop(new Vector4(pureColor.X, pureColor.Y, pureColor.Z, 0f), 0f),
                new GradientStop(new Vector4(pureColor.X, pureColor.Y, pureColor.Z, 1f), 1f)
            };

            var trackBrush = IsRightToLeftLayout
                ? new LinearGradientBrush(new Vector2(Size.X - 8f, 0f), new Vector2(8f, 0f), alphaStops)
                : new LinearGradientBrush(new Vector2(8f, 0f), new Vector2(Size.X - 8f, 0f), alphaStops);
            context.DrawRoundedRectangle(trackBrush, null, trackRect, 4f);
            context.DrawRoundedRectangle(null, new Pen(ThemeManager.GetBrush("ControlBorder"), 1f), trackRect, 4f);

            // Thumb
            float pct = Value;
            float thumbX = IsRightToLeftLayout
                ? Size.X - 8f - pct * (Size.X - 16f)
                : 8f + pct * (Size.X - 16f);
            var center = new Vector2(thumbX, yCenter);

            context.DrawCircle(new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f)), new Pen(new SolidColorBrush(new Vector4(0f, 0f, 0f, 0.4f)), 1.5f), center, 7f);
        }
    }
}
