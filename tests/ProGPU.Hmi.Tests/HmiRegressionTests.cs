using System.Reflection;
using Microsoft.UI.Xaml;
using ProGPU.WinUI.Designer;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiRegressionTests
{
    [Theory]
    [InlineData(HmiAlarmCondition.High)]
    [InlineData(HmiAlarmCondition.Low)]
    public void ZeroDeadbandDoesNotChatterAtTheThreshold(HmiAlarmCondition condition)
    {
        var project = HmiDemoProject.Create();
        project.Alarms.Clear();
        project.Alarms.Add(new HmiAlarmDefinition { Id = "threshold", Tag = "Tank.Level", Condition = condition, Limit = 50, Deadband = 0 });
        var runtime = new HmiRuntime(project, DateTimeOffset.UnixEpoch);
        for (int i = 0; i < 5; i++)
        {
            var now = DateTimeOffset.UnixEpoch.AddSeconds(i);
            runtime.Publish(new Dictionary<string, HmiTagSample> { ["Tank.Level"] = new(HmiValue.From(50d), HmiQuality.Good, now) }, now);
            Assert.True(runtime.Alarms[0].IsActive);
        }
    }

    [Fact]
    public void UnknownAlarmQualityNeedsAttentionEvenWithoutAnActiveCondition()
    {
        var runtime = new HmiRuntime(HmiDemoProject.Create(), DateTimeOffset.UnixEpoch);
        var alarm = runtime.Alarms.Single(a => a.Definition.Id == "pressure-high");
        Assert.False(alarm.NeedsAttention);
        runtime.AdvanceTime(DateTimeOffset.UnixEpoch.AddSeconds(6));
        Assert.False(alarm.IsActive);
        Assert.True(alarm.IsQualityUnknown);
        Assert.True(alarm.NeedsAttention);
    }

    [Theory]
    [InlineData("CloneElement")]
    [InlineData("CloneElementForUndo")]
    public void ExistingVisualDesignerDoesNotDuplicateAtomicPrivateVisuals(string methodName)
    {
        HmiDesignerRegistration.Register();
        var host = new DesignerHost();
        var tank = new HmiTank();
        var definition = tank.CaptureDefinition();
        definition.Label = "TK-101";
        definition.Tag = "Tank.Level";
        definition.Group = "Process";
        tank.ApplyDefinition(definition);
        host.WorkspaceCanvas.DesignSurface.Children.Add(tank);
        var method = typeof(DesignerHost).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var clone = Assert.IsType<HmiTank>(method.Invoke(host, [tank]));
        Assert.Equal(tank.Children.Count, clone.Children.Count);
        Assert.Equal("TK-101", clone.Label);
        Assert.Equal("Tank.Level", clone.TagName);
        Assert.Equal("Process", clone.CaptureDefinition().Group);
        Assert.Empty(DesignerElementRegistry.GetLogicalChildren(clone));
    }

    [Fact]
    public void SharedOutlineVisibilityPersistsAndUndoRestoresIt()
    {
        using var host = new HmiDesignerHost();
        var tank = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Single(c => c.Symbol == HmiSymbol.Tank);
        string id = tank.CaptureDefinition().Id;
        tank.Visibility = Visibility.Collapsed;
        host.WorkspaceCanvas.NotifyCanvasModified();
        Assert.True(host.Session.GetProject().Screens[0].Elements.Single(e => e.Id == id).IsHidden);
        host.Session.Undo();
        Assert.False(host.Session.GetProject().Screens[0].Elements.Single(e => e.Id == id).IsHidden);
    }

    [Fact]
    public void DeletingALockedVisualIsReconciledEvenWhenTheModelDoesNotChange()
    {
        using var host = new HmiDesignerHost();
        string id = host.Session.GetProject().Screens[0].Elements[0].Id;
        host.Session.Edit("Lock", p => p.Screens[0].Elements[0].IsLocked = true);
        string locked = host.Session.ExportJson();
        var control = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().Single(c => c.CaptureDefinition().Id == id);
        host.WorkspaceCanvas.DesignSurface.Children.Remove(control);
        host.WorkspaceCanvas.NotifyCanvasModified();
        Assert.Equal(locked, host.Session.ExportJson());
        Assert.Contains(host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>(), c => c.CaptureDefinition().Id == id);
    }
}
