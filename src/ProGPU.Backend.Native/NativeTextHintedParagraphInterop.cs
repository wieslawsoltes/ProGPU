using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProGPU.Backend.Native;

public enum NativeHintedProjectionPolicy : uint
{
    Automatic = 0,
    NativeCompute = 1,
    GpuShader = 2,
    IntrinsicSimd = 3,
    ScalarReference = 4,
}

/// <summary>Explicit vector coverage is not FreeType grayscale/B/W raster parity.</summary>
public enum NativeHintedCoverage : uint
{
    Strict = 0,
    NonzeroVector = 1,
    /// <summary>Nonzero antialiased coverage retains B/W dropout metadata without executing B/W scan conversion.</summary>
    AntialiasedVector = 2,
}

// The authoritative public C header supplies these fields through the hosted
// contract generator. No native record layout is duplicated in this bridge.
public partial struct NativeHintedParagraphDeviceStyle { }
public partial struct NativeHintedParagraphCounts { }
public partial struct NativeHintedParagraphRun { }
public partial struct NativeHintedParagraphGlyphOwner { }

/// <summary>
/// One original owned formatted generation and its measured interaction.
/// Read-only snapshots preserve source indices, exact run/descriptor owners,
/// actual writer-used bidi levels and pen origins. No prefix reshaping occurs.
/// This explicit API does not enable source Display or editor UI admission.
/// </summary>
public sealed unsafe class NativeHintedParagraph : IDisposable
{
    private readonly NativeTextContextOwner _owner = null!;
    private readonly NativeTextScalar[] _sourceScalars, _admittedScalars;
    private readonly NativeTextBidiLevel[] _scalarLevels;
    private readonly NativeTextStyleRun[] _styles;
    private readonly NativeTextStyleMetrics[] _sourceMetrics;
    private readonly NativeHintedParagraphRun[] _runs;
    private readonly NativeTextShapingGlyph[] _logicalGlyphs;
    private readonly NativeHintedParagraphGlyphOwner[] _logicalOwners, _positionedOwners;
    private readonly int[] _logicalClusterEnds, _clusterEnds;
    private readonly sbyte[] _logicalBidiLevels, _bidiLevels;
    private readonly float[] _glyphScales, _lineOrigins;
    private readonly NativePositionedTextGlyph[] _glyphs;
    private readonly NativePositionedTextLine[] _lines;
    private readonly NativeTextClusterBox[] _boxes;
    private readonly NativeTextCaretStop[] _carets;
    private readonly bool _sourceGeometry;

    internal NativeHintedParagraph(nint handle, bool sourceGeometry = false)
    {
        _sourceGeometry = sourceGeometry;
        NativeHintedParagraphCounts counts = default;
        NativeTextParagraphResult result = new() { StructSize = (uint)sizeof(NativeTextParagraphResult) };
        ThrowForStatus(NativeMethods.GetHintedParagraphCounts(handle, &counts, &result), "paragraph counts");
        Counts = counts; Result = result;
        _sourceScalars = new NativeTextScalar[checked((int)counts.SourceScalarCount)];
        _admittedScalars = new NativeTextScalar[checked((int)counts.AdmittedScalarCount)];
        _scalarLevels = new NativeTextBidiLevel[checked((int)counts.SourceScalarCount)];
        _styles = new NativeTextStyleRun[checked((int)counts.StyleCount)];
        _sourceMetrics = new NativeTextStyleMetrics[checked((int)counts.StyleCount)];
        _runs = new NativeHintedParagraphRun[checked((int)counts.RunCount)];
        _logicalGlyphs = new NativeTextShapingGlyph[checked((int)counts.LogicalGlyphCount)];
        _logicalOwners = new NativeHintedParagraphGlyphOwner[_logicalGlyphs.Length];
        _logicalClusterEnds = new int[_logicalGlyphs.Length];
        _logicalBidiLevels = new sbyte[_logicalGlyphs.Length];
        _glyphScales = new float[_logicalGlyphs.Length];
        _glyphs = new NativePositionedTextGlyph[checked((int)counts.PositionedGlyphCount)];
        _positionedOwners = new NativeHintedParagraphGlyphOwner[_glyphs.Length];
        _clusterEnds = new int[_glyphs.Length]; _bidiLevels = new sbyte[_glyphs.Length];
        _lines = new NativePositionedTextLine[checked((int)counts.LineCount)];
        _lineOrigins = new float[_lines.Length];
        _boxes = new NativeTextClusterBox[checked((int)counts.ClusterBoxCount)];
        _carets = new NativeTextCaretStop[checked((int)counts.CaretStopCount)];
        CopySnapshot(handle);
        // Last throwing construction operation: the capturing caller still
        // destroys the raw handle if any snapshot/owner allocation fails.
        _owner = new NativeTextContextOwner(handle, NativeMethods.DestroyHintedParagraph, nameof(NativeHintedParagraph));
    }

    public NativeHintedParagraphCounts Counts { get; }
    public NativeTextParagraphResult Result { get; }
    public ReadOnlySpan<NativeTextScalar> SourceScalars => _sourceScalars;
    public ReadOnlySpan<NativeTextScalar> AdmittedScalars => _admittedScalars;
    public ReadOnlySpan<NativeTextBidiLevel> ScalarLevels => _scalarLevels;
    public ReadOnlySpan<NativeTextStyleRun> Styles => _styles;
    public ReadOnlySpan<NativeTextStyleMetrics> SourceMetrics => _sourceMetrics;
    public ReadOnlySpan<NativeHintedParagraphRun> Runs => _runs;
    public ReadOnlySpan<NativeTextShapingGlyph> LogicalGlyphs => _logicalGlyphs;
    public ReadOnlySpan<NativeHintedParagraphGlyphOwner> LogicalOwners => _logicalOwners;
    public ReadOnlySpan<int> LogicalClusterEnds => _logicalClusterEnds;
    public ReadOnlySpan<sbyte> LogicalBidiLevels => _logicalBidiLevels;
    public ReadOnlySpan<float> GlyphScales => _glyphScales;
    public ReadOnlySpan<NativePositionedTextGlyph> Glyphs => _glyphs;
    public ReadOnlySpan<NativeHintedParagraphGlyphOwner> PositionedOwners => _positionedOwners;
    public ReadOnlySpan<int> ClusterEnds => _clusterEnds;
    public ReadOnlySpan<sbyte> BidiLevels => _bidiLevels;
    public ReadOnlySpan<NativePositionedTextLine> Lines => _lines;
    public ReadOnlySpan<float> LineOrigins => _lineOrigins;
    public ReadOnlySpan<NativeTextClusterBox> Boxes => _boxes;
    public ReadOnlySpan<NativeTextCaretStop> Carets => _carets;

    internal NativeTextContextOwner.Use AcquireSourceUse() => _owner.Acquire();

    private void CopySnapshot(nint handle)
    {
        fixed (NativeTextScalar* source = _sourceScalars)
        fixed (NativeTextScalar* admitted = _admittedScalars)
        fixed (NativeTextBidiLevel* scalarLevels = _scalarLevels)
        fixed (NativeTextStyleRun* styles = _styles)
        fixed (NativeTextStyleMetrics* metrics = _sourceMetrics)
        fixed (NativeHintedParagraphRun* runs = _runs)
        fixed (NativeTextShapingGlyph* logical = _logicalGlyphs)
        fixed (NativeHintedParagraphGlyphOwner* logicalOwners = _logicalOwners)
        fixed (int* logicalEnds = _logicalClusterEnds)
        fixed (sbyte* logicalLevels = _logicalBidiLevels)
        fixed (float* scales = _glyphScales)
        fixed (NativePositionedTextGlyph* glyphs = _glyphs)
        fixed (NativeHintedParagraphGlyphOwner* positionedOwners = _positionedOwners)
        fixed (int* ends = _clusterEnds)
        fixed (sbyte* levels = _bidiLevels)
        fixed (NativePositionedTextLine* lines = _lines)
        fixed (float* origins = _lineOrigins)
        fixed (NativeTextClusterBox* boxes = _boxes)
        fixed (NativeTextCaretStop* carets = _carets)
        {
            NativeMethods.HintedParagraphFormatBuffers buffers = new()
            {
                StructSize = (uint)sizeof(NativeMethods.HintedParagraphFormatBuffers),
                SourceScalars = (nuint)source, SourceScalarCapacity = Counts.SourceScalarCount,
                AdmittedScalars = (nuint)admitted, AdmittedScalarCapacity = Counts.AdmittedScalarCount,
                ScalarLevels = (nuint)scalarLevels, ScalarLevelCapacity = Counts.SourceScalarCount,
                Styles = (nuint)styles, StyleCapacity = Counts.StyleCount,
                SourceMetrics = (nuint)metrics, SourceMetricCapacity = Counts.StyleCount,
                Runs = (nuint)runs, RunCapacity = Counts.RunCount,
                LogicalGlyphs = (nuint)logical, LogicalGlyphCapacity = Counts.LogicalGlyphCount,
                LogicalOwners = (nuint)logicalOwners, LogicalOwnerCapacity = Counts.LogicalGlyphCount,
                LogicalClusterEnds = (nuint)logicalEnds, LogicalClusterEndCapacity = Counts.LogicalGlyphCount,
                LogicalBidiLevels = (nuint)logicalLevels, LogicalBidiLevelCapacity = Counts.LogicalGlyphCount,
                GlyphScales = (nuint)scales, GlyphScaleCapacity = Counts.LogicalGlyphCount,
                PositionedGlyphs = (nuint)glyphs, PositionedGlyphCapacity = Counts.PositionedGlyphCount,
                PositionedOwners = (nuint)positionedOwners, PositionedOwnerCapacity = Counts.PositionedGlyphCount,
                PositionedClusterEnds = (nuint)ends, PositionedClusterEndCapacity = Counts.PositionedGlyphCount,
                PositionedBidiLevels = (nuint)levels, PositionedBidiLevelCapacity = Counts.PositionedGlyphCount,
                Lines = (nuint)lines, LineCapacity = Counts.LineCount,
                LineOrigins = (nuint)origins, LineOriginCapacity = Counts.LineCount,
            };
            ThrowForStatus(NativeMethods.CopyHintedParagraphFormat(handle, &buffers), "paragraph format snapshot");
            ThrowForStatus(NativeMethods.CopyHintedParagraphInteraction(handle, boxes, Counts.ClusterBoxCount,
                carets, Counts.CaretStopCount), "paragraph interaction snapshot");
        }
    }

    /// <summary>
    /// Retains geometry, every original occurrence and measured interaction
    /// without requiring a GPU target, paint or fabricated source origin.
    /// Renderer imports copy flat records while the original owner is leased.
    /// </summary>
    public NativeHintedGlyphResource PrepareGlyphResource(float dpiScale,
        NativeHintedProjectionPolicy projection = NativeHintedProjectionPolicy.Automatic,
        NativeHintedCoverage coverage = NativeHintedCoverage.Strict)
        => PrepareGlyphResourceCore(dpiScale, projection, coverage, false);

    /// <summary>
    /// Also retains the original horizontal hmtx design advance for every
    /// positioned occurrence. Missing metrics and coordinate-bearing instances
    /// reject atomically; no half-em, device-advance or GPOS substitution occurs.
    /// This does not admit source offsets, frames, carets or Display selection.
    /// </summary>
    public NativeHintedGlyphResource PrepareGlyphResourceWithNominalMetrics(float dpiScale,
        NativeHintedProjectionPolicy projection = NativeHintedProjectionPolicy.Automatic,
        NativeHintedCoverage coverage = NativeHintedCoverage.Strict)
        => PrepareGlyphResourceCore(dpiScale, projection, coverage, true);

    private NativeHintedGlyphResource PrepareGlyphResourceCore(float dpiScale,
        NativeHintedProjectionPolicy projection, NativeHintedCoverage coverage, bool nominalMetrics)
    {
        if (!float.IsFinite(dpiScale) || dpiScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        using var use = _owner.Acquire();
        NativeMethods.HintedGlyphResourceRequest request = new()
        {
            AbiVersion = NativeMethods.AbiVersion,
            StructSize = (uint)sizeof(NativeMethods.HintedGlyphResourceRequest),
            DpiScale = dpiScale, ProjectionPolicy = (uint)projection, Coverage = (uint)coverage,
        };
        nint resource = 0;
        try
        {
            ThrowForStatus(nominalMetrics
                ? NativeMethods.PrepareHintedGlyphResourceWithNominalMetrics(use.Handle, &request, &resource)
                : NativeMethods.PrepareHintedGlyphResource(use.Handle, &request, &resource), "glyph resource preparation");
            if (resource == 0) throw new InvalidOperationException("Native glyph preparation returned no owner.");
            return new NativeHintedGlyphResource(resource, dpiScale, projection, coverage, nominalMetrics, _sourceGeometry);
        }
        catch
        {
            if (resource != 0) NativeMethods.DestroyHintedGlyphResource(resource);
            throw;
        }
    }

    /// <summary>
    /// Pure CPU frame preparation over this same generation. The returned frame
    /// owns its native data independently of this paragraph/context. The target
    /// stays caller-owned and must retain its exact view until rendering.
    /// </summary>
    public NativeHintedParagraphFrame PrepareFrame(GpuTexture target, float dpiScale,
        ReadOnlySpan<Vector4> styleColors, Vector2 logicalOrigin = default, Vector4 clearColor = default,
        NativeHintedProjectionPolicy projection = NativeHintedProjectionPolicy.Automatic,
        NativeHintedCoverage coverage = NativeHintedCoverage.Strict)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!float.IsFinite(dpiScale) || dpiScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        if ((uint)styleColors.Length != Counts.StyleCount)
            throw new ArgumentException("Each original style requires one solid color.", nameof(styleColors));
        using var use = _owner.Acquire();
        lock (target.Context.RenderLock)
        {
            if (target.IsDisposed || target.Context.IsDisposed || target.ViewPtr == null)
                throw new ObjectDisposedException(nameof(target));
            NativeMethods.HintedParagraphFrameRequest request = new()
            {
                AbiVersion = NativeMethods.AbiVersion,
                StructSize = (uint)sizeof(NativeMethods.HintedParagraphFrameRequest),
                Width = target.Width, Height = target.Height, DpiScale = dpiScale,
                TargetView = (nuint)target.ViewPtr, LogicalOrigin = logicalOrigin,
                ClearColor = new() { R = clearColor.X, G = clearColor.Y, B = clearColor.Z, A = clearColor.W },
                ProjectionPolicy = (uint)projection, Coverage = (uint)coverage,
            };
            nint frame = 0;
            try
            {
                fixed (Vector4* colors = styleColors)
                    ThrowForStatus(NativeMethods.PrepareHintedParagraphFrame(use.Handle, &request,
                        (NativeMethods.NativeColor*)colors, checked((uint)styleColors.Length), &frame), "paragraph frame preparation");
                if (frame == 0) throw new InvalidOperationException("Native paragraph frame preparation returned no owner.");
                return new NativeHintedParagraphFrame(frame, target, dpiScale, projection, coverage);
            }
            catch
            {
                if (frame != 0) NativeMethods.DestroyHintedParagraphFrame(frame);
                throw;
            }
        }
    }

    public NativeRendererStatus HitTest(float x, float y, out NativeTextHitTestResult result)
        => NativeTextInteractionInterop.HitTest(_boxes, x, y, out result);

    public NativeRendererStatus GetCaret(int position, bool trailingAffinity, out NativeTextCaretStop result)
        => NativeTextInteractionInterop.GetCaret(_carets, position, trailingAffinity, out result);

    internal static void ThrowForStatus(NativeRendererStatus status, string operation)
    {
        if (status == NativeRendererStatus.Success) return;
        if (status == NativeRendererStatus.Unsupported)
            throw new NotSupportedException($"Native hinted {operation} is unavailable or unsupported.");
        if (status == NativeRendererStatus.InvalidArgument)
            throw new ArgumentException($"Native hinted {operation} rejected the request.");
        throw new InvalidOperationException($"Native hinted {operation} failed with {status}.");
    }

    ~NativeHintedParagraph() => _owner?.Dispose();
    public void Dispose() { _owner.Dispose(); GC.SuppressFinalize(this); }
}

/// <summary>
/// Independently owned original flat glyph-frame data. Native pointers stay
/// private and are borrowed only under an exclusive originating-library lease.
/// </summary>
public sealed unsafe class NativeHintedParagraphFrame : IDisposable
{
    private readonly NativeTextContextOwner _owner = null!;
    private readonly GpuTexture _target;
    private readonly uint _viewGeneration;
    private readonly nuint _targetView;

    internal NativeHintedParagraphFrame(nint handle, GpuTexture target, float dpiScale,
        NativeHintedProjectionPolicy projection, NativeHintedCoverage coverage)
    {
        _target = target; _viewGeneration = target.ViewGeneration; _targetView = (nuint)target.ViewPtr;
        Width = target.Width; Height = target.Height; DpiScale = dpiScale;
        Projection = projection; Coverage = coverage;
        _owner = new NativeTextContextOwner(handle, NativeMethods.DestroyHintedParagraphFrame, nameof(NativeHintedParagraphFrame));
    }

    public uint Width { get; }
    public uint Height { get; }
    public float DpiScale { get; }
    public NativeHintedProjectionPolicy Projection { get; }
    public NativeHintedCoverage Coverage { get; }
    internal NativeTextContextOwner.Use Acquire() => _owner.Acquire();

    // Caller holds this owner's use and the target's existing RenderLock.
    internal void BorrowForRender(nint handle, GpuTexture target, out NativeMethods.GlyphFrame frame)
    {
        if (!ReferenceEquals(target, _target) || target.Width != Width || target.Height != Height ||
            target.ViewGeneration != _viewGeneration || (nuint)target.ViewPtr != _targetView)
            throw new ArgumentException("The prepared paragraph frame requires its exact original target and view.", nameof(target));
        frame = default;
        fixed (NativeMethods.GlyphFrame* output = &frame)
            NativeHintedParagraph.ThrowForStatus(NativeMethods.BorrowHintedParagraphFrame(handle, output), "paragraph frame borrow");
        if (frame.StructSize != (uint)sizeof(NativeMethods.GlyphFrame) || frame.Width != Width || frame.Height != Height ||
            frame.TargetView != _targetView || BitConverter.SingleToInt32Bits(frame.DpiScale) != BitConverter.SingleToInt32Bits(DpiScale) ||
            frame.Flags != 0 || frame.ContentRevision != 0 || frame.DrawState != null)
            throw new InvalidOperationException("Native paragraph frame changed its exact prepared target or execution contract.");
    }

    ~NativeHintedParagraphFrame() => _owner?.Dispose();
    public void Dispose() { _owner.Dispose(); GC.SuppressFinalize(this); }
}

public sealed unsafe partial class NativeTextShapingContext
{
    /// <summary>
    /// Formats the complete original styled paragraph once with explicit device
    /// configurations. Metrics and style scalars are native source snapshots;
    /// device axes address the supplied flat original-order 16.16 array.
    /// </summary>
    public NativeHintedParagraph LayoutHintedParagraph(in NativeTextShapeInput input,
        in NativeTextParagraphOptions options, ReadOnlySpan<NativeTextStyleRun> styles,
        ReadOnlySpan<NativeTextStyleMetrics> metrics, ReadOnlySpan<NativeHintedParagraphDeviceStyle> deviceStyles,
        ReadOnlySpan<int> variationCoordinates16_16 = default)
    {
        ValidateHintedShapeResources(in input);
        if (metrics.Length != styles.Length || deviceStyles.Length != styles.Length)
            throw new ArgumentException("Each original style requires one metric pair and one device configuration.");
        using var use = _owner.Acquire();
        nint paragraph = 0;
        try
        {
            fixed (NativeTextScalar* scalars = input.Input)
            fixed (NativeTextScalar* pre = input.PreContext)
            fixed (NativeTextScalar* post = input.PostContext)
            fixed (NativeTextFeature* features = input.Features)
            fixed (short* coordinates = input.NormalizedCoordinates)
            fixed (NativeTextStyleRun* styleData = styles)
            fixed (NativeTextStyleMetrics* metricData = metrics)
            fixed (NativeHintedParagraphDeviceStyle* deviceData = deviceStyles)
            fixed (int* variations = variationCoordinates16_16)
            {
                var shaping = NativeTextShapingInterop.CreateRequest(in input, null, scalars, pre, post,
                    features, coordinates, null, includeOwnedResources: false);
                var layout = CreateParagraphLayoutOptions(in input, in options);
                NativeTextParagraphResult result = new() { StructSize = (uint)sizeof(NativeTextParagraphResult) };
                NativeHintedParagraph.ThrowForStatus(NativeMethods.LayoutHintedParagraph(use.Handle, &shaping, &layout,
                    styleData, checked((uint)styles.Length), metricData, deviceData, checked((uint)deviceStyles.Length),
                    variations, checked((uint)variationCoordinates16_16.Length), &paragraph, &result), "paragraph layout");
            }
            if (paragraph == 0) throw new InvalidOperationException("Native hinted paragraph layout returned no owner.");
            return new NativeHintedParagraph(paragraph);
        }
        catch
        {
            if (paragraph != 0) NativeMethods.DestroyHintedParagraph(paragraph);
            throw;
        }
    }
}

internal static unsafe partial class NativeMethods
{
    internal partial struct HintedParagraphFormatBuffers { }
    internal partial struct HintedParagraphFrameRequest { }

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_text_context_layout_hinted_paragraph")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus LayoutHintedParagraph(nint context, NativeTextShapeRequest* shaping,
        NativeTextLayoutOptions* layout, NativeTextStyleRun* styles, uint styleCount, NativeTextStyleMetrics* metrics,
        NativeHintedParagraphDeviceStyle* deviceStyles, uint deviceStyleCount, int* variations, uint variationCount,
        nint* paragraph, NativeTextParagraphResult* result);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_paragraph_get_counts")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus GetHintedParagraphCounts(nint paragraph,
        NativeHintedParagraphCounts* counts, NativeTextParagraphResult* result);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_paragraph_copy_format")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus CopyHintedParagraphFormat(nint paragraph, HintedParagraphFormatBuffers* buffers);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_paragraph_copy_interaction")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus CopyHintedParagraphInteraction(nint paragraph,
        NativeTextClusterBox* boxes, uint boxCapacity, NativeTextCaretStop* carets, uint caretCapacity);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_paragraph_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyHintedParagraph(nint paragraph);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_paragraph_prepare_frame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus PrepareHintedParagraphFrame(nint paragraph,
        HintedParagraphFrameRequest* request, NativeColor* colors, uint colorCount, nint* frame);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_paragraph_frame_borrow")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial NativeRendererStatus BorrowHintedParagraphFrame(nint frame, GlyphFrame* wireFrame);

    [LibraryImport(LibraryName, EntryPoint = "progpu_native_hinted_paragraph_frame_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyHintedParagraphFrame(nint frame);
}
