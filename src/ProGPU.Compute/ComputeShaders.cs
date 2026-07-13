using ProGPU.Backend;

namespace ProGPU.Compute;

public static class ComputeShaders
{
    public static readonly string NonlinearColorFilter = ShaderResource.Load(typeof(ComputeShaders), "NonlinearColorFilter.wgsl");

    public static readonly string ImageLighting = ShaderResource.Load(typeof(ComputeShaders), "ImageLighting.wgsl");

    public static readonly string MatrixConvolution = ShaderResource.Load(typeof(ComputeShaders), "MatrixConvolution.wgsl");

    public static readonly string Magnifier = ShaderResource.Load(typeof(ComputeShaders), "Magnifier.wgsl");

    public static readonly string DisplacementMap = ShaderResource.Load(typeof(ComputeShaders), "DisplacementMap.wgsl");

    public static readonly string ArithmeticComposite = ShaderResource.Load(typeof(ComputeShaders), "ArithmeticComposite.wgsl");

    public static readonly string ColorTable = ShaderResource.Load(typeof(ComputeShaders), "ColorTable.wgsl");

    public static readonly string ImageBlend = ShaderResource.Load(typeof(ComputeShaders), "ImageBlend.wgsl");

    public static readonly string Morphology = ShaderResource.Load(typeof(ComputeShaders), "Morphology.wgsl");

    public static readonly string GaussianBlurHorizontal = ShaderResource.Load(typeof(ComputeShaders), "GaussianBlurHorizontal.wgsl");

    public static readonly string GaussianBlurVertical = ShaderResource.Load(typeof(ComputeShaders), "GaussianBlurVertical.wgsl");

    public static readonly string DropShadow = ShaderResource.Load(typeof(ComputeShaders), "DropShadow.wgsl");

    public static readonly string ShadowBlurHorizontal = ShaderResource.Load(typeof(ComputeShaders), "ShadowBlurHorizontal.wgsl");

    public static readonly string ShadowBlurVertical = ShaderResource.Load(typeof(ComputeShaders), "ShadowBlurVertical.wgsl");


}
