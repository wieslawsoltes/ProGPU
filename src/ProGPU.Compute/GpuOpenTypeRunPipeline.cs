using System.Runtime.InteropServices;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Text.Shaping;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;

namespace ProGPU.Compute;

/// <summary>One already-decoded scalar and its UTF input cluster.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct GpuShapingScalar(
    uint CodePoint,
    int Cluster,
    ShapingGlyphFlags Flags = ShapingGlyphFlags.None,
    uint Reserved = 0);

/// <summary>GPU-resident immutable data for one compiled font plan.</summary>
public sealed class GpuOpenTypeFontData : IDisposable
{
    private readonly Dictionary<uint, uint> _resolvedLayoutScripts;
    internal GpuBuffer CmapBuffer { get; }
    internal GpuBuffer MetricsBuffer { get; }
    internal GpuBuffer ExtentsBuffer { get; }
    internal GpuBuffer TablesBuffer { get; }
    internal GpuBuffer TableDirectoryBuffer { get; }
    internal GpuBuffer VariationBuffer { get; }
    internal GpuBuffer VariationMappingBuffer { get; }
    public int CmapRangeCount { get; }
    public int GlyphMetricCount { get; }
    public int VariationCount { get; }
    public int VariationMappingCount { get; }
    public ushort UnitsPerEm { get; }

    public GpuOpenTypeFontData(WgpuContext context, GpuOpenTypeShapingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        CmapRangeCount = plan.Cmap.Length;
        GlyphMetricCount = plan.Metrics.Length;
        VariationCount = plan.Variations.Length;
        VariationMappingCount = plan.VariationMappings.Length;
        UnitsPerEm = plan.UnitsPerEm;
        _resolvedLayoutScripts = CreateResolvedLayoutScripts(plan);
        uint cmapBytes = checked((uint)Math.Max(16, CmapRangeCount * Marshal.SizeOf<GpuCmapRange>()));
        uint metricBytes = checked((uint)Math.Max(16, GlyphMetricCount * Marshal.SizeOf<GpuGlyphMetrics>()));
        CmapBuffer = new GpuBuffer(context, cmapBytes, BufferUsage.Storage | BufferUsage.CopyDst, "OpenType cmap ranges");
        MetricsBuffer = new GpuBuffer(context, metricBytes, BufferUsage.Storage | BufferUsage.CopyDst, "OpenType glyph metrics");
        uint extentsBytes = checked((uint)Math.Max(
            Marshal.SizeOf<GpuGlyphExtents>(), plan.Extents.Length * Marshal.SizeOf<GpuGlyphExtents>()));
        ExtentsBuffer = new GpuBuffer(
            context, extentsBytes, BufferUsage.Storage | BufferUsage.CopyDst, "OpenType glyph extents");
        uint tableBytes = checked((uint)Math.Max(4, (plan.TableData.Length + 3) & ~3));
        TablesBuffer = new GpuBuffer(context, tableBytes, BufferUsage.Storage | BufferUsage.CopyDst, "OpenType table bytes");
        TableDirectoryBuffer = new GpuBuffer(context, 32, BufferUsage.Uniform | BufferUsage.CopyDst, "OpenType table directory");
        uint variationBytes = checked((uint)Math.Max(8, VariationCount * Marshal.SizeOf<GpuLayoutVariationDelta>()));
        VariationBuffer = new GpuBuffer(context, variationBytes, BufferUsage.Storage | BufferUsage.CopyDst, "OpenType variation deltas");
        uint mappingBytes = checked((uint)Math.Max(16, VariationMappingCount * Marshal.SizeOf<GpuVariationMapping>()));
        VariationMappingBuffer = new GpuBuffer(context, mappingBytes, BufferUsage.Storage | BufferUsage.CopyDst, "OpenType variation-selector mappings");
        if (CmapRangeCount != 0) CmapBuffer.Write(plan.Cmap.Span);
        if (GlyphMetricCount != 0) MetricsBuffer.Write(plan.Metrics.Span);
        if (!plan.Extents.IsEmpty) ExtentsBuffer.Write(plan.Extents.Span);
        if (!plan.TableData.IsEmpty) TablesBuffer.WriteBytes(plan.TableData.Span);
        if (VariationCount != 0) VariationBuffer.Write(plan.Variations.Span);
        if (VariationMappingCount != 0) VariationMappingBuffer.Write(plan.VariationMappings.Span);
        TableDirectoryBuffer.WriteSingle(plan.Tables);
    }

    internal uint ResolveLayoutScript(uint requested) =>
        _resolvedLayoutScripts.GetValueOrDefault(requested, requested);

    private static Dictionary<uint, uint> CreateResolvedLayoutScripts(GpuOpenTypeShapingPlan plan)
    {
        uint[] scripts =
        [
            0x62656e67, 0x64657661, 0x67756a72, 0x67757275, 0x6b6e6461,
            0x6d6c796d, 0x6d796d72, 0x6f727961, 0x74616d6c, 0x74656c75
        ];
        var result = new Dictionary<uint, uint>(scripts.Length);
        foreach (uint script in scripts)
        {
            uint resolved = GpuOpenTypeLookupPlanCompiler.ResolveLayoutScript(
                plan, new OpenTypeTag(script)).Value;
            if (resolved != script) result.Add(script, resolved);
        }
        return result;
    }

    public void Dispose()
    {
        MetricsBuffer.Dispose();
        ExtentsBuffer.Dispose();
        TablesBuffer.Dispose();
        TableDirectoryBuffer.Dispose();
        VariationBuffer.Dispose();
        VariationMappingBuffer.Dispose();
        CmapBuffer.Dispose();
    }
}

/// <summary>
/// Executes the parallel initialization and metric phases of the OpenType
/// compute pipeline. Lookup execution is added to the same retained buffers by
/// subsequent VM stages; this type deliberately does not implement
/// <see cref="IOpenTypeShaper"/> until all required stages are installed.
/// </summary>
public unsafe sealed class GpuOpenTypeRunPipeline : IDisposable
{
    private static readonly HashSet<uint> s_stagedScriptTags = CreateStagedScriptTags();
    private static readonly HashSet<uint> s_indicScriptTags = CreateTagSet(
        "beng bng2 deva dev2 gujr gjr2 guru gur2 knda knd2 mlym mlm2 orya ory2 taml tml2 telu tel2");
    private static readonly HashSet<uint> s_arabicScriptTags = CreateTagSet(
        "adlm arab chrs rohg mand mani mong nkoo ougr phag phlp sogd syrc");

    private enum LookupTransitionKind
    {
        Generic,
        Arabic,
        Myanmar,
        Use,
        Indic
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct RunParams(
        uint InputCount,
        uint Capacity,
        uint CmapCount,
        uint MetricCount,
        uint Direction,
        uint LookupCount,
        uint VariationCount,
        uint RequestFlags,
        uint ClusterLevel,
        uint ScriptTag,
        uint VariationMappingCount,
        uint Reserved1,
        uint Reserved2,
        uint Reserved3,
        uint Reserved4,
        uint Reserved5);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct RunState(
        uint GlyphCount,
        uint Status,
        uint SkipCount,
        uint NextSerial,
        uint RandomState,
        uint Reserved0,
        uint Reserved1,
        uint Reserved2,
        uint CommandCursor,
        uint StageStart,
        uint StageEnd,
        uint ActiveStage,
        uint NextStage,
        uint LookupState,
        uint StageCommandCursor,
        uint ActiveCommand,
        uint ActiveCommandEnd);

    private readonly WgpuContext _context;
    private readonly RenderPipelineCache _pipelineCache;
    private readonly ShaderModule* _shader;
    private readonly ComputePipeline* _initializePipeline;
    private readonly ComputePipeline* _metricsPipeline;
    private GpuBuffer? _paramsBuffer;
    private GpuBuffer? _inputBuffer;
    private GpuBuffer? _glyphBuffer;
    private GpuBuffer? _glyphStateBuffer;
    private GpuBuffer? _stateBuffer;
    private GpuBuffer? _lookupBuffer;
    private readonly ComputePipeline* _lookupSelectPipeline;
    private readonly ComputePipeline* _lookupCommandSelectPipeline;
    private ComputePipeline* _singleLookupPipeline;
    private ComputePipeline* _multipleLookupPipeline;
    private ComputePipeline* _alternateLookupPipeline;
    private ComputePipeline* _ligatureLookupPipeline;
    private ComputePipeline* _contextualLookupPipeline;
    private ComputePipeline* _monotoneContextualLookupPipeline;
    private readonly Dictionary<LookupTransitionKind, nint> _lookupTransitionPipelines = new();
    private readonly ComputePipeline* _preprocessPipeline;
    private readonly ComputePipeline* _substitutionFinalizePipeline;
    private readonly ComputePipeline* _positionPipeline;
    private readonly ComputePipeline* _finalizePipeline;
    private readonly GpuBuffer _unicodeDataBuffer;
    private int _capacity;
    private bool _disposed;

    public GpuOpenTypeRunPipeline(WgpuContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _pipelineCache = new RenderPipelineCache(context);
        _shader = _pipelineCache.GetOrCreateShader(
            "OpenTypeShaping", OpenTypeShapingShaders.Source, "OpenType shaping");
        _initializePipeline = _pipelineCache.GetOrCreateComputePipeline(
            "OpenTypeInitialize", _shader, "initialize_glyphs");
        _metricsPipeline = _pipelineCache.GetOrCreateComputePipeline(
            "OpenTypeMetrics", _shader, "load_metrics");
        _lookupSelectPipeline = _pipelineCache.GetOrCreateComputePipeline(
            "OpenTypeLookupStageSelect", _shader, "select_lookup_stage");
        _lookupCommandSelectPipeline = _pipelineCache.GetOrCreateComputePipeline(
            "OpenTypeLookupCommandSelect", _shader, "select_lookup_command");
        _preprocessPipeline = _pipelineCache.GetOrCreateComputePipeline(
            "OpenTypePreprocess", _shader, "preprocess_glyphs");
        _substitutionFinalizePipeline = _pipelineCache.GetOrCreateComputePipeline(
            "OpenTypeSubstitutionFinalize", _shader, "finalize_substitutions");
        _positionPipeline = _pipelineCache.GetOrCreateComputePipeline(
            "OpenTypePositions", _shader, "execute_positions");
        _finalizePipeline = _pipelineCache.GetOrCreateComputePipeline(
            "OpenTypeFinalize", _shader, "finalize_glyphs");
        ReadOnlyMemory<uint> unicodeData = GpuUnicodeShapingPlan.PackedData;
        _unicodeDataBuffer = new GpuBuffer(
            context,
            checked((uint)(unicodeData.Length * sizeof(uint))),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "Packed Unicode shaping data");
        _unicodeDataBuffer.Write(unicodeData.Span);
    }

    public void InitializeRun(
        ReadOnlySpan<GpuShapingScalar> input,
        GpuOpenTypeFontData font,
        ShapingDirection direction,
        ShapingBuffer output) =>
        ExecuteRun(input, font, direction, ReadOnlySpan<GpuOpenTypeLookupCommand>.Empty, output);

    public void ExecuteRun(
        ReadOnlySpan<GpuShapingScalar> input,
        GpuOpenTypeFontData font,
        in ShapingRequest request,
        ReadOnlySpan<GpuOpenTypeLookupCommand> lookups,
        ShapingBuffer output) =>
        ExecuteRunCore(input, font, request.Direction, request.Flags, request.ClusterLevel,
            request.Script.Value, lookups, output);

    public void ExecuteRun(
        ReadOnlySpan<GpuShapingScalar> input,
        GpuOpenTypeFontData font,
        ShapingDirection direction,
        ReadOnlySpan<GpuOpenTypeLookupCommand> lookups,
        ShapingBuffer output) =>
        ExecuteRunCore(input, font, direction, ShapingBufferFlags.None,
            ShapingClusterLevel.Graphemes, OpenTypeTag.DefaultScript.Value, lookups, output);

    private void ExecuteRunCore(
        ReadOnlySpan<GpuShapingScalar> input,
        GpuOpenTypeFontData font,
        ShapingDirection direction,
        ShapingBufferFlags requestFlags,
        ShapingClusterLevel clusterLevel,
        uint scriptTag,
        ReadOnlySpan<GpuOpenTypeLookupCommand> lookups,
        ShapingBuffer output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(output);
        if (direction == ShapingDirection.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(direction));
        output.Clear();
        if (input.IsEmpty) return;
        EnsureCapacity(input.Length, lookups.Length);
        uint resolvedScriptTag = font.ResolveLayoutScript(scriptTag);

        _paramsBuffer!.WriteSingle(new RunParams(
            checked((uint)input.Length),
            checked((uint)_capacity),
            checked((uint)font.CmapRangeCount),
            checked((uint)font.GlyphMetricCount),
            (uint)direction,
            checked((uint)lookups.Length),
            checked((uint)font.VariationCount),
            (uint)requestFlags,
            (uint)clusterLevel,
            resolvedScriptTag,
            checked((uint)font.VariationMappingCount),
            font.UnitsPerEm, 0, 0, 0, 0));
        _inputBuffer!.Write(input);
        _stateBuffer!.WriteSingle(new RunState(
            checked((uint)input.Length), 0, 0, checked((uint)input.Length + 1), 1,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        if (!lookups.IsEmpty) _lookupBuffer!.Write(lookups);

        BindGroup* initializeGroup = CreateBindGroup(_initializePipeline, font, 0);
        BindGroup* metricsGroup = CreateBindGroup(_metricsPipeline, font, 1);
        bool runLookupStages = !lookups.IsEmpty || s_stagedScriptTags.Contains(resolvedScriptTag);
        int substitutionStageCount = CountSubstitutionStages(lookups);
        uint substitutionKinds = GetSubstitutionKindMask(lookups);
        ComputePipeline* lookupTransitionPipeline = runLookupStages
            ? GetLookupTransitionPipeline(ResolveLookupTransitionKind(resolvedScriptTag))
            : null;
        ComputePipeline* singleLookupPipeline = (substitutionKinds & (1u << 1)) != 0 ? GetSingleLookupPipeline() : null;
        ComputePipeline* multipleLookupPipeline = (substitutionKinds & (1u << 2)) != 0 ? GetMultipleLookupPipeline() : null;
        ComputePipeline* alternateLookupPipeline = (substitutionKinds & (1u << 3)) != 0 ? GetAlternateLookupPipeline() : null;
        ComputePipeline* ligatureLookupPipeline = (substitutionKinds & (1u << 4)) != 0 ? GetLigatureLookupPipeline() : null;
        ComputePipeline* contextualLookupPipeline = (substitutionKinds & (1u << 16)) != 0
            ? GetContextualLookupPipeline(clusterLevel)
            : null;
        BindGroup* lookupSelectGroup = runLookupStages ? CreateBindGroup(_lookupSelectPipeline, font, 2) : null;
        BindGroup* lookupCommandSelectGroup = substitutionStageCount == 0
            ? null
            : CreateBindGroup(_lookupCommandSelectPipeline, font, 2);
        BindGroup* lookupTransitionGroup = runLookupStages ? CreateBindGroup(lookupTransitionPipeline, font, 2) : null;
        BindGroup* singleLookupGroup = singleLookupPipeline == null ? null : CreateBindGroup(singleLookupPipeline, font, 2);
        BindGroup* multipleLookupGroup = multipleLookupPipeline == null ? null : CreateBindGroup(multipleLookupPipeline, font, 2);
        BindGroup* alternateLookupGroup = alternateLookupPipeline == null ? null : CreateBindGroup(alternateLookupPipeline, font, 2);
        BindGroup* ligatureLookupGroup = ligatureLookupPipeline == null ? null : CreateBindGroup(ligatureLookupPipeline, font, 2);
        BindGroup* contextualLookupGroup = contextualLookupPipeline == null ? null : CreateBindGroup(contextualLookupPipeline, font, 2);
        BindGroup* preprocessGroup = CreateBindGroup(_preprocessPipeline, font, 6);
        BindGroup* substitutionFinalizeGroup = CreateBindGroup(_substitutionFinalizePipeline, font, 5);
        BindGroup* positionGroup = CreateBindGroup(_positionPipeline, font, 3);
        BindGroup* finalizeGroup = CreateBindGroup(_finalizePipeline, font, 4);
        CommandEncoderDescriptor encoderDescriptor = default;
        CommandEncoder* encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        if (encoder == null) throw new InvalidOperationException("Failed to create the OpenType shaping command encoder.");
        try
        {
            Dispatch(encoder, _initializePipeline, initializeGroup, checked((uint)input.Length));
            Dispatch(encoder, _preprocessPipeline, preprocessGroup, 1);
            if (runLookupStages)
            {
                var substitutionCursor = 0;
                for (var stage = 0; stage <= substitutionStageCount; stage++)
                {
                    Dispatch(encoder, _lookupSelectPipeline, lookupSelectGroup, 1);
                    Dispatch(encoder, lookupTransitionPipeline, lookupTransitionGroup, 1);
                    if (stage < substitutionStageCount)
                    {
                        DispatchSubstitutionStage(
                            encoder,
                            lookups,
                            ref substitutionCursor,
                            lookupCommandSelectGroup,
                            singleLookupPipeline, singleLookupGroup,
                            multipleLookupPipeline, multipleLookupGroup,
                            alternateLookupPipeline, alternateLookupGroup,
                            ligatureLookupPipeline, ligatureLookupGroup,
                            contextualLookupPipeline, contextualLookupGroup);
                    }
                }
            }
            Dispatch(encoder, _substitutionFinalizePipeline, substitutionFinalizeGroup, 1);
            Dispatch(encoder, _metricsPipeline, metricsGroup, checked((uint)_capacity));
            Dispatch(encoder, _positionPipeline, positionGroup, 1);
            Dispatch(encoder, _finalizePipeline, finalizeGroup, checked((uint)_capacity));
            CommandBufferDescriptor commandDescriptor = default;
            CommandBuffer* command = _context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
            if (command == null) throw new InvalidOperationException("Failed to finish the OpenType shaping command buffer.");
            try { _context.Api.QueueSubmit(_context.Queue, 1, &command); }
            finally { _context.Api.CommandBufferRelease(command); }
        }
        finally
        {
            _context.Api.CommandEncoderRelease(encoder);
            _context.Api.BindGroupRelease(finalizeGroup);
            _context.Api.BindGroupRelease(metricsGroup);
            _context.Api.BindGroupRelease(positionGroup);
            _context.Api.BindGroupRelease(substitutionFinalizeGroup);
            _context.Api.BindGroupRelease(preprocessGroup);
            if (contextualLookupGroup != null) _context.Api.BindGroupRelease(contextualLookupGroup);
            if (ligatureLookupGroup != null) _context.Api.BindGroupRelease(ligatureLookupGroup);
            if (alternateLookupGroup != null) _context.Api.BindGroupRelease(alternateLookupGroup);
            if (multipleLookupGroup != null) _context.Api.BindGroupRelease(multipleLookupGroup);
            if (singleLookupGroup != null) _context.Api.BindGroupRelease(singleLookupGroup);
            if (lookupTransitionGroup != null) _context.Api.BindGroupRelease(lookupTransitionGroup);
            if (lookupCommandSelectGroup != null) _context.Api.BindGroupRelease(lookupCommandSelectGroup);
            if (lookupSelectGroup != null) _context.Api.BindGroupRelease(lookupSelectGroup);
            _context.Api.BindGroupRelease(initializeGroup);
        }

        byte[] stateBytes = _stateBuffer!.ReadBytes(0, checked((uint)Marshal.SizeOf<RunState>()));
        RunState state = MemoryMarshal.Cast<byte, RunState>(stateBytes)[0];
        if (state.Status != 0)
            throw new InvalidOperationException($"The WebGPU shaping VM stopped with status {state.Status}.");
        int outputCount = checked((int)state.GlyphCount);
        output.EnsureCapacity(outputCount);
        byte[] bytes = _glyphBuffer!.ReadBytes(0, checked((uint)(outputCount * Marshal.SizeOf<ShapingGlyph>())));
        output.Append(MemoryMarshal.Cast<byte, ShapingGlyph>(bytes));
    }

    private BindGroup* CreateBindGroup(ComputePipeline* pipeline, GpuOpenTypeFontData font, int stage)
    {
        BindGroupLayout* layout = _context.Api.ComputePipelineGetBindGroupLayout(pipeline, 0);
        try
        {
            BindGroupEntry* entries = stackalloc BindGroupEntry[11];
            uint count;
            if (stage == 0)
            {
                entries[0] = Entry(0, _paramsBuffer!);
                entries[1] = Entry(1, _inputBuffer!);
                entries[2] = Entry(2, font.CmapBuffer);
                entries[3] = Entry(4, _glyphBuffer!);
                entries[4] = Entry(7, _stateBuffer!);
                entries[5] = Entry(9, _glyphStateBuffer!);
                count = 6;
            }
            else if (stage == 1)
            {
                entries[0] = Entry(0, _paramsBuffer!);
                entries[1] = Entry(2, font.CmapBuffer);
                entries[2] = Entry(3, font.MetricsBuffer);
                entries[3] = Entry(4, _glyphBuffer!);
                entries[4] = Entry(7, _stateBuffer!);
                entries[5] = Entry(9, _glyphStateBuffer!);
                count = 6;
            }
            else if (stage == 2)
            {
                entries[0] = Entry(0, _paramsBuffer!);
                entries[1] = Entry(4, _glyphBuffer!);
                entries[2] = Entry(5, font.TableDirectoryBuffer);
                entries[3] = Entry(6, font.TablesBuffer);
                entries[4] = Entry(7, _stateBuffer!);
                entries[5] = Entry(8, _lookupBuffer!);
                entries[6] = Entry(9, _glyphStateBuffer!);
                entries[7] = Entry(2, font.CmapBuffer);
                entries[8] = Entry(12, _unicodeDataBuffer);
                count = 9;
            }
            else if (stage == 3)
            {
                entries[0] = Entry(0, _paramsBuffer!);
                entries[1] = Entry(4, _glyphBuffer!);
                entries[2] = Entry(5, font.TableDirectoryBuffer);
                entries[3] = Entry(6, font.TablesBuffer);
                entries[4] = Entry(7, _stateBuffer!);
                entries[5] = Entry(8, _lookupBuffer!);
                entries[6] = Entry(9, _glyphStateBuffer!);
                entries[7] = Entry(10, font.VariationBuffer);
                entries[8] = Entry(12, _unicodeDataBuffer);
                entries[9] = Entry(13, font.ExtentsBuffer);
                entries[10] = Entry(3, font.MetricsBuffer);
                count = 11;
            }
            else if (stage == 4)
            {
                entries[0] = Entry(0, _paramsBuffer!);
                entries[1] = Entry(4, _glyphBuffer!);
                entries[2] = Entry(7, _stateBuffer!);
                count = 3;
            }
            else if (stage == 5)
            {
                entries[0] = Entry(0, _paramsBuffer!);
                entries[1] = Entry(2, font.CmapBuffer);
                entries[2] = Entry(4, _glyphBuffer!);
                entries[3] = Entry(7, _stateBuffer!);
                entries[4] = Entry(9, _glyphStateBuffer!);
                count = 5;
            }
            else
            {
                entries[0] = Entry(0, _paramsBuffer!);
                entries[1] = Entry(4, _glyphBuffer!);
                entries[2] = Entry(7, _stateBuffer!);
                entries[3] = Entry(9, _glyphStateBuffer!);
                entries[4] = Entry(11, font.VariationMappingBuffer);
                entries[5] = Entry(12, _unicodeDataBuffer);
                entries[6] = Entry(8, _lookupBuffer!);
                entries[7] = Entry(2, font.CmapBuffer);
                count = 8;
            }
            BindGroupDescriptor descriptor = new() { Layout = layout, EntryCount = count, Entries = entries };
            BindGroup* group = _context.Api.DeviceCreateBindGroup(_context.Device, &descriptor);
            if (group == null) throw new InvalidOperationException("Failed to create an OpenType shaping bind group.");
            return group;
        }
        finally { _context.Api.BindGroupLayoutRelease(layout); }
    }

    private static BindGroupEntry Entry(uint binding, GpuBuffer buffer) => new()
    {
        Binding = binding,
        Buffer = buffer.BufferPtr,
        Offset = 0,
        Size = buffer.Size
    };

    private void Dispatch(CommandEncoder* encoder, ComputePipeline* pipeline, BindGroup* group, uint count)
    {
        ComputePassDescriptor descriptor = default;
        ComputePassEncoder* pass = _context.Api.CommandEncoderBeginComputePass(encoder, &descriptor);
        _context.Api.ComputePassEncoderSetPipeline(pass, pipeline);
        _context.Api.ComputePassEncoderSetBindGroup(pass, 0, group, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(pass, (count + 63) / 64, 1, 1);
        _context.Api.ComputePassEncoderEnd(pass);
        _context.Api.ComputePassEncoderRelease(pass);
    }

    private void EnsureCapacity(int count, int lookupCount)
    {
        if (_paramsBuffer is null)
        {
            _paramsBuffer = new GpuBuffer(_context, 64, BufferUsage.Uniform | BufferUsage.CopyDst, "OpenType run parameters");
            _stateBuffer = new GpuBuffer(
                _context,
                checked((uint)Marshal.SizeOf<RunState>()),
                BufferUsage.Storage | BufferUsage.CopySrc | BufferUsage.CopyDst,
                "OpenType run state");
        }
        uint lookupBytes = checked((uint)Math.Max(40, lookupCount * Marshal.SizeOf<GpuOpenTypeLookupCommand>()));
        if (_lookupBuffer is null || _lookupBuffer.Size < lookupBytes)
        {
            _lookupBuffer?.Dispose();
            _lookupBuffer = new GpuBuffer(_context, lookupBytes, BufferUsage.Storage | BufferUsage.CopyDst, "OpenType lookup commands");
        }
        if (count <= _capacity) return;
        uint requested = checked((uint)Math.Max(count, Math.Min(ShapingBuffer.DefaultMaximumGlyphCount, count * 4L)));
        int capacity = Math.Max(64, checked((int)BitOperations.RoundUpToPowerOf2(requested)));
        _inputBuffer?.Dispose();
        _glyphBuffer?.Dispose();
        _glyphStateBuffer?.Dispose();
        _inputBuffer = new GpuBuffer(
            _context,
            checked((uint)(capacity * Marshal.SizeOf<GpuShapingScalar>())),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "OpenType input scalars");
        _glyphBuffer = new GpuBuffer(
            _context,
            checked((uint)(capacity * Marshal.SizeOf<ShapingGlyph>())),
            BufferUsage.Storage | BufferUsage.CopySrc | BufferUsage.CopyDst,
            "OpenType shaping glyphs");
        _glyphStateBuffer = new GpuBuffer(
            _context,
            checked((uint)(capacity * 32)),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "OpenType internal glyph state");
        _capacity = capacity;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _glyphBuffer?.Dispose();
        _glyphStateBuffer?.Dispose();
        _inputBuffer?.Dispose();
        _paramsBuffer?.Dispose();
        _stateBuffer?.Dispose();
        _lookupBuffer?.Dispose();
        _unicodeDataBuffer.Dispose();
        _pipelineCache.Dispose();
    }

    private static int CountSubstitutionStages(ReadOnlySpan<GpuOpenTypeLookupCommand> lookups)
    {
        var count = 0;
        var hasStage = false;
        uint previousStage = 0;
        for (var index = 0; index < lookups.Length; index++)
        {
            ref readonly GpuOpenTypeLookupCommand command = ref lookups[index];
            if (command.TableKind != 1 || command.FeatureValue == 0) continue;
            if (!hasStage || command.Stage != previousStage)
            {
                count++;
                hasStage = true;
                previousStage = command.Stage;
            }
        }
        return count;
    }

    private static uint GetSubstitutionKindMask(ReadOnlySpan<GpuOpenTypeLookupCommand> lookups)
    {
        uint mask = 0;
        for (var index = 0; index < lookups.Length; index++)
        {
            ref readonly GpuOpenTypeLookupCommand command = ref lookups[index];
            if (command.TableKind != 1 || command.FeatureValue == 0) continue;
            mask |= command.LookupType is >= 1 and <= 4
                ? 1u << checked((int)command.LookupType)
                : 1u << 16;
        }
        return mask;
    }

    private void DispatchSubstitutionStage(
        CommandEncoder* encoder,
        ReadOnlySpan<GpuOpenTypeLookupCommand> lookups,
        ref int cursor,
        BindGroup* commandSelectGroup,
        ComputePipeline* singlePipeline,
        BindGroup* singleGroup,
        ComputePipeline* multiplePipeline,
        BindGroup* multipleGroup,
        ComputePipeline* alternatePipeline,
        BindGroup* alternateGroup,
        ComputePipeline* ligaturePipeline,
        BindGroup* ligatureGroup,
        ComputePipeline* contextualPipeline,
        BindGroup* contextualGroup)
    {
        while (cursor < lookups.Length &&
               (lookups[cursor].TableKind != 1 || lookups[cursor].FeatureValue == 0))
        {
            cursor++;
        }
        if (cursor >= lookups.Length)
            throw new InvalidOperationException("The CPU and GPU OpenType stage selectors diverged.");

        uint stage = lookups[cursor].Stage;
        while (cursor < lookups.Length)
        {
            ref readonly GpuOpenTypeLookupCommand command = ref lookups[cursor];
            if (command.TableKind != 1 || command.FeatureValue == 0)
            {
                cursor++;
                continue;
            }
            if (command.Stage != stage) break;

            uint lookupType = command.LookupType;
            Dispatch(encoder, _lookupCommandSelectPipeline, commandSelectGroup, 1);
            switch (lookupType)
            {
                case 1: Dispatch(encoder, singlePipeline, singleGroup, 1); break;
                case 2: Dispatch(encoder, multiplePipeline, multipleGroup, 1); break;
                case 3: Dispatch(encoder, alternatePipeline, alternateGroup, 1); break;
                case 4: Dispatch(encoder, ligaturePipeline, ligatureGroup, 1); break;
                default: Dispatch(encoder, contextualPipeline, contextualGroup, 1); break;
            }

            cursor++;
            while (cursor < lookups.Length)
            {
                ref readonly GpuOpenTypeLookupCommand candidate = ref lookups[cursor];
                if (candidate.TableKind == 1 && candidate.FeatureValue != 0)
                {
                    if (candidate.Stage != stage || candidate.LookupType != lookupType) break;
                }
                cursor++;
            }
        }
    }

    private ComputePipeline* GetLookupTransitionPipeline(LookupTransitionKind kind)
    {
        if (_lookupTransitionPipelines.TryGetValue(kind, out nint cached))
            return (ComputePipeline*)cached;

        string entryPoint = kind switch
        {
            LookupTransitionKind.Arabic => "transition_arabic_lookup_stage",
            LookupTransitionKind.Myanmar => "transition_myanmar_lookup_stage",
            LookupTransitionKind.Use => "transition_use_lookup_stage",
            LookupTransitionKind.Indic => "transition_indic_lookup_stage",
            _ => "transition_generic_lookup_stage"
        };
        ComputePipeline* pipeline = _pipelineCache.GetOrCreateComputePipeline(
            $"OpenTypeLookupTransition{kind}",
            _shader,
            entryPoint);
        _lookupTransitionPipelines.Add(kind, (nint)pipeline);
        return pipeline;
    }

    private ComputePipeline* GetSingleLookupPipeline()
    {
        if (_singleLookupPipeline == null)
            _singleLookupPipeline = _pipelineCache.GetOrCreateComputePipeline(
                "OpenTypeSingleSubstitution", _shader, "execute_single_substitution_lookup");
        return _singleLookupPipeline;
    }

    private ComputePipeline* GetMultipleLookupPipeline()
    {
        if (_multipleLookupPipeline == null)
            _multipleLookupPipeline = _pipelineCache.GetOrCreateComputePipeline(
                "OpenTypeMultipleSubstitution", _shader, "execute_multiple_substitution_lookup");
        return _multipleLookupPipeline;
    }

    private ComputePipeline* GetAlternateLookupPipeline()
    {
        if (_alternateLookupPipeline == null)
            _alternateLookupPipeline = _pipelineCache.GetOrCreateComputePipeline(
                "OpenTypeAlternateSubstitution", _shader, "execute_alternate_substitution_lookup");
        return _alternateLookupPipeline;
    }

    private ComputePipeline* GetLigatureLookupPipeline()
    {
        if (_ligatureLookupPipeline == null)
            _ligatureLookupPipeline = _pipelineCache.GetOrCreateComputePipeline(
                "OpenTypeLigatureSubstitution", _shader, "execute_ligature_substitution_lookup");
        return _ligatureLookupPipeline;
    }

    private ComputePipeline* GetContextualLookupPipeline(ShapingClusterLevel clusterLevel)
    {
        if (clusterLevel is ShapingClusterLevel.MonotoneGraphemes or ShapingClusterLevel.MonotoneCharacters)
        {
            if (_monotoneContextualLookupPipeline == null)
                _monotoneContextualLookupPipeline = _pipelineCache.GetOrCreateComputePipeline(
                    "OpenTypeMonotoneContextualSubstitutionLookups",
                    _shader,
                    "execute_contextual_substitution_lookup_stage_monotone");
            return _monotoneContextualLookupPipeline;
        }

        if (_contextualLookupPipeline == null)
            _contextualLookupPipeline = _pipelineCache.GetOrCreateComputePipeline(
                "OpenTypeContextualSubstitutionLookups", _shader, "execute_contextual_substitution_lookup_stage");
        return _contextualLookupPipeline;
    }

    private static LookupTransitionKind ResolveLookupTransitionKind(uint scriptTag)
    {
        if (scriptTag is 0x6d796d72 or 0x6d796d32) return LookupTransitionKind.Myanmar;
        if (s_indicScriptTags.Contains(scriptTag)) return LookupTransitionKind.Indic;
        if (s_stagedScriptTags.Contains(scriptTag)) return LookupTransitionKind.Use;
        if (s_arabicScriptTags.Contains(scriptTag)) return LookupTransitionKind.Arabic;
        return LookupTransitionKind.Generic;
    }

    private static HashSet<uint> CreateStagedScriptTags()
    {
        const string tags =
            "beng bng2 deva dev2 gujr gjr2 guru gur2 knda knd2 mlym mlm2 orya ory2 taml tml2 telu tel2 " +
            "bng3 dev3 gjr3 gur3 knd3 mlm3 ory3 tml3 tel3 mymr mym2 " +
            "tibt mong sinh java marc limb tale bugi khar sylo tfng bali nkoo phag cham kali lepc rjng saur sund " +
            "egyp kthi mtei lana tavt batk brah mand cakm plrd shrd takr dupl gran khoj sind mahj mani modi hmng " +
            "phlp sidd tirh ahom mult adlm bhks newa gonm soyo zanb dogr gong rohg maka medf sogo sogd elym nand " +
            "hmnp wcho chrs diak kits yezi cpmn ougr tnsa toto vith kawi nagm";
        return CreateTagSet(tags);
    }

    private static HashSet<uint> CreateTagSet(string tags)
    {
        var result = new HashSet<uint>();
        foreach (string tag in tags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            result.Add(new OpenTypeTag(tag).Value);
        return result;
    }
}
