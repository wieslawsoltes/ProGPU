using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.SilkNet;
using Avalonia.Threading;

namespace ProGpuPackageApp;

internal sealed class App : Application
{
    private const string MultiWindowSmokeVariable =
        "PROGPU_INTEGRATION_MULTI_WINDOW_SMOKE";
    private const string ProfileHoldVariable =
        "PROGPU_INTEGRATION_PROFILE_HOLD_SECONDS";
    private static readonly Color Surface = Color.Parse("#22252B");
    private static readonly Color Border = Color.Parse("#343840");
    private static readonly Color PrimaryText = Color.Parse("#F7F7F2");
    private static readonly Color SecondaryText = Color.Parse("#AEB4BE");
    private static readonly bool s_multiWindowSmokeEnabled =
        Environment.GetEnvironmentVariable(MultiWindowSmokeVariable) == "1";
    private static readonly int s_profileHoldSeconds =
        GetProfileHoldSeconds();
    private static int s_multiWindowSmokeStage;
    private static int s_sharedDevicePairCount;
    private static int s_deviceOwnerDisposed;
    private static int s_deviceBorrowerDisposed;
    private static int s_survivorHealthyAfterOwnerDispose;
    private static int s_survivorHealthyAfterBorrowerDispose;
    private static int s_multiWindowSmokeCompleted;
    private static int s_multiWindowSmokeTimedOut;

    internal static bool MultiWindowSmokeEnabled =>
        s_multiWindowSmokeEnabled;
    internal static int MultiWindowSmokeStage =>
        Volatile.Read(ref s_multiWindowSmokeStage);
    internal static int SharedDevicePairCount =>
        Volatile.Read(ref s_sharedDevicePairCount);
    internal static bool DeviceOwnerDisposed =>
        Volatile.Read(ref s_deviceOwnerDisposed) != 0;
    internal static bool DeviceBorrowerDisposed =>
        Volatile.Read(ref s_deviceBorrowerDisposed) != 0;
    internal static bool SurvivorHealthyAfterOwnerDispose =>
        Volatile.Read(ref s_survivorHealthyAfterOwnerDispose) != 0;
    internal static bool SurvivorHealthyAfterBorrowerDispose =>
        Volatile.Read(ref s_survivorHealthyAfterBorrowerDispose) != 0;
    internal static bool MultiWindowSmokeCompleted =>
        Volatile.Read(ref s_multiWindowSmokeCompleted) != 0;
    internal static bool MultiWindowSmokeTimedOut =>
        Volatile.Read(ref s_multiWindowSmokeTimedOut) != 0;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = CreateWindow();
            desktop.MainWindow = window;

            if (MultiWindowSmokeEnabled)
            {
                ConfigureMultiWindowSmoke(desktop, window);
            }
            else if (
                Environment.GetEnvironmentVariable(
                    "PROGPU_INTEGRATION_SMOKE") == "1")
            {
                window.WindowState = WindowState.Maximized;
                DispatcherTimer.RunOnce(
                    () => desktop.Shutdown(),
                    TimeSpan.FromSeconds(
                        s_profileHoldSeconds > 0
                            ? s_profileHoldSeconds
                            : 2));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureMultiWindowSmoke(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window deviceOwner)
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        deviceOwner.Title = "ProGPU Device Owner";
        WindowImpl? deviceOwnerImpl = null;

        deviceOwner.Opened += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var survivor = CreateWindow(
                    "ProGPU Shared-Device Survivor",
                    560,
                    420);
                survivor.Position = new PixelPoint(180, 160);
                survivor.Opened += (_, _) =>
                {
                    if (deviceOwner.PlatformImpl is
                            WindowImpl openedOwnerImpl)
                    {
                        deviceOwnerImpl = openedOwnerImpl;
                        if (survivor.PlatformImpl is
                                WindowImpl survivorImpl &&
                            survivorImpl.SharesWebGpuDeviceWith(
                                openedOwnerImpl))
                        {
                            Interlocked.Increment(
                                ref s_sharedDevicePairCount);
                        }
                    }

                    DispatcherTimer.RunOnce(
                        () =>
                        {
                            deviceOwner.Close();
                            if (deviceOwnerImpl is not { } ownerImpl)
                            {
                                return;
                            }

                            _ = ContinueAfterDisposedAsync(
                                ownerImpl,
                                () =>
                                {
                                    Interlocked.Exchange(
                                        ref s_deviceOwnerDisposed,
                                        1);
                                    if (survivor.PlatformImpl is
                                            WindowImpl survivorImpl &&
                                        survivorImpl.HasActiveWebGpuContext)
                                    {
                                        Interlocked.Exchange(
                                            ref s_survivorHealthyAfterOwnerDispose,
                                            1);
                                    }

                                    Interlocked.Exchange(
                                        ref s_multiWindowSmokeStage,
                                        1);
                                    OpenAndDisposeBorrower(
                                        desktop,
                                        survivor);
                                });
                        },
                        TimeSpan.FromMilliseconds(350));
                };

                survivor.Show();
            });
        };

        DispatcherTimer.RunOnce(
            () =>
            {
                if (!MultiWindowSmokeCompleted)
                {
                    Interlocked.Exchange(
                        ref s_multiWindowSmokeTimedOut,
                        1);
                    desktop.Shutdown();
                }
            },
            TimeSpan.FromSeconds(
                checked(8 + s_profileHoldSeconds)));
    }

    private static void OpenAndDisposeBorrower(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window survivor)
    {
        var borrower = CreateWindow(
            "ProGPU Shared-Device Borrower",
            480,
            360);
        WindowImpl? borrowerImpl = null;
        borrower.Position = new PixelPoint(260, 220);
        borrower.Opened += (_, _) =>
        {
            if (borrower.PlatformImpl is WindowImpl openedBorrowerImpl)
            {
                borrowerImpl = openedBorrowerImpl;
                if (survivor.PlatformImpl is WindowImpl survivorImpl &&
                    openedBorrowerImpl.SharesWebGpuDeviceWith(survivorImpl))
                {
                    Interlocked.Increment(ref s_sharedDevicePairCount);
                }
            }

            DispatcherTimer.RunOnce(
                () =>
                {
                    borrower.Close();
                    if (borrowerImpl is not { } disposedBorrowerImpl)
                    {
                        return;
                    }

                    _ = ContinueAfterDisposedAsync(
                        disposedBorrowerImpl,
                        () =>
                        {
                            Interlocked.Exchange(
                                ref s_deviceBorrowerDisposed,
                                1);
                            if (survivor.PlatformImpl is
                                    WindowImpl survivorImpl &&
                                survivorImpl.HasActiveWebGpuContext)
                            {
                                Interlocked.Exchange(
                                    ref s_survivorHealthyAfterBorrowerDispose,
                                    1);
                            }

                            Interlocked.Exchange(
                                ref s_multiWindowSmokeStage,
                                2);
                            DispatcherTimer.RunOnce(
                                () =>
                                {
                                    Interlocked.Exchange(
                                        ref s_multiWindowSmokeCompleted,
                                        1);
                                    desktop.Shutdown();
                                },
                                s_profileHoldSeconds > 0
                                    ? TimeSpan.FromSeconds(
                                        s_profileHoldSeconds)
                                    : TimeSpan.FromMilliseconds(650));
                        });
                },
                TimeSpan.FromMilliseconds(350));
        };
        borrower.Show();
    }

    private static int GetProfileHoldSeconds()
    {
        string? value = Environment.GetEnvironmentVariable(
            ProfileHoldVariable);
        return int.TryParse(value, out int seconds) &&
               seconds is >= 1 and <= 120
            ? seconds
            : 0;
    }

    private static async Task ContinueAfterDisposedAsync(
        WindowImpl window,
        Action continuation)
    {
        try
        {
            await window.DisposedTask.WaitAsync(
                TimeSpan.FromSeconds(2));
        }
        catch
        {
            return;
        }

        Dispatcher.UIThread.Post(continuation);
    }

    private static Window CreateWindow(
        string title = "ProGPU Package Integration",
        double width = 680,
        double height = 540)
    {
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "ProGPU + Avalonia",
                    Foreground = new SolidColorBrush(PrimaryText),
                    FontSize = 30,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "Package-only integration smoke app",
                    Foreground = new SolidColorBrush(SecondaryText),
                    FontSize = 15,
                    Margin = new Thickness(0, 0, 0, 18)
                },
                CreateStatusRow("Renderer", "ProGPU / WebGPU", Color.Parse("#38D4C8")),
                CreateStatusRow("Windowing", "Silk.NET", Color.Parse("#FF6B5E")),
                CreateStatusRow("Integration", "12.0.5-preview.27", Color.Parse("#F4C95D")),
                new TextBlock
                {
                    Text = "Direct ProGPU + WGSL through IProGpuApiLeaseFeature",
                    Foreground = new SolidColorBrush(SecondaryText),
                    FontSize = 13,
                    Margin = new Thickness(0, 18, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                },
                new ProGpuLeaseView { Height = 92 }
            }
        };

        return new Window
        {
            Title = title,
            Width = width,
            Height = height,
            MinWidth = 520,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.Parse("#17191D")),
            Content = new Border
            {
                Padding = new Thickness(48),
                Child = content
            }
        };
    }

    private static Border CreateStatusRow(string label, string value, Color accent)
    {
        return new Border
        {
            Background = new SolidColorBrush(Surface),
            BorderBrush = new SolidColorBrush(Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 13),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new Border
                    {
                        Width = 9,
                        Height = 9,
                        CornerRadius = new CornerRadius(5),
                        Background = new SolidColorBrush(accent),
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = label,
                        Width = 150,
                        Foreground = new SolidColorBrush(SecondaryText),
                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = value,
                        Foreground = new SolidColorBrush(PrimaryText),
                        FontSize = 14,
                        FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };
    }
}
