using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Text;

public sealed class RichEditTextCharacterFormat : ITextCharacterFormat
{
    private readonly RichEditTextRange? _range;
    private RichTextStyle _snapshot;

    internal RichEditTextCharacterFormat(RichEditTextRange range) => _range = range;
    private RichEditTextCharacterFormat(RichTextStyle snapshot) => _snapshot = snapshot;

    public FormatEffect AllCaps
    {
        get => GetEffect(static style => style.IsAllCaps);
        set => Apply(style => style with { IsAllCaps = Resolve(value, style.IsAllCaps) });
    }

    public Windows.UI.Color BackgroundColor
    {
        get => TryUniform(static style => ToColor(style.Background), out Windows.UI.Color value)
            ? value : TextConstants.UndefinedColor;
        set => Apply(style => style with { Background = ToBrush(value) });
    }

    public FormatEffect Bold
    {
        get => GetEffect(static style => style.IsBold);
        set => Apply(style =>
        {
            bool bold = Resolve(value, style.IsBold);
            return style with
            {
                IsBold = bold,
                FontWeight = bold ? Math.Max(600, style.FontWeight) : style.FontWeight >= 600 ? 400 : style.FontWeight
            };
        });
    }

    public Windows.UI.Text.FontStretch FontStretch
    {
        get => TryUniform(static style => style.FontStretch, out Windows.UI.Text.FontStretch value)
            ? value : TextConstants.UndefinedFontStretch;
        set => Apply(style => style with { FontStretch = value });
    }

    public Windows.UI.Text.FontStyle FontStyle
    {
        get => TryUniform(static style => style.FontStyle, out Windows.UI.Text.FontStyle value)
            ? value : TextConstants.UndefinedFontStyle;
        set => Apply(style => style with
        {
            FontStyle = value,
            IsItalic = value is Windows.UI.Text.FontStyle.Italic or Windows.UI.Text.FontStyle.Oblique
        });
    }

    public Windows.UI.Color ForegroundColor
    {
        get => TryUniform(static style => ToColor(style.Foreground), out Windows.UI.Color value)
            ? value : TextConstants.UndefinedColor;
        set => Apply(style => style with { Foreground = ToBrush(value) });
    }

    public FormatEffect Hidden
    {
        get => GetEffect(static style => style.IsHidden);
        set => Apply(style => style with { IsHidden = Resolve(value, style.IsHidden) });
    }

    public FormatEffect Italic
    {
        get => GetEffect(static style => style.IsItalic ||
            style.FontStyle is Windows.UI.Text.FontStyle.Italic or Windows.UI.Text.FontStyle.Oblique);
        set => Apply(style =>
        {
            bool italic = Resolve(value, style.IsItalic);
            return style with
            {
                IsItalic = italic,
                FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal
            };
        });
    }

    public float Kerning
    {
        get => GetFloat(static style => style.Kerning);
        set => Apply(style => style with { Kerning = ValidateFinite(value, nameof(value)) });
    }

    public string LanguageTag
    {
        get => TryUniform(static style => style.LanguageTag ?? string.Empty, out string value)
            ? value : string.Empty;
        set => Apply(style => style with { LanguageTag = value ?? string.Empty });
    }

    public LinkType LinkType => TryUniform(
        static style => string.IsNullOrEmpty(style.Link) ? LinkType.NotALink : LinkType.ClientLink,
        out LinkType value) ? value : LinkType.Undefined;

    public string Name
    {
        get => TryUniform(static style => style.FontName ?? style.Font?.FamilyName ?? string.Empty, out string value)
            ? value : string.Empty;
        set
        {
            value ??= string.Empty;
            TtfFont? font = FontApi.Manager.MatchFamily(value);
            Apply(style => style with { FontName = value, Font = font ?? style.Font });
        }
    }

    public FormatEffect Outline
    {
        get => GetEffect(static style => style.IsOutline);
        set => Apply(style => style with { IsOutline = Resolve(value, style.IsOutline) });
    }

    public float Position
    {
        get => GetFloat(static style => style.BaselineOffset);
        set => Apply(style => style with { BaselineOffset = ValidateFinite(value, nameof(value)) });
    }

    public FormatEffect ProtectedText
    {
        get => GetEffect(static style => style.IsProtected);
        set => Apply(style => style with { IsProtected = Resolve(value, style.IsProtected) });
    }

    public float Size
    {
        get => GetFloat(static style => style.FontSize);
        set
        {
            if (!(value > 0f) || !float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            Apply(style => style with { FontSize = value });
        }
    }

    public FormatEffect SmallCaps
    {
        get => GetEffect(static style => style.IsSmallCaps);
        set => Apply(style => style with { IsSmallCaps = Resolve(value, style.IsSmallCaps) });
    }

    public float Spacing
    {
        get => GetFloat(static style => style.CharacterSpacing);
        set => Apply(style => style with { CharacterSpacing = ValidateFinite(value, nameof(value)) });
    }

    public FormatEffect Strikethrough
    {
        get => GetEffect(static style => style.IsStrikethrough);
        set => Apply(style => style with { IsStrikethrough = Resolve(value, style.IsStrikethrough) });
    }

    public FormatEffect Subscript
    {
        get => GetEffect(static style => style.IsSubscript);
        set => Apply(style => style with
        {
            IsSubscript = Resolve(value, style.IsSubscript),
            IsSuperscript = value == FormatEffect.On ? false : style.IsSuperscript
        });
    }

    public FormatEffect Superscript
    {
        get => GetEffect(static style => style.IsSuperscript);
        set => Apply(style => style with
        {
            IsSuperscript = Resolve(value, style.IsSuperscript),
            IsSubscript = value == FormatEffect.On ? false : style.IsSubscript
        });
    }

    public TextScript TextScript
    {
        get => TryUniform(static style => style.TextScript, out TextScript value)
            ? value : TextScript.Undefined;
        set => Apply(style => style with { TextScript = value });
    }

    public UnderlineType Underline
    {
        get => TryUniform(
            static style => style.UnderlineType != UnderlineType.None
                ? style.UnderlineType
                : style.IsUnderline ? UnderlineType.Single : UnderlineType.None,
            out UnderlineType value) ? value : UnderlineType.Undefined;
        set => Apply(style => style with
        {
            UnderlineType = value,
            IsUnderline = value is not (UnderlineType.None or UnderlineType.Undefined)
        });
    }

    public int Weight
    {
        get => TryUniform(
            static style => style.FontWeight != 0 ? style.FontWeight : style.IsBold ? 700 : 400,
            out int value) ? value : TextConstants.UndefinedInt32Value;
        set
        {
            if (value is < 1 or > 999) throw new ArgumentOutOfRangeException(nameof(value));
            Apply(style => style with { FontWeight = value, IsBold = value >= 600 });
        }
    }

    public ITextCharacterFormat GetClone() => new RichEditTextCharacterFormat(Current);

    public bool IsEqual(ITextCharacterFormat format) =>
        format is RichEditTextCharacterFormat rich && Current.Equals(rich.Current);

    public void SetClone(ITextCharacterFormat value) => ApplyFrom(value);

    private RichTextStyle Current => _range is null
        ? _snapshot
        : _range.CurrentStyle;

    private FormatEffect GetEffect(Func<RichTextStyle, bool> selector) =>
        TryUniform(selector, out bool value) ? Effect(value) : FormatEffect.Undefined;

    private float GetFloat(Func<RichTextStyle, float> selector) =>
        TryUniform(selector, out float value) ? value : TextConstants.UndefinedFloatValue;

    private bool TryUniform<T>(Func<RichTextStyle, T> selector, out T value)
    {
        if (_range is null || _range.Length == 0)
        {
            value = selector(Current);
            return true;
        }
        RichTextSpan[] spans = _range.Document.Owner.GetDocumentSpans(
            _range.NormalizedStart,
            _range.NormalizedEnd);
        if (spans.Length == 0)
        {
            value = selector(Current);
            return true;
        }
        value = selector(spans[0].Style);
        for (int index = 1; index < spans.Length; index++)
            if (!EqualityComparer<T>.Default.Equals(value, selector(spans[index].Style))) return false;
        return true;
    }

    private void Apply(Func<RichTextStyle, RichTextStyle> transform)
    {
        if (_range is null)
        {
            _snapshot = transform(_snapshot);
            return;
        }
        _range.Document.Owner.SetDocumentStyle(_range.NormalizedStart, _range.NormalizedEnd, transform);
    }

    internal void ApplyFrom(ITextCharacterFormat value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (ReferenceEquals(value, this)) return;
        FormatEffect bold = value.Bold;
        FormatEffect italic = value.Italic;
        UnderlineType underline = value.Underline;
        Windows.UI.Color foreground = value.ForegroundColor;
        Windows.UI.Color background = value.BackgroundColor;
        string name = value.Name;
        TtfFont? font = string.IsNullOrWhiteSpace(name) ? null : FontApi.Manager.MatchFamily(name);
        Apply(
            style => style with
            {
                IsBold = Resolve(bold, style.IsBold),
                IsItalic = Resolve(italic, style.IsItalic),
                IsUnderline = underline is not (UnderlineType.None or UnderlineType.Undefined),
                UnderlineType = underline,
                FontSize = value.Size > 0f && float.IsFinite(value.Size) ? value.Size : style.FontSize,
                Foreground = ToBrush(foreground),
                Background = ToBrush(background),
                IsAllCaps = Resolve(value.AllCaps, style.IsAllCaps),
                FontStretch = value.FontStretch,
                FontStyle = value.FontStyle,
                IsHidden = Resolve(value.Hidden, style.IsHidden),
                Kerning = value.Kerning,
                LanguageTag = value.LanguageTag,
                FontName = name,
                Font = font ?? style.Font,
                IsOutline = Resolve(value.Outline, style.IsOutline),
                BaselineOffset = value.Position,
                IsProtected = Resolve(value.ProtectedText, style.IsProtected),
                IsSmallCaps = Resolve(value.SmallCaps, style.IsSmallCaps),
                CharacterSpacing = value.Spacing,
                IsStrikethrough = Resolve(value.Strikethrough, style.IsStrikethrough),
                IsSubscript = Resolve(value.Subscript, style.IsSubscript),
                IsSuperscript = Resolve(value.Superscript, style.IsSuperscript),
                TextScript = value.TextScript,
                FontWeight = value.Weight
            });
    }

    private static FormatEffect Effect(bool value) => value ? FormatEffect.On : FormatEffect.Off;

    private static float ValidateFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }

    private static Windows.UI.Color ToColor(Brush? brush)
    {
        if (brush is not SolidColorBrush solid) return default;
        Vector4 color = Vector4.Clamp(solid.Color, Vector4.Zero, Vector4.One);
        return Windows.UI.Color.FromArgb(
            (byte)MathF.Round(color.W * 255f),
            (byte)MathF.Round(color.X * 255f),
            (byte)MathF.Round(color.Y * 255f),
            (byte)MathF.Round(color.Z * 255f));
    }

    private static Brush ToBrush(Windows.UI.Color color) => new SolidColorBrush(new Vector4(
        color.R / 255f,
        color.G / 255f,
        color.B / 255f,
        color.A / 255f));

    private static bool Resolve(FormatEffect effect, bool current) => effect switch
    {
        FormatEffect.On => true,
        FormatEffect.Off => false,
        FormatEffect.Toggle => !current,
        _ => current
    };
}
