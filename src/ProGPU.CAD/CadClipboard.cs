using System.Globalization;
using System.Security.Cryptography;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using CSMath;

namespace ProGPU.CAD;

/// <summary>
/// Detached, bounded CAD clipboard content decoded from the ProGPU DXF envelope.
/// </summary>
public sealed class CadClipboardPayload
{
    private readonly Entity[] _entities;

    public CadPoint3D BasePoint { get; }

    public int EntityCount => _entities.Length;

    public int EncodedByteCount { get; }

    internal ReadOnlySpan<Entity> Entities => _entities;

    internal CadClipboardPayload(
        CadPoint3D basePoint,
        Entity[] entities,
        int encodedByteCount)
    {
        BasePoint = basePoint;
        _entities = entities;
        EncodedByteCount = encodedByteCount;
    }
}

/// <summary>
/// Encodes COPYBASE content as a versioned, checksummed binary-DXF text
/// envelope suitable for the shared desktop/browser clipboard seam.
/// </summary>
public static class CadClipboardCodec
{
    public const int DefaultMaximumEntityCount = 65_536;
    public const int DefaultMaximumEncodedByteCount = 64 * 1024 * 1024;

    private const string Prefix = "PROGPU-CAD-CLIPBOARD\t1\t";

    public static string Encode(
        CadDocumentSession session,
        IEnumerable<ulong> sourceHandles,
        CadPoint3D basePoint,
        int maximumEntityCount = DefaultMaximumEntityCount,
        int maximumEncodedByteCount = DefaultMaximumEncodedByteCount)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sourceHandles);
        ValidateBounds(maximumEntityCount, maximumEncodedByteCount);
        ValidateFinite(basePoint, nameof(basePoint));

        ulong[] handles = CollectHandles(sourceHandles, maximumEntityCount);
        return session.Read(document => EncodeCore(
            document,
            handles,
            basePoint,
            maximumEncodedByteCount));
    }

    public static bool TryDecode(
        string? text,
        out CadClipboardPayload? payload,
        out string? errorMessage,
        int maximumEntityCount = DefaultMaximumEntityCount,
        int maximumEncodedByteCount = DefaultMaximumEncodedByteCount)
    {
        payload = null;
        errorMessage = null;
        ValidateBounds(maximumEntityCount, maximumEncodedByteCount);
        if (string.IsNullOrEmpty(text) || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            errorMessage = "The clipboard does not contain ProGPU CAD entities.";
            return false;
        }

        int lineEnd = text.IndexOf('\n');
        if (lineEnd < 0)
        {
            errorMessage = "The CAD clipboard envelope header is incomplete.";
            return false;
        }

        string[] fields = text.AsSpan(0, lineEnd).ToString().Split('\t');
        if (fields.Length != 8 ||
            !TryParseBits(fields[2], out double x) ||
            !TryParseBits(fields[3], out double y) ||
            !TryParseBits(fields[4], out double z) ||
            !int.TryParse(
                fields[5],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int declaredEntityCount) ||
            declaredEntityCount <= 0 ||
            declaredEntityCount > maximumEntityCount ||
            !int.TryParse(
                fields[6],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int declaredByteCount) ||
            declaredByteCount <= 0 ||
            declaredByteCount > maximumEncodedByteCount ||
            fields[7].Length != 64)
        {
            errorMessage = "The CAD clipboard envelope header is invalid or exceeds its bounds.";
            return false;
        }

        var basePoint = new CadPoint3D(x, y, z);
        if (!IsFinite(basePoint))
        {
            errorMessage = "The CAD clipboard base point is not finite.";
            return false;
        }

        ReadOnlySpan<char> encoded = text.AsSpan(lineEnd + 1);
        long maximumBase64Length =
            (((long)maximumEncodedByteCount + 2L) / 3L) * 4L;
        if (encoded.Length == 0 || encoded.Length > maximumBase64Length)
        {
            errorMessage = "The CAD clipboard payload exceeds its encoded-size bound.";
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded.ToString());
        }
        catch (FormatException)
        {
            errorMessage = "The CAD clipboard payload is not valid Base64.";
            return false;
        }
        if (bytes.Length != declaredByteCount)
        {
            errorMessage = "The CAD clipboard payload length does not match its header.";
            return false;
        }

        string checksum = Convert.ToHexString(SHA256.HashData(bytes));
        if (!checksum.Equals(fields[7], StringComparison.Ordinal))
        {
            errorMessage = "The CAD clipboard payload checksum does not match.";
            return false;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new DxfReader(stream)
            {
                Configuration = new DxfReaderConfiguration
                {
                    Failsafe = false,
                    KeepUnknownEntities = true,
                    KeepUnknownNonGraphicalObjects = true,
                    CreateDefaults = true,
                },
            };
            CadDocument document = reader.Read();
            if (document.Entities.Count != declaredEntityCount)
            {
                errorMessage =
                    "The CAD clipboard entity count does not match its header.";
                return false;
            }

            var entities = new Entity[declaredEntityCount];
            int index = 0;
            foreach (Entity entity in document.Entities)
            {
                Entity clone = (Entity)entity.Clone();
                if (clone.Owner is not null || clone.Document is not null || clone.Handle != 0)
                {
                    errorMessage = "A decoded CAD clipboard entity is not detached.";
                    return false;
                }
                entities[index++] = clone;
            }
            payload = new CadClipboardPayload(basePoint, entities, bytes.Length);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or IOException or
                InvalidDataException or InvalidOperationException or
                NotSupportedException or OverflowException)
        {
            errorMessage = $"The CAD clipboard DXF payload is invalid: {exception.Message}";
            return false;
        }
    }

    private static string EncodeCore(
        CadDocument document,
        ReadOnlySpan<ulong> handles,
        CadPoint3D basePoint,
        int maximumEncodedByteCount)
    {
        var scratch = new CadDocument(document.Header.Version);
        var clones = new Entity[handles.Length];
        for (int index = 0; index < handles.Length; index++)
        {
            Entity? source = document.GetCadObject<Entity>(handles[index]);
            if (source is null || !ReferenceEquals(source.Owner, document.ModelSpace))
            {
                throw new InvalidOperationException(
                    $"Model-space entity handle {handles[index]:X} does not exist.");
            }
            Entity clone = (Entity)source.Clone();
            if (clone.Owner is not null || clone.Document is not null || clone.Handle != 0)
            {
                throw new InvalidOperationException(
                    "A CAD clipboard clone is not detached.");
            }
            clones[index] = clone;
        }
        scratch.Entities.AddRange(clones);

        using var stream = new BoundedMemoryStream(maximumEncodedByteCount);
        DxfWriter.Write(
            stream,
            scratch,
            binary: true,
            new DxfWriterConfiguration
            {
                CloseStream = false,
                WriteShapes = true,
            });
        byte[] bytes = stream.ToArray();
        string checksum = Convert.ToHexString(SHA256.HashData(bytes));
        return Prefix + ToBits(basePoint.X) + '\t' + ToBits(basePoint.Y) + '\t' +
            ToBits(basePoint.Z) + '\t' +
            handles.Length.ToString(CultureInfo.InvariantCulture) + '\t' +
            bytes.Length.ToString(CultureInfo.InvariantCulture) + '\t' +
            checksum + '\n' +
            Convert.ToBase64String(bytes);
    }

    private static ulong[] CollectHandles(
        IEnumerable<ulong> sourceHandles,
        int maximumEntityCount)
    {
        var unique = new HashSet<ulong>();
        var handles = new List<ulong>();
        foreach (ulong handle in sourceHandles)
        {
            if (handle == 0)
            {
                throw new ArgumentException(
                    "Every CAD clipboard source handle must be non-zero.",
                    nameof(sourceHandles));
            }
            if (!unique.Add(handle))
            {
                continue;
            }
            if (handles.Count == maximumEntityCount)
            {
                throw new ArgumentException(
                    $"The CAD clipboard source set exceeds {maximumEntityCount} unique entities.",
                    nameof(sourceHandles));
            }
            handles.Add(handle);
        }
        if (handles.Count == 0)
        {
            throw new ArgumentException(
                "At least one CAD clipboard source handle is required.",
                nameof(sourceHandles));
        }
        return handles.ToArray();
    }

    private static void ValidateBounds(
        int maximumEntityCount,
        int maximumEncodedByteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntityCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEncodedByteCount);
    }

    private static void ValidateFinite(CadPoint3D point, string parameterName)
    {
        if (!IsFinite(point))
        {
            throw new ArgumentException(
                "A CAD clipboard point must be finite.",
                parameterName);
        }
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);

    private static string ToBits(double value) =>
        BitConverter.DoubleToUInt64Bits(value).ToString("X16", CultureInfo.InvariantCulture);

    private static bool TryParseBits(string text, out double value)
    {
        if (ulong.TryParse(
                text,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out ulong bits))
        {
            value = BitConverter.UInt64BitsToDouble(bits);
            return true;
        }
        value = default;
        return false;
    }

    private sealed class BoundedMemoryStream : MemoryStream
    {
        private readonly int _maximumLength;

        public BoundedMemoryStream(int maximumLength)
        {
            _maximumLength = maximumLength;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacity(1);
            base.WriteByte(value);
        }

        private void EnsureCapacity(int additionalLength)
        {
            if (additionalLength < 0 || Position > _maximumLength - additionalLength)
            {
                throw new InvalidDataException(
                    $"The CAD clipboard DXF exceeds {_maximumLength} bytes.");
            }
        }
    }
}

/// <summary>
/// Pastes one decoded CAD clipboard payload at an exact WCS insertion point as
/// one reversible, placement-major model-space batch.
/// </summary>
public sealed class CadPasteModelSpaceEntitiesCommand : CadEditCommand
{
    private readonly Entity[] _entities;
    private readonly ulong[] _currentHandles;

    public CadPoint3D InsertionPoint { get; }

    public CadPoint3D Translation { get; }

    public int EntityCount => _entities.Length;

    public ReadOnlyMemory<ulong> CurrentHandles => _currentHandles;

    public CadPasteModelSpaceEntitiesCommand(
        CadClipboardPayload payload,
        CadPoint3D insertionPoint,
        string description = "Paste CAD clipboard entities")
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!double.IsFinite(insertionPoint.X) ||
            !double.IsFinite(insertionPoint.Y) ||
            !double.IsFinite(insertionPoint.Z))
        {
            throw new ArgumentException(
                "The CAD clipboard insertion point must be finite.",
                nameof(insertionPoint));
        }

        InsertionPoint = insertionPoint;
        Translation = insertionPoint - payload.BasePoint;
        var translation = new XYZ(Translation.X, Translation.Y, Translation.Z);
        _entities = new Entity[payload.EntityCount];
        int index = 0;
        foreach (Entity source in payload.Entities)
        {
            Entity clone = (Entity)source.Clone();
            ValidateDetached(clone);
            if (translation != XYZ.Zero)
            {
                ApplyEntityTranslation(clone, translation);
            }
            ValidateDetached(clone);
            _entities[index++] = clone;
        }
        _currentHandles = new ulong[_entities.Length];
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        foreach (Entity entity in _entities)
        {
            ValidateDetached(entity);
        }
        document.Entities.AddRange(_entities);
        for (int index = 0; index < _entities.Length; index++)
        {
            _currentHandles[index] = _entities[index].Handle;
        }
    }

    internal override void Revert(CadDocument document)
    {
        ValidateModelSpaceEntities(document, _entities);
        if (!document.Entities.TryRemoveRange(_entities))
        {
            throw new InvalidOperationException(
                "The pasted CAD clipboard batch removal was canceled before mutation.");
        }
        Array.Clear(_currentHandles);
    }

    private static void ValidateDetached(Entity entity)
    {
        if (entity.Owner is not null || entity.Document is not null || entity.Handle != 0)
        {
            throw new InvalidOperationException(
                "A pasted CAD clipboard entity is not detached.");
        }
    }
}
