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
    private ComputePipeline* _combinedBlurHorizPipeline;
    private ComputePipeline* _combinedBlurVertPipeline;
    private ComputePipeline* _morphologyPipeline;
    private ComputePipeline* _imageBlendPipeline;
    private ComputePipeline* _colorTablePipeline;
    private ComputePipeline* _nonlinearColorFilterPipeline;
    private ComputePipeline* _overdrawColorFilterPipeline;
    private ComputePipeline* _arithmeticCompositePipeline;
    private ComputePipeline* _displacementMapPipeline;
    private ComputePipeline* _magnifierPipeline;
    private ComputePipeline* _matrixConvolutionPipeline;
    private ComputePipeline* _imageLightingPipeline;

    private BindGroupLayout* _blurHorizLayout;
    private BindGroupLayout* _blurVertLayout;
    private BindGroupLayout* _shadowBlurHorizLayout;
    private BindGroupLayout* _shadowBlurVertLayout;
    private BindGroupLayout* _sharpShadowLayout;
    private BindGroupLayout* _combinedBlurHorizLayout;
    private BindGroupLayout* _combinedBlurVertLayout;
    private GpuBuffer? _blurHorizontalParams;
    private GpuBuffer? _blurVerticalParams;
    private GpuBuffer? _shadowParams;
    private GpuBuffer? _sharpShadowParams;
    private GpuBuffer? _combinedBlurParams;
    private GpuTexture? _combinedShadowTemporary;
    private CachedPassBinding _blurHorizontalBinding;
    private CachedPassBinding _blurVerticalBinding;
    private CachedPassBinding _shadowHorizontalBinding;
    private CachedPassBinding _shadowVerticalBinding;
    private CachedPassBinding _sharpShadowBinding;
    private CachedCombinedHorizontalBinding _combinedHorizontalBinding;
    private CachedCombinedVerticalBinding _combinedVerticalBinding;


    private bool _isDisposed;

    private struct CachedPassBinding
    {
        public BindGroup* BindGroup;
        public ulong InputId;
        public uint InputGeneration;
        public ulong OutputId;
        public uint OutputGeneration;

        public readonly bool Matches(GpuTexture input, GpuTexture output) =>
            BindGroup != null &&
            InputId == input.Id &&
            InputGeneration == input.Generation &&
            OutputId == output.Id &&
            OutputGeneration == output.Generation;

        public void Set(BindGroup* bindGroup, GpuTexture input, GpuTexture output)
        {
            BindGroup = bindGroup;
            InputId = input.Id;
            InputGeneration = input.Generation;
            OutputId = output.Id;
            OutputGeneration = output.Generation;
        }
    }

    private struct CachedCombinedHorizontalBinding
    {
        public BindGroup* BindGroup;
        public ulong SourceId;
        public uint SourceGeneration;
        public ulong BlurOutputId;
        public uint BlurOutputGeneration;
        public ulong ShadowOutputId;
        public uint ShadowOutputGeneration;

        public readonly bool Matches(GpuTexture source, GpuTexture blurOutput, GpuTexture shadowOutput) =>
            BindGroup != null &&
            SourceId == source.Id && SourceGeneration == source.Generation &&
            BlurOutputId == blurOutput.Id && BlurOutputGeneration == blurOutput.Generation &&
            ShadowOutputId == shadowOutput.Id && ShadowOutputGeneration == shadowOutput.Generation;
    }

    private struct CachedCombinedVerticalBinding
    {
        public BindGroup* BindGroup;
        public ulong BlurInputId;
        public uint BlurInputGeneration;
        public ulong ShadowInputId;
        public uint ShadowInputGeneration;
        public ulong BlurOutputId;
        public uint BlurOutputGeneration;
        public ulong ShadowOutputId;
        public uint ShadowOutputGeneration;

        public readonly bool Matches(
            GpuTexture blurInput,
            GpuTexture shadowInput,
            GpuTexture blurOutput,
            GpuTexture shadowOutput) =>
            BindGroup != null &&
            BlurInputId == blurInput.Id && BlurInputGeneration == blurInput.Generation &&
            ShadowInputId == shadowInput.Id && ShadowInputGeneration == shadowInput.Generation &&
            BlurOutputId == blurOutput.Id && BlurOutputGeneration == blurOutput.Generation &&
            ShadowOutputId == shadowOutput.Id && ShadowOutputGeneration == shadowOutput.Generation;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct ShadowParams
    {
        [FieldOffset(0)] public Vector2 Offset;
        [FieldOffset(16)] public Vector4 Color;
        [FieldOffset(32)] public float BlurRadius;
        [FieldOffset(36)] private float _padding;
        [FieldOffset(40)] public Vector2 SourceSize;
        [FieldOffset(48)] private Vector4 _padding1;

        public ShadowParams(
            Vector2 offset,
            Vector4 color,
            float blurRadius,
            uint sourceWidth = 0,
            uint sourceHeight = 0)
        {
            Offset = offset;
            Color = color;
            BlurRadius = blurRadius;
            _padding = 0f;
            SourceSize = new Vector2(sourceWidth, sourceHeight);
            _padding1 = Vector4.Zero;
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

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public struct CombinedBlurParams
    {
        [FieldOffset(0)] public Vector2 Offset;
        [FieldOffset(8)] private Vector2 _padding0;
        [FieldOffset(16)] public Vector4 Color;
        [FieldOffset(32)] public float Sigma;
        [FieldOffset(36)] public uint Radius;
        [FieldOffset(40)] private Vector2 _padding1;

        public CombinedBlurParams(Vector2 offset, Vector4 color, float sigma)
        {
            Offset = offset;
            _padding0 = Vector2.Zero;
            Color = color;
            Sigma = float.IsFinite(sigma) ? Math.Max(0f, sigma) : 0f;
            Radius = (uint)Math.Clamp((int)MathF.Ceiling(Sigma * 3f), 0, 64);
            _padding1 = Vector2.Zero;
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

    [StructLayout(LayoutKind.Explicit, Size = 96)]
    public struct OverdrawColorFilterParams
    {
        [FieldOffset(0)] public Vector4 Color0;
        [FieldOffset(16)] public Vector4 Color1;
        [FieldOffset(32)] public Vector4 Color2;
        [FieldOffset(48)] public Vector4 Color3;
        [FieldOffset(64)] public Vector4 Color4;
        [FieldOffset(80)] public Vector4 Color5;

        public OverdrawColorFilterParams(ReadOnlySpan<Vector4> colors)
        {
            if (colors.Length != 6)
            {
                throw new ArgumentException(
                    "Overdraw filters require exactly six colors.",
                    nameof(colors));
            }

            Color0 = colors[0];
            Color1 = colors[1];
            Color2 = colors[2];
            Color3 = colors[3];
            Color4 = colors[4];
            Color5 = colors[5];
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
    }

    public int CachedEffectShaderCount => _cache.ShaderCount;

    public int CachedEffectPipelineCount => _cache.ComputePipelineCount;

    public ulong PersistentEffectParameterBufferBytes =>
        (_blurHorizontalParams?.AllocatedSize ?? 0u) +
        (_blurVerticalParams?.AllocatedSize ?? 0u) +
        (_shadowParams?.AllocatedSize ?? 0u) +
        (_sharpShadowParams?.AllocatedSize ?? 0u) +
        (_combinedBlurParams?.AllocatedSize ?? 0u);

    private void EnsureGaussianBlurResources()
    {
        if (_blurHorizPipeline != null)
        {
            return;
        }

        var shBlurH = _cache.GetOrCreateShader("BlurH", ComputeShaders.GaussianBlurHorizontal, "BlurHShader");
        _blurHorizPipeline = _cache.GetOrCreateComputePipeline("BlurH", shBlurH);

        var shBlurV = _cache.GetOrCreateShader("BlurV", ComputeShaders.GaussianBlurVertical, "BlurVShader");
        _blurVertPipeline = _cache.GetOrCreateComputePipeline("BlurV", shBlurV);

        _blurHorizLayout = _context.Api.ComputePipelineGetBindGroupLayout(_blurHorizPipeline, 0);
        _blurVertLayout = _context.Api.ComputePipelineGetBindGroupLayout(_blurVertPipeline, 0);
        _blurHorizontalParams = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<GaussianBlurParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Gaussian Blur Horizontal Params");
        _blurVerticalParams = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<GaussianBlurParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Gaussian Blur Vertical Params");
    }

    private void EnsureSharpShadowPipeline()
    {
        if (_shadowPipeline != null)
        {
            return;
        }

        var shShadow = _cache.GetOrCreateShader("Shadow", ComputeShaders.DropShadow, "ShadowShader");
        _shadowPipeline = _cache.GetOrCreateComputePipeline("Shadow", shShadow);
        _sharpShadowLayout = _context.Api.ComputePipelineGetBindGroupLayout(_shadowPipeline, 0);
        _sharpShadowParams = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<ShadowParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Sharp Shadow Params Buffer");
    }

    private void EnsureShadowBlurResources()
    {
        if (_shadowBlurHorizPipeline != null)
        {
            return;
        }

        var shShadowBlurH = _cache.GetOrCreateShader("ShadowBlurH", ComputeShaders.ShadowBlurHorizontal, "ShadowBlurHShader");
        _shadowBlurHorizPipeline = _cache.GetOrCreateComputePipeline("ShadowBlurH", shShadowBlurH);

        var shShadowBlurV = _cache.GetOrCreateShader("ShadowBlurV", ComputeShaders.ShadowBlurVertical, "ShadowBlurVShader");
        _shadowBlurVertPipeline = _cache.GetOrCreateComputePipeline("ShadowBlurV", shShadowBlurV);

        _shadowBlurHorizLayout = _context.Api.ComputePipelineGetBindGroupLayout(_shadowBlurHorizPipeline, 0);
        _shadowBlurVertLayout = _context.Api.ComputePipelineGetBindGroupLayout(_shadowBlurVertPipeline, 0);
        _shadowParams = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<ShadowParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Shadow Params Buffer");
    }

    private ComputePipeline* GetOrCreateMorphologyPipeline()
    {
        if (_morphologyPipeline != null)
        {
            return _morphologyPipeline;
        }

        var morphologyShader = _cache.GetOrCreateShader("Morphology", ComputeShaders.Morphology, "MorphologyShader");
        _morphologyPipeline = _cache.GetOrCreateComputePipeline("Morphology", morphologyShader);
        return _morphologyPipeline;
    }

    private ComputePipeline* GetOrCreateImageBlendPipeline()
    {
        if (_imageBlendPipeline != null)
        {
            return _imageBlendPipeline;
        }

        var imageBlendShader = _cache.GetOrCreateShader("ImageBlend", ComputeShaders.ImageBlend, "ImageBlendShader");
        _imageBlendPipeline = _cache.GetOrCreateComputePipeline("ImageBlend", imageBlendShader);
        return _imageBlendPipeline;
    }

    private ComputePipeline* GetOrCreateColorTablePipeline()
    {
        if (_colorTablePipeline != null)
        {
            return _colorTablePipeline;
        }

        var colorTableShader = _cache.GetOrCreateShader("ColorTable", ComputeShaders.ColorTable, "ColorTableShader");
        _colorTablePipeline = _cache.GetOrCreateComputePipeline("ColorTable", colorTableShader);
        return _colorTablePipeline;
    }

    private ComputePipeline* GetOrCreateArithmeticCompositePipeline()
    {
        if (_arithmeticCompositePipeline != null)
        {
            return _arithmeticCompositePipeline;
        }

        var arithmeticCompositeShader = _cache.GetOrCreateShader(
            "ArithmeticComposite",
            ComputeShaders.ArithmeticComposite,
            "ArithmeticCompositeShader");
        _arithmeticCompositePipeline = _cache.GetOrCreateComputePipeline(
            "ArithmeticComposite",
            arithmeticCompositeShader);
        return _arithmeticCompositePipeline;
    }

    private ComputePipeline* GetOrCreateDisplacementMapPipeline()
    {
        if (_displacementMapPipeline != null)
        {
            return _displacementMapPipeline;
        }

        var displacementMapShader = _cache.GetOrCreateShader(
            "DisplacementMap",
            ComputeShaders.DisplacementMap,
            "DisplacementMapShader");
        _displacementMapPipeline = _cache.GetOrCreateComputePipeline(
            "DisplacementMap",
            displacementMapShader);
        return _displacementMapPipeline;
    }

    private ComputePipeline* GetOrCreateMatrixConvolutionPipeline()
    {
        if (_matrixConvolutionPipeline != null)
        {
            return _matrixConvolutionPipeline;
        }

        var matrixConvolutionShader = _cache.GetOrCreateShader(
            "MatrixConvolution",
            ComputeShaders.MatrixConvolution,
            "MatrixConvolutionShader");
        _matrixConvolutionPipeline = _cache.GetOrCreateComputePipeline(
            "MatrixConvolution",
            matrixConvolutionShader);
        return _matrixConvolutionPipeline;
    }

    private ComputePipeline* GetOrCreateImageLightingPipeline()
    {
        if (_imageLightingPipeline != null)
        {
            return _imageLightingPipeline;
        }

        var imageLightingShader = _cache.GetOrCreateShader(
            "ImageLighting",
            ComputeShaders.ImageLighting,
            "ImageLightingShader");
        _imageLightingPipeline = _cache.GetOrCreateComputePipeline(
            "ImageLighting",
            imageLightingShader);
        return _imageLightingPipeline;
    }

    private void EnsureCombinedBlurResources(uint width, uint height)
    {
        if (_combinedBlurHorizPipeline == null)
        {
            var horizontalShader = _cache.GetOrCreateShader(
                "CombinedBlurH",
                ComputeShaders.CombinedBlurHorizontal,
                "CombinedBlurHShader");
            var verticalShader = _cache.GetOrCreateShader(
                "CombinedBlurV",
                ComputeShaders.CombinedBlurVertical,
                "CombinedBlurVShader");
            _combinedBlurHorizPipeline = _cache.GetOrCreateComputePipeline("CombinedBlurH", horizontalShader);
            _combinedBlurVertPipeline = _cache.GetOrCreateComputePipeline("CombinedBlurV", verticalShader);
            _combinedBlurHorizLayout = _context.Api.ComputePipelineGetBindGroupLayout(_combinedBlurHorizPipeline, 0);
            _combinedBlurVertLayout = _context.Api.ComputePipelineGetBindGroupLayout(_combinedBlurVertPipeline, 0);
            _combinedBlurParams = new GpuBuffer(
                _context,
                (uint)Marshal.SizeOf<CombinedBlurParams>(),
                BufferUsage.Uniform | BufferUsage.CopyDst,
                "Combined Blur Params");
            _combinedShadowTemporary = new GpuTexture(
                _context,
                width,
                height,
                TextureFormat.Rgba8Unorm,
                TextureUsage.TextureBinding | TextureUsage.StorageBinding,
                "Combined Shadow Temporary",
                alphaMode: GpuTextureAlphaMode.Premultiplied);
            return;
        }

        _combinedShadowTemporary!.Resize(width, height);
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
        EnsureGaussianBlurResources();

        uint width = source.Width;
        uint height = source.Height;

        // Ensure temp and destination are resized to match source
        temp.Resize(width, height);
        destination.Resize(width, height);

        _blurHorizontalParams!.WriteSingle(new GaussianBlurParams(sigmaX));
        _blurVerticalParams!.WriteSingle(new GaussianBlurParams(sigmaY));

        CommandEncoder* encoder;
        fixed (byte* encoderLabel = "Compute Blur Encoder\0"u8)
        {
            var encoderDesc = new CommandEncoderDescriptor
            {
                Label = encoderLabel
            };
            encoder = _context.Api.DeviceCreateCommandEncoder(
                _context.Device,
                &encoderDesc);
        }

        var horizontalBinding = GetOrCreatePassBinding(
            ref _blurHorizontalBinding,
            _blurHorizLayout,
            source,
            temp,
            _blurHorizontalParams);
        var verticalBinding = GetOrCreatePassBinding(
            ref _blurVerticalBinding,
            _blurVertLayout,
            temp,
            destination,
            _blurVerticalParams);
        RunComputePass(
            encoder,
            _blurHorizPipeline,
            horizontalBinding,
            width,
            height);
        RunComputePass(
            encoder,
            _blurVertPipeline,
            verticalBinding,
            width,
            height);

        // Submit commands to queue
        CommandBuffer* cmdBuffer;
        fixed (byte* commandLabel = "Compute Blur Buffer\0"u8)
        {
            var cmdDesc = new CommandBufferDescriptor
            {
                Label = commandLabel
            };
            cmdBuffer = _context.Api.CommandEncoderFinish(encoder, &cmdDesc);
        }

        _context.Submit(1, &cmdBuffer);

        // Release resources
        _context.Api.CommandBufferRelease(cmdBuffer);
        _context.Api.CommandEncoderRelease(encoder);

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
        GetOrCreateMorphologyPipeline();

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
        _context.Submit(1, &commandBuffer);
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
        GetOrCreateImageLightingPipeline();

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
        _context.Submit(1, &commandBuffer);

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
        GetOrCreateMatrixConvolutionPipeline();

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
        _context.Submit(1, &commandBuffer);

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
        GetOrCreateDisplacementMapPipeline();
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
        _context.Submit(1, &commandBuffer);

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
        _context.Submit(1, &commandBuffer);

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
        GetOrCreateArithmeticCompositePipeline();
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
        _context.Submit(1, &commandBuffer);

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
        GetOrCreateImageBlendPipeline();
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
        _context.Submit(1, &commandBuffer);

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
        GetOrCreateColorTablePipeline();

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
        _context.Submit(1, &commandBuffer);

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
        _context.Submit(1, &commandBuffer);

        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
        _context.Api.BindGroupRelease(bindGroup);
        _context.Api.BindGroupLayoutRelease(layout);
    }

    public void ApplyOverdrawColorFilter(
        GpuTexture source,
        GpuTexture destination,
        ReadOnlySpan<Vector4> colors)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));

        var width = source.Width;
        var height = source.Height;
        destination.Resize(width, height);
        var pipeline = GetOrCreateOverdrawColorFilterPipeline();
        using var paramsBuffer = new GpuBuffer(
            _context,
            (uint)Marshal.SizeOf<OverdrawColorFilterParams>(),
            BufferUsage.Uniform | BufferUsage.CopyDst,
            "Overdraw Color Filter Params");
        paramsBuffer.WriteSingle(new OverdrawColorFilterParams(colors));

        var encoderDescriptor = new CommandEncoderDescriptor
        {
            Label = (byte*)SilkMarshal.StringToPtr("Compute Overdraw Color Filter Encoder")
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
            Label = (byte*)SilkMarshal.StringToPtr("Compute Overdraw Color Filter Buffer")
        };
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        SilkMarshal.Free((nint)commandDescriptor.Label);
        _context.Submit(1, &commandBuffer);

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

    private ComputePipeline* GetOrCreateOverdrawColorFilterPipeline()
    {
        if (_overdrawColorFilterPipeline != null)
        {
            return _overdrawColorFilterPipeline;
        }

        var shader = _cache.GetOrCreateShader(
            "OverdrawColorFilter",
            ComputeShaders.OverdrawColorFilter,
            "OverdrawColorFilterShader");
        _overdrawColorFilterPipeline = _cache.GetOrCreateComputePipeline(
            "OverdrawColorFilter",
            shader);
        return _overdrawColorFilterPipeline;
    }

    private BindGroup* GetOrCreatePassBinding(
        ref CachedPassBinding cached,
        BindGroupLayout* layout,
        GpuTexture input,
        GpuTexture output,
        GpuBuffer paramsBuffer)
    {
        if (cached.Matches(input, output))
        {
            return cached.BindGroup;
        }

        if (cached.BindGroup != null)
        {
            _context.Api.BindGroupRelease(cached.BindGroup);
            cached = default;
        }

        var entries = stackalloc BindGroupEntry[3];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = input.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = output.ViewPtr };
        entries[2] = new BindGroupEntry
        {
            Binding = 2,
            Buffer = paramsBuffer.BufferPtr,
            Offset = 0,
            Size = paramsBuffer.Size
        };

        var descriptor = new BindGroupDescriptor
        {
            Layout = layout,
            EntryCount = 3,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &descriptor);
        cached.Set(bindGroup, input, output);
        return bindGroup;
    }

    private BindGroup* GetOrCreateCombinedHorizontalBinding(
        GpuTexture source,
        GpuTexture blurOutput,
        GpuTexture shadowOutput)
    {
        if (_combinedHorizontalBinding.Matches(source, blurOutput, shadowOutput))
        {
            return _combinedHorizontalBinding.BindGroup;
        }

        ReleaseCombinedHorizontalBinding();
        var entries = stackalloc BindGroupEntry[4];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = source.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = blurOutput.ViewPtr };
        entries[2] = new BindGroupEntry { Binding = 2, TextureView = shadowOutput.ViewPtr };
        entries[3] = new BindGroupEntry
        {
            Binding = 3,
            Buffer = _combinedBlurParams!.BufferPtr,
            Offset = 0,
            Size = _combinedBlurParams.Size
        };
        var descriptor = new BindGroupDescriptor
        {
            Layout = _combinedBlurHorizLayout,
            EntryCount = 4,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &descriptor);
        _combinedHorizontalBinding = new CachedCombinedHorizontalBinding
        {
            BindGroup = bindGroup,
            SourceId = source.Id,
            SourceGeneration = source.Generation,
            BlurOutputId = blurOutput.Id,
            BlurOutputGeneration = blurOutput.Generation,
            ShadowOutputId = shadowOutput.Id,
            ShadowOutputGeneration = shadowOutput.Generation
        };
        return bindGroup;
    }

    private BindGroup* GetOrCreateCombinedVerticalBinding(
        GpuTexture blurInput,
        GpuTexture shadowInput,
        GpuTexture blurOutput,
        GpuTexture shadowOutput)
    {
        if (_combinedVerticalBinding.Matches(blurInput, shadowInput, blurOutput, shadowOutput))
        {
            return _combinedVerticalBinding.BindGroup;
        }

        ReleaseCombinedVerticalBinding();
        var entries = stackalloc BindGroupEntry[5];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = blurInput.ViewPtr };
        entries[1] = new BindGroupEntry { Binding = 1, TextureView = shadowInput.ViewPtr };
        entries[2] = new BindGroupEntry { Binding = 2, TextureView = blurOutput.ViewPtr };
        entries[3] = new BindGroupEntry { Binding = 3, TextureView = shadowOutput.ViewPtr };
        entries[4] = new BindGroupEntry
        {
            Binding = 4,
            Buffer = _combinedBlurParams!.BufferPtr,
            Offset = 0,
            Size = _combinedBlurParams.Size
        };
        var descriptor = new BindGroupDescriptor
        {
            Layout = _combinedBlurVertLayout,
            EntryCount = 5,
            Entries = entries
        };
        var bindGroup = _context.Api.DeviceCreateBindGroup(_context.Device, &descriptor);
        _combinedVerticalBinding = new CachedCombinedVerticalBinding
        {
            BindGroup = bindGroup,
            BlurInputId = blurInput.Id,
            BlurInputGeneration = blurInput.Generation,
            ShadowInputId = shadowInput.Id,
            ShadowInputGeneration = shadowInput.Generation,
            BlurOutputId = blurOutput.Id,
            BlurOutputGeneration = blurOutput.Generation,
            ShadowOutputId = shadowOutput.Id,
            ShadowOutputGeneration = shadowOutput.Generation
        };
        return bindGroup;
    }

    private void RunComputePass(
        CommandEncoder* encoder,
        ComputePipeline* pipeline,
        BindGroup* bindGroup,
        uint width,
        uint height)
    {
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
        EnsureSharpShadowPipeline();
        _sharpShadowParams!.WriteSingle(new ShadowParams(offset, shadowColor, blurRadius));

        CommandEncoder* encoder;
        fixed (byte* encoderLabel = "Compute Shadow Encoder\0"u8)
        {
            var encoderDesc = new CommandEncoderDescriptor { Label = encoderLabel };
            encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        }

        var binding = GetOrCreatePassBinding(
            ref _sharpShadowBinding,
            _sharpShadowLayout,
            source,
            destination,
            _sharpShadowParams);
        RunComputePass(
            encoder,
            _shadowPipeline,
            binding,
            source.Width,
            source.Height);

        CommandBuffer* cmdBuffer;
        fixed (byte* commandLabel = "Compute Shadow Buffer\0"u8)
        {
            var cmdDesc = new CommandBufferDescriptor { Label = commandLabel };
            cmdBuffer = _context.Api.CommandEncoderFinish(encoder, &cmdDesc);
        }

        _context.Submit(1, &cmdBuffer);

        _context.Api.CommandBufferRelease(cmdBuffer);
        _context.Api.CommandEncoderRelease(encoder);
    }

    public void ApplySharpDropShadow(
        GpuTexture source,
        GpuTexture destination,
        Vector2 offset,
        Vector4 shadowColor)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));
        destination.Resize(source.Width, source.Height);
        RunSharpDropShadow(source, destination, offset, shadowColor, blurRadius: 0f);
    }

    public void ApplyDropShadow(GpuTexture source, GpuTexture temp, GpuTexture destination, Vector2 offset, Vector4 shadowColor, float blurRadius)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));

        float snappedBlurRadius = MathF.Round(blurRadius * 2f) / 2f;

        uint width = source.Width;
        uint height = source.Height;

        temp.Resize(checked((width + 3u) / 4u), height);
        destination.Resize(width, height);

        if (snappedBlurRadius <= 0.01f)
        {
            RunSharpDropShadow(source, destination, offset, shadowColor, snappedBlurRadius);
            return;
        }
        EnsureShadowBlurResources();

        CommandEncoder* encoder;
        fixed (byte* encoderLabel = "Compute Shadow Encoder\0"u8)
        {
            var encoderDesc = new CommandEncoderDescriptor { Label = encoderLabel };
            encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDesc);
        }

        _shadowParams!.WriteSingle(new ShadowParams(
            offset,
            shadowColor,
            snappedBlurRadius,
            width,
            height));

        var horizontalBinding = GetOrCreatePassBinding(
            ref _shadowHorizontalBinding,
            _shadowBlurHorizLayout,
            source,
            temp,
            _shadowParams);
        var verticalBinding = GetOrCreatePassBinding(
            ref _shadowVerticalBinding,
            _shadowBlurVertLayout,
            temp,
            destination,
            _shadowParams);
        RunComputePass(
            encoder,
            _shadowBlurHorizPipeline,
            horizontalBinding,
            checked((width + 3u) / 4u),
            height);
        RunComputePass(encoder, _shadowBlurVertPipeline, verticalBinding, width, height);

        CommandBuffer* cmdBuffer;
        fixed (byte* commandLabel = "Compute Shadow Buffer\0"u8)
        {
            var cmdDesc = new CommandBufferDescriptor { Label = commandLabel };
            cmdBuffer = _context.Api.CommandEncoderFinish(encoder, &cmdDesc);
        }

        _context.Submit(1, &cmdBuffer);

        _context.Api.CommandBufferRelease(cmdBuffer);
        _context.Api.CommandEncoderRelease(encoder);

    }

    public void ApplyDropShadowAndGaussianBlur(
        GpuTexture source,
        GpuTexture temporary,
        GpuTexture shadowDestination,
        GpuTexture blurDestination,
        Vector2 shadowOffset,
        Vector4 shadowColor,
        float shadowBlurRadius,
        float gaussianSigma)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(ComputeAccelerator));

        float snappedShadowRadius = MathF.Round(shadowBlurRadius * 2f) / 2f;
        if (snappedShadowRadius <= 0.01f || gaussianSigma <= 0f)
        {
            ApplyDropShadow(
                source,
                temporary,
                shadowDestination,
                shadowOffset,
                shadowColor,
                shadowBlurRadius);
            ApplyGaussianBlur(source, temporary, blurDestination, gaussianSigma);
            return;
        }

        uint width = source.Width;
        uint height = source.Height;
        temporary.Resize(width, height);
        shadowDestination.Resize(width, height);
        blurDestination.Resize(width, height);

        if (MathF.Abs(snappedShadowRadius - gaussianSigma) <= 0.0001f && gaussianSigma <= 64f / 3f)
        {
            ApplyCombinedEqualRadiusBlur(
                source,
                temporary,
                shadowDestination,
                blurDestination,
                shadowOffset,
                shadowColor,
                gaussianSigma,
                width,
                height);
            return;
        }
        EnsureShadowBlurResources();
        EnsureGaussianBlurResources();

        _shadowParams!.WriteSingle(new ShadowParams(shadowOffset, shadowColor, snappedShadowRadius));
        _blurHorizontalParams!.WriteSingle(new GaussianBlurParams(gaussianSigma));
        _blurVerticalParams!.WriteSingle(new GaussianBlurParams(gaussianSigma));

        var shadowHorizontalBinding = GetOrCreatePassBinding(
            ref _shadowHorizontalBinding,
            _shadowBlurHorizLayout,
            source,
            temporary,
            _shadowParams);
        var shadowVerticalBinding = GetOrCreatePassBinding(
            ref _shadowVerticalBinding,
            _shadowBlurVertLayout,
            temporary,
            shadowDestination,
            _shadowParams);
        var blurHorizontalBinding = GetOrCreatePassBinding(
            ref _blurHorizontalBinding,
            _blurHorizLayout,
            source,
            temporary,
            _blurHorizontalParams);
        var blurVerticalBinding = GetOrCreatePassBinding(
            ref _blurVerticalBinding,
            _blurVertLayout,
            temporary,
            blurDestination,
            _blurVerticalParams);

        var encoderDescriptor = new CommandEncoderDescriptor();
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        RunComputePass(encoder, _shadowBlurHorizPipeline, shadowHorizontalBinding, width, height);
        RunComputePass(encoder, _shadowBlurVertPipeline, shadowVerticalBinding, width, height);
        RunComputePass(encoder, _blurHorizPipeline, blurHorizontalBinding, width, height);
        RunComputePass(encoder, _blurVertPipeline, blurVerticalBinding, width, height);

        var commandDescriptor = new CommandBufferDescriptor();
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        _context.Submit(1, &commandBuffer);
        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
    }

    private void ApplyCombinedEqualRadiusBlur(
        GpuTexture source,
        GpuTexture blurTemporary,
        GpuTexture shadowDestination,
        GpuTexture blurDestination,
        Vector2 shadowOffset,
        Vector4 shadowColor,
        float sigma,
        uint width,
        uint height)
    {
        EnsureCombinedBlurResources(width, height);
        var shadowTemporary = _combinedShadowTemporary!;
        _combinedBlurParams!.WriteSingle(new CombinedBlurParams(shadowOffset, shadowColor, sigma));
        var horizontalBinding = GetOrCreateCombinedHorizontalBinding(source, blurTemporary, shadowTemporary);
        var verticalBinding = GetOrCreateCombinedVerticalBinding(
            blurTemporary,
            shadowTemporary,
            blurDestination,
            shadowDestination);

        var encoderDescriptor = new CommandEncoderDescriptor();
        var encoder = _context.Api.DeviceCreateCommandEncoder(_context.Device, &encoderDescriptor);
        RunComputePass(encoder, _combinedBlurHorizPipeline, horizontalBinding, width, height);
        RunComputePass(encoder, _combinedBlurVertPipeline, verticalBinding, width, height);
        var commandDescriptor = new CommandBufferDescriptor();
        var commandBuffer = _context.Api.CommandEncoderFinish(encoder, &commandDescriptor);
        _context.Submit(1, &commandBuffer);
        _context.Api.CommandBufferRelease(commandBuffer);
        _context.Api.CommandEncoderRelease(encoder);
    }



    public void Dispose()
    {
        if (_isDisposed) return;

        ReleaseCachedPassBinding(ref _blurHorizontalBinding);
        ReleaseCachedPassBinding(ref _blurVerticalBinding);
        ReleaseCachedPassBinding(ref _shadowHorizontalBinding);
        ReleaseCachedPassBinding(ref _shadowVerticalBinding);
        ReleaseCachedPassBinding(ref _sharpShadowBinding);
        ReleaseCombinedHorizontalBinding();
        ReleaseCombinedVerticalBinding();
        _blurHorizontalParams?.Dispose();
        _blurVerticalParams?.Dispose();
        _shadowParams?.Dispose();
        _sharpShadowParams?.Dispose();
        _combinedBlurParams?.Dispose();
        _combinedShadowTemporary?.Dispose();

        if (!_context.IsDisposed)
        {
            if (_blurHorizLayout != null)
            {
                _context.Api.BindGroupLayoutRelease(_blurHorizLayout);
                _context.Api.BindGroupLayoutRelease(_blurVertLayout);
            }
            if (_shadowBlurHorizLayout != null)
            {
                _context.Api.BindGroupLayoutRelease(_shadowBlurHorizLayout);
                _context.Api.BindGroupLayoutRelease(_shadowBlurVertLayout);
            }
            if (_sharpShadowLayout != null)
            {
                _context.Api.BindGroupLayoutRelease(_sharpShadowLayout);
            }
            if (_combinedBlurHorizLayout != null)
            {
                _context.Api.BindGroupLayoutRelease(_combinedBlurHorizLayout);
                _context.Api.BindGroupLayoutRelease(_combinedBlurVertLayout);
            }
        }

        _blurHorizLayout = null;
        _blurVertLayout = null;
        _shadowBlurHorizLayout = null;
        _shadowBlurVertLayout = null;
        _sharpShadowLayout = null;
        _combinedBlurHorizLayout = null;
        _combinedBlurVertLayout = null;
        _cache.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private void ReleaseCachedPassBinding(ref CachedPassBinding cached)
    {
        if (cached.BindGroup != null && !_context.IsDisposed)
        {
            _context.Api.BindGroupRelease(cached.BindGroup);
        }

        cached = default;
    }

    private void ReleaseCombinedHorizontalBinding()
    {
        if (_combinedHorizontalBinding.BindGroup != null && !_context.IsDisposed)
        {
            _context.Api.BindGroupRelease(_combinedHorizontalBinding.BindGroup);
        }

        _combinedHorizontalBinding = default;
    }

    private void ReleaseCombinedVerticalBinding()
    {
        if (_combinedVerticalBinding.BindGroup != null && !_context.IsDisposed)
        {
            _context.Api.BindGroupRelease(_combinedVerticalBinding.BindGroup);
        }

        _combinedVerticalBinding = default;
    }
}
