using ProGPU.Text;

namespace ProGPU.CAD;

public readonly record struct CadTextFontRequest(
    string StyleName,
    string PrimaryFontFilename,
    string BigFontFilename,
    bool IsBold,
    bool IsItalic);

public readonly record struct CadTextFontResolution(
    TtfFont? Font,
    bool IsSubstitution);

public interface ICadTextFontResolver
{
    CadTextFontResolution Resolve(in CadTextFontRequest request);
}

public readonly record struct CadShxFontRequest(
    string StyleName,
    string PrimaryFontFilename,
    string BigFontFilename);

public readonly record struct CadShxFontResolution(
    CadShxGlyphCache? GlyphCache,
    string ResolvedFontName,
    bool IsSubstitution);

public interface ICadShxFontResolver
{
    CadShxFontResolution Resolve(in CadShxFontRequest request);
}

public readonly record struct CadShxShapeRequest(
    string ShapeName,
    ushort ShapeNumber,
    string PrimaryFontFilename);

public readonly record struct CadShxShapeResolution(
    CadShxGlyphCache? GlyphCache,
    ushort ShapeNumber,
    string ResolvedFontName,
    bool IsSubstitution);

/// <summary>
/// Resolves one standalone SHAPE identity without file IO. A DWG request uses
/// its explicit font filename and number; a DXF request uses the shape name
/// and the document's registered SHX load order.
/// </summary>
public interface ICadShxShapeResolver
{
    CadShxShapeResolution ResolveShape(in CadShxShapeRequest request);
}

/// <summary>
/// Resolves CAD TrueType styles through the process font catalog with an optional
/// caller-owned fallback suitable for browser and sandboxed hosts.
/// </summary>
public sealed class CadFontManagerTextResolver : ICadTextFontResolver
{
    private readonly FontManager _fontManager;
    private readonly TtfFont? _fallback;

    public CadFontManagerTextResolver(
        TtfFont? fallback = null,
        FontManager? fontManager = null)
    {
        _fallback = fallback;
        _fontManager = fontManager ?? FontManager.Default;
    }

    public CadTextFontResolution Resolve(in CadTextFontRequest request)
    {
        var style = new FontStyleRequest(
            request.IsBold ? 700 : 400,
            5,
            request.IsItalic ? FontSlant.Italic : FontSlant.Upright);
        string family = GetFamilyCandidate(request.PrimaryFontFilename);
        TtfFont? matched = _fontManager.MatchFamily(family, style) ??
            _fontManager.MatchFamily(request.StyleName, style);
        if (matched is not null)
        {
            return new CadTextFontResolution(
                matched,
                IsStyleSubstitution(matched, request));
        }

        bool isSubstitution = _fallback is not null &&
            ((!MatchesFamily(_fallback.FamilyName, family) &&
              !MatchesFamily(_fallback.FamilyName, request.StyleName)) ||
             IsStyleSubstitution(_fallback, request));
        return new CadTextFontResolution(_fallback, isSubstitution);
    }

    private static string GetFamilyCandidate(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return string.Empty;
        }

        string name = Path.GetFileNameWithoutExtension(filename.Trim());
        return name.Replace('_', ' ').Replace('-', ' ');
    }

    private static bool MatchesFamily(string actual, string requested) =>
        !string.IsNullOrWhiteSpace(requested) &&
        actual.Equals(requested, StringComparison.OrdinalIgnoreCase);

    private static bool IsStyleSubstitution(
        TtfFont font,
        in CadTextFontRequest request)
    {
        int weight = font.WeightClass == 0 ? 400 : font.WeightClass;
        bool isBold = weight >= 600;
        return isBold != request.IsBold || font.IsItalic != request.IsItalic;
    }
}
