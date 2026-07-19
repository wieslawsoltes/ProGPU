using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
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
}

public class TextLayout
{
    private const long SharedFallbackFontFileSizeLimit = 16L * 1024L * 1024L;

    [ThreadStatic]
    private static ShapingBuffer? s_singleLineShapingBuffer;

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

    public string Text { get; }
    public TtfFont Font { get; }
    public float FontSize { get; }
    public float MaxWidth { get; }
    public TextAlignment Alignment { get; }
    public TextShapingOptions ShapingOptions { get; }

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
        Text = text ?? string.Empty;
        Font = font;
        FontSize = fontSize;
        MaxWidth = maxWidth;
        Alignment = alignment;
        ShapingOptions = shapingOptions ?? TextShapingOptions.Default;

        GenerateLayout(atlas);
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
        float AdvanceX,
        float AdvanceY,
        float OffsetX,
        float OffsetY)
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

        if (TryGenerateSingleFontSingleLineLayout())
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
            List<ShapedCandidate> candidates = ShapeRange(sourceStart, sourceEnd - sourceStart);

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
                    int candidateEnd = candidateStart;
                    for (; candidateEnd < candidates.Count; candidateEnd++)
                    {
                        ShapedCandidate candidate = candidates[candidateEnd];
                        if (candidate.IsWhitespace)
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
                    for (var candidateIndex = candidateStart; candidateIndex < candidateEnd; candidateIndex++)
                    {
                        ShapedCandidate candidate = candidates[candidateIndex];
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

    private bool TryGenerateSingleFontSingleLineLayout()
    {
        if (Alignment != TextAlignment.Left ||
            Text.IndexOf('\n') >= 0 ||
            (!float.IsInfinity(MaxWidth) && MaxWidth < 10_000f))
        {
            return false;
        }

        for (int index = 0; index < Text.Length;)
        {
            char character = Text[index++];
            uint codePoint = character;
            if (char.IsHighSurrogate(character) &&
                index < Text.Length &&
                char.IsLowSurrogate(Text[index]))
            {
                codePoint = (uint)char.ConvertToUtf32(character, Text[index++]);
            }

            if (Font.GetGlyphIndex(codePoint) == 0 &&
                codePoint is not (' ' or '\t') &&
                !OpenTypeTextShaper.IsDefaultIgnorableCodePoint(codePoint))
            {
                return false;
            }
        }

        ShapingBuffer shapingBuffer = s_singleLineShapingBuffer ??= new ShapingBuffer(initialCapacity: 64);
        OpenTypeTextShaper.ShapeDesignUnits(Text, Font, ShapingOptions, shapingBuffer);

        float scale = FontSize / Font.UnitsPerEm;
        float lineSpacing = (Font.Ascender - Font.Descender + Font.LineGap) * scale;
        float fontAscent = Font.Ascender * scale;
        float cursorX = 0f;
        ReadOnlySpan<ShapingGlyph> shapedGlyphs = shapingBuffer.Glyphs;
        Glyphs.EnsureCapacity(shapedGlyphs.Length);
        for (int index = 0; index < shapedGlyphs.Length; index++)
        {
            ShapingGlyph shaped = shapedGlyphs[index];
            float advance = shaped.AdvanceX * scale;
            int cluster = Math.Clamp(shaped.Cluster, 0, Math.Max(0, Text.Length - 1));
            Glyphs.Add(new TextRunGlyph
            {
                Character = Text[cluster],
                CodePoint = shaped.CodePoint,
                GlyphIndex = checked((ushort)shaped.GlyphId),
                Cluster = shaped.Cluster,
                Position = new Vector2(
                    cursorX + shaped.OffsetX * scale,
                    fontAscent + shaped.OffsetY * scale),
                Glyph = new GlyphInfo
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
                },
                Font = Font
            });
            cursorX += advance;
        }

        ContentSize = new Vector2(cursorX, lineSpacing);
        MeasuredSize = new Vector2(float.IsInfinity(MaxWidth) ? cursorX : MaxWidth, lineSpacing);
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
            List<ShapedCandidate> candidates = ShapeRange(sourceStart, sourceEnd - sourceStart);
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

    private List<ShapedCandidate> ShapeRange(int start, int length)
    {
        var candidates = new List<ShapedCandidate>(Math.Max(1, length));
        int end = start + length;
        int runStart = start;
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
                runStart = scalarStart;
            }
            else if (!ReferenceEquals(runFont, resolvedFont))
            {
                AppendShapedRun(candidates, runStart, scalarStart - runStart, runFont);
                runFont = resolvedFont;
                runStart = scalarStart;
            }
        }

        if (runFont is not null && runStart < end)
        {
            AppendShapedRun(candidates, runStart, end - runStart, runFont);
        }
        return candidates;
    }

    private void AppendShapedRun(List<ShapedCandidate> candidates, int start, int length, TtfFont font)
    {
        string runText = Text.Substring(start, length);
        IReadOnlyList<ShapedGlyph> shaped = OpenTypeTextShaper.Shape(runText, font, FontSize, ShapingOptions);
        for (var index = 0; index < shaped.Count; index++)
        {
            ShapedGlyph glyph = shaped[index];
            candidates.Add(new ShapedCandidate(
                font,
                glyph.GlyphIndex,
                start + glyph.Cluster,
                glyph.CodePoint,
                glyph.AdvanceX,
                glyph.AdvanceY,
                glyph.OffsetX,
                glyph.OffsetY));
        }
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
