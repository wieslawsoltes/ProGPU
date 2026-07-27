using Avalonia.Controls;
using Avalonia.ProGpu;

namespace Avalonia;

/// <summary>
/// Controls the renderer settings exposed by Avalonia's Skia-compatible
/// application contract.
/// </summary>
public class SkiaOptions
{
    /// <summary>
    /// Gets or sets the preferred upper bound for retained GPU resources.
    /// A null value leaves policy selection to the backend.
    /// </summary>
    public long? MaxGpuResourceSizeBytes { get; set; } =
        1024L * 600L * 4L * 12L;

    /// <summary>
    /// Gets or sets whether opacity should use an intermediate layer.
    /// </summary>
    public bool UseOpacitySaveLayer { get; set; }
}

/// <summary>
/// Registers ProGPU as Avalonia's rendering subsystem.
/// </summary>
public static class SkiaApplicationExtensions
{
    public static AppBuilder UseProGpu(this AppBuilder builder)
    {
        return builder.UseRenderingSubsystem(
            static () =>
            {
                var options = AvaloniaLocator.Current.GetService<SkiaOptions>()
                    ?? new SkiaOptions();
                var proGpuOptions =
                    AvaloniaLocator.Current.GetService<ProGpuOptions>()
                    ?? new ProGpuOptions();
                SkiaPlatform.Initialize(options, proGpuOptions);
            },
            "ProGPU");
    }

    /// <summary>
    /// Preserves Avalonia's Skia bootstrap name for package substitution.
    /// </summary>
    public static AppBuilder UseSkia(this AppBuilder builder) =>
        builder.UseProGpu();
}
