using System.Numerics;
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
    private int _materializedLayerDepth;
    private ulong _materializedLayerStackBits;
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
        _materializedLayerDepth = 0;
        _materializedLayerStackBits = 0U;
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
        scoped ReadOnlySpan<byte> payload,
        out uint resourceIndex,
        scoped ReadOnlySpan<byte> auxiliary = default,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (_built || _resourceCount == _resourceCapacity ||
            !IsKnownResource(kind) || resourceId == 0U ||
            resourceId <= _lastResourceId || generation == 0U ||
            payload.IsEmpty ||
            (flags & ~(NativeSceneRecordFlags.Required |
                (kind == NativeSceneResourceKind.GlyphRun
                    ? NativeSceneRecordFlags.ColorGlyphBitmaps
                    : NativeSceneRecordFlags.None))) != 0)
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

    /// <summary>
    /// Adds decoded color-glyph bitmaps. The metadata payload is pointer-free
    /// and the auxiliary payload owns straight-alpha RGBA8 rows. Font parsing,
    /// shaping, SVG handling, and compressed bitmap decoding remain managed.
    /// </summary>
    public bool TryAddColorGlyphResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeSceneColorGlyphBitmap> bitmaps,
        scoped ReadOnlySpan<byte> rgbaPixels,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (bitmaps.IsEmpty || rgbaPixels.IsEmpty ||
            (flags & ~NativeSceneRecordFlags.Required) != 0)
        {
            return false;
        }
        foreach (ref readonly NativeSceneColorGlyphBitmap bitmap in bitmaps)
        {
            ulong minimumRowBytes = (ulong)bitmap.Width * 4UL;
            ulong requiredBytes = bitmap.Height == 0U
                ? 0UL
                : (ulong)bitmap.RowBytes * (bitmap.Height - 1UL) +
                    minimumRowBytes;
            if (!bitmap.HasCanonicalReservedFields || bitmap.Width == 0U ||
                bitmap.Height == 0U || bitmap.Width > 16_384U ||
                bitmap.Height > 16_384U ||
                bitmap.RowBytes < minimumRowBytes ||
                bitmap.PixelOffset > (ulong)rgbaPixels.Length ||
                requiredBytes > (ulong)rgbaPixels.Length - bitmap.PixelOffset ||
                !float.IsFinite(bitmap.BearX) ||
                !float.IsFinite(bitmap.BearY) ||
                !float.IsFinite(bitmap.RenderWidth) ||
                !float.IsFinite(bitmap.RenderHeight) ||
                bitmap.RenderWidth < 0f || bitmap.RenderHeight < 0f)
            {
                return false;
            }
        }
        return TryAddResource(
            NativeSceneResourceKind.GlyphRun,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(bitmaps),
            out resourceIndex,
            rgbaPixels,
            flags | NativeSceneRecordFlags.ColorGlyphBitmaps);
    }

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

    public bool TryAddLayerMaskResource(
        ulong resourceId,
        ulong generation,
        in NativeSceneLayerMask mask,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (!IsValidLayerMask(mask))
        {
            return false;
        }

        return TryAddResource(
            NativeSceneResourceKind.LayerMask,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in mask),
                    1)),
            out resourceIndex,
            flags: flags);
    }

    public bool TryAddEffectChainResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeSceneEffect> effects,
        uint revision,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (effects.IsEmpty ||
            effects.Length > NativeGroupEffectChain.MaximumEffectCount ||
            revision == 0U)
        {
            return false;
        }
        foreach (ref readonly NativeSceneEffect effect in effects)
        {
            if (!IsValidEffect(effect))
            {
                return false;
            }
        }

        var chain = new NativeSceneEffectChain(
            (uint)effects.Length,
            revision);
        return TryAddResource(
            NativeSceneResourceKind.EffectChain,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref chain, 1)),
            out resourceIndex,
            MemoryMarshal.AsBytes(effects),
            flags);
    }

    /// <summary>
    /// Adds one retained material table shared by analytic and path commands.
    /// </summary>
    public bool TryAddBrushTableResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeSceneBrush> brushes,
        scoped ReadOnlySpan<NativeSceneGradientStop> gradientStops,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (brushes.IsEmpty ||
            (uint)brushes.Length > NativeMethods.SceneMaximumBrushes ||
            (uint)gradientStops.Length >
                NativeMethods.SceneMaximumGradientStops ||
            !IsValidBrushTable(brushes, gradientStops))
        {
            return false;
        }

        return TryAddResource(
            NativeSceneResourceKind.BrushTable,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(brushes),
            out resourceIndex,
            MemoryMarshal.AsBytes(gradientStops),
            flags);
    }

    /// <summary>
    /// Adds retained solid text presentation styles consumed by positioned
    /// glyph commands. Shaping and glyph outlines remain caller-owned.
    /// </summary>
    public bool TryAddTextStyleResource(
        ulong resourceId,
        ulong generation,
        scoped ReadOnlySpan<NativeSceneTextStyle> styles,
        out uint resourceIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        resourceIndex = NativeMethods.SceneNoIndex;
        if (styles.IsEmpty ||
            (uint)styles.Length > NativeMethods.SceneMaximumTextStyles)
        {
            return false;
        }
        foreach (ref readonly NativeSceneTextStyle style in styles)
        {
            if (!style.HasCanonicalReservedFields ||
                !IsFinite(style.Color) ||
                style.TextRenderingMode >
                    NativeSceneTextRenderingMode.ClearType)
            {
                return false;
            }
        }
        return TryAddResource(
            NativeSceneResourceKind.TextStyleTable,
            resourceId,
            generation,
            MemoryMarshal.AsBytes(styles),
            out resourceIndex,
            flags: flags);
    }

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
            stateIndex: stateIndex,
            materializedLayer: false);

    public bool TryPushLayer(
        ulong commandId,
        in NativeSceneLayer layer,
        uint stateIndex = NativeMethods.SceneNoIndex)
    {
        if (!IsValidLayer(layer) ||
            !HasOptionalResourceKind(
                layer.MaskResourceIndex,
                NativeSceneResourceKind.LayerMask) ||
            !HasOptionalResourceKind(
                layer.EffectResourceIndex,
                NativeSceneResourceKind.EffectChain))
        {
            return false;
        }

        return TryPushControl(
            NativeSceneCommandKind.PushLayer,
            commandId,
            isLayer: true,
            stateIndex: stateIndex,
            materializedLayer: RequiresMaterialization(layer),
            payload: MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in layer),
                    1)));
    }

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

    /// <summary>
    /// Draws an analytic batch with one brush-table index per primitive.
    /// </summary>
    public bool TryDrawAnalytic(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        uint brushResourceIndex,
        scoped ReadOnlySpan<uint> brushIndices,
        uint stateIndex = uint.MaxValue) =>
        TryDrawWithBrushes(
            NativeSceneCommandKind.DrawAnalytic,
            commandId,
            resourceIndex,
            bounds,
            brushResourceIndex,
            brushIndices,
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

    /// <summary>
    /// Draws a path batch with one brush-table index per path record.
    /// </summary>
    public bool TryDrawPath(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        uint brushResourceIndex,
        scoped ReadOnlySpan<uint> brushIndices,
        uint stateIndex = uint.MaxValue) =>
        TryDrawWithBrushes(
            NativeSceneCommandKind.DrawPath,
            commandId,
            resourceIndex,
            bounds,
            brushResourceIndex,
            brushIndices,
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

    /// <summary>
    /// Draws one positioned glyph run through a retained solid text style.
    /// The builder writes one compact style reference followed by the shaped
    /// glyph records without allocating or copying through an intermediate
    /// object graph.
    /// </summary>
    public bool TryDrawGlyphRun(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        scoped ReadOnlySpan<NativePositionedGlyph> glyphs,
        uint styleResourceIndex,
        uint styleIndex,
        uint stateIndex = uint.MaxValue)
    {
        if (_built || _commandCount == _commandCapacity || glyphs.IsEmpty ||
            styleResourceIndex >= (uint)_resourceCount ||
            !ResourceHasKind(
                styleResourceIndex,
                NativeSceneResourceKind.TextStyleTable) ||
            styleIndex >= GetResourceRecordCount<NativeSceneTextStyle>(
                styleResourceIndex))
        {
            return false;
        }

        int originalArenaSize = _arenaSize;
        int relativeOffset = checked((int)Align8(_arenaSize));
        int glyphBytes = checked(
            glyphs.Length * Unsafe.SizeOf<NativePositionedGlyph>());
        int payloadSize = checked(
            Unsafe.SizeOf<NativeSceneGlyphDraw>() + glyphBytes);
        int end = checked(relativeOffset + payloadSize);
        if (_arenaOffset + end > _destination.Length)
        {
            return false;
        }
        uint payloadOffset = (uint)(_arenaOffset + relativeOffset);
        var header = new NativeSceneGlyphDraw(
            styleResourceIndex,
            styleIndex,
            checked((uint)glyphs.Length));
        Write((int)payloadOffset, header);
        MemoryMarshal.AsBytes(glyphs).CopyTo(
            _destination.Slice(
                (int)payloadOffset + Unsafe.SizeOf<NativeSceneGlyphDraw>(),
                glyphBytes));
        _arenaSize = end;

        if (!TryWriteDrawCommand(
                NativeSceneCommandKind.DrawGlyphRun,
                commandId,
                resourceIndex,
                bounds,
                payloadOffset,
                checked((uint)payloadSize),
                stateIndex,
                NativeSceneRecordFlags.Required |
                    NativeSceneRecordFlags.StyledGlyphs))
        {
            _arenaSize = originalArenaSize;
            return false;
        }
        return true;
    }

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
        scoped ReadOnlySpan<byte> payload = default,
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
        uint stateIndex = uint.MaxValue)
    {
        if (image.Sampling == NativeImageSampling.Cubic ||
            image.Flags != NativeSceneImageFlags.None)
        {
            return false;
        }
        return TryDrawImage(
            commandId,
            resourceIndex,
            bounds,
            MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(
                    ref Unsafe.AsRef(in image),
                    1)),
            stateIndex);
    }

    public bool TryDrawImage(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        in NativeSceneImageDraw image,
        in NativeSceneImageSamplingOptions samplingOptions,
        uint stateIndex = uint.MaxValue)
    {
        if (image.Sampling != NativeImageSampling.Cubic ||
            image.Flags != NativeSceneImageFlags.None ||
            !samplingOptions.HasCanonicalFields)
        {
            return false;
        }
        Span<byte> payload = stackalloc byte[
            Unsafe.SizeOf<NativeSceneImageDraw>() +
            Unsafe.SizeOf<NativeSceneImageSamplingOptions>()];
        MemoryMarshal.Write(payload, in image);
        MemoryMarshal.Write(
            payload[Unsafe.SizeOf<NativeSceneImageDraw>()..],
            in samplingOptions);
        return TryDrawImage(
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);
    }

    public bool TryDrawImage(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        in NativeSceneImageDraw image,
        in NativeSceneImageColorMatrix colorMatrix,
        uint stateIndex = uint.MaxValue)
    {
        if (image.Sampling == NativeImageSampling.Cubic ||
            image.Flags != NativeSceneImageFlags.ColorMatrix ||
            !colorMatrix.HasCanonicalFields)
        {
            return false;
        }
        Span<byte> payload = stackalloc byte[
            Unsafe.SizeOf<NativeSceneImageDraw>() +
            Unsafe.SizeOf<NativeSceneImageColorMatrix>()];
        MemoryMarshal.Write(payload, in image);
        MemoryMarshal.Write(
            payload[Unsafe.SizeOf<NativeSceneImageDraw>()..],
            in colorMatrix);
        return TryDrawImage(
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);
    }

    public bool TryDrawImage(
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        in NativeSceneImageDraw image,
        in NativeSceneImageSamplingOptions samplingOptions,
        in NativeSceneImageColorMatrix colorMatrix,
        uint stateIndex = uint.MaxValue)
    {
        if (image.Sampling != NativeImageSampling.Cubic ||
            image.Flags != NativeSceneImageFlags.ColorMatrix ||
            !samplingOptions.HasCanonicalFields ||
            !colorMatrix.HasCanonicalFields)
        {
            return false;
        }
        Span<byte> payload = stackalloc byte[
            Unsafe.SizeOf<NativeSceneImageDraw>() +
            Unsafe.SizeOf<NativeSceneImageSamplingOptions>() +
            Unsafe.SizeOf<NativeSceneImageColorMatrix>()];
        int offset = 0;
        MemoryMarshal.Write(payload, in image);
        offset += Unsafe.SizeOf<NativeSceneImageDraw>();
        MemoryMarshal.Write(payload[offset..], in samplingOptions);
        offset += Unsafe.SizeOf<NativeSceneImageSamplingOptions>();
        MemoryMarshal.Write(payload[offset..], in colorMatrix);
        return TryDrawImage(
            commandId,
            resourceIndex,
            bounds,
            payload,
            stateIndex);
    }

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
        scoped ReadOnlySpan<byte> payload,
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

    private bool TryDrawWithBrushes(
        NativeSceneCommandKind kind,
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        uint brushResourceIndex,
        scoped ReadOnlySpan<uint> brushIndices,
        uint stateIndex)
    {
        if (_built || _commandCount == _commandCapacity ||
            brushIndices.IsEmpty ||
            (uint)brushIndices.Length >
                NativeMethods.SceneMaximumDrawBrushIndices ||
            brushResourceIndex >= (uint)_resourceCount ||
            !ResourceHasKind(
                brushResourceIndex,
                NativeSceneResourceKind.BrushTable) ||
            resourceIndex >= (uint)_resourceCount ||
            !ResourceHasKind(resourceIndex, ExpectedResourceKind(kind)) ||
            brushIndices.Length != GetDrawRecordCount(kind, resourceIndex))
        {
            return false;
        }

        uint brushCount = GetResourceRecordCount<NativeSceneBrush>(
            brushResourceIndex);
        foreach (uint brushIndex in brushIndices)
        {
            if (brushIndex >= brushCount)
            {
                return false;
            }
        }

        int originalArenaSize = _arenaSize;
        int relativeOffset = checked((int)Align8(_arenaSize));
        int payloadSize = checked(
            Unsafe.SizeOf<NativeSceneDrawBrushes>() +
            brushIndices.Length * sizeof(uint));
        int end = checked(relativeOffset + payloadSize);
        if (_arenaOffset + end > _destination.Length)
        {
            return false;
        }
        uint payloadOffset = (uint)(_arenaOffset + relativeOffset);
        var header = new NativeSceneDrawBrushes(
            brushResourceIndex,
            checked((uint)brushIndices.Length));
        Write((int)payloadOffset, header);
        MemoryMarshal.AsBytes(brushIndices).CopyTo(
            _destination.Slice(
                (int)payloadOffset + Unsafe.SizeOf<NativeSceneDrawBrushes>(),
                brushIndices.Length * sizeof(uint)));
        _arenaSize = end;

        if (!TryWriteDrawCommand(
                kind,
                commandId,
                resourceIndex,
                bounds,
                payloadOffset,
                checked((uint)payloadSize),
                stateIndex))
        {
            _arenaSize = originalArenaSize;
            return false;
        }
        return true;
    }

    private bool TryWriteDrawCommand(
        NativeSceneCommandKind kind,
        ulong commandId,
        uint resourceIndex,
        NativeImageRect bounds,
        uint payloadOffset,
        uint payloadSize,
        uint stateIndex,
        NativeSceneRecordFlags flags = NativeSceneRecordFlags.Required)
    {
        if (_built || _commandCount == _commandCapacity ||
            commandId == 0U || commandId <= _lastCommandId ||
            resourceIndex >= (uint)_resourceCount ||
            !ResourceHasKind(resourceIndex, ExpectedResourceKind(kind)) ||
            (stateIndex != NativeMethods.SceneNoIndex &&
                (stateIndex >= (uint)_resourceCount ||
                    !ResourceHasKind(
                        stateIndex,
                        NativeSceneResourceKind.State))) ||
            !IsFiniteBounds(bounds))
        {
            return false;
        }
        var command = new NativeMethods.SceneCommand
        {
            StructSize = CommandSize,
            Kind = kind,
            Flags = flags,
            CommandId = commandId,
            StateIndex = stateIndex,
            ResourceIndex = resourceIndex,
            PayloadOffset = payloadOffset,
            PayloadSize = payloadSize,
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
        uint stateIndex,
        bool materializedLayer = false,
        ReadOnlySpan<byte> payload = default)
    {
        int originalArenaSize = _arenaSize;
        if ((uint)_stackDepth == NativeMethods.SceneMaximumStackDepth ||
            (materializedLayer &&
                (uint)_materializedLayerDepth ==
                    NativeMethods.SceneMaximumMaterializedLayers) ||
            (stateIndex != NativeMethods.SceneNoIndex &&
                (stateIndex >= (uint)_resourceCount ||
                    !ResourceHasKind(
                        stateIndex,
                        NativeSceneResourceKind.State))))
        {
            return false;
        }
        if (!TryWriteArena(payload, out uint payloadOffset) ||
            !TryWriteControl(
                kind,
                commandId,
                stateIndex,
                payloadOffset,
                (uint)payload.Length))
        {
            _arenaSize = originalArenaSize;
            return false;
        }
        if (isLayer)
        {
            _layerStackBits |= 1UL << _stackDepth;
            if (materializedLayer)
            {
                _materializedLayerStackBits |= 1UL << _stackDepth;
                ++_materializedLayerDepth;
            }
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
        bool topIsMaterializedLayer =
            (_materializedLayerStackBits &
                (1UL << (_stackDepth - 1))) != 0U;
        --_stackDepth;
        _materializedLayerDepth -= topIsMaterializedLayer ? 1 : 0;
        _layerStackBits &= ~(1UL << _stackDepth);
        _materializedLayerStackBits &= ~(1UL << _stackDepth);
        return true;
    }

    private bool TryWriteControl(
        NativeSceneCommandKind kind,
        ulong commandId,
        uint stateIndex,
        uint payloadOffset = 0U,
        uint payloadSize = 0U)
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
            ResourceIndex = NativeMethods.SceneNoIndex,
            PayloadOffset = payloadOffset,
            PayloadSize = payloadSize
        };
        Write(_commandOffset + _commandCount++ * CommandSize, command);
        _lastCommandId = commandId;
        return true;
    }

    private bool TryWriteArena(
        scoped ReadOnlySpan<byte> source,
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

    private readonly uint GetResourceRecordCount<T>(uint resourceIndex)
        where T : unmanaged
    {
        var resource = MemoryMarshal.Read<NativeMethods.SceneResource>(
            _destination.Slice(
                _resourceOffset + checked((int)resourceIndex) * ResourceSize,
                ResourceSize));
        return resource.PayloadSize / checked((uint)Unsafe.SizeOf<T>());
    }

    private readonly int GetDrawRecordCount(
        NativeSceneCommandKind kind,
        uint resourceIndex) => kind switch
        {
            NativeSceneCommandKind.DrawAnalytic => checked((int)
                GetResourceRecordCount<NativeAnalyticPrimitive>(
                    resourceIndex)),
            NativeSceneCommandKind.DrawPath => checked((int)
                GetResourceRecordCount<NativeScenePathFill>(
                    resourceIndex)),
            _ => 0
        };

    private readonly bool HasOptionalResourceKind(
        uint resourceIndex,
        NativeSceneResourceKind kind) =>
        resourceIndex == NativeMethods.SceneNoIndex ||
        (resourceIndex < (uint)_resourceCount &&
            ResourceHasKind(resourceIndex, kind));

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
            <= NativeSceneResourceKind.TextStyleTable;

    private static bool IsValidBrushTable(
        ReadOnlySpan<NativeSceneBrush> brushes,
        ReadOnlySpan<NativeSceneGradientStop> gradientStops)
    {
        foreach (ref readonly NativeSceneGradientStop stop in gradientStops)
        {
            if (!stop.HasCanonicalReservedFields ||
                !IsFinite(stop.Color) || !float.IsFinite(stop.Offset))
            {
                return false;
            }
        }

        foreach (ref readonly NativeSceneBrush brush in brushes)
        {
            uint spread = (uint)brush.Spread;
            bool conical = brush.Kind ==
                NativeSceneBrushKind.TwoPointConicalGradient;
            bool perlin = brush.Kind == NativeSceneBrushKind.PerlinNoise;
            bool supported = brush.Kind is
                NativeSceneBrushKind.Solid or
                NativeSceneBrushKind.LinearGradient or
                NativeSceneBrushKind.RadialGradient or
                NativeSceneBrushKind.TwoPointConicalGradient or
                NativeSceneBrushKind.SweepGradient or
                NativeSceneBrushKind.PerlinNoise;
            bool gradient = brush.Kind is
                NativeSceneBrushKind.LinearGradient or
                NativeSceneBrushKind.RadialGradient or
                NativeSceneBrushKind.TwoPointConicalGradient or
                NativeSceneBrushKind.SweepGradient;
            if (!supported || !brush.HasCanonicalReservedFields ||
                !float.IsFinite(brush.Opacity) ||
                brush.Opacity is < 0f or > 1f ||
                !IsFinite(brush.StartPoint) ||
                !IsFinite(brush.EndPoint) ||
                !IsFinite(brush.Center) ||
                !float.IsFinite(brush.Radius) ||
                !float.IsFinite(brush.RadiusY) ||
                !IsFinite(brush.Color0) || !IsFinite(brush.Color1) ||
                !IsFinite(brush.Color2) || !IsFinite(brush.Color3) ||
                !IsFinite(brush.Color4) || !IsFinite(brush.Color5) ||
                !IsFinite(brush.Color6) || !IsFinite(brush.Color7) ||
                !IsFinite(brush.Offsets0) || !IsFinite(brush.Offsets1) ||
                !IsFinite(brush.CoordinateTransform0) ||
                !IsFinite(brush.CoordinateTransform1) ||
                brush.CoordinateTransform0.W != 0f ||
                brush.CoordinateTransform1.W != 0f ||
                (uint)brush.Interpolation >
                    (uint)NativeSceneGradientInterpolation.ScRgb ||
                (spread & 0x7FFFFFFFU) >
                    (uint)NativeSceneGradientSpread.Decal ||
                ((spread & 0x80000000U) != 0U && !conical))
            {
                return false;
            }
            if (perlin)
            {
                uint tableRecordCount = brush.StopCount == 0U ||
                    brush.Interpolation ==
                        NativeSceneGradientInterpolation.SRgb
                    ? 0U
                    : NativeSceneBrush.PerlinTableRecordCount;
                if (brush.StopCount > NativeSceneBrush.MaximumPerlinOctaves ||
                    spread > 1U ||
                    (tableRecordCount == 0U && brush.StopOffset != 0U) ||
                    brush.StopOffset > (uint)gradientStops.Length ||
                    tableRecordCount >
                        (uint)gradientStops.Length - brush.StopOffset)
                {
                    return false;
                }
                continue;
            }
            if (!gradient)
            {
                if (brush.StopCount != 0U || brush.StopOffset != 0U ||
                    spread != 0U || brush.Interpolation !=
                        NativeSceneGradientInterpolation.SRgb)
                {
                    return false;
                }
                continue;
            }
            if (brush.StopCount == 0U ||
                brush.StopOffset > (uint)gradientStops.Length ||
                brush.StopCount >
                    (uint)gradientStops.Length - brush.StopOffset)
            {
                return false;
            }
            float previous = float.NegativeInfinity;
            for (uint index = 0U; index < brush.StopCount; index++)
            {
                float offset = gradientStops[
                    checked((int)(brush.StopOffset + index))].Offset;
                if (offset < previous)
                {
                    return false;
                }
                previous = offset;
            }
        }
        return true;
    }

    private static bool IsFiniteBounds(NativeImageRect bounds) =>
        float.IsFinite(bounds.X) && float.IsFinite(bounds.Y) &&
        float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height) &&
        bounds.Width >= 0f && bounds.Height >= 0f;

    private static bool IsValidLayer(in NativeSceneLayer layer)
    {
        const NativeSceneLayerFlags knownFlags =
            NativeSceneLayerFlags.Bounds |
            NativeSceneLayerFlags.Backdrop |
            NativeSceneLayerFlags.ForceIsolation;
        bool hasBounds =
            (layer.Flags & NativeSceneLayerFlags.Bounds) != 0;
        bool canonicalBounds = hasBounds ||
            (layer.Bounds.X == 0f && layer.Bounds.Y == 0f &&
                layer.Bounds.Width == 0f && layer.Bounds.Height == 0f);
        return layer.StructSize == Unsafe.SizeOf<NativeSceneLayer>() &&
            (layer.Flags & ~knownFlags) == 0 &&
            IsFiniteBounds(layer.Bounds) && canonicalBounds &&
            float.IsFinite(layer.Opacity) &&
            layer.Opacity is >= 0f and <= 1f &&
            (uint)layer.BlendMode <= (uint)GpuBlendMode.Modulate &&
            layer.HasCanonicalReservedFields;
    }

    private static bool IsValidLayerMask(in NativeSceneLayerMask mask)
    {
        bool finiteRadii =
            IsFiniteNonnegative(mask.CornerRadiiX) &&
            IsFiniteNonnegative(mask.CornerRadiiY);
        float determinant = mask.Transform.GetDeterminant();
        bool inverseIsRepresentable = Matrix3x2.Invert(
            mask.Transform,
            out Matrix3x2 inverse) && IsFinite(inverse);
        return mask.StructSize == Unsafe.SizeOf<NativeSceneLayerMask>() &&
            mask.Kind == NativeSceneLayerMaskKind.RoundedRectangle &&
            mask.Flags == 0U && mask.HasCanonicalReservedFields &&
            IsFinitePositive(mask.Bounds) &&
            IsFinite(mask.Transform) && float.IsFinite(determinant) &&
            MathF.Abs(determinant) > 0.000001f && inverseIsRepresentable &&
            finiteRadii &&
            float.IsFinite(mask.Opacity) &&
            mask.Opacity is >= 0f and <= 1f;
    }

    private static bool IsValidEffect(in NativeSceneEffect effect)
    {
        if (effect.StructSize != Unsafe.SizeOf<NativeSceneEffect>() ||
            effect.Kind is not (NativeGroupEffectKind.GaussianBlur or
                NativeGroupEffectKind.DropShadow) ||
            effect.Flags != 0U || effect.Revision == 0U ||
            !effect.HasCanonicalReservedFields ||
            !float.IsFinite(effect.SigmaX) ||
            !float.IsFinite(effect.SigmaY) || effect.SigmaX < 0f ||
            effect.SigmaY < 0f || !float.IsFinite(effect.OffsetX) ||
            !float.IsFinite(effect.OffsetY) ||
            !float.IsFinite(effect.ColorR) ||
            !float.IsFinite(effect.ColorG) ||
            !float.IsFinite(effect.ColorB) ||
            !float.IsFinite(effect.ColorA))
        {
            return false;
        }
        if (effect.Kind == NativeGroupEffectKind.GaussianBlur)
        {
            return effect.SigmaX > 0.01f && effect.SigmaY > 0.01f &&
                effect.OffsetX == 0f && effect.OffsetY == 0f &&
                effect.ColorR == 0f && effect.ColorG == 0f &&
                effect.ColorB == 0f && effect.ColorA == 0f;
        }
        return effect.ColorR is >= 0f and <= 1f &&
            effect.ColorG is >= 0f and <= 1f &&
            effect.ColorB is >= 0f and <= 1f &&
            effect.ColorA is >= 0f and <= 1f;
    }

    private static bool IsFinite(Matrix3x2 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32);

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinitePositive(NativeImageRect value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Width) && float.IsFinite(value.Height) &&
        value.Width > 0f && value.Height > 0f;

    private static bool IsFiniteNonnegative(Vector4 value) =>
        float.IsFinite(value.X) && value.X >= 0f &&
        float.IsFinite(value.Y) && value.Y >= 0f &&
        float.IsFinite(value.Z) && value.Z >= 0f &&
        float.IsFinite(value.W) && value.W >= 0f;

    private static bool RequiresMaterialization(in NativeSceneLayer layer) =>
        (layer.Flags & (NativeSceneLayerFlags.Backdrop |
            NativeSceneLayerFlags.ForceIsolation)) != 0 ||
        layer.Opacity != 1f ||
        layer.BlendMode != GpuBlendMode.SrcOver ||
        layer.MaskResourceIndex != NativeMethods.SceneNoIndex ||
        layer.EffectResourceIndex != NativeMethods.SceneNoIndex;

    private static long Align8(long value) => (value + 7L) & ~7L;
}
