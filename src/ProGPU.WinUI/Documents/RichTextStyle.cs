using System;
using System.Collections.Generic;
using System.Text;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>Formatting shared by one contiguous editor span.</summary>
public readonly record struct RichTextStyle(
    Brush? Foreground,
    float FontSize,
    TtfFont? Font = null,
    bool IsBold = false,
    bool IsItalic = false,
    bool IsUnderline = false,
    string? Link = null,
    Brush? Background = null,
    bool IsStrikethrough = false,
    float CharacterSpacing = 0f,
    float BaselineOffset = 0f,
    bool IsHidden = false,
    bool IsProtected = false,
    bool IsAllCaps = false,
    bool IsSmallCaps = false,
    bool IsOutline = false,
    string? LanguageTag = null,
    Microsoft.UI.Text.TextScript TextScript = Microsoft.UI.Text.TextScript.Undefined,
    Microsoft.UI.Text.UnderlineType UnderlineType = Microsoft.UI.Text.UnderlineType.None,
    int FontWeight = 0,
    Windows.UI.Text.FontStretch FontStretch = Windows.UI.Text.FontStretch.Normal,
    Windows.UI.Text.FontStyle FontStyle = Windows.UI.Text.FontStyle.Normal,
    float Kerning = 0f,
    string? FontName = null,
    bool IsSubscript = false,
    bool IsSuperscript = false,
    RichTextEmbeddedObject? EmbeddedObject = null,
    FlowDirection? FlowDirection = null);
