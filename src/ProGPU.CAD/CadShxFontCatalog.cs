namespace ProGPU.CAD;

/// <summary>
/// A thread-safe, browser-neutral catalog of immutable standard SHX fonts.
/// </summary>
/// <remarks>
/// Registration and mapping changes are intended for host initialization.
/// Resolution is expected O(1), performs no file IO, and returns an already
/// constructed glyph cache. Existing document snapshots remain independent of
/// later catalog additions because they retain immutable glyph identities.
/// </remarks>
public sealed class CadShxFontCatalog : ICadShxFontResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _fonts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CadShxGlyphCache, Entry> _entriesByCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> _mappings =
        new(StringComparer.OrdinalIgnoreCase);
    private Entry? _alternate;
    private ResolverSnapshot? _resolverSnapshot;

    public int RegisteredNameCount
    {
        get
        {
            lock (_gate)
            {
                return _fonts.Count;
            }
        }
    }

    public int RegisteredFontCount
    {
        get
        {
            lock (_gate)
            {
                return _entriesByCache.Count;
            }
        }
    }

    public int MappingCount
    {
        get
        {
            lock (_gate)
            {
                return _mappings.Count;
            }
        }
    }

    public string AlternateFontName
    {
        get
        {
            lock (_gate)
            {
                return _alternate?.RegisteredName ?? string.Empty;
            }
        }
    }

    public CadShxGlyphCache ParseAndRegister(
        string fontFilename,
        ReadOnlySpan<byte> source,
        IEnumerable<string>? aliases = null,
        CadShxParseOptions? parseOptions = null,
        CadShxInterpretOptions? interpretOptions = null)
    {
        CadShxFont font = CadShxFont.Parse(source, parseOptions);
        var cache = new CadShxGlyphCache(font, interpretOptions);
        Register(fontFilename, cache, aliases);
        return cache;
    }

    public void Register(
        string fontFilename,
        CadShxGlyphCache cache,
        params string[] aliases) =>
        Register(fontFilename, cache, (IEnumerable<string>)aliases);

    public void Register(
        string fontFilename,
        CadShxGlyphCache cache,
        IEnumerable<string>? aliases)
    {
        ArgumentNullException.ThrowIfNull(cache);
        string registeredName = NormalizeShxFilename(fontFilename, nameof(fontFilename));
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            registeredName,
        };
        if (aliases is not null)
        {
            foreach (string alias in aliases)
            {
                keys.Add(NormalizeLookupName(alias, nameof(aliases)));
            }
        }

        lock (_gate)
        {
            foreach (string key in keys)
            {
                if (_fonts.TryGetValue(key, out Entry? existing) &&
                    !ReferenceEquals(existing.Cache, cache))
                {
                    throw new InvalidOperationException(
                        $"SHX catalog name '{key}' is already registered for font " +
                        $"'{existing.RegisteredName}'.");
                }
            }

            if (!_entriesByCache.TryGetValue(cache, out Entry? entry))
            {
                entry = new Entry(registeredName, cache);
                _entriesByCache.Add(cache, entry);
            }
            foreach (string key in keys)
            {
                _fonts[key] = entry;
            }
            _resolverSnapshot = null;
        }
    }

    /// <summary>
    /// Maps one requested SHX filename to another registered SHX filename.
    /// A missing mapped target falls back to the original requested filename.
    /// </summary>
    public void SetMapping(string requestedFontFilename, string replacementFontFilename)
    {
        string requested = NormalizeShxFilename(
            requestedFontFilename,
            nameof(requestedFontFilename));
        string replacement = NormalizeShxFilename(
            replacementFontFilename,
            nameof(replacementFontFilename));
        lock (_gate)
        {
            _mappings[requested] = replacement;
            _resolverSnapshot = null;
        }
    }

    public bool RemoveMapping(string requestedFontFilename)
    {
        string requested = NormalizeShxFilename(
            requestedFontFilename,
            nameof(requestedFontFilename));
        lock (_gate)
        {
            bool removed = _mappings.Remove(requested);
            if (removed)
            {
                _resolverSnapshot = null;
            }
            return removed;
        }
    }

    public void SetAlternate(string registeredName)
    {
        string key = NormalizeLookupName(registeredName, nameof(registeredName));
        lock (_gate)
        {
            if (!_fonts.TryGetValue(key, out Entry? entry))
            {
                throw new KeyNotFoundException(
                    $"SHX alternate font '{registeredName}' is not registered.");
            }
            _alternate = entry;
            _resolverSnapshot = null;
        }
    }

    public void ClearAlternate()
    {
        lock (_gate)
        {
            _alternate = null;
            _resolverSnapshot = null;
        }
    }

    public CadShxFontResolution Resolve(in CadShxFontRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.BigFontFilename))
        {
            return default;
        }

        string primary = NormalizeOptionalLookupName(request.PrimaryFontFilename);
        string style = NormalizeOptionalLookupName(request.StyleName);
        lock (_gate)
        {
            return ResolveCore(primary, style, _fonts, _mappings, _alternate);
        }
    }

    /// <summary>
    /// Captures one immutable resolver generation. Repeated calls reuse the
    /// same snapshot until registration, mapping, or alternate state changes.
    /// </summary>
    public ICadShxFontResolver CreateResolverSnapshot()
    {
        lock (_gate)
        {
            return _resolverSnapshot ??= new ResolverSnapshot(
                new Dictionary<string, Entry>(_fonts, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(_mappings, StringComparer.OrdinalIgnoreCase),
                _alternate);
        }
    }

    private static CadShxFontResolution ResolveCore(
        string primary,
        string style,
        IReadOnlyDictionary<string, Entry> fonts,
        IReadOnlyDictionary<string, string> mappings,
        Entry? alternate)
    {
        if (primary.Length != 0 &&
            mappings.TryGetValue(primary, out string? mappedName) &&
            fonts.TryGetValue(mappedName, out Entry? mapped))
        {
            return Resolution(mapped, isSubstitution: true);
        }
        if (primary.Length != 0 && fonts.TryGetValue(primary, out Entry? exact))
        {
            return Resolution(exact, isSubstitution: false);
        }
        if (style.Length != 0 && fonts.TryGetValue(style, out Entry? styleMatch))
        {
            return Resolution(styleMatch, isSubstitution: primary.Length != 0);
        }
        return alternate is null
            ? default
            : Resolution(alternate, isSubstitution: true);
    }

    private static CadShxFontResolution Resolution(Entry entry, bool isSubstitution) =>
        new(entry.Cache, entry.RegisteredName, isSubstitution);

    private static string NormalizeShxFilename(string value, string parameterName)
    {
        string key = NormalizeLookupName(value, parameterName);
        if (!key.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A standard SHX catalog filename must use the .shx extension.",
                parameterName);
        }
        return key;
    }

    private static string NormalizeLookupName(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        string key = ExtractFilename(value.Trim());
        if (key.Length == 0 || key is "." or "..")
        {
            throw new ArgumentException(
                "SHX catalog names must contain a non-empty filename or alias.",
                parameterName);
        }
        return key;
    }

    private static string NormalizeOptionalLookupName(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : ExtractFilename(value.Trim());

    private static string ExtractFilename(string value)
    {
        int separator = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        return separator < 0 ? value : value[(separator + 1)..];
    }

    private sealed record Entry(string RegisteredName, CadShxGlyphCache Cache);

    private sealed class ResolverSnapshot(
        IReadOnlyDictionary<string, Entry> fonts,
        IReadOnlyDictionary<string, string> mappings,
        Entry? alternate) : ICadShxFontResolver
    {
        public CadShxFontResolution Resolve(in CadShxFontRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.BigFontFilename))
            {
                return default;
            }
            return ResolveCore(
                NormalizeOptionalLookupName(request.PrimaryFontFilename),
                NormalizeOptionalLookupName(request.StyleName),
                fonts,
                mappings,
                alternate);
        }
    }
}
