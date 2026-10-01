using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.WinUI.Designer;
using ProGPU.WinUI.Hmi;
using ProGPU.WinUI.Hmi.Designer;
using Silk.NET.Input;
using Xunit;

namespace ProGPU.Hmi.Tests;

public sealed class HmiInlineEditingTests : IDisposable
{
    private readonly WindowInputState _previous = InputSystem.Current;
    public HmiInlineEditingTests() => InputSystem.Current = new WindowInputState();
    public void Dispose() { InputSystem.SetFocus(null); InputSystem.Current = _previous; }
    private static HmiControl Pump(HmiDesignerHost host) => host.WorkspaceCanvas.DesignSurface.Children.OfType<HmiControl>().First(c => c.Symbol == HmiSymbol.Pump);
    private static DesignerInlineTextEditor Editor() => Assert.IsType<DesignerInlineTextEditor>(InputSystem.FocusedElement);

    [Fact]
    public void NativeTextInputPreviewsOneRetainedControlAndEnterCommitsOneUndo()
    {
        using var host = new HmiDesignerHost(); var pump = Pump(host); string original = host.Session.ExportJson();
        int changes = 0; host.Session.Changed += () => changes++;
        var routes = host.DiagramLayer.RoutingPasses;
        host.BeginLabelEdit(pump.ElementId);
        InputSystem.InjectTextInput(TextInputEventKind.InsertText, "P-101 / Przepływ α");
        Assert.Equal("P-101 / Przepływ α", pump.Label);
        Assert.Equal(original, host.Session.ExportJson()); Assert.Equal(0, changes);
        Editor().OnKeyDown(new KeyRoutedEventArgs { Key = Key.Enter });
        Assert.False(host.IsEditingLabel); Assert.Equal(1, changes);
        Assert.Same(pump, Pump(host)); Assert.Equal(routes, host.DiagramLayer.RoutingPasses);
        Assert.Equal("P-101 / Przepływ α", Pump(host).Label);
        host.Session.Undo(); Assert.Equal(original, host.Session.ExportJson());
    }
    [Fact]
    public void EscapeAndFocusLossDiscardPendingTextWithoutHistory()
    {
        using var host = new HmiDesignerHost(); var pump = Pump(host); string original = host.Session.ExportJson(), label = pump.Label;
        host.BeginLabelEdit(pump.ElementId); host.PreviewLabelText("temporary");
        Editor().OnKeyDown(new KeyRoutedEventArgs { Key = Key.Escape });
        Assert.False(host.IsEditingLabel); Assert.Equal(label, pump.Label); Assert.Equal(original, host.Session.ExportJson());
        host.BeginLabelEdit(pump.ElementId); host.PreviewLabelText("temporary"); InputSystem.SetFocus(new TextBox());
        Assert.False(host.IsEditingLabel); Assert.Equal(label, pump.Label); Assert.Equal(original, host.Session.ExportJson());
    }
    [Fact]
    public void ForeignEditsAreNeverOverwrittenByPendingCaption()
    {
        using var host = new HmiDesignerHost(); var pump = Pump(host);
        host.BeginLabelEdit(pump.ElementId); host.PreviewLabelText("pending");
        host.Session.Edit("Foreign caption", p => p.Screens[0].Elements.Single(e => e.Id == pump.ElementId).Label = "authoritative");
        host.CommitLabelEdit();
        Assert.False(host.IsEditingLabel); Assert.Equal("authoritative", Pump(host).Label);
    }
    [Fact]
    public void UnchangedEnterDoesNotCreateHistory()
    {
        using var host = new HmiDesignerHost(); host.BeginLabelEdit(Pump(host).ElementId); host.CommitLabelEdit();
        Assert.False(host.Session.CanUndo); Assert.False(host.Session.IsDirty);
    }
    [Fact]
    public void NativeImeCompositionIsNotCommittedByEnterBeforeCompletion()
    {
        using var host = new HmiDesignerHost(); var pump = Pump(host); string before = host.Session.ExportJson();
        host.BeginLabelEdit(pump.ElementId);
        InputSystem.InjectTextInput(TextInputEventKind.CompositionStarted, isComposing: true);
        InputSystem.InjectTextInput(TextInputEventKind.CompositionUpdated, "圧", true);
        Assert.True(Editor().IsComposing);
        Editor().OnKeyDown(new KeyRoutedEventArgs { Key = Key.Enter });
        Assert.True(host.IsEditingLabel); Assert.Equal(before, host.Session.ExportJson());
        Assert.Throws<InvalidOperationException>(host.CommitLabelEdit);
        InputSystem.InjectTextInput(TextInputEventKind.CompositionCompleted, "圧力");
        Assert.False(Editor().IsComposing); Editor().OnKeyDown(new KeyRoutedEventArgs { Key = Key.Enter });
        Assert.False(host.IsEditingLabel); Assert.Equal("圧力", pump.Label);
    }
    [Fact]
    public void OverBudgetNativePasteCancelsRatherThanCorruptingCaretOrDocument()
    {
        using var host = new HmiDesignerHost(); var pump = Pump(host); string before = host.Session.ExportJson();
        host.BeginLabelEdit(pump.ElementId); InputSystem.InjectTextInput(TextInputEventKind.InsertText, new string('x', 4097));
        Assert.False(host.IsEditingLabel); Assert.Equal(before, host.Session.ExportJson());
        host.BeginLabelEdit(pump.ElementId); InputSystem.InjectTextInput(TextInputEventKind.InsertText, "valid"); host.CommitLabelEdit();
        Assert.Equal("valid", pump.Label);
    }
    [Fact]
    public void LockRuntimeAndCanvasMutationsCannotCommitDraftLabels()
    {
        using var host = new HmiDesignerHost(); var pump = Pump(host); string before = host.Session.ExportJson();
        host.BeginLabelEdit(pump.ElementId); host.PreviewLabelText("draft"); host.WorkspaceCanvas.NotifyCanvasModified();
        Assert.Equal(before, host.Session.ExportJson()); Assert.False(host.IsEditingLabel);
        host.StartPreview(automaticTicks: false);
        Assert.Throws<InvalidOperationException>(() => host.BeginLabelEdit(pump.ElementId)); host.StopPreview();
        host.Session.Edit("Lock", p => p.Screens[0].Elements.Single(e => e.Id == pump.ElementId).IsLocked = true);
        Assert.Throws<InvalidOperationException>(() => host.BeginLabelEdit(pump.ElementId));
    }
    [Fact]
    public void TabCommitsOnceAndEditsTheNextVisibleUnlockedCaption()
    {
        using var host = new HmiDesignerHost(); var pump = Pump(host); string original = host.Session.ExportJson();
        host.BeginLabelEdit(pump.ElementId); host.PreviewLabelText("Tab caption");
        Editor().OnKeyDown(new KeyRoutedEventArgs { Key = Key.Tab });
        Assert.True(host.IsEditingLabel); Assert.NotEqual(pump.ElementId, host.EditingLabelElementId);
        Assert.Equal("Tab caption", pump.Label); host.CancelLabelEdit();
        host.Session.Undo(); Assert.Equal(original, host.Session.ExportJson());
        host.BeginLabelEdit(pump.ElementId); host.AdvanceLabelEdit(backward: true);
        Assert.NotEqual(pump.ElementId, host.EditingLabelElementId);
    }

    [Fact]
    public void FocusReentrancyCannotCommitAnOldCaptionIntoANewEdit()
    {
        using var host = new HmiDesignerHost(); var pump = Pump(host); string original = host.Session.ExportJson();
        host.BeginLabelEdit(pump.ElementId); host.PreviewLabelText("old draft");
        bool reentered = false;
        InputSystem.Current.FocusChanged = focused =>
        {
            if (!ReferenceEquals(focused, host.WorkspaceCanvas) || reentered) return;
            reentered = true; host.BeginLabelEdit(pump.ElementId); host.PreviewLabelText("new draft");
        };
        Assert.Throws<InvalidOperationException>(host.CommitLabelEdit);
        Assert.True(host.IsEditingLabel); Assert.Equal("new draft", pump.Label); Assert.Equal(original, host.Session.ExportJson());
        InputSystem.Current.FocusChanged = null;
        host.CancelLabelEdit(); Assert.Equal(original, host.Session.ExportJson());
    }
}
