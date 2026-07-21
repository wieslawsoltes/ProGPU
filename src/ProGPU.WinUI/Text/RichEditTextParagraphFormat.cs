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

public sealed class RichEditTextParagraphFormat : ITextParagraphFormat
{
    private readonly RichEditBox? _owner;
    private readonly RichEditTextRange? _range;
    private RichParagraphFormatState _state;

    internal RichEditTextParagraphFormat(RichEditBox owner)
    {
        _owner = owner;
        _state = owner.ParagraphFormatState;
    }

    internal RichEditTextParagraphFormat(RichEditTextRange range)
    {
        _range = range;
        _owner = range.Document.Owner;
        _state = _owner.GetDocumentParagraphFormatState(range.NormalizedStart);
    }

    private RichEditTextParagraphFormat(RichParagraphFormatState state) => _state = state;

    public ParagraphAlignment Alignment
    {
        get => TryUniform(ResolveAlignment, out ParagraphAlignment value)
            ? value : ParagraphAlignment.Undefined;
        set
        {
            if (_state.Alignment == value) return;
            PrepareChange();
            _state.Alignment = value;
            if (_owner is not null && _range is null)
            {
                _owner.TextAlignment = value switch
                {
                    ParagraphAlignment.Center => Microsoft.UI.Xaml.TextAlignment.Center,
                    ParagraphAlignment.Right => Microsoft.UI.Xaml.TextAlignment.Right,
                    ParagraphAlignment.Justify => Microsoft.UI.Xaml.TextAlignment.Justify,
                    _ => Microsoft.UI.Xaml.TextAlignment.Left
                };
            }
            Changed();
        }
    }

    public float FirstLineIndent => GetFloat(static state => state.FirstLineIndent);
    public FormatEffect KeepTogether { get => GetEffect(static state => state.KeepTogether); set => Set(ref _state.KeepTogether, value); }
    public FormatEffect KeepWithNext { get => GetEffect(static state => state.KeepWithNext); set => Set(ref _state.KeepWithNext, value); }
    public float LeftIndent => GetFloat(static state => state.LeftIndent);
    public float LineSpacing => GetFloat(static state => state.LineSpacing);
    public LineSpacingRule LineSpacingRule => TryUniform(static state => state.LineSpacingRule, out LineSpacingRule value) ? value : LineSpacingRule.Undefined;
    public MarkerAlignment ListAlignment { get => TryUniform(static state => state.ListAlignment, out MarkerAlignment value) ? value : MarkerAlignment.Undefined; set => Set(ref _state.ListAlignment, value); }
    public int ListLevelIndex { get => GetInt(static state => state.ListLevelIndex); set => Set(ref _state.ListLevelIndex, Math.Max(0, value)); }
    public int ListStart { get => GetInt(static state => state.ListStart); set => Set(ref _state.ListStart, value); }
    public MarkerStyle ListStyle { get => TryUniform(static state => state.ListStyle, out MarkerStyle value) ? value : MarkerStyle.Undefined; set => Set(ref _state.ListStyle, value); }
    public float ListTab { get => GetFloat(static state => state.ListTab); set => SetFinite(ref _state.ListTab, value); }
    public MarkerType ListType { get => TryUniform(static state => state.ListType, out MarkerType value) ? value : MarkerType.Undefined; set => Set(ref _state.ListType, value); }
    public FormatEffect NoLineNumber { get => GetEffect(static state => state.NoLineNumber); set => Set(ref _state.NoLineNumber, value); }
    public FormatEffect PageBreakBefore { get => GetEffect(static state => state.PageBreakBefore); set => Set(ref _state.PageBreakBefore, value); }
    public float RightIndent { get => GetFloat(static state => state.RightIndent); set => SetFinite(ref _state.RightIndent, value); }
    public FormatEffect RightToLeft
    {
        get => TryUniform(ResolveRightToLeft, out FormatEffect value)
            ? value : FormatEffect.Undefined;
        set
        {
            if (_state.RightToLeft == value) return;
            PrepareChange();
            _state.RightToLeft = value;
            if (_owner is not null && _range is null && value is (FormatEffect.On or FormatEffect.Off))
                _owner.FlowDirection = value == FormatEffect.On ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            Changed();
        }
    }
    public float SpaceAfter { get => GetFloat(static state => state.SpaceAfter); set => SetFinite(ref _state.SpaceAfter, value); }
    public float SpaceBefore { get => GetFloat(static state => state.SpaceBefore); set => SetFinite(ref _state.SpaceBefore, value); }
    public ParagraphStyle Style { get => TryUniform(static state => state.Style, out ParagraphStyle value) ? value : ParagraphStyle.Undefined; set => Set(ref _state.Style, value); }
    public int TabCount => TryUniform(TabSignature, out string _) ? _state.Tabs.Count : TextConstants.UndefinedInt32Value;
    public FormatEffect WidowControl { get => GetEffect(static state => state.WidowControl); set => Set(ref _state.WidowControl, value); }

    public void AddTab(float position, TabAlignment align, TabLeader leader)
    {
        if (!float.IsFinite(position) || position < 0f) throw new ArgumentOutOfRangeException(nameof(position));
        int existing = _state.Tabs.FindIndex(tab => tab.Position == position);
        if (existing < 0 && _state.Tabs.Count >= 63)
            throw new InvalidOperationException("A paragraph cannot contain more than 63 tab stops.");
        PrepareChange();
        if (existing >= 0) _state.Tabs.RemoveAt(existing);
        _state.Tabs.Add(new RichTextTab(position, align, leader));
        _state.Tabs.Sort(static (left, right) => left.Position.CompareTo(right.Position));
        Changed();
    }

    public void ClearAllTabs()
    {
        if (_state.Tabs.Count == 0) return;
        PrepareChange();
        _state.Tabs.Clear();
        Changed();
    }

    public void DeleteTab(float position)
    {
        int index = _state.Tabs.FindIndex(tab => tab.Position == position);
        if (index < 0) return;
        PrepareChange();
        _state.Tabs.RemoveAt(index);
        Changed();
    }

    public ITextParagraphFormat GetClone() => new RichEditTextParagraphFormat(_state.Clone());

    public void GetTab(int index, out float position, out TabAlignment align, out TabLeader leader)
    {
        if (TabCount == TextConstants.UndefinedInt32Value || (uint)index >= (uint)_state.Tabs.Count)
        {
            position = 0f;
            align = TabAlignment.Left;
            leader = TabLeader.Spaces;
            return;
        }
        RichTextTab tab = _state.Tabs[index];
        position = tab.Position;
        align = tab.Alignment;
        leader = tab.Leader;
    }

    public bool IsEqual(ITextParagraphFormat format) =>
        format is RichEditTextParagraphFormat rich && StateEquals(_state, rich._state);

    public void SetClone(ITextParagraphFormat format) => ApplyFrom(format);

    public void SetIndents(float start, float left, float right)
    {
        if (!float.IsFinite(start) || !float.IsFinite(left) || !float.IsFinite(right))
            throw new ArgumentOutOfRangeException(nameof(start));
        if (_state.FirstLineIndent == start && _state.LeftIndent == left && _state.RightIndent == right) return;
        PrepareChange();
        _state.FirstLineIndent = start;
        _state.LeftIndent = left;
        _state.RightIndent = right;
        Changed();
    }

    public void SetLineSpacing(LineSpacingRule rule, float spacing)
    {
        if (!float.IsFinite(spacing)) throw new ArgumentOutOfRangeException(nameof(spacing));
        if (rule == LineSpacingRule.Percent) throw new ArgumentException("Percent line spacing is not supported by RichEditBox.", nameof(rule));
        if (_state.LineSpacingRule == rule && _state.LineSpacing == spacing) return;
        PrepareChange();
        _state.LineSpacingRule = rule;
        _state.LineSpacing = spacing;
        Changed();
    }

    internal void ApplyFrom(ITextParagraphFormat value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (ReferenceEquals(value, this)) return;
        if (value.LineSpacingRule == LineSpacingRule.Percent)
            throw new ArgumentException("Percent line spacing is not supported by RichEditBox.", nameof(value));
        if (value.TabCount > 63)
            throw new InvalidOperationException("A paragraph cannot contain more than 63 tab stops.");
        PrepareChange();
        _state.Alignment = value.Alignment;
        _state.FirstLineIndent = value.FirstLineIndent;
        _state.KeepTogether = value.KeepTogether;
        _state.KeepWithNext = value.KeepWithNext;
        _state.LeftIndent = value.LeftIndent;
        _state.LineSpacing = value.LineSpacing;
        _state.LineSpacingRule = value.LineSpacingRule;
        _state.ListAlignment = value.ListAlignment;
        _state.ListLevelIndex = value.ListLevelIndex;
        _state.ListStart = value.ListStart;
        _state.ListStyle = value.ListStyle;
        _state.ListTab = value.ListTab;
        _state.ListType = value.ListType;
        _state.NoLineNumber = value.NoLineNumber;
        _state.PageBreakBefore = value.PageBreakBefore;
        _state.RightIndent = value.RightIndent;
        _state.RightToLeft = value.RightToLeft;
        _state.SpaceAfter = value.SpaceAfter;
        _state.SpaceBefore = value.SpaceBefore;
        _state.Style = value.Style;
        _state.WidowControl = value.WidowControl;
        _state.Tabs.Clear();
        for (int index = 0; index < value.TabCount; index++)
        {
            value.GetTab(index, out float position, out TabAlignment align, out TabLeader leader);
            _state.Tabs.Add(new RichTextTab(position, align, leader));
        }
        if (_owner is not null && _range is null)
        {
            _owner.TextAlignment = value.Alignment switch
            {
                ParagraphAlignment.Center => Microsoft.UI.Xaml.TextAlignment.Center,
                ParagraphAlignment.Right => Microsoft.UI.Xaml.TextAlignment.Right,
                ParagraphAlignment.Justify => Microsoft.UI.Xaml.TextAlignment.Justify,
                _ => Microsoft.UI.Xaml.TextAlignment.Left
            };
            if (value.RightToLeft is FormatEffect.On or FormatEffect.Off)
                _owner.FlowDirection = value.RightToLeft == FormatEffect.On
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight;
        }
        Changed();
    }

    private void PrepareChange() => _owner?.SaveDocumentUndoState();

    private void Changed()
    {
        if (_owner is null) return;
        if (_range is not null)
            _owner.ApplyDocumentParagraphFormat(_range.NormalizedStart, _range.NormalizedEnd, _state);
        else
            _owner.OnDocumentParagraphFormatChanged();
    }

    private ParagraphAlignment ResolveAlignment(RichParagraphFormatState state)
    {
        if (state.Alignment != ParagraphAlignment.Undefined) return state.Alignment;
        return _owner?.TextAlignment switch
        {
            Microsoft.UI.Xaml.TextAlignment.Center => ParagraphAlignment.Center,
            Microsoft.UI.Xaml.TextAlignment.Right => ParagraphAlignment.Right,
            Microsoft.UI.Xaml.TextAlignment.Justify => ParagraphAlignment.Justify,
            null => ParagraphAlignment.Undefined,
            _ => ParagraphAlignment.Left
        };
    }

    private FormatEffect ResolveRightToLeft(RichParagraphFormatState state)
    {
        if (state.RightToLeft != FormatEffect.Undefined) return state.RightToLeft;
        return _owner is null || _range is not null
            ? state.RightToLeft
            : _owner.FlowDirection == FlowDirection.RightToLeft ? FormatEffect.On : FormatEffect.Off;
    }

    private bool TryUniform<T>(Func<RichParagraphFormatState, T> selector, out T value)
    {
        if (_range is null || _owner is null)
        {
            value = selector(_state);
            return true;
        }
        return _owner.TryGetUniformDocumentParagraphValue(
            _range.NormalizedStart,
            _range.NormalizedEnd,
            selector,
            out value);
    }

    private float GetFloat(Func<RichParagraphFormatState, float> selector) =>
        TryUniform(selector, out float value) ? value : TextConstants.UndefinedFloatValue;

    private int GetInt(Func<RichParagraphFormatState, int> selector) =>
        TryUniform(selector, out int value) ? value : TextConstants.UndefinedInt32Value;

    private FormatEffect GetEffect(Func<RichParagraphFormatState, FormatEffect> selector) =>
        TryUniform(selector, out FormatEffect value) ? value : FormatEffect.Undefined;

    private static string TabSignature(RichParagraphFormatState state)
    {
        var builder = new StringBuilder(state.Tabs.Count * 24);
        foreach (RichTextTab tab in state.Tabs)
            builder.Append(tab.Position.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                .Append((int)tab.Alignment).Append(':').Append((int)tab.Leader).Append(';');
        return builder.ToString();
    }

    private void Set<T>(ref T field, T value) where T : struct
    {
        if (field.Equals(value)) return;
        PrepareChange();
        field = value;
        Changed();
    }

    private void SetFinite(ref float field, float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (field == value) return;
        PrepareChange();
        field = value;
        Changed();
    }

    internal static bool StateEquals(RichParagraphFormatState left, RichParagraphFormatState right)
    {
        if (left.Alignment != right.Alignment || left.FirstLineIndent != right.FirstLineIndent ||
            left.KeepTogether != right.KeepTogether || left.KeepWithNext != right.KeepWithNext ||
            left.LeftIndent != right.LeftIndent || left.LineSpacing != right.LineSpacing ||
            left.LineSpacingRule != right.LineSpacingRule || left.ListAlignment != right.ListAlignment ||
            left.ListLevelIndex != right.ListLevelIndex || left.ListStart != right.ListStart ||
            left.ListStyle != right.ListStyle || left.ListTab != right.ListTab ||
            left.ListType != right.ListType || left.NoLineNumber != right.NoLineNumber ||
            left.PageBreakBefore != right.PageBreakBefore || left.RightIndent != right.RightIndent ||
            left.RightToLeft != right.RightToLeft || left.SpaceAfter != right.SpaceAfter ||
            left.SpaceBefore != right.SpaceBefore || left.Style != right.Style ||
            left.WidowControl != right.WidowControl || left.Tabs.Count != right.Tabs.Count ||
            left.IsTableRow != right.IsTableRow || !TableEdgesEqual(left.TableCellRightEdges, right.TableCellRightEdges) ||
            left.TableCellPadding != right.TableCellPadding ||
            left.TableBorderThickness != right.TableBorderThickness ||
            !Equals(left.TableBorderBrush, right.TableBorderBrush) ||
            !TableBrushesEqual(left.TableCellBackgrounds, right.TableCellBackgrounds) ||
            !TableSpansEqual(left.TableCellColumnSpans, right.TableCellColumnSpans) ||
            !TableFlagsEqual(left.TableCellVerticalMergeFlags, right.TableCellVerticalMergeFlags))
            return false;
        for (int index = 0; index < left.Tabs.Count; index++)
            if (left.Tabs[index] != right.Tabs[index]) return false;
        return true;
    }

    private static bool TableEdgesEqual(float[]? left, float[]? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
            if (left[index] != right[index]) return false;
        return true;
    }

    private static bool TableBrushesEqual(
        ProGPU.Vector.Brush?[]? left,
        ProGPU.Vector.Brush?[]? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
            if (!Equals(left[index], right[index])) return false;
        return true;
    }

    private static bool TableSpansEqual(int[]? left, int[]? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
            if (left[index] != right[index]) return false;
        return true;
    }

    private static bool TableFlagsEqual(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
            if (left[index] != right[index]) return false;
        return true;
    }
}
