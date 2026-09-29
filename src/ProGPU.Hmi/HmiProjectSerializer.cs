using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace ProGPU.Hmi;

public static class HmiProjectSerializer
{
    public const int MaximumDocumentBytes = 8 * 1024 * 1024;

    public static string Serialize(HmiProject project)
    {
        Validate(project);
        string json = JsonSerializer.Serialize(project, HmiJsonContext.ProjectContext.HmiProject);
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw new InvalidDataException("The HMI project exceeds the 8 MiB document budget.");
        return json;
    }

    public static HmiProject Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumDocumentBytes || Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw new InvalidDataException("The HMI project exceeds the 8 MiB document budget.");
        var project = JsonSerializer.Deserialize(json, HmiJsonContext.ProjectContext.HmiProject)
            ?? throw new InvalidDataException("An HMI project is required.");
        Validate(project);
        return project;
    }

    public static HmiProject Clone(HmiProject project) => Deserialize(Serialize(project));

    public static void Validate(HmiProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Require(project.SchemaVersion == 1, "Unsupported HMI schema version; expected 1.");
        Text(project.Name, "Project name");
        Require(project.Screens is { Count: > 0 and <= 128 }, "A project requires 1–128 screens.");
        Require(project.Tags is { Count: <= 4096 } && project.Alarms is { Count: <= 4096 } && project.Recipes is { Count: <= 256 }, "Project collections are null or exceed their budgets.");
        var tags = new Dictionary<string, HmiTagDefinition>(StringComparer.Ordinal);
        foreach (var tag in project.Tags)
        {
            Require(tag != null, "Null tag.");
            Text(tag.Name, "Tag name");
            Require(tags.TryAdd(tag.Name, tag), $"Duplicate tag: {tag.Name}.");
            Require(Enum.IsDefined(tag.Type) && Enum.IsDefined(tag.Simulation), "Unknown tag or simulation type.");
            Require(double.IsFinite(tag.Minimum) && double.IsFinite(tag.Maximum) && double.IsFinite(tag.Maximum - tag.Minimum) && tag.Minimum < tag.Maximum, $"Invalid range for {tag.Name}.");
            Require(tag.StaleAfterMilliseconds is >= 100 and <= 86_400_000, "Tag stale timeout must be 100–86400000 ms.");
            Require(double.IsFinite(tag.PeriodSeconds) && tag.PeriodSeconds >= 0.1 && tag.PeriodSeconds <= 86_400, "Simulation period must be 0.1–86400 seconds.");
            Require(tag.Simulation != HmiSimulationKind.Toggle || tag.Type == HmiTagType.Boolean, "Toggle simulation requires a Boolean tag.");
            Require(tag.Simulation is not (HmiSimulationKind.Sine or HmiSimulationKind.Ramp) || tag.Type == HmiTagType.Number, "Sine and ramp simulation require numeric tags.");
            ValidateValue(tag, tag.InitialValue, enforceRange: true);
        }
        var screenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in project.Screens)
        {
            Require(screen != null, "Null screen.");
            Text(screen.Id, "Screen ID"); Text(screen.Name, "Screen name");
            Require(screenIds.Add(screen.Id), $"Duplicate screen ID: {screen.Id}.");
            Require(float.IsFinite(screen.Width) && float.IsFinite(screen.Height) && screen.Width is >= 64 and <= 16384 && screen.Height is >= 64 and <= 16384, "Screen dimensions must be 64–16384.");
            Require(screen.Elements is { Count: <= 5000 }, "A screen supports at most 5000 elements.");
        }
        Require(screenIds.Contains(project.StartScreenId ?? ""), "The start screen does not exist.");
        var recipeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipe in project.Recipes)
        {
            Require(recipe != null, "Null recipe."); Text(recipe.Name, "Recipe name");
            Require(recipeNames.Add(recipe.Name), $"Duplicate recipe: {recipe.Name}.");
            Require(recipe.Values is { Count: <= 4096 }, "Invalid recipe values.");
            foreach (var pair in recipe.Values)
            {
                Require(tags.TryGetValue(pair.Key, out var tag), $"Unknown recipe tag: {pair.Key}.");
                Require(tag!.Writable, $"Recipe tag is read-only: {pair.Key}.");
                ValidateValue(tag, pair.Value, true);
            }
        }
        foreach (var screen in project.Screens)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in screen.Elements)
            {
                Require(element != null, "Null element."); Text(element.Id, "Element ID");
                Require(ids.Add(element.Id), $"Duplicate element ID: {element.Id}.");
                Require(Enum.IsDefined(element.Symbol), "Unknown HMI symbol.");
                Require(float.IsFinite(element.X) && float.IsFinite(element.Y) && Math.Abs(element.X) <= 32768 && Math.Abs(element.Y) <= 32768, "Invalid element position.");
                Require(float.IsFinite(element.Width) && float.IsFinite(element.Height) && element.Width is >= 8 and <= 16384 && element.Height is >= 8 and <= 16384, "Element dimensions must be 8–16384.");
                Require(double.IsFinite(element.Minimum) && double.IsFinite(element.Maximum) && double.IsFinite(element.Maximum - element.Minimum) && element.Minimum < element.Maximum && element.Decimals is >= 0 and <= 6, "Invalid component range or precision.");
                Text(element.Name, "Element name"); Text(element.Label, "Label", true); Text(element.Unit, "Unit", true); Text(element.Group, "Group", true);
                Binding(element.Tag, tags, false); Binding(element.VisibilityTag, tags, true); Binding(element.EnabledTag, tags, true);
                Require(element.Action != null && Enum.IsDefined(element.Action.Kind), "Invalid action.");
                switch (element.Action.Kind)
                {
                    case HmiActionKind.ToggleTag:
                    case HmiActionKind.WriteTag:
                        Require(tags.TryGetValue(element.Action.Target ?? "", out var target) && target.Writable, "Actions require an existing writable tag.");
                        if (element.Action.Kind == HmiActionKind.ToggleTag) Require(target!.Type == HmiTagType.Boolean, "Toggle actions require Boolean tags.");
                        else ValidateValue(target!, element.Action.Value, true);
                        break;
                    case HmiActionKind.Navigate: Require(screenIds.Contains(element.Action.Target ?? ""), "Navigation target does not exist."); break;
                    case HmiActionKind.ApplyRecipe: Require(recipeNames.Contains(element.Action.Target ?? ""), "Recipe target does not exist."); break;
                }
            }
        }
        HmiProjectExtensions.Validate(project);
        var alarmIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alarm in project.Alarms)
        {
            Require(alarm != null, "Null alarm."); Text(alarm.Id, "Alarm ID"); Text(alarm.Message, "Alarm message");
            Require(alarmIds.Add(alarm.Id), "Duplicate alarm ID.");
            Require(tags.TryGetValue(alarm.Tag ?? "", out var tag), "Alarm references an unknown tag.");
            Require(Enum.IsDefined(alarm.Condition) && Enum.IsDefined(alarm.Severity), "Invalid alarm type.");
            Require(tag!.Type == (alarm.Condition is HmiAlarmCondition.High or HmiAlarmCondition.Low ? HmiTagType.Number : HmiTagType.Boolean), "Alarm condition does not match the tag type.");
            Require(double.IsFinite(alarm.Limit) && double.IsFinite(alarm.Deadband) && alarm.Deadband >= 0 && alarm.DelayMilliseconds is >= 0 and <= 3_600_000, "Invalid alarm threshold, deadband or delay.");
        }
    }
    public static void ValidateValue(HmiTagDefinition tag, HmiValue value, bool enforceRange)
    {
        Require(value.Type == tag.Type && Enum.IsDefined(value.Type), $"Value type does not match {tag.Name}.");
        Require(double.IsFinite(value.Number) && value.Text != null && value.Text.Length <= 4096, "Invalid tag value.");
        if (enforceRange && tag.Type == HmiTagType.Number)
            Require(value.Number >= tag.Minimum && value.Number <= tag.Maximum, $"{tag.Name} must be between {tag.Minimum} and {tag.Maximum}.");
    }
    private static void Binding(string name, Dictionary<string, HmiTagDefinition> tags, bool boolean)
    {
        Require(name != null, "Null binding.");
        if (name.Length == 0) return;
        Require(tags.TryGetValue(name, out var tag), $"Unknown binding tag: {name}.");
        Require(!boolean || tag!.Type == HmiTagType.Boolean, "Visibility/enabled bindings require Boolean tags.");
    }
    private static void Text(string value, string name, bool empty = false) => Require(value != null && value.Length <= 4096 && (empty || !string.IsNullOrWhiteSpace(value)), $"Invalid {name}.");
    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
