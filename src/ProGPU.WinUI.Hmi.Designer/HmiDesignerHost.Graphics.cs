using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private HmiAppearance? _copiedGraphicFormat;
    private readonly List<(Button Button, HmiGraphicStyle Style)> _graphicStyleButtons = [];
    private StackPanel? _graphicInspector;

    private bool HasEditableGraphics => !IsPreviewing && _selection.Selection.Count > 0 &&
        _selection.Selection.All(c => c is HmiControl { IsDesignLocked: false, Visibility: Visibility.Visible });

    private FrameworkElement BuildGraphicInspector()
    {
        _graphicInspector = new StackPanel { Margin = new Thickness(0, 0, 0, 5) };
        var styles = Toolbar();
        foreach (var style in Enum.GetValues<HmiGraphicStyle>())
        {
            var current = style;
            var button = Command(style == HmiGraphicStyle.HighPerformance ? "High performance" : style.ToString(), () => SetGraphicStyle(current));
            button.Height = 27; button.Padding = new Thickness(5, 0);
            ToolTipService.SetToolTip(button, "Appearance convention, not a certified symbol library or alarm policy.");
            _graphicStyleButtons.Add((button, current));
            _studioCommands.Add((button, () => HasEditableGraphics));
            styles.AddChild(button);
        }
        _graphicInspector.AddChild(styles);
        var tools = Toolbar();
        foreach (var item in new (string Text, string Tip, Action Action)[] {
            ("Edit text", "F2 or double-click the caption. Enter applies; Escape cancels.", () => BeginLabelEdit()),
            ("Rotate", "Rotate glyph clockwise; text stays upright and nozzles reroute.", () => RotateSelectedGraphics()),
            ("Mirror", "Mirror local symbol geometry horizontally.", MirrorSelectedGraphics),
            ("Copy style", "Copy visual formatting only, without engineering bands, loop identity or bindings.", CopyGraphicFormat),
            ("Paste style", "Apply copied visual formatting in one undo transaction.", PasteGraphicFormat) })
        {
            var button = Command(item.Text, item.Action); button.Height = 27; button.Padding = new Thickness(5, 0);
            ToolTipService.SetToolTip(button, item.Tip); tools.AddChild(button);
            _studioCommands.Add((button, () => HasEditableGraphics));
        }
        _graphicInspector.AddChild(tools);
        return _graphicInspector;
    }

    private void RefreshGraphicInspector()
    {
        if (_graphicInspector == null) return;
        _graphicInspector.Visibility = _selection.Selection.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var styles = _selection.Selection.OfType<HmiControl>().Select(c => c.Appearance.GraphicStyle).Distinct().ToArray();
        foreach (var (button, style) in _graphicStyleButtons)
        {
            bool selected = styles.Length == 1 && styles[0] == style;
            button.BorderBrush = HmiThemeResources.GetReference(ColorScheme, selected ? HmiBrushRole.Accent : HmiBrushRole.Border);
            button.BorderThickness = new Thickness(selected ? 2 : 1);
        }
    }

    public void SetGraphicStyle(HmiGraphicStyle style)
    {
        if (!Enum.IsDefined(style)) throw new ArgumentOutOfRangeException(nameof(style));
        EditSelectedGraphics("Change graphic convention", e => e.Appearance.GraphicStyle = style);
    }
    public void RotateSelectedGraphics() => EditSelectedGraphics("Rotate equipment", e => e.Appearance.QuarterTurns = (e.Appearance.QuarterTurns + 1) % 4);
    public void MirrorSelectedGraphics() => EditSelectedGraphics("Mirror equipment", e => e.Appearance.MirrorHorizontal = !e.Appearance.MirrorHorizontal);
    public void CopyGraphicFormat()
    {
        var control = _selection.Selection.OfType<HmiControl>().FirstOrDefault() ?? throw new InvalidOperationException("Select a source component.");
        _copiedGraphicFormat = control.Appearance;
        Status("Visual format copied; tags, loop identities, engineering bands and actions are not copied.");
    }
    public void PasteGraphicFormat()
    {
        var a = _copiedGraphicFormat ?? throw new InvalidOperationException("Copy a component's graphic format first.");
        EditSelectedGraphics("Paste graphic format", e =>
        {
            var target = e.Appearance;
            target.GraphicStyle = a.GraphicStyle; target.Presentation = a.Presentation;
            target.CaptionFontSize = a.CaptionFontSize; target.CaptionAlignment = a.CaptionAlignment;
            target.ShowTagName = a.ShowTagName; target.ShowEngineeringRange = a.ShowEngineeringRange;
            target.ShowValue = a.ShowValue; target.ShowConnectionPorts = a.ShowConnectionPorts;
            target.QuarterTurns = a.QuarterTurns; target.MirrorHorizontal = a.MirrorHorizontal; target.MirrorVertical = a.MirrorVertical;
            target.AnimateFlow = a.AnimateFlow;
        });
    }
    private void EditSelectedGraphics(string description, Action<HmiElement> edit)
    {
        if (!HasEditableGraphics) throw new InvalidOperationException("Select visible, unlocked components and stop preview before formatting.");
        CancelLabelEdit(); CancelRouteEdit(); CancelDiagramConnection();
        var ids = _selection.Selection.OfType<HmiControl>().Select(c => c.ElementId).ToHashSet(StringComparer.Ordinal);
        Session.Edit(description, project =>
        {
            foreach (var element in project.Screens.Single(s => s.Id == Session.ActiveScreenId).Elements.Where(e => ids.Contains(e.Id))) edit(element);
        });
    }
}
