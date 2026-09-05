using Microsoft.UI.Xaml;
using Silk.NET.Input.Glfw;
using Silk.NET.Windowing.Glfw;

namespace ProGPU.Samples.Suntrail.Desktop;

public static class Program
{
    public static void Main(string[] args)
    {
        long launched=System.Diagnostics.Stopwatch.GetTimestamp();
        int benchmark=Array.IndexOf(args,"--benchmark");
        App.AutoPlay = args.Contains("--autoplay", StringComparer.Ordinal) || benchmark>=0;
        int worldArgument = Array.IndexOf(args, "--world");
        int world = 0;
        if (worldArgument >= 0 && (worldArgument + 1 >= args.Length || !int.TryParse(args[worldArgument + 1], out world) || world < 1 || world > 8))
            throw new ArgumentException("--world requires a number from 1 through 8.");
        if (args.Contains("--no-occlusion", StringComparer.Ordinal))
            App.Started += (view, _) => view.Surface.Batch.EnableBackgroundOcclusion = false;
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
