using System;

namespace Windows.UI.Core;

public enum AppViewBackButtonVisibility
{
    Visible = 0,
    Collapsed = 1,
    Disabled = 2
}

public sealed class BackRequestedEventArgs : EventArgs
{
    public bool Handled { get; set; }
}

/// <summary>
/// Provides the WinUI system-back contract. Platform hosts call
/// <see cref="NotifyBackRequested"/> for their native back command or gesture.
/// </summary>
public sealed class SystemNavigationManager
{
    private static readonly SystemNavigationManager CurrentView = new();

    private SystemNavigationManager()
    {
    }

    public AppViewBackButtonVisibility AppViewBackButtonVisibility { get; set; } =
        AppViewBackButtonVisibility.Collapsed;

    public event EventHandler<BackRequestedEventArgs>? BackRequested;

    public static SystemNavigationManager GetForCurrentView() => CurrentView;

    /// <summary>
    /// Raises the current view's system-back event and returns whether application
    /// code handled it. This is a ProGPU platform-host extension.
    /// </summary>
    public bool NotifyBackRequested()
    {
        var args = new BackRequestedEventArgs();
        BackRequested?.Invoke(this, args);
        return args.Handled;
    }
}
