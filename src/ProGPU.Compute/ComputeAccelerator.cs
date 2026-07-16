using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.WebGPU;
using ProGPU.Backend;

namespace ProGPU.Compute;

public unsafe class ComputeAccelerator : IDisposable
{
    private readonly WgpuContext _context;
    private readonly RenderPipelineCache _cache;

    private ComputePipeline* _blurHorizPipeline;
    private ComputePipeline* _blurVertPipeline;
    private ComputePipeline* _shadowPipeline;
    private ComputePipeline* _shadowBlurHorizPipeline;
    private ComputePipeline* _shadowBlurVertPipeline;
    private ComputePipeline* _morphologyPipeline;
    private ComputePipeline* _imageBlendPipeline;
    private ComputePipeline* _colorTablePipeline;
    private ComputePipeline* _nonlinearColorFilterPipeline;
    private ComputePipeline* _arithmeticCompositePipeline;
    private ComputePipeline* _displacementMapPipeline;
    private ComputePipeline* _magnifierPipeline;
    private ComputePipeline* _matrixConvolutionPipeline;
    private ComputePipeline* _imageLightingPipeline;


    private bool _isDisposed;

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct ShadowParams
    {
        [FieldOffset(0)] public Vector2 Offset;
        [FieldOffset(16)] public Vector4 Color;
        [FieldOffset(32)] public float BlurRadius;
        [FieldOffset(36)] private float _padding;
        [FieldOffset(40)] private float _pad0;
        [FieldOffset(44)] private float _pad1;
        [FieldOffset(48)] private float _pad2;
        [FieldOffset(52)] private float _pad3;
        [FieldOffset(56)] private float _pad4;
        [FieldOffset(60)] private float _pad5;

        public ShadowParams(Vector2 offset, Vector4 color, float blurRadius)
        {
            Offset = offset;
            Color = color;
            BlurRadius = blurRadius;
            _padding = 0f;
            _pad0 = 0f;
            _pad1 = 0f;
            _pad2 = 0f;
            _pad3 = 0f;
            _pad4 = 0f;
            _pad5 = 0f;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct GaussianBlurParams
    {
        [FieldOffset(0)] public float Sigma;
        [FieldOffset(4)] public uint Radius;

        public GaussianBlurParams(float sigma)
        {
            Sigma = float.IsFinite(sigma) ? Math.Max(0f, sigma) : 0f;
            Radius = (uint)Math.Clamp((int)MathF.Ceiling(Sigma * 3f), 0, 128);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct MorphologyParams
    {
        [FieldOffset(0)] public int DirectionX;
        [FieldOffset(4)] public int DirectionY;
        [FieldOffset(8)] public uint Radius;
        [FieldOffset(12)] public uint Dilate;

        public MorphologyParams(int directionX, int directionY, uint radius, bool dilate)
        {
            DirectionX = directionX;
            DirectionY = directionY;
            Radius = radius;
            Dilate = dilate ? 1u : 0u;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct ImageBlendParams
    {
        [FieldOffset(0)] public uint Mode;
        [FieldOffset(4)] public uint LinearRgb;
        [FieldOffset(8)] private uint _padding0;
        [FieldOffset(12)] private uint _padding1;

        public ImageBlendParams(GpuBlendMode mode, bool linearRgb)
        {
            Mode = (uint)mode;
            LinearRgb = linearRgb ? 1u : 0u;
            _padding0 = 0u;
            _padding1 = 0u;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct ArithmeticCompositeParams
    {
        [FieldOffset(0)] public Vector4 Coefficients;
        [FieldOffset(16)] public uint EnforcePremultipliedColor;
        [FieldOffset(20)] private uint _padding0;
        [FieldOffset(24)] private uint _padding1;
        [FieldOffset(28)] private uint _padding2;

        public ArithmeticCompositeParams(
            float k1,
            float k2,
            float k3,
            float k4,
            bool enforcePremultipliedColor)
        {
            Coefficients = new Vector4(k1, k2, k3, k4);
            EnforcePremultipliedColor = enforcePremultipliedColor ? 1u : 0u;
            _padding0 = 0u;
            _padding1 = 0u;
            _padding2 = 0u;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 96)]
    public struct NonlinearColorFilterParams
    {
        private const float FloatMachineEpsilon = 1.1920929e-7f;

        [FieldOffset(0)] public Vector4 MatrixRed;
        [FieldOffset(16)] public Vector4 MatrixGreen;
        [FieldOffset(32)] public Vector4 MatrixBlue;
        [FieldOffset(48)] public Vector4 MatrixAlpha;
        [FieldOffset(64)] public Vector4 MatrixOffset;
        [FieldOffset(80)] public Vector4 Configuration;

        public NonlinearColorFilterParams(
            ReadOnlySpan<float> matrix,
            bool hsla,
            bool grayscale,
            uint invertStyle,
            float contrast)
        {
            if (hsla && matrix.Length != 20)
            {
                throw new ArgumentException("HSLA color matrices must contain 20 values.", nameof(matrix));
            }

            MatrixRed = hsla ? new Vector4(matrix[0], matrix[1], matrix[2], matrix[3]) : Vector4.Zero;
            MatrixGreen = hsla ? new Vector4(matrix[5], matrix[6], matrix[7], matrix[8]) : Vector4.Zero;
            MatrixBlue = hsla ? new Vector4(matrix[10], matrix[11], matrix[12], matrix[13]) : Vector4.Zero;
            MatrixAlpha = hsla ? new Vector4(matrix[15], matrix[16], matrix[17], matrix[18]) : Vector4.Zero;
            MatrixOffset = hsla ? new Vector4(matrix[4], matrix[9], matrix[14], matrix[19]) : Vector4.Zero;

            contrast = float.IsFinite(contrast) ? contrast : 0f;
            contrast = Math.Clamp(
                contrast,
                -1f + FloatMachineEpsilon,
                1f - FloatMachineEpsilon);
            var contrastScale = (1f + contrast) / (1f - contrast);
            Configuration = new Vector4(
                hsla ? 0f : 1f,
                grayscale ? 1f : 0f,
                Math.Min(invertStyle, 2u),
                contrastScale);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    public struct DisplacementMapParams
    {
        [FieldOffset(0)] public Vector4 Transform;
        [FieldOffset(16)] public uint XChannel;
        [FieldOffset(20)] public uint YChannel;
        [FieldOffset(24)] private uint _padding0;
        [FieldOffset(28)] private uint _padding1;

        public DisplacementMapParams(float scale, uint xChannel, uint yChannel)
            : this(new Vector4(scale, 0f, 0f, scale), xChannel, yChannel)
        {
        }

        public DisplacementMapParams(Vector4 transform, uint xChannel, uint yChannel)
        {
            Transform = IsFinite(transform) ? transform : Vector4.Zero;
            XChannel = Math.Min(xChannel, 3u);
            YChannel = Math.Min(yChannel, 3u);
            _padding0 = 0u;
            _padding1 = 0u;
        }

        private static bool IsFinite(Vector4 value) =>
            float.IsFinite(value.X) &&
            float.IsFinite(value.Y) &&
            float.IsFinite(value.Z) &&
            float.IsFinite(value.W);
    }

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    public struct MagnifierParams
    {
        [FieldOffset(0)] public Vector4 LensBounds;
        [FieldOffset(16)] public Vector4 OutputBounds;
        [FieldOffset(32)] public Vector4 ZoomTransform;
        [FieldOffset(48)] public Vector2 InverseInset;
        [FieldOffset(56)] public uint SamplingMode;
        [FieldOffset(60)] private uint _padding0;
        [FieldOffset(64)] public Vector2 Cubic;
        [FieldOffset(72)] private Vector2 _padding1;

        public MagnifierParams(
            Vector4 lensBounds,
            Vector4 outputBounds,
            Vector4 zoomTransform,
            Vector2 inverseInset,
            uint samplingMode,
            Vector2 cubic)
        {
            LensBounds = IsFinite(lensBounds) ? lensBounds : Vector4.Zero;
            OutputBounds = IsFinite(outputBounds) ? outputBounds : Vector4.Zero;
            ZoomTransform = IsFinite(zoomTransform) ? zoomTransform : Vector4.Zero;
            InverseInset = IsFinite(inverseInset)
                ? Vector2.Max(inverseInset, Vector2.Zero)
                : Vector2.Zero;
            SamplingMode = Math.Min(samplingMode, 2u);
            _padding0 = 0u;
            Cubic = IsFinite(cubic) ? cubic : Vector2.Zero;
            _padding1 = Vector2.Zero;
        }

        private static bool IsFinite(Vector2 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y);

        private static bool IsFinite(Vector4 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) &&
            float.IsFinite(value.Z) && float.IsFinite(value.W);
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public struct MatrixConvolutionParams
    {
        [FieldOffset(0)] public int KernelWidth;
        [FieldOffset(4)] public int KernelHeight;
        [FieldOffset(8)] public int KernelOffsetX;
        [FieldOffset(12)] public int KernelOffsetY;
        [FieldOffset(16)] public float Gain;
        [FieldOffset(20)] public float Bias;
        [FieldOffset(24)] public uint TileMode;
        [FieldOffset(28)] public uint ConvolveAlpha;
        [FieldOffset(32)] public int TileOriginX;
        [FieldOffset(36)] public int TileOriginY;
        [FieldOffset(40)] public int TileWidth;
        [FieldOffset(44)] public int TileHeight;

        public MatrixConvolutionParams(
            int kernelWidth,
            int kernelHeight,
            int kernelOffsetX,
            int kernelOffsetY,
            float gain,
            float bias,
            uint tileMode,
            bool convolveAlpha,
            int tileOriginX,
            int tileOriginY,
            int tileWidth,
            int tileHeight)
        {
            KernelWidth = kernelWidth;
            KernelHeight = kernelHeight;
            KernelOffsetX = kernelOffsetX;
            KernelOffsetY = kernelOffsetY;
            Gain = float.IsFinite(gain) ? gain : 0f;
            Bias = float.IsFinite(bias) ? bias : 0f;
            TileMode = Math.Min(tileMode, 3u);
            ConvolveAlpha = convolveAlpha ? 1u : 0u;
            TileOriginX = tileOriginX;
            TileOriginY = tileOriginY;
            TileWidth = Math.Max(tileWidth, 0);
            TileHeight = Math.Max(tileHeight, 0);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 80)]
    public struct ImageLightingParams
    {
        [FieldOffset(0)] public Vector4 LightPositionAndType;
        [FieldOffset(16)] public Vector4 LightTargetAndSpotExponent;
        [FieldOffset(32)] public Vector4 LightColor;
        [FieldOffset(48)] public Vector4 SurfaceParams;
        [FieldOffset(64)] public Vector4 ModeParams;

        public ImageLightingParams(
            Vector3 lightPosition,
            uint lightType,
            Vector3 lightTarget,
            float spotExponent,
            Vector4 lightColor,
            float surfaceScale,
            float lightingConstant,
            float shininess,
            float cutoffAngle,
            bool specular)
        {
            LightPositionAndType = new Vector4(lightPosition, Math.Min(lightType, 2u));
            LightTargetAndSpotExponent = new Vector4(
                lightTarget,
                float.IsFinite(spotExponent) ? Math.Max(0f, spotExponent) : 0f);
            LightColor = Vector4.Clamp(lightColor, Vector4.Zero, Vector4.One);
            SurfaceParams = new Vector4(
                float.IsFinite(surfaceScale) ? surfaceScale : 0f,
                float.IsFinite(lightingConstant) ? Math.Max(0f, lightingConstant) : 0f,
                float.IsFinite(shininess) ? Math.Clamp(shininess, 1f, 128f) : 1f,
                float.IsFinite(cutoffAngle) ? Math.Clamp(MathF.Abs(cutoffAngle), 0f, 90f) : 90f);
            ModeParams = new Vector4(specular ? 1f : 0f, 0f, 0f, 0f);
        }
    }



    public ComputeAccelerator(WgpuContext context)
    {
        _context = context;
        _cache = new RenderPipelineCache(_context);

        InitializePipelines();
    }

    private void InitializePipelines()
    {
        var shBlurH = _cache.GetOrCreateShader("BlurH", ComputeShaders.GaussianBlurHorizontal, "BlurHShader");
        _blurHorizPipeline = _cache.GetOrCreateComputePipeline("BlurH", shBlurH);

        var shBlurV = _cache.GetOrCreateShader("BlurV", ComputeShaders.GaussianBlurVertical, "BlurVShader");
        _blurVertPipeline = _cache.GetOrCreateComputePipeline("BlurV", shBlurV);

        var shShadow = _cache.GetOrCreateShader("Shadow", ComputeShaders.DropShadow, "ShadowShader");
        _shadowPipeline = _cache.GetOrCreateComputePipeline("Shadow", shShadow);

        var shShadowBlurH = _cache.GetOrCreateShader("ShadowBlurH", ComputeShaders.ShadowBlurHorizontal, "ShadowBlurHShader");
        _shadowBlurHorizPipeline = _cache.GetOrCreateComputePipeline("ShadowBlurH", shShadowBlurH);

        var shShadowBlurV = _cache.GetOrCreateShader("ShadowBlurV", ComputeShaders.ShadowBlurVertical, "ShadowBlurVShader");
        _shadowBlurVertPipeline = _cache.GetOrCreateComputePipeline("ShadowBlurV", shShadowBlurV);

        var morphologyShader = _cache.GetOrCreateShader("Morphology", ComputeShaders.Morphology, "MorphologyShader");
        _morphologyPipeline = _cache.GetOrCreateComputePipeline("Morphology", morphologyShader);

        var imageBlendShader = _cache.GetOrCreateShader("ImageBlend", ComputeShaders.ImageBlend, "ImageBlendShader");
        _imageBlendPipeline = _cache.GetOrCreateComputePipeline("ImageBlend", imageBlendShader);

        var colorTableShader = _cache.GetOrCreateShader("ColorTable", ComputeShaders.ColorTable, "ColorTableShader");
        _colorTablePipeline = _cache.GetOrCreateComputePipeline("ColorTable", colorTableShader);

        var arithmeticCompositeShader = _cache.GetOrCreateShader(
            "ArithmeticComposite",
            ComputeShaders.ArithmeticComposite,
            "ArithmeticCompositeShader");
        _arithmeticCompositePipeline = _cache.GetOrCreateComputePipeline(
            "ArithmeticComposite",
            arithmeticCompositeShader);

        var displacementMapShader = _cache.GetOrCreateShader(
            "DisplacementMap",
            ComputeShaders.DisplacementMap,
            "DisplacementMapShader");
        _displacementMapPipeline = _cache.GetOrCreateComputePipeline(
            "DisplacementMap",
            displacementMapShader);

        var matrixConvolutionShader = _cache.GetOrCreateShader(
            "MatrixConvolution",
            ComputeShaders.MatrixConvolution,
            "MatrixConvolutionShader");
        _matrixConvolutionPipeline = _cache.GetOrCreateComputePipeline(
            "MatrixConvolution",
            matrixConvolutionShader);

        var imageLightingShader = _cache.GetOrCreateShader(
            "ImageLighting",
            ComputeShaders.ImageLighting,
            "ImageLightingShader");
        _imageLightingPipeline = _cache.GetOrCreateComputePipeline(
            "ImageLighting",
            imageLightingShader);
    }

    private static void TrackBindGroupForRelease(Span<nint> bindGroupsToRelease, ref int count, BindGroup* bindGroup)
    {
        bindGroupsToRelease[count++] = (nint)bindGroup;
    }

    private void ReleaseBindGroups(ReadOnlySpan<nint> bindGroupsToRelease)
    {
        for (int i = 0; i < bindGroupsToRelease.Length; i++)
        {
            _context.Api.BindGroupRelease((BindGroup*)bindGroupsToRelease[i]);
        }
    }

    public void ApplyGaussianBlur(
        GpuTexture source,
        GpuTexture temp,
        GpuTexture destination,
        float sigmaX,
        float sigmaY)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));

        uint width = source.Width;
        uint height = source.Height;

        // Ensure temp and destination are resized to match source
        temp.Resize(width, height);
        destination.Resize(width, height);

        using var horizontalParams = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<GaussianBlurParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Gaussian Blur Horizontal Params");
        using var verticalParams = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<GaussianBlurParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Gaussian Blur Vertical Params");
        horizontalParams.WriteSingle(new GaussianBlurParams(sigmaX));
        verticalParams.WriteSingle(new GaussianBlurParams(sigmaY));

        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Blur Encoder") };
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        var blurHLayout = _context.Api.ComputePipelineGetBindGroupLayout(_blurHorizPipeline, 0);
        var blurVLayout = _context.Api.ComputePipelineGetBindGroupLayout(_blurVertPipeline, 0);

        Span<nint> bindGroupsToRelease = stackalloc nint[2];
        var bindGroupToReleaseCount = 0;

        RunShadowPass(
            encoder,
            _blurHorizPipeline,
            blurHLayout,
            source,
            temp,
            horizontalParams,
            width,
            height,
            bindGroupsToRelease,
            ref bindGroupToReleaseCount);
        RunShadowPass(
            encoder,
            _blurVertPipeline,
            blurVLayout,
            temp,
            destination,
            verticalParams,
            width,
            height,
            bindGroupsToRelease,
            ref bindGroupToReleaseCount);

        // Submit commands to queue
        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Blur Buffer") };
        var cmdBuffer = _context.Api.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Api.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        // Release resources
        _context.Api.CommandBufferRelease(cmdBuffer);
        _context.Api.CommandEncoderRelease(encoder);

        ReleaseBindGroups(bindGroupsToRelease[..bindGroupToReleaseCount]);

        _context.Api.BindGroupLayoutRelease(blurHLayout);
        _context.Api.BindGroupLayoutRelease(blurVLayout);
    }

    public void ApplyGaussianBlur(GpuTexture source, GpuTexture temp, GpuTexture destination, float sigma) =>
        ApplyGaussianBlur(source, temp, destination, sigma, sigma);

    public void ApplyMorphology(
        GpuTexture source,
        GpuTexture temp,
        GpuTexture destination,
        float radiusX,
        float radiusY,
        bool dilate)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));

        var horizontalRadius = (uint)Math.Clamp((int)MathF.Ceiling(radiusX), 0, 128);
        var verticalRadius = (uint)Math.Clamp((int)MathF.Ceiling(radiusY), 0, 128);
        if (horizontalRadius == 0 && verticalRadius == 0)
        {
            destination.CopyFrom(source);
            return;
        }

        var width = source.Width;
        var height = source.Height;
        temp.Resize(width, height);
        destination.Resize(width, height);

        using var horizontalParams = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<MorphologyParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Morphology Horizontal Params");
        using var verticalParams = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<MorphologyParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Morphology Vertical Params");
        horizontalParams.WriteSingle(new MorphologyParams(1, 0, horizontalRadius, dilate));
        verticalParams.WriteSingle(new MorphologyParams(0, 1, verticalRadius, dilate));

        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Morphology Encoder") };
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);
        var layout = _context.Api.ComputePipelineGetBindGroupLayout(_morphologyPipeline, 0);
        Span<nint> bindGroupsToRelease = stackalloc nint[2];
        var bindGroupToReleaseCount = 0;

        RunShadowPass(
            encoder,
            _morphologyPipeline,
            layout,
            source,
            temp,
            horizontalParams,
            width,
            height,
            bindGroupsToRelease,
            ref bindGroupToReleaseCount);
        RunShadowPass(
            encoder,
            _morphologyPipeline,
            layout,
            temp,
            destination,
            verticalParams,
            width,
            height,
            bindGroupsToRelease,
            ref bindGroupToReleaseCount);

        var commandDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Morphology Buffer") };
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandDesc);
        SilkMarshal.Free((nint)commandDesc.Label);
        _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);
        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
        ReleaseBindGroups(bindGroupsToRelease[..bindGroupToReleaseCount]);
        _context.Api.BindGroupLayoutRelease(layout);
    }

    public void ApplyImageLighting(
        GpuTexture source,
        GpuTexture destination,
        Vector3 lightPosition,
        uint lightType,
        Vector3 lightTarget,
        float spotExponent,
        Vector4 lightColor,
        float surfaceScale,
        float lightingConstant,
        float shininess,
        float cutoffAngle,
        bool specular)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));

        var width = source.Width;
        var height = source.Height;
        destination.Resize(width, height);
        using var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<ImageLightingParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Image Lighting Params");
        paramsBuffer.WriteSingle(new ImageLightingParams(
            lightPosition,
            lightType,
            lightTarget,
            spotExponent,
            lightColor,
            surfaceScale,
            lightingConstant,
            shininess,
            cutoffAngle,
            specular));

        var encoderDescriptor = new CommandEncoderDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Image Lighting Encoder")
        };
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        SilkMarshal.Free((nint)encoderDescriptor.Label);
        var layout = _context.Api.ComputePipelineGetBindGroupLayout(_imageLightingPipeline, 0);

        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = source.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = destination.ViewPtr };
        entries[2] = new BindGroupEntry
        {
            Binding = 2,
            Buffer = paramsBuffer.BufferPtr,
            Offset = 0,
            Size = paramsBuffer.Size
        };
        var bindGroupDescriptor = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 3,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &bindGroupDescriptor);

        var passDescriptor = new ComputePassDescriptor();
        var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
        _context.Api.ComputePassEncoderSetPipeline(pass, _imageLightingPipeline);
        _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(
            pass,
            (width + 15) / 16,
            (height + 15) / 16,
            1);
        _context.Api.ComputePassEncoderEnd(pass);
        _context.Api.ComputePassEncoderRelease(pass);

        var commandDescriptor = new CommandBufferDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Image Lighting Buffer")
        };
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        SilkMarshal.Free((nint)commandDescriptor.Label);
        _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);

        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
        _context.Api.BindGroupRelease(bindGroup);
        _context.Api.BindGroupLayoutRelease(layout);
    }

    public void ApplyMatrixConvolution(
        GpuTexture source,
        GpuTexture destination,
        int kernelWidth,
        int kernelHeight,
        ReadOnlySpan<float> kernel,
        float gain,
        float bias,
        int kernelOffsetX,
        int kernelOffsetY,
        uint tileMode,
        bool convolveAlpha,
        int tileOriginX,
        int tileOriginY,
        int tileWidth,
        int tileHeight)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));
        if (kernelWidth is <= 0 or > 64 || kernelHeight is <= 0 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(kernelWidth), "Convolution kernels must be between 1x1 and 64x64.");
        }

        var kernelLength = checked(kernelWidth * kernelHeight);
        if (kernel.Length < kernelLength)
        {
            throw new ArgumentException("The convolution kernel does not match its declared dimensions.", nameof(kernel));
        }

        var width = source.Width;
        var height = source.Height;
        destination.Resize(width, height);
        using var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<MatrixConvolutionParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Matrix Convolution Params");
        paramsBuffer.WriteSingle(new MatrixConvolutionParams(
            kernelWidth,
            kernelHeight,
            kernelOffsetX,
            kernelOffsetY,
            gain,
            bias,
            tileMode,
            convolveAlpha,
            tileOriginX,
            tileOriginY,
            tileWidth,
            tileHeight));
        using var kernelBuffer = new GpuBuffer(
            _context,
            (uint)(kernelLength * sizeof(float)),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "Matrix Convolution Kernel");
        kernelBuffer.Write(kernel[..kernelLength]);

        var encoderDescriptor = new CommandEncoderDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Matrix Convolution Encoder")
        };
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        SilkMarshal.Free((nint)encoderDescriptor.Label);
        var layout = _context.Api.ComputePipelineGetBindGroupLayout(_matrixConvolutionPipeline, 0);

        var entries = stackalloc BindGroupEntry[4];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = source.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = destination.ViewPtr };
        entries[2] = new BindGroupEntry
        {
            Binding = 2,
            Buffer = paramsBuffer.BufferPtr,
            Offset = 0,
            Size = paramsBuffer.Size
        };
        entries[3] = new BindGroupEntry
        {
            Binding = 3,
            Buffer = kernelBuffer.BufferPtr,
            Offset = 0,
            Size = kernelBuffer.Size
        };
        var bindGroupDescriptor = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 4,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &bindGroupDescriptor);

        var passDescriptor = new ComputePassDescriptor();
        var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
        _context.Api.ComputePassEncoderSetPipeline(pass, _matrixConvolutionPipeline);
        _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(
            pass,
            (width + 15) / 16,
            (height + 15) / 16,
            1);
        _context.Api.ComputePassEncoderEnd(pass);
        _context.Api.ComputePassEncoderRelease(pass);

        var commandDescriptor = new CommandBufferDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Matrix Convolution Buffer")
        };
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        SilkMarshal.Free((nint)commandDescriptor.Label);
        _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);

        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
        _context.Api.BindGroupRelease(bindGroup);
        _context.Api.BindGroupLayoutRelease(layout);
    }

    public void ApplyDisplacementMap(
        GpuTexture source,
        GpuTexture displacement,
        GpuTexture destination,
        float scale,
        uint xChannel,
        uint yChannel) =>
        ApplyDisplacementMap(
            source,
            displacement,
            destination,
            new Vector4(scale, 0f, 0f, scale),
            xChannel,
            yChannel);

    public void ApplyDisplacementMap(
        GpuTexture source,
        GpuTexture displacement,
        GpuTexture destination,
        Vector4 transform,
        uint xChannel,
        uint yChannel)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));
        var width = Math.Max(destination.Width, source.Width);
        var height = Math.Max(destination.Height, source.Height);
        destination.Resize(width, height);
        using var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<DisplacementMapParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Displacement Map Params");
        paramsBuffer.WriteSingle(new DisplacementMapParams(transform, xChannel, yChannel));

        var encoderDescriptor = new CommandEncoderDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Displacement Map Encoder")
        };
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        SilkMarshal.Free((nint)encoderDescriptor.Label);
        var layout = _context.Api.ComputePipelineGetBindGroupLayout(_displacementMapPipeline, 0);

        var entries = stackalloc BindGroupEntry[4];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = source.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = displacement.ViewPtr };
        entries[2] = new BindGroupEntry { Binding = 2, TextureView = destination.ViewPtr };
        entries[3] = new BindGroupEntry
        {
            Binding = 3,
            Buffer = paramsBuffer.BufferPtr,
            Offset = 0,
            Size = paramsBuffer.Size
        };
        var bindGroupDescriptor = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 4,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &bindGroupDescriptor);

        var passDescriptor = new ComputePassDescriptor();
        var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
        _context.Api.ComputePassEncoderSetPipeline(pass, _displacementMapPipeline);
        _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(
            pass,
            (width + 15) / 16,
            (height + 15) / 16,
            1);
        _context.Api.ComputePassEncoderEnd(pass);
        _context.Api.ComputePassEncoderRelease(pass);

        var commandDescriptor = new CommandBufferDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Displacement Map Buffer")
        };
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        SilkMarshal.Free((nint)commandDescriptor.Label);
        _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);

        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
        _context.Api.BindGroupRelease(bindGroup);
        _context.Api.BindGroupLayoutRelease(layout);
    }

    public void ApplyMagnifier(
        GpuTexture source,
        GpuTexture destination,
        Vector4 lensBounds,
        Vector4 outputBounds,
        Vector4 zoomTransform,
        Vector2 inverseInset,
        uint samplingMode,
        Vector2 cubic)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));
        var pipeline = GetOrCreateMagnifierPipeline();
        var width = source.Width;
        var height = source.Height;
        destination.Resize(width, height);
        using var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<MagnifierParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Magnifier Params");
        paramsBuffer.WriteSingle(new MagnifierParams(
            lensBounds,
            outputBounds,
            zoomTransform,
            inverseInset,
            samplingMode,
            cubic));

        var encoderDescriptor = new CommandEncoderDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Magnifier Encoder")
        };
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        SilkMarshal.Free((nint)encoderDescriptor.Label);
        var layout = _context.Api.ComputePipelineGetBindGroupLayout(pipeline, 0);

        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = source.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = destination.ViewPtr };
        entries[2] = new BindGroupEntry
        {
            Binding = 2,
            Buffer = paramsBuffer.BufferPtr,
            Offset = 0,
            Size = paramsBuffer.Size
        };
        var bindGroupDescriptor = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 3,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &bindGroupDescriptor);

        var passDescriptor = new ComputePassDescriptor();
        var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
        _context.Api.ComputePassEncoderSetPipeline(pass, pipeline);
        _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(
            pass,
            (width + 15) / 16,
            (height + 15) / 16,
            1);
        _context.Api.ComputePassEncoderEnd(pass);
        _context.Api.ComputePassEncoderRelease(pass);

        var commandDescriptor = new CommandBufferDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Magnifier Buffer")
        };
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        SilkMarshal.Free((nint)commandDescriptor.Label);
        _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);

        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
        _context.Api.BindGroupRelease(bindGroup);
        _context.Api.BindGroupLayoutRelease(layout);
    }

    private ComputePipeline* GetOrCreateMagnifierPipeline()
    {
        if (_magnifierPipeline != null)
        {
            return _magnifierPipeline;
        }

        var shader = _cache.GetOrCreateShader(
            "Magnifier",
            ComputeShaders.Magnifier,
            "MagnifierShader");
        _magnifierPipeline = _cache.GetOrCreateComputePipeline("Magnifier", shader);
        return _magnifierPipeline;
    }

    public void ApplyArithmeticComposite(
        GpuTexture background,
        GpuTexture foreground,
        GpuTexture destination,
        float k1,
        float k2,
        float k3,
        float k4,
        bool enforcePremultipliedColor)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));
        var width = Math.Max(destination.Width, Math.Max(background.Width, foreground.Width));
        var height = Math.Max(destination.Height, Math.Max(background.Height, foreground.Height));
        destination.Resize(width, height);
        using var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<ArithmeticCompositeParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Arithmetic Composite Params");
        paramsBuffer.WriteSingle(new ArithmeticCompositeParams(
            k1,
            k2,
            k3,
            k4,
            enforcePremultipliedColor));

        var encoderDescriptor = new CommandEncoderDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Arithmetic Composite Encoder")
        };
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        SilkMarshal.Free((nint)encoderDescriptor.Label);
        var layout = _context.Api.ComputePipelineGetBindGroupLayout(_arithmeticCompositePipeline, 0);

        var entries = stackalloc BindGroupEntry[4];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = background.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = foreground.ViewPtr };
        entries[2] = new BindGroupEntry { Binding = 2, TextureView = destination.ViewPtr };
        entries[3] = new BindGroupEntry
        {
            Binding = 3,
            Buffer = paramsBuffer.BufferPtr,
            Offset = 0,
            Size = paramsBuffer.Size
        };
        var bindGroupDescriptor = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 4,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &bindGroupDescriptor);

        var passDescriptor = new ComputePassDescriptor();
        var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
        _context.Api.ComputePassEncoderSetPipeline(pass, _arithmeticCompositePipeline);
        _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(
            pass,
            (width + 15) / 16,
            (height + 15) / 16,
            1);
        _context.Api.ComputePassEncoderEnd(pass);
        _context.Api.ComputePassEncoderRelease(pass);

        var commandDescriptor = new CommandBufferDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Arithmetic Composite Buffer")
        };
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        SilkMarshal.Free((nint)commandDescriptor.Label);
        _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);

        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
        _context.Api.BindGroupRelease(bindGroup);
        _context.Api.BindGroupLayoutRelease(layout);
    }

    public void ApplyImageBlend(
        GpuTexture background,
        GpuTexture foreground,
        GpuTexture destination,
        GpuBlendMode blendMode,
        bool linearRgb)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));
        var width = Math.Max(destination.Width, Math.Max(background.Width, foreground.Width));
        var height = Math.Max(destination.Height, Math.Max(background.Height, foreground.Height));
        destination.Resize(width, height);
        using var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<ImageBlendParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Image Blend Params");
        paramsBuffer.WriteSingle(new ImageBlendParams(blendMode, linearRgb));

        var encoderDescriptor = new CommandEncoderDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Image Blend Encoder")
        };
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        SilkMarshal.Free((nint)encoderDescriptor.Label);
        var layout = _context.Api.ComputePipelineGetBindGroupLayout(_imageBlendPipeline, 0);

        var entries = stackalloc BindGroupEntry[4];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = background.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = foreground.ViewPtr };
        entries[2] = new BindGroupEntry { Binding = 2, TextureView = destination.ViewPtr };
        entries[3] = new BindGroupEntry
        {
            Binding = 3,
            Buffer = paramsBuffer.BufferPtr,
            Offset = 0,
            Size = paramsBuffer.Size
        };
        var bindGroupDescriptor = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 4,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &bindGroupDescriptor);

        var passDescriptor = new ComputePassDescriptor();
        var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
        _context.Api.ComputePassEncoderSetPipeline(pass, _imageBlendPipeline);
        _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(
            pass,
            (width + 15) / 16,
            (height + 15) / 16,
            1);
        _context.Api.ComputePassEncoderEnd(pass);
        _context.Api.ComputePassEncoderRelease(pass);

        var commandDescriptor = new CommandBufferDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Image Blend Buffer")
        };
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        SilkMarshal.Free((nint)commandDescriptor.Label);
        _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);

        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
        _context.Api.BindGroupRelease(bindGroup);
        _context.Api.BindGroupLayoutRelease(layout);
    }

    public void ApplyColorTable(
        GpuTexture source,
        GpuTexture destination,
        ReadOnlySpan<byte> alpha,
        ReadOnlySpan<byte> red,
        ReadOnlySpan<byte> green,
        ReadOnlySpan<byte> blue)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));
        if (alpha.Length < 256 || red.Length < 256 || green.Length < 256 || blue.Length < 256)
        {
            throw new ArgumentException("Color filter tables must contain 256 entries.");
        }

        var width = source.Width;
        var height = source.Height;
        destination.Resize(width, height);

        Span<uint> packedTables = stackalloc uint[1024];
        for (var i = 0; i < 256; i++)
        {
            packedTables[i] = red[i];
            packedTables[256 + i] = green[i];
            packedTables[512 + i] = blue[i];
            packedTables[768 + i] = alpha[i];
        }

        using var tablesBuffer = new GpuBuffer(
            _context,
            (uint)(packedTables.Length * sizeof(uint)),
            BufferUsage.Storage | BufferUsage.CopyDst,
            "Color Table Values");
        tablesBuffer.Write(packedTables);

        var encoderDescriptor = new CommandEncoderDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Color Table Encoder")
        };
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        SilkMarshal.Free((nint)encoderDescriptor.Label);
        var layout = _context.Api.ComputePipelineGetBindGroupLayout(_colorTablePipeline, 0);

        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = source.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = destination.ViewPtr };
        entries[2] = new BindGroupEntry
        {
            Binding = 2,
            Buffer = tablesBuffer.BufferPtr,
            Offset = 0,
            Size = tablesBuffer.Size
        };
        var bindGroupDescriptor = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 3,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &bindGroupDescriptor);

        var passDescriptor = new ComputePassDescriptor();
        var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
        _context.Api.ComputePassEncoderSetPipeline(pass, _colorTablePipeline);
        _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(
            pass,
            (width + 15) / 16,
            (height + 15) / 16,
            1);
        _context.Api.ComputePassEncoderEnd(pass);
        _context.Api.ComputePassEncoderRelease(pass);

        var commandDescriptor = new CommandBufferDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Color Table Buffer")
        };
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        SilkMarshal.Free((nint)commandDescriptor.Label);
        _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);

        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
        _context.Api.BindGroupRelease(bindGroup);
        _context.Api.BindGroupLayoutRelease(layout);
    }

    public void ApplyNonlinearColorFilter(
        GpuTexture source,
        GpuTexture destination,
        ReadOnlySpan<float> matrix,
        bool hsla,
        bool grayscale,
        uint invertStyle,
        float contrast)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));

        var width = source.Width;
        var height = source.Height;
        destination.Resize(width, height);
        var pipeline = GetOrCreateNonlinearColorFilterPipeline();
        using var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<NonlinearColorFilterParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Nonlinear Color Filter Params");
        paramsBuffer.WriteSingle(new NonlinearColorFilterParams(
            matrix,
            hsla,
            grayscale,
            invertStyle,
            contrast));

        var encoderDescriptor = new CommandEncoderDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Nonlinear Color Filter Encoder")
        };
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        SilkMarshal.Free((nint)encoderDescriptor.Label);
        var layout = _context.Api.ComputePipelineGetBindGroupLayout(pipeline, 0);

        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = source.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = destination.ViewPtr };
        entries[2] = new BindGroupEntry
        {
            Binding = 2,
            Buffer = paramsBuffer.BufferPtr,
            Offset = 0,
            Size = paramsBuffer.Size
        };
        var bindGroupDescriptor = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 3,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &bindGroupDescriptor);

        var passDescriptor = new ComputePassDescriptor();
        var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDescriptor);
        _context.Api.ComputePassEncoderSetPipeline(pass, pipeline);
        _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bindGroup, 0, null);
        _context.Api.ComputePassEncoderDispatchWorkgroups(
            pass,
            (width + 15) / 16,
            (height + 15) / 16,
            1);
        _context.Api.ComputePassEncoderEnd(pass);
        _context.Api.ComputePassEncoderRelease(pass);

        var commandDescriptor = new CommandBufferDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Nonlinear Color Filter Buffer")
        };
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        SilkMarshal.Free((nint)commandDescriptor.Label);
        _context.Api.QueueSubmit(_context.Queue, 1, &commandBuffer);

        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
        _context.Api.BindGroupRelease(bindGroup);
        _context.Api.BindGroupLayoutRelease(layout);
    }

    private ComputePipeline* GetOrCreateNonlinearColorFilterPipeline()
    {
        if (_nonlinearColorFilterPipeline != null)
        {
            return _nonlinearColorFilterPipeline;
        }

        var shader = _cache.GetOrCreateShader(
            "NonlinearColorFilter",
            ComputeShaders.NonlinearColorFilter,
            "NonlinearColorFilterShader");
        _nonlinearColorFilterPipeline = _cache.GetOrCreateComputePipeline(
            "NonlinearColorFilter",
            shader);
        return _nonlinearColorFilterPipeline;
    }

    private void RunShadowPass(
        CommandEncoder* encoder,
        ComputePipeline* pipeline,
        BindGroupLayout* layout,
        GpuTexture input,
        GpuTexture output,
        GpuBuffer paramsBuffer,
        uint width,
        uint height,
        Span<nint> bindGroupsToRelease,
        ref int bindGroupToReleaseCount)
    {
        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = input.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = output.ViewPtr };
        entries[2] = new BindGroupEntry { Binding = 2, Buffer = paramsBuffer.BufferPtr, Offset = 0, Size = paramsBuffer.Size };

        var bgDesc = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 3,
            Entries = entries
        };
        var bg = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDesc);
        TrackBindGroupForRelease(bindGroupsToRelease, ref bindGroupToReleaseCount, bg);

        var passDesc = new ComputePassDescriptor();
        var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDesc);
        _context.Api.ComputePassEncoderSetPipeline(pass, pipeline);
        _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);

        uint workgroupX = (width + 15) / 16;
        uint workgroupY = (height + 15) / 16;
        _context.Api.ComputePassEncoderDispatchWorkgroups(pass, workgroupX, workgroupY, 1);

        _context.Api.ComputePassEncoderEnd(pass);
        _context.Api.ComputePassEncoderRelease(pass);
    }

    private void RunSharpDropShadow(GpuTexture source, GpuTexture destination, Vector2 offset, Vector4 shadowColor, float blurRadius)
    {
        var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<ShadowParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Shadow Params Buffer"
        );
        paramsBuffer.WriteSingle(new ShadowParams(offset, shadowColor, blurRadius));

        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Shadow Encoder") };
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        var shadowLayout = _context.Api.ComputePipelineGetBindGroupLayout(_shadowPipeline, 0);

        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = source.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = destination.ViewPtr };
        entries[2] = new BindGroupEntry { Binding = 2, Buffer = paramsBuffer.BufferPtr, Offset = 0, Size = paramsBuffer.Size };

        var bgDesc = new BindGroupDescriptor
        {
            Layout = shadowLayout,
            EntryCount = 3,
            Entries = entries
        };
        var bg = _context.Api.DeviceCreateBindGroup(_context.Device, &bgDesc);

        var passDesc = new ComputePassDescriptor();
        var pass = _context.Api.CommandEncoderBeginComputePass(encoder, &passDesc);

        _context.Api.ComputePassEncoderSetPipeline(pass, _shadowPipeline);
        _context.Api.ComputePassEncoderSetBindGroup(pass, 0, bg, 0, null);

        uint workgroupX = (source.Width + 15) / 16;
        uint workgroupY = (source.Height + 15) / 16;
        _context.Api.ComputePassEncoderDispatchWorkgroups(pass, workgroupX, workgroupY, 1);

        _context.Api.ComputePassEncoderEnd(pass);
        _context.Api.ComputePassEncoderRelease(pass);

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Shadow Buffer") };
        var cmdBuffer = _context.Api.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Api.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Api.CommandBufferRelease(cmdBuffer);
        _context.Api.CommandEncoderRelease(encoder);
        _context.Api.BindGroupRelease(bg);
        _context.Api.BindGroupLayoutRelease(shadowLayout);
        paramsBuffer.Dispose();
    }

    public void ApplyDropShadow(GpuTexture source, GpuTexture temp, GpuTexture destination, Vector2 offset, Vector4 shadowColor, float blurRadius)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));

        float snappedBlurRadius = MathF.Round(blurRadius * 2f) / 2f;

        uint width = source.Width;
        uint height = source.Height;

        temp.Resize(width, height);
        destination.Resize(width, height);

        if (snappedBlurRadius <= 0.01f)
        {
            RunSharpDropShadow(source, destination, offset, shadowColor, snappedBlurRadius);
            return;
        }

        var encoderDesc = new CommandEncoderDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Shadow Encoder") };
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        SilkMarshal.Free((nint)encoderDesc.Label);

        var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<ShadowParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Shadow Params Buffer"
        );
        paramsBuffer.WriteSingle(new ShadowParams(offset, shadowColor, snappedBlurRadius));

        var shadowHLayout = _context.Api.ComputePipelineGetBindGroupLayout(_shadowBlurHorizPipeline, 0);
        var shadowVLayout = _context.Api.ComputePipelineGetBindGroupLayout(_shadowBlurVertPipeline, 0);

        Span<nint> bindGroupsToRelease = stackalloc nint[2];
        var bindGroupToReleaseCount = 0;

        RunShadowPass(encoder, _shadowBlurHorizPipeline, shadowHLayout, source, temp, paramsBuffer, width, height, bindGroupsToRelease, ref bindGroupToReleaseCount);
        RunShadowPass(encoder, _shadowBlurVertPipeline, shadowVLayout, temp, destination, paramsBuffer, width, height, bindGroupsToRelease, ref bindGroupToReleaseCount);

        var cmdDesc = new CommandBufferDescriptor { Label = (byte*)SilkMarshal.StringToPtr("Compute Shadow Buffer") };
        var cmdBuffer = _context.Api.CommandEncoderFinish(encoder, &cmdDesc);
        SilkMarshal.Free((nint)cmdDesc.Label);

        _context.Api.QueueSubmit(_context.Queue, 1, &cmdBuffer);

        _context.Api.CommandBufferRelease(cmdBuffer);
        _context.Api.CommandEncoderRelease(encoder);

        ReleaseBindGroups(bindGroupsToRelease[..bindGroupToReleaseCount]);

        _context.Api.BindGroupLayoutRelease(shadowHLayout);
        _context.Api.BindGroupLayoutRelease(shadowVLayout);
        paramsBuffer.Dispose();
    }



    public void Dispose()
    {
        if (_isDisposed) return;

        _cache.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
