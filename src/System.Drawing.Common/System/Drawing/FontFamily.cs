using ProGPU.Text;
using System.Drawing.Text;

namespace System.Drawing;

internal sealed class FontFamilySource
{
    private readonly TtfFont[]? _privateFaces;

    internal FontFamilySource(string name, TtfFont[]? privateFaces = null)
    {
        Name = name;
        _privateFaces = privateFaces;
    }

    internal string Name { get; }

    internal TtfFont? Resolve(FontStyle style)
    {
        FontStyleRequest request = FontFamily.CreateStyleRequest(style);
        if (_privateFaces is null)
        {
            return FontApi.Manager.MatchFamily(Name, request);
        }

        TtfFont? best = null;
        int bestDistance = int.MaxValue;
        for (int index = 0; index < _privateFaces.Length; index++)
        {
            TtfFont candidate = _privateFaces[index];
            int distance = GetStyleDistance(candidate, request);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best is null ? null : FontApi.Manager.MatchTypeface(best, request);
    }

    internal bool IsStyleAvailable(FontStyle style)
    {
        FontStyleRequest request = FontFamily.CreateStyleRequest(style);
        if (_privateFaces is null)
        {
            IReadOnlyList<FontFace> faces = FontApi.Manager.GetFontStyles(Name);
            for (int index = 0; index < faces.Count; index++)
            {
                if (HasRequestedStyle(faces[index].Style, request))
                {
                    return true;
                }
            }

            return false;
        }

        for (int index = 0; index < _privateFaces.Length; index++)
        {
            if (HasRequestedStyle(FontStyleRequest.FromFont(_privateFaces[index]), request))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasRequestedStyle(FontStyleRequest candidate, FontStyleRequest request) =>
        (candidate.Weight >= 600) == (request.Weight >= 600) &&
        (candidate.Slant != FontSlant.Upright) == (request.Slant != FontSlant.Upright);

    private static int GetStyleDistance(TtfFont candidate, FontStyleRequest request)
    {
        FontStyleRequest actual = FontStyleRequest.FromFont(candidate);
        int slant = (actual.Slant == FontSlant.Upright) == (request.Slant == FontSlant.Upright) ? 0 : 10000;
        return slant + Math.Abs(actual.Weight - request.Weight) * 10 + Math.Abs(actual.Width - request.Width);
    }
}

/// <summary>
/// Describes a portable family of related ProGPU typefaces.
/// </summary>
public sealed class FontFamily : MarshalByRefObject, IDisposable
{
    private static readonly string[] s_sansSerifPreferences =
    [
        "Segoe UI", "Microsoft Sans Serif", "Arial", "Helvetica", "Roboto", "Noto Sans", "DejaVu Sans"
    ];

    private static readonly string[] s_serifPreferences =
    [
        "Times New Roman", "Georgia", "Noto Serif", "DejaVu Serif", "Liberation Serif"
    ];

    private static readonly string[] s_monospacePreferences =
    [
        "Consolas", "Courier New", "SFMono-Regular", "Noto Sans Mono", "DejaVu Sans Mono", "Liberation Mono"
    ];

    private static readonly object s_genericLock = new();
    private static FontFamilySource? s_sansSerif;
    private static FontFamilySource? s_serif;
    private static FontFamilySource? s_monospace;

    private readonly FontFamilySource _source;
    private bool _disposed;

    public FontFamily(string name)
        : this(ResolveInstalled(name, createDefaultOnFail: false))
    {
    }

    public FontFamily(string name, FontCollection? fontCollection)
        : this(Resolve(name, fontCollection))
    {
    }

    public FontFamily(GenericFontFamilies genericFamily)
        : this(ResolveGeneric(genericFamily))
    {
    }

    internal FontFamily(FontFamilySource source)
    {
        _source = source;
    }

    internal FontFamily(TtfFont typeface)
        : this(new FontFamilySource(GetTypefaceFamilyName(typeface), [typeface]))
    {
    }

    internal static FontFamily CreateDefault(string? requestedName = null) =>
        new(ResolveInstalled(requestedName, createDefaultOnFail: true));

    public string Name
    {
        get
        {
            ThrowIfDisposed();
            return _source.Name;
        }
    }

    public static FontFamily[] Families => CreateInstalledFamilies();

    public static FontFamily GenericSansSerif => new(ResolveGeneric(GenericFontFamilies.SansSerif));

    public static FontFamily GenericSerif => new(ResolveGeneric(GenericFontFamilies.Serif));

    public static FontFamily GenericMonospace => new(ResolveGeneric(GenericFontFamilies.Monospace));

    [Obsolete("Use Families instead.")]
    public static FontFamily[] GetFamilies(Graphics graphics)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        return CreateInstalledFamilies();
    }

    public string GetName(int language) => Name;

    public bool IsStyleAvailable(FontStyle style)
    {
        ThrowIfDisposed();
        return _source.IsStyleAvailable(style);
    }

    public int GetEmHeight(FontStyle style) => GetMetricsFace(style).UnitsPerEm;

    public int GetCellAscent(FontStyle style) => GetMetricsFace(style).Ascender;

    public int GetCellDescent(FontStyle style) => -GetMetricsFace(style).Descender;

    public int GetLineSpacing(FontStyle style)
    {
        TtfFont face = GetMetricsFace(style);
        return face.Ascender - face.Descender + face.LineGap;
    }

    internal TtfFont ResolveTypeface(FontStyle style)
    {
        ThrowIfDisposed();
        return _source.Resolve(style) ?? throw new ArgumentException($"Font family '{_source.Name}' has no loadable faces.");
    }

    internal FontFamily Snapshot()
    {
        ThrowIfDisposed();
        return new FontFamily(_source);
    }

    internal static FontStyleRequest CreateStyleRequest(FontStyle style) =>
        new(
            (style & FontStyle.Bold) != 0 ? 700 : 400,
            5,
            (style & FontStyle.Italic) != 0 ? FontSlant.Italic : FontSlant.Upright);

    public override bool Equals(object? obj) =>
        ReferenceEquals(this, obj) ||
        obj is FontFamily family &&
        !_disposed &&
        !family._disposed &&
        string.Equals(_source.Name, family._source.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode()
    {
        ThrowIfDisposed();
        return _source.Name.GetHashCode();
    }

    public override string ToString() => _disposed ? "[FontFamily: disposed]" : $"[FontFamily: Name={_source.Name}]";

    public void Dispose() => _disposed = true;

    private TtfFont GetMetricsFace(FontStyle style)
    {
        ThrowIfDisposed();
        return _source.Resolve(style) ?? throw new ArgumentException($"Font family '{_source.Name}' has no loadable faces.");
    }

    private static FontFamilySource Resolve(string name, FontCollection? collection)
    {
        ArgumentNullException.ThrowIfNull(name);
        return collection is null
            ? ResolveInstalled(name, createDefaultOnFail: false)
            : collection.ResolveFamily(name);
    }

    private static FontFamilySource ResolveInstalled(string? name, bool createDefaultOnFail)
    {
        string lookupName = (name ?? string.Empty).TrimStart('@');
        if (!string.IsNullOrWhiteSpace(lookupName))
        {
            IReadOnlyList<string> names = FontApi.Manager.FontFamilies;
            for (int index = 0; index < names.Count; index++)
            {
                if (names[index].Equals(lookupName, StringComparison.OrdinalIgnoreCase))
                {
                    return new FontFamilySource(names[index]);
                }
            }
        }

        if (createDefaultOnFail)
        {
            return ResolveGeneric(GenericFontFamilies.SansSerif);
        }

        ArgumentNullException.ThrowIfNull(name);
        throw new ArgumentException($"Font family '{name}' was not found.", nameof(name));
    }

    private static FontFamilySource ResolveGeneric(GenericFontFamilies family)
    {
        FontFamilySource? cached = family switch
        {
            GenericFontFamilies.Serif => Volatile.Read(ref s_serif),
            GenericFontFamilies.SansSerif => Volatile.Read(ref s_sansSerif),
            _ => Volatile.Read(ref s_monospace)
        };
        if (cached is not null)
        {
            return cached;
        }

        lock (s_genericLock)
        {
            cached = family switch
            {
                GenericFontFamilies.Serif => s_serif,
                GenericFontFamilies.SansSerif => s_sansSerif,
                _ => s_monospace
            };
            if (cached is not null)
            {
                return cached;
            }

            cached = ResolveGenericCore(family);
            if (family == GenericFontFamilies.Serif)
            {
                Volatile.Write(ref s_serif, cached);
            }
            else if (family == GenericFontFamilies.SansSerif)
            {
                Volatile.Write(ref s_sansSerif, cached);
            }
            else
            {
                Volatile.Write(ref s_monospace, cached);
            }

            return cached;
        }
    }

    private static FontFamilySource ResolveGenericCore(GenericFontFamilies family)
    {
        string[] preferences = family switch
        {
            GenericFontFamilies.Serif => s_serifPreferences,
            GenericFontFamilies.SansSerif => s_sansSerifPreferences,
            _ => s_monospacePreferences
        };

        IReadOnlyList<string> names = FontApi.Manager.FontFamilies;
        for (int preferred = 0; preferred < preferences.Length; preferred++)
        {
            for (int index = 0; index < names.Count; index++)
            {
                if (names[index].Equals(preferences[preferred], StringComparison.OrdinalIgnoreCase))
                {
                    return new FontFamilySource(names[index]);
                }
            }
        }

        if (names.Count != 0)
        {
            return new FontFamilySource(names[0]);
        }

        TtfFont? fallback = FontApi.PlatformFallbackFont;
        if (fallback is not null)
        {
            return new FontFamilySource(GetTypefaceFamilyName(fallback), [fallback]);
        }

        throw new ArgumentException("No installed or platform font is available.");
    }

    private static FontFamily[] CreateInstalledFamilies()
    {
        IReadOnlyList<string> names = FontApi.Manager.FontFamilies;
        var result = new FontFamily[names.Count];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = new FontFamily(new FontFamilySource(names[index]));
        }

        return result;
    }

    private static string GetTypefaceFamilyName(TtfFont typeface) =>
        !string.IsNullOrWhiteSpace(typeface.FamilyName)
            ? typeface.FamilyName
            : !string.IsNullOrWhiteSpace(typeface.FullName)
                ? typeface.FullName
                : "ProGPU Font";

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ArgumentException("Parameter is not valid.");
        }
    }
}
