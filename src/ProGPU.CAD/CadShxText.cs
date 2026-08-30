using System.Numerics;
using ProGPU.Vector;

namespace ProGPU.CAD;

[Flags]
public enum CadShxTextDecoration : byte
{
    None = 0,
    Overline = 1 << 0,
    Underline = 1 << 1,
    StrikeThrough = 1 << 2,
}

public sealed class CadShxGlyph
{
    public ushort ShapeNumber { get; }
    public CadShxOrientation Orientation { get; }
    public Vector2 Advance { get; }
    public Vector2 BoundsMin { get; }
    public Vector2 BoundsMax { get; }
    public bool HasGeometry { get; }
    public int SegmentCount { get; }

    internal PathGeometry Path { get; }

    internal CadShxGlyph(
        ushort shapeNumber,
        CadShxOrientation orientation,
        CadShxGeometry geometry)
    {
        ShapeNumber = shapeNumber;
        Orientation = orientation;
        Advance = geometry.EndPoint;
        Path = geometry.Path;
        SegmentCount = geometry.SegmentCount;
        HasGeometry = Path.TryGetBounds(out Vector2 minimum, out Vector2 maximum);
        BoundsMin = HasGeometry ? minimum : Vector2.Zero;
        BoundsMax = HasGeometry ? maximum : Vector2.Zero;
    }
}

/// <summary>
/// Owns device-independent interpreted glyphs for one immutable standard or
/// Unicode SHX font or shape file.
/// </summary>
public sealed class CadShxGlyphCache
{
    private readonly object _gate = new();
    private readonly Dictionary<(ushort Shape, CadShxOrientation Orientation), CadShxGlyph> _glyphs = new();
    private readonly CadShxInterpretOptions _interpretOptions;

    public CadShxFont Font { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _glyphs.Count;
            }
        }
    }

    public CadShxGlyphCache(
        CadShxFont font,
        CadShxInterpretOptions? interpretOptions = null)
    {
        ArgumentNullException.ThrowIfNull(font);
        Font = font;
        _interpretOptions = interpretOptions ?? new CadShxInterpretOptions();
    }

    public CadShxGlyph GetGlyph(
        ushort shapeNumber,
        CadShxOrientation orientation = CadShxOrientation.Horizontal)
    {
        lock (_gate)
        {
            var key = (shapeNumber, orientation);
            if (_glyphs.TryGetValue(key, out CadShxGlyph? glyph))
            {
                return glyph;
            }

            CadShxGeometry geometry = CadShxInterpreter.Interpret(
                Font,
                shapeNumber,
                orientation,
                _interpretOptions);
            glyph = new CadShxGlyph(shapeNumber, orientation, geometry);
            _glyphs.Add(key, glyph);
            return glyph;
        }
    }
}

public sealed class CadShxTextLayoutOptions
{
    public const int DefaultMaxCodeUnits = 65_536;
    public const int DefaultMaxGlyphs = 65_536;
    public const double DefaultMaxCoordinateMagnitude = 1_000_000_000.0;

    public int MaxCodeUnits { get; init; } = DefaultMaxCodeUnits;
    public int MaxGlyphs { get; init; } = DefaultMaxGlyphs;
    public double MaxCoordinateMagnitude { get; init; } = DefaultMaxCoordinateMagnitude;
}

public readonly record struct CadShxGlyphPlacement(
    CadShxGlyph Glyph,
    Vector2 Origin,
    CadShxTextDecoration Decorations,
    bool IsBreakOpportunity = false);

/// <summary>
/// A bounded device-independent standard or Unicode SHX character layout.
/// </summary>
/// <remarks>
/// Layout is O(C + G) time and O(G) placement storage for C UTF-16/control-code
/// units and G resolved characters. Cached glyph lookup is expected O(1); a
/// first lookup additionally pays the bounded interpreter cost for that shape.
/// </remarks>
public sealed class CadShxTextLayout
{
    private readonly CadShxGlyphPlacement[] _glyphs;

    public CadShxOrientation Orientation { get; }
    public Vector2 Advance { get; }
    public Vector2 BoundsMin { get; }
    public Vector2 BoundsMax { get; }
    public ReadOnlyMemory<CadShxGlyphPlacement> Glyphs => _glyphs;

    public CadShxTextLayout(
        string source,
        CadShxGlyphCache cache,
        CadShxOrientation orientation = CadShxOrientation.Horizontal,
        CadShxTextLayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cache);
        if (!cache.Font.IsTextFont)
        {
            throw new ArgumentException(
                "SHX text layout requires a text-font header shape.",
                nameof(cache));
        }
        if (cache.Font.IsUnicodeFont &&
            cache.Font.UnicodeEncoding != CadShxUnicodeEncoding.Unicode)
        {
            throw new NotSupportedException(
                $"Unicode SHX encoding {cache.Font.UnicodeEncoding} requires a distinct character decoder.");
        }
        options ??= new CadShxTextLayoutOptions();
        ValidateOptions(options);
        if (source.Length == 0 || source.IndexOfAny(['\r', '\n']) >= 0)
        {
            throw new ArgumentException(
                "SHX text layout requires one non-empty logical line.",
                nameof(source));
        }
        if (source.Length > options.MaxCodeUnits)
        {
            throw new InvalidDataException(
                $"SHX text exceeds the configured limit of {options.MaxCodeUnits} UTF-16 code units.");
        }

        Orientation = orientation;
        var placements = new List<CadShxGlyphPlacement>(Math.Min(source.Length, options.MaxGlyphs));
        CadShxTextDecoration decorations = CadShxTextDecoration.None;
        double penX = 0.0;
        double penY = 0.0;
        double minimumX = 0.0;
        double minimumY = 0.0;
        double maximumX = 0.0;
        double maximumY = 0.0;
        bool hasBounds = false;

        for (int i = 0; i < source.Length; i++)
        {
            char value = source[i];
            if (value == '\\' && i + 2 < source.Length &&
                (source[i + 1] is 'U' or 'u') && source[i + 2] == '+')
            {
                if (i + 6 >= source.Length)
                {
                    throw new NotSupportedException("SHX text contains a truncated DXF Unicode escape.");
                }

                int scalar = 0;
                for (int digit = 0; digit < 4; digit++)
                {
                    int hex = HexValue(source[i + 3 + digit]);
                    if (hex < 0)
                    {
                        throw new NotSupportedException("SHX text contains an invalid DXF Unicode escape.");
                    }
                    scalar = (scalar << 4) | hex;
                }
                AddShape(
                    MapScalar(cache.Font, scalar),
                    isBreakOpportunity: scalar == 0x20);
                i += 6;
                continue;
            }

            if (value == '%' && i + 1 < source.Length && source[i + 1] == '%')
            {
                if (i + 2 >= source.Length)
                {
                    throw new NotSupportedException("SHX text contains a truncated AutoCAD control code.");
                }

                char code = char.ToLowerInvariant(source[i + 2]);
                if (code is 'o' or 'u' or 'k')
                {
                    decorations ^= code switch
                    {
                        'o' => CadShxTextDecoration.Overline,
                        'u' => CadShxTextDecoration.Underline,
                        _ => CadShxTextDecoration.StrikeThrough,
                    };
                    i += 2;
                    continue;
                }
                if (code is >= '0' and <= '9')
                {
                    if (i + 4 >= source.Length ||
                        source[i + 3] is < '0' or > '9' ||
                        source[i + 4] is < '0' or > '9')
                    {
                        throw new NotSupportedException(
                            "SHX numeric control codes require exactly three decimal digits.");
                    }
                    ushort shapeNumber = checked((ushort)(
                        ((source[i + 2] - '0') * 100) +
                        ((source[i + 3] - '0') * 10) +
                        (source[i + 4] - '0')));
                    AddShape(shapeNumber, isBreakOpportunity: shapeNumber == 32);
                    i += 4;
                    continue;
                }

                AddShape(code switch
                {
                    'd' => MapScalar(cache.Font, 0x00B0),
                    'p' => MapScalar(cache.Font, 0x00B1),
                    'c' => MapScalar(cache.Font, 0x2205),
                    '%' => (ushort)'%',
                    _ => throw new NotSupportedException(
                        $"SHX text contains unsupported AutoCAD control code '%%{source[i + 2]}'."),
                });
                i += 2;
                continue;
            }

            if (char.IsSurrogate(value))
            {
                throw new NotSupportedException(
                    "SHX shape identities are 16-bit and do not accept UTF-16 surrogate code units.");
            }
            AddShape(
                MapScalar(cache.Font, value),
                isBreakOpportunity: value == ' ');
        }

        if (placements.Count == 0)
        {
            throw new InvalidDataException(
                "SHX text control codes produced no character placements.");
        }
        CheckCoordinate(penX, "advance X");
        CheckCoordinate(penY, "advance Y");
        _glyphs = placements.ToArray();
        Advance = new Vector2((float)penX, (float)penY);
        BoundsMin = hasBounds
            ? new Vector2((float)minimumX, (float)minimumY)
            : Vector2.Zero;
        BoundsMax = hasBounds
            ? new Vector2((float)maximumX, (float)maximumY)
            : Vector2.Zero;

        void AddShape(ushort shapeNumber, bool isBreakOpportunity = false)
        {
            if (placements.Count == options.MaxGlyphs)
            {
                throw new InvalidDataException(
                    $"SHX text exceeds the configured limit of {options.MaxGlyphs} glyphs.");
            }

            CadShxGlyph glyph;
            try
            {
                glyph = cache.GetGlyph(shapeNumber, orientation);
            }
            catch (KeyNotFoundException exception)
            {
                throw new InvalidDataException(
                    $"SHX font '{cache.Font.Name}' has no shape {shapeNumber} required by the text.",
                    exception);
            }

            CheckCoordinate(penX, "glyph origin X");
            CheckCoordinate(penY, "glyph origin Y");
            var origin = new Vector2((float)penX, (float)penY);
            placements.Add(new CadShxGlyphPlacement(
                glyph,
                origin,
                decorations,
                isBreakOpportunity));
            if (glyph.HasGeometry)
            {
                double glyphMinimumX = penX + glyph.BoundsMin.X;
                double glyphMinimumY = penY + glyph.BoundsMin.Y;
                double glyphMaximumX = penX + glyph.BoundsMax.X;
                double glyphMaximumY = penY + glyph.BoundsMax.Y;
                CheckCoordinate(glyphMinimumX, "glyph minimum X");
                CheckCoordinate(glyphMinimumY, "glyph minimum Y");
                CheckCoordinate(glyphMaximumX, "glyph maximum X");
                CheckCoordinate(glyphMaximumY, "glyph maximum Y");
                minimumX = hasBounds ? Math.Min(minimumX, glyphMinimumX) : glyphMinimumX;
                minimumY = hasBounds ? Math.Min(minimumY, glyphMinimumY) : glyphMinimumY;
                maximumX = hasBounds ? Math.Max(maximumX, glyphMaximumX) : glyphMaximumX;
                maximumY = hasBounds ? Math.Max(maximumY, glyphMaximumY) : glyphMaximumY;
                hasBounds = true;
            }

            penX += glyph.Advance.X;
            penY += glyph.Advance.Y;
        }

        void CheckCoordinate(double coordinate, string field)
        {
            if (!double.IsFinite(coordinate) ||
                Math.Abs(coordinate) > options.MaxCoordinateMagnitude ||
                !float.IsFinite((float)coordinate))
            {
                throw new InvalidDataException(
                    $"SHX text {field} exceeds the configured coordinate limit.");
            }
        }
    }

    private static ushort MapScalar(CadShxFont font, int scalar)
    {
        if (font.IsUnicodeFont)
        {
            if (scalar is < 0 or > ushort.MaxValue ||
                scalar is >= 0xD800 and <= 0xDFFF)
            {
                throw new NotSupportedException(
                    $"U+{scalar:X} is outside the 16-bit Unicode SHX shape domain.");
            }
            return (ushort)scalar;
        }

        return MapStandardScalar(scalar);
    }

    private static ushort MapStandardScalar(int scalar) => scalar switch
    {
        0x00A0 => 32,
        0x00B0 => 256,
        0x00B1 => 257,
        0x2205 => 258,
        >= 0 and <= byte.MaxValue => (ushort)scalar,
        _ => throw new NotSupportedException(
            $"U+{scalar:X4} requires a Unicode or Big Font SHX contract."),
    };

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'F' => value - 'A' + 10,
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => -1,
    };

    private static void ValidateOptions(CadShxTextLayoutOptions options)
    {
        if (options.MaxCodeUnits <= 0 || options.MaxGlyphs <= 0 ||
            !double.IsFinite(options.MaxCoordinateMagnitude) ||
            options.MaxCoordinateMagnitude <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "SHX text layout limits must be finite positive bounded values.");
        }
    }
}
