using Foundation;
using Microsoft.UI.Xaml;
using ObjCRuntime;
using UIKit;

namespace ProGPU.iOS;

/// <summary>
/// Starts a ProGPU application inside UIKit while retaining the shared WinUI application
/// and window model used by the desktop and browser hosts.
/// </summary>
public static class IosApplication
{
    public static void Run<TApp>(string[] args, string title = "ProGPU")
        where TApp : Application, new()
    {
        ArgumentNullException.ThrowIfNull(args);
        IosLaunchContext.Configure(
            title,
            host => AppBuilder<TApp>
                .Configure()
                .WithTitle(title)
                .Build()
                .RunAsync(args));

        UIApplication.Main(args, principalClass: null, delegateClass: typeof(ProGpuApplicationDelegate));
    }
}

internal static class IosLaunchContext
{
    private static Func<IosWindowHost, Task>? s_launch;

    public static string Title { get; private set; } = "ProGPU";

    public static void Configure(string title, Func<IosWindowHost, Task> launch)
    {
        if (s_launch != null)
            throw new InvalidOperationException("The ProGPU iOS application has already been configured.");

        Title = string.IsNullOrWhiteSpace(title) ? "ProGPU" : title;
        s_launch = launch ?? throw new ArgumentNullException(nameof(launch));
    }

    public static Task LaunchAsync(IosWindowHost host) =>
        (s_launch ?? throw new InvalidOperationException("IosApplication.Run must configure the app before UIKit launches."))(host);
}

[Register("ProGpuApplicationDelegate")]
internal sealed class ProGpuApplicationDelegate : UIApplicationDelegate
{
    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        return true;
    }

    public override UISceneConfiguration GetConfiguration(
        UIApplication application,
        UISceneSession connectingSceneSession,
        UISceneConnectionOptions options)
    {
        return new UISceneConfiguration("Default Configuration", connectingSceneSession.Role)
        {
            SceneClass = new Class(typeof(UIWindowScene)),
            DelegateClass = new Class(typeof(ProGpuSceneDelegate))
        };
    }

    public override void WillTerminate(UIApplication application)
    {
        if (WindowHostServices.Current is IDisposable disposable) disposable.Dispose();
        WindowHostServices.Current = null;
    }
}

[Register("ProGpuSceneDelegate")]
internal sealed class ProGpuSceneDelegate : UISceneDelegate
{
    private IosWindowHost? _host;

    public UIWindow? Window { get; private set; }

    public override void WillConnect(
        UIScene scene,
        UISceneSession session,
        UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene) return;
        if (WindowHostServices.Current != null)
            throw new NotSupportedException("The ProGPU iPhone host currently supports one connected UIKit scene.");

        UIScreen screen = windowScene.Screen;
        var controller = new UIViewController();
        var renderView = new MetalRenderView(screen.Bounds, screen);
        controller.View = renderView;

        Window = new UIWindow(windowScene)
        {
            RootViewController = controller
        };
        Window.MakeKeyAndVisible();

        _host = new IosWindowHost(renderView, controller, screen);
        WindowHostServices.Current = _host;
        _ = ObserveLaunchAsync(_host);
    }

    public override void DidBecomeActive(UIScene scene) => _host?.Resume();

    public override void WillResignActive(UIScene scene) => _host?.Pause();

    public override void DidEnterBackground(UIScene scene) => _host?.Pause();

    public override void DidDisconnect(UIScene scene)
    {
        _host?.Dispose();
        if (ReferenceEquals(WindowHostServices.Current, _host)) WindowHostServices.Current = null;
        _host = null;
        Window = null;
    }

    private static async Task ObserveLaunchAsync(IosWindowHost host)
    {
        try
        {
            await IosLaunchContext.LaunchAsync(host);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[ProGPU.iOS] Application terminated: {exception}");
            throw;
        }
    }
}
