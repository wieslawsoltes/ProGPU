using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;
using ProGPU.WinUI.Designer;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiDesignerTests
{
    [Fact]
    public void SharedJournalTracksSavedStateAndDropsRedoBranches()
    {
        var history = new DesignerHistory<string>("a", capacity: 3, byteBudget: 20, measure: s => s.Length);
        history.Record("b", "B"); history.MarkSaved(); history.Record("c", "C");
        Assert.True(history.IsDirty); Assert.Equal("b", history.Undo()); Assert.False(history.IsDirty);
        history.Record("d", "D"); Assert.False(history.CanRedo);
        history.Record("e", "E"); Assert.Equal("d", history.Undo()); Assert.Equal("b", history.Undo()); Assert.False(history.CanUndo);
    }
    [Fact]
    public void SessionEditsAreTransactionalAndUndoable()
    {
        var session = new HmiDesignerSession(); string initial = session.ExportJson();
        Assert.Throws<InvalidDataException>(() => session.Edit("Invalid", p => p.StartScreenId = "missing"));
        Assert.Equal(initial, session.ExportJson()); Assert.False(session.CanUndo);
        session.Edit("Rename", p => p.Name = "Renamed"); Assert.True(session.IsDirty);
        session.Undo(); Assert.Equal(initial, session.ExportJson()); Assert.False(session.IsDirty);
        session.Redo(); Assert.Equal("Renamed", session.GetProject().Name);
    }
    [Fact]
    public void TagRenameUpdatesBindingsActionsAlarmsAndRecipes()
    {
        var session = new HmiDesignerSession();
        session.RenameTag("Pump.Running", "Pump.Enabled");
        var project = session.GetProject();
        Assert.DoesNotContain(project.Tags, t => t.Name == "Pump.Running");
        Assert.Contains(project.Tags, t => t.Name == "Pump.Enabled");
        Assert.Equal("Pump.Enabled", project.Alarms.Single(a => a.Id == "pump-stopped").Tag);
        Assert.All(project.Recipes, r => Assert.True(r.Values.ContainsKey("Pump.Enabled")));
        Assert.DoesNotContain(project.Screens.SelectMany(s => s.Elements), e => e.Tag == "Pump.Running" || e.Action.Target == "Pump.Running");
    }
    [Fact]
    public void ScreensCannotBeDeletedWhileReferenced()
    {
        var session = new HmiDesignerSession();
        Assert.Throws<InvalidOperationException>(session.DeleteScreen);
        session.AddScreen(); var id = session.ActiveScreenId;
        session.DeleteScreen(); Assert.DoesNotContain(session.GetProject().Screens, s => s.Id == id);
        session.Undo(); Assert.Contains(session.GetProject().Screens, s => s.Id == id);
    }
    [Fact]
    public void SavingOlderRevisionDoesNotClearNewerEdits()
    {
        var session = new HmiDesignerSession(); string old = session.ExportJson();
        session.Edit("Rename", p => p.Name = "New name"); session.MarkSaved(old);
        Assert.True(session.IsDirty);
        session.MarkSaved(session.ExportJson()); Assert.False(session.IsDirty);
    }
    [Fact]
    public void EveryControlRegistersClonesStateAndHidesPrivateChildren()
    {
        HmiDesignerRegistration.Register();
        Assert.Equal(Enum.GetValues<HmiSymbol>().Length, HmiControlCatalog.Items.Count);
        foreach (var descriptor in HmiControlCatalog.Items)
        {
            Assert.True(DesignerElementRegistry.TryCreate(HmiDesignerRegistration.ToolboxKey(descriptor.Symbol), null, out var element));
            var control = Assert.IsAssignableFrom<HmiControl>(element);
            var definition = control.CaptureDefinition();
            definition.Label = "Custom label"; definition.Tag = "Tag.X"; definition.Group = "group"; definition.IsLocked = true;
            definition.Action = new HmiAction { Kind = HmiActionKind.Navigate, Target = "screen" };
            control.ApplyDefinition(definition);
            Assert.True(DesignerElementRegistry.TryCreateLike(control, out var copy));
            var cloned = Assert.IsAssignableFrom<HmiControl>(copy);
            Assert.Equal(definition.Symbol, cloned.Symbol); Assert.Equal(definition.Label, cloned.Label);
            Assert.Equal("Tag.X", cloned.TagName); Assert.Equal("group", cloned.CaptureDefinition().Group);
            Assert.True(cloned.IsDesignLocked); Assert.False(cloned.CommandsEnabled);
            Assert.Empty(DesignerElementRegistry.GetLogicalChildren(control));
            Assert.False(DesignerElementRegistry.IsDropContainer(control));
            Assert.False(DesignerElementRegistry.TryAddChild(control, new Button()));
            cloned.CaptureDefinition().Action.Target = "changed";
            Assert.Equal("screen", control.CaptureDefinition().Action.Target);
        }
    }
    [Fact]
    public void SharedSelectionCommandsAlignDistributeAndRespectLocks()
    {
        var canvas = new DesignerCanvas();
        var first = new Button { Width = 20, Height = 20 }; var middle = new Button { Width = 20, Height = 20 }; var last = new Button { Width = 20, Height = 20 };
        Canvas.SetLeft(first, 0); Canvas.SetLeft(middle, 25); Canvas.SetLeft(last, 100);
        canvas.DesignSurface.Children.Add(first); canvas.DesignSurface.Children.Add(middle); canvas.DesignSurface.Children.Add(last);
        var selection = new DesignerSelectionService(canvas); selection.SelectAll(); selection.Distribute(true);
        Assert.Equal(50f, Canvas.GetLeft(middle));
        selection.Align(DesignerAlignment.Left); Assert.Equal(0f, Canvas.GetLeft(last));
        selection.CanEdit = e => e != first; selection.Translate(10, 5);
        Assert.Equal(0f, Canvas.GetLeft(first)); Assert.Equal(10f, Canvas.GetLeft(middle));
    }
    [Fact]
    public void RuntimeScreenUpdatesBindingsAndDisposesSubscriptions()
    {
        var project = HmiDemoProject.Create(); var runtime = new HmiRuntime(project, DateTimeOffset.UnixEpoch); runtime.Start(true);
        var view = new HmiScreenView(project, runtime);
        var pump = view.Controls.Single(c => c.Symbol == HmiSymbol.Pump);
        Assert.True(pump.IsActive);
        runtime.Write("Pump.Running", HmiValue.From(false)); Assert.False(pump.IsActive);
        runtime.Execute(new HmiAction { Kind = HmiActionKind.Navigate, Target = "operations" }); Assert.Equal("operations", view.ScreenId);
        view.Dispose();
        runtime.Write("Pump.Running", HmiValue.From(true));
        runtime.Execute(new HmiAction { Kind = HmiActionKind.Navigate, Target = "overview" });
        Assert.Empty(view.Controls);
    }
    [Fact]
    public void FullWorkbenchPreviewDoesNotChangeDesignHistory()
    {
        using var host = new HmiDesignerHost();
        string original = host.Session.ExportJson();
        host.StartPreview(automaticTicks: false);
        host.AdvancePreview(TimeSpan.FromSeconds(1));
        host.Runtime!.Write("Pump.Running", HmiValue.From(false));
        host.StopPreview();
        Assert.Equal(original, host.Session.ExportJson()); Assert.False(host.Session.IsDirty);
        host.AddComponent(HmiSymbol.Tank);
        Assert.True(host.Session.IsDirty); Assert.True(host.Session.CanUndo);
        host.Session.Undo(); Assert.Equal(original, host.Session.ExportJson());
    }
}
