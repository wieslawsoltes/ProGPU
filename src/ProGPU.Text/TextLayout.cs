using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

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
}

public class TextLayout
{
    private const long SharedFallbackFontFileSizeLimit = 16L * 1024L * 1024L;

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

    private static bool TryResolveFallback(uint codePoint, out TtfFont? font, out ushort glyphIndex)
    {
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

    public List<TextRunGlyph> Glyphs { get; } = new();
    public Vector2 ContentSize { get; private set; }
    public Vector2 MeasuredSize { get; private set; }
    public bool HasTextures { get; private set; }

    public TextLayout(string text, TtfFont font, float fontSize, float maxWidth = float.PositiveInfinity, TextAlignment alignment = TextAlignment.Left, GlyphAtlas? atlas = null)
    {
        Text = text ?? string.Empty;
        Font = font;
        FontSize = fontSize;
        MaxWidth = maxWidth;
        Alignment = alignment;

        GenerateLayout(atlas);
    }

    public void GenerateLayout(GlyphAtlas? atlas)
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
                if (TryResolveFallback(codePoint, out TtfFont? fallbackFont, out ushort fallbackGlyphIndex) &&
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
