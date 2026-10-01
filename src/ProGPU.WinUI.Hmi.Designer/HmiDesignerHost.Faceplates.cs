using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private StackPanel _faceplateList = null!;
    private TextBox _faceplateName = null!;
    private TextBox _faceplatePrefix = null!;
    private DataGrid _stateRules = null!;
    private TextBox _stateIndex = null!;

    private FrameworkElement BuildFaceplatesPane()
    {
        _faceplateName = Input("Template name", 230);
        _faceplatePrefix = Input("Instance tag prefix, e.g. P101", 250);
        var tools = Toolbar(); tools.AddChild(_faceplateName); tools.AddChild(_faceplatePrefix);
        tools.AddChild(Command("Capture selection", () => DesignCommand(() =>
        {
            var selected = _selection.Selection.OfType<HmiControl>().Select(c => c.CaptureDefinition().Id).ToArray();
            Session.Edit("Create faceplate master", p => p.Faceplates.Add(HmiFaceplates.Capture(p, p.Screens.Single(s => s.Id == Session.ActiveScreenId), selected, _faceplateName.Text)));
        })));
        tools.AddChild(Command("Add pump-station master", () => DesignCommand(() => Session.Edit("Add equipment master", p =>
        {
            var template = HmiBuiltInFaceplates.PumpStation();
            if (p.Faceplates.Any(t => t.Id == template.Id)) throw new InvalidOperationException("The built-in pump station is already in this project.");
            p.Faceplates.Add(template);
        }))));
        tools.AddChild(Command("Detach selected instance", () => DesignCommand(() =>
        {
            var instances = _selection.Selection.OfType<HmiControl>().Select(c => c.CaptureDefinition().FaceplateInstanceId).ToArray();
            Session.Edit("Detach faceplate", p => HmiFaceplates.Detach(p.Screens.Single(s => s.Id == Session.ActiveScreenId), instances));
        })));
        _faceplateList = new StackPanel();
        var root = new StackPanel(); root.AddChild(tools);
        root.AddChild(Text("Typed slot bindings create/reuse prefix-qualified tags. Insert adds a grouped instance. Edit masters in Project JSON; Update instances explicitly replaces local overrides. Copies detach from the master.", 11));
        root.AddChild(_faceplateList);
        return new ScrollViewer { Content = root };
    }
    private void RefreshFaceplateList()
    {
        if (_faceplateList == null) return;
        _faceplateList.Children.Clear();
        foreach (var template in Session.Document.Faceplates)
        {
            string id = template.Id;
            var row = Toolbar();
            row.AddChild(Text($"{template.Name} · rev {template.Revision} · {template.Elements.Count} components · {template.Slots.Count} slots", 12));
            row.AddChild(Command("Insert", () => DesignCommand(() => Session.Edit("Insert equipment faceplate", p =>
            {
                _restoreSelection = HmiFaceplates.Instantiate(p, p.Screens.Single(s => s.Id == Session.ActiveScreenId), id, _faceplatePrefix.Text, 50, 50).ToArray();
            }))));
            row.AddChild(Command("Update instances", () => DesignCommand(() => Session.Edit("Synchronize equipment instances", p => HmiFaceplates.Synchronize(p, id)))));
            _faceplateList.AddChild(row);
            _faceplateList.AddChild(Text("Slots: " + string.Join(", ", template.Slots.Select(s => $"${s.Name} ({s.Type}{(s.Writable ? ", writable" : "")})")), 10));
        }
    }
    private FrameworkElement BuildStateRulesPane()
    {
        _stateRules = Table(("Index", "55", "Index"), ("Tag", "200", "Tag"), ("Condition", "105", "Condition"), ("Threshold", "90", "Threshold"), ("Tone", "115", "Tone"), ("Text", "*", "Text"), ("Priority", "70", "Priority"));
        _stateIndex = Input("Rule index to delete", 150);
        var tools = Toolbar();
        tools.AddChild(Command("Add state to selected", () => DesignCommand(() =>
        {
            var selected = _selection.Selection.OfType<HmiControl>().SingleOrDefault() ?? throw new InvalidOperationException("Select one component.");
            if (selected.IsDesignLocked) throw new InvalidOperationException("Unlock the component first.");
            string id = selected.CaptureDefinition().Id;
            Session.Edit("Add equipment state", p =>
            {
                var tag = p.Tags.FirstOrDefault(t => t.Type != HmiTagType.Text) ?? throw new InvalidOperationException("Add a numeric or Boolean tag first.");
                p.Screens.Single(s => s.Id == Session.ActiveScreenId).Elements.Single(e => e.Id == id).States.Add(new HmiStateRule
                { Tag = tag.Name, Condition = tag.Type == HmiTagType.Boolean ? HmiStateCondition.IsTrue : HmiStateCondition.Above, Threshold = tag.Maximum * 0.8 + tag.Minimum * 0.2 });
            });
        })));
        tools.AddChild(_stateIndex);
        tools.AddChild(Command("Delete state", () => DesignCommand(() =>
        {
            var selected = _selection.Selection.OfType<HmiControl>().SingleOrDefault() ?? throw new InvalidOperationException("Select one component.");
            if (selected.IsDesignLocked) throw new InvalidOperationException("Unlock the component first.");
            string id = selected.CaptureDefinition().Id;
            Session.Edit("Remove equipment state", p => p.Screens.Single(s => s.Id == Session.ActiveScreenId).Elements.Single(e => e.Id == id).States.RemoveAt(Integer(_stateIndex.Text)));
        })));
        tools.AddChild(Text("Highest priority wins. Missing or bad/stale state tags force UNKNOWN QUALITY. Conditions: IsTrue, IsFalse, Above, Below, Equal.", 10));
        return TablePane(tools, _stateRules);
    }
    private void RefreshStateTable()
    {
        if (_stateRules == null) return;
        _stateRules.ClearItems();
        var selected = _selection.Selection.OfType<HmiControl>().ToArray();
        if (selected.Length != 1) return;
        var element = selected[0].CaptureDefinition();
        for (int i = 0; i < element.States.Count; i++)
        {
            int index = i; var rule = element.States[index];
            _stateRules.AddItem(new HmiEditorRow(new()
            {
                ["Index"] = index.ToString(Invariant), ["Tag"] = rule.Tag, ["Condition"] = rule.Condition.ToString(),
                ["Threshold"] = Format(rule.Threshold), ["Tone"] = rule.Tone.ToString(), ["Text"] = rule.Text, ["Priority"] = rule.Priority.ToString(Invariant)
            }, (property, value) => DesignCommand(() => Session.Edit("Edit equipment state", p =>
            {
                var target = p.Screens.Single(s => s.Id == Session.ActiveScreenId).Elements.Single(e => e.Id == element.Id);
                if (target.IsLocked) throw new InvalidOperationException("Unlock this component first.");
                var state = target.States[index];
                switch (property)
                {
                    case "Tag":
                        var tag = p.Tags.SingleOrDefault(t => t.Name == value && t.Type != HmiTagType.Text) ?? throw new InvalidOperationException("Choose a numeric or Boolean tag.");
                        state.Tag = value; state.Condition = tag.Type == HmiTagType.Boolean ? HmiStateCondition.IsTrue : HmiStateCondition.Above; break;
                    case "Condition": state.Condition = Choice<HmiStateCondition>(value); break;
                    case "Threshold": state.Threshold = Number(value); break;
                    case "Tone": state.Tone = Choice<HmiVisualTone>(value); break;
                    case "Text": state.Text = value; break;
                    case "Priority": state.Priority = Integer(value); break;
                    default: throw new InvalidOperationException("Rule indices are read-only.");
                }
            })), error => Status(error, true)));
        }
    }
}
