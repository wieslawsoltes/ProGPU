using System;
using Silk.NET.Windowing;

namespace Microsoft.UI.Xaml;

public class AppBuilder<TApp> where TApp : Application, new()
{
    private string _title = "ProGPU Application";
    private int _width = 1280;
    private int _height = 800;

    public static AppBuilder<TApp> Configure()
    {
        return new AppBuilder<TApp>();
    }

    public AppBuilder<TApp> WithTitle(string title)
    {
        _title = title;
        return this;
    }

    public AppBuilder<TApp> WithSize(int width, int height)
    {
        _width = width;
        _height = height;
        return this;
    }

    public AppRunner<TApp> Build()
    {
        return new AppRunner<TApp>(_title, _width, _height);
    }
}

public class AppRunner<TApp> where TApp : Application, new()
{
    private readonly string _title;
    private readonly int _width;
    private readonly int _height;

    internal AppRunner(string title, int width, int height)
    {
        _title = title;
        _width = width;
        _height = height;
    }

    public void Run(string[]? args = null)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

        WindowsDpiAwareness.TryEnablePerMonitorV2();

        // Set static dispatcher delegate for asynchronous work
        Microsoft.UI.Xaml.Input.InputSystem.DispatcherQueue = UIThread.Post;

        // Launch the App
        var app = new TApp();
        Application.Current = app;
        app.Launch(new LaunchActivatedEventArgs(args ?? Array.Empty<string>()));

        // Loop and run while windows are active
        while (WindowManager.ActiveWindows.Count > 0)
        {
            var activeWindows = WindowManager.ActiveWindows;
            var allWindowsUseVSync = true;
            foreach (var activeWindow in activeWindows)
            {
                if (activeWindow.SilkWindow != null)
                {
                    allWindowsUseVSync &= activeWindow.SilkWindow.VSync;
                    if (!activeWindow.SilkWindow.IsInitialized)
                    {
                        activeWindow.SilkWindow.Initialize();
                    }
                    activeWindow.SilkWindow.DoEvents();
                    if (activeWindow.SilkWindow != null)
                    {
                        activeWindow.SilkWindow.DoUpdate();
                    }
                    if (activeWindow.SilkWindow != null)
                    {
                        activeWindow.SilkWindow.DoRender();
                    }
                }
            }

            // Present blocks the loop for synchronized windows. An unconditional one
            // millisecond sleep also throttled explicitly uncapped windows by hundreds
            // of frames per second and amplified any scene-compilation regression.
            if (allWindowsUseVSync)
            {
                System.Threading.Thread.Yield();
            }
        }
    }

    public Task RunAsync(string[]? args = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (WindowHostServices.Current is { } host)
        {
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
            Microsoft.UI.Xaml.Input.InputSystem.DispatcherQueue = UIThread.Post;

            var app = new TApp();
            Application.Current = app;
            app.Launch(new LaunchActivatedEventArgs(args ?? Array.Empty<string>()));
            return host.RunAsync(cancellationToken);
        }
        Run(args);
        return Task.CompletedTask;
    }
}
