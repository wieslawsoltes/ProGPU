using Microsoft.UI.Xaml.Controls;
using ProGPU.WinUI.Designer;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiAuthoringPolishTests
{
    [Fact]
    public void DesignerRetainsControlInstancesAcrossPropertyEditsAndUndo()
    {
        using var host = new HmiDesignerHost();
        var original = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().ToDictionary(c => c.ElementId);
        var id = host.Session.GetProject().Screens[0].Elements[0].Id;
        host.Session.Edit("Rename caption", p => p.Screens[0].Elements[0].Label = "Updated caption");
        foreach (var control in host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>()) Assert.Same(original[control.ElementId], control);
        Assert.Equal("Updated caption", original[id].Label);
        host.Session.Undo();
        foreach (var control in host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>()) Assert.Same(original[control.ElementId], control);
        Assert.NotEqual("Updated caption", original[id].Label);
    }

    [Fact]
    public void AlignDoesNotTranslateAnAlreadyAlignedMemberTwice()
    {
        var project = new HmiProject
        {
            Screens = [new HmiScreen { Id = "overview", Elements =
            [new HmiElement { X = 0 }, new HmiElement { X = 50 }, new HmiElement { X = 100 }] }]
        };
        using var host = new HmiDesignerHost(project);
        var controls = host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().ToArray();
        host.WorkspaceCanvas.SelectElement(controls[1]);
        host.Selection.Select(controls[0]);
        host.Selection.Select(controls[1], true);
        host.Selection.Select(controls[2], true);
        host.Selection.Align(DesignerAlignment.Left);
        Assert.All(host.Session.GetProject().Screens[0].Elements, e => Assert.Equal(0f, e.X));
        Assert.False(host.Selection.IsExecutingCommand);
        host.Session.Undo();
        Assert.Equal(new[] { 0f, 50f, 100f }, host.Session.GetProject().Screens[0].Elements.Select(e => e.X));
    }

    [Fact]
    public void StaticEquipmentDoesNotAllocateHiddenOperatorEditors()
    {
        var tank = new HmiTank();
        Assert.Equal(5, tank.Children.Count);
        Assert.DoesNotContain(tank.Children, child => child is Button or TextBox or Grid);
        var definition = tank.CaptureDefinition();
        definition.Action.Kind = HmiActionKind.ToggleTag;
        definition.Action.Target = "Pump.Running";
        tank.ApplyDefinition(definition);
        Assert.Single(tank.Children.OfType<Button>());
        definition.Action.Kind = HmiActionKind.None;
        tank.ApplyDefinition(definition);
        Assert.Equal(5, tank.Children.Count);
    }

    [Fact]
    public void PendingOperatorInputSurvivesTelemetryUpdatesUntilReset()
    {
        var control = new HmiControl(HmiSymbol.NumericInput) { CommandsEnabled = true };
        var input = control.Children.OfType<Grid>().Single().Children.OfType<TextBox>().Single();
        control.UpdateSample(new(HmiValue.From(20d), HmiQuality.Good, DateTimeOffset.UnixEpoch));
        input.Text = "33.5";
        control.UpdateSample(new(HmiValue.From(44d), HmiQuality.Good, DateTimeOffset.UnixEpoch.AddSeconds(1)));
        Assert.True(control.HasPendingInput);
        Assert.Equal("33.5", input.Text);
        control.ResetPendingInput();
        Assert.False(control.HasPendingInput);
        Assert.Equal("44.0", input.Text);
    }

    [Theory]
    [InlineData(HmiSymbol.Fan)]
    [InlineData(HmiSymbol.Compressor)]
    [InlineData(HmiSymbol.Pump)]
    public void DiscreteEquipmentDoesNotPresentUnknownQualityAsStopped(HmiSymbol symbol)
    {
        var control = HmiControlCatalog.Create(symbol);
        control.UpdateSample(new(HmiValue.From(true), HmiQuality.Good, DateTimeOffset.UnixEpoch));
        Assert.Equal("RUNNING", control.DisplayText);
        control.UpdateSample(new(HmiValue.From(false), HmiQuality.Bad, DateTimeOffset.UnixEpoch));
        Assert.Equal("UNKNOWN", control.DisplayText);
    }

    [Fact]
    public void TrendSettingsAreDetachedRoundTrippedAndUndoable()
    {
        using var host = new HmiDesignerHost();
        host.Session.Edit("Configure trend", p => p.Screens[0].Elements.Single(e => e.Symbol == HmiSymbol.Trend).Trend.WindowSeconds = 300);
        var project = host.Session.GetProject();
        var element = project.Screens[0].Elements.Single(e => e.Symbol == HmiSymbol.Trend);
        var copy = element.Copy(); copy.Trend.WindowSeconds = 10;
        Assert.Equal(300d, element.Trend.WindowSeconds);
        Assert.False(HmiElementComparer.Equals(element, copy));
        Assert.Equal(300d, HmiProjectSerializer.Clone(project).Screens[0].Elements.Single(e => e.Symbol == HmiSymbol.Trend).Trend.WindowSeconds);
        host.Session.Undo();
        Assert.Equal(60d, host.Session.GetProject().Screens[0].Elements.Single(e => e.Symbol == HmiSymbol.Trend).Trend.WindowSeconds);
    }

    [Fact]
    public void EngineeringFindsAndLocatesAnUnboundInstrumentWithoutChangingTheDocument()
    {
        using var host = new HmiDesignerHost();
        host.AddComponent(HmiSymbol.Gauge);
        string before = host.Session.ExportJson();
        var report = host.AnalyzeProject();
        var issue = Assert.Single(report.Diagnostics, d => d.Code == "HMI2001");
        host.SelectComponent(issue.ScreenId, issue.ElementId);
        Assert.Equal(issue.ElementId, Assert.IsAssignableFrom<HmiControl>(host.WorkspaceCanvas.SelectedElement).ElementId);
        Assert.Equal(before, host.Session.ExportJson());
    }
}
