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

internal sealed class RichParagraphFormatState
{
    public ParagraphAlignment Alignment = ParagraphAlignment.Undefined;
    public float FirstLineIndent;
    public FormatEffect KeepTogether;
    public FormatEffect KeepWithNext;
    public float LeftIndent;
    public float LineSpacing;
    public LineSpacingRule LineSpacingRule = LineSpacingRule.Single;
    public MarkerAlignment ListAlignment = MarkerAlignment.Left;
    public int ListLevelIndex;
    public int ListStart = 1;
    public MarkerStyle ListStyle = MarkerStyle.Period;
    public float ListTab;
    public MarkerType ListType;
    public FormatEffect NoLineNumber;
    public FormatEffect PageBreakBefore;
    public float RightIndent;
    public FormatEffect RightToLeft = FormatEffect.Undefined;
    public float SpaceAfter;
    public float SpaceBefore;
    public ParagraphStyle Style = ParagraphStyle.Normal;
    public FormatEffect WidowControl = FormatEffect.On;
    public readonly System.Collections.Generic.List<RichTextTab> Tabs = new();
    public float DefaultTabStop = 36f;
    internal bool IsTableRow;
    internal float[]? TableCellRightEdges;
    internal float TableCellPadding = 8f;
    internal float TableBorderThickness = 1f;
    internal ProGPU.Vector.Brush? TableBorderBrush;
    internal ProGPU.Vector.Brush?[]? TableCellBackgrounds;
    internal int[]? TableCellColumnSpans;
    internal byte[]? TableCellVerticalMergeFlags;

    public RichParagraphFormatState Clone()
    {
        var clone = new RichParagraphFormatState
        {
            Alignment = Alignment,
            FirstLineIndent = FirstLineIndent,
            KeepTogether = KeepTogether,
            KeepWithNext = KeepWithNext,
            LeftIndent = LeftIndent,
            LineSpacing = LineSpacing,
            LineSpacingRule = LineSpacingRule,
            ListAlignment = ListAlignment,
            ListLevelIndex = ListLevelIndex,
            ListStart = ListStart,
            ListStyle = ListStyle,
            ListTab = ListTab,
            ListType = ListType,
            NoLineNumber = NoLineNumber,
            PageBreakBefore = PageBreakBefore,
            RightIndent = RightIndent,
            RightToLeft = RightToLeft,
            SpaceAfter = SpaceAfter,
            SpaceBefore = SpaceBefore,
            Style = Style,
            WidowControl = WidowControl,
            DefaultTabStop = DefaultTabStop,
            IsTableRow = IsTableRow,
            TableCellRightEdges = TableCellRightEdges is null ? null : (float[])TableCellRightEdges.Clone(),
            TableCellPadding = TableCellPadding,
            TableBorderThickness = TableBorderThickness,
            TableBorderBrush = TableBorderBrush,
            TableCellBackgrounds = TableCellBackgrounds is null
                ? null
                : (ProGPU.Vector.Brush?[])TableCellBackgrounds.Clone(),
            TableCellColumnSpans = TableCellColumnSpans is null
                ? null
                : (int[])TableCellColumnSpans.Clone(),
            TableCellVerticalMergeFlags = TableCellVerticalMergeFlags is null
                ? null
                : (byte[])TableCellVerticalMergeFlags.Clone()
        };
        clone.Tabs.AddRange(Tabs);
        return clone;
    }

    public void CopyFrom(RichParagraphFormatState source)
    {
        Alignment = source.Alignment;
        FirstLineIndent = source.FirstLineIndent;
        KeepTogether = source.KeepTogether;
        KeepWithNext = source.KeepWithNext;
        LeftIndent = source.LeftIndent;
        LineSpacing = source.LineSpacing;
        LineSpacingRule = source.LineSpacingRule;
        ListAlignment = source.ListAlignment;
        ListLevelIndex = source.ListLevelIndex;
        ListStart = source.ListStart;
        ListStyle = source.ListStyle;
        ListTab = source.ListTab;
        ListType = source.ListType;
        NoLineNumber = source.NoLineNumber;
        PageBreakBefore = source.PageBreakBefore;
        RightIndent = source.RightIndent;
        RightToLeft = source.RightToLeft;
        SpaceAfter = source.SpaceAfter;
        SpaceBefore = source.SpaceBefore;
        Style = source.Style;
        WidowControl = source.WidowControl;
        DefaultTabStop = source.DefaultTabStop;
        IsTableRow = source.IsTableRow;
        TableCellRightEdges = source.TableCellRightEdges is null
            ? null
            : (float[])source.TableCellRightEdges.Clone();
        TableCellPadding = source.TableCellPadding;
        TableBorderThickness = source.TableBorderThickness;
        TableBorderBrush = source.TableBorderBrush;
        TableCellBackgrounds = source.TableCellBackgrounds is null
            ? null
            : (ProGPU.Vector.Brush?[])source.TableCellBackgrounds.Clone();
        TableCellColumnSpans = source.TableCellColumnSpans is null
            ? null
            : (int[])source.TableCellColumnSpans.Clone();
        TableCellVerticalMergeFlags = source.TableCellVerticalMergeFlags is null
            ? null
            : (byte[])source.TableCellVerticalMergeFlags.Clone();
        Tabs.Clear();
        Tabs.AddRange(source.Tabs);
    }
}
