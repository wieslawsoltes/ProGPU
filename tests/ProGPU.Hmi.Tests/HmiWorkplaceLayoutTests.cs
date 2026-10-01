using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Scene;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using ProGPU.WinUI.Hmi.Workplace;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiWorkplaceLayoutTests
{
    [Fact]
    public void ViewboxChildInvalidationTraversesTheMeasuredPresenter()
    {
        var child = new MeasurementProbe();
        var view = new Viewbox { Child = child };
        var root = new Grid(); root.AddChild(view);
        var size = new Vector2(800, 600);
        root.Measure(size); root.Arrange(new Rect(0, 0, 800, 600));
        int before = child.MeasureCount;
        child.ChangeDesiredSize(new Vector2(400, 300));
        root.Measure(size); root.Arrange(new Rect(0, 0, 800, 600));
        Assert.True(child.MeasureCount > before);
        Assert.Equal(new Vector2(400, 300), child.DesiredSize);
    }
    [Fact]
    public void CompactWorkplaceKeepsTheProcessViewportUsable()
    {
        using var studio = new HmiDcsStudio(automaticTicks: false);
        studio.Measure(new Vector2(1100, 760)); studio.Arrange(new Rect(0, 0, 1100, 760));
        Assert.False(studio.Operator.IsPlantExplorerVisible);
        Assert.True(studio.Operator.IsFaceplateVisible);
        Assert.NotNull(studio.Operator.ProcessView);
        Assert.True(studio.Operator.ProcessView!.Size.X > 0);
    }
    [Fact]
    public void UnloadingRetiresRuntimeAndProjectionButSupportsReattachment()
    {
        using var studio = new HmiDcsStudio(automaticTicks: false);
        studio.Measure(new Vector2(1600, 1000)); studio.Arrange(new Rect(0, 0, 1600, 1000));
        var old = studio.Operator.ProcessView;
        studio.Operator.Session.StartSimulation();
        studio.FireUnloaded(); studio.Operator.FireUnloaded();
        Assert.False(studio.Operator.Session.Runtime.IsRunning);
        Assert.Empty(old!.Controls);
        studio.Measure(new Vector2(1600, 1000)); studio.Arrange(new Rect(0, 0, 1600, 1000));
        Assert.NotSame(old, studio.Operator.ProcessView);
        Assert.NotEmpty(studio.Operator.ProcessView!.Controls);
    }
    [Fact]
    public void ReadOnlyAspectPickingIsOptInAndSuppressesEmbeddedCommands()
    {
        var p = HmiDemoProject.Create(); var runtime = new HmiRuntime(p); runtime.Start(true);
        using var view = new HmiScreenView(p, runtime);
        var pump = view.Controls.Single(c => c.Symbol == HmiSymbol.Pump);
        Assert.False(((Microsoft.UI.Xaml.Input.IHitTestBackgroundProvider)pump).HasHitTestBackground);
        view.ObjectSelectionEnabled = true;
        Assert.True(((Microsoft.UI.Xaml.Input.IHitTestBackgroundProvider)pump).HasHitTestBackground);
        Assert.All(view.Controls, c => Assert.False(c.CommandsEnabled));
        view.ObjectSelectionEnabled = false;
        Assert.All(view.Controls, c => Assert.True(c.CommandsEnabled));
        runtime.Stop();
    }
    private sealed class MeasurementProbe : FrameworkElement
    {
        private Vector2 _desired = new(200, 100);
        internal int MeasureCount;
        internal void ChangeDesiredSize(Vector2 desired) { _desired = desired; InvalidateMeasure(); }
        protected override Vector2 MeasureOverride(Vector2 availableSize) { MeasureCount++; return _desired; }
    }
}
