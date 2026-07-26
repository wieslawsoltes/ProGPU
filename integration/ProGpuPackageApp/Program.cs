using Avalonia;
using Avalonia.Rendering.Composition;
#if PROGPU_REPLACEMENT_PACKAGE
using Avalonia.ProGpu;
using ProGPU.Scene;
#endif

namespace ProGpuPackageApp;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
#if PROGPU_REPLACEMENT_PACKAGE
        bool multiWindowSmoke =
            App.MultiWindowSmokeEnabled;
        bool smoke =
            multiWindowSmoke ||
            Environment.GetEnvironmentVariable("PROGPU_INTEGRATION_SMOKE") ==
                "1";
        if (smoke)
            ProGpuRenderingDiagnostics.FrameRendered += OnFrameRendered;
#endif

        try
        {
            int exitCode =
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
#if PROGPU_REPLACEMENT_PACKAGE
            if (smoke && exitCode == 0)
            {
                exitCode = ValidateReplacementSmoke();
                if (exitCode == 0 && multiWindowSmoke)
                    exitCode = ValidateMultiWindowSmoke();
            }
#endif
            return exitCode;
        }
        finally
        {
#if PROGPU_REPLACEMENT_PACKAGE
            if (smoke)
                ProGpuRenderingDiagnostics.FrameRendered -= OnFrameRendered;
#endif
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSilkNet()
            .UseProGpu()
#if PROGPU_REPLACEMENT_PACKAGE
            .With(new ProGpuOptions
            {
                RequireNativeCompositionScene = true
            })
#endif
            .With(new CompositionOptions
            {
                UseRegionDirtyRectClipping = false
            })
            .UseProGpuTextShaping()
            .WithInterFont();

#if PROGPU_REPLACEMENT_PACKAGE
    private static int s_renderedFrames;
    private static long s_serverBackendRenders;
    private static int s_retainedSceneCount;
    private static int s_fallbackNodeCount;
    private static readonly int[] s_multiWindowStageFrames = new int[3];

    private static void OnFrameRendered(CompositorMetrics metrics)
    {
        Interlocked.Increment(ref s_renderedFrames);
        InterlockedMax(
            ref s_serverBackendRenders,
            metrics.RetainedCompositionServerBackendRenderCount);
        if (App.MultiWindowSmokeEnabled)
        {
            int stage = Math.Clamp(App.MultiWindowSmokeStage, 0, 2);
            Interlocked.Increment(ref s_multiWindowStageFrames[stage]);
        }
        InterlockedMax(
            ref s_retainedSceneCount,
            metrics.RetainedCompositionSceneCount);
        InterlockedMax(
            ref s_fallbackNodeCount,
            metrics.RetainedCompositionFallbackNodeCount);
    }

    private static int ValidateReplacementSmoke()
    {
        int renderedFrames = Volatile.Read(ref s_renderedFrames);
        long serverBackendRenders =
            Volatile.Read(ref s_serverBackendRenders);
        int retainedScenes = Volatile.Read(ref s_retainedSceneCount);
        int fallbackNodes = Volatile.Read(ref s_fallbackNodeCount);
        Console.WriteLine(
            "[ProGpuPackageSmoke] " +
            $"frames={renderedFrames} serverBackendRenders={serverBackendRenders} " +
            $"retainedScenes={retainedScenes} " +
            $"fallbackNodes={fallbackNodes}");

        if (renderedFrames == 0)
        {
            Console.Error.WriteLine(
                "The replacement package smoke rendered no ProGPU frames.");
            return 10;
        }

        if (retainedScenes == 0)
        {
            Console.Error.WriteLine(
                "The packaged renderer does not contain the source-built " +
                "retained Avalonia compositor seam.");
            return 11;
        }

        if (serverBackendRenders == 0)
        {
            Console.Error.WriteLine(
                "The replacement package did not render through the typed " +
                "ProGPU composition server backend.");
            return 17;
        }

        if (fallbackNodes != 0)
        {
            Console.Error.WriteLine(
                "The replacement package smoke used Avalonia subtree " +
                "flattening instead of native ProGPU composition.");
            return 12;
        }

        return 0;
    }

    private static int ValidateMultiWindowSmoke()
    {
        int initialFrames = Volatile.Read(ref s_multiWindowStageFrames[0]);
        int ownerDisposedFrames =
            Volatile.Read(ref s_multiWindowStageFrames[1]);
        int borrowerDisposedFrames =
            Volatile.Read(ref s_multiWindowStageFrames[2]);
        Console.WriteLine(
            "[ProGpuMultiWindowSmoke] " +
            $"initialFrames={initialFrames} " +
            $"ownerDisposedFrames={ownerDisposedFrames} " +
            $"borrowerDisposedFrames={borrowerDisposedFrames} " +
            $"sharedPairs={App.SharedDevicePairCount} " +
            $"ownerDisposed={App.DeviceOwnerDisposed} " +
            $"borrowerDisposed={App.DeviceBorrowerDisposed} " +
            $"survivorAfterOwner={App.SurvivorHealthyAfterOwnerDispose} " +
            $"survivorAfterBorrower={App.SurvivorHealthyAfterBorrowerDispose} " +
            $"completed={App.MultiWindowSmokeCompleted} " +
            $"timedOut={App.MultiWindowSmokeTimedOut}");

        if (App.SharedDevicePairCount != 2)
        {
            Console.Error.WriteLine(
                "The package smoke did not observe both expected typed " +
                "shared-device window pairs.");
            return 13;
        }

        if (!App.DeviceOwnerDisposed ||
            !App.SurvivorHealthyAfterOwnerDispose ||
            ownerDisposedFrames == 0)
        {
            Console.Error.WriteLine(
                "The surviving window stopped rendering after the original " +
                "shared-device owner was disposed.");
            return 14;
        }

        if (!App.DeviceBorrowerDisposed ||
            !App.SurvivorHealthyAfterBorrowerDispose ||
            borrowerDisposedFrames == 0)
        {
            Console.Error.WriteLine(
                "The device owner stopped rendering after a borrowing " +
                "window was disposed.");
            return 15;
        }

        if (initialFrames == 0 ||
            !App.MultiWindowSmokeCompleted ||
            App.MultiWindowSmokeTimedOut)
        {
            Console.Error.WriteLine(
                "The multi-window lifecycle sequence did not complete.");
            return 16;
        }

        return 0;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (value > current)
        {
            int observed = Interlocked.CompareExchange(
                ref target,
                value,
                current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long current = Volatile.Read(ref target);
        while (value > current)
        {
            long observed = Interlocked.CompareExchange(
                ref target,
                value,
                current);
            if (observed == current)
                return;
            current = observed;
        }
    }
#endif
}
