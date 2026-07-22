using System.Numerics;
using System.Runtime.CompilerServices;

namespace ProGPU.Text;

/// <summary>Describes one axis from an OpenType <c>fvar</c> table.</summary>
public readonly record struct FontVariationAxis(
    string Tag,
    string Name,
    float Minimum,
    float Default,
    float Maximum,
    bool IsHidden);

/// <summary>Selects a user-space coordinate on an OpenType variation axis.</summary>
public readonly record struct FontVariationSetting(string Tag, float Value);

public partial class TtfFont : IEquatable<TtfFont>
{
    private const int VariationInstanceCacheLimit = 32;

    private OpenTypeVariationData? _variationData;
    private bool _variationDataInitialized;
    private OpenTypeVariationInstance? _variationInstance;
    private TtfFont? _variationRoot;
    private readonly object _variationCacheLock = new();
    private Dictionary<string, TtfFont>? _variationInstanceCache;
    private Queue<string>? _variationInstanceOrder;
    private int[]? _variationItemCountCache;

    /// <summary>Gets the continuous axes exposed by this font's OpenType <c>fvar</c> table.</summary>
    public IReadOnlyList<FontVariationAxis> VariationAxes =>
        GetVariationData()?.Axes ?? Array.Empty<FontVariationAxis>();

    /// <summary>Gets the user-space coordinates selected for this immutable font instance.</summary>
    public IReadOnlyList<FontVariationSetting> VariationSettings =>
        _variationInstance?.Settings ?? Array.Empty<FontVariationSetting>();

    public bool IsVariableFont => VariationAxes.Count != 0;

    private bool HasActiveVariations => _variationInstance is { IsDefault: false };
    internal bool HasActiveFontVariations => HasActiveVariations;

    /// <summary>
    /// Returns a cached immutable instance at the requested axis coordinates. Values are
    /// clamped to each axis range and normalized to the OpenType 2.14 lattice. Unknown tags
    /// are ignored so callers can pass a shared settings collection to different families.
    /// </summary>
    public TtfFont WithVariations(params FontVariationSetting[] settings) =>
        WithVariations((IReadOnlyList<FontVariationSetting>)settings);

    public TtfFont WithVariations(IReadOnlyList<FontVariationSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        TtfFont root = _variationRoot ?? this;
        OpenTypeVariationData? data = root.GetVariationData();
        if (data is null)
        {
            return root;
        }

        OpenTypeVariationInstance instance = data.CreateInstance(settings);
        if (instance.IsDefault)
        {
            return root;
        }

        lock (root._variationCacheLock)
        {
            root._variationInstanceCache ??= new Dictionary<string, TtfFont>(StringComparer.Ordinal);
            root._variationInstanceOrder ??= new Queue<string>();
            if (root._variationInstanceCache.TryGetValue(instance.CacheKey, out TtfFont? cached))
            {
                return cached;
            }

            var created = new TtfFont(root, data, instance);
            root._variationInstanceCache.Add(instance.CacheKey, created);
            root._variationInstanceOrder.Enqueue(instance.CacheKey);
            while (root._variationInstanceOrder.Count > VariationInstanceCacheLimit)
            {
                string oldest = root._variationInstanceOrder.Dequeue();
                root._variationInstanceCache.Remove(oldest);
            }

            return created;
        }
    }

    private OpenTypeVariationData? GetVariationData()
    {
        if (Volatile.Read(ref _variationDataInitialized))
        {
            return _variationData;
        }

        lock (_variationCacheLock)
        {
            if (!_variationDataInitialized)
            {
                _variationData = OpenTypeVariationData.TryCreate(
                    _data,
                    _tables,
                    nameId => _face.TryGetName(nameId, out string? name) ? name : null,
                    NumGlyphs);
                Volatile.Write(ref _variationDataInitialized, true);
            }
            return _variationData;
        }
    }

    private TtfFont(
        TtfFont source,
        OpenTypeVariationData variationData,
        OpenTypeVariationInstance variationInstance)
    {
        _data = source._data;
        _face = source._face;
        _cffOutlineSource = source._cffOutlineSource;
        _cffTypeface = source._cffTypeface;
        _tables = source._tables;
        _svgColorLayerCache = source._svgColorLayerCache;

        FaceIndex = source.FaceIndex;
        FamilyName = source.FamilyName;
        SubfamilyName = source.SubfamilyName;
        FullName = source.FullName;
        PostScriptName = source.PostScriptName;
        WeightClass = source.WeightClass;
        WidthClass = source.WidthClass;
        IsItalic = source.IsItalic;
        HasTrueTypeOutlines = source.HasTrueTypeOutlines;
        HasBitmapGlyphs = source.HasBitmapGlyphs;
        HasColorGlyphs = source.HasColorGlyphs;
        IsFixedPitch = source.IsFixedPitch;

        UnitsPerEm = source.UnitsPerEm;
        XMin = source.XMin;
        YMin = source.YMin;
        XMax = source.XMax;
        YMax = source.YMax;
        Ascender = source.Ascender;
        Descender = source.Descender;
        LineGap = source.LineGap;
        UnderlinePosition = source.UnderlinePosition;
        UnderlineThickness = source.UnderlineThickness;
        StrikeoutPosition = source.StrikeoutPosition;
        StrikeoutThickness = source.StrikeoutThickness;
        XHeight = source.XHeight;
        CapHeight = source.CapHeight;
        NumGlyphs = source.NumGlyphs;

        _indexToLocFormat = source._indexToLocFormat;
        _numberOfHMetrics = source._numberOfHMetrics;
        _hmtxOffset = source._hmtxOffset;
        _numberOfVMetrics = source._numberOfVMetrics;
        _vmtxOffset = source._vmtxOffset;
        _locaOffset = source._locaOffset;
        _glyfOffset = source._glyfOffset;
        _colrOffset = source._colrOffset;
        _cpalOffset = source._cpalOffset;
        _numPaletteEntries = source._numPaletteEntries;
        _numPalettes = source._numPalettes;
        _numColorRecords = source._numColorRecords;
        _colorRecordsOffset = source._colorRecordsOffset;
        _colorPalette = source._colorPalette;
        _numBaseGlyphRecords = source._numBaseGlyphRecords;
        _baseGlyphRecordsOffset = source._baseGlyphRecordsOffset;
        _layerRecordsOffset = source._layerRecordsOffset;
        _numLayerRecords = source._numLayerRecords;

        _variationRoot = source;
        _variationData = variationData;
        _variationDataInitialized = true;
        _variationInstance = variationInstance;
        ApplyVariationMetadata(variationData, variationInstance);
    }

    /// <summary>
    /// Variable instances at identical normalized coordinates compare equal even after the
    /// small construction cache rotates. Retained layout and atlas dictionaries can therefore
    /// reuse their original GPU entries instead of accumulating duplicates during axis drags.
    /// Ordinary font objects retain reference identity.
    /// </summary>
    public bool Equals(TtfFont? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null ||
            _variationInstance is null ||
            other._variationInstance is null ||
            !ReferenceEquals(_variationRoot, other._variationRoot))
        {
            return false;
        }

        return _variationInstance.CacheKey.Equals(
            other._variationInstance.CacheKey,
            StringComparison.Ordinal);
    }

    public override bool Equals(object? obj) => obj is TtfFont other && Equals(other);

    public override int GetHashCode()
    {
        if (_variationInstance is null || _variationRoot is null)
        {
            return RuntimeHelpers.GetHashCode(this);
        }

        return HashCode.Combine(
            RuntimeHelpers.GetHashCode(_variationRoot),
            StringComparer.Ordinal.GetHashCode(_variationInstance.CacheKey));
    }

    private void ApplyVariationMetadata(
        OpenTypeVariationData data,
        OpenTypeVariationInstance instance)
    {
        if (data.TryGetUserCoordinate(instance, "wght", out float weight))
        {
            WeightClass = (ushort)Math.Clamp((int)MathF.Round(weight), 1, 1000);
        }

        if (data.TryGetUserCoordinate(instance, "ital", out float italic))
        {
            IsItalic = italic >= 0.5f;
        }

        if (data.TryGetUserCoordinate(instance, "slnt", out float slant))
        {
            IsItalic |= MathF.Abs(slant) > 0.01f;
        }

        Ascender = AddMetricDelta(Ascender, data.GetMetricDelta(instance, "hasc"));
        Descender = AddMetricDelta(Descender, data.GetMetricDelta(instance, "hdsc"));
        LineGap = AddMetricDelta(LineGap, data.GetMetricDelta(instance, "hlgp"));
        XHeight = AddMetricDelta(XHeight, data.GetMetricDelta(instance, "xhgt"));
        CapHeight = AddMetricDelta(CapHeight, data.GetMetricDelta(instance, "cpht"));
        UnderlinePosition = AddMetricDelta(UnderlinePosition, data.GetMetricDelta(instance, "undo"));
        UnderlineThickness = AddMetricDelta(UnderlineThickness, data.GetMetricDelta(instance, "unds"));
        StrikeoutPosition = AddMetricDelta(StrikeoutPosition, data.GetMetricDelta(instance, "stro"));
        StrikeoutThickness = AddMetricDelta(StrikeoutThickness, data.GetMetricDelta(instance, "strs"));
    }

    private static short AddMetricDelta(short value, float delta) =>
        (short)Math.Clamp(MathF.Round(value + delta), short.MinValue, short.MaxValue);

    private static short? AddMetricDelta(short? value, float delta) =>
        value is null ? null : AddMetricDelta(value.Value, delta);

    private float GetVariationAdvanceDelta(ushort glyphIndex)
    {
        if (_variationInstance is null || _variationData is null) return 0f;
        if (!_variationData.UsesGlyphPhantomAdvance)
            return _variationData.GetAdvanceDelta(_variationInstance, glyphIndex);
        return _variationData.GetGlyphPhantomAdvanceDelta(
            _variationInstance,
            glyphIndex,
            GetGlyphVariationItemCount(glyphIndex));
    }

    private int GetGlyphVariationItemCount(ushort glyphIndex)
    {
        TtfFont root = _variationRoot ?? this;
        int[] cache;
        lock (root._variationCacheLock)
        {
            cache = root._variationItemCountCache ??= new int[root.NumGlyphs];
        }
        int cached = Volatile.Read(ref cache[glyphIndex]);
        if (cached != 0) return cached;
        int computed = root.ComputeGlyphVariationItemCount(glyphIndex);
        Interlocked.CompareExchange(ref cache[glyphIndex], computed, 0);
        return cache[glyphIndex];
    }

    private int ComputeGlyphVariationItemCount(ushort glyphIndex)
    {
        if (!HasTrueTypeOutlines || glyphIndex >= NumGlyphs) return 4;
        uint startOffset;
        uint endOffset;
        if (_indexToLocFormat == 0)
        {
            startOffset = (uint)(ReadUShort(_locaOffset + (uint)(glyphIndex * 2)) * 2);
            endOffset = (uint)(ReadUShort(_locaOffset + (uint)((glyphIndex + 1) * 2)) * 2);
        }
        else
        {
            startOffset = ReadUInt(_locaOffset + (uint)(glyphIndex * 4));
            endOffset = ReadUInt(_locaOffset + (uint)((glyphIndex + 1) * 4));
        }
        if (startOffset == endOffset) return 4;

        uint glyphOffset = _glyfOffset + startOffset;
        if (glyphOffset > (uint)_data.Length - 10u) return 4;
        short contourCount = ReadShort(glyphOffset);
        if (contourCount >= 0)
        {
            if (contourCount == 0) return 4;
            uint lastContourEnd = glyphOffset + 10u + (uint)(contourCount - 1) * 2u;
            return lastContourEnd <= (uint)_data.Length - 2u
                ? ReadUShort(lastContourEnd) + 5
                : 4;
        }

        const ushort ArgumentsAreWords = 0x0001;
        const ushort WeHaveScale = 0x0008;
        const ushort MoreComponents = 0x0020;
        const ushort WeHaveXAndYScale = 0x0040;
        const ushort WeHaveTwoByTwo = 0x0080;
        uint cursor = glyphOffset + 10;
        int componentCount = 0;
        ushort flags;
        do
        {
            if (cursor > (uint)_data.Length - 4u) return 4;
            flags = ReadUShort(cursor);
            cursor += 4;
            cursor += (flags & ArgumentsAreWords) != 0 ? 4u : 2u;
            if ((flags & WeHaveScale) != 0) cursor += 2;
            else if ((flags & WeHaveXAndYScale) != 0) cursor += 4;
            else if ((flags & WeHaveTwoByTwo) != 0) cursor += 8;
            componentCount++;
        }
        while ((flags & MoreComponents) != 0 && cursor <= (uint)_data.Length);
        return componentCount + 4;
    }

    internal float GetLayoutVariationDelta(ushort outerIndex, ushort innerIndex) =>
        _variationInstance is null || _variationData is null
            ? 0f
            : _variationData.GetLayoutDelta(_variationInstance, outerIndex, innerIndex);

    internal bool TryGetNormalizedVariationCoordinate(int axisIndex, out short coordinate)
    {
        OpenTypeVariationData? data = GetVariationData();
        if (data is null || (uint)axisIndex >= data.Axes.Count)
        {
            coordinate = 0;
            return false;
        }
        coordinate = _variationInstance?.NormalizedCoordinates[axisIndex] ?? 0;
        return true;
    }

    private void ApplySimpleGlyphVariations(
        ushort glyphIndex,
        Vector2[] coordinates,
        ushort[] contourEndPoints)
    {
        if (_variationInstance is null || _variationData is null || _variationInstance.IsDefault)
        {
            return;
        }

        _variationData.ApplySimpleGlyphDeltas(
            _variationInstance,
            glyphIndex,
            coordinates,
            contourEndPoints);
    }

    private Vector2[] GetCompositeGlyphVariationOffsets(int componentCount, ushort glyphIndex)
    {
        if (_variationInstance is null || _variationData is null || _variationInstance.IsDefault)
        {
            return Array.Empty<Vector2>();
        }

        return _variationData.GetCompositeGlyphDeltas(
            _variationInstance,
            glyphIndex,
            componentCount);
    }
}
