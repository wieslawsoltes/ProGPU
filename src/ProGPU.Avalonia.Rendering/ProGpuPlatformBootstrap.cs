using Avalonia.Platform;
using ProGPU.Backend;

namespace Avalonia.ProGpu;

/// <summary>
/// Installs the typed ProGPU render, font, and shaping services.
/// </summary>
public static class SkiaPlatform
{
    public static Vector DefaultDpi => new(96, 96);

    public static void Initialize() =>
        Initialize(new SkiaOptions(), new ProGpuOptions());

    public static void Initialize(SkiaOptions options) =>
        Initialize(options, new ProGpuOptions());

    public static void Initialize(
        SkiaOptions options,
        ProGpuOptions proGpuOptions)
    {
#if !AVALONIA11
        SharedGpuTextureSource.RegisterCompositionImporter();
#endif
        var renderInterface = new PlatformRenderInterface(
            options.MaxGpuResourceSizeBytes,
            proGpuOptions.RequireNativeCompositionScene,
            proGpuOptions.UseDawnMetalPresentation,
            proGpuOptions.RequireDawnMetalPresentation,
            proGpuOptions.UseDawnNativePresentation,
            proGpuOptions.RequireDawnNativePresentation);

        var services = AvaloniaLocator.CurrentMutable
            .Bind<IPlatformRenderInterface>()
            .ToConstant(renderInterface)
            .Bind<IFontManagerImpl>()
            .ToConstant(new FontManagerImpl());

#if AVALONIA11
        services.Bind<ITextShaperImpl>().ToConstant(new ProGpuTextShaper());
#endif
    }
}
