using System.Globalization;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;
using ProGPU.Scene;

namespace ProGPU.WinUI.Hmi;

public partial class HmiControl
{
    public static readonly DependencyProperty ColorSchemeProperty = DependencyProperty.Register(nameof(ColorScheme), typeof(HmiColorScheme), typeof(HmiControl),
        new PropertyMetadata(HmiColorScheme.Light, OnPresentationChanged) { AffectsRender = true });
    private TextBlock? _tagCaption;
    private TextBlock? _rangeCaption;
    private (double Minimum, double Maximum, string Unit)? _rangeKey;
    private Vector2 _presentationSize = new(float.NaN, float.NaN);

    public HmiColorScheme ColorScheme
    {
        get => (HmiColorScheme)(GetValue(ColorSchemeProperty) ?? HmiColorScheme.Light);
        set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); SetValue(ColorSchemeProperty, value); }
    }

    public HmiAppearance Appearance => _definition.Appearance.Copy();

    private static void OnPresentationChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var control = (HmiControl)owner;
        if (control._label == null) return;
        control.UpdatePresentation();
        control.Invalidate();
    }

    private void InitializePresentation()
    {
        if (_tagCaption != null) return;
        _tagCaption = new TextBlock { Font = Font, FontSize = 10, IsHitTestVisible = false };
        _rangeCaption = new TextBlock { Font = Font, FontSize = 10, IsHitTestVisible = false };
        AddChild(_tagCaption); AddChild(_rangeCaption);
    }

    private void UpdatePresentation()
    {
        InitializePresentation();
        RequestedTheme = ColorScheme == HmiColorScheme.Light ? ElementTheme.Light : ElementTheme.Dark;
        _label.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Text);
        _value.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Text);
        _quality.Foreground = HmiThemeResources.StatusReference(ColorScheme, VisualTone, IsActive, Quality != HmiQuality.Good || VisualTone == HmiVisualTone.Unknown);
        _tagCaption!.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Muted);
        _rangeCaption!.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Muted);
        _tagCaption.Font = Font; _rangeCaption.Font = Font;
        SetText(_tagCaption, TagName.Length == 0 ? "" : TagName);
        var rangeKey = (_definition.Minimum, _definition.Maximum, Unit);
        if (_rangeKey != rangeKey)
        {
            _rangeKey = rangeKey;
            SetText(_rangeCaption, _definition.Minimum.ToString("0.###", CultureInfo.InvariantCulture) + "  —  " +
                _definition.Maximum.ToString("0.###", CultureInfo.InvariantCulture) + (Unit.Length == 0 ? "" : " " + Unit));
        }
        float width = float.IsFinite(Width) ? Width : Size.X;
        float height = float.IsFinite(Height) ? Height : Size.Y;
        ApplyPresentationLayout(new Vector2(Math.Max(0, width), Math.Max(0, height)));
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var size = new Vector2(float.IsFinite(Width) ? Width : float.IsFinite(availableSize.X) ? availableSize.X : 180,
            float.IsFinite(Height) ? Height : float.IsFinite(availableSize.Y) ? availableSize.Y : 130);
        if (size != _presentationSize) ApplyPresentationLayout(size);
        return base.MeasureOverride(availableSize);
    }

    private void ApplyPresentationLayout(Vector2 size)
    {
        if (_tagCaption == null || !float.IsFinite(size.X) || !float.IsFinite(size.Y)) return;
        _presentationSize = size;
        var l = HmiVisualLayout.Calculate(Symbol, Math.Max(0, size.X), Math.Max(0, size.Y), _definition.Appearance);
        _label.TextWrapping = TextWrapping.NoWrap;
        _label.TextTrimming = TextTrimming.CharacterEllipsis;
        _tagCaption.TextWrapping = TextWrapping.NoWrap;
        _tagCaption.TextTrimming = TextTrimming.CharacterEllipsis;
        _value.TextWrapping = Symbol == HmiSymbol.AlarmList ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _value.TextTrimming = TextTrimming.CharacterEllipsis;
        _rangeCaption!.TextWrapping = TextWrapping.NoWrap;
        _rangeCaption.TextTrimming = TextTrimming.CharacterEllipsis;
        _quality.TextWrapping = TextWrapping.NoWrap;
        _quality.TextTrimming = TextTrimming.CharacterEllipsis;
        Place(_label, l.Header);
        Place(_tagCaption, l.Tag, TagName.Length > 0);
        Place(_value, l.Value, Symbol != HmiSymbol.NumericInput && _definition.Appearance.ShowValue);
        Place(_rangeCaption!, l.Range);
        _label.FontSize = Symbol == HmiSymbol.Label ? size.Y < 40 ? 11 : 20 : HmiSymbolTraits.IsCommand(Symbol) ? 13 : 12;
        _value.FontSize = Symbol is HmiSymbol.AlarmList or HmiSymbol.AlarmBanner or HmiSymbol.Trend ? 12 :
            Symbol == HmiSymbol.NumericDisplay ? Math.Clamp(size.Y * 0.26f, 18, 34) : HmiSymbolTraits.IsBinary(Symbol) ? 12 : 17;
        _value.HorizontalAlignment = HorizontalAlignment.Left;
        bool unknown = Quality != HmiQuality.Good || VisualTone == HmiVisualTone.Unknown;
        string status = unknown ? Quality == HmiQuality.Good ? "UNKNOWN QUALITY" : "DATA " + Quality.ToString().ToUpperInvariant() : StateText;
        SetText(_quality, status);
        if (status.Length > 0)
        {
            if (l.Range.Height > 0) { Place(_quality, l.Range); _rangeCaption!.Visibility = Visibility.Collapsed; }
            else if (l.Tag.Height > 0) { Place(_quality, l.Tag); _tagCaption.Visibility = Visibility.Collapsed; }
            else if (l.Value.Height > 0) { Place(_quality, l.Value); _value.Visibility = Visibility.Collapsed; }
            else Place(_quality, new Rect(2, Math.Max(0, size.Y - 14), Math.Max(0, size.X - 4), Math.Min(14, size.Y)));
        }
        else _quality.Visibility = Visibility.Collapsed;
        if (_inputRow != null) Place(_inputRow, l.Glyph);
        if (_input != null)
        {
            _input.PlaceholderForeground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Muted);
            _input.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Text); _input.Background = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Surface); _input.BorderBrush = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Outline);
        }
        if (_applyLabel != null) _applyLabel.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Text);
        if (_trendAxis != null)
        {
            _trendAxis.TextWrapping = TextWrapping.NoWrap;
            _trendAxis.TextTrimming = TextTrimming.CharacterEllipsis;
            _trendAxis.Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Muted);
            Place(_trendAxis, new Rect(12, Math.Max(0, size.Y - 26), Math.Max(0, size.X / 2 - 20), 18));
        }
    }

    private static void Place(FrameworkElement element, Rect rectangle, bool show = true)
    {
        var visibility = show && rectangle.Width > 0 && rectangle.Height > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != visibility) element.Visibility = visibility;
        if (visibility == Visibility.Collapsed) return;
        var margin = new Thickness(rectangle.X, rectangle.Y, 0, 0);
        if (!element.Margin.Equals(margin)) element.Margin = margin;
        if (element.Width != rectangle.Width) element.Width = rectangle.Width;
        if (element.Height != rectangle.Height) element.Height = rectangle.Height;
        element.HorizontalAlignment = HorizontalAlignment.Left;
        element.VerticalAlignment = VerticalAlignment.Top;
        if (element is TextBlock) element.ClipBounds = new Rect(0, 0, rectangle.Width, rectangle.Height);
    }
}
