using System.Runtime.CompilerServices;

namespace ProGPU.Backend.Native;

/// <summary>
/// Caller-owned inputs for one synchronous native OpenType shaping run.
/// No span is retained after a requirements or shape call returns.
/// </summary>
public readonly ref struct NativeTextShapeInput
{
    public NativeTextShapeInput(
        ReadOnlySpan<byte> fontData,
        ReadOnlySpan<NativeTextScalar> input,
        uint faceIndex = 0,
        NativeTextDirection direction = NativeTextDirection.Unspecified,
        uint unicodeScript = 0,
        uint language = 0,
        NativeTextClusterLevel clusterLevel = NativeTextClusterLevel.MonotoneGraphemes,
        NativeTextBufferFlags bufferFlags = NativeTextBufferFlags.None,
        ReadOnlySpan<NativeTextFeature> features = default,
        ReadOnlySpan<short> normalizedCoordinates = default,
        ReadOnlySpan<byte> normalizationData = default,
        ReadOnlySpan<NativeTextScalar> preContext = default,
        ReadOnlySpan<NativeTextScalar> postContext = default,
        NativeTextShapeFlags flags = NativeTextShapeFlags.ZeroMarkAdvances,
        uint alternateValue = 1)
    {
        FontData = fontData;
        Input = input;
        FaceIndex = faceIndex;
        Direction = direction;
        UnicodeScript = unicodeScript;
        Language = language;
        ClusterLevel = clusterLevel;
        BufferFlags = bufferFlags;
        Features = features;
        NormalizedCoordinates = normalizedCoordinates;
        NormalizationData = normalizationData;
        PreContext = preContext;
        PostContext = postContext;
        Flags = flags;
        AlternateValue = alternateValue;
    }

    public ReadOnlySpan<byte> FontData { get; }
    public ReadOnlySpan<NativeTextScalar> Input { get; }
    public uint FaceIndex { get; }
    public NativeTextDirection Direction { get; }
    public uint UnicodeScript { get; }
    public uint Language { get; }
    public NativeTextClusterLevel ClusterLevel { get; }
    public NativeTextBufferFlags BufferFlags { get; }
    public ReadOnlySpan<NativeTextFeature> Features { get; }
    public ReadOnlySpan<short> NormalizedCoordinates { get; }
    public ReadOnlySpan<byte> NormalizationData { get; }
    public ReadOnlySpan<NativeTextScalar> PreContext { get; }
    public ReadOnlySpan<NativeTextScalar> PostContext { get; }
    public NativeTextShapeFlags Flags { get; }
    public uint AlternateValue { get; }
}

/// <summary>
/// Batched, allocation-free C# binding for the ProGPU C++ text shaper.
/// </summary>
public static unsafe class NativeTextShapingInterop
{
    public static NativeRendererStatus GetRequirements(
        in NativeTextShapeInput input,
        out NativeTextShapeRequirements requirements)
    {
        requirements = new NativeTextShapeRequirements
        {
            StructSize = (uint)Unsafe.SizeOf<NativeTextShapeRequirements>()
        };
        fixed (byte* fontData = input.FontData)
        fixed (NativeTextScalar* scalars = input.Input)
        fixed (NativeTextScalar* preContext = input.PreContext)
        fixed (NativeTextScalar* postContext = input.PostContext)
        fixed (NativeTextFeature* features = input.Features)
        fixed (short* coordinates = input.NormalizedCoordinates)
        fixed (byte* normalizationData = input.NormalizationData)
        {
            NativeTextShapeRequest request = CreateRequest(
                in input,
                fontData,
                scalars,
                preContext,
                postContext,
                features,
                coordinates,
                normalizationData);
            return NativeMethods.GetTextShapeRequirements(
                &request,
                (NativeTextShapeRequirements*)Unsafe.AsPointer(ref requirements));
        }
    }

    public static NativeRendererStatus Shape(
        in NativeTextShapeInput input,
        Span<NativeTextShapingGlyph> glyphs,
        Span<byte> scratch,
        out NativeTextShapeResult result)
    {
        result = new NativeTextShapeResult
        {
            StructSize = (uint)Unsafe.SizeOf<NativeTextShapeResult>()
        };
        fixed (byte* fontData = input.FontData)
        fixed (NativeTextScalar* scalars = input.Input)
        fixed (NativeTextScalar* preContext = input.PreContext)
        fixed (NativeTextScalar* postContext = input.PostContext)
        fixed (NativeTextFeature* features = input.Features)
        fixed (short* coordinates = input.NormalizedCoordinates)
        fixed (byte* normalizationData = input.NormalizationData)
        fixed (NativeTextShapingGlyph* glyphOutput = glyphs)
        fixed (byte* scratchData = scratch)
        {
            NativeTextShapeRequest request = CreateRequest(
                in input,
                fontData,
                scalars,
                preContext,
                postContext,
                features,
                coordinates,
                normalizationData);
            return NativeMethods.ShapeText(
                &request,
                glyphOutput,
                checked((uint)glyphs.Length),
                scratchData,
                checked((nuint)scratch.Length),
                (NativeTextShapeResult*)Unsafe.AsPointer(ref result));
        }
    }

    internal static NativeTextShapeRequest CreateRequest(
        in NativeTextShapeInput input,
        byte* fontData,
        NativeTextScalar* scalars,
        NativeTextScalar* preContext,
        NativeTextScalar* postContext,
        NativeTextFeature* features,
        short* coordinates,
        byte* normalizationData,
        bool includeOwnedResources = true) => new()
    {
        StructSize = (uint)Unsafe.SizeOf<NativeTextShapeRequest>(),
        AbiVersion = NativeMethods.AbiVersion,
        FontData = includeOwnedResources ? (nuint)fontData : 0,
        FontSize = includeOwnedResources ? checked((nuint)input.FontData.Length) : 0,
        FaceIndex = input.FaceIndex,
        Flags = (uint)input.Flags,
        Input = (nuint)scalars,
        InputCount = checked((uint)input.Input.Length),
        PreContext = (nuint)preContext,
        PreContextCount = checked((uint)input.PreContext.Length),
        PostContext = (nuint)postContext,
        PostContextCount = checked((uint)input.PostContext.Length),
        Features = (nuint)features,
        FeatureCount = checked((uint)input.Features.Length),
        NormalizedCoordinates = (nuint)coordinates,
        NormalizedCoordinateCount = checked((uint)input.NormalizedCoordinates.Length),
        NormalizationData = includeOwnedResources ? (nuint)normalizationData : 0,
        NormalizationDataSize = includeOwnedResources
            ? checked((nuint)input.NormalizationData.Length)
            : 0,
        UnicodeScript = input.UnicodeScript,
        Language = input.Language,
        Direction = (uint)input.Direction,
        ClusterLevel = (uint)input.ClusterLevel,
        BufferFlags = (uint)input.BufferFlags,
        AlternateValue = input.AlternateValue
    };
}

public readonly record struct NativeTextParagraphOptions(
    float Scale,
    float MaximumWidth = 0,
    float LineHeight = 0,
    uint MaximumLines = 0,
    NativeTextTrimming Trimming = NativeTextTrimming.None,
    NativeTextAlignment Alignment = NativeTextAlignment.Left,
    uint EllipsisGlyphId = 0,
    float EllipsisAdvance = 0);

/// <summary>
/// Owns an immutable native font snapshot and reusable OpenType plan storage.
/// Create once per font/variation domain and reuse it for stable shaping and
/// complete bidi-aware paragraph layout runs.
/// </summary>
public sealed unsafe class NativeTextShapingContext : IDisposable
{
    private nint _handle;

    public NativeTextShapingContext(
        ReadOnlySpan<byte> fontData,
        uint faceIndex = 0,
        ReadOnlySpan<byte> normalizationData = default)
    {
        fixed (byte* fontPointer = fontData)
        fixed (byte* normalizationPointer = normalizationData)
        fixed (nint* handle = &_handle)
        {
            NativeRendererStatus status = NativeMethods.CreateTextContext(
                NativeMethods.AbiVersion,
                fontPointer,
                checked((nuint)fontData.Length),
                faceIndex,
                normalizationPointer,
                checked((nuint)normalizationData.Length),
                handle);
            if (status != NativeRendererStatus.Success)
            {
                throw new InvalidOperationException(
                    $"Native text context creation failed with {status}.");
            }
        }
    }

    ~NativeTextShapingContext() => DisposeCore();

    public NativeRendererStatus GetRequirements(
        in NativeTextShapeInput input,
        out NativeTextShapeRequirements requirements)
    {
        nint handle = GetHandle();
        requirements = new NativeTextShapeRequirements
        {
            StructSize = (uint)Unsafe.SizeOf<NativeTextShapeRequirements>()
        };
        fixed (NativeTextScalar* scalars = input.Input)
        fixed (NativeTextScalar* preContext = input.PreContext)
        fixed (NativeTextScalar* postContext = input.PostContext)
        fixed (NativeTextFeature* features = input.Features)
        fixed (short* coordinates = input.NormalizedCoordinates)
        {
            NativeTextShapeRequest request = NativeTextShapingInterop.CreateRequest(
                in input,
                null,
                scalars,
                preContext,
                postContext,
                features,
                coordinates,
                null,
                includeOwnedResources: false);
            return NativeMethods.GetTextContextShapeRequirements(
                handle,
                &request,
                (NativeTextShapeRequirements*)Unsafe.AsPointer(ref requirements));
        }
    }

    public NativeRendererStatus Shape(
        in NativeTextShapeInput input,
        Span<NativeTextShapingGlyph> glyphs,
        Span<byte> scratch,
        out NativeTextShapeResult result)
    {
        nint handle = GetHandle();
        result = new NativeTextShapeResult
        {
            StructSize = (uint)Unsafe.SizeOf<NativeTextShapeResult>()
        };
        fixed (NativeTextScalar* scalars = input.Input)
        fixed (NativeTextScalar* preContext = input.PreContext)
        fixed (NativeTextScalar* postContext = input.PostContext)
        fixed (NativeTextFeature* features = input.Features)
        fixed (short* coordinates = input.NormalizedCoordinates)
        fixed (NativeTextShapingGlyph* glyphOutput = glyphs)
        fixed (byte* scratchData = scratch)
        {
            NativeTextShapeRequest request = NativeTextShapingInterop.CreateRequest(
                in input,
                null,
                scalars,
                preContext,
                postContext,
                features,
                coordinates,
                null,
                includeOwnedResources: false);
            return NativeMethods.ShapeTextContext(
                handle,
                &request,
                glyphOutput,
                checked((uint)glyphs.Length),
                scratchData,
                checked((nuint)scratch.Length),
                (NativeTextShapeResult*)Unsafe.AsPointer(ref result));
        }
    }

    public NativeRendererStatus GetParagraphRequirements(
        in NativeTextShapeInput input,
        in NativeTextParagraphOptions options,
        out NativeTextParagraphRequirements requirements)
    {
        nint handle = GetHandle();
        requirements = new NativeTextParagraphRequirements
        {
            StructSize = (uint)Unsafe.SizeOf<NativeTextParagraphRequirements>()
        };
        fixed (NativeTextScalar* scalars = input.Input)
        fixed (NativeTextScalar* preContext = input.PreContext)
        fixed (NativeTextScalar* postContext = input.PostContext)
        fixed (NativeTextFeature* features = input.Features)
        fixed (short* coordinates = input.NormalizedCoordinates)
        {
            NativeTextShapeRequest shaping = NativeTextShapingInterop.CreateRequest(
                in input,
                null,
                scalars,
                preContext,
                postContext,
                features,
                coordinates,
                null,
                includeOwnedResources: false);
            NativeTextLayoutOptions layout = CreateParagraphLayoutOptions(
                in input, in options);
            return NativeMethods.GetTextContextParagraphRequirements(
                handle,
                &shaping,
                &layout,
                (NativeTextParagraphRequirements*)Unsafe.AsPointer(ref requirements));
        }
    }

    public NativeRendererStatus LayoutParagraph(
        in NativeTextShapeInput input,
        in NativeTextParagraphOptions options,
        Span<NativePositionedTextGlyph> glyphs,
        Span<NativePositionedTextLine> lines,
        Span<byte> scratch,
        out NativeTextParagraphResult result)
    {
        nint handle = GetHandle();
        result = new NativeTextParagraphResult
        {
            StructSize = (uint)Unsafe.SizeOf<NativeTextParagraphResult>()
        };
        fixed (NativeTextScalar* scalars = input.Input)
        fixed (NativeTextScalar* preContext = input.PreContext)
        fixed (NativeTextScalar* postContext = input.PostContext)
        fixed (NativeTextFeature* features = input.Features)
        fixed (short* coordinates = input.NormalizedCoordinates)
        fixed (NativePositionedTextGlyph* positioned = glyphs)
        fixed (NativePositionedTextLine* positionedLines = lines)
        fixed (byte* scratchData = scratch)
        {
            NativeTextShapeRequest shaping = NativeTextShapingInterop.CreateRequest(
                in input,
                null,
                scalars,
                preContext,
                postContext,
                features,
                coordinates,
                null,
                includeOwnedResources: false);
            NativeTextLayoutOptions layout = CreateParagraphLayoutOptions(
                in input, in options);
            return NativeMethods.LayoutTextContextParagraph(
                handle,
                &shaping,
                &layout,
                positioned,
                checked((uint)glyphs.Length),
                positionedLines,
                checked((uint)lines.Length),
                scratchData,
                checked((nuint)scratch.Length),
                (NativeTextParagraphResult*)Unsafe.AsPointer(ref result));
        }
    }

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private nint GetHandle()
    {
        nint handle = Volatile.Read(ref _handle);
        return handle != 0
            ? handle
            : throw new ObjectDisposedException(nameof(NativeTextShapingContext));
    }

    private static NativeTextLayoutOptions CreateParagraphLayoutOptions(
        in NativeTextShapeInput input,
        in NativeTextParagraphOptions options) => new()
    {
        StructSize = (uint)Unsafe.SizeOf<NativeTextLayoutOptions>(),
        Scale = options.Scale,
        MaximumWidth = options.MaximumWidth,
        LineHeight = options.LineHeight,
        MaximumLines = options.MaximumLines,
        Direction = (uint)input.Direction,
        Trimming = (uint)options.Trimming,
        Alignment = (uint)options.Alignment,
        EllipsisGlyphId = options.EllipsisGlyphId,
        EllipsisAdvance = options.EllipsisAdvance
    };

    private void DisposeCore()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) NativeMethods.DestroyTextContext(handle);
    }
}

public partial struct NativeTextScalar
{
    public NativeTextScalar(uint codePoint, uint inputIndex, ushort inputLength)
    {
        CodePoint = codePoint;
        InputIndex = inputIndex;
        InputLength = inputLength;
        CanonicalCombiningClass = 0;
        Reserved = 0;
        Script = 0;
    }
}

public partial struct NativeTextFeature
{
    public NativeTextFeature(uint tag, uint value = 1, uint start = 0, uint end = uint.MaxValue)
    {
        Tag = tag;
        Value = value;
        Start = start;
        End = end;
    }
}
