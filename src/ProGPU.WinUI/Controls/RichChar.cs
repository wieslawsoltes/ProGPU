using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Text;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Text.Shaping;

namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml.Documents;

public struct RichChar
{
    public char Character;
    public int TextPosition;
    public Brush Foreground;
    public float FontSize;
    public TtfFont? Font;
    public bool IsBold;
    public bool IsItalic;
    public bool IsUnderline;
    public Brush? Background;
    public bool IsStrikethrough;
    public float CharacterSpacing;
    public float BaselineOffset;
    public bool IsHidden;
    public bool IsProtected;
    public bool IsAllCaps;
    public bool IsSmallCaps;
    public bool IsOutline;
    public string? LanguageTag;
    public Microsoft.UI.Text.TextScript TextScript;
    public Microsoft.UI.Text.UnderlineType UnderlineType;
    public int FontWeight;
    public Windows.UI.Text.FontStretch FontStretch;
    public Windows.UI.Text.FontStyle FontStyle;
    public float Kerning;
    public string? FontName;
    public bool IsSubscript;
    public bool IsSuperscript;
    public FlowDirection? FlowDirection;
    public RichTextStyle? RetainedStyle;
    public Inline? SourceInline;
    public FrameworkElement? EmbeddedElement;
    public float LeftIndent;    // Bullet list indents
    public float BulletOffset;  // Bullet negative gutter shift
}

} // namespace Microsoft.UI.Xaml.Controls
