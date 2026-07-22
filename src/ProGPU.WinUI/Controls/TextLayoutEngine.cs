using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Text.Bidi;
using ProGPU.Text.Shaping;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Controls
{
    public static class TextLayoutEngine
    {
        private static readonly SolidColorBrush HyperlinkBrush = new SolidColorBrush(0x0078D4FF);
        private static readonly SolidColorBrush SelectionHighlightBrush = new SolidColorBrush(0x0078D435);
        private static readonly SolidColorBrush HoveredHyperlinkBrush = new SolidColorBrush(0x005A9EFF);
        private static readonly TextShapingOptions[] CommonLeftToRightShapingOptions =
            CreateCommonShapingOptions(ShapingDirection.LeftToRight);
        private static readonly TextShapingOptions[] CommonRightToLeftShapingOptions =
            CreateCommonShapingOptions(ShapingDirection.RightToLeft);

        public static void AccumulateInlines(
            Inline inline,
            List<RichChar> list,
            Brush defaultFg,
            float defaultSize,
            bool isBold,
            bool isItalic,
            bool isUnderline,
            ElementTheme theme,
            Inline? parentInline = null,
            float leftIndent = 0f,
            TtfFont? parentFont = null)
        {
            Brush fg = inline.Foreground ?? defaultFg;
            if (fg is ThemeResourceBrush trBrush)
            {
                fg = ThemeManager.GetBrush(trBrush.ResourceKey, theme);
            }
            float size = inline.FontSize ?? defaultSize;
            TtfFont? font = inline.Font ?? parentFont;
            Inline source = parentInline ?? inline;

            if (inline is Run run)
            {
                RichTextStyle? retained = run.RetainedStyle;
                float retainedSize = retained?.FontSize ?? size;
                float effectiveSize = retained is { IsSubscript: true } or { IsSuperscript: true }
                    ? retainedSize * 0.75f
                    : retainedSize;
                float effectiveBaseline = (retained?.BaselineOffset ?? 0f) + (retained switch
                {
                    { IsSuperscript: true } => retainedSize * 0.3f,
                    { IsSubscript: true } => -retainedSize * 0.18f,
                    _ => 0f
                });
                foreach (char c in run.Text)
                {
                    list.Add(new RichChar
                    {
                        Character = c,
                        Foreground = retained?.Foreground ?? fg,
                        FontSize = effectiveSize,
                        Font = retained?.Font ?? font,
                        IsBold = retained?.IsBold ?? isBold,
                        IsItalic = retained?.IsItalic ?? isItalic,
                        IsUnderline = retained?.IsUnderline ?? isUnderline,
                        Background = retained?.Background,
                        IsStrikethrough = retained?.IsStrikethrough ?? false,
                        CharacterSpacing = retained?.CharacterSpacing ?? 0f,
                        BaselineOffset = effectiveBaseline,
                        IsHidden = retained?.IsHidden ?? false,
                        IsProtected = retained?.IsProtected ?? false,
                        IsAllCaps = retained?.IsAllCaps ?? false,
                        IsSmallCaps = retained?.IsSmallCaps ?? false,
                        IsOutline = retained?.IsOutline ?? false,
                        LanguageTag = retained?.LanguageTag,
                        TextScript = retained?.TextScript ?? Microsoft.UI.Text.TextScript.Undefined,
                        UnderlineType = retained?.UnderlineType ?? Microsoft.UI.Text.UnderlineType.None,
                        FontWeight = retained?.FontWeight ?? 0,
                        FontStretch = retained?.FontStretch ?? Windows.UI.Text.FontStretch.Normal,
                        FontStyle = retained?.FontStyle ?? Windows.UI.Text.FontStyle.Normal,
                        Kerning = retained?.Kerning ?? 0f,
                        FontName = retained?.FontName,
                        IsSubscript = retained?.IsSubscript ?? false,
                        IsSuperscript = retained?.IsSuperscript ?? false,
                        FlowDirection = run.HasExplicitFlowDirection
                            ? run.FlowDirection
                            : retained?.FlowDirection,
                        RetainedStyle = retained,
                        SourceInline = source,
                        LeftIndent = leftIndent
                    });
                }
            }
            else if (inline is LineBreak)
            {
                list.Add(new RichChar
                {
                    Character = '\n',
                    Foreground = fg,
                    FontSize = size,
                    Font = font,
                    IsBold = isBold,
                    IsItalic = isItalic,
                    IsUnderline = isUnderline,
                    SourceInline = source,
                    LeftIndent = leftIndent
                });
            }
            else if (inline is InlineUIContainer uic)
            {
                list.Add(new RichChar
                {
                    Character = '\uFFFC',
                    Foreground = fg,
                    FontSize = size,
                    Font = font,
                    IsBold = isBold,
                    IsItalic = isItalic,
                    IsUnderline = isUnderline,
                    SourceInline = uic,
                    EmbeddedElement = uic.Child,
                    LeftIndent = leftIndent
                });
            }
            else if (inline is ListBlock listBlock)
            {
                int itemIdx = 1;
                foreach (var item in listBlock.Items)
                {
                    if (list.Count > 0 && list[^1].Character != '\n')
                    {
                        list.Add(new RichChar { Character = '\n', Foreground = fg, FontSize = size, Font = font, SourceInline = item, LeftIndent = leftIndent });
                    }

                    string prefix = listBlock.IsOrdered ? $"{itemIdx}. " : "• ";
                    itemIdx++;

                    foreach (char bulletChar in prefix)
                    {
                        list.Add(new RichChar
                        {
                            Character = bulletChar,
                            Foreground = fg,
                            FontSize = size,
                            Font = font,
                            IsBold = isBold,
                            IsItalic = isItalic,
                            IsUnderline = isUnderline,
                            SourceInline = item,
                            LeftIndent = leftIndent + listBlock.Indentation,
                            BulletOffset = listBlock.Indentation - 8f
                        });
                    }

                    foreach (var sub in item.Inlines)
                    {
                        AccumulateInlines(sub, list, fg, size, isBold, isItalic, isUnderline, theme, item, leftIndent + listBlock.Indentation, font);
                    }
                }
            }
            else if (inline is Table table)
            {
                if (list.Count > 0 && list[^1].Character != '\n')
                {
                    list.Add(new RichChar { Character = '\n', Foreground = fg, FontSize = size, Font = font, SourceInline = table, LeftIndent = leftIndent });
                }

                list.Add(new RichChar
                {
                    Character = '\uFFFD',
                    Foreground = fg,
                    FontSize = size,
                    Font = font,
                    SourceInline = table,
                    LeftIndent = leftIndent
                });

                list.Add(new RichChar { Character = '\n', Foreground = fg, FontSize = size, Font = font, SourceInline = table, LeftIndent = leftIndent });
            }
            else if (inline is Span span)
            {
                bool nextBold = isBold || (span is Bold);
                bool nextItalic = isItalic || (span is Italic);
                bool nextUnderline = isUnderline || (span is Underline || span is Hyperlink);

                if (span is Hyperlink && inline.Foreground == null)
                {
                    fg = HyperlinkBrush;
                }

                foreach (var sub in span.Inlines)
                {
                    AccumulateInlines(sub, list, fg, size, nextBold, nextItalic, nextUnderline, theme, span is Hyperlink ? span : source, leftIndent, font);
                }
            }
        }

        public static float LayoutSingleColumn(
            IReadOnlyList<Inline> inlines,
            float maxWidth,
            Thickness padding,
            TtfFont activeFont,
            float baseFontSize,
            Brush? defaultFg,
            TextAlignment alignment,
            ElementTheme theme,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> tableDecorations,
            FrameworkElement parent,
            Action<Visual> addChild,
            Action<Visual> removeChild,
            TextWrapping textWrapping = TextWrapping.Wrap,
            TextReadingOrder textReadingOrder = TextReadingOrder.DetectFromContent,
            FlowDirection flowDirection = FlowDirection.LeftToRight,
            RichDocumentLayoutSession? layoutSession = null,
            bool alignmentIncludesTrailingWhitespace = false,
            bool ignoreTrailingCharacterSpacing = false)
        {
            var blocks = new List<Block>
            {
                new Paragraph(inlines.ToArray())
                {
                    MarginBottom = 0f,
                    TextAlignment = alignment
                }
            };
            return LayoutSingleColumn(
                blocks,
                maxWidth,
                padding,
                activeFont,
                baseFontSize,
                defaultFg,
                alignment,
                theme,
                positionedChars,
                tableDecorations,
                parent,
                addChild,
                removeChild,
                textWrapping,
                textReadingOrder,
                flowDirection,
                layoutSession,
                alignmentIncludesTrailingWhitespace,
                ignoreTrailingCharacterSpacing);
        }

        private static int GetInlinesLength(IEnumerable<Inline> inlines)
        {
            int len = 0;
            foreach (var inline in inlines)
            {
                if (inline is Run run) len += run.Text.Length;
                else if (inline is Span span) len += GetInlinesLength(span.Inlines);
                else if (inline is LineBreak) len += 1;
            }
            return len;
        }

        private static int GetBlockTextLength(Block block) => block switch
        {
            Paragraph paragraph => GetInlinesLength(paragraph.Inlines),
            Span span => GetInlinesLength(span.Inlines),
            Run run => run.Text.Length,
            LineBreak => 1,
            _ => 1
        };

        private static int GetCachedBlockTextLength(Block block, RichBlockLayoutCache cache)
        {
            if (cache.LogicalTextLength < 0)
            {
                cache.LogicalTextLength = GetBlockTextLength(block);
            }

            return cache.LogicalTextLength;
        }

        private static void RebaseBlockCharacters(RichBlockLayoutCache cache, int logicalTextOffset)
        {
            cache.RebaseTextPositions(logicalTextOffset);
        }

        private static int CountLineBreaks(IEnumerable<Inline> inlines)
        {
            int count = 0;
            foreach (var inline in inlines)
            {
                if (inline is LineBreak) count++;
                else if (inline is Span span) count += CountLineBreaks(span.Inlines);
            }
            return count;
        }

        private static ushort ResolveGlyph(
            ref RichChar richChar,
            TtfFont activeFont,
            out TtfFont charFont,
            uint? codePoint = null)
        {
            uint scalar = codePoint ?? richChar.Character;
            TtfFont requestedFont = richChar.Font ?? activeFont;
            var requestedStyle = new FontStyleRequest(
                richChar.FontWeight > 0 ? richChar.FontWeight : richChar.IsBold ? 700 : requestedFont.WeightClass == 0 ? 400 : requestedFont.WeightClass,
                richChar.FontStretch == Windows.UI.Text.FontStretch.Undefined
                    ? requestedFont.WidthClass == 0 ? 5 : requestedFont.WidthClass
                    : (int)richChar.FontStretch,
                richChar.FontStyle is Windows.UI.Text.FontStyle.Italic or Windows.UI.Text.FontStyle.Oblique ||
                richChar.IsItalic || requestedFont.IsItalic ? FontSlant.Italic : FontSlant.Upright);
            charFont = FontApi.Manager.MatchTypeface(requestedFont, requestedStyle) ?? requestedFont;
            richChar.Font = charFont;
            ushort glyphIndex = charFont.GetGlyphIndex(scalar);
            if (glyphIndex == 0 &&
                FontApi.TryResolvePlatformFallback(
                    charFont,
                    checked((int)scalar),
                    out TtfFont? fallbackFont,
                    out ushort fallbackGlyphIndex) &&
                fallbackFont != null)
            {
                charFont = fallbackFont;
                richChar.Font = fallbackFont;
                glyphIndex = fallbackGlyphIndex;
            }

            return glyphIndex;
        }

        private static void ResolveCharacterFonts(List<RichChar> characters, TtfFont activeFont)
        {
            for (int index = 0; index < characters.Count; index++)
            {
                RichChar character = characters[index];
                character.TextPosition = index;
                if (character.EmbeddedElement is not null ||
                    character.Character is '\n' or '\uFFFD')
                {
                    characters[index] = character;
                    continue;
                }

                if (char.IsHighSurrogate(character.Character) &&
                    index + 1 < characters.Count &&
                    char.IsLowSurrogate(characters[index + 1].Character))
                {
                    uint scalar = checked((uint)char.ConvertToUtf32(
                        character.Character,
                        characters[index + 1].Character));
                    ResolveGlyph(ref character, activeFont, out _, scalar);
                    characters[index] = character;

                    RichChar trailing = characters[++index];
                    trailing.TextPosition = index;
                    trailing.Font = character.Font;
                    characters[index] = trailing;
                    continue;
                }

                // Isolated UTF-16 surrogates are invalid Unicode scalars. Shape them as
                // U+FFFD while retaining their one-code-unit document position.
                uint codePoint = char.IsSurrogate(character.Character)
                    ? 0xFFFDu
                    : character.Character;
                ResolveGlyph(ref character, activeFont, out _, codePoint);
                characters[index] = character;
            }
        }

        private static void ApplyShapedClusterMetrics(
            List<PositionedRichChar> line,
            BidiParagraph bidi,
            TtfFont activeFont) => ApplyShapedClusterMetrics(line, bidi.Utf16Levels, activeFont);

        private static void ApplyShapedClusterMetrics(
            List<PositionedRichChar> line,
            ReadOnlySpan<sbyte> bidiLevels,
            TtfFont activeFont,
            bool ignoreTrailingCharacterSpacing = false)
        {
            for (int i = 0; i < line.Count; i++)
            {
                PositionedRichChar character = line[i];
                character.BidiLevel = bidiLevels[i];
                character.ClusterStart = character.Info.TextPosition;
                character.ClusterLength = 1;
                character.ShapedAdvance = 0f;
                character.ShapedAdvanceWithoutCharacterSpacing = 0f;
                character.HasShapedAdvance = false;
                character.ShapingFlags = ShapingGlyphFlags.None;
            }

            string logicalText = string.Create(line.Count, line, static (span, characters) =>
            {
                for (var index = 0; index < characters.Count; index++)
                {
                    RichChar character = characters[index].Info;
                    span[index] = character.IsAllCaps
                        ? char.ToUpperInvariant(character.Character)
                        : character.Character;
                }
            });

            int runStart = 0;
            while (runStart < line.Count)
            {
                RichChar first = line[runStart].Info;
                if (first.EmbeddedElement is { } embeddedElement)
                {
                    PositionedRichChar embedded = line[runStart];
                    embedded.HasShapedAdvance = true;
                    embedded.ShapedAdvance = embeddedElement.DesiredSize.X + 4f;
                    embedded.ShapedAdvanceWithoutCharacterSpacing = embedded.ShapedAdvance;
                    runStart++;
                    continue;
                }
                TtfFont runFont = first.Font ?? activeFont;
                sbyte runLevel = line[runStart].BidiLevel;
                int runEnd = runStart + 1;
                while (runEnd < line.Count &&
                       line[runEnd].BidiLevel == runLevel &&
                       ReferenceEquals(line[runEnd].Info.Font ?? activeFont, runFont) &&
                       line[runEnd].Info.FontSize == first.FontSize &&
                       line[runEnd].Info.IsBold == first.IsBold &&
                       line[runEnd].Info.IsItalic == first.IsItalic &&
                       line[runEnd].Info.IsUnderline == first.IsUnderline &&
                       line[runEnd].Info.IsSmallCaps == first.IsSmallCaps &&
                       line[runEnd].Info.IsAllCaps == first.IsAllCaps &&
                       line[runEnd].Info.Kerning == first.Kerning &&
                       string.Equals(line[runEnd].Info.LanguageTag, first.LanguageTag, StringComparison.OrdinalIgnoreCase) &&
                       line[runEnd].Info.EmbeddedElement is null &&
                       Equals(line[runEnd].Info.Foreground, first.Foreground))
                {
                    runEnd++;
                }

                int runLength = runEnd - runStart;
                ShapingBufferFlags flags = ShapingBufferFlags.None;
                if (runStart == 0) flags |= ShapingBufferFlags.BeginningOfText;
                if (runEnd == line.Count) flags |= ShapingBufferFlags.EndOfText;
                TextShapingOptions options = CreateShapingOptions(first, runLevel, flags);
                string runText = runStart == 0 && runLength == logicalText.Length
                    ? logicalText
                    : logicalText.Substring(runStart, runLength);
                IReadOnlyList<ShapedGlyph> shaped = OpenTypeTextShaper.Shape(
                    runText,
                    runFont,
                    first.FontSize,
                    options,
                    logicalText.AsMemory(0, runStart),
                    logicalText.AsMemory(runEnd));

                var clusterAdvances = new float[runLength];
                var clusterStarts = new bool[runLength];
                for (int i = 0; i < shaped.Count; i++)
                {
                    int cluster = Math.Clamp(shaped[i].Cluster, 0, runLength - 1);
                    clusterStarts[cluster] = true;
                    clusterAdvances[cluster] += shaped[i].AdvanceX;
                    line[runStart + cluster].ShapingFlags |= shaped[i].Flags;
                }

                int clusterStart = 0;
                while (clusterStart < runLength)
                {
                    while (clusterStart < runLength && !clusterStarts[clusterStart])
                    {
                        clusterStart++;
                    }
                    if (clusterStart >= runLength) break;

                    int clusterEnd = clusterStart + 1;
                    while (clusterEnd < runLength && !clusterStarts[clusterEnd])
                    {
                        clusterEnd++;
                    }

                    int textPosition = line[runStart + clusterStart].Info.TextPosition;
                    int clusterLength = clusterEnd - clusterStart;
                    int advanceOwner = (runLevel & 1) == 0 ? clusterStart : clusterEnd - 1;
                    for (int i = clusterStart; i < clusterEnd; i++)
                    {
                        PositionedRichChar character = line[runStart + i];
                        character.ClusterStart = textPosition;
                        character.ClusterLength = clusterLength;
                        character.HasShapedAdvance = true;
                        character.ShapedAdvance = i == advanceOwner ? clusterAdvances[clusterStart] : 0f;
                        if (character.Info.IsHidden) character.ShapedAdvance = 0f;
                        character.ShapedAdvanceWithoutCharacterSpacing = character.ShapedAdvance;
                        bool isTrailingCluster = runEnd == line.Count && clusterEnd == runLength;
                        if (i == advanceOwner && character.ShapedAdvance > 0f &&
                            !(ignoreTrailingCharacterSpacing && isTrailingCluster))
                            character.ShapedAdvance = Math.Max(0f, character.ShapedAdvance + character.Info.CharacterSpacing);
                    }

                    clusterStart = clusterEnd;
                }

                // Embedded elements and defensive missing-glyph cases remain measurable.
                for (int i = runStart; i < runEnd; i++)
                {
                    PositionedRichChar character = line[i];
                    if (!character.HasShapedAdvance)
                    {
                        character.HasShapedAdvance = true;
                        character.ShapedAdvance = character.Info.EmbeddedElement is { } element
                            ? element.DesiredSize.X + 4f
                            : runFont.GetAdvanceWidth(runFont.GetGlyphIndex(character.Info.Character), character.Info.FontSize);
                        character.ShapedAdvanceWithoutCharacterSpacing = character.ShapedAdvance;
                    }
                }

                runStart = runEnd;
            }
        }

        private static TextShapingOptions CreateShapingOptions(
            RichChar style,
            sbyte bidiLevel,
            ShapingBufferFlags bufferFlags = ShapingBufferFlags.None)
        {
            ShapingDirection direction = (bidiLevel & 1) == 0
                ? ShapingDirection.LeftToRight
                : ShapingDirection.RightToLeft;
            List<OpenTypeFeatureSetting>? overrides = null;
            if (style.Kerning > 0f && style.FontSize < style.Kerning)
                (overrides ??= new List<OpenTypeFeatureSetting>()).Add(new OpenTypeFeatureSetting("kern", 0));
            if (style.IsSmallCaps)
            {
                (overrides ??= new List<OpenTypeFeatureSetting>()).Add(new OpenTypeFeatureSetting("smcp", 1));
                (overrides ??= new List<OpenTypeFeatureSetting>()).Add(new OpenTypeFeatureSetting("c2sc", 1));
            }
            if (overrides is not null)
            {
                TextShapingOptions resolved = TextShapingOptions.WithFeatures(overrides.ToArray());
                return new TextShapingOptions
                {
                    Direction = direction,
                    Language = string.IsNullOrWhiteSpace(style.LanguageTag) ? null : style.LanguageTag,
                    Features = resolved.Features,
                    ExplicitFeatureTags = resolved.ExplicitFeatureTags,
                    BufferFlags = bufferFlags
                };
            }

            if (string.IsNullOrWhiteSpace(style.LanguageTag) &&
                (bufferFlags & ~(ShapingBufferFlags.BeginningOfText | ShapingBufferFlags.EndOfText)) == 0)
            {
                TextShapingOptions[] common = direction == ShapingDirection.RightToLeft
                    ? CommonRightToLeftShapingOptions
                    : CommonLeftToRightShapingOptions;
                return common[(int)bufferFlags];
            }

            return new TextShapingOptions
            {
                Direction = direction,
                Language = string.IsNullOrWhiteSpace(style.LanguageTag) ? null : style.LanguageTag,
                BufferFlags = bufferFlags
            };
        }

        private static TextShapingOptions[] CreateCommonShapingOptions(ShapingDirection direction) =>
        [
            new TextShapingOptions { Direction = direction },
            new TextShapingOptions
            {
                Direction = direction,
                BufferFlags = ShapingBufferFlags.BeginningOfText
            },
            new TextShapingOptions
            {
                Direction = direction,
                BufferFlags = ShapingBufferFlags.EndOfText
            },
            new TextShapingOptions
            {
                Direction = direction,
                BufferFlags = ShapingBufferFlags.BeginningOfText | ShapingBufferFlags.EndOfText
            }
        ];

        private sealed class ParagraphShapingMetrics
        {
            public ParagraphShapingMetrics(int length)
            {
                Advances = new float[length];
                AdvancesWithoutCharacterSpacing = new float[length];
                Levels = new sbyte[length];
                ParagraphLevels = new sbyte[length];
                BreakSafety = new byte[length];
            }

            public float[] Advances { get; }
            public float[] AdvancesWithoutCharacterSpacing { get; }
            public sbyte[] Levels { get; }
            public sbyte[] ParagraphLevels { get; }
            // 0 = not a cluster boundary, 1 = safe boundary, 2 = unsafe boundary.
            public byte[] BreakSafety { get; }
        }

        private static ParagraphShapingMetrics MeasureShapedParagraphAdvances(
            List<RichChar> characters,
            TtfFont activeFont,
            TextReadingOrder textReadingOrder,
            FlowDirection flowDirection,
            RichDocumentLayoutSession? layoutSession = null)
        {
            var metrics = new ParagraphShapingMetrics(characters.Count);
            int paragraphStart = 0;
            while (paragraphStart < characters.Count)
            {
                while (paragraphStart < characters.Count &&
                       (characters[paragraphStart].Character == '\n' ||
                        characters[paragraphStart].Character == '\uFFFD'))
                {
                    paragraphStart++;
                }
                if (paragraphStart >= characters.Count) break;

                int paragraphEnd = paragraphStart;
                while (paragraphEnd < characters.Count &&
                       characters[paragraphEnd].Character != '\n' &&
                       characters[paragraphEnd].Character != '\uFFFD')
                {
                    paragraphEnd++;
                }

                List<PositionedRichChar> paragraph = layoutSession?.GetShapingCharacterScratch() ??
                    new List<PositionedRichChar>(paragraphEnd - paragraphStart);
                var textBuilder = new StringBuilder(paragraphEnd - paragraphStart);
                for (int i = paragraphStart; i < paragraphEnd; i++)
                {
                    RichChar character = characters[i];
                    paragraph.Add(layoutSession?.RentPositionedCharacter(character) ??
                        new PositionedRichChar { Info = character });
                    textBuilder.Append(characters[i].Character);
                }

                ShapingDirection baseDirection = textReadingOrder == TextReadingOrder.DetectFromContent
                    ? ShapingDirection.Unspecified
                    : flowDirection == FlowDirection.RightToLeft
                        ? ShapingDirection.RightToLeft
                        : ShapingDirection.LeftToRight;
                ShapingDirection[]? inlineDirections = null;
                for (int i = paragraphStart; i < paragraphEnd; i++)
                {
                    if (characters[i].FlowDirection is not { } direction) continue;
                    inlineDirections ??= new ShapingDirection[paragraphEnd - paragraphStart];
                    inlineDirections[i - paragraphStart] = direction == FlowDirection.RightToLeft
                        ? ShapingDirection.RightToLeft
                        : ShapingDirection.LeftToRight;
                }
                string paragraphText = textBuilder.ToString();
                BidiParagraph bidi = inlineDirections is null
                    ? BidiParagraph.Resolve(paragraphText, baseDirection)
                    : BidiParagraph.Resolve(paragraphText, inlineDirections, baseDirection);
                ApplyShapedClusterMetrics(paragraph, bidi, activeFont);
                for (int i = 0; i < paragraph.Count; i++)
                {
                    int target = paragraphStart + i;
                    metrics.Advances[target] = paragraph[i].ShapedAdvance;
                    metrics.AdvancesWithoutCharacterSpacing[target] =
                        paragraph[i].ShapedAdvanceWithoutCharacterSpacing;
                    metrics.Levels[target] = bidi.Utf16Levels[i];
                    metrics.ParagraphLevels[target] = bidi.ParagraphLevel;
                    if (paragraph[i].Info.TextPosition == paragraph[i].ClusterStart)
                    {
                        metrics.BreakSafety[target] =
                            (paragraph[i].ShapingFlags & ShapingGlyphFlags.UnsafeToBreak) == 0
                                ? (byte)1
                                : (byte)2;
                    }
                }

                layoutSession?.ReleaseCharacters(paragraph);

                paragraphStart = paragraphEnd;
            }

            return metrics;
        }

        private static sbyte[] GetLineBidiLevels(
            IReadOnlyList<PositionedRichChar> line,
            ParagraphShapingMetrics metrics,
            out sbyte paragraphLevel)
        {
            var levels = new sbyte[line.Count];
            paragraphLevel = 0;
            for (int index = 0; index < line.Count; index++)
            {
                int position = Math.Clamp(line[index].Info.TextPosition, 0, metrics.Levels.Length - 1);
                levels[index] = metrics.Levels[position];
                if (index == 0) paragraphLevel = metrics.ParagraphLevels[position];
            }

            // UAX #9 L1 resets trailing whitespace on each already-broken line.
            for (int index = line.Count - 1; index >= 0 && char.IsWhiteSpace(line[index].Info.Character); index--)
                levels[index] = paragraphLevel;
            return levels;
        }

        private static bool IsClusterBoundaryBefore(ParagraphShapingMetrics metrics, int textPosition)
        {
            if (textPosition <= 0 || textPosition >= metrics.BreakSafety.Length) return true;
            // HarfBuzz's UnsafeToBreak flag means that splitting here requires both
            // fragments to be reshaped; it does not prohibit a line break. Every
            // committed rich-document line is reshaped, so only positions inside a
            // shaping cluster (0) are unavailable to the wrapper.
            return metrics.BreakSafety[textPosition] != 0;
        }

        private static float EstimateBlockHeight(Block block, float availableWidth, float baseFontSize, TtfFont activeFont)
        {
            float scale = baseFontSize / activeFont.UnitsPerEm;
            float lineSpacing = (activeFont.Ascender - activeFont.Descender + activeFont.LineGap) * scale;

            if (block is Paragraph paragraph)
            {
                int charCount = GetInlinesLength(paragraph.Inlines);
                if (charCount == 0) return block.MarginBottom;

                // Detect the maximum font size in children runs/spans
                float maxFontSize = baseFontSize;
                foreach (var inline in paragraph.Inlines)
                {
                    if (inline.FontSize.HasValue)
                    {
                        maxFontSize = Math.Max(maxFontSize, inline.FontSize.Value);
                    }
                    if (inline is Span span)
                    {
                        foreach (var sub in span.Inlines)
                        {
                            if (sub.FontSize.HasValue)
                            {
                                maxFontSize = Math.Max(maxFontSize, sub.FontSize.Value);
                            }
                        }
                    }
                }

                float avgCharWidth = maxFontSize * 0.49f;
                float charsPerLine = Math.Max(10f, availableWidth / avgCharWidth);
                int estimatedLines = (int)Math.Ceiling(charCount / charsPerLine);

                int lineBreaks = CountLineBreaks(paragraph.Inlines);
                estimatedLines = Math.Max(estimatedLines, lineBreaks + 1);

                float blockLineSpacing = (activeFont.Ascender - activeFont.Descender + activeFont.LineGap) * (maxFontSize / activeFont.UnitsPerEm);

                float embeddedHeight = 0f;
                foreach (var inline in paragraph.Inlines)
                {
                    if (inline is InlineUIContainer uic && uic.Child != null)
                    {
                        if (uic.Child.HeightConstraint.HasValue)
                        {
                            embeddedHeight += uic.Child.HeightConstraint.Value;
                        }
                        else if (uic.Child is Border border && border.Child is RichTextBlock rtb)
                        {
                            int codeChars = GetInlinesLength(rtb.Inlines);
                            embeddedHeight += Math.Max(40f, (codeChars / 50f) * blockLineSpacing + 20f);
                        }
                        else if (uic.Child is Border border2 && border2.Child is StackPanel quoteStack)
                        {
                            float quoteHeight = 10f;
                            foreach (var quoteChild in quoteStack.Children)
                            {
                                if (quoteChild is RichTextBlock qRtb)
                                {
                                    int quoteChars = GetInlinesLength(qRtb.Inlines);
                                    quoteHeight += Math.Max(20f, (quoteChars / 60f) * blockLineSpacing + 6f);
                                }
                            }
                            embeddedHeight += quoteHeight;
                        }
                        else
                        {
                            embeddedHeight += 100f;
                        }
                    }
                }

                if (embeddedHeight > 0f)
                {
                    return embeddedHeight + block.MarginBottom;
                }

                return estimatedLines * blockLineSpacing + block.MarginBottom;
            }
            else if (block is ListBlock listBlock)
            {
                float listHeight = 0f;
                foreach (var item in listBlock.Items)
                {
                    int itemCharCount = GetInlinesLength(item.Inlines);
                    float avgCharWidth = baseFontSize * 0.49f;
                    float charsPerLine = Math.Max(10f, (availableWidth - listBlock.Indentation) / avgCharWidth);
                    int estimatedLines = (int)Math.Ceiling(itemCharCount / charsPerLine);
                    listHeight += Math.Max(1, estimatedLines) * lineSpacing;
                }
                return listHeight + block.MarginBottom;
            }
            else if (block is Table table)
            {
                float tableHeight = 10f;
                foreach (var row in table.Rows)
                {
                    float rowHeight = baseFontSize + table.CellPadding * 2f;
                    int maxCellChars = 0;
                    foreach (var cell in row.Cells)
                    {
                        maxCellChars = Math.Max(maxCellChars, GetInlinesLength(cell.Inlines));
                    }
                    float cellWidth = availableWidth / Math.Max(1, row.Cells.Count);
                    float charsPerLine = Math.Max(5f, cellWidth / (baseFontSize * 0.49f));
                    int cellLines = (int)Math.Ceiling(maxCellChars / charsPerLine);
                    rowHeight = Math.Max(rowHeight, Math.Max(1, cellLines) * lineSpacing + table.CellPadding * 2f);

                    tableHeight += rowHeight;
                }
                return tableHeight + block.MarginBottom;
            }

            return 30f + block.MarginBottom;
        }

        private static void LayoutBlock(
            Block block,
            RichBlockLayoutCache cache,
            float startY,
            float maxWidth,
            Thickness padding,
            TtfFont activeFont,
            float baseFontSize,
            Brush resolvedFg,
            TextAlignment alignment,
            ElementTheme theme,
            FrameworkElement parent,
            Action<Visual> addChild,
            HashSet<Visual> encounteredChildren,
            RichDocumentLayoutSession layoutSession,
            List<PositionedRichChar> blockChars,
            List<TableVisualDecoration> blockDecorations,
            TextWrapping textWrapping,
            TextReadingOrder textReadingOrder,
            FlowDirection flowDirection,
            bool alignmentIncludesTrailingWhitespace,
            bool ignoreTrailingCharacterSpacing)
        {
            layoutSession.ReleaseCharacters(blockChars);
            blockDecorations.Clear();

            Paragraph? paragraphBlock = block as Paragraph;
            FlowDirection blockFlowDirection = paragraphBlock?.FlowDirection ?? flowDirection;
            float paragraphLeftIndent = paragraphBlock?.LeftIndent ?? 0f;
            float paragraphRightIndent = paragraphBlock?.RightIndent ?? 0f;
            float firstLineIndent = paragraphBlock?.FirstLineIndent ?? 0f;
            float continuationStartX = padding.Left + paragraphLeftIndent;
            float lineStartX = continuationStartX + firstLineIndent;
            float cursorX = lineStartX;
            float cursorY = startY + (paragraphBlock?.SpaceBefore ?? 0f);
            float availableWidth = Math.Max(0f, maxWidth - padding.Horizontal - paragraphLeftIndent - paragraphRightIndent);
            float scale = baseFontSize / activeFont.UnitsPerEm;
            float lineSpacing = (activeFont.Ascender - activeFont.Descender + activeFont.LineGap) * scale;

            if (paragraphBlock?.EditorFormatState is { } paragraphState &&
                FormatListMarker(paragraphState) is { } marker)
            {
                var markerLayout = new TextLayout(
                    marker,
                    activeFont,
                    baseFontSize,
                    float.PositiveInfinity,
                    ProGPU.Text.TextAlignment.Left);
                float markerWidth = markerLayout.ContentSize.X;
                float markerArea = Math.Max(
                    paragraphState.ListTab > 0f ? paragraphState.ListTab : baseFontSize * 1.75f,
                    markerWidth + Math.Max(4f, baseFontSize * 0.35f));
                float markerX;
                if (blockFlowDirection == FlowDirection.RightToLeft)
                {
                    markerX = maxWidth - padding.Right - paragraphRightIndent - markerWidth;
                }
                else
                {
                    markerX = continuationStartX;
                    continuationStartX += markerArea;
                    lineStartX += markerArea;
                    cursorX += markerArea;
                }
                availableWidth = Math.Max(0f, availableWidth - markerArea);
                blockDecorations.Add(new TableVisualDecoration
                {
                    Text = marker,
                    TextPosition = new Vector2(markerX, cursorY),
                    TextForeground = resolvedFg,
                    TextFont = activeFont,
                    TextFontSize = baseFontSize
                });
            }

            List<RichChar> charList = layoutSession.GetRichCharacterScratch();
            if (block is Paragraph paragraph)
            {
                foreach (var inline in paragraph.Inlines)
                {
                    AccumulateInlines(inline, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
                }
            }
            else if (block is Inline inlineBlock)
            {
                AccumulateInlines(inlineBlock, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
            }
            else if (block is ListBlock listBlock)
            {
                AccumulateInlines(listBlock, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
            }
            else if (block is Table tableBlock)
            {
                AccumulateInlines(tableBlock, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
            }

            if (charList.Count == 0)
            {
                cache.Height = block is Paragraph
                    ? (cursorY - startY) + ResolveParagraphLineHeight(paragraphBlock, lineSpacing) + block.MarginBottom
                    : block.MarginBottom;
                if (paragraphBlock?.EditorFormatState is { IsTableRow: true } emptyTableRowState)
                {
                    float rowTop = startY + paragraphBlock.SpaceBefore;
                    float rowHeight = ResolveParagraphLineHeight(paragraphBlock, lineSpacing) +
                        Math.Max(0f, emptyTableRowState.TableCellPadding) * 2f;
                    AppendEditableTableRowDecorations(
                        emptyTableRowState,
                        rowTop,
                        rowHeight,
                        maxWidth,
                        padding,
                        paragraphLeftIndent,
                        paragraphRightIndent,
                        blockFlowDirection,
                        theme,
                        blockDecorations);
                    cache.Height = (rowTop - startY) + rowHeight + block.MarginBottom;
                }
                return;
            }

            for (int i = 0; i < charList.Count; i++)
            {
                RichChar character = charList[i];
                if (character.EmbeddedElement is { } embeddedElement)
                {
                    encounteredChildren.Add(embeddedElement);
                    if (embeddedElement.Parent != parent)
                    {
                        addChild(embeddedElement);
                    }
                    embeddedElement.Measure(new Vector2(availableWidth, float.PositiveInfinity));
                }
                charList[i] = character;
            }

            if (paragraphBlock?.EditorFormatState is { IsTableRow: true } editableTableState)
            {
                float rowHeight = LayoutEditableTableRow(
                    charList,
                    editableTableState,
                    cursorY,
                    maxWidth,
                    padding,
                    paragraphLeftIndent,
                    paragraphRightIndent,
                    baseFontSize,
                    activeFont,
                    theme,
                    blockChars,
                    blockDecorations,
                    textWrapping,
                    textReadingOrder,
                    blockFlowDirection,
                    ignoreTrailingCharacterSpacing);
                cache.Height = (cursorY - startY) + rowHeight + block.MarginBottom;
                return;
            }
            ResolveCharacterFonts(charList, activeFont);

            ParagraphShapingMetrics shapingMetrics = MeasureShapedParagraphAdvances(
                charList,
                activeFont,
                textReadingOrder,
                blockFlowDirection,
                layoutSession);
            float[] shapedAdvances = shapingMetrics.Advances;

            var currentLine = new List<PositionedRichChar>();
            int lastWordStart = -1;
            float lastWordStartCursorX = lineStartX;
            bool hasResetLineIndent = false;

            void CommitLine(List<PositionedRichChar> line, bool isLastLine)
            {
                if (line.Count == 0)
                {
                    cursorY += lineSpacing;
                    return;
                }

                float completedLineHeight = 0f;
                foreach (var pc in line)
                {
                    float h;
                    if (pc.Info.EmbeddedElement != null)
                    {
                        h = pc.Info.EmbeddedElement.DesiredSize.Y;
                    }
                    else
                    {
                        TtfFont charFont = pc.Info.Font ?? activeFont;
                        float charScale = pc.Info.FontSize / charFont.UnitsPerEm;
                        h = (charFont.Ascender - charFont.Descender + charFont.LineGap) * charScale;
                    }
                    completedLineHeight = Math.Max(completedLineHeight, h);
                }
                if (completedLineHeight == 0f) completedLineHeight = lineSpacing;

                foreach (var pc in line)
                {
                    float h;
                    if (pc.Info.EmbeddedElement != null)
                    {
                        h = pc.Info.EmbeddedElement.DesiredSize.Y;
                    }
                    else
                    {
                        TtfFont charFont = pc.Info.Font ?? activeFont;
                        float charScale = pc.Info.FontSize / charFont.UnitsPerEm;
                        h = (charFont.Ascender - charFont.Descender + charFont.LineGap) * charScale;
                    }
                    pc.Position.Y = cursorY + (completedLineHeight - h) / 2f - pc.Info.BaselineOffset;
                }

                sbyte[] lineLevels = GetLineBidiLevels(line, shapingMetrics, out sbyte paragraphLevel);
                ApplyShapedClusterMetrics(line, lineLevels, activeFont, ignoreTrailingCharacterSpacing);
                float logicalCursor = line.Min(static character => character.Position.X);
                for (int logicalIndex = 0; logicalIndex < line.Count; logicalIndex++)
                {
                    PositionedRichChar logical = line[logicalIndex];
                    if (logical.Info.Character == '\t')
                    {
                        logical.ShapedAdvance = ResolvePositionedTabAdvance(
                            paragraphBlock?.EditorFormatState,
                            line,
                        logicalIndex,
                        logicalCursor - padding.Left - paragraphLeftIndent,
                        baseFontSize,
                        blockFlowDirection == FlowDirection.RightToLeft);
                    }
                    logicalCursor += logical.ShapedAdvance;
                }
                int[] visualOrder = BidiParagraph.GetVisualOrder(lineLevels);
                float visualCursorX = line.Min(static character => character.Position.X);
                float visualLineOrigin = visualCursorX;
                for (int visualIndex = 0; visualIndex < visualOrder.Length; visualIndex++)
                {
                    int logicalIndex = visualOrder[visualIndex];
                    PositionedRichChar character = line[logicalIndex];
                    character.Position.X = visualCursorX;
                    visualCursorX += character.ShapedAdvance;
                }

                GetAlignmentBounds(
                    line,
                    alignmentIncludesTrailingWhitespace,
                    visualLineOrigin,
                    visualCursorX,
                    out float alignmentLeft,
                    out float alignmentRight,
                    out int alignmentCharacterCount);
                float lineW = alignmentRight - alignmentLeft;

                float shiftX = 0f;
                TextAlignment align = block is Paragraph pBlock && pBlock.HasExplicitTextAlignment
                    ? pBlock.TextAlignment
                    : alignment;
                if (align == TextAlignment.DetectFromContent)
                {
                    align = paragraphLevel == 0 ? TextAlignment.Left : TextAlignment.Right;
                }
                bool hasDirectionalTabs = blockFlowDirection == FlowDirection.RightToLeft &&
                    line.Any(static character => character.Info.Character == '\t');
                if (align == TextAlignment.Right || hasDirectionalTabs)
                {
                    shiftX = visualLineOrigin + availableWidth - alignmentRight;
                }
                else if (align == TextAlignment.Center)
                {
                    shiftX = visualLineOrigin + availableWidth * 0.5f -
                        (alignmentLeft + alignmentRight) * 0.5f;
                }
                else if (align == TextAlignment.Justify)
                {
                    int spaceCount = 0;
                    for (int k = 0; k < alignmentCharacterCount; k++)
                    {
                        if (line[k].Info.Character == ' ')
                            spaceCount++;
                    }

                    if (!isLastLine && spaceCount > 0 && lineW < availableWidth)
                    {
                        float extraW = availableWidth - lineW;
                        float spaceAddition = extraW / spaceCount;
                        float runningAddition = 0f;
                        for (int k = 0; k < visualOrder.Length; k++)
                        {
                            var pc = line[visualOrder[k]];
                            pc.Position.X += runningAddition;
                            if (pc.Info.Character == ' ')
                            {
                                runningAddition += spaceAddition;
                            }
                        }
                    }
                }

                if (float.IsFinite(shiftX) &&
                    (shiftX > 0f || !alignmentIncludesTrailingWhitespace && Math.Abs(shiftX) > 0.001f))
                {
                    foreach (var pc in line)
                    {
                        pc.Position.X += shiftX;
                    }
                }

                blockChars.AddRange(line);
                cursorY += ResolveParagraphLineHeight(paragraphBlock, completedLineHeight);
            }

            for (int i = 0; i < charList.Count; i++)
            {
                var rc = charList[i];
                char c = rc.Character;

                if (c == '\n')
                {
                    CommitLine(currentLine, true);
                    currentLine = new List<PositionedRichChar>();
                    cursorX = continuationStartX;
                    lastWordStart = -1;
                    hasResetLineIndent = false;
                    continue;
                }

                if (c == '\uFFFD' && rc.SourceInline is Table table)
                {
                    if (currentLine.Count > 0)
                    {
                        CommitLine(currentLine, true);
                        currentLine = new List<PositionedRichChar>();
                    }
                    cursorX = continuationStartX;
                    LayoutTable(table, ref cursorY, availableWidth, rc.LeftIndent, padding, baseFontSize, activeFont, theme, blockChars, blockDecorations, textReadingOrder, blockFlowDirection);
                    lastWordStart = -1;
                    hasResetLineIndent = false;
                    continue;
                }

                float advance = c == '\t'
                    ? ResolveTabAdvance(
                        paragraphBlock?.EditorFormatState,
                        charList,
                        shapedAdvances,
                        i,
                        cursorX - padding.Left - paragraphLeftIndent,
                        baseFontSize,
                        blockFlowDirection == FlowDirection.RightToLeft)
                    : shapedAdvances[i];
                if (c == '\t') shapedAdvances[i] = advance;
                float terminalAdvance = ignoreTrailingCharacterSpacing && c != '\t'
                    ? shapingMetrics.AdvancesWithoutCharacterSpacing[i]
                    : advance;

                if (c == ' ' || c == '\t')
                {
                    lastWordStart = -1;
                }
                else if (lastWordStart == -1)
                {
                    lastWordStart = currentLine.Count;
                    lastWordStartCursorX = cursorX;
                }

                bool safeWordBreak = lastWordStart > 0 && lastWordStart < currentLine.Count &&
                    IsClusterBoundaryBefore(shapingMetrics, currentLine[lastWordStart].Info.TextPosition);
                bool safeCurrentBreak = IsClusterBoundaryBefore(shapingMetrics, i);
                if (textWrapping != TextWrapping.NoWrap &&
                    cursorX + terminalAdvance > maxWidth - padding.Right - paragraphRightIndent &&
                    cursorX > continuationStartX + rc.LeftIndent &&
                    (safeWordBreak || safeCurrentBreak))
                {
                    if (safeWordBreak)
                    {
                        int wrapCount = currentLine.Count - lastWordStart;
                        var wrapped = currentLine.GetRange(lastWordStart, wrapCount);
                        currentLine.RemoveRange(lastWordStart, wrapCount);

                        CommitLine(currentLine, false);
                        currentLine = new List<PositionedRichChar>();

                        float wrapStart = continuationStartX + (wrapped.Count > 0 ? wrapped[0].Info.LeftIndent : rc.LeftIndent);
                        cursorX = wrapStart;
                        hasResetLineIndent = true;

                        foreach (var wc in wrapped)
                        {
                            var remapped = wc;
                            float shift = wc.Position.X - lastWordStartCursorX;
                            remapped.Position = new Vector2(wrapStart + shift, cursorY);
                            currentLine.Add(remapped);

                            float wAdv = shapedAdvances[remapped.Info.TextPosition];
                            cursorX = wrapStart + shift + wAdv;
                        }

                        if (rc.BulletOffset == 0 && !hasResetLineIndent)
                        {
                            cursorX = continuationStartX + rc.LeftIndent;
                            hasResetLineIndent = true;
                        }
                        float finalXVal = cursorX;
                        if (rc.BulletOffset > 0)
                        {
                            finalXVal = continuationStartX + rc.LeftIndent - rc.BulletOffset + (cursorX - continuationStartX);
                        }
                        var pos = new Vector2(finalXVal, cursorY);
                        currentLine.Add(layoutSession.RentPositionedCharacter(rc, pos));
                        cursorX += advance;
                        lastWordStart = 0;
                        lastWordStartCursorX = continuationStartX + rc.LeftIndent;
                        continue;
                    }
                    else if (textWrapping == TextWrapping.Wrap)
                    {
                        CommitLine(currentLine, false);
                        currentLine = new List<PositionedRichChar>();
                        float wrapStart = continuationStartX + rc.LeftIndent;
                        cursorX = wrapStart;
                        hasResetLineIndent = true;
                    }
                }

                if (rc.BulletOffset == 0 && !hasResetLineIndent)
                {
                    cursorX = lineStartX + rc.LeftIndent;
                    hasResetLineIndent = true;
                }
                float finalX = cursorX;
                if (rc.BulletOffset > 0)
                {
                    finalX = continuationStartX + rc.LeftIndent - rc.BulletOffset + (cursorX - continuationStartX);
                }
                var charPos = new Vector2(finalX, cursorY);
                currentLine.Add(layoutSession.RentPositionedCharacter(rc, charPos));
                cursorX += advance;
            }

            if (currentLine.Count > 0)
            {
                CommitLine(currentLine, true);
            }

            cache.Height = (cursorY - startY) + block.MarginBottom;
        }

        private static void AppendEditableTableRowDecorations(
            Microsoft.UI.Text.RichParagraphFormatState state,
            float top,
            float height,
            float maxWidth,
            Thickness padding,
            float leftIndent,
            float rightIndent,
            FlowDirection flowDirection,
            ElementTheme theme,
            List<TableVisualDecoration> decorations)
        {
            float[]? rightEdges = state.TableCellRightEdges;
            if (rightEdges is null || rightEdges.Length == 0) return;
            float contentLeft = padding.Left + leftIndent;
            float contentRight = maxWidth - padding.Right - rightIndent;
            float previousEdge = 0f;
            int cellCount = state.TableCellColumnSpans?.Length ??
                state.TableCellBackgrounds?.Length ??
                rightEdges.Length;
            int logicalColumn = 0;
            for (int cell = 0; cell < cellCount && logicalColumn < rightEdges.Length; cell++)
            {
                int span = Math.Min(
                    GetEditableTableCellSpan(state, cell),
                    rightEdges.Length - logicalColumn);
                int lastColumn = logicalColumn + span - 1;
                float edge = Math.Max(previousEdge + 1f, rightEdges[lastColumn]);
                float width = edge - previousEdge;
                float x = flowDirection == FlowDirection.RightToLeft
                    ? contentRight - edge
                    : contentLeft + previousEdge;
                Brush? background = state.TableCellBackgrounds is { } backgrounds && cell < backgrounds.Length
                    ? ResolveTableBrush(backgrounds[cell], theme)
                    : null;
                byte verticalMerge = state.TableCellVerticalMergeFlags is { } mergeFlags && cell < mergeFlags.Length
                    ? mergeFlags[cell]
                    : (byte)0;
                decorations.Add(new TableVisualDecoration
                {
                    Rect = new Rect(x, top, width, height),
                    Background = background,
                    BorderThickness = state.TableBorderThickness,
                    BorderBrush = ResolveTableBrush(state.TableBorderBrush, theme),
                    SuppressTopBorder = verticalMerge >= 2,
                    SuppressBottomBorder = verticalMerge is 1 or 2
                });
                previousEdge = edge;
                logicalColumn += span;
            }
        }

        private static Brush? ResolveTableBrush(Brush? brush, ElementTheme theme) =>
            brush is ThemeResourceBrush resource
                ? ThemeManager.GetBrush(resource.ResourceKey, theme)
                : brush;

        private static float ResolveParagraphLineHeight(Paragraph? paragraph, float naturalHeight)
        {
            if (paragraph is null) return naturalHeight;
            float spacing = paragraph.LineSpacing;
            return paragraph.LineSpacingRule switch
            {
                Microsoft.UI.Text.LineSpacingRule.OneAndHalf => naturalHeight * 1.5f,
                Microsoft.UI.Text.LineSpacingRule.Double => naturalHeight * 2f,
                Microsoft.UI.Text.LineSpacingRule.AtLeast when spacing > 0f => Math.Max(naturalHeight, spacing),
                Microsoft.UI.Text.LineSpacingRule.Exactly when spacing > 0f => spacing,
                Microsoft.UI.Text.LineSpacingRule.Multiple when spacing > 0f => naturalHeight * spacing,
                Microsoft.UI.Text.LineSpacingRule.Percent when spacing > 0f => naturalHeight * spacing / 100f,
                _ => naturalHeight
            };
        }

        internal static string? FormatListMarker(Microsoft.UI.Text.RichParagraphFormatState state)
        {
            if (state.ListLevelIndex <= 0 || state.ListType is Microsoft.UI.Text.MarkerType.None or Microsoft.UI.Text.MarkerType.Undefined)
                return null;
            if (state.ListStyle == Microsoft.UI.Text.MarkerStyle.NoNumber) return null;
            int value = Math.Max(1, state.ListStart);
            string marker = state.ListType switch
            {
                Microsoft.UI.Text.MarkerType.Bullet or Microsoft.UI.Text.MarkerType.BlackCircleWingding => "•",
                Microsoft.UI.Text.MarkerType.WhiteCircleWingding => "◦",
                Microsoft.UI.Text.MarkerType.LowercaseEnglishLetter => FormatAlphabetic(value, upper: false),
                Microsoft.UI.Text.MarkerType.UppercaseEnglishLetter => FormatAlphabetic(value, upper: true),
                Microsoft.UI.Text.MarkerType.LowercaseRoman => FormatRoman(value).ToLowerInvariant(),
                Microsoft.UI.Text.MarkerType.UppercaseRoman => FormatRoman(value),
                Microsoft.UI.Text.MarkerType.CircledNumber when value is >= 1 and <= 20 => char.ConvertFromUtf32(0x245F + value),
                Microsoft.UI.Text.MarkerType.UnicodeSequence when value <= char.MaxValue => ((char)value).ToString(),
                _ => value.ToString(CultureInfo.InvariantCulture)
            };
            return state.ListStyle switch
            {
                Microsoft.UI.Text.MarkerStyle.Parenthesis => marker + ")",
                Microsoft.UI.Text.MarkerStyle.Parentheses => "(" + marker + ")",
                Microsoft.UI.Text.MarkerStyle.Plain => marker,
                Microsoft.UI.Text.MarkerStyle.Minus => marker + "-",
                _ => marker + "."
            };
        }

        private static string FormatAlphabetic(int value, bool upper)
        {
            Span<char> buffer = stackalloc char[16];
            int cursor = buffer.Length;
            int current = value;
            while (current > 0 && cursor > 0)
            {
                current--;
                buffer[--cursor] = (char)((upper ? 'A' : 'a') + current % 26);
                current /= 26;
            }
            return new string(buffer[cursor..]);
        }

        private static string FormatRoman(int value)
        {
            if (value is <= 0 or > 3999) return value.ToString(CultureInfo.InvariantCulture);
            ReadOnlySpan<int> values = [1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1];
            ReadOnlySpan<string> symbols = ["M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I"];
            var builder = new StringBuilder(12);
            for (int index = 0; index < values.Length; index++)
            {
                while (value >= values[index])
                {
                    builder.Append(symbols[index]);
                    value -= values[index];
                }
            }
            return builder.ToString();
        }

        private static float ResolveTabAdvance(
            Microsoft.UI.Text.RichParagraphFormatState? paragraph,
            IReadOnlyList<RichChar> characters,
            IReadOnlyList<float> advances,
            int tabIndex,
            float current,
            float fontSize,
            bool rightToLeft)
        {
            if (paragraph is { IsTableRow: true, TableCellRightEdges: { Length: > 0 } cellEdges })
            {
                int cellIndex = 0;
                for (int index = 0; index < tabIndex; index++)
                    if (characters[index].Character == '\t') cellIndex++;
                float cellTarget = cellEdges[Math.Min(cellIndex, cellEdges.Length - 1)] +
                    Math.Max(0f, paragraph.TableCellPadding);
                return Math.Max(1f, cellTarget - current);
            }
            float defaultInterval = Math.Max(1f, paragraph?.DefaultTabStop ?? fontSize * 4f);
            Microsoft.UI.Text.RichTextTab? selected = null;
            if (paragraph is not null)
            {
                for (int index = 0; index < paragraph.Tabs.Count; index++)
                {
                    if (paragraph.Tabs[index].Position <= current + 0.01f) continue;
                    selected = paragraph.Tabs[index];
                    break;
                }
            }
            float target = selected?.Position ?? (MathF.Floor(current / defaultInterval) + 1f) * defaultInterval;
            float followingWidth = 0f;
            float decimalWidth = 0f;
            bool foundDecimal = false;
            for (int index = tabIndex + 1; index < characters.Count; index++)
            {
                char character = characters[index].Character;
                if (character is '\t' or '\n') break;
                if (!foundDecimal && character == '.')
                {
                    decimalWidth = followingWidth;
                    foundDecimal = true;
                }
                followingWidth += advances[index];
            }
            float desiredStart = selected?.Alignment switch
            {
                Microsoft.UI.Text.TabAlignment.Left when rightToLeft => target - followingWidth,
                Microsoft.UI.Text.TabAlignment.Right when rightToLeft => target,
                Microsoft.UI.Text.TabAlignment.Right => target - followingWidth,
                Microsoft.UI.Text.TabAlignment.Center => target - followingWidth * 0.5f,
                Microsoft.UI.Text.TabAlignment.Decimal => target - (foundDecimal ? decimalWidth : followingWidth),
                _ => target
            };
            if (desiredStart <= current)
                desiredStart = (MathF.Floor(current / defaultInterval) + 1f) * defaultInterval;
            return Math.Max(1f, desiredStart - current);
        }

        private static float ResolvePositionedTabAdvance(
            Microsoft.UI.Text.RichParagraphFormatState? paragraph,
            IReadOnlyList<PositionedRichChar> characters,
            int tabIndex,
            float current,
            float fontSize,
            bool rightToLeft)
        {
            if (paragraph is { IsTableRow: true, TableCellRightEdges: { Length: > 0 } cellEdges })
            {
                int cellIndex = 0;
                for (int index = 0; index < tabIndex; index++)
                    if (characters[index].Info.Character == '\t') cellIndex++;
                float cellTarget = cellEdges[Math.Min(cellIndex, cellEdges.Length - 1)] +
                    Math.Max(0f, paragraph.TableCellPadding);
                return Math.Max(1f, cellTarget - current);
            }
            float defaultInterval = Math.Max(1f, paragraph?.DefaultTabStop ?? fontSize * 4f);
            Microsoft.UI.Text.RichTextTab? selected = null;
            if (paragraph is not null)
            {
                for (int index = 0; index < paragraph.Tabs.Count; index++)
                {
                    if (paragraph.Tabs[index].Position <= current + 0.01f) continue;
                    selected = paragraph.Tabs[index];
                    break;
                }
            }
            float target = selected?.Position ?? (MathF.Floor(current / defaultInterval) + 1f) * defaultInterval;
            float followingWidth = 0f;
            float decimalWidth = 0f;
            bool foundDecimal = false;
            for (int index = tabIndex + 1; index < characters.Count; index++)
            {
                char character = characters[index].Info.Character;
                if (character is '\t' or '\n') break;
                if (!foundDecimal && character == '.')
                {
                    decimalWidth = followingWidth;
                    foundDecimal = true;
                }
                followingWidth += characters[index].ShapedAdvance;
            }
            float desiredStart = selected?.Alignment switch
            {
                Microsoft.UI.Text.TabAlignment.Left when rightToLeft => target - followingWidth,
                Microsoft.UI.Text.TabAlignment.Right when rightToLeft => target,
                Microsoft.UI.Text.TabAlignment.Right => target - followingWidth,
                Microsoft.UI.Text.TabAlignment.Center => target - followingWidth * 0.5f,
                Microsoft.UI.Text.TabAlignment.Decimal => target - (foundDecimal ? decimalWidth : followingWidth),
                _ => target
            };
            if (desiredStart <= current)
                desiredStart = (MathF.Floor(current / defaultInterval) + 1f) * defaultInterval;
            return Math.Max(1f, desiredStart - current);
        }

        private static void GetAlignmentBounds(
            IReadOnlyList<PositionedRichChar> line,
            bool includeTrailingWhitespace,
            float fullLeft,
            float fullRight,
            out float left,
            out float right,
            out int characterCount)
        {
            characterCount = line.Count;
            if (!includeTrailingWhitespace)
            {
                while (characterCount > 0 &&
                       char.IsWhiteSpace(line[characterCount - 1].Info.Character))
                {
                    characterCount--;
                }
            }

            // An all-whitespace line has no visible alignment box. Retaining its
            // full advance keeps its logical caret stops stable and reachable.
            if (characterCount == 0 || characterCount == line.Count)
            {
                left = fullLeft;
                right = fullRight;
                return;
            }

            left = float.PositiveInfinity;
            right = float.NegativeInfinity;
            for (int index = 0; index < characterCount; index++)
            {
                PositionedRichChar character = line[index];
                left = Math.Min(left, character.Position.X);
                right = Math.Max(right, character.Position.X + character.ShapedAdvance);
            }
        }

        public static float LayoutSingleColumn(
            IReadOnlyList<Block> blocks,
            float maxWidth,
            Thickness padding,
            TtfFont activeFont,
            float baseFontSize,
            Brush? defaultFg,
            TextAlignment alignment,
            ElementTheme theme,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> tableDecorations,
            FrameworkElement parent,
            Action<Visual> addChild,
            Action<Visual> removeChild,
            TextWrapping textWrapping = TextWrapping.Wrap,
            TextReadingOrder textReadingOrder = TextReadingOrder.DetectFromContent,
            FlowDirection flowDirection = FlowDirection.LeftToRight,
            RichDocumentLayoutSession? layoutSession = null,
            bool alignmentIncludesTrailingWhitespace = false,
            bool ignoreTrailingCharacterSpacing = false)
        {
            positionedChars.Clear();
            tableDecorations.Clear();

            layoutSession ??= new RichDocumentLayoutSession();
            layoutSession.RetainOnly(blocks);

            List<Visual> currentChildren = layoutSession.CurrentChildren;
            currentChildren.Clear();
            currentChildren.AddRange(parent.Children);
            if (activeFont == null || blocks.Count == 0)
            {
                foreach (var child in currentChildren)
                {
                    removeChild(child);
                }
                return 0f;
            }

            var resolvedFg = defaultFg ?? ThemeManager.GetBrush("TextPrimary", theme);
            HashSet<Visual> encounteredChildren = layoutSession.EncounteredChildren;
            encounteredChildren.Clear();

            // Invalidate only this presenter's cache when a complete layout key changes.
            foreach (var block in blocks)
            {
                RichBlockLayoutCache cache = layoutSession.GetOrCreate(block);
                if (!cache.Matches(
                        maxWidth,
                        padding,
                        activeFont,
                        baseFontSize,
                        defaultFg,
                        alignment,
                        theme,
                        textWrapping,
                        textReadingOrder,
                        flowDirection,
                        alignmentIncludesTrailingWhitespace,
                        ignoreTrailingCharacterSpacing))
                {
                    cache.IsLayoutValid = false;
                    cache.Height = -1f;
                    layoutSession.ReleaseCharacters(cache.Characters);
                    cache.Decorations.Clear();
                }
            }

            // 1. Locate ScrollViewer ancestor and compute relative Y offset for viewport virtualization
            ScrollViewer? scrollViewer = null;
            float relativeY = 0f;
            var current = parent.Parent;
            var visualChild = (Visual)parent;
            while (current != null)
            {
                if (current is ScrollViewer sv)
                {
                    scrollViewer = sv;
                    break;
                }
                relativeY += visualChild.Offset.Y;
                visualChild = current;
                current = current.Parent;
            }

            float visibleTop = 0f;
            float visibleBottom = float.PositiveInfinity;

            // 2. Anchor scroll offset to prevent jumpiness on dynamic height refinement
            Block? anchorBlock = null;
            float anchorRelativeOffset = 0f;
            float initialAnchorY = 0f;

            float availableWidth = maxWidth - padding.Horizontal;
            float cursorY = padding.Top;

            int iterations = 0;
            while (iterations < 2)
            {
                if (scrollViewer != null)
                {
                    float viewportTop = scrollViewer.VerticalOffset;
                    float viewportHeight = scrollViewer.Size.Y > 0f ? scrollViewer.Size.Y : 800f; // Default fallback to 800px if not arranged yet
                    float buffer = Math.Max(1000f, viewportHeight * 2.0f); // 2.0 viewport pre-fetch buffer
                    visibleTop = Math.Max(0f, viewportTop - buffer);
                    visibleBottom = viewportTop + viewportHeight + buffer;
                }

                if (scrollViewer != null && iterations == 0)
                {
                    float currentScrollY = scrollViewer.VerticalOffset - relativeY;
                    foreach (var block in blocks)
                    {
                        RichBlockLayoutCache cache = layoutSession.GetOrCreate(block);
                        if (cache.Height > 0f)
                        {
                            if (cache.YOffset <= currentScrollY && cache.YOffset + cache.Height > currentScrollY)
                            {
                                anchorBlock = block;
                                anchorRelativeOffset = currentScrollY - cache.YOffset;
                                initialAnchorY = cache.YOffset;
                                break;
                            }
                        }
                    }
                }

                cursorY = padding.Top;
                encounteredChildren.Clear();

                // Pass 1: Offset assignment and block-level measurement (lazy / viewport-driven)
                int logicalTextOffset = 0;
                foreach (var block in blocks)
                {
                    RichBlockLayoutCache cache = layoutSession.GetOrCreate(block);
                    cache.YOffset = cursorY;

                    // Detect visible intersection using current height (cached actual or estimated fallback) offset by relativeY
                    float absoluteY = relativeY + cursorY;
                    float currentHeight = cache.Height > 0f ? cache.Height : EstimateBlockHeight(block, availableWidth, baseFontSize, activeFont);
                    bool intersects = (absoluteY + currentHeight >= visibleTop) && (absoluteY <= visibleBottom);

                    if (intersects)
                    {
                        bool isCacheValid = cache.Matches(
                            maxWidth,
                            padding,
                            activeFont,
                            baseFontSize,
                            defaultFg,
                            alignment,
                            theme,
                            textWrapping,
                            textReadingOrder,
                            flowDirection,
                            alignmentIncludesTrailingWhitespace,
                            ignoreTrailingCharacterSpacing);

                        if (!isCacheValid)
                        {
                            LayoutBlock(block, cache, cursorY, maxWidth, padding, activeFont, baseFontSize, resolvedFg, alignment, theme, parent, addChild, encounteredChildren, layoutSession, cache.Characters, cache.Decorations, textWrapping, textReadingOrder, flowDirection, alignmentIncludesTrailingWhitespace, ignoreTrailingCharacterSpacing);
                            cache.LogicalTextOffset = 0;
                            RebaseBlockCharacters(cache, logicalTextOffset);
                            cache.SetKey(maxWidth, padding, activeFont, baseFontSize, defaultFg, alignment, theme, textWrapping, textReadingOrder, flowDirection, alignmentIncludesTrailingWhitespace, ignoreTrailingCharacterSpacing);
                        }
                        else
                        {
                            RebaseBlockCharacters(cache, logicalTextOffset);
                        }
                        cursorY += cache.Height;
                    }
                    else
                    {
                        // Drop off-screen content and return character objects to the
                        // presenter-local bounded pool for the next realized block.
                        if (cache.IsLayoutValid)
                        {
                            cache.IsLayoutValid = false;
                            layoutSession.ReleaseCharacters(cache.Characters);
                            cache.Decorations.Clear();
                        }
                        if (cache.Height <= 0f)
                        {
                            cache.Height = EstimateBlockHeight(block, availableWidth, baseFontSize, activeFont);
                        }
                        cursorY += cache.Height;
                    }
                    logicalTextOffset += GetCachedBlockTextLength(block, cache) + block.LogicalTextSeparatorLength;
                }

                // Adjust scroll anchoring if preceding block measurements caused absolute shifting
                bool anchorShifted = false;
                if (scrollViewer != null && anchorBlock != null)
                {
                    float newAnchorY = layoutSession.GetOrCreate(anchorBlock).YOffset;
                    float deltaY = newAnchorY - initialAnchorY;
                    if (Math.Abs(deltaY) > 0.1f)
                    {
                        scrollViewer.VerticalOffset = newAnchorY + anchorRelativeOffset + relativeY;
                        anchorShifted = true;
                    }
                }

                if (!anchorShifted)
                {
                    break;
                }
                iterations++;
            }

            // Pass 2: Gather visible chars and decorations, and measure any newly visible blocks
            int gatheredLogicalTextOffset = 0;
            foreach (var block in blocks)
            {
                RichBlockLayoutCache cache = layoutSession.GetOrCreate(block);
                float blockTop = cache.YOffset;
                float blockBottom = blockTop + cache.Height;

                float absoluteTop = relativeY + blockTop;
                float absoluteBottom = relativeY + blockBottom;

                bool intersects = (absoluteBottom >= visibleTop) && (absoluteTop <= visibleBottom);
                if (intersects)
                {
                    if (!cache.Matches(maxWidth, padding, activeFont, baseFontSize, defaultFg, alignment, theme, textWrapping, textReadingOrder, flowDirection, alignmentIncludesTrailingWhitespace, ignoreTrailingCharacterSpacing))
                    {
                        LayoutBlock(block, cache, blockTop, maxWidth, padding, activeFont, baseFontSize, resolvedFg, alignment, theme, parent, addChild, encounteredChildren, layoutSession, cache.Characters, cache.Decorations, textWrapping, textReadingOrder, flowDirection, alignmentIncludesTrailingWhitespace, ignoreTrailingCharacterSpacing);
                        cache.LogicalTextOffset = 0;
                        RebaseBlockCharacters(cache, gatheredLogicalTextOffset);
                        cache.SetKey(maxWidth, padding, activeFont, baseFontSize, defaultFg, alignment, theme, textWrapping, textReadingOrder, flowDirection, alignmentIncludesTrailingWhitespace, ignoreTrailingCharacterSpacing);
                    }
                    else
                    {
                        RebaseBlockCharacters(cache, gatheredLogicalTextOffset);
                    }

                    positionedChars.AddRange(cache.Characters);
                    tableDecorations.AddRange(cache.Decorations);

                    // Ensure all embedded elements in this block are marked as encountered so they are not recycled
                    foreach (var pc in cache.Characters)
                    {
                        if (pc.Info.EmbeddedElement != null)
                        {
                            var child = pc.Info.EmbeddedElement;
                            encounteredChildren.Add(child);
                            if (child.Parent != parent)
                            {
                                addChild(child);
                            }
                            child.Measure(new Vector2(availableWidth, float.PositiveInfinity));
                        }
                    }
                }
                gatheredLogicalTextOffset += GetCachedBlockTextLength(block, cache) + block.LogicalTextSeparatorLength;
            }

            // Cleanup recycled off-screen UI controls
            foreach (var child in currentChildren)
            {
                if (child is FrameworkElement fe && !encounteredChildren.Contains(fe))
                {
                    removeChild(fe);
                }
            }

            // Preserve the existing diagnostics surface without using model-owned
            // state as the authoritative cache.
            foreach (Block block in blocks)
            {
                RichBlockLayoutCache cache = layoutSession.GetOrCreate(block);
                block.CachedHeight = cache.Height;
                block.CachedYOffset = cache.YOffset;
                block.IsLayoutValid = cache.IsLayoutValid;
            }

            return cursorY + padding.Bottom;
        }

        private static void LayoutTable(
            Table table,
            ref float cursorY,
            float availableWidth,
            float leftIndent,
            Thickness padding,
            float baseFontSize,
            TtfFont activeFont,
            ElementTheme theme,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> tableDecorations,
            TextReadingOrder textReadingOrder,
            FlowDirection flowDirection)
        {
            flowDirection = table.FlowDirection ?? flowDirection;
            MeasuredTableLayout layout = MeasureTable(
                table,
                Math.Max(0f, availableWidth - leftIndent),
                baseFontSize,
                activeFont,
                theme,
                textReadingOrder,
                flowDirection);
            AppendMeasuredTable(
                table,
                layout,
                padding.Left + leftIndent,
                cursorY,
                theme,
                flowDirection,
                positionedChars,
                tableDecorations);
            cursorY += layout.Height;
        }

        private sealed class MeasuredTableCell
        {
            public required TableCell Cell { get; init; }
            public required List<PositionedRichChar> Characters { get; init; }
            public required List<TableVisualDecoration> Decorations { get; init; }
            public int Row;
            public int Column;
            public int RowSpan;
            public int ColumnSpan;
            public float Width;
            public float ContentHeight;
        }

        private sealed class MeasuredTableLayout
        {
            public required float[] ColumnWidths { get; init; }
            public required float[] RowHeights { get; init; }
            public required List<MeasuredTableCell> Cells { get; init; }
            public float Width;
            public float Height;
        }

        private static MeasuredTableLayout MeasureTable(
            Table table,
            float availableWidth,
            float baseFontSize,
            TtfFont activeFont,
            ElementTheme theme,
            TextReadingOrder textReadingOrder,
            FlowDirection flowDirection)
        {
            var placements = new List<(TableCell Cell, int Row, int Column, int RowSpan, int ColumnSpan)>();
            var occupiedUntilRow = new List<int>();
            int columnCount = table.ColumnWidths?.Count ?? 0;
            for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                int logicalColumn = 0;
                foreach (TableCell cell in table.Rows[rowIndex].Cells)
                {
                    int columnSpan = Math.Max(1, cell.ColumnSpan);
                    while (true)
                    {
                        while (logicalColumn < occupiedUntilRow.Count &&
                               occupiedUntilRow[logicalColumn] > rowIndex)
                        {
                            logicalColumn++;
                        }
                        while (occupiedUntilRow.Count < logicalColumn + columnSpan)
                            occupiedUntilRow.Add(0);
                        bool rangeIsFree = true;
                        for (int offset = 0; offset < columnSpan; offset++)
                        {
                            if (occupiedUntilRow[logicalColumn + offset] <= rowIndex) continue;
                            logicalColumn += offset + 1;
                            rangeIsFree = false;
                            break;
                        }
                        if (rangeIsFree) break;
                    }

                    int rowSpan = Math.Min(Math.Max(1, cell.RowSpan), table.Rows.Count - rowIndex);
                    placements.Add((cell, rowIndex, logicalColumn, rowSpan, columnSpan));
                    for (int offset = 0; offset < columnSpan; offset++)
                        occupiedUntilRow[logicalColumn + offset] = rowIndex + rowSpan;
                    logicalColumn += columnSpan;
                    columnCount = Math.Max(columnCount, logicalColumn);
                }
            }

            if (columnCount == 0)
            {
                return new MeasuredTableLayout
                {
                    ColumnWidths = Array.Empty<float>(),
                    RowHeights = new float[table.Rows.Count],
                    Cells = new List<MeasuredTableCell>()
                };
            }

            var columnWidths = new float[columnCount];
            float remainingWidth = availableWidth;
            if (table.ColumnWidths is { Count: > 0 } requestedWidths)
            {
                for (int column = 0; column < columnCount; column++)
                {
                    float width = column < requestedWidths.Count
                        ? Math.Max(1f, requestedWidths[column])
                        : Math.Max(1f, remainingWidth / Math.Max(1, columnCount - column));
                    columnWidths[column] = width;
                    remainingWidth -= width;
                }
            }
            else
            {
                float equalWidth = availableWidth / columnCount;
                Array.Fill(columnWidths, equalWidth);
            }

            float naturalRowHeight = baseFontSize + table.CellPadding * 2f;
            var rowHeights = new float[table.Rows.Count];
            Array.Fill(rowHeights, naturalRowHeight);
            var measuredCells = new List<MeasuredTableCell>(placements.Count);
            foreach ((TableCell cell, int row, int column, int rowSpan, int columnSpan) in placements)
            {
                float width = 0f;
                for (int offset = 0; offset < columnSpan && column + offset < columnWidths.Length; offset++)
                    width += columnWidths[column + offset];
                LayoutTableCellContent(
                    cell,
                    width,
                    table.CellPadding,
                    baseFontSize,
                    activeFont,
                    theme,
                    textReadingOrder,
                    flowDirection,
                    out List<PositionedRichChar> characters,
                    out List<TableVisualDecoration> decorations,
                    out float contentHeight);
                measuredCells.Add(new MeasuredTableCell
                {
                    Cell = cell,
                    Characters = characters,
                    Decorations = decorations,
                    Row = row,
                    Column = column,
                    RowSpan = rowSpan,
                    ColumnSpan = columnSpan,
                    Width = width,
                    ContentHeight = contentHeight
                });
                if (rowSpan == 1) rowHeights[row] = Math.Max(rowHeights[row], contentHeight);
            }

            // First establish ordinary rows, then satisfy spanning-cell minimums by
            // growing the final row in each span. This is deterministic and keeps
            // preceding row baselines stable when content is edited incrementally.
            foreach (MeasuredTableCell cell in measuredCells)
            {
                if (cell.RowSpan <= 1) continue;
                float allocated = 0f;
                for (int row = cell.Row; row < cell.Row + cell.RowSpan; row++)
                    allocated += rowHeights[row];
                if (allocated < cell.ContentHeight)
                    rowHeights[cell.Row + cell.RowSpan - 1] += cell.ContentHeight - allocated;
            }

            var result = new MeasuredTableLayout
            {
                ColumnWidths = columnWidths,
                RowHeights = rowHeights,
                Cells = measuredCells
            };
            for (int column = 0; column < columnWidths.Length; column++) result.Width += columnWidths[column];
            for (int row = 0; row < rowHeights.Length; row++) result.Height += rowHeights[row];
            return result;
        }

        private static void LayoutTableCellContent(
            TableCell cell,
            float cellWidth,
            float cellPadding,
            float baseFontSize,
            TtfFont activeFont,
            ElementTheme theme,
            TextReadingOrder textReadingOrder,
            FlowDirection flowDirection,
            out List<PositionedRichChar> characters,
            out List<TableVisualDecoration> decorations,
            out float contentHeight)
        {
            decorations = new List<TableVisualDecoration>();
            if (!cell.Inlines.Any(static inline => inline is Table))
            {
                characters = LayoutCellChars(
                    cell,
                    cellWidth,
                    cellPadding,
                    out contentHeight,
                    baseFontSize,
                    activeFont,
                    theme,
                    textReadingOrder,
                    flowDirection);
                return;
            }

            var outputCharacters = new List<PositionedRichChar>();
            var ordinary = new List<RichChar>();
            float verticalOffset = 0f;
            Brush defaultForeground = ThemeManager.GetBrush("TextPrimary", theme);

            void FlushOrdinary()
            {
                if (ordinary.Count == 0) return;
                List<PositionedRichChar> segment = LayoutCellChars(
                    ordinary,
                    cellWidth,
                    cellPadding,
                    out float segmentHeight,
                    baseFontSize,
                    activeFont,
                    TextWrapping.Wrap,
                    textReadingOrder,
                    flowDirection);
                foreach (PositionedRichChar character in segment)
                    character.Position += new Vector2(0f, verticalOffset);
                outputCharacters.AddRange(segment);
                verticalOffset += Math.Max(0f, segmentHeight - cellPadding);
                ordinary.Clear();
            }

            foreach (Inline inline in cell.Inlines)
            {
                if (inline is not Table nestedTable)
                {
                    AccumulateInlines(
                        inline,
                        ordinary,
                        defaultForeground,
                        baseFontSize,
                        false,
                        false,
                        false,
                        theme);
                    continue;
                }

                FlushOrdinary();
                float nestedWidth = Math.Max(1f, cellWidth - cellPadding * 2f);
                FlowDirection nestedDirection = nestedTable.FlowDirection ?? flowDirection;
                MeasuredTableLayout nestedLayout = MeasureTable(
                    nestedTable,
                    nestedWidth,
                    baseFontSize,
                    activeFont,
                    theme,
                    textReadingOrder,
                    nestedDirection);
                AppendMeasuredTable(
                    nestedTable,
                    nestedLayout,
                    cellPadding,
                    verticalOffset + cellPadding,
                    theme,
                    nestedDirection,
                    outputCharacters,
                    decorations);
                verticalOffset += nestedLayout.Height + cellPadding * 2f;
            }
            FlushOrdinary();
            characters = outputCharacters;
            contentHeight = Math.Max(
                baseFontSize + cellPadding * 2f,
                verticalOffset + cellPadding);
        }

        private static void AppendMeasuredTable(
            Table table,
            MeasuredTableLayout layout,
            float originX,
            float originY,
            ElementTheme theme,
            FlowDirection flowDirection,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> tableDecorations)
        {
            var columnOffsets = new float[layout.ColumnWidths.Length + 1];
            for (int column = 0; column < layout.ColumnWidths.Length; column++)
                columnOffsets[column + 1] = columnOffsets[column] + layout.ColumnWidths[column];
            var rowOffsets = new float[layout.RowHeights.Length + 1];
            for (int row = 0; row < layout.RowHeights.Length; row++)
                rowOffsets[row + 1] = rowOffsets[row] + layout.RowHeights[row];

            Brush? borderBrush = table.BorderBrush;
            if (borderBrush is ThemeResourceBrush borderThemeBrush)
                borderBrush = ThemeManager.GetBrush(borderThemeBrush.ResourceKey, theme);

            foreach (MeasuredTableCell cell in layout.Cells)
            {
                float logicalX = columnOffsets[cell.Column];
                float x = flowDirection == FlowDirection.RightToLeft
                    ? originX + layout.Width - logicalX - cell.Width
                    : originX + logicalX;
                float y = originY + rowOffsets[cell.Row];
                float height = rowOffsets[cell.Row + cell.RowSpan] - rowOffsets[cell.Row];
                Brush? background = cell.Cell.Background;
                if (background is ThemeResourceBrush backgroundThemeBrush)
                    background = ThemeManager.GetBrush(backgroundThemeBrush.ResourceKey, theme);
                tableDecorations.Add(new TableVisualDecoration
                {
                    Rect = new Rect(x, y, cell.Width, height),
                    Background = background,
                    BorderThickness = table.BorderThickness,
                    BorderBrush = borderBrush
                });

                foreach (TableVisualDecoration nested in cell.Decorations)
                {
                    tableDecorations.Add(new TableVisualDecoration
                    {
                        Rect = new Rect(
                            nested.Rect.X + x,
                            nested.Rect.Y + y,
                            nested.Rect.Width,
                            nested.Rect.Height),
                        Background = nested.Background,
                        BorderThickness = nested.BorderThickness,
                        BorderBrush = nested.BorderBrush,
                        IsTop = nested.IsTop,
                        IsLeft = nested.IsLeft,
                        Text = nested.Text,
                        TextPosition = nested.TextPosition + new Vector2(x, y),
                        TextForeground = nested.TextForeground,
                        TextFont = nested.TextFont,
                        TextFontSize = nested.TextFontSize
                    });
                }

                foreach (PositionedRichChar character in cell.Characters)
                {
                    positionedChars.Add(new PositionedRichChar
                    {
                        Info = character.Info,
                        Position = new Vector2(character.Position.X + x, character.Position.Y + y),
                        BidiLevel = character.BidiLevel,
                        ClusterStart = character.ClusterStart,
                        ClusterLength = character.ClusterLength,
                        ShapedAdvance = character.ShapedAdvance,
                        ShapedAdvanceWithoutCharacterSpacing = character.ShapedAdvanceWithoutCharacterSpacing,
                        HasShapedAdvance = character.HasShapedAdvance,
                        ShapingFlags = character.ShapingFlags
                    });
                }
            }
        }

        private static List<PositionedRichChar> LayoutCellChars(
            TableCell cell,
            float cellWidth,
            float cellPadding,
            out float cellHeight,
            float baseFontSize,
            TtfFont activeFont,
            ElementTheme theme,
            TextReadingOrder textReadingOrder,
            FlowDirection flowDirection)
        {
            var charList = new List<RichChar>();
            var defaultFg = ThemeManager.GetBrush("TextPrimary", theme);
            foreach (var inline in cell.Inlines)
            {
                AccumulateInlines(inline, charList, defaultFg, baseFontSize, false, false, false, theme, null, 0f);
            }

            return LayoutCellChars(
                charList,
                cellWidth,
                cellPadding,
                out cellHeight,
                baseFontSize,
                activeFont,
                TextWrapping.Wrap,
                textReadingOrder,
                flowDirection);
        }

        private static float LayoutEditableTableRow(
            List<RichChar> rowCharacters,
            Microsoft.UI.Text.RichParagraphFormatState state,
            float top,
            float maxWidth,
            Thickness padding,
            float leftIndent,
            float rightIndent,
            float baseFontSize,
            TtfFont activeFont,
            ElementTheme theme,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> decorations,
            TextWrapping textWrapping,
            TextReadingOrder textReadingOrder,
            FlowDirection flowDirection,
            bool ignoreTrailingCharacterSpacing)
        {
            float cellPadding = Math.Max(0f, state.TableCellPadding);
            int textCellCount = 1;
            for (int index = 0; index < rowCharacters.Count; index++)
                if (rowCharacters[index].Character == '\t') textCellCount++;
            int cellCount = Math.Max(1, textCellCount);
            int logicalColumnCount = state.TableCellRightEdges?.Length ?? 0;
            if (logicalColumnCount == 0)
            {
                for (int cell = 0; cell < cellCount; cell++)
                    logicalColumnCount += GetEditableTableCellSpan(state, cell);
            }

            var cellCharacters = new List<List<PositionedRichChar>>(cellCount);
            var cellStarts = new int[cellCount];
            var separators = new int[cellCount];
            int sourceStart = 0;
            int logicalColumn = 0;
            float rowHeight = 0f;
            for (int column = 0; column < cellCount; column++)
            {
                int separator = sourceStart;
                while (separator < rowCharacters.Count && rowCharacters[separator].Character != '\t') separator++;
                int length = separator - sourceStart;
                var content = length == 0
                    ? new List<RichChar>()
                    : rowCharacters.GetRange(sourceStart, length);
                int columnSpan = Math.Min(
                    GetEditableTableCellSpan(state, column),
                    Math.Max(1, logicalColumnCount - logicalColumn));
                float columnWidth = GetEditableTableColumnWidth(
                    state,
                    logicalColumn,
                    columnSpan,
                    logicalColumnCount,
                    maxWidth - padding.Horizontal - leftIndent - rightIndent);
                List<PositionedRichChar> cell = LayoutCellChars(
                    content,
                    columnWidth,
                    cellPadding,
                    out float cellHeight,
                    baseFontSize,
                    activeFont,
                    textWrapping,
                    textReadingOrder,
                    flowDirection,
                    ignoreTrailingCharacterSpacing);
                cellCharacters.Add(cell);
                cellStarts[column] = sourceStart;
                separators[column] = separator;
                rowHeight = Math.Max(rowHeight, cellHeight);
                sourceStart = separator < rowCharacters.Count ? separator + 1 : separator;
                logicalColumn += columnSpan;
            }

            float scale = baseFontSize / activeFont.UnitsPerEm;
            float naturalLineHeight = (activeFont.Ascender - activeFont.Descender + activeFont.LineGap) * scale;
            rowHeight = Math.Max(rowHeight, naturalLineHeight + cellPadding * 2f);
            float contentLeft = padding.Left + leftIndent;
            float contentRight = maxWidth - padding.Right - rightIndent;
            float previousEdge = 0f;
            logicalColumn = 0;
            for (int column = 0; column < cellCount; column++)
            {
                int columnSpan = Math.Min(
                    GetEditableTableCellSpan(state, column),
                    Math.Max(1, logicalColumnCount - logicalColumn));
                float width = GetEditableTableColumnWidth(
                    state,
                    logicalColumn,
                    columnSpan,
                    logicalColumnCount,
                    contentRight - contentLeft);
                float edge = previousEdge + width;
                float cellX = flowDirection == FlowDirection.RightToLeft
                    ? contentRight - edge
                    : contentLeft + previousEdge;
                List<PositionedRichChar> cell = cellCharacters[column];
                for (int index = 0; index < cell.Count; index++)
                {
                    PositionedRichChar character = cell[index];
                    character.Info.TextPosition += cellStarts[column];
                    character.ClusterStart += cellStarts[column];
                    character.Position = new Vector2(
                        character.Position.X + cellX,
                        character.Position.Y + top);
                    positionedChars.Add(character);
                }

                int separator = separators[column];
                if (separator < rowCharacters.Count && rowCharacters[separator].Character == '\t')
                {
                    RichChar tab = rowCharacters[separator];
                    tab.TextPosition = separator;
                    positionedChars.Add(new PositionedRichChar
                    {
                        Info = tab,
                        Position = new Vector2(
                            flowDirection == FlowDirection.RightToLeft
                                ? cellX + cellPadding
                                : cellX + Math.Max(cellPadding, width - cellPadding),
                            top + cellPadding),
                        BidiLevel = flowDirection == FlowDirection.RightToLeft ? (sbyte)1 : (sbyte)0,
                        ClusterStart = separator,
                        ClusterLength = 1,
                        ShapedAdvance = Math.Max(1f, cellPadding * 2f),
                        HasShapedAdvance = true
                    });
                }
                previousEdge = edge;
                logicalColumn += columnSpan;
            }

            AppendEditableTableRowDecorations(
                state,
                top,
                rowHeight,
                maxWidth,
                padding,
                leftIndent,
                rightIndent,
                flowDirection,
                theme,
                decorations);
            return rowHeight;
        }

        private static float GetEditableTableColumnWidth(
            Microsoft.UI.Text.RichParagraphFormatState state,
            int logicalColumn,
            int columnSpan,
            int logicalColumnCount,
            float availableWidth)
        {
            if (state.TableCellRightEdges is { Length: > 0 } edges && logicalColumn < edges.Length)
            {
                float left = logicalColumn == 0 ? 0f : edges[logicalColumn - 1];
                int last = Math.Min(edges.Length - 1, logicalColumn + Math.Max(1, columnSpan) - 1);
                return Math.Max(1f, edges[last] - left);
            }
            float consumed = state.TableCellRightEdges is { Length: > 0 } known
                ? known[^1]
                : 0f;
            int remaining = Math.Max(1, logicalColumnCount - logicalColumn);
            return Math.Max(1f, (availableWidth - consumed) * Math.Max(1, columnSpan) / remaining);
        }

        private static int GetEditableTableCellSpan(
            Microsoft.UI.Text.RichParagraphFormatState state,
            int cell) => state.TableCellColumnSpans is { } spans && cell < spans.Length
                ? Math.Max(1, spans[cell])
                : 1;

        private static List<PositionedRichChar> LayoutCellChars(
            List<RichChar> charList,
            float cellWidth,
            float cellPadding,
            out float cellHeight,
            float baseFontSize,
            TtfFont activeFont,
            TextWrapping textWrapping,
            TextReadingOrder textReadingOrder,
            FlowDirection flowDirection,
            bool ignoreTrailingCharacterSpacing = false)
        {
            var positionedChars = new List<PositionedRichChar>(charList.Count);
            cellHeight = cellPadding * 2f;

            if (charList.Count == 0) return positionedChars;

            for (int i = 0; i < charList.Count; i++)
            {
                RichChar character = charList[i];
                if (character.EmbeddedElement is { } embeddedElement)
                {
                    embeddedElement.Measure(new Vector2(Math.Max(0f, cellWidth - cellPadding * 2f), float.PositiveInfinity));
                }
                charList[i] = character;
            }
            ResolveCharacterFonts(charList, activeFont);

            ParagraphShapingMetrics shapingMetrics = MeasureShapedParagraphAdvances(
                charList,
                activeFont,
                textReadingOrder,
                flowDirection);
            float[] shapedAdvances = shapingMetrics.Advances;

            float scale = baseFontSize / activeFont.UnitsPerEm;
            float lineSpacing = (activeFont.Ascender - activeFont.Descender + activeFont.LineGap) * scale;

            float cursorX = cellPadding;
            float cursorY = cellPadding;

            var currentLine = new List<PositionedRichChar>();
            int lastWordStart = -1;
            float lastWordStartCursorX = cellPadding;

            void CommitCellLine(List<PositionedRichChar> line)
            {
                if (line.Count == 0)
                {
                    cursorY += lineSpacing;
                    return;
                }

                float maxElementHeight = 0f;
                foreach (var pc in line)
                {
                    if (pc.Info.EmbeddedElement != null)
                    {
                        maxElementHeight = Math.Max(maxElementHeight, pc.Info.EmbeddedElement.DesiredSize.Y);
                    }
                }
                float completedLineHeight = Math.Max(lineSpacing, maxElementHeight);

                foreach (var pc in line)
                {
                    float h = pc.Info.EmbeddedElement != null ? pc.Info.EmbeddedElement.DesiredSize.Y : lineSpacing;
                    pc.Position.Y = cursorY + (completedLineHeight - h) / 2f - pc.Info.BaselineOffset;
                }

                sbyte[] lineLevels = GetLineBidiLevels(line, shapingMetrics, out _);
                ApplyShapedClusterMetrics(line, lineLevels, activeFont, ignoreTrailingCharacterSpacing);
                int[] visualOrder = BidiParagraph.GetVisualOrder(lineLevels);
                float visualCursorX = cellPadding;
                for (int visualIndex = 0; visualIndex < visualOrder.Length; visualIndex++)
                {
                    PositionedRichChar character = line[visualOrder[visualIndex]];
                    character.Position.X = visualCursorX;
                    visualCursorX += character.ShapedAdvance;
                }

                positionedChars.AddRange(line);
                cursorY += completedLineHeight;
            }

            for (int i = 0; i < charList.Count; i++)
            {
                var rc = charList[i];
                char c = rc.Character;

                if (c == '\n')
                {
                    CommitCellLine(currentLine);
                    currentLine = new List<PositionedRichChar>();
                    cursorX = cellPadding;
                    lastWordStart = -1;
                    continue;
                }

                float advance = shapedAdvances[i];
                float terminalAdvance = ignoreTrailingCharacterSpacing
                    ? shapingMetrics.AdvancesWithoutCharacterSpacing[i]
                    : advance;

                if (c == ' ' || c == '\t')
                {
                    lastWordStart = -1;
                }
                else if (lastWordStart == -1)
                {
                    lastWordStart = currentLine.Count;
                    lastWordStartCursorX = cursorX;
                }

                bool safeWordBreak = lastWordStart > 0 && lastWordStart < currentLine.Count &&
                    IsClusterBoundaryBefore(shapingMetrics, currentLine[lastWordStart].Info.TextPosition);
                bool safeCurrentBreak = IsClusterBoundaryBefore(shapingMetrics, i);
                if (textWrapping != TextWrapping.NoWrap &&
                    cursorX + terminalAdvance > cellWidth - cellPadding && cursorX > cellPadding &&
                    (safeWordBreak || safeCurrentBreak))
                {
                    if (safeWordBreak)
                    {
                        int wrapCount = currentLine.Count - lastWordStart;
                        var wrapped = currentLine.GetRange(lastWordStart, wrapCount);
                        currentLine.RemoveRange(lastWordStart, wrapCount);

                        CommitCellLine(currentLine);
                        currentLine = new List<PositionedRichChar>();

                        cursorX = cellPadding;

                        foreach (var wc in wrapped)
                        {
                            var remapped = wc;
                            float shift = wc.Position.X - lastWordStartCursorX;
                            remapped.Position = new Vector2(cellPadding + shift, cursorY);
                            currentLine.Add(remapped);

                            float wAdv = shapedAdvances[remapped.Info.TextPosition];
                            cursorX = cellPadding + shift + wAdv;
                        }

                        var pos = new Vector2(cursorX, cursorY);
                        currentLine.Add(new PositionedRichChar { Info = rc, Position = pos });
                        cursorX += advance;
                        lastWordStart = 0;
                        lastWordStartCursorX = cellPadding;
                        continue;
                    }
                    else
                    {
                        CommitCellLine(currentLine);
                        currentLine = new List<PositionedRichChar>();
                        cursorX = cellPadding;
                    }
                }

                var charPos = new Vector2(cursorX, cursorY);
                currentLine.Add(new PositionedRichChar { Info = rc, Position = charPos });
                cursorX += advance;
            }

            if (currentLine.Count > 0)
            {
                CommitCellLine(currentLine);
            }

            cellHeight = cursorY + cellPadding;
            return positionedChars;
        }

        public static void LayoutMultiColumn(
            IReadOnlyList<Block> blocks,
            IReadOnlyList<Paragraph> extraParagraphs,
            float width,
            float height,
            Thickness padding,
            int columnCount,
            float columnGap,
            TtfFont activeFont,
            float baseFontSize,
            Brush? defaultFg,
            ElementTheme theme,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> tableDecorations,
            FrameworkElement parent,
            Action<Visual> addChild,
            Action<Visual> removeChild,
            TextReadingOrder textReadingOrder = TextReadingOrder.DetectFromContent,
            FlowDirection flowDirection = FlowDirection.LeftToRight,
            TextAlignment alignment = TextAlignment.Left)
        {
            positionedChars.Clear();
            tableDecorations.Clear();

            var currentChildren = new List<Visual>(parent.Children);

            var allBlocks = new List<Block>();
            allBlocks.AddRange(blocks);
            foreach (var p in extraParagraphs)
            {
                if (!allBlocks.Contains(p)) allBlocks.Add(p);
            }

            if (activeFont == null || allBlocks.Count == 0 || width <= 0f || height <= 0f)
            {
                foreach (var child in currentChildren)
                {
                    removeChild(child);
                }
                return;
            }

            float scale = baseFontSize / activeFont.UnitsPerEm;
            float lineSpacing = (activeFont.Ascender - activeFont.Descender + activeFont.LineGap) * scale;

            float availableWidth = width - padding.Horizontal;
            float colWidth = (availableWidth - (columnCount - 1) * columnGap) / columnCount;
            float colHeight = height - padding.Vertical;

            int currentColumn = 0;
            float cursorX = flowDirection == FlowDirection.RightToLeft
                ? padding.Left + (columnCount - 1) * (colWidth + columnGap)
                : padding.Left;
            float cursorY = padding.Top;

            var resolvedFg = defaultFg ?? ThemeManager.GetBrush("TextPrimary", theme);

            var encounteredChildren = new HashSet<Visual>();

            foreach (var block in allBlocks)
            {
                Paragraph? paragraphBlock = block as Paragraph;
                FlowDirection blockFlowDirection = paragraphBlock?.FlowDirection ?? flowDirection;
                Microsoft.UI.Text.RichParagraphFormatState? paragraphState = paragraphBlock?.EditorFormatState;
                var charList = new List<RichChar>();
                if (block is Paragraph paragraph)
                {
                    foreach (var inline in paragraph.Inlines)
                    {
                        AccumulateInlines(inline, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
                    }
                }
                else if (block is ListBlock listBlock)
                {
                    AccumulateInlines(listBlock, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
                }
                else if (block is Table tableBlock)
                {
                    AccumulateInlines(tableBlock, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
                }
                else if (block is Inline inlineBlock)
                {
                    AccumulateInlines(inlineBlock, charList, resolvedFg, baseFontSize, false, false, false, theme, null, 0f);
                }

                if (charList.Count == 0) continue;

                for (int charIndex = 0; charIndex < charList.Count; charIndex++)
                {
                    RichChar character = charList[charIndex];
                    if (character.EmbeddedElement is { } embeddedElement)
                    {
                        encounteredChildren.Add(embeddedElement);
                        if (embeddedElement.Parent != parent)
                        {
                            addChild(embeddedElement);
                        }
                        embeddedElement.Measure(new Vector2(colWidth, float.PositiveInfinity));
                    }
                    charList[charIndex] = character;
                }
                ResolveCharacterFonts(charList, activeFont);

                ParagraphShapingMetrics shapingMetrics = MeasureShapedParagraphAdvances(
                    charList,
                    activeFont,
                    textReadingOrder,
                    blockFlowDirection);
                float[] shapedAdvances = shapingMetrics.Advances;

                var paragraphLines = new List<(List<PositionedRichChar> Chars, float ColumnX, int[] VisualOrder, sbyte ParagraphLevel)>();
                int i = 0;
                bool hasResetLineIndent = false;

                while (i < charList.Count)
                {
                    var lineChars = new List<RichChar>();
                    float lineW = 0f;
                    int lastWordIdx = -1;

                    while (i < charList.Count)
                    {
                        var rc = charList[i];
                        char c = rc.Character;

                        if (c == '\n')
                        {
                            i++;
                            break;
                        }

                        if (c == '\uFFFD' && rc.SourceInline is Table)
                        {
                            break;
                        }

                        float advance = c == '\t'
                            ? ResolveTabAdvance(
                                paragraphState,
                                charList,
                                shapedAdvances,
                                i,
                                lineW,
                                baseFontSize,
                                blockFlowDirection == FlowDirection.RightToLeft)
                            : shapedAdvances[i];
                        if (c == '\t') shapedAdvances[i] = advance;

                        if (rc.Character == ' ' || rc.Character == '\t')
                        {
                            lastWordIdx = lineChars.Count;
                        }

                        bool safeWordBreak = lastWordIdx > 0 && lastWordIdx < lineChars.Count &&
                            IsClusterBoundaryBefore(shapingMetrics, lineChars[lastWordIdx].TextPosition);
                        bool safeCurrentBreak = IsClusterBoundaryBefore(shapingMetrics, i);
                        if (lineW + advance > colWidth && lineChars.Count > 0 &&
                            (safeWordBreak || safeCurrentBreak))
                        {
                            if (safeWordBreak)
                            {
                                int diff = lineChars.Count - lastWordIdx;
                                lineChars.RemoveRange(lastWordIdx, diff);
                                i -= diff;
                            }
                            break;
                        }

                        lineChars.Add(rc);
                        lineW += advance;
                        i++;
                    }

                    if (lineChars.Count > 0)
                    {
                        float lineMaxH = lineSpacing;
                        foreach (var rc in lineChars)
                        {
                            if (rc.EmbeddedElement != null)
                            {
                                lineMaxH = Math.Max(lineMaxH, rc.EmbeddedElement.DesiredSize.Y);
                            }
                            else
                            {
                                lineMaxH = Math.Max(lineMaxH, rc.FontSize);
                            }
                        }

                        if (cursorY + lineMaxH > padding.Top + colHeight && cursorY > padding.Top)
                        {
                            currentColumn++;
                            if (currentColumn >= columnCount)
                            {
                                break;
                            }
                            cursorX = flowDirection == FlowDirection.RightToLeft
                                ? padding.Left + (columnCount - 1 - currentColumn) * (colWidth + columnGap)
                                : padding.Left + currentColumn * (colWidth + columnGap);
                            cursorY = padding.Top;
                        }

                        var currentLine = new List<PositionedRichChar>();
                        float runningX = cursorX;
                        hasResetLineIndent = false;

                        foreach (var rc in lineChars)
                        {
                            float advance = shapedAdvances[rc.TextPosition];
                            float elementH = 0f;
                            if (rc.EmbeddedElement != null)
                            {
                                elementH = rc.EmbeddedElement.DesiredSize.Y;
                            }
                            else
                            {
                                elementH = rc.FontSize;
                            }

                            if (rc.BulletOffset == 0 && !hasResetLineIndent)
                            {
                                runningX = cursorX + rc.LeftIndent;
                                hasResetLineIndent = true;
                            }

                            float finalX = runningX;
                            if (rc.BulletOffset > 0)
                            {
                                finalX = cursorX + rc.LeftIndent - rc.BulletOffset + (runningX - cursorX);
                            }

                            float yOffset = (lineMaxH - elementH) / 2f;

                            currentLine.Add(new PositionedRichChar
                            {
                                Info = rc,
                                Position = new Vector2(finalX, cursorY + yOffset)
                            });
                            runningX += advance;
                        }

                        sbyte[] lineLevels = GetLineBidiLevels(currentLine, shapingMetrics, out sbyte paragraphLevel);
                        ApplyShapedClusterMetrics(currentLine, lineLevels, activeFont);
                        float logicalCursor = 0f;
                        for (int logicalIndex = 0; logicalIndex < currentLine.Count; logicalIndex++)
                        {
                            PositionedRichChar logical = currentLine[logicalIndex];
                            if (logical.Info.Character == '\t')
                            {
                                logical.ShapedAdvance = ResolvePositionedTabAdvance(
                                    paragraphState,
                                    currentLine,
                                    logicalIndex,
                                    logicalCursor,
                                    baseFontSize,
                                    blockFlowDirection == FlowDirection.RightToLeft);
                            }
                            logicalCursor += logical.ShapedAdvance;
                        }
                        int[] visualOrder = BidiParagraph.GetVisualOrder(lineLevels);
                        float visualCursorX = currentLine.Min(static character => character.Position.X);
                        for (int visualIndex = 0; visualIndex < visualOrder.Length; visualIndex++)
                        {
                            PositionedRichChar character = currentLine[visualOrder[visualIndex]];
                            character.Position.X = visualCursorX;
                            visualCursorX += character.ShapedAdvance;
                        }

                        paragraphLines.Add((currentLine, cursorX, visualOrder, paragraphLevel));
                        cursorY += lineMaxH;
                    }

                    if (i < charList.Count && charList[i].Character == '\uFFFD' && charList[i].SourceInline is Table tbl)
                    {
                        LayoutTableFlow(tbl, ref currentColumn, ref cursorX, ref cursorY, colWidth, colHeight, padding, columnCount, columnGap, baseFontSize, activeFont, theme, positionedChars, tableDecorations, textReadingOrder, flowDirection);
                        i++;
                        hasResetLineIndent = false;
                    }
                }

                for (int l = 0; l < paragraphLines.Count; l++)
                {
                    var (line, lineColumnX, visualOrder, paragraphLevel) = paragraphLines[l];
                    if (line.Count == 0) continue;

                    float lineRight = line.Max(static character => character.Position.X + character.ShapedAdvance);
                    float lineW = lineRight - lineColumnX;

                    float shiftX = 0f;
                    TextAlignment align = block is Paragraph pBlock && pBlock.HasExplicitTextAlignment
                        ? pBlock.TextAlignment
                        : alignment;
                    if (align == TextAlignment.DetectFromContent)
                    {
                        align = paragraphLevel % 2 == 0 ? TextAlignment.Left : TextAlignment.Right;
                    }
                    bool hasDirectionalTabs = blockFlowDirection == FlowDirection.RightToLeft &&
                        line.Any(static character => character.Info.Character == '\t');
                    if (align == TextAlignment.Right || hasDirectionalTabs)
                    {
                        shiftX = colWidth - lineW;
                    }
                    else if (align == TextAlignment.Center)
                    {
                        shiftX = (colWidth - lineW) / 2f;
                    }
                    else if (align == TextAlignment.Justify)
                    {
                        bool isLastLine = (l == paragraphLines.Count - 1);
                        int spaceCount = 0;
                        for (int k = 0; k < line.Count - 1; k++)
                        {
                            if (line[k].Info.Character == ' ')
                                spaceCount++;
                        }

                        if (!isLastLine && spaceCount > 0 && lineW < colWidth)
                        {
                            float extraW = colWidth - lineW;
                            float spaceAddition = extraW / spaceCount;
                            float runningAddition = 0f;
                            for (int k = 0; k < visualOrder.Length; k++)
                            {
                                var pc = line[visualOrder[k]];
                                pc.Position.X += runningAddition;
                                if (pc.Info.Character == ' ')
                                {
                                    runningAddition += spaceAddition;
                                }
                            }
                        }
                    }

                    if (shiftX > 0f && !float.IsInfinity(shiftX))
                    {
                        foreach (var pc in line)
                        {
                            pc.Position.X += shiftX;
                        }
                    }

                    positionedChars.AddRange(line);
                }

                cursorY += block.MarginBottom;
                if (currentColumn >= columnCount) break;
            }

            foreach (var child in currentChildren)
            {
                if (child is FrameworkElement fe && !encounteredChildren.Contains(fe))
                {
                    removeChild(fe);
                }
            }
        }

        private static void LayoutTableFlow(
            Table table,
            ref int currentColumn,
            ref float cursorX,
            ref float cursorY,
            float colWidth,
            float colHeight,
            Thickness padding,
            int columnCount,
            float columnGap,
            float baseFontSize,
            TtfFont activeFont,
            ElementTheme theme,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> tableDecorations,
            TextReadingOrder textReadingOrder,
            FlowDirection flowDirection)
        {
            flowDirection = table.FlowDirection ?? flowDirection;
            MeasuredTableLayout layout = MeasureTable(
                table,
                colWidth,
                baseFontSize,
                activeFont,
                theme,
                textReadingOrder,
                flowDirection);
            if (layout.ColumnWidths.Length == 0) return;

            // A row span cannot be split without duplicating its border/background
            // and defining a fragment ownership model. Keep the measured table
            // atomic when it fits a column; oversized tables intentionally overflow
            // one column instead of corrupting merged-cell geometry.
            if (cursorY > padding.Top &&
                cursorY + layout.Height > padding.Top + colHeight &&
                currentColumn + 1 < columnCount)
            {
                currentColumn++;
                cursorX = flowDirection == FlowDirection.RightToLeft
                    ? padding.Left + (columnCount - 1 - currentColumn) * (colWidth + columnGap)
                    : padding.Left + currentColumn * (colWidth + columnGap);
                cursorY = padding.Top;
            }

            AppendMeasuredTable(
                table,
                layout,
                cursorX,
                cursorY,
                theme,
                flowDirection,
                positionedChars,
                tableDecorations);
            cursorY += layout.Height;
        }

        public static void Render(
            DrawingContext context,
            List<PositionedRichChar> positionedChars,
            List<TableVisualDecoration> tableDecorations,
            TtfFont activeFont,
            int selectionStart,
            int selectionLength,
            IReadOnlyList<RichEditTableCellRange>? tableSelection,
            Hyperlink? hoveredHyperlink,
            Brush? selectionHighlightBrush = null)
        {
            if (activeFont == null) return;

            foreach (var dec in tableDecorations)
            {
                if (!string.IsNullOrEmpty(dec.Text) && dec.TextFont is { } markerFont && dec.TextForeground is { } markerBrush)
                {
                    context.DrawText(
                        dec.Text,
                        markerFont,
                        dec.TextFontSize,
                        markerBrush,
                        dec.TextPosition);
                }
                if (dec.Background != null)
                {
                    context.DrawRectangle(dec.Background, null, dec.Rect);
                }
                if (dec.BorderBrush != null && dec.BorderThickness > 0f)
                {
                    var pen = new Pen(dec.BorderBrush, dec.BorderThickness);
                    if (!dec.SuppressTopBorder && !dec.SuppressBottomBorder)
                    {
                        context.DrawRectangle(null, pen, dec.Rect);
                    }
                    else
                    {
                        float left = dec.Rect.X;
                        float top = dec.Rect.Y;
                        float right = dec.Rect.X + dec.Rect.Width;
                        float bottom = dec.Rect.Y + dec.Rect.Height;
                        if (!dec.SuppressTopBorder)
                            context.DrawLine(pen, new Vector2(left, top), new Vector2(right, top));
                        context.DrawLine(pen, new Vector2(left, top), new Vector2(left, bottom));
                        context.DrawLine(pen, new Vector2(right, top), new Vector2(right, bottom));
                        if (!dec.SuppressBottomBorder)
                            context.DrawLine(pen, new Vector2(left, bottom), new Vector2(right, bottom));
                    }
                }
            }

            if (positionedChars.Count == 0) return;

            if ((selectionStart >= 0 && selectionLength > 0) || tableSelection is { Count: > 0 })
            {
                for (int i = 0; i < positionedChars.Count; i++)
                {
                    var pc = positionedChars[i];
                    int clusterStart = pc.ClusterStart;
                    int clusterEnd = clusterStart + Math.Max(1, pc.ClusterLength);
                    int selectionEnd = selectionStart + selectionLength;
                    bool isSelected = tableSelection is { Count: > 0 }
                        ? IsInTableSelection(tableSelection, clusterStart, clusterEnd)
                        : clusterStart < selectionEnd && clusterEnd > selectionStart;
                    if (pc.ShapedAdvance > 0f && isSelected)
                    {
                        if (pc.Info.EmbeddedElement != null) continue;
                        TtfFont charFont = pc.Info.Font ?? activeFont;
                        ushort gIdx = charFont.GetGlyphIndex(pc.Info.Character);
                        float advance = pc.HasShapedAdvance
                            ? pc.ShapedAdvance
                            : charFont.GetAdvanceWidth(gIdx, pc.Info.FontSize);
                        context.DrawRectangle(selectionHighlightBrush ?? SelectionHighlightBrush, null, new Rect(pc.Position.X, pc.Position.Y, advance, pc.Info.FontSize));
                    }
                }
            }

            var runBuffer = new StringBuilder(Math.Min(positionedChars.Count, 4096));
            Vector2 startPos = Vector2.Zero;
            RichChar style = default;
            sbyte runBidiLevel = 0;
            float runWidth = 0f;

            void FlushRun()
            {
                if (runBuffer.Length == 0)
                {
                    return;
                }

                RenderRun(
                    context,
                    runBuffer.ToString(),
                    startPos,
                    runWidth,
                    style,
                    style.Font ?? activeFont,
                    runBidiLevel);
                runBuffer.Clear();
                runWidth = 0f;
            }

            foreach (var pc in positionedChars)
            {
                if (pc.Info.EmbeddedElement != null)
                {
                    FlushRun();
                    continue;
                }

                var pcStyle = pc.Info;
                if (pcStyle.IsHidden)
                {
                    FlushRun();
                    continue;
                }
                if (pc.Info.SourceInline is Hyperlink hl && hl == hoveredHyperlink)
                {
                    pcStyle.Foreground = HoveredHyperlinkBrush;
                }

                if (pc.Info.Character == ' ' || pc.Info.Character == '\t')
                {
                    FlushRun();
                    RenderRun(
                        context,
                        pc.Info.Character.ToString(),
                        pc.Position,
                        pc.ShapedAdvance,
                        pcStyle,
                        pcStyle.Font ?? activeFont,
                        pc.BidiLevel);
                    continue;
                }

                if (runBuffer.Length == 0)
                {
                    runBuffer.Append(pc.Info.Character);
                    startPos = pc.Position;
                    style = pcStyle;
                    runBidiLevel = pc.BidiLevel;
                    runWidth = pc.ShapedAdvance;
                }
                else if (pcStyle.IsBold == style.IsBold &&
                         pcStyle.IsItalic == style.IsItalic &&
                         pcStyle.IsUnderline == style.IsUnderline &&
                         pcStyle.IsAllCaps == style.IsAllCaps &&
                         pcStyle.IsSmallCaps == style.IsSmallCaps &&
                         pcStyle.IsStrikethrough == style.IsStrikethrough &&
                         Equals(pcStyle.Background, style.Background) &&
                         pcStyle.CharacterSpacing == 0f &&
                         style.CharacterSpacing == 0f &&
                         pcStyle.BaselineOffset == style.BaselineOffset &&
                         pcStyle.FontSize == style.FontSize &&
                         pcStyle.Foreground.Equals(style.Foreground) &&
                         pcStyle.Font == style.Font &&
                         pc.BidiLevel == runBidiLevel &&
                         Math.Abs(pc.Position.Y - startPos.Y) < 1f)
                {
                    runBuffer.Append(pc.Info.Character);
                    startPos.X = Math.Min(startPos.X, pc.Position.X);
                    runWidth += pc.ShapedAdvance;
                }
                else
                {
                    FlushRun();
                    runBuffer.Append(pc.Info.Character);
                    startPos = pc.Position;
                    style = pcStyle;
                    runBidiLevel = pc.BidiLevel;
                    runWidth = pc.ShapedAdvance;
                }
            }

            FlushRun();
        }

        private static bool IsInTableSelection(
            IReadOnlyList<RichEditTableCellRange> selection,
            int clusterStart,
            int clusterEnd)
        {
            // Cell ranges are sorted by source position. Visual glyph order can be
            // bidi-reordered, so use a bounded binary search rather than a moving cursor.
            int low = 0;
            int high = selection.Count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                RichEditTableCellRange range = selection[middle];
                if (clusterEnd <= range.StartPosition) high = middle - 1;
                else if (clusterStart >= range.EndPosition) low = middle + 1;
                else return true;
            }
            return false;
        }

        private static void RenderRun(
            DrawingContext context,
            string text,
            Vector2 pos,
            float width,
            RichChar style,
            TtfFont activeFont,
            sbyte bidiLevel)
        {
            if (activeFont == null) return;
            if (style.IsAllCaps) text = TransformUppercaseOneToOne(text);
            TtfFont runFont = style.Font ?? activeFont;
            bool simulateBold = style.IsBold && runFont.WeightClass < 600;
            bool simulateItalic = style.IsItalic && !runFont.IsItalic;
            TextShapingOptions shapingOptions = CreateShapingOptions(style, bidiLevel);
            if (style.Background is not null)
                context.DrawRectangle(style.Background, null, new Rect(pos.X, pos.Y, width, style.FontSize));
            context.DrawText(
                text,
                runFont,
                style.FontSize,
                style.Foreground!,
                pos,
                simulateBold,
                simulateItalic,
                textShapingOptions: shapingOptions);
            if (style.IsUnderline)
            {
                DrawUnderline(context, style, text, pos, width);
            }
            if (style.IsStrikethrough)
                context.DrawRectangle(style.Foreground, null, new Rect(pos.X, pos.Y + style.FontSize * 0.55f, width, 1f));
        }

        private static void DrawUnderline(DrawingContext context, RichChar style, string text, Vector2 position, float width)
        {
            Microsoft.UI.Text.UnderlineType type = style.UnderlineType is Microsoft.UI.Text.UnderlineType.None or Microsoft.UI.Text.UnderlineType.Undefined
                ? Microsoft.UI.Text.UnderlineType.Single
                : style.UnderlineType;
            if (type == Microsoft.UI.Text.UnderlineType.Words && string.IsNullOrWhiteSpace(text)) return;
            float y = position.Y + style.FontSize - 1f;
            float thickness = type is Microsoft.UI.Text.UnderlineType.Thick or
                Microsoft.UI.Text.UnderlineType.ThickDash or
                Microsoft.UI.Text.UnderlineType.ThickDashDot or
                Microsoft.UI.Text.UnderlineType.ThickDashDotDot or
                Microsoft.UI.Text.UnderlineType.ThickDotted or
                Microsoft.UI.Text.UnderlineType.ThickLongDash ? 2f : 1f;
            if (type is Microsoft.UI.Text.UnderlineType.Double or Microsoft.UI.Text.UnderlineType.DoubleWave)
            {
                context.DrawRectangle(style.Foreground, null, new Rect(position.X, y - 2f, width, 1f));
                context.DrawRectangle(style.Foreground, null, new Rect(position.X, y + 1f, width, 1f));
                return;
            }
            bool patterned = type is Microsoft.UI.Text.UnderlineType.Dotted or
                Microsoft.UI.Text.UnderlineType.Dash or
                Microsoft.UI.Text.UnderlineType.DashDot or
                Microsoft.UI.Text.UnderlineType.DashDotDot or
                Microsoft.UI.Text.UnderlineType.LongDash or
                Microsoft.UI.Text.UnderlineType.ThickDash or
                Microsoft.UI.Text.UnderlineType.ThickDashDot or
                Microsoft.UI.Text.UnderlineType.ThickDashDotDot or
                Microsoft.UI.Text.UnderlineType.ThickDotted or
                Microsoft.UI.Text.UnderlineType.ThickLongDash or
                Microsoft.UI.Text.UnderlineType.Wave or
                Microsoft.UI.Text.UnderlineType.HeavyWave;
            if (!patterned)
            {
                context.DrawRectangle(style.Foreground, null, new Rect(position.X, y, width, thickness));
                return;
            }
            float dash = type is Microsoft.UI.Text.UnderlineType.Dotted or Microsoft.UI.Text.UnderlineType.ThickDotted ? thickness : Math.Max(2f, style.FontSize * 0.25f);
            float gap = Math.Max(1f, thickness);
            for (float x = 0f; x < width; x += dash + gap)
                context.DrawRectangle(style.Foreground, null, new Rect(position.X + x, y, Math.Min(dash, width - x), thickness));
        }

        private static string TransformUppercaseOneToOne(string text)
        {
            char[]? transformed = null;
            for (int index = 0; index < text.Length; index++)
            {
                char upper = char.ToUpperInvariant(text[index]);
                if (upper == text[index]) continue;
                transformed ??= text.ToCharArray();
                transformed[index] = upper;
            }
            return transformed is null ? text : new string(transformed);
        }
    }
}
