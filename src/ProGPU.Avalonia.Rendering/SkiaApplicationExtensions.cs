using Avalonia.Controls;
using Avalonia.ProGpu;

// ReSharper disable once CheckNamespace
namespace Avalonia
{
    /// <summary>
    /// Skia application extensions.
    /// </summary>
    public static class SkiaApplicationExtensions
    {
        /// <summary>
        /// Enables the ProGPU renderer.
        /// </summary>
        /// <param name="builder">Builder.</param>
        /// <returns>Configure builder.</returns>
        public static AppBuilder UseProGpu(this AppBuilder builder)
        {
            return builder.UseRenderingSubsystem(() => SkiaPlatform.Initialize(
                AvaloniaLocator.Current.GetService<SkiaOptions>() ??
                    new SkiaOptions(),
                AvaloniaLocator.Current.GetService<ProGpuOptions>() ??
                    new ProGpuOptions()),
                "ProGPU");
        }

        /// <summary>
        /// Enables the ProGPU renderer.
        /// </summary>
        /// <param name="builder">Builder.</param>
        /// <returns>Configure builder.</returns>
        public static AppBuilder UseSkia(this AppBuilder builder) => builder.UseProGpu();
    }
}
