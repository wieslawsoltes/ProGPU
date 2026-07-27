using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Simple;

namespace ProGPU.Avalonia.HeadlessPixelTests;

public sealed class TestApplication : Application
{
    public TestApplication()
    {
        Styles.Add(new SimpleTheme());
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<TestApplication>()
            .WithInterFont()
            .UseHarfBuzz()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false
            });
    }
}
