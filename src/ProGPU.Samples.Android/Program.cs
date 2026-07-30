using Android.App;
using Android.Content.PM;
using Microsoft.UI.Xaml;
using ProGPU.Android;

[assembly: UsesPermission(Android.Manifest.Permission.Internet)]

namespace ProGPU.Samples.Android;

[Activity(
    Label = "ProGPU Samples",
    Theme = "@android:style/Theme.Material.NoActionBar",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize |
        ConfigChanges.UiMode |
        ConfigChanges.Density |
        ConfigChanges.Keyboard |
        ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : ProGpuActivity
{
    private IDisposable? _mediaRegistration;

    protected override Task LaunchProGpuApplicationAsync()
    {
        _mediaRegistration ??=
            ProGPU.Android.Media.AndroidMedia.Register();
        return AppBuilder<ProGPU.Samples.App>
            .Configure()
            .WithTitle("ProGPU Samples")
            .Build()
            .RunAsync([]);
    }

    protected override void OnDestroy()
    {
        _mediaRegistration?.Dispose();
        _mediaRegistration = null;
        base.OnDestroy();
    }
}
