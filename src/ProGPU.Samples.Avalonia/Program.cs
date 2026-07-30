using Avalonia;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ProGPU.Samples.Avalonia;

class Program
{
    private static readonly HashSet<string> s_sampleKeys = new(
        new[]
        {
            "Charting",
            "Dxf",
            "Drawing",
            "MotionMark",
            "Markdown",
            "Glyphs",
            "DataGrid",
            "Designer",
            "MediaPlayer",
            "VideoEditor"
        },
        StringComparer.OrdinalIgnoreCase);

    internal static bool EnableSharedImageReadback { get; private set; }
    internal static bool EnableSharedTextureMemory { get; private set; }
    internal static string RequestedSample { get; private set; } = "Charting";
    internal static AvaloniaSampleBenchmark? Benchmark { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
#if MACOS
        using IDisposable mediaRegistration =
            ProGPU.Apple.Media.AppleMedia.Register();
#else
        using IDisposable? mediaRegistration =
            OperatingSystem.IsWindows()
                ? ProGPU.Windows.Media.WindowsMedia.Register()
                : OperatingSystem.IsLinux()
                    ? ProGPU.Linux.Media.LinuxMedia.Register()
                    : null;
#endif
        bool useHarfBuzz = args.Contains(
            "--harfbuzz",
            StringComparer.OrdinalIgnoreCase);
        EnableSharedImageReadback = args.Contains(
            "--shared-image-readback",
            StringComparer.OrdinalIgnoreCase);
        EnableSharedTextureMemory =
            (OperatingSystem.IsMacOS() ||
             OperatingSystem.IsWindows() ||
             OperatingSystem.IsLinux()) &&
            !args.Contains(
                "--disable-shared-texture-memory",
                StringComparer.OrdinalIgnoreCase);

        var lifetimeArgs = new List<string>(args.Length);
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument.Equals("--sample", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Length ||
                    !s_sampleKeys.TryGetValue(args[index], out string? sample))
                {
                    throw new ArgumentException(
                        "--sample must name one of: " +
                        string.Join(", ", s_sampleKeys.Order(StringComparer.Ordinal)));
                }

                RequestedSample = sample;
                continue;
            }

            if (argument.Equals("--harfbuzz", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--shared-image-readback", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--disable-shared-texture-memory", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lifetimeArgs.Add(argument);
        }

        using var benchmark = AvaloniaSampleBenchmark.TryStart(
            RequestedSample,
            useHarfBuzz ? "HarfBuzz" : "ProGPU");
        Benchmark = benchmark;
        try
        {
            BuildAvaloniaApp(useHarfBuzz)
                .StartWithClassicDesktopLifetime(lifetimeArgs.ToArray());
        }
        finally
        {
            Benchmark = null;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => BuildAvaloniaApp(useHarfBuzz: false);

    private static AppBuilder BuildAvaloniaApp(bool useHarfBuzz)
    {
        AppBuilder builder = AppBuilder.Configure<App>();
#if MACOS
        builder = builder
            .UseAvaloniaNative()
            .With(
                new global::Avalonia.ProGpu.ProGpuOptions
                {
                    // macOS 26 may expose CAMetalLayer drawables through
                    // losslessly compressed '&BGA' IOSurfaces, which Dawn
                    // does not currently accept for direct presentation.
                    // Keep the shared Dawn device for zero-copy media and
                    // let Avalonia present the shell through its framebuffer
                    // fallback until that format is supported upstream.
                    UseDawnMetalPresentation = false,
                    RequireDawnMetalPresentation = false,
                    PrewarmDawnMetalDevice = true
                })
            .UseProGpu();
#else
        if (OperatingSystem.IsWindows())
        {
            builder = builder
                .UseWin32()
                .With(
                    new global::Avalonia.ProGpu.ProGpuOptions
                    {
                        UseDawnNativePresentation = true,
                        RequireDawnNativePresentation = false
                    })
                .UseProGpu();
        }
        else
        {
            builder = builder
                .UseSilkNet()
                .UseProGpu();
        }
#endif
        builder = useHarfBuzz
            ? builder.UseHarfBuzz()
            : builder.UseProGpuTextShaping();
        return builder
            .WithInterFont()
            .LogToTrace();
    }
}
