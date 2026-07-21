using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Documents;

internal sealed class RichBlockLayoutCache
{
    public float Height = -1f;
    public float YOffset;
    public bool IsLayoutValid;
    public float WidthConstraint = -1f;
    public Thickness Padding;
    public ElementTheme Theme;
    public TtfFont? Font;
    public float FontSize = -1f;
    public Brush? Foreground;
    public TextAlignment Alignment;
    public TextWrapping TextWrapping;
    public TextReadingOrder TextReadingOrder;
    public FlowDirection FlowDirection;
    public bool AlignmentIncludesTrailingWhitespace;
    public bool IgnoreTrailingCharacterSpacing;
    public int LogicalTextOffset;
    public List<PositionedRichChar> Characters { get; } = new();
    public List<TableVisualDecoration> Decorations { get; } = new();

    public void RebaseTextPositions(int logicalTextOffset)
    {
        int delta = logicalTextOffset - LogicalTextOffset;
        if (delta == 0) return;
        for (int index = 0; index < Characters.Count; index++)
        {
            PositionedRichChar character = Characters[index];
            RichChar info = character.Info;
            info.TextPosition += delta;
            character.Info = info;
            character.ClusterStart += delta;
        }
        LogicalTextOffset = logicalTextOffset;
    }

    public bool Matches(
        float width,
        Thickness padding,
        TtfFont font,
        float fontSize,
        Brush? foreground,
        TextAlignment alignment,
        ElementTheme theme,
        TextWrapping textWrapping,
        TextReadingOrder textReadingOrder,
        FlowDirection flowDirection,
        bool alignmentIncludesTrailingWhitespace,
        bool ignoreTrailingCharacterSpacing) =>
        IsLayoutValid &&
        Math.Abs(WidthConstraint - width) < 0.01f &&
        Padding.Equals(padding) &&
        ReferenceEquals(Font, font) &&
        FontSize == fontSize &&
        Equals(Foreground, foreground) &&
        Alignment == alignment &&
        Theme == theme &&
        TextWrapping == textWrapping &&
        TextReadingOrder == textReadingOrder &&
        FlowDirection == flowDirection &&
        AlignmentIncludesTrailingWhitespace == alignmentIncludesTrailingWhitespace &&
        IgnoreTrailingCharacterSpacing == ignoreTrailingCharacterSpacing;

    public void SetKey(
        float width,
        Thickness padding,
        TtfFont font,
        float fontSize,
        Brush? foreground,
        TextAlignment alignment,
        ElementTheme theme,
        TextWrapping textWrapping,
        TextReadingOrder textReadingOrder,
        FlowDirection flowDirection,
        bool alignmentIncludesTrailingWhitespace,
        bool ignoreTrailingCharacterSpacing)
    {
        WidthConstraint = width;
        Padding = padding;
        Font = font;
        FontSize = fontSize;
        Foreground = foreground;
        Alignment = alignment;
        Theme = theme;
        TextWrapping = textWrapping;
        TextReadingOrder = textReadingOrder;
        FlowDirection = flowDirection;
        AlignmentIncludesTrailingWhitespace = alignmentIncludesTrailingWhitespace;
        IgnoreTrailingCharacterSpacing = ignoreTrailingCharacterSpacing;
        IsLayoutValid = true;
    }
}
