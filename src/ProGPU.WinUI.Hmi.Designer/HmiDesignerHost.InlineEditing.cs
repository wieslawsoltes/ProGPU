using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Hmi;
using ProGPU.WinUI.Designer;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private sealed record LabelGesture(string Document, string Screen, HmiControl Control, string Original);
    private LabelGesture? _labelGesture;
    private DesignerInlineTextEditor? _labelEditor;
    private bool _updatingLabelEditor;
    private long _labelEpoch;

    public bool IsEditingLabel => _labelGesture != null;
    public string? EditingLabelElementId => _labelGesture?.Control.ElementId;

    /// <summary>F2/double-click authoring. Uses the control's own caption rectangle and the shared adorner transform.</summary>
    public void BeginLabelEdit(string? elementId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPreviewing) throw new InvalidOperationException("Stop runtime preview before editing captions.");
        var control = elementId == null
            ? _selection.Selection.Count == 1 ? _selection.Selection[0] as HmiControl : null
            : _canvas.DesignSurface.Children.OfType<HmiControl>().SingleOrDefault(c => c.ElementId == elementId);
        if (control == null) throw new InvalidOperationException("Select one HMI component to edit its caption.");
        var definition = Session.ActiveScreen.Elements.SingleOrDefault(e => e.Id == control.ElementId);
        if (definition == null || definition.IsLocked || definition.IsHidden || control.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Show and unlock the selected component before editing its caption.");
        var bounds = control.CaptionBounds;
        if (bounds.Width < 8 || bounds.Height < 8) throw new InvalidOperationException("Enlarge this component to expose its caption.");
        CancelLabelEdit(); CancelRouteEdit(); CancelDiagramConnection();
        _canvas.SelectElement(control);
        _selection.Select(control);
        _labelEpoch++;
        _labelGesture = new(Session.ExportJson(), Session.ActiveScreenId, control, definition.Label);
        var editor = new DesignerInlineTextEditor
        {
            Name = "HmiInlineCaptionEditor", Text = definition.Label, Font = control.Font, FontSize = control.CaptionFontSize,
            TextAlignment = definition.Appearance.CaptionAlignment switch { HmiCaptionAlignment.Center => TextAlignment.Center, HmiCaptionAlignment.End => TextAlignment.Right, _ => TextAlignment.Left },
            Width = bounds.Width + 4, Height = Math.Max(bounds.Height + 4, control.CaptionFontSize * 1.5f + 4),
            Padding = new Thickness(2, 0), BorderThickness = new Thickness(1), CornerRadius = 2,
            Foreground = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Text),
            Background = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Surface),
            BorderBrush = HmiThemeResources.GetReference(ColorScheme, HmiBrushRole.Accent)
        };
        Canvas.SetLeft(editor, definition.X + bounds.X - 2);
        Canvas.SetTop(editor, definition.Y + bounds.Y - 2);
        _labelEditor = editor;
        editor.CommitRequested += () => Guard(CommitLabelEdit);
        editor.CancelRequested += CancelLabelEdit;
        editor.NextRequested += backward => Guard(() => AdvanceLabelEdit(backward));
        editor.TextChanged += (_, _) =>
        {
            if (_updatingLabelEditor || !ReferenceEquals(_labelEditor, editor) || _labelGesture == null) return;
            if (editor.Text.Length > 4096)
            {
                CancelLabelEdit();
                Status("Captions support at most 4096 UTF-16 code units.", true);
                return;
            }
            Guard(() => PreviewLabelText(editor.Text));
        };
        _canvas.AdornerSurface.Children.Add(editor);
        _canvas.AdornerSurface.IsHitTestVisible = true;
        InputSystem.SetFocus(editor);
        // A host focus observer may reenter and retire this edit.
        if (!ReferenceEquals(_labelEditor, editor)) return;
        editor.CaretIndex = editor.Text.Length;
        editor.SelectionStart = 0; editor.SelectionLength = editor.Text.Length;
        Status("Edit caption in place · Enter applies · Tab edits next · Escape or focus loss cancels · No runtime write");
    }

    /// <summary>Updates one retained caption without serializing the project or recording history.</summary>
    public void PreviewLabelText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var gesture = _labelGesture ?? throw new InvalidOperationException("Begin caption editing first.");
        if (text.Length > 4096) throw new ArgumentException("Caption exceeds the document text budget.", nameof(text));
        if (IsPreviewing || Session.ActiveScreenId != gesture.Screen || Session.ExportJson() != gesture.Document)
        { CancelLabelEdit(); throw new InvalidOperationException("The document changed; caption editing was canceled."); }
        gesture.Control.Label = text;
        if (_labelEditor != null && _labelEditor.Text != text)
        {
            _updatingLabelEditor = true;
            try { _labelEditor.Text = text; }
            finally { _updatingLabelEditor = false; }
        }
    }

    public void CommitLabelEdit()
    {
        var gesture = _labelGesture;
        if (gesture == null) return;
        if (_labelEditor is { IsComposing: true }) throw new InvalidOperationException("Complete the current input-method composition before applying the caption.");
        string text = _labelEditor?.Text ?? gesture.Control.Label;
        // Retire ownership before releasing focus: cancellation callbacks may run synchronously.
        long epoch = RetireLabelEditor();
        if (epoch != _labelEpoch || IsPreviewing || Session.ActiveScreenId != gesture.Screen || Session.ExportJson() != gesture.Document)
            throw new InvalidOperationException("The document changed; caption editing was canceled.");
        Session.Edit("Edit caption", project =>
        {
            var element = project.Screens.Single(s => s.Id == gesture.Screen).Elements.Single(e => e.Id == gesture.Control.ElementId);
            if (element.IsLocked || element.IsHidden) throw new InvalidOperationException("The component is not editable.");
            element.Label = text;
        });
        Status("Caption applied · Ctrl+Z restores the previous text");
    }

    public void AdvanceLabelEdit(bool backward = false)
    {
        var gesture = _labelGesture ?? throw new InvalidOperationException("Begin caption editing first.");
        var candidates = _canvas.DesignSurface.Children.OfType<HmiControl>()
            .Where(c => c.Visibility == Visibility.Visible && !c.IsDesignLocked && c.CaptionBounds.Width >= 8 && c.CaptionBounds.Height >= 8).ToArray();
        int index = Array.IndexOf(candidates, gesture.Control);
        CommitLabelEdit();
        if (index < 0 || candidates.Length == 0) return;
        string next = candidates[(index + (backward ? candidates.Length - 1 : 1)) % candidates.Length].ElementId;
        BeginLabelEdit(next);
    }

    public void CancelLabelEdit()
    {
        var gesture = _labelGesture;
        if (gesture == null) return;
        RetireLabelEditor();
    }

    private long RetireLabelEditor()
    {
        var editor = _labelEditor;
        var gesture = _labelGesture;
        long epoch = ++_labelEpoch;
        _labelGesture = null; _labelEditor = null;
        // Restore the old control before any focus observer can start another edit.
        if (gesture != null) gesture.Control.Label = gesture.Original;
        if (editor == null) return epoch;
        editor.CancelRequested -= CancelLabelEdit;
        _canvas.AdornerSurface.Children.Remove(editor);
        if (ReferenceEquals(InputSystem.FocusedElement, editor)) InputSystem.SetFocus(_canvas);
        return epoch;
    }

    private void HandleCaptionDoubleTap(DoubleTappedRoutedEventArgs args)
    {
        if (IsPreviewing || IsEditingRoute || IsConnectingDiagram || IsEditingLabel || _canvas.IsInteractionMode) return;
        var position = args.GetPosition(_canvas);
        float x = ((float)position.X - _canvas.PanOffset.X) / _canvas.ZoomScale;
        float y = ((float)position.Y - _canvas.PanOffset.Y) / _canvas.ZoomScale;
        // Match painter order; only the actual owned caption area starts text editing.
        foreach (var control in _canvas.DesignSurface.Children.OfType<HmiControl>().Reverse())
        {
            if (control.Visibility != Visibility.Visible) continue;
            float px = x - Canvas.GetLeft(control), py = y - Canvas.GetTop(control);
            if (px < 0 || px > control.Width || py < 0 || py > control.Height) continue;
            var b = control.CaptionBounds;
            // Covered captions must not be edited through foreground equipment.
            if (control.IsDesignLocked || px < b.X || px > b.Right || py < b.Y || py > b.Bottom) return;
            args.Handled = true;
            Guard(() => BeginLabelEdit(control.ElementId));
            return;
        }
    }
}
