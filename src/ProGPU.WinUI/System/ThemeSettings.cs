using Microsoft.UI.Xaml;
using ProGPU.WinUI.Platform;
using Windows.Foundation;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.System;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class ThemeSettings
{
    private static readonly object RegistrySync = new();
    private static readonly List<WeakReference<ThemeSettings>> Registry = [];
    private static bool _isRegistrySubscribed;

    private bool _highContrast;
    private string _highContrastScheme;

    private ThemeSettings(WindowId windowId)
    {
        WindowId = windowId;
        (_highContrast, _highContrastScheme) = ReadCurrentState();
        Register(this);
    }

    public bool HighContrast => ReadCurrentState().HighContrast;

    public string HighContrastScheme =>
        ReadCurrentState().HighContrastScheme;

    public event TypedEventHandler<ThemeSettings, object>? Changed;

    internal WindowId WindowId { get; }

    public static ThemeSettings CreateForWindowId(WindowId windowId)
    {
        if (windowId.Value == 0)
        {
            throw new ArgumentException(
                "ThemeSettings requires a nonzero top-level WindowId.",
                nameof(windowId));
        }

        return new ThemeSettings(windowId);
    }

    private static (bool HighContrast, string HighContrastScheme)
        ReadCurrentState()
    {
        var provider = XamlPlatformResources.Provider;
        bool highContrast =
            provider?.IsHighContrast ?? ThemeManager.IsHighContrast;
        string scheme =
            highContrast &&
            provider is IHighContrastSchemeProvider schemeProvider
                ? schemeProvider.HighContrastScheme ?? string.Empty
                : string.Empty;
        return (highContrast, scheme);
    }

    private static void Register(ThemeSettings settings)
    {
        lock (RegistrySync)
        {
            PruneDeadRegistrations();
            Registry.Add(new WeakReference<ThemeSettings>(settings));
            if (_isRegistrySubscribed)
                return;

            ThemeManager.ThemeChanged += OnThemeChanged;
            _isRegistrySubscribed = true;
        }
    }

    private static void OnThemeChanged()
    {
        List<ThemeSettings> liveSettings;
        lock (RegistrySync)
        {
            PruneDeadRegistrations();
            liveSettings = new List<ThemeSettings>(Registry.Count);
            foreach (var registration in Registry)
            {
                if (registration.TryGetTarget(out var settings))
                    liveSettings.Add(settings);
            }
        }

        foreach (var settings in liveSettings)
            settings.NotifyIfChanged();
    }

    private static void PruneDeadRegistrations()
    {
        for (int index = Registry.Count - 1; index >= 0; index--)
        {
            if (!Registry[index].TryGetTarget(out _))
                Registry.RemoveAt(index);
        }
    }

    private void NotifyIfChanged()
    {
        var current = ReadCurrentState();
        if (_highContrast == current.HighContrast &&
            string.Equals(
                _highContrastScheme,
                current.HighContrastScheme,
                StringComparison.Ordinal))
        {
            return;
        }

        _highContrast = current.HighContrast;
        _highContrastScheme = current.HighContrastScheme;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
