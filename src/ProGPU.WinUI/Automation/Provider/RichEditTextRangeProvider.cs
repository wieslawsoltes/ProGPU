using System.Globalization;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Microsoft.UI.Xaml.Automation.Provider;

internal sealed class RichEditTextRangeProvider : ITextRangeProvider
{
    private readonly RichEditBox _owner;

    public RichEditTextRangeProvider(RichEditBox owner, int start, int end)
    {
        _owner = owner;
        int length = owner.Text.Length;
        Start = Math.Clamp(Math.Min(start, end), 0, length);
        End = Math.Clamp(Math.Max(start, end), 0, length);
    }

    public int Start { get; private set; }
    public int End { get; private set; }

    public ITextRangeProvider Clone() => new RichEditTextRangeProvider(_owner, Start, End);

    public bool Compare(ITextRangeProvider textRangeProvider) =>
        textRangeProvider is RichEditTextRangeProvider other &&
        ReferenceEquals(_owner, other._owner) && Start == other.Start && End == other.End;

    public int CompareEndpoints(
        TextPatternRangeEndpoint endpoint,
        ITextRangeProvider textRangeProvider,
        TextPatternRangeEndpoint targetEndpoint)
    {
        if (textRangeProvider is not RichEditTextRangeProvider other || !ReferenceEquals(_owner, other._owner))
        {
            throw new ArgumentException("Text ranges must belong to the same provider.", nameof(textRangeProvider));
        }

        int left = endpoint == TextPatternRangeEndpoint.Start ? Start : End;
        int right = targetEndpoint == TextPatternRangeEndpoint.Start ? other.Start : other.End;
        return left.CompareTo(right);
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        Microsoft.UI.Text.ITextRange range = _owner.TextDocument.GetRange(Start, End);
        range.Expand(ToRangeUnit(unit));
        Update(range);
    }

    public ITextRangeProvider? FindAttribute(int attributeId, object value, bool backward)
    {
        ArgumentNullException.ThrowIfNull(value);
        RichTextSpan[] spans = _owner.GetDocumentSpans(Start, End);
        int cursor = Start;
        RichEditTextRangeProvider? match = null;
        foreach (RichTextSpan span in spans)
        {
            object candidate = GetStyleAttributeValue(attributeId, span.Style);
            if (!ReferenceEquals(candidate, AutomationElementIdentifiers.NotSupported) && AttributeEquals(candidate, value))
            {
                var current = new RichEditTextRangeProvider(_owner, cursor, cursor + span.Text.Length);
                if (!backward)
                {
                    return current;
                }

                match = current;
            }

            cursor += span.Text.Length;
        }

        return match;
    }

    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(text);
        string source = _owner.Text.Substring(Start, End - Start);
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int offset = backward ? source.LastIndexOf(text, comparison) : source.IndexOf(text, comparison);
        return offset < 0
            ? null
            : new RichEditTextRangeProvider(_owner, Start + offset, Start + offset + text.Length);
    }

    public object GetAttributeValue(int attributeId)
    {
        object paragraph = GetParagraphAttributeValue(attributeId);
        if (!ReferenceEquals(paragraph, AutomationElementIdentifiers.NotSupported))
        {
            return paragraph;
        }

        RichTextSpan[] spans = _owner.GetDocumentSpans(Start, End);
        if (spans.Length == 0)
        {
            return GetStyleAttributeValue(attributeId, _owner.GetDocumentStyleForRange(Start, End));
        }

        object value = GetStyleAttributeValue(attributeId, spans[0].Style);
        if (ReferenceEquals(value, AutomationElementIdentifiers.NotSupported))
        {
            return value;
        }

        for (int index = 1; index < spans.Length; index++)
        {
            object candidate = GetStyleAttributeValue(attributeId, spans[index].Style);
            if (!AttributeEquals(value, candidate))
            {
                return AutomationElementIdentifiers.MixedAttributeValue;
            }
        }

        return value;
    }

    public void GetBoundingRectangles(out double[] returnValue)
    {
        ProGPU.Scene.Rect[] lines = _owner.GetDocumentClientRangeRectangles(Start, End);
        returnValue = new double[lines.Length * 4];
        for (int index = 0; index < lines.Length; index++)
        {
            ProGPU.Scene.Rect bounds = _owner.ClientToScreenBounds(lines[index]);
            int offset = index * 4;
            returnValue[offset] = bounds.X;
            returnValue[offset + 1] = bounds.Y;
            returnValue[offset + 2] = bounds.Width;
            returnValue[offset + 3] = bounds.Height;
        }
    }

    public IRawElementProviderSimple GetEnclosingElement() =>
        new(_owner.GetOrCreateAutomationPeer()!);

    public string GetText(int maxLength = -1)
    {
        string value = _owner.Text.Substring(Start, End - Start);
        return maxLength >= 0 && value.Length > maxLength ? value[..maxLength] : value;
    }

    public void Select() => _owner.TextDocument.Selection.SetRange(Start, End);

    public int Move(TextUnit unit, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        int moved = 0;
        int direction = Math.Sign(count);
        int length = End - Start;
        for (int index = 0; index < Math.Abs(count); index++)
        {
            Microsoft.UI.Text.ITextRange range = _owner.TextDocument.GetRange(Start, Start);
            range.Move(ToRangeUnit(unit), direction);
            int next = range.StartPosition;
            if (next == Start)
            {
                break;
            }

            Start = next;
            End = Math.Clamp(Start + length, Start, _owner.Text.Length);
            moved += direction;
        }

        return moved;
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        if (count == 0)
        {
            return 0;
        }

        int moved = 0;
        int direction = Math.Sign(count);
        for (int index = 0; index < Math.Abs(count); index++)
        {
            int current = endpoint == TextPatternRangeEndpoint.Start ? Start : End;
            Microsoft.UI.Text.ITextRange range = _owner.TextDocument.GetRange(current, current);
            range.Move(ToRangeUnit(unit), direction);
            int next = range.StartPosition;
            if (next == current)
            {
                break;
            }

            if (endpoint == TextPatternRangeEndpoint.Start)
            {
                Start = Math.Min(next, End);
            }
            else
            {
                End = Math.Max(next, Start);
            }

            moved += direction;
        }

        return moved;
    }

    public void MoveEndpointByRange(
        TextPatternRangeEndpoint endpoint,
        ITextRangeProvider textRangeProvider,
        TextPatternRangeEndpoint targetEndpoint)
    {
        if (textRangeProvider is not RichEditTextRangeProvider other || !ReferenceEquals(_owner, other._owner))
        {
            throw new ArgumentException("Text ranges must belong to the same provider.", nameof(textRangeProvider));
        }

        int value = targetEndpoint == TextPatternRangeEndpoint.Start ? other.Start : other.End;
        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            Start = Math.Min(value, End);
        }
        else
        {
            End = Math.Max(value, Start);
        }
    }

    public void AddToSelection() => Select();

    public void RemoveFromSelection() => _owner.TextDocument.Selection.SetRange(Start, Start);

    public void ScrollIntoView(bool alignToTop) =>
        _owner.ScrollDocumentPositionIntoView(alignToTop ? Start : End);

    public IRawElementProviderSimple[] GetChildren()
    {
        FrameworkElement[] children = _owner.GetDocumentEmbeddedChildren(Start, End);
        if (children.Length == 0)
        {
            return Array.Empty<IRawElementProviderSimple>();
        }

        var result = new IRawElementProviderSimple[children.Length];
        for (int index = 0; index < children.Length; index++)
        {
            AutomationPeer peer = children[index].GetOrCreateAutomationPeer() ??
                new FrameworkElementAutomationPeer(children[index]);
            result[index] = new IRawElementProviderSimple(peer);
        }

        return result;
    }

    private object GetParagraphAttributeValue(int attributeId)
    {
        Microsoft.UI.Text.RichParagraphFormatState state = _owner.GetDocumentParagraphFormatState(Start);
        return (AutomationTextAttributesEnum)attributeId switch
        {
            AutomationTextAttributesEnum.HorizontalTextAlignmentAttribute => (int)state.Alignment,
            AutomationTextAttributesEnum.IndentationFirstLineAttribute => (double)state.FirstLineIndent,
            AutomationTextAttributesEnum.IndentationLeadingAttribute => (double)state.LeftIndent,
            AutomationTextAttributesEnum.IndentationTrailingAttribute => (double)state.RightIndent,
            AutomationTextAttributesEnum.MarginBottomAttribute => (double)state.SpaceAfter,
            AutomationTextAttributesEnum.MarginTopAttribute => (double)state.SpaceBefore,
            AutomationTextAttributesEnum.TextFlowDirectionsAttribute =>
                state.RightToLeft == Microsoft.UI.Text.FormatEffect.On
                    ? AutomationFlowDirections.RightToLeft
                    : AutomationFlowDirections.Default,
            AutomationTextAttributesEnum.TabsAttribute => GetTabs(state),
            _ => AutomationElementIdentifiers.NotSupported
        };
    }

    private object GetStyleAttributeValue(int attributeId, RichTextStyle style) =>
        (AutomationTextAttributesEnum)attributeId switch
        {
            AutomationTextAttributesEnum.BackgroundColorAttribute => ToColorRef(style.Background),
            AutomationTextAttributesEnum.CapStyleAttribute => style.IsAllCaps ? 2 : style.IsSmallCaps ? 1 : 0,
            AutomationTextAttributesEnum.CultureAttribute => GetCulture(style.LanguageTag),
            AutomationTextAttributesEnum.FontNameAttribute => style.FontName ?? style.Font?.FamilyName ?? _owner.Font?.FamilyName ?? string.Empty,
            AutomationTextAttributesEnum.FontSizeAttribute => (double)style.FontSize,
            AutomationTextAttributesEnum.FontWeightAttribute => style.FontWeight > 0 ? style.FontWeight : style.IsBold ? 700 : 400,
            AutomationTextAttributesEnum.ForegroundColorAttribute => ToColorRef(style.Foreground),
            AutomationTextAttributesEnum.IsHiddenAttribute => style.IsHidden,
            AutomationTextAttributesEnum.IsItalicAttribute => style.IsItalic,
            AutomationTextAttributesEnum.IsReadOnlyAttribute => _owner.IsReadOnly || style.IsProtected,
            AutomationTextAttributesEnum.IsSubscriptAttribute => style.IsSubscript,
            AutomationTextAttributesEnum.IsSuperscriptAttribute => style.IsSuperscript,
            AutomationTextAttributesEnum.OutlineStylesAttribute => style.IsOutline ? 1 : 0,
            AutomationTextAttributesEnum.StrikethroughColorAttribute => ToColorRef(style.Foreground),
            AutomationTextAttributesEnum.StrikethroughStyleAttribute => style.IsStrikethrough ? 1 : 0,
            AutomationTextAttributesEnum.UnderlineColorAttribute => ToColorRef(style.Foreground),
            AutomationTextAttributesEnum.UnderlineStyleAttribute => (int)style.UnderlineType,
            AutomationTextAttributesEnum.LinkAttribute => !string.IsNullOrEmpty(style.Link),
            AutomationTextAttributesEnum.IsActiveAttribute => _owner.IsFocused,
            _ => AutomationElementIdentifiers.NotSupported
        };

    private static bool AttributeEquals(object left, object right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is IConvertible && right is IConvertible)
        {
            try
            {
                return Convert.ToString(left, CultureInfo.InvariantCulture) ==
                       Convert.ToString(right, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
            }
        }

        return left.Equals(right);
    }

    private static int ToColorRef(ProGPU.Vector.Brush? brush)
    {
        if (brush is not ProGPU.Vector.SolidColorBrush solid)
        {
            return 0;
        }

        System.Numerics.Vector4 color = System.Numerics.Vector4.Clamp(
            solid.Color,
            System.Numerics.Vector4.Zero,
            System.Numerics.Vector4.One);
        int red = (int)MathF.Round(color.X * 255f);
        int green = (int)MathF.Round(color.Y * 255f);
        int blue = (int)MathF.Round(color.Z * 255f);
        return red | (green << 8) | (blue << 16);
    }

    private static int GetCulture(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return CultureInfo.InvariantCulture.LCID;
        }

        try
        {
            return CultureInfo.GetCultureInfo(languageTag).LCID;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture.LCID;
        }
    }

    private static double[] GetTabs(Microsoft.UI.Text.RichParagraphFormatState state)
    {
        var tabs = new double[state.Tabs.Count];
        for (int index = 0; index < tabs.Length; index++)
        {
            tabs[index] = state.Tabs[index].Position;
        }

        return tabs;
    }

    private void Update(Microsoft.UI.Text.ITextRange range)
    {
        Start = Math.Clamp(Math.Min(range.StartPosition, range.EndPosition), 0, _owner.Text.Length);
        End = Math.Clamp(Math.Max(range.StartPosition, range.EndPosition), Start, _owner.Text.Length);
    }

    private static Microsoft.UI.Text.TextRangeUnit ToRangeUnit(TextUnit unit) => unit switch
    {
        TextUnit.Format => Microsoft.UI.Text.TextRangeUnit.CharacterFormat,
        TextUnit.Word => Microsoft.UI.Text.TextRangeUnit.Word,
        TextUnit.Line => Microsoft.UI.Text.TextRangeUnit.Line,
        TextUnit.Paragraph => Microsoft.UI.Text.TextRangeUnit.Paragraph,
        TextUnit.Page => Microsoft.UI.Text.TextRangeUnit.Screen,
        TextUnit.Document => Microsoft.UI.Text.TextRangeUnit.Story,
        _ => Microsoft.UI.Text.TextRangeUnit.Character
    };
}
