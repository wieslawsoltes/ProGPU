using System.Numerics;
using System.Text;

namespace ProGPU.Text;

internal sealed class OpenTypeVariationInstance
{
    public required short[] NormalizedCoordinates { get; init; }
    public required FontVariationSetting[] Settings { get; init; }
    public required string CacheKey { get; init; }
    public required float[] RegionScalars { get; init; }
    public bool IsDefault { get; init; }
}

/// <summary>
/// Parsed, process-neutral OpenType variation data. Parsing is O(B) once for B table
/// bytes. HVAR/MVAR lookup is O(R) for referenced regions; gvar interpolation is O(P+T)
/// for P glyph points and T tuple records and occurs only on first outline use per instance.
/// </summary>
internal sealed class OpenTypeVariationData
{
    private sealed class AxisData
    {
        public required FontVariationAxis PublicAxis { get; init; }
        public required int MinimumFixed { get; init; }
        public required int DefaultFixed { get; init; }
        public required int MaximumFixed { get; init; }
        public AxisMap[] Map { get; set; } = [];
    }

    private readonly record struct AxisMap(short From, short To);
    private readonly record struct RegionAxis(float Start, float Peak, float End);
    private readonly record struct MvarRecord(ushort OuterIndex, ushort InnerIndex);

    private sealed class TupleData
    {
        public required float[] Start { get; init; }
        public required float[] Peak { get; init; }
        public required float[] End { get; init; }
        public required int[]? PointNumbers { get; init; }
        public required short[] X { get; init; }
        public required short[] Y { get; init; }
    }

    private sealed class GlyphVariationData
    {
        public required TupleData[] Tuples { get; init; }
    }

    private sealed class ItemVariationStore
    {
        internal sealed class Subtable
        {
            public required ushort ItemCount { get; init; }
            public required ushort WordDeltaCount { get; init; }
            public required bool LongWords { get; init; }
            public required ushort[] RegionIndexes { get; init; }
            public required int DeltaOffset { get; init; }
            public required int BytesPerRow { get; init; }
        }

        public required RegionAxis[][] Regions { get; init; }
        public required Subtable?[] Subtables { get; init; }
    }

    private sealed class DeltaSetIndexMap
    {
        private readonly uint[] _entries;
        private readonly int _innerBits;

        public DeltaSetIndexMap(uint[] entries, int innerBits)
        {
            _entries = entries;
            _innerBits = innerBits;
        }

        public (ushort Outer, ushort Inner) Get(int index)
        {
            if (_entries.Length == 0)
            {
                return (0, (ushort)Math.Clamp(index, 0, ushort.MaxValue));
            }

            uint entry = _entries[Math.Min(index, _entries.Length - 1)];
            uint mask = _innerBits == 32 ? uint.MaxValue : (1u << _innerBits) - 1u;
            return ((ushort)(entry >> _innerBits), (ushort)(entry & mask));
        }
    }

    private readonly byte[] _data;
    private readonly AxisData[] _axisData;
    private readonly FontVariationAxis[] _axes;
    private readonly int _glyphCount;
    private readonly object _glyphLock = new();
    private readonly GlyphVariationData?[]? _glyphVariations;
    private readonly bool[]? _glyphVariationsParsed;
    private readonly float[][] _sharedTuples = [];
    private readonly int[]? _glyphVariationOffsets;
    private readonly int _glyphVariationDataBase;
    private readonly ItemVariationStore? _itemStore;
    private readonly ItemVariationStore? _layoutStore;
    private readonly DeltaSetIndexMap? _advanceMap;
    private readonly Dictionary<string, MvarRecord> _metricRecords = new(StringComparer.Ordinal);

    private OpenTypeVariationData(
        byte[] data,
        Dictionary<string, (uint offset, uint length)> tables,
        Func<ushort, string?> nameResolver,
        int glyphCount)
    {
        _data = data;
        _glyphCount = glyphCount;
        _axisData = ParseAxes(data, tables, nameResolver);
        _axes = new FontVariationAxis[_axisData.Length];
        for (var index = 0; index < _axisData.Length; index++)
        {
            _axes[index] = _axisData[index].PublicAxis;
        }

        ParseAvar(tables);
        if (TryParseGvar(tables, out float[][] sharedTuples, out int[]? offsets, out int variationBase))
        {
            _sharedTuples = sharedTuples;
            _glyphVariationOffsets = offsets;
            _glyphVariationDataBase = variationBase;
            _glyphVariations = new GlyphVariationData?[glyphCount];
            _glyphVariationsParsed = new bool[glyphCount];
        }

        if (tables.TryGetValue("HVAR", out var hvar))
        {
            int hvarOffset = checked((int)hvar.offset);
            int storeRelative = ReadInt32(data, hvarOffset + 4);
            if (storeRelative > 0)
            {
                _itemStore = TryParseItemVariationStore(hvarOffset + storeRelative);
            }

            int mapRelative = ReadInt32(data, hvarOffset + 8);
            if (mapRelative > 0)
            {
                _advanceMap = TryParseDeltaSetIndexMap(hvarOffset + mapRelative);
            }
        }


        if (tables.TryGetValue("GDEF", out var gdef) && gdef.length >= 18)
        {
            int gdefOffset = checked((int)gdef.offset);
            ushort major = ReadUInt16(data, gdefOffset);
            ushort minor = ReadUInt16(data, gdefOffset + 2);
            if (major == 1 && minor >= 3)
            {
                int storeRelative = ReadInt32(data, gdefOffset + 14);
                if (storeRelative > 0)
                {
                    _layoutStore = TryParseItemVariationStore(gdefOffset + storeRelative);
                }
            }
        }

        ParseMvar(tables);
    }

    public IReadOnlyList<FontVariationAxis> Axes => _axes;

    public static OpenTypeVariationData? TryCreate(
        byte[] data,
        Dictionary<string, (uint offset, uint length)> tables,
        Func<ushort, string?> nameResolver,
        int glyphCount)
    {
        if (!tables.ContainsKey("fvar"))
        {
            return null;
        }

        try
        {
            var result = new OpenTypeVariationData(data, tables, nameResolver, glyphCount);
            return result._axisData.Length == 0 ? null : result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ProGpuTextDiagnostics.WriteLine($"[TtfFont] Invalid OpenType variation data: {exception.Message}");
            return null;
        }
    }

    public OpenTypeVariationInstance CreateInstance(IReadOnlyList<FontVariationSetting> settings)
    {
        var selected = new float[_axisData.Length];
        for (var index = 0; index < _axisData.Length; index++)
        {
            selected[index] = _axisData[index].PublicAxis.Default;
        }

        for (var settingIndex = 0; settingIndex < settings.Count; settingIndex++)
        {
            FontVariationSetting setting = settings[settingIndex];
            for (var axisIndex = 0; axisIndex < _axisData.Length; axisIndex++)
            {
                if (_axisData[axisIndex].PublicAxis.Tag.Equals(setting.Tag, StringComparison.Ordinal))
                {
                    selected[axisIndex] = setting.Value;
                    break;
                }
            }
        }

        var normalized = new short[_axisData.Length];
        var publicSettings = new FontVariationSetting[_axisData.Length];
        var keyChars = new char[_axisData.Length];
        var isDefault = true;
        for (var index = 0; index < _axisData.Length; index++)
        {
            AxisData axis = _axisData[index];
            float clamped = Math.Clamp(
                selected[index],
                axis.PublicAxis.Minimum,
                axis.PublicAxis.Maximum);
            int userFixed = FloatToFixed(clamped);
            short coordinate = Normalize(axis, userFixed);
            normalized[index] = coordinate;
            keyChars[index] = unchecked((char)(ushort)coordinate);
            isDefault &= coordinate == 0;
            publicSettings[index] = new FontVariationSetting(
                axis.PublicAxis.Tag,
                FixedToFloat(userFixed));
        }

        return new OpenTypeVariationInstance
        {
            NormalizedCoordinates = normalized,
            Settings = publicSettings,
            CacheKey = new string(keyChars),
            RegionScalars = ComputeRegionScalars(normalized),
            IsDefault = isDefault
        };
    }

    public bool TryGetUserCoordinate(
        OpenTypeVariationInstance instance,
        string tag,
        out float value)
    {
        for (var index = 0; index < _axisData.Length; index++)
        {
            if (_axisData[index].PublicAxis.Tag.Equals(tag, StringComparison.Ordinal))
            {
                value = instance.Settings[index].Value;
                return true;
            }
        }

        value = 0;
        return false;
    }

    public float GetAdvanceDelta(OpenTypeVariationInstance instance, ushort glyphIndex)
    {
        if (_itemStore is null)
        {
            return 0f;
        }

        (ushort outer, ushort inner) = _advanceMap?.Get(glyphIndex) ?? (0, glyphIndex);
        return GetItemDelta(_itemStore, instance, outer, inner);
    }

    public float GetMetricDelta(OpenTypeVariationInstance instance, string tag)
    {
        if (_mvarStore is null || !_metricRecords.TryGetValue(tag, out MvarRecord record))
        {
            return 0f;
        }

        return GetItemDelta(_mvarStore, instance, record.OuterIndex, record.InnerIndex);
    }

    public float GetLayoutDelta(
        OpenTypeVariationInstance instance,
        ushort outerIndex,
        ushort innerIndex) =>
        _layoutStore is null ? 0f : GetItemDelta(_layoutStore, instance, outerIndex, innerIndex);

    public void ApplySimpleGlyphDeltas(
        OpenTypeVariationInstance instance,
        ushort glyphIndex,
        Vector2[] coordinates,
        ushort[] contourEndPoints)
    {
        GlyphVariationData? variation = GetGlyphVariation(glyphIndex, coordinates.Length + 4);
        if (variation is null)
        {
            return;
        }

        var tupleX = new float[coordinates.Length];
        var tupleY = new float[coordinates.Length];
        var touched = new bool[coordinates.Length];
        for (var tupleIndex = 0; tupleIndex < variation.Tuples.Length; tupleIndex++)
        {
            TupleData tuple = variation.Tuples[tupleIndex];
            float scalar = CalculateScalar(instance.NormalizedCoordinates, tuple.Start, tuple.Peak, tuple.End);
            if (scalar == 0f)
            {
                continue;
            }

            Array.Clear(tupleX);
            Array.Clear(tupleY);
            Array.Clear(touched);
            if (tuple.PointNumbers is null)
            {
                int count = Math.Min(coordinates.Length, tuple.X.Length);
                for (var point = 0; point < count; point++)
                {
                    tupleX[point] = tuple.X[point];
                    tupleY[point] = tuple.Y[point];
                    touched[point] = true;
                }
            }
            else
            {
                for (var deltaIndex = 0; deltaIndex < tuple.PointNumbers.Length; deltaIndex++)
                {
                    int point = tuple.PointNumbers[deltaIndex];
                    if ((uint)point >= (uint)coordinates.Length)
                    {
                        continue;
                    }

                    tupleX[point] += tuple.X[deltaIndex];
                    tupleY[point] += tuple.Y[deltaIndex];
                    touched[point] = true;
                }

                InferUntouchedDeltas(coordinates, contourEndPoints, tupleX, tupleY, touched);
            }

            for (var point = 0; point < coordinates.Length; point++)
            {
                coordinates[point].X += tupleX[point] * scalar;
                coordinates[point].Y += tupleY[point] * scalar;
            }
        }
    }

    public Vector2[] GetCompositeGlyphDeltas(
        OpenTypeVariationInstance instance,
        ushort glyphIndex,
        int componentCount)
    {
        GlyphVariationData? variation = GetGlyphVariation(glyphIndex, componentCount + 4);
        if (variation is null)
        {
            return Array.Empty<Vector2>();
        }

        var result = new Vector2[componentCount];
        for (var tupleIndex = 0; tupleIndex < variation.Tuples.Length; tupleIndex++)
        {
            TupleData tuple = variation.Tuples[tupleIndex];
            float scalar = CalculateScalar(instance.NormalizedCoordinates, tuple.Start, tuple.Peak, tuple.End);
            if (scalar == 0f)
            {
                continue;
            }

            if (tuple.PointNumbers is null)
            {
                int count = Math.Min(componentCount, tuple.X.Length);
                for (var component = 0; component < count; component++)
                {
                    result[component].X += tuple.X[component] * scalar;
                    result[component].Y += tuple.Y[component] * scalar;
                }
            }
            else
            {
                for (var deltaIndex = 0; deltaIndex < tuple.PointNumbers.Length; deltaIndex++)
                {
                    int component = tuple.PointNumbers[deltaIndex];
                    if ((uint)component >= (uint)componentCount)
                    {
                        continue;
                    }

                    result[component].X += tuple.X[deltaIndex] * scalar;
                    result[component].Y += tuple.Y[deltaIndex] * scalar;
                }
            }
        }

        return result;
    }

    private static AxisData[] ParseAxes(
        byte[] data,
        Dictionary<string, (uint offset, uint length)> tables,
        Func<ushort, string?> nameResolver)
    {
        (uint tableOffset, uint tableLength) = tables["fvar"];
        int start = checked((int)tableOffset);
        int end = checked(start + (int)tableLength);
        EnsureRange(data, start, 16, end);
        int axesOffset = ReadUInt16(data, start + 4);
        int axisCount = ReadUInt16(data, start + 8);
        int axisSize = ReadUInt16(data, start + 10);
        if (axisCount <= 0 || axisSize < 20)
        {
            return [];
        }

        var result = new AxisData[axisCount];
        for (var index = 0; index < axisCount; index++)
        {
            int offset = checked(start + axesOffset + index * axisSize);
            EnsureRange(data, offset, 20, end);
            string tag = ReadTag(data, offset);
            int minimum = ReadInt32(data, offset + 4);
            int @default = ReadInt32(data, offset + 8);
            int maximum = ReadInt32(data, offset + 12);
            ushort flags = ReadUInt16(data, offset + 16);
            ushort nameId = ReadUInt16(data, offset + 18);
            result[index] = new AxisData
            {
                PublicAxis = new FontVariationAxis(
                    tag,
                    nameResolver(nameId) ?? tag,
                    FixedToFloat(minimum),
                    FixedToFloat(@default),
                    FixedToFloat(maximum),
                    (flags & 1) != 0),
                MinimumFixed = minimum,
                DefaultFixed = @default,
                MaximumFixed = maximum
            };
        }

        return result;
    }

    private void ParseAvar(Dictionary<string, (uint offset, uint length)> tables)
    {
        if (!tables.TryGetValue("avar", out var table))
        {
            return;
        }

        int start = checked((int)table.offset);
        int end = checked(start + (int)table.length);
        EnsureRange(_data, start, 8, end);
        int axisCount = ReadUInt16(_data, start + 6);
        if (axisCount != _axisData.Length)
        {
            return;
        }

        int offset = start + 8;
        for (var axisIndex = 0; axisIndex < axisCount; axisIndex++)
        {
            EnsureRange(_data, offset, 2, end);
            int count = ReadUInt16(_data, offset);
            offset += 2;
            var map = new AxisMap[count];
            for (var mapIndex = 0; mapIndex < count; mapIndex++)
            {
                EnsureRange(_data, offset, 4, end);
                map[mapIndex] = new AxisMap(ReadInt16(_data, offset), ReadInt16(_data, offset + 2));
                offset += 4;
            }

            _axisData[axisIndex].Map = map;
        }
    }

    private bool TryParseGvar(
        Dictionary<string, (uint offset, uint length)> tables,
        out float[][] sharedTuples,
        out int[]? glyphOffsets,
        out int glyphDataBase)
    {
        sharedTuples = [];
        glyphOffsets = null;
        glyphDataBase = 0;
        if (!tables.TryGetValue("gvar", out var table))
        {
            return false;
        }

        int start = checked((int)table.offset);
        int end = checked(start + (int)table.length);
        EnsureRange(_data, start, 20, end);
        int axisCount = ReadUInt16(_data, start + 4);
        int sharedCount = ReadUInt16(_data, start + 6);
        int sharedOffset = checked((int)ReadUInt32(_data, start + 8));
        int glyphCount = ReadUInt16(_data, start + 12);
        int flags = ReadUInt16(_data, start + 14);
        int dataOffset = checked((int)ReadUInt32(_data, start + 16));
        if (axisCount != _axisData.Length || glyphCount != _glyphCount)
        {
            return false;
        }

        int offsetsStart = start + 20;
        glyphOffsets = new int[glyphCount + 1];
        bool longOffsets = (flags & 1) != 0;
        for (var index = 0; index <= glyphCount; index++)
        {
            if (longOffsets)
            {
                EnsureRange(_data, offsetsStart + index * 4, 4, end);
                glyphOffsets[index] = checked((int)ReadUInt32(_data, offsetsStart + index * 4));
            }
            else
            {
                EnsureRange(_data, offsetsStart + index * 2, 2, end);
                glyphOffsets[index] = ReadUInt16(_data, offsetsStart + index * 2) * 2;
            }
        }

        sharedTuples = new float[sharedCount][];
        int tupleOffset = start + sharedOffset;
        for (var tupleIndex = 0; tupleIndex < sharedCount; tupleIndex++)
        {
            sharedTuples[tupleIndex] = ReadTuple(tupleOffset, end);
            tupleOffset += axisCount * 2;
        }

        glyphDataBase = start + dataOffset;
        return glyphDataBase >= start && glyphDataBase <= end;
    }

    private GlyphVariationData? GetGlyphVariation(ushort glyphIndex, int itemCount)
    {
        if (_glyphVariationOffsets is null ||
            _glyphVariations is null ||
            _glyphVariationsParsed is null ||
            glyphIndex >= _glyphVariations.Length)
        {
            return null;
        }

        lock (_glyphLock)
        {
            if (_glyphVariationsParsed[glyphIndex])
            {
                return _glyphVariations[glyphIndex];
            }

            _glyphVariationsParsed[glyphIndex] = true;
            int relativeStart = _glyphVariationOffsets[glyphIndex];
            int relativeEnd = _glyphVariationOffsets[glyphIndex + 1];
            if (relativeStart == relativeEnd)
            {
                return null;
            }

            _glyphVariations[glyphIndex] = ParseGlyphVariation(
                _glyphVariationDataBase + relativeStart,
                _glyphVariationDataBase + relativeEnd,
                itemCount);
            return _glyphVariations[glyphIndex];
        }
    }

    private GlyphVariationData? ParseGlyphVariation(int start, int end, int itemCount)
    {
        EnsureRange(_data, start, 4, end);
        int countAndFlags = ReadUInt16(_data, start);
        int tupleCount = countAndFlags & 0x0FFF;
        int dataOffset = ReadUInt16(_data, start + 2);
        int headerOffset = start + 4;
        var headers = new (int Size, int Flags, float[] Start, float[] Peak, float[] End)[tupleCount];
        for (var tupleIndex = 0; tupleIndex < tupleCount; tupleIndex++)
        {
            EnsureRange(_data, headerOffset, 4, end);
            int size = ReadUInt16(_data, headerOffset);
            int tupleFlags = ReadUInt16(_data, headerOffset + 2);
            headerOffset += 4;

            float[] peak;
            if ((tupleFlags & 0x8000) != 0)
            {
                peak = ReadTuple(headerOffset, end);
                headerOffset += _axisData.Length * 2;
            }
            else
            {
                int sharedIndex = tupleFlags & 0x0FFF;
                if ((uint)sharedIndex >= (uint)_sharedTuples.Length)
                {
                    return null;
                }
                peak = _sharedTuples[sharedIndex];
            }

            float[] regionStart;
            float[] regionEnd;
            if ((tupleFlags & 0x4000) != 0)
            {
                regionStart = ReadTuple(headerOffset, end);
                headerOffset += _axisData.Length * 2;
                regionEnd = ReadTuple(headerOffset, end);
                headerOffset += _axisData.Length * 2;
            }
            else
            {
                regionStart = new float[_axisData.Length];
                regionEnd = new float[_axisData.Length];
                for (var axis = 0; axis < peak.Length; axis++)
                {
                    regionStart[axis] = MathF.Min(peak[axis], 0f);
                    regionEnd[axis] = MathF.Max(peak[axis], 0f);
                }
            }

            headers[tupleIndex] = (size, tupleFlags, regionStart, peak, regionEnd);
        }

        int serializedOffset = start + dataOffset;
        int[]? sharedPoints = null;
        if ((countAndFlags & 0x8000) != 0)
        {
            sharedPoints = ReadPackedPoints(ref serializedOffset, end);
        }

        var tuples = new TupleData[tupleCount];
        for (var tupleIndex = 0; tupleIndex < tupleCount; tupleIndex++)
        {
            var header = headers[tupleIndex];
            int tupleEnd = checked(serializedOffset + header.Size);
            if (tupleEnd > end)
            {
                return null;
            }

            int[]? points = (header.Flags & 0x2000) != 0
                ? ReadPackedPoints(ref serializedOffset, tupleEnd)
                : sharedPoints;
            int deltaCount = points?.Length ?? itemCount;
            short[] x = ReadPackedDeltas(ref serializedOffset, tupleEnd, deltaCount);
            short[] y = ReadPackedDeltas(ref serializedOffset, tupleEnd, deltaCount);
            serializedOffset = tupleEnd;
            tuples[tupleIndex] = new TupleData
            {
                Start = header.Start,
                Peak = header.Peak,
                End = header.End,
                PointNumbers = points,
                X = x,
                Y = y
            };
        }

        return new GlyphVariationData { Tuples = tuples };
    }

    private int[]? ReadPackedPoints(ref int offset, int end)
    {
        EnsureRange(_data, offset, 1, end);
        int first = _data[offset++];
        if (first == 0)
        {
            return null;
        }

        int count;
        if ((first & 0x80) != 0)
        {
            EnsureRange(_data, offset, 1, end);
            count = ((first & 0x7F) << 8) | _data[offset++];
        }
        else
        {
            count = first;
        }

        var points = new int[count];
        int written = 0;
        int current = 0;
        while (written < count)
        {
            EnsureRange(_data, offset, 1, end);
            int control = _data[offset++];
            int runCount = (control & 0x7F) + 1;
            bool words = (control & 0x80) != 0;
            if (written + runCount > count)
            {
                throw new InvalidDataException("Packed point run exceeds its declared count.");
            }

            for (var run = 0; run < runCount; run++)
            {
                int delta;
                if (words)
                {
                    EnsureRange(_data, offset, 2, end);
                    delta = ReadUInt16(_data, offset);
                    offset += 2;
                }
                else
                {
                    EnsureRange(_data, offset, 1, end);
                    delta = _data[offset++];
                }

                current += delta;
                points[written++] = current;
            }
        }

        return points;
    }

    private short[] ReadPackedDeltas(ref int offset, int end, int count)
    {
        var result = new short[count];
        int written = 0;
        while (written < count)
        {
            EnsureRange(_data, offset, 1, end);
            int control = _data[offset++];
            int runCount = (control & 0x3F) + 1;
            if (written + runCount > count)
            {
                throw new InvalidDataException("Packed delta run exceeds its expected count.");
            }

            if ((control & 0x80) != 0)
            {
                written += runCount;
                continue;
            }

            bool words = (control & 0x40) != 0;
            for (var run = 0; run < runCount; run++)
            {
                if (words)
                {
                    EnsureRange(_data, offset, 2, end);
                    result[written++] = ReadInt16(_data, offset);
                    offset += 2;
                }
                else
                {
                    EnsureRange(_data, offset, 1, end);
                    result[written++] = unchecked((sbyte)_data[offset++]);
                }
            }
        }

        return result;
    }

    private ItemVariationStore? TryParseItemVariationStore(int start)
    {
        EnsureRange(_data, start, 8, _data.Length);
        if (ReadUInt16(_data, start) != 1)
        {
            return null;
        }

        int regionRelative = ReadInt32(_data, start + 2);
        int subtableCount = ReadUInt16(_data, start + 6);
        EnsureRange(_data, start + 8, subtableCount * 4, _data.Length);
        var offsets = new int[subtableCount];
        for (var index = 0; index < subtableCount; index++)
        {
            offsets[index] = ReadInt32(_data, start + 8 + index * 4);
        }

        int regionOffset = start + regionRelative;
        EnsureRange(_data, regionOffset, 4, _data.Length);
        int axisCount = ReadUInt16(_data, regionOffset);
        int regionCount = ReadUInt16(_data, regionOffset + 2);
        if (axisCount != _axisData.Length)
        {
            return null;
        }

        var regions = new RegionAxis[regionCount][];
        int cursor = regionOffset + 4;
        for (var regionIndex = 0; regionIndex < regionCount; regionIndex++)
        {
            var axes = new RegionAxis[axisCount];
            for (var axisIndex = 0; axisIndex < axisCount; axisIndex++)
            {
                EnsureRange(_data, cursor, 6, _data.Length);
                axes[axisIndex] = new RegionAxis(
                    ReadF2Dot14(_data, cursor),
                    ReadF2Dot14(_data, cursor + 2),
                    ReadF2Dot14(_data, cursor + 4));
                cursor += 6;
            }
            regions[regionIndex] = axes;
        }

        var subtables = new ItemVariationStore.Subtable?[subtableCount];
        for (var tableIndex = 0; tableIndex < subtableCount; tableIndex++)
        {
            if (offsets[tableIndex] == 0)
            {
                continue;
            }

            int tableOffset = start + offsets[tableIndex];
            EnsureRange(_data, tableOffset, 6, _data.Length);
            ushort itemCount = ReadUInt16(_data, tableOffset);
            ushort packedWordCount = ReadUInt16(_data, tableOffset + 2);
            int regionIndexCount = ReadUInt16(_data, tableOffset + 4);
            bool longWords = (packedWordCount & 0x8000) != 0;
            ushort wordCount = (ushort)(packedWordCount & 0x7FFF);
            if (wordCount > regionIndexCount)
            {
                return null;
            }

            EnsureRange(_data, tableOffset + 6, regionIndexCount * 2, _data.Length);
            var regionIndexes = new ushort[regionIndexCount];
            for (var region = 0; region < regionIndexCount; region++)
            {
                regionIndexes[region] = ReadUInt16(_data, tableOffset + 6 + region * 2);
            }

            int wordBytes = longWords ? 4 : 2;
            int shortBytes = longWords ? 2 : 1;
            subtables[tableIndex] = new ItemVariationStore.Subtable
            {
                ItemCount = itemCount,
                WordDeltaCount = wordCount,
                LongWords = longWords,
                RegionIndexes = regionIndexes,
                DeltaOffset = tableOffset + 6 + regionIndexCount * 2,
                BytesPerRow = wordCount * wordBytes + (regionIndexCount - wordCount) * shortBytes
            };
        }

        return new ItemVariationStore { Regions = regions, Subtables = subtables };
    }

    private DeltaSetIndexMap? TryParseDeltaSetIndexMap(int start)
    {
        EnsureRange(_data, start, 4, _data.Length);
        int format = _data[start];
        int entryFormat = _data[start + 1];
        int count;
        int cursor;
        if (format == 0)
        {
            count = ReadUInt16(_data, start + 2);
            cursor = start + 4;
        }
        else if (format == 1)
        {
            EnsureRange(_data, start, 6, _data.Length);
            count = checked((int)ReadUInt32(_data, start + 2));
            cursor = start + 6;
        }
        else
        {
            return null;
        }

        int entrySize = ((entryFormat & 0x30) >> 4) + 1;
        int innerBits = (entryFormat & 0x0F) + 1;
        EnsureRange(_data, cursor, checked(count * entrySize), _data.Length);
        var entries = new uint[count];
        for (var index = 0; index < count; index++)
        {
            uint value = 0;
            for (var part = 0; part < entrySize; part++)
            {
                value = (value << 8) | _data[cursor++];
            }
            entries[index] = value;
        }

        return new DeltaSetIndexMap(entries, innerBits);
    }

    private void ParseMvar(Dictionary<string, (uint offset, uint length)> tables)
    {
        if (!tables.TryGetValue("MVAR", out var table))
        {
            return;
        }

        int start = checked((int)table.offset);
        int end = checked(start + (int)table.length);
        EnsureRange(_data, start, 12, end);
        int recordSize = ReadUInt16(_data, start + 6);
        int recordCount = ReadUInt16(_data, start + 8);
        int storeRelative = ReadUInt16(_data, start + 10);
        if (recordSize < 8 || storeRelative == 0)
        {
            return;
        }

        // HVAR and MVAR normally use separate stores. If HVAR was absent, use MVAR's
        // store for metrics; when both are present retain HVAR for advances and evaluate
        // MVAR records through the store at their own location below.
        ItemVariationStore? mvarStore = TryParseItemVariationStore(start + storeRelative);
        if (mvarStore is null)
        {
            return;
        }

        int recordOffset = start + 12;
        for (var index = 0; index < recordCount; index++)
        {
            EnsureRange(_data, recordOffset, 8, end);
            string tag = ReadTag(_data, recordOffset);
            _metricRecords[tag] = new MvarRecord(
                ReadUInt16(_data, recordOffset + 4),
                ReadUInt16(_data, recordOffset + 6));
            recordOffset += recordSize;
        }

        _mvarStore = mvarStore;
    }

    private ItemVariationStore? _mvarStore;

    private float[] ComputeRegionScalars(short[] normalized)
    {
        int regionCount = Math.Max(
            _itemStore?.Regions.Length ?? 0,
            Math.Max(_mvarStore?.Regions.Length ?? 0, _layoutStore?.Regions.Length ?? 0));
        var result = new float[regionCount];
        if (_itemStore is not null)
        {
            for (var index = 0; index < _itemStore.Regions.Length; index++)
            {
                result[index] = CalculateScalar(normalized, _itemStore.Regions[index]);
            }
        }

        return result;
    }

    private float GetItemDelta(
        ItemVariationStore store,
        OpenTypeVariationInstance instance,
        ushort outerIndex,
        ushort innerIndex)
    {
        if (outerIndex >= store.Subtables.Length ||
            store.Subtables[outerIndex] is not { } subtable ||
            innerIndex >= subtable.ItemCount)
        {
            return 0f;
        }

        int offset = subtable.DeltaOffset + innerIndex * subtable.BytesPerRow;
        float result = 0f;
        for (var region = 0; region < subtable.RegionIndexes.Length; region++)
        {
            int delta;
            if (region < subtable.WordDeltaCount)
            {
                if (subtable.LongWords)
                {
                    delta = ReadInt32(_data, offset);
                    offset += 4;
                }
                else
                {
                    delta = ReadInt16(_data, offset);
                    offset += 2;
                }
            }
            else if (subtable.LongWords)
            {
                delta = ReadInt16(_data, offset);
                offset += 2;
            }
            else
            {
                delta = unchecked((sbyte)_data[offset++]);
            }

            int regionIndex = subtable.RegionIndexes[region];
            float scalar = store == _itemStore && regionIndex < instance.RegionScalars.Length
                ? instance.RegionScalars[regionIndex]
                : regionIndex < store.Regions.Length
                    ? CalculateScalar(instance.NormalizedCoordinates, store.Regions[regionIndex])
                    : 0f;
            result += delta * scalar;
        }

        return result;
    }

    private static void InferUntouchedDeltas(
        Vector2[] points,
        ushort[] contourEnds,
        float[] x,
        float[] y,
        bool[] touched)
    {
        int contourStart = 0;
        for (var contourIndex = 0; contourIndex < contourEnds.Length; contourIndex++)
        {
            int contourEnd = contourEnds[contourIndex];
            var touchedPoints = new List<int>();
            for (var point = contourStart; point <= contourEnd; point++)
            {
                if (touched[point])
                {
                    touchedPoints.Add(point);
                }
            }

            if (touchedPoints.Count == 1)
            {
                int source = touchedPoints[0];
                for (var point = contourStart; point <= contourEnd; point++)
                {
                    x[point] = x[source];
                    y[point] = y[source];
                }
            }
            else if (touchedPoints.Count > 1)
            {
                for (var pair = 0; pair < touchedPoints.Count; pair++)
                {
                    int first = touchedPoints[pair];
                    int second = touchedPoints[(pair + 1) % touchedPoints.Count];
                    int current = first;
                    while (true)
                    {
                        current = current == contourEnd ? contourStart : current + 1;
                        if (current == second)
                        {
                            break;
                        }

                        x[current] = InterpolateDelta(
                            points[current].X,
                            points[first].X,
                            points[second].X,
                            x[first],
                            x[second]);
                        y[current] = InterpolateDelta(
                            points[current].Y,
                            points[first].Y,
                            points[second].Y,
                            y[first],
                            y[second]);
                    }
                }
            }

            contourStart = contourEnd + 1;
        }
    }

    private static float InterpolateDelta(float target, float first, float second, float deltaFirst, float deltaSecond)
    {
        if (first == second)
        {
            return target <= first
                ? MathF.Min(deltaFirst, deltaSecond)
                : MathF.Max(deltaFirst, deltaSecond);
        }

        if (first > second)
        {
            (first, second) = (second, first);
            (deltaFirst, deltaSecond) = (deltaSecond, deltaFirst);
        }

        if (target <= first) return deltaFirst;
        if (target >= second) return deltaSecond;
        float ratio = (target - first) / (second - first);
        return deltaFirst + ratio * (deltaSecond - deltaFirst);
    }

    private static short Normalize(AxisData axis, int userFixed)
    {
        userFixed = Math.Clamp(userFixed, axis.MinimumFixed, axis.MaximumFixed);
        double normalized;
        if (userFixed < axis.DefaultFixed)
        {
            int range = axis.DefaultFixed - axis.MinimumFixed;
            normalized = range == 0 ? 0 : -(double)(axis.DefaultFixed - userFixed) / range;
        }
        else if (userFixed > axis.DefaultFixed)
        {
            int range = axis.MaximumFixed - axis.DefaultFixed;
            normalized = range == 0 ? 0 : (double)(userFixed - axis.DefaultFixed) / range;
        }
        else
        {
            normalized = 0;
        }

        short coordinate = (short)Math.Clamp(
            (int)Math.Round(normalized * 16384.0, MidpointRounding.AwayFromZero),
            -16384,
            16384);
        AxisMap[] map = axis.Map;
        if (map.Length < 2)
        {
            return coordinate;
        }

        for (var index = 0; index < map.Length; index++)
        {
            if (coordinate > map[index].From)
            {
                continue;
            }

            if (coordinate == map[index].From || index == 0)
            {
                return map[index].To;
            }

            AxisMap start = map[index - 1];
            AxisMap end = map[index];
            float ratio = (coordinate - start.From) / (float)(end.From - start.From);
            return (short)Math.Clamp(
                (int)MathF.Round(start.To + ratio * (end.To - start.To)),
                -16384,
                16384);
        }

        return map[^1].To;
    }

    private float[] ReadTuple(int offset, int end)
    {
        EnsureRange(_data, offset, _axisData.Length * 2, end);
        var tuple = new float[_axisData.Length];
        for (var axis = 0; axis < tuple.Length; axis++)
        {
            tuple[axis] = ReadF2Dot14(_data, offset + axis * 2);
        }
        return tuple;
    }

    private static float CalculateScalar(short[] normalized, RegionAxis[] axes)
    {
        float scalar = 1f;
        int count = Math.Min(normalized.Length, axes.Length);
        for (var axisIndex = 0; axisIndex < count; axisIndex++)
        {
            RegionAxis axis = axes[axisIndex];
            scalar *= CalculateAxisScalar(normalized[axisIndex] / 16384f, axis.Start, axis.Peak, axis.End);
            if (scalar == 0f) break;
        }
        return scalar;
    }

    private static float CalculateScalar(short[] normalized, float[] start, float[] peak, float[] end)
    {
        float scalar = 1f;
        int count = Math.Min(normalized.Length, peak.Length);
        for (var axis = 0; axis < count; axis++)
        {
            scalar *= CalculateAxisScalar(normalized[axis] / 16384f, start[axis], peak[axis], end[axis]);
            if (scalar == 0f) break;
        }
        return scalar;
    }

    private static float CalculateAxisScalar(float coordinate, float start, float peak, float end)
    {
        if (start > peak || peak > end || (start < 0f && end > 0f && peak != 0f) || peak == 0f)
        {
            return 1f;
        }
        if (coordinate < start || coordinate > end) return 0f;
        if (coordinate == peak) return 1f;
        if (coordinate < peak)
        {
            return peak == start ? 1f : (coordinate - start) / (peak - start);
        }
        return end == peak ? 1f : (end - coordinate) / (end - peak);
    }

    private float[] ComputeRegionScalarsFor(ItemVariationStore store, short[] normalized)
    {
        var scalars = new float[store.Regions.Length];
        for (var index = 0; index < scalars.Length; index++)
        {
            scalars[index] = CalculateScalar(normalized, store.Regions[index]);
        }
        return scalars;
    }

    private static int FloatToFixed(float value)
    {
        if (!float.IsFinite(value)) return 0;
        double scaled = value * 65536.0;
        return (int)Math.Clamp(
            Math.Round(scaled, MidpointRounding.AwayFromZero),
            int.MinValue,
            int.MaxValue);
    }

    private static float FixedToFloat(int value) => value / 65536f;
    private static float ReadF2Dot14(byte[] data, int offset) => ReadInt16(data, offset) / 16384f;

    private static string ReadTag(byte[] data, int offset) =>
        Encoding.ASCII.GetString(data, offset, 4);

    private static ushort ReadUInt16(byte[] data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static short ReadInt16(byte[] data, int offset) =>
        unchecked((short)ReadUInt16(data, offset));

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)((data[offset] << 24) |
               (data[offset + 1] << 16) |
               (data[offset + 2] << 8) |
               data[offset + 3]);

    private static int ReadInt32(byte[] data, int offset) => unchecked((int)ReadUInt32(data, offset));

    private static void EnsureRange(byte[] data, int offset, int length, int end)
    {
        if (offset < 0 || length < 0 || end < 0 || end > data.Length || offset > end - length)
        {
            throw new InvalidDataException("OpenType variation table contains an out-of-range offset.");
        }
    }
}
