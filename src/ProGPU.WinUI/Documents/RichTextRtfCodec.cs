using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using ProGPU.Text;
using ProGPU.Vector;

namespace Microsoft.UI.Xaml.Documents;

/// <summary>
/// Reflection-free RTF 1.x interchange for the editor's retained styled spans.
/// Unknown destinations are skipped, while Unicode, paragraphs, tabs, font size,
/// bold, italic, and underline round-trip without entering the layout hot path.
/// </summary>
public static class RichTextRtfCodec
{
    private const int MaxCoalescedTextLength = 4096;

    internal readonly record struct ParagraphSpan(
        int Start,
        int Length,
        Microsoft.UI.Text.RichParagraphFormatState Format)
    {
        public bool IsTableRow { get; init; }
        public float[]? TableCellRightEdges { get; init; }
    }

    internal sealed class DecodedDocument
    {
        public required RichTextSpan[] Spans { get; init; }
        public required ParagraphSpan[] Paragraphs { get; init; }
        public RichTextStyle? DocumentDefaultStyle { get; init; }
        public Microsoft.UI.Text.RichParagraphFormatState? DocumentDefaultParagraphFormat { get; init; }
    }

    public static string Encode(IReadOnlyList<RichTextSpan> spans) => Encode(spans, null);

    internal static string Encode(
        IReadOnlyList<RichTextSpan> spans,
        IReadOnlyList<ParagraphSpan>? paragraphs) => Encode(spans, paragraphs, includeFinalEop: false);

    internal static string Encode(
        IReadOnlyList<RichTextSpan> spans,
        IReadOnlyList<ParagraphSpan>? paragraphs,
        bool includeFinalEop)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var fonts = new List<string> { "Segoe UI" };
        var fontIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Segoe UI"] = 0
        };
        var colors = new List<RtfColor>();
        var colorIndices = new Dictionary<RtfColor, int>();
        foreach (RichTextSpan span in spans)
        {
            string? family = !string.IsNullOrWhiteSpace(span.Style.FontName)
                ? span.Style.FontName
                : span.Style.Font?.FamilyName;
            if (!string.IsNullOrWhiteSpace(family) && !fontIndices.ContainsKey(family))
            {
                fontIndices.Add(family, fonts.Count);
                fonts.Add(family);
            }
            AddColor(span.Style.Foreground, colors, colorIndices);
            AddColor(span.Style.Background, colors, colorIndices);
        }
        if (paragraphs is not null)
        {
            for (int index = 0; index < paragraphs.Count; index++)
            {
                Microsoft.UI.Text.RichParagraphFormatState format = paragraphs[index].Format;
                AddColor(format.TableBorderBrush, colors, colorIndices);
                if (format.TableCellBackgrounds is not { } backgrounds) continue;
                for (int cell = 0; cell < backgrounds.Length; cell++)
                    AddColor(backgrounds[cell], colors, colorIndices);
            }
        }

        var builder = new StringBuilder();
        builder.Append(@"{\rtf1\ansi\deff0\uc1");
        if (spans.Count > 0 && TryGetLanguageCode(spans[0].Style.LanguageTag, out int defaultLanguageCode))
            builder.Append(@"\deflang").Append(defaultLanguageCode);
        if (paragraphs is { Count: > 0 } && paragraphs[0].Format.DefaultTabStop > 0f)
            AppendTwips(@"\deftab", paragraphs[0].Format.DefaultTabStop, builder, includeZero: true);
        builder.Append(@"{\fonttbl");
        for (int index = 0; index < fonts.Count; index++)
        {
            builder.Append(@"{\f").Append(index).Append(@"\fnil ");
            AppendEscaped(fonts[index], builder);
            builder.Append(";}");
        }
        builder.Append('}');
        if (colors.Count > 0)
        {
            builder.Append(@"{\colortbl;");
            foreach (RtfColor color in colors)
            {
                builder.Append(@"\red").Append(color.Red)
                    .Append(@"\green").Append(color.Green)
                    .Append(@"\blue").Append(color.Blue).Append(';');
            }
            builder.Append('}');
        }
        builder.Append(@"\viewkind4 ");
        int totalTextLength = 0;
        for (int index = 0; index < spans.Count; index++) totalTextLength += spans[index].Text.Length;
        int absolutePosition = 0;
        int tableCellIndex = 0;
        for (int index = 0; index < spans.Count; index++)
        {
            RichTextSpan span = spans[index];
            int segmentStart = 0;
            for (int characterIndex = 0; characterIndex <= span.Text.Length; characterIndex++)
            {
                bool paragraphBreak = characterIndex < span.Text.Length && span.Text[characterIndex] == '\n';
                if (!paragraphBreak && characterIndex < span.Text.Length) continue;
                ReadOnlySpan<char> segment = span.Text.AsSpan(segmentStart, characterIndex - segmentStart);
                if (segment.Length > 0 || paragraphBreak)
                {
                    ParagraphSpan? paragraph = FindParagraph(paragraphs, absolutePosition);
                    bool paragraphStart = paragraph is { } descriptor &&
                        absolutePosition == descriptor.Start;
                    bool paragraphEnd = paragraph is { } endingDescriptor &&
                        absolutePosition + segment.Length >= endingDescriptor.Start + endingDescriptor.Length &&
                        (paragraphBreak || endingDescriptor.Start + endingDescriptor.Length >= totalTextLength);
                    if (paragraph is { IsTableRow: true } tableParagraph && paragraphStart)
                    {
                        tableCellIndex = 0;
                        AppendParagraphFormat(tableParagraph.Format, builder);
                        AppendTableRowStart(tableParagraph, colorIndices, builder);
                    }
                    AppendStyledSegment(
                        segment,
                        paragraphBreak,
                        span.Style,
                        paragraph,
                        paragraphStart,
                        paragraphEnd,
                        fontIndices,
                        colorIndices,
                        ref tableCellIndex,
                        builder);
                }
                absolutePosition += segment.Length + (paragraphBreak ? 1 : 0);
                segmentStart = characterIndex + 1;
            }
        }
        if (includeFinalEop) builder.Append(@"\par ");
        builder.Append('}');
        return builder.ToString();
    }

    private static void AppendStyledSegment(
        ReadOnlySpan<char> text,
        bool paragraphBreak,
        RichTextStyle style,
        ParagraphSpan? paragraph,
        bool isParagraphStart,
        bool isParagraphEnd,
        IReadOnlyDictionary<string, int> fontIndices,
        IReadOnlyDictionary<RtfColor, int> colorIndices,
        ref int tableCellIndex,
        StringBuilder builder)
    {
        bool hyperlink = !string.IsNullOrWhiteSpace(style.Link);
        if (hyperlink)
        {
            builder.Append("{\\field{\\*\\fldinst HYPERLINK \"");
            AppendEscaped(style.Link!, builder);
            builder.Append("\"}{\\fldrslt ");
        }
        builder.Append('{');
        if (paragraph is { } descriptor)
        {
            if (descriptor.IsTableRow) builder.Append(@"\intbl ");
            else AppendParagraphFormat(descriptor.Format, builder);
        }
        if (text.Length == 1 && text[0] == '\uFFFC' && style.EmbeddedObject is { } embedded)
        {
            AppendPicture(embedded, builder);
            AppendStructuralBreak(paragraph, paragraphBreak, isParagraphEnd, ref tableCellIndex, builder);
            builder.Append('}');
            if (hyperlink) builder.Append("}}");
            return;
        }
        string? family = !string.IsNullOrWhiteSpace(style.FontName)
            ? style.FontName
            : style.Font?.FamilyName;
        if (!string.IsNullOrWhiteSpace(family) && fontIndices.TryGetValue(family, out int fontIndex))
            builder.Append(@"\f").Append(fontIndex).Append(' ');
        if (TryGetLanguageCode(style.LanguageTag, out int languageCode))
            builder.Append(@"\lang").Append(languageCode).Append(' ');
        if (style.FlowDirection == FlowDirection.RightToLeft) builder.Append(@"\rtlch ");
        else if (style.FlowDirection == FlowDirection.LeftToRight) builder.Append(@"\ltrch ");
        if (TryGetColor(style.Foreground, out RtfColor foreground) && colorIndices.TryGetValue(foreground, out int foregroundIndex))
            builder.Append(@"\cf").Append(foregroundIndex + 1).Append(' ');
        if (TryGetColor(style.Background, out RtfColor background) && colorIndices.TryGetValue(background, out int backgroundIndex))
            builder.Append(@"\highlight").Append(backgroundIndex + 1).Append(' ');
        if (style.IsBold) builder.Append(@"\b ");
        if (style.IsItalic) builder.Append(@"\i ");
        AppendUnderline(style, builder);
        if (style.IsStrikethrough) builder.Append(@"\strike ");
        if (style.IsHidden) builder.Append(@"\v ");
        if (style.IsProtected) builder.Append(@"\protect ");
        if (style.IsAllCaps) builder.Append(@"\caps ");
        if (style.IsSmallCaps) builder.Append(@"\scaps ");
        if (style.IsOutline) builder.Append(@"\outl ");
        if (style.IsSubscript) builder.Append(@"\sub ");
        else if (style.IsSuperscript) builder.Append(@"\super ");
        else if (style.BaselineOffset != 0f)
        {
            builder.Append(style.BaselineOffset > 0f ? @"\up" : @"\dn");
            builder.Append(Math.Max(1, (int)MathF.Round(MathF.Abs(style.BaselineOffset) * 2f)).ToString(CultureInfo.InvariantCulture));
            builder.Append(' ');
        }
        if (style.CharacterSpacing != 0f)
        {
            builder.Append(@"\expndtw");
            builder.Append(((int)MathF.Round(style.CharacterSpacing * 20f)).ToString(CultureInfo.InvariantCulture));
            builder.Append(' ');
        }
        if (style.FontSize > 0f && float.IsFinite(style.FontSize))
        {
            builder.Append(@"\fs");
            builder.Append(Math.Max(1, (int)MathF.Round(style.FontSize * 2f)).ToString(CultureInfo.InvariantCulture));
            builder.Append(' ');
        }
        if (paragraph is { IsTableRow: true } tableParagraph)
            AppendEscapedTableText(text, tableParagraph.Format, ref tableCellIndex, builder);
        else AppendEscaped(text, builder);
        AppendStructuralBreak(paragraph, paragraphBreak, isParagraphEnd, ref tableCellIndex, builder);
        builder.Append('}');
        if (hyperlink) builder.Append("}}");
    }

    public static RichTextSpan[] Decode(string? rtf, RichTextStyle defaultStyle) =>
        DecodeDocument(rtf, defaultStyle).Spans;

    internal static DecodedDocument DecodeDocument(string? rtf, RichTextStyle defaultStyle)
    {
        if (string.IsNullOrEmpty(rtf))
        {
            return new DecodedDocument
            {
                Spans = Array.Empty<RichTextSpan>(),
                Paragraphs = [new ParagraphSpan(0, 0, new Microsoft.UI.Text.RichParagraphFormatState())]
            };
        }
        if (!rtf.AsSpan().TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase))
        {
            return new DecodedDocument
            {
                Spans = [new RichTextSpan(rtf, defaultStyle)],
                Paragraphs = [new ParagraphSpan(0, rtf.Length, new Microsoft.UI.Text.RichParagraphFormatState())]
            };
        }

        var spans = new List<RichTextSpan>();
        var paragraphs = new List<ParagraphSpan>();
        var text = new StringBuilder();
        var stack = new Stack<ParserState>();
        IReadOnlyDictionary<int, string> fonts = ExtractFontTable(rtf);
        IReadOnlyList<RtfColor?> colors = ExtractColorTable(rtf);
        IReadOnlyDictionary<int, Microsoft.UI.Text.MarkerType[]> lists = ExtractListDefinitions(rtf);
        int defaultFontIndex = TryFindControlNumber(rtf, "deff", out int parsedDefaultFontIndex)
            ? parsedDefaultFontIndex
            : 0;
        RichTextStyle documentDefaultStyle = defaultStyle;
        if (fonts.TryGetValue(defaultFontIndex, out string? defaultFontName))
        {
            documentDefaultStyle = documentDefaultStyle with
            {
                FontName = defaultFontName,
                Font = FontApi.Manager.MatchFamily(defaultFontName) ?? documentDefaultStyle.Font
            };
        }
        if (TryFindControlNumber(rtf, "deflang", out int defaultLanguage) &&
            TryGetLanguageTag(defaultLanguage, out string? defaultLanguageTag))
        {
            documentDefaultStyle = documentDefaultStyle with { LanguageTag = defaultLanguageTag };
        }
        var documentDefaultParagraphFormat = new Microsoft.UI.Text.RichParagraphFormatState();
        if (TryFindControlNumber(rtf, "deftab", out int defaultTabTwips) && defaultTabTwips > 0)
            documentDefaultParagraphFormat.DefaultTabStop = defaultTabTwips / 20f;
        ParserState state = new(documentDefaultStyle, fonts, colors, lists)
        {
            FontIndex = defaultFontIndex,
            ParagraphFormat = documentDefaultParagraphFormat.Clone()
        };
        int fallbackCharacters = 0;
        string? pendingHyperlink = null;
        int outputLength = 0;
        int paragraphStart = 0;
        Microsoft.UI.Text.RichParagraphFormatState lastTextParagraphFormat = state.ParagraphFormat.Clone();
        bool lastTextIsTableRow = false;
        float[]? lastTextTableCellRightEdges = null;

        void Flush()
        {
            if (text.Length == 0) return;
            spans.Add(new RichTextSpan(text.ToString(), state.ToStyle(documentDefaultStyle)));
            outputLength += text.Length;
            lastTextParagraphFormat = state.ParagraphFormat.Clone();
            lastTextIsTableRow = state.IsTableRow;
            lastTextTableCellRightEdges = state.TableCellRightEdges.Count == 0
                ? null
                : state.TableCellRightEdges.ToArray();
            text.Clear();
        }

        void FinishParagraph(bool appendBreak)
        {
            Flush();
            paragraphs.Add(new ParagraphSpan(
                paragraphStart,
                outputLength - paragraphStart,
                lastTextParagraphFormat.Clone())
            {
                IsTableRow = state.IsTableRow || lastTextIsTableRow,
                TableCellRightEdges = state.TableCellRightEdges.Count > 0
                    ? state.TableCellRightEdges.ToArray()
                    : lastTextTableCellRightEdges
            });
            if (!appendBreak) return;
            text.Append('\n');
            Flush();
            paragraphStart = outputLength;
        }

        for (int index = 0; index < rtf.Length; index++)
        {
            char character = rtf[index];
            if (character == '{')
            {
                Flush();
                stack.Push(state);
                state.ParagraphFormat = state.ParagraphFormat.Clone();
                state.TableCellRightEdges = new List<float>(state.TableCellRightEdges);
                state.TableCellBackgrounds = new List<Brush?>(state.TableCellBackgrounds);
                state.TableCellMergeFlags = new List<byte>(state.TableCellMergeFlags);
                state.TableCellVerticalMergeFlags = new List<byte>(state.TableCellVerticalMergeFlags);
                continue;
            }
            if (character == '}')
            {
                Flush();
                if (stack.Count > 0)
                {
                    ParserState childState = state;
                    state = stack.Pop();
                    state.TableContentLogicalCellIndex = Math.Max(
                        state.TableContentLogicalCellIndex,
                        childState.TableContentLogicalCellIndex);
                    if (childState.IsListDefinition)
                    {
                        state.ParagraphFormat.ListType = childState.ParagraphFormat.ListType;
                        state.ParagraphFormat.ListStart = childState.ParagraphFormat.ListStart;
                        state.ParagraphFormat.ListLevelIndex = childState.ParagraphFormat.ListLevelIndex;
                    }
                }
                continue;
            }
            if (character != '\\')
            {
                if (state.SkipDestination || character is '\r' or '\n') continue;
                if (fallbackCharacters > 0)
                {
                    fallbackCharacters--;
                    continue;
                }
                text.Append(character);
                continue;
            }

            if (++index >= rtf.Length) break;
            character = rtf[index];
            if (character is '\\' or '{' or '}')
            {
                if (!state.SkipDestination)
                {
                    if (fallbackCharacters > 0) fallbackCharacters--;
                    else text.Append(character);
                }
                continue;
            }
            if (character == '\'')
            {
                if (index + 2 < rtf.Length &&
                    byte.TryParse(rtf.AsSpan(index + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
                {
                    index += 2;
                    if (!state.SkipDestination)
                    {
                        if (fallbackCharacters > 0) fallbackCharacters--;
                        else text.Append(DecodeWindows1252(value));
                    }
                }
                continue;
            }
            if (!char.IsLetter(character))
            {
                if (!state.SkipDestination && fallbackCharacters == 0)
                {
                    if (character == '~') text.Append('\u00A0');
                    else if (character == '_') text.Append('\u2011');
                }
                else if (fallbackCharacters > 0)
                {
                    fallbackCharacters--;
                }
                continue;
            }

            int wordStart = index;
            while (index + 1 < rtf.Length && char.IsLetter(rtf[index + 1])) index++;
            ReadOnlySpan<char> word = rtf.AsSpan(wordStart, index - wordStart + 1);
            bool negative = false;
            int number = 0;
            bool hasNumber = false;
            if (index + 1 < rtf.Length && rtf[index + 1] == '-')
            {
                negative = true;
                index++;
            }
            while (index + 1 < rtf.Length && char.IsDigit(rtf[index + 1]))
            {
                hasNumber = true;
                number = checked(number * 10 + (rtf[++index] - '0'));
            }
            if (negative) number = -number;
            if (index + 1 < rtf.Length && rtf[index + 1] == ' ') index++;

            if (word.SequenceEqual("pict"))
            {
                Flush();
                if (TryDecodePicture(rtf, index + 1, out RichTextEmbeddedObject? embedded))
                {
                    RichTextStyle pictureStyle = state.ToStyle(documentDefaultStyle) with { EmbeddedObject = embedded };
                    spans.Add(new RichTextSpan("\uFFFC", pictureStyle));
                    outputLength++;
                    lastTextParagraphFormat = state.ParagraphFormat.Clone();
                }
                state.SkipDestination = true;
                continue;
            }
            if (word.SequenceEqual("fonttbl") ||
                word.SequenceEqual("colortbl") ||
                word.SequenceEqual("stylesheet") ||
                word.SequenceEqual("listtable") ||
                word.SequenceEqual("listoverridetable") ||
                word.SequenceEqual("listtext") ||
                word.SequenceEqual("info") ||
                word.SequenceEqual("object") ||
                word.SequenceEqual("generator"))
            {
                Flush();
                state.SkipDestination = true;
                continue;
            }
            if (word.SequenceEqual("fldinst"))
            {
                Flush();
                pendingHyperlink = ExtractHyperlinkInstruction(rtf, index + 1);
                state.SkipDestination = true;
                continue;
            }
            if (word.SequenceEqual("fldrslt"))
            {
                Flush();
                state.Link = pendingHyperlink;
                pendingHyperlink = null;
                continue;
            }
            if (state.SkipDestination) continue;

            if (word.SequenceEqual("pard"))
            {
                Flush();
                float defaultTabStop = state.ParagraphFormat.DefaultTabStop;
                state.ParagraphFormat = new Microsoft.UI.Text.RichParagraphFormatState
                {
                    DefaultTabStop = defaultTabStop
                };
                state.ListOverrideIndex = 0;
            }
            else if (word.SequenceEqual("ql")) { Flush(); state.ParagraphFormat.Alignment = Microsoft.UI.Text.ParagraphAlignment.Left; }
            else if (word.SequenceEqual("qr")) { Flush(); state.ParagraphFormat.Alignment = Microsoft.UI.Text.ParagraphAlignment.Right; }
            else if (word.SequenceEqual("qc")) { Flush(); state.ParagraphFormat.Alignment = Microsoft.UI.Text.ParagraphAlignment.Center; }
            else if (word.SequenceEqual("qj")) { Flush(); state.ParagraphFormat.Alignment = Microsoft.UI.Text.ParagraphAlignment.Justify; }
            else if (word.SequenceEqual("rtlpar")) { Flush(); state.ParagraphFormat.RightToLeft = Microsoft.UI.Text.FormatEffect.On; }
            else if (word.SequenceEqual("ltrpar")) { Flush(); state.ParagraphFormat.RightToLeft = Microsoft.UI.Text.FormatEffect.Off; }
            else if (word.SequenceEqual("fi") && hasNumber) { Flush(); state.ParagraphFormat.FirstLineIndent = number / 20f; }
            else if (word.SequenceEqual("li") && hasNumber) { Flush(); state.ParagraphFormat.LeftIndent = number / 20f; }
            else if (word.SequenceEqual("ri") && hasNumber) { Flush(); state.ParagraphFormat.RightIndent = number / 20f; }
            else if (word.SequenceEqual("sb") && hasNumber) { Flush(); state.ParagraphFormat.SpaceBefore = number / 20f; }
            else if (word.SequenceEqual("sa") && hasNumber) { Flush(); state.ParagraphFormat.SpaceAfter = number / 20f; }
            else if (word.SequenceEqual("keep")) { Flush(); state.ParagraphFormat.KeepTogether = hasNumber && number == 0 ? Microsoft.UI.Text.FormatEffect.Off : Microsoft.UI.Text.FormatEffect.On; }
            else if (word.SequenceEqual("keepn")) { Flush(); state.ParagraphFormat.KeepWithNext = hasNumber && number == 0 ? Microsoft.UI.Text.FormatEffect.Off : Microsoft.UI.Text.FormatEffect.On; }
            else if (word.SequenceEqual("pagebb")) { Flush(); state.ParagraphFormat.PageBreakBefore = hasNumber && number == 0 ? Microsoft.UI.Text.FormatEffect.Off : Microsoft.UI.Text.FormatEffect.On; }
            else if (word.SequenceEqual("widctlpar")) { Flush(); state.ParagraphFormat.WidowControl = Microsoft.UI.Text.FormatEffect.On; }
            else if (word.SequenceEqual("nowidctlpar")) { Flush(); state.ParagraphFormat.WidowControl = Microsoft.UI.Text.FormatEffect.Off; }
            else if (word.SequenceEqual("deftab") && hasNumber) { state.ParagraphFormat.DefaultTabStop = Math.Max(1f, number / 20f); }
            else if (word.SequenceEqual("sl") && hasNumber) { Flush(); state.LineSpacingTwips = number; ApplyParsedLineSpacing(ref state); }
            else if (word.SequenceEqual("slmult") && hasNumber) { Flush(); state.LineSpacingMultiple = number != 0; ApplyParsedLineSpacing(ref state); }
            else if (word.SequenceEqual("tql")) { state.PendingTabAlignment = Microsoft.UI.Text.TabAlignment.Left; }
            else if (word.SequenceEqual("tqc")) { state.PendingTabAlignment = Microsoft.UI.Text.TabAlignment.Center; }
            else if (word.SequenceEqual("tqr")) { state.PendingTabAlignment = Microsoft.UI.Text.TabAlignment.Right; }
            else if (word.SequenceEqual("tqdec")) { state.PendingTabAlignment = Microsoft.UI.Text.TabAlignment.Decimal; }
            else if (word.SequenceEqual("tb")) { state.PendingTabAlignment = Microsoft.UI.Text.TabAlignment.Bar; }
            else if (word.SequenceEqual("tldot")) { state.PendingTabLeader = Microsoft.UI.Text.TabLeader.Dots; }
            else if (word.SequenceEqual("tlhyph")) { state.PendingTabLeader = Microsoft.UI.Text.TabLeader.Dashes; }
            else if (word.SequenceEqual("tlul")) { state.PendingTabLeader = Microsoft.UI.Text.TabLeader.Lines; }
            else if (word.SequenceEqual("tlth")) { state.PendingTabLeader = Microsoft.UI.Text.TabLeader.ThickLines; }
            else if (word.SequenceEqual("tleq")) { state.PendingTabLeader = Microsoft.UI.Text.TabLeader.Equals; }
            else if (word.SequenceEqual("tx") && hasNumber)
            {
                Flush();
                state.ParagraphFormat.Tabs.Add(new Microsoft.UI.Text.RichTextTab(
                    number / 20f,
                    state.PendingTabAlignment,
                    state.PendingTabLeader));
                state.PendingTabAlignment = Microsoft.UI.Text.TabAlignment.Left;
                state.PendingTabLeader = Microsoft.UI.Text.TabLeader.Spaces;
            }
            else if (word.SequenceEqual("pn")) { state.IsListDefinition = true; }
            else if (word.SequenceEqual("pnlvlblt")) { state.ParagraphFormat.ListType = Microsoft.UI.Text.MarkerType.Bullet; }
            else if (word.SequenceEqual("pnlvlbody") || word.SequenceEqual("pndec")) { state.ParagraphFormat.ListType = Microsoft.UI.Text.MarkerType.Arabic; }
            else if (word.SequenceEqual("pnlcltr")) { state.ParagraphFormat.ListType = Microsoft.UI.Text.MarkerType.LowercaseEnglishLetter; }
            else if (word.SequenceEqual("pnucltr")) { state.ParagraphFormat.ListType = Microsoft.UI.Text.MarkerType.UppercaseEnglishLetter; }
            else if (word.SequenceEqual("pnlcrm")) { state.ParagraphFormat.ListType = Microsoft.UI.Text.MarkerType.LowercaseRoman; }
            else if (word.SequenceEqual("pnucrm")) { state.ParagraphFormat.ListType = Microsoft.UI.Text.MarkerType.UppercaseRoman; }
            else if (word.SequenceEqual("pnstart") && hasNumber) { state.ParagraphFormat.ListStart = number; }
            else if (word.SequenceEqual("ls") && hasNumber)
            {
                state.ListOverrideIndex = number;
                state.ParagraphFormat.ListType = ResolveListType(state.Lists, number, state.ParagraphFormat.ListLevelIndex);
            }
            else if (word.SequenceEqual("ilvl") && hasNumber)
            {
                state.ParagraphFormat.ListLevelIndex = Math.Max(0, number);
                if (state.ListOverrideIndex != 0)
                    state.ParagraphFormat.ListType = ResolveListType(
                        state.Lists,
                        state.ListOverrideIndex,
                        state.ParagraphFormat.ListLevelIndex);
            }
            else if (word.SequenceEqual("trowd"))
            {
                Flush();
                state.IsTableRow = true;
                state.TableCellRightEdges.Clear();
                state.TableCellBackgrounds.Clear();
                state.TableCellMergeFlags.Clear();
                state.TableCellVerticalMergeFlags.Clear();
                state.PendingTableCellBackgroundColorIndex = 0;
                state.HasPendingTableCellBackground = false;
                state.PendingTableCellMergeFlag = 0;
                state.PendingTableCellVerticalMergeFlag = 0;
                state.TableContentLogicalCellIndex = 0;
                state.ParsingTableCellBorder = false;
                state.ParagraphFormat.IsTableRow = true;
                state.ParagraphFormat.TableCellRightEdges = null;
                state.ParagraphFormat.TableCellBackgrounds = null;
                state.ParagraphFormat.TableCellColumnSpans = null;
                state.ParagraphFormat.TableCellVerticalMergeFlags = null;
                state.ParagraphFormat.TableCellPadding = 0f;
                state.ParagraphFormat.TableBorderThickness = 0f;
                state.ParagraphFormat.TableBorderBrush = null;
                state.ParagraphFormat.RightToLeft = Microsoft.UI.Text.FormatEffect.Undefined;
            }
            else if (word.SequenceEqual("intbl")) { state.IsTableRow = true; }
            else if (word.SequenceEqual("rtlrow"))
            {
                state.ParagraphFormat.RightToLeft = Microsoft.UI.Text.FormatEffect.On;
            }
            else if (word.SequenceEqual("ltrrow"))
            {
                state.ParagraphFormat.RightToLeft = Microsoft.UI.Text.FormatEffect.Off;
            }
            else if (hasNumber &&
                (word.SequenceEqual("clpadl") || word.SequenceEqual("clpadr") ||
                 word.SequenceEqual("clpadt") || word.SequenceEqual("clpadb")))
            {
                state.ParagraphFormat.TableCellPadding = Math.Max(0f, number / 20f);
            }
            else if (word.SequenceEqual("clbrdrt") || word.SequenceEqual("clbrdrl") ||
                     word.SequenceEqual("clbrdrb") || word.SequenceEqual("clbrdrr"))
            {
                state.ParsingTableCellBorder = true;
            }
            else if (word.SequenceEqual("brdrw") && hasNumber && state.ParsingTableCellBorder)
            {
                state.ParagraphFormat.TableBorderThickness = Math.Max(
                    state.ParagraphFormat.TableBorderThickness,
                    Math.Max(0f, number / 20f));
            }
            else if (word.SequenceEqual("brdrcf") && hasNumber && state.ParsingTableCellBorder)
            {
                state.ParagraphFormat.TableBorderBrush = state.ResolveTableColor(number);
            }
            else if (word.SequenceEqual("clcbpat") && hasNumber)
            {
                state.PendingTableCellBackgroundColorIndex = number;
                state.HasPendingTableCellBackground = true;
            }
            else if (word.SequenceEqual("clmgf"))
            {
                state.PendingTableCellMergeFlag = 1;
            }
            else if (word.SequenceEqual("clmrg"))
            {
                state.PendingTableCellMergeFlag = 2;
            }
            else if (word.SequenceEqual("clvmgf"))
            {
                state.PendingTableCellVerticalMergeFlag = 1;
            }
            else if (word.SequenceEqual("clvmrg"))
            {
                state.PendingTableCellVerticalMergeFlag = 2;
            }
            else if (word.SequenceEqual("cellx") && hasNumber)
            {
                state.IsTableRow = true;
                state.TableCellRightEdges.Add(number / 20f);
                state.TableCellBackgrounds.Add(state.HasPendingTableCellBackground
                    ? state.ResolveTableColor(state.PendingTableCellBackgroundColorIndex)
                    : null);
                state.TableCellMergeFlags.Add(state.PendingTableCellMergeFlag);
                state.TableCellVerticalMergeFlags.Add(state.PendingTableCellVerticalMergeFlag);
                state.PendingTableCellBackgroundColorIndex = 0;
                state.HasPendingTableCellBackground = false;
                state.PendingTableCellMergeFlag = 0;
                state.PendingTableCellVerticalMergeFlag = 0;
                state.ParsingTableCellBorder = false;
                state.ParagraphFormat.IsTableRow = true;
                state.ParagraphFormat.TableCellRightEdges = state.TableCellRightEdges.ToArray();
                ApplyParsedTableCellMetadata(ref state);
            }
            else if (word.SequenceEqual("cell"))
            {
                int nextCell = ++state.TableContentLogicalCellIndex;
                bool nextIsMergeContinuation = nextCell < state.TableCellMergeFlags.Count &&
                    state.TableCellMergeFlags[nextCell] == 2;
                if (!nextIsMergeContinuation) text.Append('\t');
            }
            else if (word.SequenceEqual("row"))
            {
                Flush();
                RemoveTrailingCharacter(spans, ref outputLength, '\t');
                FinishParagraph(appendBreak: true);
                state.IsTableRow = false;
                state.TableCellRightEdges.Clear();
                state.TableCellBackgrounds.Clear();
                state.TableCellMergeFlags.Clear();
                state.TableCellVerticalMergeFlags.Clear();
                state.TableContentLogicalCellIndex = 0;
                state.ParagraphFormat.IsTableRow = false;
                state.ParagraphFormat.TableCellRightEdges = null;
                state.ParagraphFormat.TableCellBackgrounds = null;
                state.ParagraphFormat.TableCellColumnSpans = null;
                state.ParagraphFormat.TableCellVerticalMergeFlags = null;
            }
            else if (word.SequenceEqual("b"))
            {
                Flush();
                state.Bold = !hasNumber || number != 0;
            }
            else if (word.SequenceEqual("i"))
            {
                Flush();
                state.Italic = !hasNumber || number != 0;
            }
            else if (word.SequenceEqual("ul"))
            {
                Flush();
                state.Underline = !hasNumber || number != 0;
                state.UnderlineType = state.Underline
                    ? Microsoft.UI.Text.UnderlineType.Single
                    : Microsoft.UI.Text.UnderlineType.None;
            }
            else if (word.SequenceEqual("ulnone"))
            {
                Flush();
                state.Underline = false;
                state.UnderlineType = Microsoft.UI.Text.UnderlineType.None;
            }
            else if (word.SequenceEqual("uldb")) { Flush(); state.Underline = true; state.UnderlineType = Microsoft.UI.Text.UnderlineType.Double; }
            else if (word.SequenceEqual("uld")) { Flush(); state.Underline = true; state.UnderlineType = Microsoft.UI.Text.UnderlineType.Dotted; }
            else if (word.SequenceEqual("uldash")) { Flush(); state.Underline = true; state.UnderlineType = Microsoft.UI.Text.UnderlineType.Dash; }
            else if (word.SequenceEqual("ulwave")) { Flush(); state.Underline = true; state.UnderlineType = Microsoft.UI.Text.UnderlineType.Wave; }
            else if (word.SequenceEqual("strike")) { Flush(); state.Strikethrough = !hasNumber || number != 0; }
            else if (word.SequenceEqual("v")) { Flush(); state.Hidden = !hasNumber || number != 0; }
            else if (word.SequenceEqual("protect")) { Flush(); state.Protected = !hasNumber || number != 0; }
            else if (word.SequenceEqual("caps")) { Flush(); state.AllCaps = !hasNumber || number != 0; }
            else if (word.SequenceEqual("scaps")) { Flush(); state.SmallCaps = !hasNumber || number != 0; }
            else if (word.SequenceEqual("outl")) { Flush(); state.Outline = !hasNumber || number != 0; }
            else if (word.SequenceEqual("rtlch")) { Flush(); state.FlowDirection = FlowDirection.RightToLeft; }
            else if (word.SequenceEqual("ltrch")) { Flush(); state.FlowDirection = FlowDirection.LeftToRight; }
            else if (word.SequenceEqual("sub")) { Flush(); state.Subscript = true; state.Superscript = false; }
            else if (word.SequenceEqual("super")) { Flush(); state.Superscript = true; state.Subscript = false; }
            else if (word.SequenceEqual("nosupersub")) { Flush(); state.Superscript = false; state.Subscript = false; }
            else if (word.SequenceEqual("up") && hasNumber) { Flush(); state.BaselineOffset = number * 0.5f; }
            else if (word.SequenceEqual("dn") && hasNumber) { Flush(); state.BaselineOffset = -number * 0.5f; }
            else if (word.SequenceEqual("expndtw") && hasNumber) { Flush(); state.CharacterSpacing = number / 20f; }
            else if (word.SequenceEqual("plain"))
            {
                Flush();
                Microsoft.UI.Text.RichParagraphFormatState paragraphFormat = state.ParagraphFormat;
                state = new ParserState(documentDefaultStyle, fonts, colors, lists)
                {
                    SkipDestination = state.SkipDestination,
                    ParagraphFormat = paragraphFormat,
                    FontIndex = defaultFontIndex
                };
            }
            else if (word.SequenceEqual("fs") && hasNumber && number > 0)
            {
                Flush();
                state.FontSize = number * 0.5f;
            }
            else if (word.SequenceEqual("f") && hasNumber)
            {
                Flush();
                state.FontIndex = number;
            }
            else if (word.SequenceEqual("cf") && hasNumber)
            {
                Flush();
                state.ForegroundColorIndex = number;
            }
            else if (word.SequenceEqual("highlight") && hasNumber)
            {
                Flush();
                state.BackgroundColorIndex = number;
            }
            else if (word.SequenceEqual("lang") && hasNumber)
            {
                Flush();
                state.LanguageTag = TryGetLanguageTag(number, out string? languageTag)
                    ? languageTag
                    : documentDefaultStyle.LanguageTag;
            }
            else if (word.SequenceEqual("par"))
            {
                FinishParagraph(appendBreak: true);
            }
            else if (word.SequenceEqual("line"))
            {
                text.Append('\n');
            }
            else if (word.SequenceEqual("tab"))
            {
                text.Append('\t');
            }
            else if (word.SequenceEqual("uc") && hasNumber)
            {
                state.UnicodeSkipCount = Math.Max(0, number);
            }
            else if (word.SequenceEqual("u") && hasNumber)
            {
                text.Append((char)(ushort)(short)number);
                fallbackCharacters = state.UnicodeSkipCount;
            }
        }

        Flush();
        if (paragraphs.Count == 0 || paragraphStart < outputLength ||
            (outputLength > 0 && spans.Count > 0 && !spans[^1].Text.EndsWith('\n')))
        {
            paragraphs.Add(new ParagraphSpan(
                paragraphStart,
                outputLength - paragraphStart,
                lastTextParagraphFormat.Clone())
            {
                IsTableRow = lastTextIsTableRow,
                TableCellRightEdges = lastTextTableCellRightEdges
            });
        }
        else if (paragraphStart == outputLength && outputLength > 0 && spans[^1].Text.EndsWith('\n'))
        {
            paragraphs.Add(new ParagraphSpan(
                paragraphStart,
                0,
                lastTextParagraphFormat.Clone())
            {
                IsTableRow = lastTextIsTableRow,
                TableCellRightEdges = lastTextTableCellRightEdges
            });
        }
        return new DecodedDocument
        {
            Spans = Coalesce(spans),
            Paragraphs = paragraphs.ToArray(),
            DocumentDefaultStyle = documentDefaultStyle,
            DocumentDefaultParagraphFormat = documentDefaultParagraphFormat
        };
    }

    private static bool TryGetLanguageTag(int languageCode, out string? languageTag)
    {
        try
        {
            languageTag = CultureInfo.GetCultureInfo(languageCode).Name;
            return !string.IsNullOrWhiteSpace(languageTag);
        }
        catch (CultureNotFoundException)
        {
            languageTag = null;
            return false;
        }
    }

    private static bool TryGetLanguageCode(string? languageTag, out int languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            languageCode = 0;
            return false;
        }
        try
        {
            languageCode = CultureInfo.GetCultureInfo(languageTag).LCID;
            return languageCode > 0;
        }
        catch (CultureNotFoundException)
        {
            languageCode = 0;
            return false;
        }
    }

    private static ParagraphSpan? FindParagraph(
        IReadOnlyList<ParagraphSpan>? paragraphs,
        int position)
    {
        if (paragraphs is null || paragraphs.Count == 0) return null;
        int low = 0;
        int high = paragraphs.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            if (paragraphs[middle].Start <= position) low = middle + 1;
            else high = middle - 1;
        }
        return paragraphs[Math.Clamp(high, 0, paragraphs.Count - 1)];
    }

    private static void AppendTableRowStart(
        ParagraphSpan paragraph,
        IReadOnlyDictionary<RtfColor, int> colorIndices,
        StringBuilder builder)
    {
        builder.Append(@"\trowd\intbl");
        Microsoft.UI.Text.RichParagraphFormatState format = paragraph.Format;
        if (format.RightToLeft == Microsoft.UI.Text.FormatEffect.On) builder.Append(@"\rtlrow");
        else if (format.RightToLeft == Microsoft.UI.Text.FormatEffect.Off) builder.Append(@"\ltrrow");
        float[]? rightEdges = paragraph.TableCellRightEdges;
        if (rightEdges is null || rightEdges.Length == 0)
        {
            AppendTableCellDefinition(format, 0, colorIndices, builder);
            builder.Append(@"\cellx2400");
            return;
        }
        for (int index = 0; index < rightEdges.Length; index++)
        {
            int actualCell = 0;
            int cellOffset = 0;
            GetTableCellForLogicalColumn(format, index, out actualCell, out cellOffset);
            int span = GetTableCellColumnSpan(format, actualCell);
            if (span > 1) builder.Append(cellOffset == 0 ? @"\clmgf" : @"\clmrg");
            byte verticalMerge = GetTableCellVerticalMergeFlag(format, actualCell);
            if (verticalMerge == 1) builder.Append(@"\clvmgf");
            else if (verticalMerge >= 2) builder.Append(@"\clvmrg");
            AppendTableCellDefinition(format, actualCell, colorIndices, builder);
            AppendTwips(@"\cellx", rightEdges[index], builder, includeZero: true);
        }
        builder.Append(' ');
    }

    private static void AppendTableCellDefinition(
        Microsoft.UI.Text.RichParagraphFormatState format,
        int cell,
        IReadOnlyDictionary<RtfColor, int> colorIndices,
        StringBuilder builder)
    {
        if (format.TableCellPadding > 0f)
        {
            int paddingTwips = Math.Max(0, (int)MathF.Round(format.TableCellPadding * 20f));
            builder.Append(@"\clpadl").Append(paddingTwips).Append(@"\clpadfl3")
                .Append(@"\clpadr").Append(paddingTwips).Append(@"\clpadfr3")
                .Append(@"\clpadt").Append(paddingTwips).Append(@"\clpadft3")
                .Append(@"\clpadb").Append(paddingTwips).Append(@"\clpadfb3");
        }
        if (format.TableBorderThickness > 0f &&
            TryGetColor(format.TableBorderBrush, out RtfColor border) &&
            colorIndices.TryGetValue(border, out int borderIndex))
        {
            int widthTwips = Math.Max(1, (int)MathF.Round(format.TableBorderThickness * 20f));
            AppendCellBorder(@"\clbrdrt", widthTwips, borderIndex + 1, builder);
            AppendCellBorder(@"\clbrdrl", widthTwips, borderIndex + 1, builder);
            AppendCellBorder(@"\clbrdrb", widthTwips, borderIndex + 1, builder);
            AppendCellBorder(@"\clbrdrr", widthTwips, borderIndex + 1, builder);
        }
        if (format.TableCellBackgrounds is { } backgrounds &&
            cell < backgrounds.Length &&
            TryGetColor(backgrounds[cell], out RtfColor background) &&
            colorIndices.TryGetValue(background, out int backgroundIndex))
        {
            builder.Append(@"\clcbpat").Append(backgroundIndex + 1);
        }
    }

    private static void AppendCellBorder(
        string side,
        int widthTwips,
        int colorIndex,
        StringBuilder builder) =>
        builder.Append(side).Append(@"\brdrs\brdrw").Append(widthTwips)
            .Append(@"\brdrcf").Append(colorIndex);

    private static void AppendStructuralBreak(
        ParagraphSpan? paragraph,
        bool paragraphBreak,
        bool isParagraphEnd,
        ref int tableCellIndex,
        StringBuilder builder)
    {
        if (paragraph is { IsTableRow: true } tableParagraph && isParagraphEnd)
        {
            AppendTableCellTerminators(
                GetTableCellColumnSpan(tableParagraph.Format, tableCellIndex),
                builder);
            tableCellIndex++;
            builder.Append(@"\row ");
            return;
        }
        if (paragraphBreak) builder.Append(@"\par ");
    }

    private static void AppendEscapedTableText(
        ReadOnlySpan<char> text,
        Microsoft.UI.Text.RichParagraphFormatState format,
        ref int tableCellIndex,
        StringBuilder builder)
    {
        int segmentStart = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] != '\t') continue;
            AppendEscaped(text[segmentStart..index], builder);
            AppendTableCellTerminators(GetTableCellColumnSpan(format, tableCellIndex), builder);
            tableCellIndex++;
            segmentStart = index + 1;
        }
        AppendEscaped(text[segmentStart..], builder);
    }

    private static int GetTableCellColumnSpan(
        Microsoft.UI.Text.RichParagraphFormatState format,
        int cell) => format.TableCellColumnSpans is { } spans && cell < spans.Length
            ? Math.Max(1, spans[cell])
            : 1;

    private static byte GetTableCellVerticalMergeFlag(
        Microsoft.UI.Text.RichParagraphFormatState format,
        int cell) => format.TableCellVerticalMergeFlags is { } flags && cell < flags.Length
            ? flags[cell]
            : (byte)0;

    private static void GetTableCellForLogicalColumn(
        Microsoft.UI.Text.RichParagraphFormatState format,
        int logicalColumn,
        out int cell,
        out int offset)
    {
        int logicalStart = 0;
        int count = format.TableCellColumnSpans?.Length ?? logicalColumn + 1;
        for (cell = 0; cell < count; cell++)
        {
            int span = GetTableCellColumnSpan(format, cell);
            if (logicalColumn < logicalStart + span)
            {
                offset = logicalColumn - logicalStart;
                return;
            }
            logicalStart += span;
        }
        cell = logicalColumn;
        offset = 0;
    }

    private static void AppendTableCellTerminators(int count, StringBuilder builder)
    {
        for (int index = 0; index < Math.Max(1, count); index++) builder.Append(@"\cell ");
    }

    private static void AppendParagraphFormat(
        Microsoft.UI.Text.RichParagraphFormatState format,
        StringBuilder builder)
    {
        builder.Append(@"\pard");
        builder.Append(format.Alignment switch
        {
            Microsoft.UI.Text.ParagraphAlignment.Center => @"\qc",
            Microsoft.UI.Text.ParagraphAlignment.Right => @"\qr",
            Microsoft.UI.Text.ParagraphAlignment.Justify => @"\qj",
            _ => @"\ql"
        });
        if (format.RightToLeft == Microsoft.UI.Text.FormatEffect.On) builder.Append(@"\rtlpar");
        else if (format.RightToLeft == Microsoft.UI.Text.FormatEffect.Off) builder.Append(@"\ltrpar");
        AppendTwips(@"\fi", format.FirstLineIndent, builder);
        AppendTwips(@"\li", format.LeftIndent, builder);
        AppendTwips(@"\ri", format.RightIndent, builder);
        AppendTwips(@"\sb", format.SpaceBefore, builder);
        AppendTwips(@"\sa", format.SpaceAfter, builder);
        if (format.KeepTogether == Microsoft.UI.Text.FormatEffect.On) builder.Append(@"\keep");
        if (format.KeepWithNext == Microsoft.UI.Text.FormatEffect.On) builder.Append(@"\keepn");
        if (format.PageBreakBefore == Microsoft.UI.Text.FormatEffect.On) builder.Append(@"\pagebb");
        if (format.WidowControl == Microsoft.UI.Text.FormatEffect.Off) builder.Append(@"\nowidctlpar");
        else if (format.WidowControl == Microsoft.UI.Text.FormatEffect.On) builder.Append(@"\widctlpar");
        AppendLineSpacing(format, builder);
        foreach (Microsoft.UI.Text.RichTextTab tab in format.Tabs)
        {
            builder.Append(tab.Alignment switch
            {
                Microsoft.UI.Text.TabAlignment.Center => @"\tqc",
                Microsoft.UI.Text.TabAlignment.Right => @"\tqr",
                Microsoft.UI.Text.TabAlignment.Decimal => @"\tqdec",
                Microsoft.UI.Text.TabAlignment.Bar => @"\tb",
                _ => @"\tql"
            });
            builder.Append(tab.Leader switch
            {
                Microsoft.UI.Text.TabLeader.Dots => @"\tldot",
                Microsoft.UI.Text.TabLeader.Dashes => @"\tlhyph",
                Microsoft.UI.Text.TabLeader.Lines => @"\tlul",
                Microsoft.UI.Text.TabLeader.ThickLines => @"\tlth",
                Microsoft.UI.Text.TabLeader.Equals => @"\tleq",
                _ => string.Empty
            });
            AppendTwips(@"\tx", tab.Position, builder, includeZero: true);
        }
        if (format.ListType != Microsoft.UI.Text.MarkerType.None &&
            format.ListType != Microsoft.UI.Text.MarkerType.Undefined)
        {
            builder.Append(@"{\pn");
            builder.Append(format.ListType switch
            {
                Microsoft.UI.Text.MarkerType.Bullet or
                Microsoft.UI.Text.MarkerType.BlackCircleWingding or
                Microsoft.UI.Text.MarkerType.WhiteCircleWingding => @"\pnlvlblt",
                _ => @"\pnlvlbody"
            });
            builder.Append(format.ListType switch
            {
                Microsoft.UI.Text.MarkerType.LowercaseEnglishLetter => @"\pnlcltr",
                Microsoft.UI.Text.MarkerType.UppercaseEnglishLetter => @"\pnucltr",
                Microsoft.UI.Text.MarkerType.LowercaseRoman => @"\pnlcrm",
                Microsoft.UI.Text.MarkerType.UppercaseRoman => @"\pnucrm",
                _ => @"\pndec"
            });
            builder.Append(@"\pnstart").Append(Math.Max(1, format.ListStart));
            builder.Append('}');
            builder.Append(@"\ilvl").Append(Math.Max(0, format.ListLevelIndex));
        }
        builder.Append(' ');
    }

    private static void AppendLineSpacing(
        Microsoft.UI.Text.RichParagraphFormatState format,
        StringBuilder builder)
    {
        int twips = format.LineSpacingRule switch
        {
            Microsoft.UI.Text.LineSpacingRule.OneAndHalf => 360,
            Microsoft.UI.Text.LineSpacingRule.Double => 480,
            Microsoft.UI.Text.LineSpacingRule.Multiple =>
                Math.Max(1, (int)MathF.Round(format.LineSpacing * 240f)),
            Microsoft.UI.Text.LineSpacingRule.Exactly =>
                -Math.Max(1, (int)MathF.Round(MathF.Abs(format.LineSpacing) * 20f)),
            Microsoft.UI.Text.LineSpacingRule.AtLeast =>
                Math.Max(1, (int)MathF.Round(MathF.Abs(format.LineSpacing) * 20f)),
            _ => 0
        };
        if (twips == 0) return;
        builder.Append(@"\sl").Append(twips);
        builder.Append(format.LineSpacingRule is Microsoft.UI.Text.LineSpacingRule.OneAndHalf or
            Microsoft.UI.Text.LineSpacingRule.Double or Microsoft.UI.Text.LineSpacingRule.Multiple
            ? @"\slmult1"
            : @"\slmult0");
    }

    private static void AppendPicture(RichTextEmbeddedObject embedded, StringBuilder builder)
    {
        ReadOnlySpan<byte> data = embedded.Data.Span;
        builder.Append(@"{\pict");
        builder.Append(GetPictureControl(data));
        builder.Append(@"\picw").Append(Math.Max(1, embedded.Width));
        builder.Append(@"\pich").Append(Math.Max(1, embedded.Height));
        AppendTwips(@"\picwgoal", Math.Max(1, embedded.Width), builder, includeZero: true);
        AppendTwips(@"\pichgoal", Math.Max(1, embedded.Height), builder, includeZero: true);
        if (!string.IsNullOrEmpty(embedded.AlternateText))
        {
            builder.Append(@"{\*\progpualt ");
            AppendHex(Encoding.UTF8.GetBytes(embedded.AlternateText), builder);
            builder.Append('}');
        }
        builder.AppendLine();
        AppendHex(data, builder);
        builder.Append('}');
    }

    private static string GetPictureControl(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return @"\pngblip";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return @"\jpegblip";
        return @"\pngblip";
    }

    private static void AppendHex(ReadOnlySpan<byte> data, StringBuilder builder)
    {
        const string digits = "0123456789abcdef";
        for (int index = 0; index < data.Length; index++)
        {
            byte value = data[index];
            builder.Append(digits[value >> 4]);
            builder.Append(digits[value & 0x0F]);
            if ((index & 31) == 31) builder.AppendLine();
        }
    }

    private static bool TryDecodePicture(
        string source,
        int start,
        out RichTextEmbeddedObject? embedded)
    {
        int depth = 0;
        int width = 0;
        int height = 0;
        int widthGoal = 0;
        int heightGoal = 0;
        var payload = new List<byte>();
        int highNibble = -1;
        int end = start;
        for (; end < source.Length; end++)
        {
            char character = source[end];
            if (character == '{' && !IsEscaped(source, end))
            {
                depth++;
                continue;
            }
            if (character == '}' && !IsEscaped(source, end))
            {
                if (depth == 0) break;
                depth--;
                continue;
            }
            if (depth != 0) continue;
            if (character == '\\')
            {
                int wordStart = ++end;
                while (end < source.Length && char.IsLetter(source[end])) end++;
                ReadOnlySpan<char> word = source.AsSpan(wordStart, end - wordStart);
                bool negative = end < source.Length && source[end] == '-';
                if (negative) end++;
                int number = 0;
                bool hasNumber = false;
                while (end < source.Length && char.IsDigit(source[end]))
                {
                    hasNumber = true;
                    number = checked(number * 10 + source[end++] - '0');
                }
                if (negative) number = -number;
                if (hasNumber)
                {
                    if (word.SequenceEqual("picw")) width = number;
                    else if (word.SequenceEqual("pich")) height = number;
                    else if (word.SequenceEqual("picwgoal")) widthGoal = number;
                    else if (word.SequenceEqual("pichgoal")) heightGoal = number;
                }
                if (end < source.Length && source[end] != ' ') end--;
                continue;
            }
            if (char.IsWhiteSpace(character)) continue;
            int nibble = HexNibble(character);
            if (nibble < 0) continue;
            if (highNibble < 0) highNibble = nibble;
            else
            {
                payload.Add((byte)((highNibble << 4) | nibble));
                highNibble = -1;
            }
        }
        if (payload.Count == 0)
        {
            embedded = null;
            return false;
        }

        string alternateText = DecodePictureAlternateText(source.AsSpan(start, Math.Max(0, end - start)));
        int logicalWidth = widthGoal != 0 ? Math.Max(1, (int)MathF.Round(widthGoal / 20f)) : Math.Max(1, width);
        int logicalHeight = heightGoal != 0 ? Math.Max(1, (int)MathF.Round(heightGoal / 20f)) : Math.Max(1, height);
        embedded = new RichTextEmbeddedObject(
            logicalWidth,
            logicalHeight,
            logicalHeight,
            Microsoft.UI.Text.VerticalCharacterAlignment.Baseline,
            alternateText,
            payload.ToArray());
        return true;
    }

    private static string DecodePictureAlternateText(ReadOnlySpan<char> picture)
    {
        ReadOnlySpan<char> marker = @"{\*\progpualt ";
        int start = picture.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return "Image";
        start += marker.Length;
        int end = picture[start..].IndexOf('}');
        if (end < 0) return "Image";
        ReadOnlySpan<char> encoded = picture.Slice(start, end);
        var bytes = new List<byte>(encoded.Length / 2);
        int high = -1;
        foreach (char character in encoded)
        {
            int nibble = HexNibble(character);
            if (nibble < 0) continue;
            if (high < 0) high = nibble;
            else
            {
                bytes.Add((byte)((high << 4) | nibble));
                high = -1;
            }
        }
        return bytes.Count == 0 ? "Image" : Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static int HexNibble(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1
    };

    private static void AppendTwips(
        string control,
        float value,
        StringBuilder builder,
        bool includeZero = false)
    {
        if (!float.IsFinite(value) || (!includeZero && value == 0f)) return;
        builder.Append(control).Append((int)MathF.Round(value * 20f));
    }

    private static void AppendEscaped(ReadOnlySpan<char> text, StringBuilder builder)
    {
        foreach (char character in text)
        {
            switch (character)
            {
                case '\\': builder.Append(@"\\"); break;
                case '{': builder.Append(@"\{"); break;
                case '}': builder.Append(@"\}"); break;
                case '\n': builder.Append(@"\par "); break;
                case '\r': break;
                case '\t': builder.Append(@"\tab "); break;
                default:
                    if (character is >= ' ' and <= '~') builder.Append(character);
                    else
                    {
                        builder.Append(@"\u");
                        builder.Append(((short)character).ToString(CultureInfo.InvariantCulture));
                        builder.Append('?');
                    }
                    break;
            }
        }
    }

    private static void AppendUnderline(RichTextStyle style, StringBuilder builder)
    {
        if (!style.IsUnderline) return;
        builder.Append(style.UnderlineType switch
        {
            Microsoft.UI.Text.UnderlineType.Double => @"\uldb ",
            Microsoft.UI.Text.UnderlineType.Dotted or Microsoft.UI.Text.UnderlineType.ThickDotted => @"\uld ",
            Microsoft.UI.Text.UnderlineType.Dash or Microsoft.UI.Text.UnderlineType.LongDash or Microsoft.UI.Text.UnderlineType.ThickDash => @"\uldash ",
            Microsoft.UI.Text.UnderlineType.Wave or Microsoft.UI.Text.UnderlineType.DoubleWave or Microsoft.UI.Text.UnderlineType.HeavyWave => @"\ulwave ",
            _ => @"\ul "
        });
    }

    private static void AddColor(
        Brush? brush,
        List<RtfColor> colors,
        Dictionary<RtfColor, int> indices)
    {
        if (!TryGetColor(brush, out RtfColor color) || indices.ContainsKey(color)) return;
        indices.Add(color, colors.Count);
        colors.Add(color);
    }

    private static bool TryGetColor(Brush? brush, out RtfColor color)
    {
        if (brush is SolidColorBrush solid)
        {
            Vector4 value = Vector4.Clamp(solid.Color, Vector4.Zero, Vector4.One);
            color = new RtfColor(
                (byte)MathF.Round(value.X * 255f),
                (byte)MathF.Round(value.Y * 255f),
                (byte)MathF.Round(value.Z * 255f));
            return true;
        }
        color = default;
        return false;
    }

    private static IReadOnlyDictionary<int, string> ExtractFontTable(string rtf)
    {
        var result = new Dictionary<int, string>();
        if (!TryExtractDestination(rtf, @"{\fonttbl", out ReadOnlySpan<char> table)) return result;
        for (int index = 0; index < table.Length - 2; index++)
        {
            if (table[index] != '\\' || table[index + 1] != 'f' || !char.IsDigit(table[index + 2])) continue;
            int cursor = index + 2;
            int fontIndex = 0;
            while (cursor < table.Length && char.IsDigit(table[cursor]))
                fontIndex = checked(fontIndex * 10 + table[cursor++] - '0');
            int semicolon = table[cursor..].IndexOf(';');
            if (semicolon < 0) break;
            ReadOnlySpan<char> entry = table.Slice(cursor, semicolon);
            var name = new StringBuilder();
            for (int entryIndex = 0; entryIndex < entry.Length; entryIndex++)
            {
                char character = entry[entryIndex];
                if (character is '{' or '}') continue;
                if (character == '\\')
                {
                    entryIndex++;
                    while (entryIndex < entry.Length && char.IsLetter(entry[entryIndex])) entryIndex++;
                    while (entryIndex < entry.Length && (entry[entryIndex] == '-' || char.IsDigit(entry[entryIndex]))) entryIndex++;
                    if (entryIndex < entry.Length && entry[entryIndex] != ' ') entryIndex--;
                    continue;
                }
                name.Append(character);
            }
            string family = name.ToString().Trim();
            if (family.Length > 0) result[fontIndex] = family;
            index = cursor + semicolon;
        }
        return result;
    }

    private static IReadOnlyList<RtfColor?> ExtractColorTable(string rtf)
    {
        var result = new List<RtfColor?> { null };
        if (!TryExtractDestination(rtf, @"{\colortbl", out ReadOnlySpan<char> table)) return result;
        int red = 0;
        int green = 0;
        int blue = 0;
        bool hasComponent = false;
        for (int index = 10; index < table.Length; index++)
        {
            if (table[index] == ';')
            {
                if (hasComponent) result.Add(new RtfColor((byte)Math.Clamp(red, 0, 255), (byte)Math.Clamp(green, 0, 255), (byte)Math.Clamp(blue, 0, 255)));
                red = green = blue = 0;
                hasComponent = false;
                continue;
            }
            if (table[index] != '\\') continue;
            int wordStart = ++index;
            while (index < table.Length && char.IsLetter(table[index])) index++;
            ReadOnlySpan<char> word = table[wordStart..index];
            int number = 0;
            bool hasNumber = false;
            while (index < table.Length && char.IsDigit(table[index]))
            {
                hasNumber = true;
                number = checked(number * 10 + table[index++] - '0');
            }
            index--;
            if (!hasNumber) continue;
            if (word.SequenceEqual("red")) red = number;
            else if (word.SequenceEqual("green")) green = number;
            else if (word.SequenceEqual("blue")) blue = number;
            else continue;
            hasComponent = true;
        }
        return result;
    }

    private static IReadOnlyDictionary<int, Microsoft.UI.Text.MarkerType[]> ExtractListDefinitions(string rtf)
    {
        var definitions = new Dictionary<int, Microsoft.UI.Text.MarkerType[]>();
        if (TryExtractEitherDestination(rtf, @"{\*\listtable", @"{\listtable", out ReadOnlySpan<char> listTable))
        {
            int searchStart = 0;
            while (TryFindExactGroup(listTable, @"{\list", searchStart, out int groupStart, out ReadOnlySpan<char> group))
            {
                searchStart = groupStart + group.Length;
                if (!TryFindControlNumber(group, "listid", out int listId)) continue;
                var levels = new List<Microsoft.UI.Text.MarkerType>();
                int controlStart = 0;
                while (TryFindControlNumber(group, "levelnfc", controlStart, out int nfc, out int controlEnd))
                {
                    levels.Add(MapListNumberFormat(nfc));
                    controlStart = controlEnd;
                }
                definitions[listId] = levels.Count == 0
                    ? [Microsoft.UI.Text.MarkerType.Arabic]
                    : levels.ToArray();
            }
        }

        var overrides = new Dictionary<int, Microsoft.UI.Text.MarkerType[]>();
        if (!TryExtractEitherDestination(rtf, @"{\*\listoverridetable", @"{\listoverridetable", out ReadOnlySpan<char> overrideTable))
            return overrides;
        int overrideSearchStart = 0;
        while (TryFindExactGroup(overrideTable, @"{\listoverride", overrideSearchStart, out int groupStart, out ReadOnlySpan<char> group))
        {
            overrideSearchStart = groupStart + group.Length;
            if (!TryFindControlNumber(group, "listid", out int listId) ||
                !TryFindControlNumber(group, "ls", out int overrideIndex))
                continue;
            if (definitions.TryGetValue(listId, out Microsoft.UI.Text.MarkerType[]? levels))
                overrides[overrideIndex] = levels;
        }
        return overrides;
    }

    private static Microsoft.UI.Text.MarkerType ResolveListType(
        IReadOnlyDictionary<int, Microsoft.UI.Text.MarkerType[]> lists,
        int overrideIndex,
        int levelIndex)
    {
        if (!lists.TryGetValue(overrideIndex, out Microsoft.UI.Text.MarkerType[]? levels) || levels.Length == 0)
            return Microsoft.UI.Text.MarkerType.Arabic;
        return levels[Math.Clamp(levelIndex, 0, levels.Length - 1)];
    }

    private static Microsoft.UI.Text.MarkerType MapListNumberFormat(int numberFormat) => numberFormat switch
    {
        1 => Microsoft.UI.Text.MarkerType.UppercaseRoman,
        2 => Microsoft.UI.Text.MarkerType.LowercaseRoman,
        3 => Microsoft.UI.Text.MarkerType.UppercaseEnglishLetter,
        4 => Microsoft.UI.Text.MarkerType.LowercaseEnglishLetter,
        23 => Microsoft.UI.Text.MarkerType.Bullet,
        _ => Microsoft.UI.Text.MarkerType.Arabic
    };

    private static bool TryExtractEitherDestination(
        string source,
        string starredPrefix,
        string ordinaryPrefix,
        out ReadOnlySpan<char> destination) =>
        TryExtractDestination(source, starredPrefix, out destination) ||
        TryExtractDestination(source, ordinaryPrefix, out destination);

    private static bool TryFindExactGroup(
        ReadOnlySpan<char> source,
        ReadOnlySpan<char> prefix,
        int searchStart,
        out int groupStart,
        out ReadOnlySpan<char> group)
    {
        while (searchStart < source.Length)
        {
            int relativeStart = source[searchStart..].IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (relativeStart < 0) break;
            groupStart = searchStart + relativeStart;
            int afterPrefix = groupStart + prefix.Length;
            if (afterPrefix < source.Length && char.IsLetter(source[afterPrefix]))
            {
                searchStart = afterPrefix;
                continue;
            }
            int depth = 0;
            for (int index = groupStart; index < source.Length; index++)
            {
                if (source[index] == '{') depth++;
                else if (source[index] == '}') depth--;
                else continue;
                if (depth != 0) continue;
                group = source[groupStart..(index + 1)];
                return true;
            }
            break;
        }
        groupStart = -1;
        group = default;
        return false;
    }

    private static bool TryFindControlNumber(ReadOnlySpan<char> source, string control, out int value) =>
        TryFindControlNumber(source, control, 0, out value, out _);

    private static bool TryFindControlNumber(
        ReadOnlySpan<char> source,
        string control,
        int searchStart,
        out int value,
        out int controlEnd)
    {
        ReadOnlySpan<char> needle = control.AsSpan();
        for (int index = Math.Max(0, searchStart); index + needle.Length + 1 < source.Length; index++)
        {
            if (source[index] != '\\' ||
                !source.Slice(index + 1, needle.Length).Equals(needle, StringComparison.OrdinalIgnoreCase))
                continue;
            int cursor = index + 1 + needle.Length;
            if (cursor < source.Length && char.IsLetter(source[cursor])) continue;
            bool negative = cursor < source.Length && source[cursor] == '-';
            if (negative) cursor++;
            if (cursor >= source.Length || !char.IsDigit(source[cursor])) continue;
            int number = 0;
            while (cursor < source.Length && char.IsDigit(source[cursor]))
                number = checked(number * 10 + source[cursor++] - '0');
            value = negative ? -number : number;
            controlEnd = cursor;
            return true;
        }
        value = 0;
        controlEnd = source.Length;
        return false;
    }

    private static bool TryExtractDestination(string source, string prefix, out ReadOnlySpan<char> destination)
    {
        int start = source.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            destination = default;
            return false;
        }
        int depth = 0;
        for (int index = start; index < source.Length; index++)
        {
            char character = source[index];
            if (character is not ('{' or '}') || IsEscaped(source, index)) continue;
            depth += character == '{' ? 1 : -1;
            if (depth != 0) continue;
            destination = source.AsSpan(start, index - start + 1);
            return true;
        }
        destination = default;
        return false;
    }

    private static bool IsEscaped(string source, int index)
    {
        int slashes = 0;
        while (--index >= 0 && source[index] == '\\') slashes++;
        return (slashes & 1) != 0;
    }

    private static string? ExtractHyperlinkInstruction(string source, int start)
    {
        int end = start;
        int depth = 1;
        while (end < source.Length && depth > 0)
        {
            if (!IsEscaped(source, end))
            {
                if (source[end] == '{') depth++;
                else if (source[end] == '}') depth--;
            }
            if (depth > 0) end++;
        }
        ReadOnlySpan<char> instruction = source.AsSpan(start, Math.Max(0, end - start));
        int keyword = instruction.IndexOf("HYPERLINK", StringComparison.OrdinalIgnoreCase);
        if (keyword < 0) return null;
        instruction = instruction[(keyword + "HYPERLINK".Length)..].Trim();
        if (instruction.Length == 0) return null;
        if (instruction[0] == '"')
        {
            instruction = instruction[1..];
            int quote = instruction.IndexOf('"');
            if (quote >= 0) instruction = instruction[..quote];
        }
        else
        {
            int whitespace = -1;
            for (int index = 0; index < instruction.Length; index++)
            {
                if (!char.IsWhiteSpace(instruction[index])) continue;
                whitespace = index;
                break;
            }
            if (whitespace >= 0) instruction = instruction[..whitespace];
        }
        return DecodeEscapedInstruction(instruction);
    }

    private static string DecodeEscapedInstruction(ReadOnlySpan<char> value)
    {
        var builder = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 1 < value.Length && value[index + 1] is '\\' or '{' or '}')
                index++;
            builder.Append(value[index]);
        }
        return builder.ToString();
    }

    private static RichTextSpan[] Coalesce(List<RichTextSpan> spans)
    {
        if (spans.Count < 2) return spans.ToArray();
        var result = new List<RichTextSpan>(spans.Count);
        foreach (RichTextSpan span in spans)
        {
            if (span.Text.Length == 0) continue;
            if (result.Count > 0 &&
                result[^1].Style.Equals(span.Style) &&
                result[^1].Text.Length + span.Text.Length <= MaxCoalescedTextLength)
            {
                RichTextSpan prior = result[^1];
                result[^1] = new RichTextSpan(prior.Text + span.Text, prior.Style);
            }
            else
            {
                result.Add(span);
            }
        }
        return result.ToArray();
    }

    private static void RemoveTrailingCharacter(
        List<RichTextSpan> spans,
        ref int outputLength,
        char character)
    {
        if (spans.Count == 0 || spans[^1].Text.Length == 0 || spans[^1].Text[^1] != character) return;
        RichTextSpan last = spans[^1];
        if (last.Text.Length == 1) spans.RemoveAt(spans.Count - 1);
        else spans[^1] = new RichTextSpan(last.Text[..^1], last.Style);
        outputLength--;
    }

    private static char DecodeWindows1252(byte value) => value switch
    {
        0x80 => '\u20AC', 0x82 => '\u201A', 0x83 => '\u0192', 0x84 => '\u201E',
        0x85 => '\u2026', 0x86 => '\u2020', 0x87 => '\u2021', 0x88 => '\u02C6',
        0x89 => '\u2030', 0x8A => '\u0160', 0x8B => '\u2039', 0x8C => '\u0152',
        0x8E => '\u017D', 0x91 => '\u2018', 0x92 => '\u2019', 0x93 => '\u201C',
        0x94 => '\u201D', 0x95 => '\u2022', 0x96 => '\u2013', 0x97 => '\u2014',
        0x98 => '\u02DC', 0x99 => '\u2122', 0x9A => '\u0161', 0x9B => '\u203A',
        0x9C => '\u0153', 0x9E => '\u017E', 0x9F => '\u0178',
        _ => (char)value
    };

    private static void ApplyParsedLineSpacing(ref ParserState state)
    {
        int twips = state.LineSpacingTwips;
        if (twips == 0)
        {
            state.ParagraphFormat.LineSpacingRule = Microsoft.UI.Text.LineSpacingRule.Single;
            state.ParagraphFormat.LineSpacing = 0f;
            return;
        }
        if (state.LineSpacingMultiple)
        {
            if (twips == 360)
            {
                state.ParagraphFormat.LineSpacingRule = Microsoft.UI.Text.LineSpacingRule.OneAndHalf;
                state.ParagraphFormat.LineSpacing = 1.5f;
            }
            else if (twips == 480)
            {
                state.ParagraphFormat.LineSpacingRule = Microsoft.UI.Text.LineSpacingRule.Double;
                state.ParagraphFormat.LineSpacing = 2f;
            }
            else
            {
                state.ParagraphFormat.LineSpacingRule = Microsoft.UI.Text.LineSpacingRule.Multiple;
                state.ParagraphFormat.LineSpacing = twips / 240f;
            }
            return;
        }
        state.ParagraphFormat.LineSpacingRule = twips < 0
            ? Microsoft.UI.Text.LineSpacingRule.Exactly
            : Microsoft.UI.Text.LineSpacingRule.AtLeast;
        state.ParagraphFormat.LineSpacing = MathF.Abs(twips) / 20f;
    }

    private static void ApplyParsedTableCellMetadata(ref ParserState state)
    {
        var spans = new List<int>(state.TableCellMergeFlags.Count);
        var backgrounds = new List<Brush?>(state.TableCellMergeFlags.Count);
        var verticalMergeFlags = new List<byte>(state.TableCellMergeFlags.Count);
        for (int index = 0; index < state.TableCellMergeFlags.Count; index++)
        {
            if (state.TableCellMergeFlags[index] == 2 && spans.Count > 0)
            {
                spans[^1]++;
                continue;
            }
            spans.Add(1);
            backgrounds.Add(index < state.TableCellBackgrounds.Count
                ? state.TableCellBackgrounds[index]
                : null);
            verticalMergeFlags.Add(index < state.TableCellVerticalMergeFlags.Count
                ? state.TableCellVerticalMergeFlags[index]
                : (byte)0);
        }
        state.ParagraphFormat.TableCellColumnSpans = spans.ToArray();
        state.ParagraphFormat.TableCellBackgrounds = backgrounds.ToArray();
        state.ParagraphFormat.TableCellVerticalMergeFlags = verticalMergeFlags.ToArray();
    }

    private struct ParserState
    {
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public Microsoft.UI.Text.UnderlineType UnderlineType;
        public bool Strikethrough;
        public bool Hidden;
        public bool Protected;
        public bool AllCaps;
        public bool SmallCaps;
        public bool Outline;
        public bool Subscript;
        public bool Superscript;
        public float BaselineOffset;
        public float CharacterSpacing;
        public float FontSize;
        public int FontIndex;
        public int ForegroundColorIndex;
        public int BackgroundColorIndex;
        public string? LanguageTag;
        public string? Link;
        public FlowDirection? FlowDirection;
        public int UnicodeSkipCount;
        public bool SkipDestination;
        public Microsoft.UI.Text.RichParagraphFormatState ParagraphFormat;
        public Microsoft.UI.Text.TabAlignment PendingTabAlignment;
        public Microsoft.UI.Text.TabLeader PendingTabLeader;
        public int LineSpacingTwips;
        public bool LineSpacingMultiple;
        public bool IsListDefinition;
        public int ListOverrideIndex;
        public bool IsTableRow;
        public List<float> TableCellRightEdges;
        public List<Brush?> TableCellBackgrounds;
        public List<byte> TableCellMergeFlags;
        public List<byte> TableCellVerticalMergeFlags;
        public int PendingTableCellBackgroundColorIndex;
        public bool HasPendingTableCellBackground;
        public byte PendingTableCellMergeFlag;
        public byte PendingTableCellVerticalMergeFlag;
        public int TableContentLogicalCellIndex;
        public bool ParsingTableCellBorder;
        public IReadOnlyDictionary<int, string> Fonts;
        public IReadOnlyList<RtfColor?> Colors;
        public IReadOnlyDictionary<int, Microsoft.UI.Text.MarkerType[]> Lists;

        public ParserState(
            RichTextStyle style,
            IReadOnlyDictionary<int, string> fonts,
            IReadOnlyList<RtfColor?> colors,
            IReadOnlyDictionary<int, Microsoft.UI.Text.MarkerType[]> lists)
        {
            Bold = style.IsBold;
            Italic = style.IsItalic;
            Underline = style.IsUnderline;
            UnderlineType = style.UnderlineType;
            Strikethrough = style.IsStrikethrough;
            Hidden = style.IsHidden;
            Protected = style.IsProtected;
            AllCaps = style.IsAllCaps;
            SmallCaps = style.IsSmallCaps;
            Outline = style.IsOutline;
            Subscript = style.IsSubscript;
            Superscript = style.IsSuperscript;
            BaselineOffset = style.BaselineOffset;
            CharacterSpacing = style.CharacterSpacing;
            FontSize = style.FontSize > 0f ? style.FontSize : 14f;
            FontIndex = 0;
            ForegroundColorIndex = 0;
            BackgroundColorIndex = 0;
            LanguageTag = style.LanguageTag;
            Link = style.Link;
            FlowDirection = style.FlowDirection;
            UnicodeSkipCount = 1;
            SkipDestination = false;
            ParagraphFormat = new Microsoft.UI.Text.RichParagraphFormatState();
            PendingTabAlignment = Microsoft.UI.Text.TabAlignment.Left;
            PendingTabLeader = Microsoft.UI.Text.TabLeader.Spaces;
            LineSpacingTwips = 0;
            LineSpacingMultiple = false;
            IsListDefinition = false;
            ListOverrideIndex = 0;
            IsTableRow = false;
            TableCellRightEdges = new List<float>();
            TableCellBackgrounds = new List<Brush?>();
            TableCellMergeFlags = new List<byte>();
            TableCellVerticalMergeFlags = new List<byte>();
            PendingTableCellBackgroundColorIndex = 0;
            HasPendingTableCellBackground = false;
            PendingTableCellMergeFlag = 0;
            PendingTableCellVerticalMergeFlag = 0;
            TableContentLogicalCellIndex = 0;
            ParsingTableCellBorder = false;
            Fonts = fonts;
            Colors = colors;
            Lists = lists;
        }

        public readonly RichTextStyle ToStyle(RichTextStyle fallback)
        {
            string? family = Fonts.TryGetValue(FontIndex, out string? fontName) ? fontName : fallback.FontName;
            Brush? foreground = ResolveColor(ForegroundColorIndex) ?? fallback.Foreground;
            Brush? background = ResolveColor(BackgroundColorIndex) ?? fallback.Background;
            return fallback with
            {
                FontSize = FontSize,
                FontName = family,
                Font = string.IsNullOrWhiteSpace(family) ? fallback.Font : FontApi.Manager.MatchFamily(family) ?? fallback.Font,
                Foreground = foreground,
                Background = background,
                LanguageTag = LanguageTag,
                Link = Link,
                FlowDirection = FlowDirection,
                IsBold = Bold,
                IsItalic = Italic,
                IsUnderline = Underline,
                UnderlineType = UnderlineType,
                IsStrikethrough = Strikethrough,
                IsHidden = Hidden,
                IsProtected = Protected,
                IsAllCaps = AllCaps,
                IsSmallCaps = SmallCaps,
                IsOutline = Outline,
                IsSubscript = Subscript,
                IsSuperscript = Superscript,
                BaselineOffset = BaselineOffset,
                CharacterSpacing = CharacterSpacing
            };
        }

        private readonly Brush? ResolveColor(int index)
        {
            if ((uint)index >= (uint)Colors.Count || Colors[index] is not RtfColor color) return null;
            return new SolidColorBrush(new Vector4(color.Red / 255f, color.Green / 255f, color.Blue / 255f, 1f));
        }

        public readonly Brush? ResolveTableColor(int index) => ResolveColor(index);
    }

    private readonly record struct RtfColor(byte Red, byte Green, byte Blue);
}
