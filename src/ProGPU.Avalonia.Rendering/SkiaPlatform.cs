using Avalonia.Platform;
using ProGPU.Backend;

namespace Avalonia.ProGpu
{
    /// <summary>
    /// Skia platform initializer.
    /// </summary>
    public static class SkiaPlatform
    {
        /// <summary>
        /// Initialize Skia platform.
        /// </summary>
        public static void Initialize()
        {
            Initialize(new SkiaOptions(), new ProGpuOptions());
        }

        public static void Initialize(SkiaOptions options)
        {
            Initialize(options, new ProGpuOptions());
        }

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

            AvaloniaLocator.CurrentMutable
                .Bind<IPlatformRenderInterface>().ToConstant(renderInterface)
                .Bind<IFontManagerImpl>().ToConstant(new FontManagerImpl())
#if AVALONIA11
                .Bind<ITextShaperImpl>().ToConstant(new ProGpuTextShaper())
#endif
                ;
        }

        /// <summary>
        /// Default DPI.
        /// </summary>
        public static Vector DefaultDpi => new Vector(96.0f, 96.0f);
    }
}
