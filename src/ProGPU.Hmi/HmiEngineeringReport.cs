namespace ProGPU.Hmi;

public enum HmiDiagnosticSeverity { Information, Warning, Error }
public enum HmiTagReferenceKind { Value, Visibility, Enabled, State, Command, Alarm, Recipe, Acquisition, Permissive, Faceplate, Diagram }

public sealed record HmiDiagnostic(HmiDiagnosticSeverity Severity, string Code, string Message,
    string ScreenId = "", string ElementId = "", string Tag = "");
public sealed record HmiTagReference(string Tag, HmiTagReferenceKind Kind, string Owner,
    string ScreenId = "", string ElementId = "");
public sealed record HmiEngineeringReport(IReadOnlyList<HmiDiagnostic> Diagnostics,
    IReadOnlyList<HmiTagReference> References, bool IsTruncated);

/// <summary>Nonmutating, bounded engineering checks and tag cross-references. Warnings are not commissioning approval.</summary>
public static class HmiProjectAnalyzer
{
    public static HmiEngineeringReport Analyze(HmiProject project, int maximumResults = 8192)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (maximumResults is < 1 or > 65536) throw new ArgumentOutOfRangeException(nameof(maximumResults));
        var diagnostics = new List<HmiDiagnostic>();
        var references = new List<HmiTagReference>();
        bool truncated = false;
        void Issue(HmiDiagnosticSeverity severity, string code, string message, string screen = "", string element = "", string tag = "")
        {
            if (diagnostics.Count < maximumResults) diagnostics.Add(new(severity, code, message, screen, element, tag));
            else truncated = true;
        }
        try { HmiProjectSerializer.Validate(project); }
        catch (InvalidDataException error)
        {
            Issue(HmiDiagnosticSeverity.Error, "HMI0001", error.Message);
            return new(diagnostics.AsReadOnly(), references.AsReadOnly(), false);
        }
        var tags = project.Tags.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var acquired = project.Connections.SelectMany(c => c.Mappings).Select(m => m.Tag).ToHashSet(StringComparer.Ordinal);
        var externallyWritable = project.Connections.SelectMany(c => c.Mappings).Where(m => m.Writable).Select(m => m.Tag).ToHashSet(StringComparer.Ordinal);
        void Reference(string tag, HmiTagReferenceKind kind, string owner, string screen = "", string element = "")
        {
            if (tag.Length == 0 || tag.StartsWith('$')) return;
            if (kind != HmiTagReferenceKind.Acquisition) used.Add(tag);
            if (references.Count < maximumResults) references.Add(new(tag, kind, owner, screen, element));
            else truncated = true;
        }
        var reached = new HashSet<string>(StringComparer.Ordinal) { project.StartScreenId };
        var screenById = project.Screens.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var pending = new Queue<string>(); pending.Enqueue(project.StartScreenId);
        while (pending.TryDequeue(out var id))
            foreach (var element in screenById[id].Elements)
                if (element.Action.Kind == HmiActionKind.Navigate && reached.Add(element.Action.Target)) pending.Enqueue(element.Action.Target);
        foreach (var screen in project.Screens)
        {
            if (!reached.Contains(screen.Id)) Issue(HmiDiagnosticSeverity.Information, "HMI1001", "Screen is not reachable from the start screen through configured navigation actions.", screen.Id);
            foreach (var link in screen.Links)
                Reference(link.ActivityTag, HmiTagReferenceKind.Diagram, link.Name, screen.Id);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in screen.Elements)
            {
                if (!names.Add(element.Name)) Issue(HmiDiagnosticSeverity.Information, "HMI1002", "Repeated component name: " + element.Name, screen.Id, element.Id);
                if (element.X < 0 || element.Y < 0 || element.X + element.Width > screen.Width || element.Y + element.Height > screen.Height)
                    Issue(HmiDiagnosticSeverity.Warning, "HMI1003", "Component extends beyond the screen bounds.", screen.Id, element.Id);
                Reference(element.Tag, HmiTagReferenceKind.Value, element.Name, screen.Id, element.Id);
                Reference(element.VisibilityTag, HmiTagReferenceKind.Visibility, element.Name, screen.Id, element.Id);
                Reference(element.EnabledTag, HmiTagReferenceKind.Enabled, element.Name, screen.Id, element.Id);
                foreach (var rule in element.States) Reference(rule.Tag, HmiTagReferenceKind.State, element.Name, screen.Id, element.Id);
                bool numeric = element.Symbol is HmiSymbol.NumericDisplay or HmiSymbol.NumericInput or HmiSymbol.Tank or HmiSymbol.Gauge or HmiSymbol.BarGraph or HmiSymbol.Trend or HmiSymbol.Thermometer;
                if (numeric && element.Tag.Length == 0)
                    Issue(HmiDiagnosticSeverity.Warning, "HMI2001", "Instrument has no value tag; its displayed value is not process feedback.", screen.Id, element.Id);
                if (numeric && tags.TryGetValue(element.Tag, out var tag) && tag.Type != HmiTagType.Number)
                    Issue(HmiDiagnosticSeverity.Warning, "HMI2002", "Numeric instrument is bound to a nonnumeric tag.", screen.Id, element.Id, tag.Name);
                if (element.Symbol == HmiSymbol.NumericInput && tags.TryGetValue(element.Tag, out var input) && !input.Writable)
                    Issue(HmiDiagnosticSeverity.Warning, "HMI2003", "Numeric input targets a read-only project tag.", screen.Id, element.Id, input.Name);
                if (element.Action.Kind is HmiActionKind.WriteTag or HmiActionKind.ToggleTag)
                {
                    Reference(element.Action.Target, HmiTagReferenceKind.Command, element.Name, screen.Id, element.Id);
                    if (project.Connections.Count > 0 && !externallyWritable.Contains(element.Action.Target))
                        Issue(HmiDiagnosticSeverity.Warning, "HMI2004", "Command has no writable external mapping. Local simulation remains separate.", screen.Id, element.Id, element.Action.Target);
                }
                if (element.Action.Kind == HmiActionKind.None && element.Symbol is HmiSymbol.PushButton or HmiSymbol.NavigationButton or HmiSymbol.RecipeButton)
                    Issue(HmiDiagnosticSeverity.Warning, "HMI2005", "Operator button has no configured action.", screen.Id, element.Id);
            }
        }
        foreach (var alarm in project.Alarms) Reference(alarm.Tag, HmiTagReferenceKind.Alarm, alarm.Message);
        foreach (var recipe in project.Recipes)
            foreach (var pair in recipe.Values) Reference(pair.Key, HmiTagReferenceKind.Recipe, recipe.Name);
        foreach (var connection in project.Connections)
            foreach (var mapping in connection.Mappings)
            {
                Reference(mapping.Tag, HmiTagReferenceKind.Acquisition, connection.Name);
                Reference(mapping.InterlockTag, HmiTagReferenceKind.Permissive, connection.Name);
                if (mapping.Writable && mapping.InterlockTag.Length == 0)
                    Issue(HmiDiagnosticSeverity.Information, "HMI3001", "External write has no client-side permissive. Host authorization and controller-side interlocks are still required.", tag: mapping.Tag);
            }
        foreach (var template in project.Faceplates)
            foreach (var element in template.Elements)
            {
                Reference(element.Tag, HmiTagReferenceKind.Faceplate, template.Name);
                Reference(element.VisibilityTag, HmiTagReferenceKind.Faceplate, template.Name);
                Reference(element.EnabledTag, HmiTagReferenceKind.Faceplate, template.Name);
                foreach (var state in element.States) Reference(state.Tag, HmiTagReferenceKind.Faceplate, template.Name);
                if (element.Action.Kind is HmiActionKind.WriteTag or HmiActionKind.ToggleTag)
                    Reference(element.Action.Target, HmiTagReferenceKind.Faceplate, template.Name);
            }
        foreach (var tag in project.Tags)
        {
            if (!used.Contains(tag.Name)) Issue(HmiDiagnosticSeverity.Information, "HMI3002", "Tag has no display, alarm, recipe or command consumer.", tag: tag.Name);
            if (project.Connections.Count > 0 && used.Contains(tag.Name) && !acquired.Contains(tag.Name))
                Issue(HmiDiagnosticSeverity.Information, "HMI3003", "Tag has no configured live source; initial/simulation values are not hardware feedback.", tag: tag.Name);
        }
        return new(diagnostics.AsReadOnly(), references.AsReadOnly(), truncated);
    }
}
