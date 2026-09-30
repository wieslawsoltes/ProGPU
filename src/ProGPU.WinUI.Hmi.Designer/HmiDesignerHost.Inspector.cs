using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;
using ProGPU.WinUI.Designer;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static double Number(string value) => double.Parse(value, NumberStyles.Float, Invariant);
    private static float Coordinate(string value) => float.Parse(value, NumberStyles.Float, Invariant);
    private static int Integer(string value) => int.Parse(value, NumberStyles.Integer, Invariant);
    private static bool Boolean(string value) => bool.Parse(value);
    private static T Choice<T>(string value) where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, true, out var result) || !Enum.IsDefined(result)) throw new FormatException("Choose one of: " + string.Join(", ", Enum.GetNames<T>()));
        return result;
    }
    private static string Format(double value) => value.ToString("0.###", Invariant);

    private void UpdateInspector()
    {
        if (_rebuilding || _disposed) return;
        UpdateStudioState();
        _alarmConsole?.AttachRuntime(_runtime);
        _outline.IsHitTestVisible = !IsPreviewing;
        _palette.IsHitTestVisible = !IsPreviewing;
        RefreshStateTable();
        _properties.ClearItems();
        var selection = _selection.Selection.OfType<HmiControl>().ToArray();
        _selectionLabel.Text = selection.Length == 0 ? "Screen properties" : $"{selection.Length} selected · {selection[0].Label}";
        _layout.SelectedElement = selection.Length == 1 && !selection[0].IsDesignLocked && !IsPreviewing ? selection[0] : null;
        _canvas.AdornerSurface.IsHitTestVisible = selection.All(c => !c.IsDesignLocked) && !IsPreviewing;
        _multiAdorner.Invalidate();
        if (selection.Length == 0)
        {
            if (BuildLinkInspector()) return;
            var screen = Session.ActiveScreen;
            ProjectProperty("Project name", Session.Document.Name, (p, v) => p.Name = v);
            ProjectProperty("Screen name", screen.Name, (p, v) => p.Screens.Single(s => s.Id == screen.Id).Name = v);
            ProjectProperty("Screen width", Format(screen.Width), (p, v) => p.Screens.Single(s => s.Id == screen.Id).Width = Coordinate(v));
            ProjectProperty("Screen height", Format(screen.Height), (p, v) => p.Screens.Single(s => s.Id == screen.Id).Height = Coordinate(v));
            ProjectProperty("Start screen ID", Session.Document.StartScreenId, (p, v) => p.StartScreenId = v);
            ReadOnlyProperty("Screen ID", screen.Id);
            ReadOnlyProperty("Elements", screen.Elements.Count.ToString(Invariant));
            return;
        }
        var model = selection[0].CaptureDefinition();
        ReadOnlyProperty("ID", model.Id);
        ReadOnlyProperty("Symbol", model.Symbol.ToString());
        ElementProperty("Locked", model.IsLocked.ToString(), (e, v) => e.IsLocked = Boolean(v), allowLocked: true);
        if (selection.Any(c => c.IsDesignLocked))
        {
            ReadOnlyProperty("Editing", "Unlock selected components to edit their properties.");
            return;
        }
        ElementProperty("Name", model.Name, (e, v) => e.Name = v);
        ElementProperty("Label", model.Label, (e, v) => e.Label = v);
        ElementProperty("Presentation", model.Appearance.Presentation.ToString(), (e, v) => e.Appearance.Presentation = Choice<HmiPresentation>(v));
        ElementProperty("Show tag name", model.Appearance.ShowTagName.ToString(), (e, v) => e.Appearance.ShowTagName = Boolean(v));
        ElementProperty("Show range", model.Appearance.ShowEngineeringRange.ToString(), (e, v) => e.Appearance.ShowEngineeringRange = Boolean(v));
        ElementProperty("Show ports", model.Appearance.ShowConnectionPorts.ToString(), (e, v) => e.Appearance.ShowConnectionPorts = Boolean(v));
        ElementProperty("Show value", model.Appearance.ShowValue.ToString(), (e, v) => e.Appearance.ShowValue = Boolean(v));
        ElementProperty("Symbol quarter turns", model.Appearance.QuarterTurns.ToString(Invariant), (e, v) => e.Appearance.QuarterTurns = Integer(v));
        ElementProperty("Mirror symbol X", model.Appearance.MirrorHorizontal.ToString(), (e, v) => e.Appearance.MirrorHorizontal = Boolean(v));
        ElementProperty("Mirror symbol Y", model.Appearance.MirrorVertical.ToString(), (e, v) => e.Appearance.MirrorVertical = Boolean(v));
        ElementProperty("Animate flow", model.Appearance.AnimateFlow.ToString(), (e, v) => e.Appearance.AnimateFlow = Boolean(v));
        ElementProperty("Unit", model.Unit, (e, v) => e.Unit = v);
        ElementProperty("Value tag", model.Tag, (e, v) => e.Tag = v.Trim());
        ElementProperty("Visibility tag", model.VisibilityTag, (e, v) => e.VisibilityTag = v.Trim());
        ElementProperty("Enabled tag", model.EnabledTag, (e, v) => e.EnabledTag = v.Trim());
        ElementProperty("Minimum", Format(model.Minimum), (e, v) => e.Minimum = Number(v));
        ElementProperty("Maximum", Format(model.Maximum), (e, v) => e.Maximum = Number(v));
        ElementProperty("Decimals", model.Decimals.ToString(Invariant), (e, v) => e.Decimals = Integer(v));
        if (model.Symbol == HmiSymbol.Trend)
        {
            ElementProperty("Trend window (s)", Format(model.Trend.WindowSeconds), (e, v) => e.Trend.WindowSeconds = Number(v));
            ElementProperty("Maximum gap (s)", Format(model.Trend.MaximumGapSeconds), (e, v) => e.Trend.MaximumGapSeconds = Number(v));
        }
        ElementProperty("Hidden", model.IsHidden.ToString(), (e, v) => e.IsHidden = Boolean(v));
        ElementProperty("Group", model.Group, (e, v) => e.Group = v);
        ElementProperty("X", Format(model.X), (e, v) => e.X = Coordinate(v));
        ElementProperty("Y", Format(model.Y), (e, v) => e.Y = Coordinate(v));
        ElementProperty("Width", Format(model.Width), (e, v) => e.Width = Coordinate(v));
        ElementProperty("Height", Format(model.Height), (e, v) => e.Height = Coordinate(v));
        ElementProperty("Action", model.Action.Kind.ToString(), (e, v) =>
        {
            var kind = Choice<HmiActionKind>(v);
            e.Action.Kind = kind;
            // Supply a valid editable starting target instead of trapping the operator
            // between two individually invalid property edits.
            switch (kind)
            {
                case HmiActionKind.ToggleTag:
                    e.Action.Target = Session.Document.Tags.FirstOrDefault(t => t.Writable && t.Type == HmiTagType.Boolean)?.Name ?? ""; break;
                case HmiActionKind.WriteTag:
                    var target = Session.Document.Tags.FirstOrDefault(t => t.Writable);
                    e.Action.Target = target?.Name ?? "";
                    if (target != null) e.Action.Value = target.InitialValue;
                    break;
                case HmiActionKind.Navigate: e.Action.Target = Session.Document.Screens[0].Id; break;
                case HmiActionKind.ApplyRecipe: e.Action.Target = Session.Document.Recipes.FirstOrDefault()?.Name ?? ""; break;
                default: e.Action.Target = ""; break;
            }
        });
        ElementProperty("Action target", model.Action.Target, (e, v) =>
        {
            e.Action.Target = v;
            if (e.Action.Kind == HmiActionKind.WriteTag && Session.Document.Tags.SingleOrDefault(t => t.Name == v) is { } tag)
                e.Action.Value = tag.InitialValue;
        });
        ElementProperty("Action value", model.Action.Value.ToString(), (e, v) =>
        {
            var type = Session.Document.Tags.SingleOrDefault(t => t.Name == e.Action.Target)?.Type ?? e.Action.Value.Type;
            e.Action.Value = HmiValue.Parse(v, type);
        });
        ReadOnlyProperty("Action choices", string.Join(", ", Enum.GetNames<HmiActionKind>()));
        ReadOnlyProperty("Binding names", string.Join(", ", Session.Document.Tags.Take(12).Select(t => t.Name)));
    }
    private void ReadOnlyProperty(string name, string value) => _properties.AddItem(new HmiEditorRow(new() { ["Name"] = name, ["Value"] = value }));
    private void ProjectProperty(string name, string value, Action<HmiProject, string> write)
    {
        _properties.AddItem(new HmiEditorRow(new() { ["Name"] = name, ["Value"] = value }, (property, text) =>
        {
            if (property != "Value") throw new InvalidOperationException("Property names are read-only.");
            DesignCommand(() => Session.Edit("Edit " + name, p => write(p, text)));
        }, error => Status(error, true)));
    }
    private void ElementProperty(string name, string value, Action<HmiElement, string> write, bool allowLocked = false)
    {
        var ids = _selection.Selection.OfType<HmiControl>().Select(c => c.CaptureDefinition().Id).ToHashSet(StringComparer.Ordinal);
        _properties.AddItem(new HmiEditorRow(new() { ["Name"] = name, ["Value"] = value }, (property, text) =>
        {
            if (property != "Value") throw new InvalidOperationException("Property names are read-only.");
            DesignCommand(() => Session.Edit("Edit " + name, p =>
            {
                foreach (var element in p.Screens.Single(s => s.Id == Session.ActiveScreenId).Elements.Where(e => ids.Contains(e.Id)))
                {
                    if (element.IsLocked && !allowLocked) throw new InvalidOperationException("Unlock this component first.");
                    write(element, text);
                }
            }));
        }, error => Status(error, true)));
    }
}
