using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ProGPU.Text.Bidi;
using ProGPU.Text.Shaping;

namespace ProGPU.Text;

public enum TextAlignment
{
    Left,
    Center,
    Right,
    Justify
}

public struct TextRunGlyph
{
    public char Character;
    public uint CodePoint;
    public ushort GlyphIndex;
    public Vector2 Position; // Top-Left screen coordinates of the glyph box
    public GlyphInfo Glyph;
    public TtfFont Font; // The font that owns/defines this glyph
    public int Cluster; // UTF-16 index in the original text
    public sbyte BidiLevel; // Resolved UAX #9 embedding level for the source cluster
    public ShapingGlyphFlags ShapingFlags; // HarfBuzz-compatible break/concat safety contract
}

public readonly record struct TextCaretStop(
    int TextPosition,
    bool IsTrailing,
    Vector2 Position,
    float Height,
    sbyte BidiLevel);

public readonly record struct TextBounds(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
}

public readonly record struct TextHitTestResult(
    int TextPosition,
    bool IsTrailingHit,
    bool IsInside,
    TextBounds Bounds,
    sbyte BidiLevel);

public class TextLayout
{
    private const long SharedFallbackFontFileSizeLimit = 16L * 1024L * 1024L;

    [ThreadStatic]
    private static ShapingBuffer? t_shapingBuffer;

    private readonly struct LineRange
    {
        public LineRange(int start, int count)
        {
            Start = start;
            Count = count;
        }

        public int Start { get; }
        public int Count { get; }
    }

    private static readonly string[] FallbackFontPaths = new[]
    {
        "/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc",
        "/System/Library/Fonts/PingFang.ttc",
        "/System/Library/Fonts/Apple Symbols.ttf",
        "/System/Library/Fonts/Apple Color Emoji.ttc"
    };

    private static readonly Lazy<IReadOnlyList<FontInfo>> FallbackFontInfos = new(
        CreateFallbackFontInfos,
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<(string Path, int FaceIndex), Lazy<TtfFont?>> SharedFallbackFonts = new();
    private static readonly ConcurrentDictionary<(string Path, int FaceIndex, ushort GlyphIndex), Lazy<TtfFont?>> FallbackFonts = new();
    private static readonly ConcurrentDictionary<string, bool> SharedFallbackDecisions = new(
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
    private static readonly Lazy<Task> FallbackMetadataWarmup = new(
        static () => Task.Run(static () => _ = FallbackFontInfos.Value),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static int EstimateGlyphCapacity(string text)
    {
        var capacity = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                continue;
            }

            capacity++;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
        }

        return Math.Max(1, capacity);
    }

    private static int EstimateLineCapacity(string text)
    {
        var capacity = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                capacity++;
            }
        }

        return capacity;
    }

    private static void AddLineRange(List<LineRange> lines, int start, int end)
    {
        if (end > start)
        {
            lines.Add(new LineRange(start, end - start));
        }
    }

    private static IReadOnlyList<FontInfo> CreateFallbackFontInfos()
    {
        var fallbackFontInfos = new List<FontInfo>();

        for (int pathIndex = 0; pathIndex < FallbackFontPaths.Length; pathIndex++)
        {
            var path = FallbackFontPaths[pathIndex];
            if (System.IO.File.Exists(path))
            {
                List<FontInfo> fileFaces = FontApi.ParseFontInfos(path);
                for (int faceIndex = 0; faceIndex < fileFaces.Count; faceIndex++)
                {
                    fallbackFontInfos.Add(fileFaces[faceIndex]);
                }
            }
        }

        return fallbackFontInfos;
    }

    /// <summary>
    /// Warms the small fallback-face inventory without constructing fonts or retaining
    /// glyph outlines. Hosts can call this during startup so page activation does no I/O discovery.
    /// </summary>
    public static Task WarmUpFallbackMetadataAsync()
    {
        return FallbackMetadataWarmup.Value;
    }

    private static bool TryResolveFallback(
        TtfFont requestedFont,
        uint codePoint,
        out TtfFont? font,
        out ushort glyphIndex)
    {
        if (codePoint <= int.MaxValue &&
            FontApi.TryResolvePlatformFallback(requestedFont, (int)codePoint, out font, out glyphIndex))
        {
            return true;
        }

        IReadOnlyList<FontInfo> fallbackFontInfos = FallbackFontInfos.Value;
        for (int fallbackIndex = 0; fallbackIndex < fallbackFontInfos.Count; fallbackIndex++)
        {
            FontInfo info = fallbackFontInfos[fallbackIndex];
            if (!FontApi.TryGetGlyphIndex(info, codePoint, out ushort metadataGlyphIndex))
            {
                continue;
            }

            font = GetOrLoadFallbackFont(info.FilePath, info.FaceIndex, metadataGlyphIndex);
            if (font is not null)
            {
                glyphIndex = font.GetGlyphIndex(codePoint);
                if (glyphIndex != 0)
                {
                    return true;
                }
            }
        }

        font = null;
        glyphIndex = 0;
        return false;
    }

    internal static TtfFont? GetOrLoadFallbackFont(string path, int faceIndex, ushort glyphIndex)
    {
        if (ShouldShareFallbackFace(path))
        {
            return SharedFallbackFonts.GetOrAdd(
                (path, faceIndex),
                static value => new Lazy<TtfFont?>(
                    () => LoadSharedFallbackFont(value.Path, value.FaceIndex),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        return FallbackFonts.GetOrAdd(
            (path, faceIndex, glyphIndex),
            static value => new Lazy<TtfFont?>(
                () => LoadGlyphResidentFallbackFont(value.Path, value.FaceIndex, value.GlyphIndex),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static bool ShouldShareFallbackFace(string path)
    {
        return SharedFallbackDecisions.GetOrAdd(
            path,
            static candidate =>
            {
                try
                {
                    return new System.IO.FileInfo(candidate).Length <= SharedFallbackFontFileSizeLimit;
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            });
    }

    private static TtfFont? LoadSharedFallbackFont(string path, int faceIndex)
    {
        try
        {
            var font = new TtfFont(path, faceIndex);
            ProGpuTextDiagnostics.WriteLine($"[TextLayout] Loaded shared system fallback font face {faceIndex}: {path}");
            return font;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ProGpuTextDiagnostics.WriteLine($"[TextLayout] Warning: Failed to load shared fallback font face {faceIndex} from '{path}': {ex.Message}");
            return null;
        }
    }

    private static TtfFont? LoadGlyphResidentFallbackFont(string path, int faceIndex, ushort glyphIndex)
    {
        try
        {
            var font = TtfFont.LoadGlyphResidentFile(path, faceIndex, glyphIndex);
            ProGpuTextDiagnostics.WriteLine($"[TextLayout] Loaded glyph-resident system fallback font face {faceIndex}: {path}");
            return font;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ProGpuTextDiagnostics.WriteLine($"[TextLayout] Warning: Failed to load fallback font face {faceIndex} from '{path}': {ex.Message}");
            return null;
        }
    }

    public string Text { get; private set; } = string.Empty;
    public TtfFont Font { get; private set; } = null!;
    public float FontSize { get; private set; }
    public float MaxWidth { get; private set; }
    public TextAlignment Alignment { get; private set; }
    public TextShapingOptions ShapingOptions { get; private set; } = TextShapingOptions.Default;

    public List<TextRunGlyph> Glyphs { get; } = new();
    public Vector2 ContentSize { get; private set; }
    public Vector2 MeasuredSize { get; private set; }
    public bool HasTextures { get; private set; }

    public TextLayout(
        string text,
        TtfFont font,
        float fontSize,
        float maxWidth = float.PositiveInfinity,
        TextAlignment alignment = TextAlignment.Left,
        GlyphAtlas? atlas = null,
        TextShapingOptions? shapingOptions = null)
    {
        Reset(text, font, fontSize, maxWidth, alignment, atlas, shapingOptions);
    }

    internal void Reset(
        string text,
        TtfFont font,
        float fontSize,
        float maxWidth,
        TextAlignment alignment,
        GlyphAtlas? atlas,
        TextShapingOptions? shapingOptions)
    {
        Text = text ?? string.Empty;
        Font = font;
        FontSize = fontSize;
        MaxWidth = maxWidth;
        Alignment = alignment;
        ShapingOptions = shapingOptions ?? TextShapingOptions.Default;

        GenerateLayout(atlas);
    }

    internal void ClearForReuse()
    {
        Text = string.Empty;
        Font = null!;
        FontSize = 0f;
        MaxWidth = float.PositiveInfinity;
        Alignment = TextAlignment.Left;
        ShapingOptions = TextShapingOptions.Default;
        Glyphs.Clear();
        ContentSize = Vector2.Zero;
        MeasuredSize = Vector2.Zero;
        HasTextures = false;
    }

    public void GenerateLayout(GlyphAtlas? atlas)
    {
        GenerateShapedLayout();
    }

    private readonly record struct ShapedCandidate(
        TtfFont Font,
        ushort GlyphIndex,
        int Cluster,
        uint CodePoint,
        sbyte BidiLevel,
        float AdvanceX,
        float AdvanceY,
        float OffsetX,
        float OffsetY,
        ShapingGlyphFlags Flags)
    {
        public bool IsWhitespace => CodePoint is ' ' or '\t';
    }

    private void GenerateShapedLayout()
    {
        HasTextures = true;
        Glyphs.Clear();
        if (string.IsNullOrEmpty(Text))
        {
            ContentSize = Vector2.Zero;
            MeasuredSize = Vector2.Zero;
            return;
        }

        Glyphs.EnsureCapacity(EstimateGlyphCapacity(Text));
        if (ShapingOptions.Direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop)
        {
            GenerateVerticalShapedLayout();
            return;
        }

        if (TryGenerateSingleLineAsciiLayout())
        {
            return;
        }

        float scale = FontSize / Font.UnitsPerEm;
        float lineSpacing = (Font.Ascender - Font.Descender + Font.LineGap) * scale;
        float fontAscent = Font.Ascender * scale;
        var lines = new List<LineRange>(EstimateLineCapacity(Text));
        var lineWidths = new List<float>(EstimateLineCapacity(Text));
        float cursorY = 0f;
        int sourceStart = 0;

        while (sourceStart <= Text.Length)
        {
            int newline = Text.IndexOf('\n', sourceStart);
            int sourceEnd = newline >= 0 ? newline : Text.Length;
            BidiParagraph paragraph = BidiParagraph.Resolve(
                Text.AsSpan(sourceStart, sourceEnd - sourceStart),
                ShapingOptions.Direction);
            List<ShapedCandidate> candidates = ShapeRange(sourceStart, sourceEnd - sourceStart, paragraph);

            if (candidates.Count == 0)
            {
                lines.Add(new LineRange(Glyphs.Count, 0));
                lineWidths.Add(0f);
                cursorY += lineSpacing;
            }
            else
            {
                int candidateStart = 0;
                while (candidateStart < candidates.Count)
                {
                    float width = 0f;
                    int lastBreak = -1;
                    int lastSafeClusterBreak = -1;
                    int candidateEnd = candidateStart;
                    for (; candidateEnd < candidates.Count; candidateEnd++)
                    {
                        ShapedCandidate candidate = candidates[candidateEnd];
                        if (candidateEnd > candidateStart &&
                            IsSafeBreakBefore(candidates, candidateEnd))
                        {
                            lastSafeClusterBreak = candidateEnd;
                        }
                        if (candidate.IsWhitespace &&
                            IsSafeBreakBefore(candidates, candidateEnd + 1))
                        {
                            lastBreak = candidateEnd + 1;
                        }

                        bool exceeds = !float.IsInfinity(MaxWidth) &&
                                       width + candidate.AdvanceX > MaxWidth &&
                                       candidateEnd > candidateStart;
                        if (exceeds)
                        {
                            if (lastBreak > candidateStart)
                            {
                                candidateEnd = lastBreak;
                            }
                            else if (lastSafeClusterBreak > candidateStart)
                            {
                                candidateEnd = lastSafeClusterBreak;
                            }
                            else
                            {
                                candidateEnd = FindNextSafeBreak(candidates, candidateEnd + 1);
                            }
                            break;
                        }
                        width += candidate.AdvanceX;
                    }

                    if (candidateEnd == candidateStart)
                    {
                        candidateEnd++;
                    }

                    int glyphStart = Glyphs.Count;
                    float cursorX = 0f;
                    List<ShapedCandidate> visualCandidates = GetVisualLineCandidates(
                        candidates,
                        candidateStart,
                        candidateEnd,
                        paragraph.ParagraphLevel);
                    for (var candidateIndex = 0; candidateIndex < visualCandidates.Count; candidateIndex++)
                    {
                        ShapedCandidate candidate = visualCandidates[candidateIndex];
                        float advance = candidate.AdvanceX;
                        var glyph = new GlyphInfo
                        {
                            X = 0,
                            Y = 0,
                            Width = (uint)Math.Max(0f, MathF.Ceiling(advance)),
                            Height = (uint)Math.Max(0f, MathF.Ceiling(lineSpacing)),
                            BearX = 0,
                            BearY = 0,
                            Advance = advance,
                            TexCoordMin = Vector2.Zero,
                            TexCoordMax = Vector2.Zero
                        };
                        int cluster = Math.Clamp(candidate.Cluster, 0, Math.Max(0, Text.Length - 1));
                        Glyphs.Add(new TextRunGlyph
                        {
                            Character = Text[cluster],
                            CodePoint = candidate.CodePoint,
                            GlyphIndex = candidate.GlyphIndex,
                            Cluster = candidate.Cluster,
                            BidiLevel = candidate.BidiLevel,
                            ShapingFlags = candidate.Flags,
                            Position = new Vector2(
                                cursorX + candidate.OffsetX,
                                cursorY + fontAscent + candidate.OffsetY),
                            Glyph = glyph,
                            Font = candidate.Font
                        });
                        cursorX += advance;
                    }

                    lines.Add(new LineRange(glyphStart, Glyphs.Count - glyphStart));
                    lineWidths.Add(cursorX);
                    cursorY += lineSpacing;
                    candidateStart = candidateEnd;
                }
            }

            if (newline < 0)
            {
                break;
            }
            sourceStart = newline + 1;
        }

        float maxLineWidth = 0f;
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            LineRange line = lines[lineIndex];
            float lineWidth = lineWidths[lineIndex];
            maxLineWidth = Math.Max(maxLineWidth, lineWidth);
            float shiftX = Alignment switch
            {
                TextAlignment.Center => (MaxWidth - lineWidth) * 0.5f,
                TextAlignment.Right => MaxWidth - lineWidth,
                _ => 0f
            };
            if (shiftX <= 0f || float.IsInfinity(shiftX))
            {
                continue;
            }

            int lineEnd = line.Start + line.Count;
            for (var glyphIndex = line.Start; glyphIndex < lineEnd; glyphIndex++)
            {
                TextRunGlyph glyph = Glyphs[glyphIndex];
                glyph.Position.X += shiftX;
                Glyphs[glyphIndex] = glyph;
            }
        }

        ContentSize = new Vector2(maxLineWidth, cursorY);
        MeasuredSize = new Vector2(float.IsInfinity(MaxWidth) ? maxLineWidth : MaxWidth, cursorY);
    }

    private bool TryGenerateSingleLineAsciiLayout()
    {
        if (Alignment != TextAlignment.Left ||
            ShapingOptions.Direction is not (ShapingDirection.Unspecified or ShapingDirection.LeftToRight))
        {
            return false;
        }

        for (var index = 0; index < Text.Length; index++)
        {
            char character = Text[index];
            if (character is < ' ' or > '~')
            {
                return false;
            }
        }

        ShapingBuffer shaping = t_shapingBuffer ??= new ShapingBuffer(64);
        OpenTypeTextShaper.ShapeDesignUnits(Text, Font, ShapingOptions, shaping);
        ReadOnlySpan<ShapingGlyph> shaped = shaping.Glyphs;
        float scale = FontSize / Font.UnitsPerEm;
        float width = 0f;
        for (var index = 0; index < shaped.Length; index++)
        {
            width += shaped[index].AdvanceX * scale;
        }
        if (!float.IsInfinity(MaxWidth) && width > MaxWidth)
        {
            return false;
        }

        float lineSpacing = (Font.Ascender - Font.Descender + Font.LineGap) * scale;
        float fontAscent = Font.Ascender * scale;
        float cursorX = 0f;
        Glyphs.EnsureCapacity(shaped.Length);
        for (var index = 0; index < shaped.Length; index++)
        {
            ShapingGlyph candidate = shaped[index];
            if (candidate.GlyphId == 0)
            {
                Glyphs.Clear();
                return false;
            }
            float advance = candidate.AdvanceX * scale;
            int cluster = Math.Clamp(candidate.Cluster, 0, Text.Length - 1);
            Glyphs.Add(new TextRunGlyph
            {
                Character = Text[cluster],
                CodePoint = candidate.CodePoint,
                GlyphIndex = checked((ushort)candidate.GlyphId),
                Cluster = candidate.Cluster,
                ShapingFlags = candidate.Flags,
                Position = new Vector2(
                    cursorX + candidate.OffsetX * scale,
                    fontAscent + candidate.OffsetY * scale),
                Glyph = new GlyphInfo
                {
                    Width = (uint)Math.Max(0f, MathF.Ceiling(advance)),
                    Height = (uint)Math.Max(0f, MathF.Ceiling(lineSpacing)),
                    Advance = advance,
                    TexCoordMin = Vector2.Zero,
                    TexCoordMax = Vector2.Zero
                },
                Font = Font
            });
            cursorX += advance;
        }

        ContentSize = new Vector2(width, lineSpacing);
        MeasuredSize = new Vector2(float.IsInfinity(MaxWidth) ? width : MaxWidth, lineSpacing);
        return true;
    }

    private void GenerateVerticalShapedLayout()
    {
        float scale = FontSize / Font.UnitsPerEm;
        float columnSpacing = (Font.Ascender - Font.Descender + Font.LineGap) * scale;
        float columnX = 0f;
        float maxColumnHeight = 0f;
        int sourceStart = 0;

        while (sourceStart <= Text.Length)
        {
            int newline = Text.IndexOf('\n', sourceStart);
            int sourceEnd = newline >= 0 ? newline : Text.Length;
            List<ShapedCandidate> candidates = ShapeRange(
                sourceStart,
                sourceEnd - sourceStart,
                BidiParagraph.Resolve(Text.AsSpan(sourceStart, sourceEnd - sourceStart), ShapingOptions.Direction));
            float cursorY = 0f;

            for (var index = 0; index < candidates.Count; index++)
            {
                ShapedCandidate candidate = candidates[index];
                float advance = candidate.AdvanceY;
                var glyph = new GlyphInfo
                {
                    X = 0,
                    Y = 0,
                    Width = (uint)Math.Max(0f, MathF.Ceiling(columnSpacing)),
                    Height = (uint)Math.Max(0f, MathF.Ceiling(MathF.Abs(advance))),
                    BearX = 0,
                    BearY = 0,
                    Advance = advance,
                    TexCoordMin = Vector2.Zero,
                    TexCoordMax = Vector2.Zero
                };
                int cluster = Math.Clamp(candidate.Cluster, 0, Math.Max(0, Text.Length - 1));
                Glyphs.Add(new TextRunGlyph
                {
                    Character = Text[cluster],
                    CodePoint = candidate.CodePoint,
                    GlyphIndex = candidate.GlyphIndex,
                    Cluster = candidate.Cluster,
                    BidiLevel = candidate.BidiLevel,
                    ShapingFlags = candidate.Flags,
                    Position = new Vector2(
                        columnX + columnSpacing * 0.5f + candidate.OffsetX,
                        cursorY + candidate.OffsetY),
                    Glyph = glyph,
                    Font = candidate.Font
                });
                cursorY += advance;
            }

            maxColumnHeight = Math.Max(maxColumnHeight, MathF.Abs(cursorY));
            columnX += columnSpacing;
            if (newline < 0) break;
            sourceStart = newline + 1;
        }

        float contentWidth = columnX;
        float shiftX = Alignment switch
        {
            TextAlignment.Center => (MaxWidth - contentWidth) * 0.5f,
            TextAlignment.Right => MaxWidth - contentWidth,
            _ => 0f
        };
        if (shiftX > 0f && !float.IsInfinity(shiftX))
        {
            for (var index = 0; index < Glyphs.Count; index++)
            {
                TextRunGlyph glyph = Glyphs[index];
                glyph.Position.X += shiftX;
                Glyphs[index] = glyph;
            }
        }

        ContentSize = new Vector2(contentWidth, maxColumnHeight);
        MeasuredSize = new Vector2(float.IsInfinity(MaxWidth) ? contentWidth : MaxWidth, maxColumnHeight);
    }

    private List<ShapedCandidate> ShapeRange(int start, int length, BidiParagraph paragraph)
    {
        var candidates = new List<ShapedCandidate>(Math.Max(1, length));
        for (int bidiRunIndex = 0; bidiRunIndex < paragraph.Runs.Length; bidiRunIndex++)
        {
            BidiRun bidiRun = paragraph.Runs[bidiRunIndex];
            AppendFontRuns(
                candidates,
                start + bidiRun.Start,
                bidiRun.Length,
                bidiRun.Level,
                start,
                start + length);
        }
        return candidates;
    }

    private void AppendFontRuns(
        List<ShapedCandidate> candidates,
        int start,
        int length,
        sbyte bidiLevel,
        int paragraphStart,
        int paragraphEnd)
    {
        int end = start + length;
        int fontRunStart = start;
        TtfFont? runFont = null;

        for (int index = start; index < end;)
        {
            int scalarStart = index;
            char character = Text[index++];
            uint codePoint = character;
            if (char.IsHighSurrogate(character) && index < end && char.IsLowSurrogate(Text[index]))
            {
                codePoint = (uint)char.ConvertToUtf32(character, Text[index++]);
            }

            TtfFont resolvedFont = Font;
            ushort glyphIndex = Font.GetGlyphIndex(codePoint);
            if (glyphIndex == 0 && codePoint is not (' ' or '\t'))
            {
                if (runFont is not null && OpenTypeTextShaper.IsDefaultIgnorableCodePoint(codePoint))
                {
                    // Variation selectors, joiners, and other default ignorables belong to
                    // the preceding fallback run even when they have no standalone cmap entry.
                    resolvedFont = runFont;
                }
                else if (TryResolveFallback(Font, codePoint, out TtfFont? fallbackFont, out _) &&
                         fallbackFont is not null)
                {
                    resolvedFont = fallbackFont;
                }
            }

            if (runFont is null)
            {
                runFont = resolvedFont;
                fontRunStart = scalarStart;
            }
            else if (!ReferenceEquals(runFont, resolvedFont))
            {
                AppendShapedRun(
                    candidates,
                    fontRunStart,
                    scalarStart - fontRunStart,
                    runFont,
                    bidiLevel,
                    paragraphStart,
                    paragraphEnd);
                runFont = resolvedFont;
                fontRunStart = scalarStart;
            }
        }

        if (runFont is not null && fontRunStart < end)
        {
            AppendShapedRun(
                candidates,
                fontRunStart,
                end - fontRunStart,
                runFont,
                bidiLevel,
                paragraphStart,
                paragraphEnd);
        }
    }

    private void AppendShapedRun(
        List<ShapedCandidate> candidates,
        int start,
        int length,
        TtfFont font,
        sbyte bidiLevel,
        int paragraphStart,
        int paragraphEnd)
    {
        // The overwhelmingly common layout is one font run spanning the complete
        // string. Reuse the command text in that case so first-touch virtualized
        // rows do not allocate an identical substring before shaping.
        string runText = start == 0 && length == Text.Length
            ? Text
            : Text.Substring(start, length);
        int runEnd = start + length;
        ShapingBufferFlags flags = ShapingOptions.BufferFlags;
        if (start == paragraphStart) flags |= ShapingBufferFlags.BeginningOfText;
        if (runEnd == paragraphEnd) flags |= ShapingBufferFlags.EndOfText;
        ReadOnlyMemory<char> preContext = Text.AsMemory(paragraphStart, start - paragraphStart);
        ReadOnlyMemory<char> postContext = Text.AsMemory(runEnd, paragraphEnd - runEnd);
        TextShapingOptions contextualOptions = ShapingOptions.WithBufferFlags(flags);
        if (ShapingOptions.Direction is ShapingDirection.TopToBottom or ShapingDirection.BottomToTop)
        {
            IReadOnlyList<ShapedGlyph> vertical = OpenTypeTextShaper.Shape(
                runText,
                font,
                FontSize,
                contextualOptions,
                preContext,
                postContext);
            for (var index = 0; index < vertical.Count; index++)
            {
                ShapedGlyph glyph = vertical[index];
                candidates.Add(new ShapedCandidate(
                    font,
                    glyph.GlyphIndex,
                    start + glyph.Cluster,
                    glyph.CodePoint,
                    bidiLevel,
                    glyph.AdvanceX,
                    glyph.AdvanceY,
                    glyph.OffsetX,
                    glyph.OffsetY,
                    glyph.Flags));
            }
            return;
        }

        ShapingBuffer shaping = t_shapingBuffer ??= new ShapingBuffer(64);
        ShapingDirection runDirection = (bidiLevel & 1) == 0
            ? ShapingDirection.LeftToRight
            : ShapingDirection.RightToLeft;
        OpenTypeTextShaper.ShapeDesignUnits(
            runText,
            font,
            contextualOptions.WithDirection(runDirection),
            shaping,
            preContext,
            postContext);
        float scale = FontSize / font.UnitsPerEm;
        ReadOnlySpan<ShapingGlyph> shaped = shaping.Glyphs;
        var runCandidates = new List<ShapedCandidate>(shaped.Length);
        for (var index = 0; index < shaped.Length; index++)
        {
            ShapingGlyph glyph = shaped[index];
            runCandidates.Add(new ShapedCandidate(
                font,
                checked((ushort)glyph.GlyphId),
                start + glyph.Cluster,
                glyph.CodePoint,
                bidiLevel,
                glyph.AdvanceX * scale,
                glyph.AdvanceY * scale,
                glyph.OffsetX * scale,
                glyph.OffsetY * scale,
                glyph.Flags));
        }

        AppendInLogicalClusterOrder(candidates, runCandidates, (bidiLevel & 1) != 0);
    }

    private static void AppendInLogicalClusterOrder(
        List<ShapedCandidate> destination,
        List<ShapedCandidate> run,
        bool rightToLeft)
    {
        if (!rightToLeft || run.Count < 2)
        {
            destination.AddRange(run);
            return;
        }

        // RTL shaping returns cluster groups in visual order. Reverse the groups,
        // while retaining the shaper's glyph order inside each cluster, so line
        // breaking can operate over logical text before UAX #9 L2 is applied.
        int groupEnd = run.Count;
        while (groupEnd > 0)
        {
            int groupStart = groupEnd - 1;
            int cluster = run[groupStart].Cluster;
            while (groupStart > 0 && run[groupStart - 1].Cluster == cluster)
            {
                groupStart--;
            }

            for (int index = groupStart; index < groupEnd; index++)
            {
                destination.Add(run[index]);
            }
            groupEnd = groupStart;
        }
    }

    private static bool IsSafeBreakBefore(IReadOnlyList<ShapedCandidate> candidates, int index)
    {
        if (index <= 0 || index >= candidates.Count) return true;
        if (candidates[index - 1].Cluster == candidates[index].Cluster) return false;
        return (candidates[index].Flags & ShapingGlyphFlags.UnsafeToBreak) == 0;
    }

    private static int FindNextSafeBreak(IReadOnlyList<ShapedCandidate> candidates, int index)
    {
        index = Math.Clamp(index, 0, candidates.Count);
        while (index < candidates.Count && !IsSafeBreakBefore(candidates, index)) index++;
        return index;
    }

    private static List<ShapedCandidate> GetVisualLineCandidates(
        List<ShapedCandidate> logicalCandidates,
        int start,
        int end,
        sbyte paragraphLevel)
    {
        var groupStarts = new List<int>();
        var groupLevels = new List<sbyte>();
        for (int index = start; index < end;)
        {
            groupStarts.Add(index);
            groupLevels.Add(logicalCandidates[index].BidiLevel);
            int cluster = logicalCandidates[index].Cluster;
            index++;
            while (index < end && logicalCandidates[index].Cluster == cluster)
            {
                index++;
            }
        }
        groupStarts.Add(end);

        // UAX #9 L1 resets trailing whitespace on each wrapped line.
        for (int group = groupLevels.Count - 1; group >= 0; group--)
        {
            bool whitespace = true;
            for (int index = groupStarts[group]; index < groupStarts[group + 1]; index++)
            {
                whitespace &= logicalCandidates[index].IsWhitespace;
            }
            if (!whitespace)
            {
                break;
            }
            groupLevels[group] = paragraphLevel;
        }

        int[] visualOrder = BidiParagraph.GetVisualOrder(groupLevels.ToArray());
        var result = new List<ShapedCandidate>(end - start);
        for (int visualIndex = 0; visualIndex < visualOrder.Length; visualIndex++)
        {
            int group = visualOrder[visualIndex];
            for (int index = groupStarts[group]; index < groupStarts[group + 1]; index++)
            {
                result.Add(logicalCandidates[index]);
            }
        }
        return result;
    }

    public IReadOnlyList<TextCaretStop> GetVisualCaretStops()
    {
        List<ClusterBox> boxes = BuildClusterBoxes();
        if (boxes.Count == 0)
        {
            return [new TextCaretStop(0, false, Vector2.Zero, Math.Max(0f, FontSize), 0)];
        }

        var stops = new List<TextCaretStop>(boxes.Count * 2);
        for (int index = 0; index < boxes.Count; index++)
        {
            ClusterBox box = boxes[index];
            bool rtl = (box.Level & 1) != 0;
            // Cluster boxes are already emitted line-by-line in physical order.
            // Preserve that order instead of sorting by glyph bounds: fallback
            // fonts can have different ascenders on the same baseline.
            stops.Add(new TextCaretStop(
                rtl ? box.End : box.Start,
                rtl,
                new Vector2(box.Left, box.Top),
                box.Height,
                box.Level));
            stops.Add(new TextCaretStop(
                rtl ? box.Start : box.End,
                !rtl,
                new Vector2(box.Right, box.Top),
                box.Height,
                box.Level));
        }
        for (int index = stops.Count - 1; index > 0; index--)
        {
            TextCaretStop current = stops[index];
            TextCaretStop previous = stops[index - 1];
            if (current.TextPosition == previous.TextPosition &&
                current.IsTrailing == previous.IsTrailing &&
                Vector2.DistanceSquared(current.Position, previous.Position) < 0.0001f)
            {
                stops.RemoveAt(index);
            }
        }
        return stops;
    }

    public TextHitTestResult HitTestPoint(Vector2 point)
    {
        List<ClusterBox> boxes = BuildClusterBoxes();
        if (boxes.Count == 0)
        {
            return new TextHitTestResult(0, false, false, new TextBounds(0f, 0f, 0f, FontSize), 0);
        }

        float bestDistance = float.PositiveInfinity;
        ClusterBox best = boxes[0];
        bool inside = false;
        for (int index = 0; index < boxes.Count; index++)
        {
            ClusterBox box = boxes[index];
            float dx = point.X < box.Left ? box.Left - point.X : point.X > box.Right ? point.X - box.Right : 0f;
            float dy = point.Y < box.Top ? box.Top - point.Y : point.Y > box.Bottom ? point.Y - box.Bottom : 0f;
            float distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = box;
            inside = dx == 0f && dy == 0f;
        }

        bool visualRightHalf = point.X >= (best.Left + best.Right) * 0.5f;
        bool rtl = (best.Level & 1) != 0;
        bool trailing = rtl ? !visualRightHalf : visualRightHalf;
        int position = trailing ? best.End : best.Start;
        return new TextHitTestResult(
            position,
            trailing,
            inside,
            new TextBounds(best.Left, best.Top, best.Width, best.Height),
            best.Level);
    }

    public TextCaretStop GetCaretStop(int textPosition, bool trailingAffinity = false)
    {
        IReadOnlyList<TextCaretStop> stops = GetVisualCaretStops();
        TextCaretStop best = stops[0];
        int bestDistance = int.MaxValue;
        for (int index = 0; index < stops.Count; index++)
        {
            TextCaretStop candidate = stops[index];
            int distance = Math.Abs(candidate.TextPosition - textPosition);
            if (distance < bestDistance ||
                (distance == bestDistance && candidate.IsTrailing == trailingAffinity && best.IsTrailing != trailingAffinity))
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best;
    }

    public TextCaretStop MoveCaretVisually(
        int textPosition,
        bool trailingAffinity,
        int direction)
    {
        IReadOnlyList<TextCaretStop> stops = GetVisualCaretStops();
        if (stops.Count == 0) return default;
        int current = 0;
        float bestDistance = float.PositiveInfinity;
        for (int index = 0; index < stops.Count; index++)
        {
            TextCaretStop candidate = stops[index];
            float affinityPenalty = candidate.IsTrailing == trailingAffinity ? 0f : 0.25f;
            float logicalPenalty = Math.Abs(candidate.TextPosition - textPosition) * 1000f;
            float distance = logicalPenalty + affinityPenalty;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                current = index;
            }
        }
        return stops[Math.Clamp(current + Math.Sign(direction), 0, stops.Count - 1)];
    }

    public IReadOnlyList<TextBounds> GetSelectionRectangles(int textStart, int textLength)
    {
        int selectionStart = Math.Clamp(Math.Min(textStart, textStart + textLength), 0, Text.Length);
        int selectionEnd = Math.Clamp(Math.Max(textStart, textStart + textLength), 0, Text.Length);
        if (selectionEnd <= selectionStart) return Array.Empty<TextBounds>();

        List<ClusterBox> boxes = BuildClusterBoxes();
        var selected = new List<ClusterBox>();
        for (int index = 0; index < boxes.Count; index++)
        {
            ClusterBox box = boxes[index];
            if (box.End > selectionStart && box.Start < selectionEnd)
            {
                selected.Add(box);
            }
        }
        selected.Sort(static (left, right) =>
        {
            int line = left.Top.CompareTo(right.Top);
            return line != 0 ? line : left.Left.CompareTo(right.Left);
        });

        var result = new List<TextBounds>();
        for (int index = 0; index < selected.Count; index++)
        {
            ClusterBox box = selected[index];
            if (result.Count > 0)
            {
                TextBounds previous = result[^1];
                if (Math.Abs(previous.Y - box.Top) < 0.01f &&
                    Math.Abs(previous.Height - box.Height) < 0.01f &&
                    box.Left <= previous.Right + 0.5f)
                {
                    result[^1] = new TextBounds(previous.X, previous.Y, Math.Max(previous.Right, box.Right) - previous.X, previous.Height);
                    continue;
                }
            }
            result.Add(new TextBounds(box.Left, box.Top, box.Width, box.Height));
        }
        return result;
    }

    private List<ClusterBox> BuildClusterBoxes()
    {
        var result = new List<ClusterBox>();
        if (Glyphs.Count == 0) return result;

        var logicalClusters = Glyphs.Select(static glyph => glyph.Cluster).Distinct().Order().ToArray();
        var clusterEnds = new Dictionary<int, int>(logicalClusters.Length);
        for (int index = 0; index < logicalClusters.Length; index++)
        {
            int start = logicalClusters[index];
            int end = index + 1 < logicalClusters.Length ? logicalClusters[index + 1] : Text.Length;
            clusterEnds[start] = Math.Max(start + 1, end);
        }

        for (int glyphIndex = 0; glyphIndex < Glyphs.Count;)
        {
            TextRunGlyph first = Glyphs[glyphIndex];
            int cluster = first.Cluster;
            float baseline = first.Position.Y;
            float left = first.Position.X;
            float right = first.Position.X + Math.Max(0f, first.Glyph.Advance);
            float scale = FontSize / first.Font.UnitsPerEm;
            float top = baseline - first.Font.Ascender * scale;
            float bottom = top + Math.Max(first.Glyph.Height, FontSize);
            int endGlyph = glyphIndex + 1;
            while (endGlyph < Glyphs.Count &&
                   Glyphs[endGlyph].Cluster == cluster &&
                   Math.Abs(Glyphs[endGlyph].Position.Y - baseline) < 0.01f)
            {
                TextRunGlyph glyph = Glyphs[endGlyph++];
                left = Math.Min(left, glyph.Position.X);
                right = Math.Max(right, glyph.Position.X + Math.Max(0f, glyph.Glyph.Advance));
                float glyphScale = FontSize / glyph.Font.UnitsPerEm;
                float glyphTop = glyph.Position.Y - glyph.Font.Ascender * glyphScale;
                top = Math.Min(top, glyphTop);
                bottom = Math.Max(bottom, glyphTop + Math.Max(glyph.Glyph.Height, FontSize));
            }
            result.Add(new ClusterBox(cluster, clusterEnds[cluster], first.BidiLevel, left, top, Math.Max(0f, right - left), Math.Max(1f, bottom - top)));
            glyphIndex = endGlyph;
        }
        return result;
    }

    private readonly record struct ClusterBox(int Start, int End, sbyte Level, float Left, float Top, float Width, float Height)
    {
        public float Right => Left + Width;
        public float Bottom => Top + Height;
    }

    private void GenerateLayoutLegacy(GlyphAtlas? atlas)
    {
        HasTextures = true;
        Glyphs.Clear();
        if (string.IsNullOrEmpty(Text))
        {
            ContentSize = Vector2.Zero;
            MeasuredSize = Vector2.Zero;
            return;
        }

        int estimatedGlyphCapacity = EstimateGlyphCapacity(Text);
        Glyphs.EnsureCapacity(estimatedGlyphCapacity);

        // Layout metric scales
        float scale = FontSize / Font.UnitsPerEm;
        float lineSpacing = (Font.Ascender - Font.Descender + Font.LineGap) * scale;
        float fontAscent = Font.Ascender * scale;

        var lines = new List<LineRange>(EstimateLineCapacity(Text));
        int currentLineStart = 0;

        float cursorX = 0f;
        float cursorY = 0f;
        uint prevCodePoint = 0;

        // Keep track of words to enable word wrapping
        int lastWordStartIndex = -1;
        float lastWordStartCursorX = 0f;

        for (int i = 0; i < Text.Length; i++)
        {
            char c = Text[i];
            uint codePoint = c;

            // Handle surrogate pairs to decode to full 32-bit UTF-32 code point
            if (char.IsHighSurrogate(c) && i + 1 < Text.Length && char.IsLowSurrogate(Text[i + 1]))
            {
                codePoint = (uint)char.ConvertToUtf32(c, Text[i + 1]);
                i++; // skip low surrogate
            }

            if (codePoint == '\n')
            {
                // Explicit line break
                AddLineRange(lines, currentLineStart, Glyphs.Count);
                currentLineStart = Glyphs.Count;
                cursorX = 0f;
                cursorY += lineSpacing;
                prevCodePoint = 0;
                lastWordStartIndex = -1;
                continue;
            }

            ushort glyphIdx = Font.GetGlyphIndex(codePoint);
            TtfFont resolvedFont = Font;

            // If the character is not supported in the primary font, try system fallback fonts (e.g. CJK or Emojis)
            if (glyphIdx == 0 && codePoint != ' ' && codePoint != '\t' && codePoint != '\n')
            {
                if (TryResolveFallback(Font, codePoint, out TtfFont? fallbackFont, out ushort fallbackGlyphIndex) &&
                    fallbackFont is not null)
                {
                    glyphIdx = fallbackGlyphIndex;
                    resolvedFont = fallbackFont;
                }
            }

            float advance = resolvedFont.GetAdvanceWidth(glyphIdx, FontSize);
            GlyphInfo glyph = new GlyphInfo
            {
                X = 0,
                Y = 0,
                Width = (uint)advance,
                Height = (uint)lineSpacing,
                BearX = 0,
                BearY = 0,
                Advance = advance,
                TexCoordMin = Vector2.Zero,
                TexCoordMax = Vector2.Zero
            };
            
            // Add kerning offset
            if (prevCodePoint != 0)
            {
                cursorX += Font.GetKerning(prevCodePoint, codePoint, FontSize);
            }

            // Word boundary tracking
            if (codePoint == ' ' || codePoint == '\t')
            {
                lastWordStartIndex = -1;
            }
            else if (lastWordStartIndex == -1)
            {
                lastWordStartIndex = Glyphs.Count;
                lastWordStartCursorX = cursorX;
            }

            // Auto-wrapping logic on layout boundary
            if (cursorX + glyph.Advance > MaxWidth && cursorX > 0)
            {
                if (lastWordStartIndex > currentLineStart)
                {
                    // Wrap the last partial word to the next line
                    int wrapStartIndex = lastWordStartIndex;
                    int previousLineEnd = Glyphs.Count;
                    int wrapCount = previousLineEnd - wrapStartIndex;
                    AddLineRange(lines, currentLineStart, wrapStartIndex);
                    
                    cursorX = 0f;
                    cursorY += lineSpacing;
                    prevCodePoint = 0;

                    // Re-position the wrapped glyphs on the new line
                    for (int wrapIndex = wrapStartIndex; wrapIndex < previousLineEnd; wrapIndex++)
                    {
                        var wg = Glyphs[wrapIndex];
                        var remapped = wg;
                        float shift = wg.Position.X - lastWordStartCursorX;
                        remapped.Position = new Vector2(shift, cursorY + fontAscent + remapped.Glyph.BearY);
                        Glyphs.Add(remapped);
                        cursorX = shift + remapped.Glyph.Advance;
                        prevCodePoint = remapped.CodePoint;
                    }

                    Glyphs.RemoveRange(wrapStartIndex, wrapCount);
                    currentLineStart = wrapStartIndex;
                    
                    // Add the current character
                    if (prevCodePoint != 0)
                    {
                        cursorX += Font.GetKerning(prevCodePoint, codePoint, FontSize);
                    }
                    var glyphPos = new Vector2(cursorX + glyph.BearX, cursorY + fontAscent + glyph.BearY);
                    Glyphs.Add(new TextRunGlyph { Character = c, CodePoint = codePoint, GlyphIndex = glyphIdx, Position = glyphPos, Glyph = glyph, Font = resolvedFont });
                    cursorX += glyph.Advance;
                    prevCodePoint = codePoint;
                    lastWordStartIndex = currentLineStart;
                    lastWordStartCursorX = 0f;
                    continue;
                }
                else
                {
                    // Hard wrap (word is longer than MaxWidth)
                    AddLineRange(lines, currentLineStart, Glyphs.Count);
                    currentLineStart = Glyphs.Count;
                    cursorX = 0f;
                    cursorY += lineSpacing;
                    prevCodePoint = 0;
                    lastWordStartIndex = codePoint == ' ' || codePoint == '\t' ? -1 : currentLineStart;
                    lastWordStartCursorX = 0f;
                }
            }

            // Position calculation (Y is offset by the ascender height so baseline aligns perfectly)
            var pos = new Vector2(cursorX + glyph.BearX, cursorY + fontAscent + glyph.BearY);
            Glyphs.Add(new TextRunGlyph { Character = c, CodePoint = codePoint, GlyphIndex = glyphIdx, Position = pos, Glyph = glyph, Font = resolvedFont });
            cursorX += glyph.Advance;
            prevCodePoint = codePoint;
        }

        AddLineRange(lines, currentLineStart, Glyphs.Count);

        // Apply Horizontal Alignments and calculate layout size
        float maxLineWidth = 0f;
        float totalHeight = cursorY + lineSpacing;

        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            int lineEnd = line.Start + line.Count;

            float lineWidth = 0f;
            for (int glyphIndex = line.Start; glyphIndex < lineEnd; glyphIndex++)
            {
                var g = Glyphs[glyphIndex];
                lineWidth = Math.Max(lineWidth, g.Position.X - g.Glyph.BearX + g.Glyph.Advance);
            }
            maxLineWidth = Math.Max(maxLineWidth, lineWidth);

            // Calculate shift for Center/Right alignment
            float shiftX = 0f;
            if (Alignment == TextAlignment.Center)
            {
                shiftX = (MaxWidth - lineWidth) / 2.0f;
            }
            else if (Alignment == TextAlignment.Right)
            {
                shiftX = MaxWidth - lineWidth;
            }

            if (shiftX > 0f && !float.IsInfinity(shiftX))
            {
                for (int glyphIndex = line.Start; glyphIndex < lineEnd; glyphIndex++)
                {
                    var remap = Glyphs[glyphIndex];
                    remap.Position.X += shiftX;
                    Glyphs[glyphIndex] = remap;
                }
            }
        }

        ContentSize = new Vector2(maxLineWidth, totalHeight);
        MeasuredSize = new Vector2(
            float.IsInfinity(MaxWidth) ? maxLineWidth : MaxWidth, 
            totalHeight
        );
    }
}
