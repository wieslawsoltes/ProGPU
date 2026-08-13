using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

/// <summary>
/// Builds one deterministic, pointer-free native semantic scene stream into a
/// caller-owned buffer without managed allocation.
/// </summary>
/// <remarks>
/// Resource and command ids must be nonzero and strictly increasing. The
/// resource table is therefore canonical while command-table order remains
/// the display-list order chosen by the caller. A successful build requires a
/// balanced save/layer stack. The returned bytes must remain immutable while
/// they are passed to <see cref="NativeCompositor.UpdateScene"/>.
/// </remarks>
public ref struct NativeSceneStreamBuilder
{
    private const int HeaderSize = 80;
    private const int CommandSize = 64;
    private const int ResourceSize = 48;

    private Span<byte> _destination;
    private readonly int _commandCapacity;
    private readonly int _resourceCapacity;
    private readonly int _commandOffset;
    private readonly int _resourceOffset;
    private readonly int _arenaOffset;
    private readonly ulong _sceneId;
    private readonly ulong _generation;
    private int _commandCount;
    private int _resourceCount;
    private int _arenaSize;
    private int _stackDepth;
    private ulong _layerStackBits;
    private ulong _lastCommandId;
    private ulong _lastResourceId;
    private bool _built;

    public static int GetRequiredBufferSize(
        int commandCapacity,
        int resourceCapacity,
        int arenaCapacity)
    {
        if (commandCapacity < 0 ||
            (uint)commandCapacity > NativeMethods.SceneMaximumCommands)
        {
            throw new ArgumentOutOfRangeException(nameof(commandCapacity));
        }
        if (resourceCapacity < 0 ||
            (uint)resourceCapacity > NativeMethods.SceneMaximumResources)
        {
            throw new ArgumentOutOfRangeException(nameof(resourceCapacity));
        }
        if (arenaCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(arenaCapacity));
        }
        long resourceOffset = Align8(
            HeaderSize + checked((long)commandCapacity * CommandSize));
        long arenaOffset = Align8(
            resourceOffset + checked((long)resourceCapacity * ResourceSize));
        long total = checked(arenaOffset + arenaCapacity);
        if (total > NativeMethods.SceneMaximumStreamBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(arenaCapacity));
        }
        return checked((int)total);
    }

    public NativeSceneStreamBuilder(
        Span<byte> destination,
        ulong sceneId,
        ulong generation,
        int commandCapacity,
        int resourceCapacity)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException(
                "Native semantic scene streams require a little-endian host.");
        }
        if (sceneId == 0U)
        {
            throw new ArgumentOutOfRangeException(nameof(sceneId));
        }
        if (generation == 0U)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
        if (commandCapacity < 0 ||
            (uint)commandCapacity > NativeMethods.SceneMaximumCommands)
        {
            throw new ArgumentOutOfRangeException(nameof(commandCapacity));
        }
        if (resourceCapacity < 0 ||
            (uint)resourceCapacity > NativeMethods.SceneMaximumResources)
        {
            throw new ArgumentOutOfRangeException(nameof(resourceCapacity));
        }
        if ((uint)destination.Length > NativeMethods.SceneMaximumStreamBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        int minimumSize = GetRequiredBufferSize(
            commandCapacity,
            resourceCapacity,
            arenaCapacity: 0);
        long resourceOffset = Align8(
            HeaderSize + checked((long)commandCapacity * CommandSize));
        long arenaOffset = minimumSize;
        if (arenaOffset > destination.Length)
        {
            throw new ArgumentException(
                "The destination cannot hold the reserved scene tables.",
                nameof(destination));
        }

        _destination = destination;
        _destination.Clear();
        _commandCapacity = commandCapacity;
        _resourceCapacity = resourceCapacity;
        _commandOffset = HeaderSize;
        _resourceOffset = checked((int)resourceOffset);
        _arenaOffset = checked((int)arenaOffset);
        _sceneId = sceneId;
        _generation = generation;
        _commandCount = 0;
        _resourceCount = 0;
        _arenaSize = 0;
        _stackDepth = 0;
        _layerStackBits = 0U;
        _lastCommandId = 0U;
        _lastResourceId = 0U;
        _built = false;
    }

    public readonly int CommandCount => _commandCount;

    public readonly int ResourceCount => _resourceCount;

    public readonly int BytesWritten => _built
        ? _arenaOffset + _arenaSize
        : 0;

    public bool TryAddResource(
        NativeSceneResourceKind kind,
        ulong resourceId,
        ulong generation,
        ReadOnlySpan<byte> payload,
        out uint resourceIndex,
        ReadOnlySpan<byte> auxiliary = default,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (_built || _resourceCount == _resourceCapacity ||
            !IsKnownResource(kind) || resourceId == 0U ||
            resourceId <= _lastResourceId || generation == 0U ||
            payload.IsEmpty ||
            (flags & ~NativeSceneRecordFlags.Required) != 0)
        {
            return false;
        }
        int originalArenaSize = _arenaSize;
        if (!TryWriteArena(payload, out uint payloadOffset) ||
            !TryWriteArena(auxiliary, out uint auxiliaryOffset))
        {
            _arenaSize = originalArenaSize;
            return false;
        }

        var resource = new NativeMethods.SceneResource
        {
            StructSize = ResourceSize,
            Kind = kind,
            Flags = flags,
            ResourceId = resourceId,
            Generation = generation,
            PayloadOffset = payloadOffset,
            PayloadSize = (uint)payload.Length,
            AuxiliaryOffset = auxiliaryOffset,
            AuxiliarySize = (uint)auxiliary.Length
        };
        Write(
            _resourceOffset + _resourceCount * ResourceSize,
            resource);
        resourceIndex = (uint)_resourceCount++;
        _lastResourceId = resourceId;
        return true;
    }

    public bool TryAddAnalyticResource(
        ulong resourceId,
        ulong generation,
        ReadOnlySpan<NativeAnalyticPrimitive> primitives,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required) =>
        TryAddResource(
            NativeSceneResourceKind.AnalyticBatch,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(primitives),
            out resourceIndex,
            flags: flags);

    public bool TryAddPathResource(
        ulong resourceId,
        ulong generation,
        ReadOnlySpan<NativeScenePathFill> paths,
        ReadOnlySpan<NativePathSegment> segments,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required) =>
        TryAddResource(
            NativeSceneResourceKind.PathBatch,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(paths),
            out resourceIndex,
            MemoryMarshal.AsBytes(segments),
            flags);

    public bool TryAddGlyphResource(
        ulong resourceId,
        ulong generation,
        ReadOnlySpan<NativeSceneGlyphOutline> outlines,
        ReadOnlySpan<NativePathSegment> segments,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required) =>
        TryAddResource(
            NativeSceneResourceKind.GlyphRun,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(outlines),
            out resourceIndex,
            MemoryMarshal.AsBytes(segments),
            flags);

    public bool TryAddImageResource(
        ulong resourceId,
        ulong generation,
        ReadOnlySpan<byte> rgbaPixels,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required) =>
        TryAddResource(
            NativeSceneResourceKind.Image,
            resourceId,
            generation,
            rgbaPixels,
            out resourceIndex,
            flags: flags);

    public bool TryAddStateResource(
        ulong resourceId,
        ulong generation,
        in NativeSceneState state,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required) =>
        TryAddResource(
            NativeSceneResourceKind.State,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in state),
                    1)),
            out resourceIndex,
            flags: flags);

    public bool TrySave(
        ulong commandId,
        uint stateIndex = NativeMethods.SceneNoIndex) =>
        TryPushControl(
            NativeSceneCommandKind.Save,
            commandId,
            isLayer: false,
            stateIndex: stateIndex);

    public bool TryRestore(ulong commandId) =>
        TryPopControl(NativeSceneCommandKind.Restore, commandId, isLayer: false);

    public bool TryPushLayer(
        ulong commandId,
        uint stateIndex = NativeMethods.SceneNoIndex) =>
        TryPushControl(
            NativeSceneCommandKind.PushLayer,
            commandId,
            isLayer: true,
            stateIndex: stateIndex);

    public bool TryPopLayer(ulong commandId) =>
        TryPopControl(NativeSceneCommandKind.PopLayer, commandId, isLayer: true);

    public bool TryDrawAnalytic(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        ReadOnlySpan<byte> payload = default,
        uint stateIndex = uint.MaxValue) =>
        TryDraw(
            NativeSceneCommandKind.DrawAnalytic,
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);

    public bool TryDrawPath(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        ReadOnlySpan<byte> payload = default,
        uint stateIndex = uint.MaxValue) =>
        TryDraw(
            NativeSceneCommandKind.DrawPath,
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);

    public bool TryDrawGlyphRun(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        ReadOnlySpan<byte> payload = default,
        uint stateIndex = uint.MaxValue) =>
        TryDraw(
            NativeSceneCommandKind.DrawGlyphRun,
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);

    public bool TryDrawGlyphRun(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        ReadOnlySpan<NativePositionedGlyph> glyphs,
        uint stateIndex = uint.MaxValue) =>
        TryDrawGlyphRun(
            commandId,
            resourceIndex,
            bounds,
            MemoryMarshal.AsBytes(glyphs),
            stateIndex);

    public bool TryDrawImage(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        ReadOnlySpan<byte> payload = default,
        uint stateIndex = uint.MaxValue) =>
        TryDraw(
            NativeSceneCommandKind.DrawImage,
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);

    public bool TryDrawImage(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        in NativeSceneImageDraw image,
        uint stateIndex = uint.MaxValue) =>
        TryDrawImage(
            commandId,
            resourceIndex,
            bounds,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in image),
                    1)),
            stateIndex);

    public bool TryBuild(out ReadOnlySpan<byte> stream)
    {
        stream = default;
        if (_built || _stackDepth != 0)
        {
            return false;
        }
        int totalSize = _arenaOffset + _arenaSize;
        var header = new NativeMethods.SceneHeader
        {
            StructSize = HeaderSize,
            Magic = NativeMethods.SceneStreamMagic,
            StreamVersion = NativeMethods.SceneStreamVersion,
            EndianMarker = NativeMethods.SceneStreamEndianMarker,
            TotalSize = (uint)totalSize,
            SceneId = _sceneId,
            Generation = _generation,
            CommandOffset = (uint)_commandOffset,
            CommandCount = (uint)_commandCount,
            CommandStride = CommandSize,
            ResourceOffset = (uint)_resourceOffset,
            ResourceCount = (uint)_resourceCount,
            ResourceStride = ResourceSize,
            ArenaOffset = (uint)_arenaOffset,
            ArenaSize = (uint)_arenaSize
        };
        Write(0, header);
        _built = true;
        stream = _destination[..totalSize];
        return true;
    }

    private bool TryDraw(
        NativeSceneCommandKind kind,
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        ReadOnlySpan<byte> payload,
        uint stateIndex)
    {
        if (_built || _commandCount == _commandCapacity ||
            commandId == 0U || commandId <= _lastCommandId ||
            resourceIndex >= (uint)_resourceCount ||
            !ResourceHasKind(
                resourceIndex,
                ExpectedResourceKind(kind)) ||
            (stateIndex != NativeMethods.SceneNoIndex &&
                (stateIndex >= (uint)_resourceCount ||
                    !ResourceHasKind(
                        stateIndex,
                        NativeSceneResourceKind.State))) ||
            !IsFiniteBounds(bounds))
        {
            return false;
        }
        int originalArenaSize = _arenaSize;
        if (!TryWriteArena(payload, out uint payloadOffset))
        {
            _arenaSize = originalArenaSize;
            return false;
        }
        var command = new NativeMethods.SceneCommand
        {
            StructSize = CommandSize,
            Kind = kind,
            Flags = NativeSceneRecordFlags.Required,
            CommandId = commandId,
            StateIndex = stateIndex,
            ResourceIndex = resourceIndex,
            PayloadOffset = payloadOffset,
            PayloadSize = (uint)payload.Length,
            Bounds = bounds
        };
        Write(_commandOffset + _commandCount++ * CommandSize, command);
        _lastCommandId = commandId;
        return true;
    }

    private bool TryPushControl(
        NativeSceneCommandKind kind,
        ulong commandId,
        bool isLayer,
        uint stateIndex)
    {
        if ((uint)_stackDepth == NativeMethods.SceneMaximumStackDepth ||
            (stateIndex != NativeMethods.SceneNoIndex &&
                (stateIndex >= (uint)_resourceCount ||
                    !ResourceHasKind(
                        stateIndex,
                        NativeSceneResourceKind.State))) ||
            !TryWriteControl(kind, commandId, stateIndex))
        {
            return false;
        }
        if (isLayer)
        {
            _layerStackBits |= 1UL << _stackDepth;
        }
        ++_stackDepth;
        return true;
    }

    private bool TryPopControl(
        NativeSceneCommandKind kind,
        ulong commandId,
        bool isLayer)
    {
        if (_stackDepth == 0)
        {
            return false;
        }
        bool topIsLayer =
            (_layerStackBits & (1UL << (_stackDepth - 1))) != 0U;
        if (topIsLayer != isLayer ||
            !TryWriteControl(
                kind,
                commandId,
                NativeMethods.SceneNoIndex))
        {
            return false;
        }
        --_stackDepth;
        _layerStackBits &= ~(1UL << _stackDepth);
        return true;
    }

    private bool TryWriteControl(
        NativeSceneCommandKind kind,
        ulong commandId,
        uint stateIndex)
    {
        if (_built || _commandCount == _commandCapacity ||
            commandId == 0U || commandId <= _lastCommandId)
        {
            return false;
        }
        var command = new NativeMethods.SceneCommand
        {
            StructSize = CommandSize,
            Kind = kind,
            Flags = NativeSceneRecordFlags.Required,
            CommandId = commandId,
            StateIndex = stateIndex,
            ResourceIndex = NativeMethods.SceneNoIndex
        };
        Write(_commandOffset + _commandCount++ * CommandSize, command);
        _lastCommandId = commandId;
        return true;
    }

    private bool TryWriteArena(
        ReadOnlySpan<byte> source,
        out uint absoluteOffset)
    {
        absoluteOffset = 0U;
        if (source.IsEmpty)
        {
            return true;
        }
        int relativeOffset = checked((int)Align8(_arenaSize));
        int end = checked(relativeOffset + source.Length);
        if (_arenaOffset + end > _destination.Length)
        {
            return false;
        }
        source.CopyTo(
            _destination.Slice(_arenaOffset + relativeOffset, source.Length));
        _arenaSize = end;
        absoluteOffset = (uint)(_arenaOffset + relativeOffset);
        return true;
    }

    private void Write<T>(int offset, in T value)
        where T : unmanaged
    {
        Span<byte> destination = _destination.Slice(
            offset,
            Unsafe.SizeOf<T>());
        MemoryMarshal.Write(destination, in value);
    }

    private readonly bool ResourceHasKind(
        uint resourceIndex,
        NativeSceneResourceKind kind)
    {
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            _destination.Slice(
                _resourceOffset + checked((int)resourceIndex) * ResourceSize,
                ResourceSize));
        return resource.Kind == kind;
    }

    private static NativeSceneResourceKind ExpectedResourceKind(
        NativeSceneCommandKind kind) => kind switch
        {
            NativeSceneCommandKind.DrawAnalytic =>
                NativeSceneResourceKind.AnalyticBatch,
            NativeSceneCommandKind.DrawPath =>
                NativeSceneResourceKind.PathBatch,
            NativeSceneCommandKind.DrawGlyphRun =>
                NativeSceneResourceKind.GlyphRun,
            NativeSceneCommandKind.DrawImage =>
                NativeSceneResourceKind.Image,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static bool IsKnownResource(NativeSceneResourceKind kind) =>
        kind is >= NativeSceneResourceKind.AnalyticBatch and
            <= NativeSceneResourceKind.State;

    private static bool IsFiniteBounds(NativeImageRect bounds) =>
        float.IsFinite(bounds.X) && float.IsFinite(bounds.Y) &&
        float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height) &&
        bounds.Width >= 0f && bounds.Height >= 0f;

    private static long Align8(long value) => (value + 7L) & ~7L;
}
