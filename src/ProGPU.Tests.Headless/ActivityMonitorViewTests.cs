using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Fonts.Inter;
using ProGPU.Samples.ActivityMonitor;
using ProGPU.Samples.ActivityMonitor.Monitoring;
using ProGPU.Samples.ActivityMonitor.Presentation;
using ProGPU.Text;
using Xunit;

namespace ProGPU.Tests.Headless;

[Collection("HeadlessTests")]
public sealed class ActivityMonitorViewTests
{
    [Fact]
    public void CpuTickDeltaHandlesThirtyTwoBitRollover()
    {
        Assert.Equal(
            4UL,
            MacOsActivityMonitorDataSource.ComputeTickDelta(
                current: 2,
                previous: uint.MaxValue - 1));
    }

    [Fact]
    public void ActivityMonitorRendersPopulatedCpuView()
    {
        Application previousApplication = Application.Current;
        ElementTheme previousTheme = ThemeManager.CurrentTheme;
        VisualThemeFamily previousFamily = ThemeManager.CurrentThemeFamily;
        TtfFont? previousPopupFont = PopupService.DefaultFont;
        try
        {
            var application = new App();
            Application.Current = application;
            InterFontFamily.RegisterFonts();
            FontApi.RegisterPlatformFallbackFont(InterFontFamily.Regular);
            PopupService.DefaultFont = InterFontFamily.Regular;
            var view = new ActivityMonitorView(InterFontFamily.Regular);
            ActivitySnapshot snapshot = CreateSnapshot();
            for (int sample = 0; sample < 120; sample++)
            {
                view.ApplySnapshot(snapshot with
                {
                    CapturedAt = snapshot.CapturedAt.AddSeconds(sample * 2),
                    System = snapshot.System with
                    {
                        UserCpuPercent = 18 + sample % 22,
                        SystemCpuPercent = 8 + sample % 13
                    }
                });
            }

            using var window = new HeadlessWindow(1440, 900)
            {
                Content = view
            };
            window.Render();
            window.Render();
            byte[] pixels = window.ReadPixels();

            int lightPixels = 0;
            int bluePixels = 0;
            for (int index = 0; index < pixels.Length; index += 4)
            {
                byte red = pixels[index];
                byte green = pixels[index + 1];
                byte blue = pixels[index + 2];
                if (red > 210 && green > 210 && blue > 210)
                {
                    lightPixels++;
                }
                if (blue > 150 && blue > red + 35 && blue > green + 15)
                {
                    bluePixels++;
                }
            }

            window.SaveScreenshot(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "activity_monitor_cpu.png"));
            Assert.True(lightPixels > 500_000, $"Expected a light macOS surface, found {lightPixels} light pixels.");
            Assert.True(bluePixels > 50, $"Expected chart blue accents, found {bluePixels} blue pixels.");
            Assert.Equal(ActivityCategory.Cpu, view.ActiveCategory);
            Assert.Equal(48, view.VisibleProcessCount);

            foreach (ActivityCategory category in Enum.GetValues<ActivityCategory>())
            {
                view.SelectCategory(category);
                window.Render();
                window.Render();
                Assert.True(view.VisibleProcessCount > 0);
                byte[] categoryPixels = window.ReadPixels();
                int darkLeftPixels = 0;
                for (int y = 0; y < 700; y++)
                {
                    for (int x = 0; x < 400; x++)
                    {
                        int pixel = (y * 1440 + x) * 4;
                        if (categoryPixels[pixel] < 100 &&
                            categoryPixels[pixel + 1] < 100 &&
                            categoryPixels[pixel + 2] < 100)
                        {
                            darkLeftPixels++;
                        }
                    }
                }
                Assert.True(
                    darkLeftPixels > 200,
                    $"Expected {category} content in the left process column, found {darkLeftPixels} dark pixels.");
                window.SaveScreenshot(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    $"activity_monitor_{category.ToString().ToLowerInvariant()}.png"));
            }
        }
        finally
        {
            Application.Current = previousApplication;
            ThemeManager.CurrentTheme = previousTheme;
            ThemeManager.CurrentThemeFamily = previousFamily;
            PopupService.DefaultFont = previousPopupFont;
        }
    }

    [Fact]
    public void CategoriesAndSearchReconfigureTheProcessTable()
    {
        Application previousApplication = Application.Current;
        ElementTheme previousTheme = ThemeManager.CurrentTheme;
        VisualThemeFamily previousFamily = ThemeManager.CurrentThemeFamily;
        TtfFont? previousPopupFont = PopupService.DefaultFont;
        try
        {
            Application.Current = new App();
            InterFontFamily.RegisterFonts();
            PopupService.DefaultFont = InterFontFamily.Regular;
            var view = new ActivityMonitorView(InterFontFamily.Regular);
            view.ApplySnapshot(CreateSnapshot());

            view.SelectCategory(ActivityCategory.Memory);
            Assert.Contains("Real Mem", view.ColumnHeaders);
            Assert.Equal(48, view.VisibleProcessCount);

            view.SelectProcessScope(ActivityProcessScope.SystemProcesses);
            Assert.Equal(ActivityProcessScope.SystemProcesses, view.ProcessScope);
            Assert.Equal(9, view.VisibleProcessCount);

            view.SelectProcessScope(ActivityProcessScope.OtherUsersProcesses);
            Assert.Equal(39, view.VisibleProcessCount);

            view.SelectProcessScope(ActivityProcessScope.AllProcesses);
            view.SelectCategory(ActivityCategory.Energy);
            Assert.Contains("Energy Impact", view.ColumnHeaders);
            Assert.DoesNotContain("12 hr Power", view.ColumnHeaders);
            Assert.Equal(12, view.VisibleProcessCount);

            view.SetSearchText("Process 08");
            Assert.Equal(1, view.VisibleProcessCount);

            view.SelectCategory(ActivityCategory.Disk);
            Assert.Contains("Bytes Written", view.ColumnHeaders);
            view.SelectCategory(ActivityCategory.Network);
            Assert.Contains("Rcvd Bytes", view.ColumnHeaders);

            Assert.Equal(1, view.HistoryPointCount(ActivityCategory.Cpu));
            view.SelectCategory(ActivityCategory.Cpu);
            view.SelectCategory(ActivityCategory.Memory);
            view.SelectCategory(ActivityCategory.Cpu);
            Assert.Equal(1, view.HistoryPointCount(ActivityCategory.Cpu));
        }
        finally
        {
            Application.Current = previousApplication;
            ThemeManager.CurrentTheme = previousTheme;
            ThemeManager.CurrentThemeFamily = previousFamily;
            PopupService.DefaultFont = previousPopupFont;
        }
    }

    [Fact]
    public void ProcessInspectorRendersSummaryTabsAndOpenFileContent()
    {
        Application previousApplication = Application.Current;
        ElementTheme previousTheme = ThemeManager.CurrentTheme;
        VisualThemeFamily previousFamily = ThemeManager.CurrentThemeFamily;
        TtfFont? previousPopupFont = PopupService.DefaultFont;
        try
        {
            Application.Current = new App();
            InterFontFamily.RegisterFonts();
            PopupService.DefaultFont = InterFontFamily.Regular;
            ProcessSnapshot process = CreateSnapshot().Processes[0];
            var details = new ProcessDetails(
                process.ProcessId,
                process.ParentProcessId,
                process.Name,
                process.User,
                process.ExecutablePath,
                $"{process.ExecutablePath} --headless",
                DateTimeOffset.Now.AddMinutes(-4),
                process,
                ["/Applications/Sample.app/Contents/MacOS/Sample", "TCP 127.0.0.1:8080"]);
            var inspector = new ProcessInspectorView(InterFontFamily.Regular, details);

            using var window = new HeadlessWindow(800, 500)
            {
                Content = inspector
            };
            window.Render();
            window.Render();
            byte[] pixels = window.ReadPixels();
            int darkPixels = 0;
            for (int index = 0; index < pixels.Length; index += 4)
            {
                if (pixels[index] < 120 &&
                    pixels[index + 1] < 120 &&
                    pixels[index + 2] < 120)
                {
                    darkPixels++;
                }
            }

            window.SaveScreenshot(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "activity_monitor_inspector.png"));
            Assert.True(
                darkPixels > 500,
                $"Expected inspector labels and values, found {darkPixels} dark pixels.");
        }
        finally
        {
            Application.Current = previousApplication;
            ThemeManager.CurrentTheme = previousTheme;
            ThemeManager.CurrentThemeFamily = previousFamily;
            PopupService.DefaultFont = previousPopupFont;
        }
    }

    private static ActivitySnapshot CreateSnapshot()
    {
        var processes = Enumerable.Range(1, 48)
            .Select(index => new ProcessSnapshot(
                1000 + index,
                1,
                1000,
                $"Process {index:00}",
                index % 5 == 0 ? "root" : "sample-user",
                DateTimeOffset.UtcNow.AddMinutes(-index),
                96 - index * 1.3,
                TimeSpan.FromSeconds(index * 17),
                2 + index % 20,
                index * 38_000_000L,
                index * 62_000_000L,
                index * 1_200_000L,
                index * 1_800_000L,
                index * 400_000L,
                index * 300_000L,
                index * 400L,
                index * 300L,
                50 - index * 0.7,
                index * 7,
                20 + index,
                index % 8,
                TimeSpan.FromSeconds(index),
                false,
                index % 9 == 0,
                index % 3 == 0 ? "Apple" : "Other",
                $"/Applications/Process {index:00}.app/Contents/MacOS/Process {index:00}",
                index % 4 == 0))
            .ToArray();
        var system = new SystemSnapshot(
            24.5,
            17.5,
            58,
            19_327_352_832,
            13_400_000_000,
            2_100_000_000,
            7_000_000_000,
            2_600_000_000,
            1_300_000_000,
            4_200_000_000,
            42_000_000_000,
            28_000_000_000,
            410_000,
            280_000,
            9_000_000_000,
            6_000_000_000,
            90_000,
            60_000,
            processes.Length,
            processes.Sum(process => process.ThreadCount),
            new BatterySnapshot(true, 78, false, "AC Power", "3:20"));
        return new ActivitySnapshot(DateTimeOffset.UtcNow, processes, system);
    }
}
