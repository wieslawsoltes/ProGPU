using Microsoft.UI.Xaml;
using Silk.NET.Input.Glfw;
using Silk.NET.Windowing.Glfw;

namespace ProGPU.Samples.Suntrail.Desktop;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--render-benchmark")
        {
            if (args.Length is not (4 or 5) || args[2] is not ("on" or "off" or "coverage") || !int.TryParse(args[3], out int renderFrames) || renderFrames < 60 || renderFrames > 36000)
                throw new ArgumentException("--render-benchmark requires output-prefix, on/off/coverage, 60–36000 frames, and optional world 1–8.");
            int renderWorld = 1;
            if (args.Length == 5 && (!int.TryParse(args[4], out renderWorld) || renderWorld < 1 || renderWorld > 8))
                throw new ArgumentException("World must be 1 through 8.");
            GpuWorkload.Run(args[1], args[2] == "on", renderFrames, renderWorld - 1, args[2] == "coverage");
            return;
        }
        long launched=System.Diagnostics.Stopwatch.GetTimestamp();
        int benchmark=Array.IndexOf(args,"--benchmark");
        App.AutoPlay = args.Contains("--autoplay", StringComparer.Ordinal) || benchmark>=0;
        int worldArgument = Array.IndexOf(args, "--world");
        int world = 0;
        if (worldArgument >= 0 && (worldArgument + 1 >= args.Length || !int.TryParse(args[worldArgument + 1], out world) || world < 1 || world > 8))
            throw new ArgumentException("--world requires a number from 1 through 8.");
        if (args.Contains("--no-occlusion", StringComparer.Ordinal))
            App.Started += (view, _) => view.Surface.Batch.EnableBackgroundOcclusion = false;
        if (args.Contains("--material-pages", StringComparer.Ordinal))
            App.Started += (_, window) => ((Rendering.ProceduralPipeline)window.Compositor!.GetDrawingExtension(Rendering.ProceduralDrawingContextExtensions.Definition)!).EnableMaterialPages = true;
        if (args.Contains("--sky-cache", StringComparer.Ordinal))
            App.Started += (_, window) => ((Rendering.ProceduralPipeline)window.Compositor!.GetDrawingExtension(Rendering.ProceduralDrawingContextExtensions.Definition)!).EnableSkyCache = true;
        if (world > 0) App.Started += (view, _) => view.Surface.Session.StartLevel(world - 1);
        if(benchmark>=0)
        {
            if(benchmark+1>=args.Length)throw new ArgumentException("--benchmark requires an output file prefix.");
            int frames=benchmark+2<args.Length && int.TryParse(args[benchmark+2],out int count)?Math.Clamp(count,60,36000):1200;
            var run=new PerformanceRun(args[benchmark+1],frames,launched);App.Started+=run.Attach;
        }
        GlfwWindowing.Use(); GlfwInput.RegisterPlatform();
        AppBuilder<App>.Configure().WithTitle("Suntrail").WithSize(1440, 900).Build().Run(args);
    }
}
