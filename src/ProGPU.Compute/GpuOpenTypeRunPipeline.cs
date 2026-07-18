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
    internal GpuBuffer CmapBuffer { get; }
    internal GpuBuffer MetricsBuffer { get; }
    internal GpuBuffer TablesBuffer { get; }
    internal GpuBuffer TableDirectoryBuffer { get; }
    internal GpuBuffer VariationBuffer { get; }
    public int CmapRangeCount { get; }
    public int GlyphMetricCount { get; }
    public int VariationCount { get; }

    public GpuOpenTypeFontData(WgpuContext context, GpuOpenTypeShapingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        CmapRangeCount = plan.Cmap.Length;
        GlyphMetricCount = plan.Metrics.Length;
        VariationCount = plan.Variations.Length;
        uint cmapBytes = checked((uint)Math.Max(16, CmapRangeCount * Marshal.SizeOf<GpuCmapRange>()));
        uint metricBytes = checked((uint)Math.Max(16, GlyphMetricCount * Marshal.SizeOf<GpuGlyphMetrics>()));
        CmapBuffer = new GpuBuffer(context, cmapBytes, BufferUsage.Storage | BufferUsage.CopyDst, "OpenType cmap ranges");
        MetricsBuffer = new GpuBuffer(context, metricBytes, BufferUsage.Storage | BufferUsage.CopyDst, "OpenType glyph metrics");
        uint tableBytes = checked((uint)Math.Max(4, (plan.TableData.Length + 3) & ~3));
        TablesBuffer = new GpuBuffer(context, tableBytes, BufferUsage.Storage | BufferUsage.CopyDst, "OpenType table bytes");
        TableDirectoryBuffer = new GpuBuffer(context, 32, BufferUsage.Uniform | BufferUsage.CopyDst, "OpenType table directory");
        uint variationBytes = checked((uint)Math.Max(8, VariationCount * Marshal.SizeOf<GpuLayoutVariationDelta>()));
        VariationBuffer = new GpuBuffer(context, variationBytes, BufferUsage.Storage | BufferUsage.CopyDst, "OpenType variation deltas");
        if (CmapRangeCount != 0) CmapBuffer.Write(plan.Cmap.Span);
        if (GlyphMetricCount != 0) MetricsBuffer.Write(plan.Metrics.Span);
        if (!plan.TableData.IsEmpty) TablesBuffer.WriteBytes(plan.TableData.Span);
        if (VariationCount != 0) VariationBuffer.Write(plan.Variations.Span);
        TableDirectoryBuffer.WriteSingle(plan.Tables);
    }

    public void Dispose()
    {
        MetricsBuffer.Dispose();
        TablesBuffer.Dispose();
        TableDirectoryBuffer.Dispose();
        VariationBuffer.Dispose();
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
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct RunParams(
        uint InputCount,
        uint Capacity,
        uint CmapCount,
        uint MetricCount,
        uint Direction,
        uint LookupCount,
        uint VariationCount,
        uint Reserved1);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct RunState(
        uint GlyphCount,
        uint Status,
        uint SkipCount,
        uint NextSerial,
        uint RandomState,
        uint Reserved0,
        uint Reserved1,
        uint Reserved2);

    private readonly WgpuContext _context;
    private readonly RenderPipelineCache _pipelineCache;
    private readonly ComputePipeline* _initializePipeline;
    private readonly ComputePipeline* _metricsPipeline;
    private GpuBuffer? _paramsBuffer;
    private GpuBuffer? _inputBuffer;
    private GpuBuffer? _glyphBuffer;
    private GpuBuffer? _glyphStateBuffer;
    private GpuBuffer? _stateBuffer;
    private GpuBuffer? _lookupBuffer;
    private readonly ComputePipeline* _lookupPipeline;
    private readonly ComputePipeline* _positionPipeline;
    private readonly ComputePipeline* _finalizePipeline;
    private int _capacity;
    private bool _disposed;

    public GpuOpenTypeRunPipeline(WgpuContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _pipelineCache = new RenderPipelineCache(context);
        ShaderModule* shader = _pipelineCache.GetOrCreateShader(
            "OpenTypeShaping", OpenTypeShapingShaders.Source, "OpenType shaping");
        _initializePipeline = _pipelineCache.GetOrCreateComputePipeline(
            "OpenTypeInitialize", shader, "initialize_glyphs");
        _metricsPipeline = _pipelineCache.GetOrCreateComputePipeline(
            "OpenTypeMetrics", shader, "load_metrics");
        _lookupPipeline = _pipelineCache.GetOrCreateComputePipeline(
            "OpenTypeLookups", shader, "execute_lookups");
        _positionPipeline = _pipelineCache.GetOrCreateComputePipeline(
            "OpenTypePositions", shader, "execute_positions");
        _finalizePipeline = _pipelineCache.GetOrCreateComputePipeline(
            "OpenTypeFinalize", shader, "finalize_glyphs");
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
        ShapingDirection direction,
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

        _paramsBuffer!.WriteSingle(new RunParams(
            checked((uint)input.Length),
            checked((uint)_capacity),
            checked((uint)font.CmapRangeCount),
            checked((uint)font.GlyphMetricCount),
            (uint)direction,
            checked((uint)lookups.Length),
            checked((uint)font.VariationCount),
            0));
        _inputBuffer!.Write(input);
        _stateBuffer!.WriteSingle(new RunState(checked((uint)input.Length), 0, 0, checked((uint)input.Length + 1), 1, 0, 0, 0));
        if (!lookups.IsEmpty) _lookupBuffer!.Write(lookups);

        BindGroup* initializeGroup = CreateBindGroup(_initializePipeline, font, 0);
        BindGroup* metricsGroup = CreateBindGroup(_metricsPipeline, font, 1);
        BindGroup* lookupGroup = CreateBindGroup(_lookupPipeline, font, 2);
        BindGroup* positionGroup = CreateBindGroup(_positionPipeline, font, 3);
        BindGroup* finalizeGroup = CreateBindGroup(_finalizePipeline, font, 4);
        CommandEncoderDescriptor encoderDescriptor = default;
        CommandEncoder* encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        if (encoder == null) throw new InvalidOperationException("Failed to create the OpenType shaping command encoder.");
        try
        {
            Dispatch(encoder, _initializePipeline, initializeGroup, checked((uint)input.Length));
            if (!lookups.IsEmpty) Dispatch(encoder, _lookupPipeline, lookupGroup, 1);
            Dispatch(encoder, _metricsPipeline, metricsGroup, checked((uint)_capacity));
            if (!lookups.IsEmpty) Dispatch(encoder, _positionPipeline, positionGroup, 1);
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
            _context.Api.BindGroupRelease(lookupGroup);
            _context.Api.BindGroupRelease(initializeGroup);
        }

        byte[] stateBytes = _stateBuffer!.ReadBytes(0, 32);
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
            BindGroupEntry* entries = stackalloc BindGroupEntry[8];
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
                entries[1] = Entry(3, font.MetricsBuffer);
                entries[2] = Entry(4, _glyphBuffer!);
                entries[3] = Entry(7, _stateBuffer!);
                count = 4;
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
                count = 7;
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
                count = 8;
            }
            else
            {
                entries[0] = Entry(4, _glyphBuffer!);
                entries[1] = Entry(7, _stateBuffer!);
                count = 2;
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
            _paramsBuffer = new GpuBuffer(_context, 32, BufferUsage.Uniform | BufferUsage.CopyDst, "OpenType run parameters");
            _stateBuffer = new GpuBuffer(_context, 32, BufferUsage.Storage | BufferUsage.CopySrc | BufferUsage.CopyDst, "OpenType run state");
        }
        uint lookupBytes = checked((uint)Math.Max(36, lookupCount * Marshal.SizeOf<GpuOpenTypeLookupCommand>()));
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
        _pipelineCache.Dispose();
    }
}
