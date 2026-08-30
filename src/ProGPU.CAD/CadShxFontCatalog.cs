namespace ProGPU.CAD;

/// <summary>
/// A thread-safe, browser-neutral catalog of immutable standard and Unicode
/// SHX fonts.
/// </summary>
/// <remarks>
/// Registration and mapping changes are intended for host initialization.
/// Resolution is expected O(1), performs no file IO, and returns an already
/// constructed glyph cache. Existing document snapshots remain independent of
/// later catalog additions because they retain immutable glyph identities.
/// </remarks>
public sealed class CadShxFontCatalog : ICadShxFontResolver, ICadShxShapeResolver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _fonts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CadShxGlyphCache, Entry> _entriesByCache =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<Entry> _registrationOrder = [];
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

    public bool ContainsRegisteredName(string name)
    {
        string key = NormalizeLookupName(name, nameof(name));
        lock (_gate)
        {
            return _fonts.ContainsKey(key);
        }
    }

    public bool TryGetMapping(
        string requestedFontName,
        out string replacementFontFilename)
    {
        string requested = NormalizeMappingSource(
            requestedFontName,
            nameof(requestedFontName));
        lock (_gate)
        {
            if (_mappings.TryGetValue(requested, out string? replacement))
            {
                replacementFontFilename = replacement;
                return true;
            }
        }
        replacementFontFilename = string.Empty;
        return false;
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
                _registrationOrder.Add(entry);
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
    public void SetMapping(string requestedFontName, string replacementFontFilename)
    {
        string requested = NormalizeMappingSource(
            requestedFontName,
            nameof(requestedFontName));
        string replacement = NormalizeShxFilename(
            replacementFontFilename,
            nameof(replacementFontFilename));
        lock (_gate)
        {
            _mappings[requested] = replacement;
            _resolverSnapshot = null;
        }
    }

    /// <summary>
    /// Applies the SHX-to-SHX subset of a parsed AutoCAD font mapping table.
    /// The operation validates every entry before changing catalog state.
    /// </summary>
    public void ApplyShxMappings(CadFontMappingTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        ReadOnlySpan<CadFontMapping> source = table.Mappings.Span;
        var mappings = new KeyValuePair<string, string>[source.Length];
        var requestedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < source.Length; i++)
        {
            string requested = NormalizeMappingSource(
                source[i].RequestedFontName,
                nameof(table));
            string replacement = NormalizeShxFilename(
                source[i].ReplacementFontFilename,
                nameof(table));
            if (!requestedNames.Add(requested))
            {
                throw new InvalidDataException(
                    $"Font mapping table contains duplicate SHX source '{requested}'.");
            }
            mappings[i] = new KeyValuePair<string, string>(requested, replacement);
        }

        lock (_gate)
        {
            foreach (KeyValuePair<string, string> mapping in mappings)
            {
                _mappings[mapping.Key] = mapping.Value;
            }
            _resolverSnapshot = null;
        }
    }

    public bool RemoveMapping(string requestedFontName)
    {
        string requested = NormalizeMappingSource(
            requestedFontName,
            nameof(requestedFontName));
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
        string mappingSource = NormalizeOptionalMappingSource(primary);
        lock (_gate)
        {
            return ResolveCore(
                primary,
                mappingSource,
                style,
                _fonts,
                _mappings,
                _alternate);
        }
    }

    public CadShxShapeResolution ResolveShape(in CadShxShapeRequest request)
    {
        string primary = NormalizeOptionalLookupName(request.PrimaryFontFilename);
        string mappingSource = NormalizeOptionalMappingSource(primary);
        string shapeName = request.ShapeName?.Trim() ?? string.Empty;
        lock (_gate)
        {
            return ResolveShapeCore(
                shapeName,
                request.ShapeNumber,
                primary,
                mappingSource,
                _fonts,
                _mappings,
                _registrationOrder);
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
                _alternate,
                _registrationOrder.ToArray());
        }
    }

    private static CadShxFontResolution ResolveCore(
        string primary,
        string mappingSource,
        string style,
        IReadOnlyDictionary<string, Entry> fonts,
        IReadOnlyDictionary<string, string> mappings,
        Entry? alternate)
    {
        if (mappingSource.Length != 0 &&
            mappings.TryGetValue(mappingSource, out string? mappedName) &&
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

    private static CadShxShapeResolution ResolveShapeCore(
        string shapeName,
        ushort shapeNumber,
        string primary,
        string mappingSource,
        IReadOnlyDictionary<string, Entry> fonts,
        IReadOnlyDictionary<string, string> mappings,
        IReadOnlyList<Entry> registrationOrder)
    {
        if (primary.Length != 0)
        {
            Entry? entry = null;
            bool substitution = false;
            if (mappingSource.Length != 0 &&
                mappings.TryGetValue(mappingSource, out string? mappedName) &&
                fonts.TryGetValue(mappedName, out Entry? mapped))
            {
                entry = mapped;
                substitution = true;
            }
            else
            {
                fonts.TryGetValue(primary, out entry);
            }

            if (entry is null ||
                !TryResolveShapeNumber(entry.Cache.Font, shapeName, shapeNumber, out ushort resolved))
            {
                return default;
            }
            return new CadShxShapeResolution(
                entry.Cache,
                resolved,
                entry.RegisteredName,
                substitution);
        }

        if (shapeName.Length == 0)
        {
            return default;
        }
        for (int i = 0; i < registrationOrder.Count; i++)
        {
            Entry entry = registrationOrder[i];
            if (!entry.Cache.Font.IsTextFont &&
                entry.Cache.Font.TryGetShape(shapeName, out CadShxShape? shape))
            {
                return new CadShxShapeResolution(
                    entry.Cache,
                    shape!.Number,
                    entry.RegisteredName,
                    false);
            }
        }
        return default;
    }

    private static bool TryResolveShapeNumber(
        CadShxFont font,
        string shapeName,
        ushort shapeNumber,
        out ushort resolved)
    {
        if (shapeNumber != 0 && font.TryGetShape(shapeNumber, out _))
        {
            resolved = shapeNumber;
            return true;
        }
        if (shapeName.Length != 0 && font.TryGetShape(shapeName, out CadShxShape? shape))
        {
            resolved = shape!.Number;
            return true;
        }
        resolved = 0;
        return false;
    }

    private static string NormalizeShxFilename(string value, string parameterName)
    {
        string key = NormalizeLookupName(value, parameterName);
        if (!key.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "An SHX catalog filename must use the .shx extension.",
                parameterName);
        }
        return key;
    }

    private static string NormalizeMappingSource(string value, string parameterName)
    {
        string key = NormalizeLookupName(value, parameterName);
        int extension = key.LastIndexOf('.');
        if (extension < 0)
        {
            return key;
        }
        if (!key.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "An SHX mapping source must omit its extension or use .shx.",
                parameterName);
        }
        return key[..^4];
    }

    private static string NormalizeOptionalMappingSource(string primary)
    {
        if (primary.Length == 0)
        {
            return string.Empty;
        }
        return primary.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
            ? primary[..^4]
            : primary;
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
        Entry? alternate,
        IReadOnlyList<Entry> registrationOrder) : ICadShxFontResolver, ICadShxShapeResolver
    {
        public CadShxFontResolution Resolve(in CadShxFontRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.BigFontFilename))
            {
                return default;
            }
            string primary = NormalizeOptionalLookupName(request.PrimaryFontFilename);
            return ResolveCore(
                primary,
                NormalizeOptionalMappingSource(primary),
                NormalizeOptionalLookupName(request.StyleName),
                fonts,
                mappings,
                alternate);
        }

        public CadShxShapeResolution ResolveShape(in CadShxShapeRequest request)
        {
            string primary = NormalizeOptionalLookupName(request.PrimaryFontFilename);
            return ResolveShapeCore(
                request.ShapeName?.Trim() ?? string.Empty,
                request.ShapeNumber,
                primary,
                NormalizeOptionalMappingSource(primary),
                fonts,
                mappings,
                registrationOrder);
        }
    }
}
