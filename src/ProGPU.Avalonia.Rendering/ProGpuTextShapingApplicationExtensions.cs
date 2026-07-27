using Avalonia.Platform;
using Avalonia.ProGpu;

// ReSharper disable once CheckNamespace
namespace Avalonia
{
    /// <summary>
    /// Configures Avalonia to use ProGPU's managed OpenType text shaper.
    /// </summary>
    public static class ProGpuTextShapingApplicationExtensions
    {
        public static AppBuilder UseProGpuTextShaping(this AppBuilder builder)
        {
#if AVALONIA11
            // Avalonia 11 has no independent text-shaping subsystem hook.
            // The ProGPU rendering initializer registers ProGpuTextShaper.
            return builder;
#else
            return builder.UseTextShapingSubsystem(
                () => AvaloniaLocator.CurrentMutable
                    .Bind<ITextShaperImpl>()
                    .ToConstant(new ProGpuTextShaper()),
                "ProGPU managed OpenType");
#endif
        }
    }
}
