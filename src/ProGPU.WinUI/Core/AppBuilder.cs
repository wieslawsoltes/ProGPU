using System;
using Silk.NET.Windowing;
using Microsoft.UI.Xaml.HotReload;
using ProGPU.Backend;

namespace Microsoft.UI.Xaml;

public class AppBuilder<TApp> where TApp : Application, new()
{
    private string _title = "ProGPU Application";
    private int _width = 1280;
    private int _height = 800;
    private Func<NativeWindowHandle, uint, uint, WgpuContext>?
        _gpuContextFactory;

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

    /// <summary>
    /// Configures a host-specific WebGPU device and presentation surface
    /// after the native window exists but before compositor initialization.
    /// </summary>
    public AppBuilder<TApp> WithGpuContextFactory(
        Func<NativeWindowHandle, uint, uint, WgpuContext> factory)
    {
        _gpuContextFactory =
            factory ??
            throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public AppRunner<TApp> Build()
    {
        return new AppRunner<TApp>(
            _title,
            _width,
            _height,
            _gpuContextFactory);
    }
}

public class AppRunner<TApp> where TApp : Application, new()
{
    private readonly string _title;
    private readonly int _width;
    private readonly int _height;
    private readonly Func<
        NativeWindowHandle,
        uint,
        uint,
        WgpuContext>? _gpuContextFactory;

    internal AppRunner(
        string title,
        int width,
        int height,
        Func<NativeWindowHandle, uint, uint, WgpuContext>?
            gpuContextFactory)
    {
        _title = title;
        _width = width;
        _height = height;
        _gpuContextFactory = gpuContextFactory;
    }

    public void Run(string[]? args = null)
    {
        global::System.Globalization.CultureInfo.DefaultThreadCurrentCulture = global::System.Globalization.CultureInfo.InvariantCulture;
        global::System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = global::System.Globalization.CultureInfo.InvariantCulture;
        global::System.Threading.Thread.CurrentThread.CurrentCulture = global::System.Globalization.CultureInfo.InvariantCulture;
        global::System.Threading.Thread.CurrentThread.CurrentUICulture = global::System.Globalization.CultureInfo.InvariantCulture;

        WindowsDpiAwareness.TryEnablePerMonitorV2();

        // Set static dispatcher delegate for asynchronous work
        Microsoft.UI.Xaml.Input.InputSystem.DispatcherQueue = UIThread.Post;
        HotReloadManager.Initialize("Desktop");

        var previousFactory = Window.GpuContextFactory;
        Window.GpuContextFactory = _gpuContextFactory;
        try
        {
            // Launch the App
            var app = new TApp();
            Application.Current = app;
            app.Launch(
                new LaunchActivatedEventArgs(
                    args ?? Array.Empty<string>()));

            // Loop and run while windows are active
            while (WindowManager.ActiveWindows.Count > 0)
            {
                var activeWindows = WindowManager.ActiveWindows;
                var allWindowsUseVSync = true;
                var presentedAnyFrame = false;
                foreach (var activeWindow in activeWindows)
                {
                    if (activeWindow.SilkWindow != null)
                    {
                        allWindowsUseVSync &=
                            activeWindow.SilkWindow.VSync;
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
                            presentedAnyFrame |=
                                activeWindow.PresentedScheduledFrame;
                        }
                    }
                }

                // A synchronized present blocks the active loop. When every retained scene
                // is unchanged there is no present to provide that backpressure, so yield
                // one millisecond instead of spinning on dispatcher/layout checks.
                if (!presentedAnyFrame)
                {
                    global::System.Threading.Thread.Sleep(1);
                }
                else if (allWindowsUseVSync)
                {
                    global::System.Threading.Thread.Yield();
                }
            }
        }
        finally
        {
            Window.GpuContextFactory = previousFactory;
        }
    }

    public Task RunAsync(string[]? args = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (WindowHostServices.Current is { } host)
        {
            global::System.Globalization.CultureInfo.DefaultThreadCurrentCulture = global::System.Globalization.CultureInfo.InvariantCulture;
            global::System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = global::System.Globalization.CultureInfo.InvariantCulture;
            global::System.Threading.Thread.CurrentThread.CurrentCulture = global::System.Globalization.CultureInfo.InvariantCulture;
            global::System.Threading.Thread.CurrentThread.CurrentUICulture = global::System.Globalization.CultureInfo.InvariantCulture;
            Microsoft.UI.Xaml.Input.InputSystem.DispatcherQueue = UIThread.Post;
            HotReloadManager.Initialize(WindowHostServices.Current.GetType().Name);

            var app = new TApp();
            Application.Current = app;
            app.Launch(new LaunchActivatedEventArgs(args ?? Array.Empty<string>()));
            return host.RunAsync(cancellationToken);
        }
        Run(args);
        return Task.CompletedTask;
    }
}
