using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ProGpuEcosystemCompatibility;

internal sealed partial class EcosystemView : UserControl
{
    public EcosystemView()
    {
        InitializeComponent();
    }

    public void LoadAndVerify(string markup)
    {
        SkiaSvgControl.Source = markup;
        WaitForSkiaSource();
        if (SkiaSvgControl.SkSvg?.Picture is not { } skiaPicture ||
            skiaPicture.CullRect.Width != 64f ||
            skiaPicture.CullRect.Height != 64f)
        {
            throw new InvalidOperationException(
                "Svg.Controls.Skia.Avalonia did not load the SVG picture.");
        }

        AvaloniaSvgControl.Source = markup;
        if (AvaloniaSvgControl.Model is not { } avaloniaPicture ||
            avaloniaPicture.CullRect.Width != 64f ||
            avaloniaPicture.CullRect.Height != 64f)
        {
            throw new InvalidOperationException(
                "Svg.Controls.Avalonia did not record the SVG picture.");
        }

        var context = WebCanvas.GetContext("2d");
        context.strokeStyle = "#1d4ed8";
        context.lineWidth = 3d;
        context.beginPath();
        context.moveTo(4d, 4d);
        context.lineTo(60d, 60d);
        context.stroke();
        if (context.strokeStyle != "#ff1d4ed8" ||
            context.lineWidth != 3d)
        {
            throw new InvalidOperationException(
                "The WebScene canvas package probe did not preserve state.");
        }

        if (WebComponent.State !=
                WebScene.Sdk.Avalonia.WebSceneComponentHostState.Idle ||
            WebComponent.View.RenderDiagnostics is null)
        {
            throw new InvalidOperationException(
                "The WebScene Avalonia component host was not initialized.");
        }
    }

    public ValueTask DisposeWebSceneAsync() =>
        WebComponent.DisposeAsync();

    private void WaitForSkiaSource()
    {
        var deadline = Stopwatch.StartNew();
        while (SkiaSvgControl.SkSvg?.Picture is null &&
               deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Dispatcher.UIThread.RunJobs();
    }
}
