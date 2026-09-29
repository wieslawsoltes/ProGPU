using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Hmi;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private DataGrid _tags = null!;
    private DataGrid _alarms = null!;
    private DataGrid _recipes = null!;
    private TextBox _monitor = null!;

    private Grid BuildDataArea()
    {
        var area = new Grid();
        var tabs = new Pivot { Font = _font };
        area.AddChild(tabs);
        _tags = Table(("Tag", "160", "Name"), ("Type", "80", "Type"), ("Initial", "95", "Initial"), ("Live", "95", "Live"), ("Quality", "75", "Quality"),
            ("Unit", "60", "Unit"), ("Writable", "70", "Writable"), ("Min", "65", "Minimum"), ("Max", "65", "Maximum"), ("Stale ms", "75", "Stale"), ("Simulation", "90", "Simulation"), ("Period s", "75", "Period"));
        var tagTools = Toolbar(); var tagKey = Input("Tag name to delete", 220);
        tagTools.AddChild(Command("Add tag", AddTag)); tagTools.AddChild(tagKey);
        tagTools.AddChild(Command("Delete", () => DesignCommand(() => Session.Edit("Delete tag", p =>
        {
            if (p.Tags.RemoveAll(t => t.Name == tagKey.Text) == 0) throw new InvalidOperationException("Enter an existing tag name.");
        }))));
        tagTools.AddChild(Command("Refresh live values", RefreshTables));
        tagTools.AddChild(Text("Edit cells directly. Removing referenced tags is rejected.", 10));
        tabs.Items.Add(new PivotItem("Tags", TablePane(tagTools, _tags)));

        _alarms = Table(("ID", "130", "Id"), ("Tag", "145", "Tag"), ("Message", "*", "Message"), ("Condition", "95", "Condition"),
            ("Limit", "65", "Limit"), ("Deadband", "75", "Deadband"), ("Delay ms", "75", "Delay"), ("Severity", "90", "Severity"));
        var alarmTools = Toolbar(); var alarmKey = Input("Alarm ID to delete", 220);
        alarmTools.AddChild(Command("Add alarm", AddAlarm)); alarmTools.AddChild(alarmKey);
        alarmTools.AddChild(Command("Delete", () => DesignCommand(() => Session.Edit("Delete alarm", p =>
        {
            if (p.Alarms.RemoveAll(a => a.Id == alarmKey.Text) == 0) throw new InvalidOperationException("Enter an existing alarm ID.");
        }))));
        alarmTools.AddChild(Text("Conditions: High, Low, IsTrue, IsFalse. Severity: Information, Warning, Critical.", 10));
        tabs.Items.Add(new PivotItem("Alarms", TablePane(alarmTools, _alarms)));

        _recipes = Table(("Recipe", "230", "Name"), ("Tag", "230", "Tag"), ("Value", "*", "Value"));
        var recipeTools = Toolbar(); var recipeKey = Input("Recipe name", 190); var recipeTag = Input("Writable tag", 170);
        recipeTools.AddChild(Command("Add recipe", AddRecipe)); recipeTools.AddChild(recipeKey); recipeTools.AddChild(recipeTag);
        recipeTools.AddChild(Command("Add value", () => DesignCommand(() => Session.Edit("Add recipe value", p =>
        {
            var recipe = p.Recipes.SingleOrDefault(r => r.Name == recipeKey.Text) ?? throw new InvalidOperationException("Enter an existing recipe name.");
            var tag = p.Tags.SingleOrDefault(t => t.Name == recipeTag.Text && t.Writable) ?? throw new InvalidOperationException("Choose a writable tag.");
            recipe.Values[tag.Name] = tag.InitialValue;
        }))));
        recipeTools.AddChild(Command("Remove value", () => DesignCommand(() => Session.Edit("Remove recipe value", p =>
        {
            var recipe = p.Recipes.SingleOrDefault(r => r.Name == recipeKey.Text) ?? throw new InvalidOperationException("Unknown recipe.");
            if (!recipe.Values.Remove(recipeTag.Text)) throw new InvalidOperationException("Recipe does not contain that tag.");
        }))));
        recipeTools.AddChild(Command("Delete recipe", () => DesignCommand(() => Session.Edit("Delete recipe", p =>
        {
            if (p.Recipes.RemoveAll(r => r.Name == recipeKey.Text) == 0) throw new InvalidOperationException("Unknown recipe.");
        }))));
        tabs.Items.Add(new PivotItem("Recipes", TablePane(recipeTools, _recipes)));

        var jsonTools = Toolbar();
        jsonTools.AddChild(Command("Export current project", () => { _json.Text = Session.ExportJson(); Status("Project JSON exported to the editor. This does not mark the project saved."); }));
        jsonTools.AddChild(Command("Validate", () => { HmiProjectSerializer.Deserialize(_json.Text); Status("Project JSON is valid."); }));
        jsonTools.AddChild(Command("Apply JSON", () => DesignCommand(() => Session.ImportJson(_json.Text))));
        jsonTools.AddChild(Text("Versioned project source · all bindings, actions and screen metadata · invalid imports leave the project unchanged", 10));
        tabs.Items.Add(new PivotItem("Project JSON", TablePane(jsonTools, _json)));
        _json.Text = Session.ExportJson();

        _monitor = new TextBox { Font = _font, FontSize = 11, AcceptsReturn = true, IsReadOnly = true };
        var monitorTools = Toolbar(); var historyTag = Input("History tag (for CSV export)", 240);
        monitorTools.AddChild(Command("Refresh diagnostics", RefreshMonitor));
        monitorTools.AddChild(Command("Acknowledge all", () => { if (_runtime == null) throw new InvalidOperationException("Run simulation first."); _runtime.Acknowledge(); RefreshMonitor(); }));
        monitorTools.AddChild(historyTag);
        monitorTools.AddChild(Command("Export history CSV", () =>
        {
            if (_runtime == null) throw new InvalidOperationException("Run simulation first.");
            _monitor.Text = _runtime.ExportHistoryCsv(historyTag.Text);
            Status("CSV exported into the diagnostics pane; select/copy the text to save it.");
        }));
        tabs.Items.Add(new PivotItem("Runtime / audit", TablePane(monitorTools, _monitor)));
        tabs.Items.Add(new PivotItem("Help", new ScrollViewer { Content = Text(
            "GETTING STARTED\nChoose Components and drag a symbol to the canvas, or press its + button. Select components to edit HMI properties and tag bindings.\n\n" +
            "LAYOUT\nDrag to move; use the shared resize handles. Ctrl-click or Select all for multiple selection. Align, distribute, group, reorder and lock from the toolbar.\nCtrl+C/X/V/D copy, cut, paste and duplicate. Delete removes unlocked selections. Arrow keys nudge; Shift moves 10 units. Ctrl+Z/Y undo/redo.\nMiddle-drag pans. Ctrl+wheel zooms at the pointer. Fit frames the active screen.\n\n" +
            "DATA\nTags, alarms and recipe values are editable in the tables. Bindings are case-sensitive. Numbers use an invariant decimal point.\nVisibility and enable bindings require Boolean tags. Commands are None, ToggleTag, WriteTag, Navigate, AcknowledgeAlarms and ApplyRecipe.\nDeleting referenced tags/screens/recipes is rejected. Rename tags in the table to update all references atomically.\n\n" +
            "PREVIEW\nRun starts a separate local simulation. Pause/Resume and Step control deterministic time. Operating controls never edits the design document.\nAcknowledge does not clear active conditions. Invalid/stale quality blocks writes and cannot silently clear an alarm.\nStop before editing. No PLC, OPC UA or MQTT transport is connected. These samples are not a safety controller.\n\n" +
            "FILES\nEnter a desktop file path and use Open/Save. Unsaved destructive changes require a second explicit click.\nProject JSON supports round-trip editing and validation. Save uses a same-directory temporary file followed by replacement.\nSee docs/hmi-designer.md for embedding, package structure, tests and integration boundaries.", 12) }));
        return area;
    }
    private static Grid TablePane(FrameworkElement toolbar, FrameworkElement content)
    {
        var panel = new Grid(); panel.RowDefinitions.Add(GridLength.Auto); panel.RowDefinitions.Add(GridLength.Star(1));
        panel.AddChild(toolbar); panel.AddChild(content); SetRow(content, 1);
        return panel;
    }
    private void RefreshTables()
    {
        if (_tags == null) return;
        _tags.ClearItems(); _alarms.ClearItems(); _recipes.ClearItems();
        foreach (var tag in Session.Document.Tags)
        {
            string name = tag.Name;
            bool live = _runtime != null && _runtime.TryRead(name, out _);
            _tags.AddItem(new HmiEditorRow(new()
            {
                ["Name"] = name, ["Type"] = tag.Type.ToString(), ["Initial"] = tag.InitialValue.ToString(),
                ["Live"] = live ? _runtime!.Read(name).Value.ToString() : "—", ["Quality"] = live ? _runtime!.Read(name).Quality.ToString() : "Design",
                ["Unit"] = tag.Unit, ["Writable"] = tag.Writable.ToString(), ["Minimum"] = Format(tag.Minimum), ["Maximum"] = Format(tag.Maximum),
                ["Stale"] = tag.StaleAfterMilliseconds.ToString(Invariant), ["Simulation"] = tag.Simulation.ToString(), ["Period"] = Format(tag.PeriodSeconds)
            }, (property, value) => DesignCommand(() =>
            {
                if (property == "Name") { Session.RenameTag(name, value.Trim()); return; }
                Session.Edit("Edit tag " + name, p =>
                {
                    var target = p.Tags.Single(t => t.Name == name);
                    switch (property)
                    {
                        case "Type":
                            target.Type = Choice<HmiTagType>(value); target.Simulation = HmiSimulationKind.Constant;
                            target.InitialValue = target.Type switch { HmiTagType.Number => HmiValue.From(target.Minimum), HmiTagType.Boolean => HmiValue.From(false), _ => HmiValue.From("") }; break;
                        case "Initial": target.InitialValue = HmiValue.Parse(value, target.Type); break;
                        case "Unit": target.Unit = value; break;
                        case "Writable": target.Writable = Boolean(value); break;
                        case "Minimum": target.Minimum = Number(value); break;
                        case "Maximum": target.Maximum = Number(value); break;
                        case "Stale": target.StaleAfterMilliseconds = Integer(value); break;
                        case "Simulation": target.Simulation = Choice<HmiSimulationKind>(value); break;
                        case "Period": target.PeriodSeconds = Number(value); break;
                        default: throw new InvalidOperationException("This telemetry column is read-only.");
                    }
                });
            }), error => Status(error, true)));
        }
        foreach (var alarm in Session.Document.Alarms)
        {
            string id = alarm.Id;
            _alarms.AddItem(new HmiEditorRow(new()
            {
                ["Id"] = id, ["Tag"] = alarm.Tag, ["Message"] = alarm.Message, ["Condition"] = alarm.Condition.ToString(),
                ["Limit"] = Format(alarm.Limit), ["Deadband"] = Format(alarm.Deadband), ["Delay"] = alarm.DelayMilliseconds.ToString(Invariant), ["Severity"] = alarm.Severity.ToString()
            }, (property, value) => DesignCommand(() => Session.Edit("Edit alarm", p =>
            {
                var target = p.Alarms.Single(a => a.Id == id);
                switch (property)
                {
                    case "Id": target.Id = value; break;
                    case "Tag":
                        var tag = p.Tags.SingleOrDefault(t => t.Name == value) ?? throw new InvalidOperationException("Unknown tag.");
                        if (tag.Type == HmiTagType.Text) throw new InvalidOperationException("Alarms require numeric or Boolean tags.");
                        target.Tag = value;
                        target.Condition = tag.Type == HmiTagType.Boolean ? HmiAlarmCondition.IsTrue : HmiAlarmCondition.High;
                        target.Limit = tag.Maximum * 0.8 + tag.Minimum * 0.2; break;
                    case "Message": target.Message = value; break;
                    case "Condition": target.Condition = Choice<HmiAlarmCondition>(value); break;
                    case "Limit": target.Limit = Number(value); break;
                    case "Deadband": target.Deadband = Number(value); break;
                    case "Delay": target.DelayMilliseconds = Integer(value); break;
                    case "Severity": target.Severity = Choice<HmiAlarmSeverity>(value); break;
                    default: throw new InvalidOperationException("Read-only column.");
                }
            })), error => Status(error, true)));
        }
        foreach (var recipe in Session.Document.Recipes)
        {
            if (recipe.Values.Count == 0) _recipes.AddItem(new HmiEditorRow(new() { ["Name"] = recipe.Name, ["Tag"] = "(empty)", ["Value"] = "Add a writable tag using the toolbar." }));
            foreach (var pair in recipe.Values)
            {
                string recipeName = recipe.Name, tagName = pair.Key;
                _recipes.AddItem(new HmiEditorRow(new() { ["Name"] = recipeName, ["Tag"] = tagName, ["Value"] = pair.Value.ToString() }, (property, value) => DesignCommand(() => Session.Edit("Edit recipe", p =>
                {
                    var target = p.Recipes.Single(r => r.Name == recipeName);
                    if (property == "Value") target.Values[tagName] = HmiValue.Parse(value, p.Tags.Single(t => t.Name == tagName).Type);
                    else if (property == "Name")
                    {
                        target.Name = value;
                        foreach (var element in p.Screens.SelectMany(s => s.Elements))
                            if (element.Action.Kind == HmiActionKind.ApplyRecipe && element.Action.Target == recipeName) element.Action.Target = value;
                    }
                    else throw new InvalidOperationException("Use Add value / Remove value to change recipe tag destinations.");
                })), error => Status(error, true)));
            }
        }
    }
    private void AddTag() => DesignCommand(() => Session.Edit("Add tag", p =>
    {
        int suffix = 1;
        while (p.Tags.Any(t => t.Name == "Tag" + suffix)) suffix++;
        p.Tags.Add(new HmiTagDefinition { Name = "Tag" + suffix });
    }));
    private void AddAlarm() => DesignCommand(() => Session.Edit("Add alarm", p =>
    {
        var tag = p.Tags.FirstOrDefault(t => t.Type != HmiTagType.Text) ?? throw new InvalidOperationException("Create a numeric or Boolean tag first.");
        int suffix = 1; while (p.Alarms.Any(a => a.Id == "alarm-" + suffix)) suffix++;
        p.Alarms.Add(new HmiAlarmDefinition { Id = "alarm-" + suffix, Tag = tag.Name, Message = tag.Name + " alarm", Condition = tag.Type == HmiTagType.Boolean ? HmiAlarmCondition.IsTrue : HmiAlarmCondition.High, Limit = tag.Minimum + (tag.Maximum - tag.Minimum) * 0.8 });
    }));
    private void AddRecipe() => DesignCommand(() => Session.Edit("Add recipe", p =>
    {
        int suffix = 1; while (p.Recipes.Any(r => r.Name == "Recipe " + suffix)) suffix++;
        var recipe = new HmiRecipe { Name = "Recipe " + suffix };
        if (p.Tags.FirstOrDefault(t => t.Writable) is { } tag) recipe.Values.Add(tag.Name, tag.InitialValue);
        p.Recipes.Add(recipe);
    }));
    private void RefreshMonitor()
    {
        if (_runtime == null) { _monitor.Text = "Simulation is stopped. No process connection is active."; return; }
        _monitor.Text = $"LOCAL SIMULATION\nLogical time: {_runtime.Now:O}\nTags: {_runtime.TagNames.Count}\nCommands: {(_runtime.AllowLocalWrites ? "local writes enabled" : "read-only")}\n\nALARMS\n" +
            string.Join("\n", _runtime.Alarms.Select(a => $"{a.Definition.Id}: {(a.IsActive ? "ACTIVE" : "normal")} / {(a.IsAcknowledged ? "ACK" : "UNACK")} / {(a.IsQualityUnknown ? "UNKNOWN QUALITY" : "good quality")}")) +
            "\n\nAUDIT (most recent 100 entries)\n" + string.Join("\n", _runtime.Audit.TakeLast(100).Select(e => $"{e.Timestamp:O} · {e.Operation} · {e.Target} · {e.Detail}"));
    }
}
