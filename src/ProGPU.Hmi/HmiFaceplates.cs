namespace ProGPU.Hmi;

/// <summary>Transactional callers apply these operations to a candidate document, then validate before publishing.</summary>
public static class HmiFaceplates
{
    public static HmiFaceplateTemplate Capture(HmiProject project, HmiScreen screen, IReadOnlyCollection<string> elementIds, string name)
    {
        var selected = screen.Elements.Where(e => elementIds.Contains(e.Id)).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select equipment components first.");
        float x = selected.Min(e => e.X), y = selected.Min(e => e.Y);
        var template = new HmiFaceplateTemplate
        {
            Name = name, Width = Math.Max(64, selected.Max(e => e.X + e.Width) - x),
            Height = Math.Max(64, selected.Max(e => e.Y + e.Height) - y),
            Elements = selected.Select(e => e.Copy(newIdentity: true)).ToList()
        };
        var tags = selected.SelectMany(BindingNames).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var tag in tags) template.Slots.Add(CloneTag(project.Tags.Single(t => t.Name == tag), tag));
        foreach (var element in template.Elements)
        {
            element.X -= x; element.Y -= y; element.Group = ""; element.IsLocked = false;
            ReplaceBindings(element, tag => "$" + tag);
        }
        return template;
    }
    public static IReadOnlyList<string> Instantiate(HmiProject project, HmiScreen screen, string templateId, string prefix, float x, float y)
    {
        ValidatePrefix(prefix);
        var template = project.Faceplates.SingleOrDefault(t => t.Id == templateId) ?? throw new InvalidOperationException("Unknown faceplate template.");
        EnsureSlots(project, template, prefix);
        string instanceId = Guid.NewGuid().ToString("N");
        var elements = Materialize(template, prefix, instanceId, x, y, null);
        screen.Elements.AddRange(elements);
        return elements.Select(e => e.Id).ToArray();
    }
    /// <summary>Explicitly replaces instance overrides from their master, retaining instance origins and matching child identities.</summary>
    public static int Synchronize(HmiProject project, string templateId)
    {
        var template = project.Faceplates.Single(t => t.Id == templateId);
        int count = 0;
        foreach (var screen in project.Screens)
        {
            foreach (var group in screen.Elements.Where(e => e.FaceplateTemplateId == templateId).GroupBy(e => e.FaceplateInstanceId).ToArray())
            {
                var current = group.ToArray();
                var first = current.FirstOrDefault(e => template.Elements.Any(source => source.Id == e.FaceplateSourceId)) ?? current[0];
                // Original master-local coordinates retain the instance origin across master geometry edits.
                float x = first.X - first.FaceplateSourceX, y = first.Y - first.FaceplateSourceY;
                string prefix = first.FaceplatePrefix;
                EnsureSlots(project, template, prefix);
                var identities = current.ToDictionary(e => e.FaceplateSourceId, e => e.Id, StringComparer.Ordinal);
                int index = screen.Elements.IndexOf(current[0]);
                var replacement = Materialize(template, prefix, group.Key, x, y, identities);
                screen.Elements.RemoveAll(e => e.FaceplateInstanceId == group.Key);
                screen.Elements.InsertRange(Math.Min(index, screen.Elements.Count), replacement);
                count++;
            }
        }
        return count;
    }
    public static void Detach(HmiScreen screen, IEnumerable<string> instanceIds)
    {
        var ids = instanceIds.Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var element in screen.Elements.Where(e => ids.Contains(e.FaceplateInstanceId))) ClearLink(element);
    }
    public static void ClearLink(HmiElement element)
    {
        element.FaceplateTemplateId = ""; element.FaceplateInstanceId = "";
        element.FaceplateSourceId = ""; element.FaceplatePrefix = "";
        element.FaceplateSourceX = 0; element.FaceplateSourceY = 0;
    }
    public static void ValidateLibrary(HmiProject project)
    {
        if (project.Faceplates is not { Count: <= 128 }) throw new InvalidDataException("A project supports at most 128 faceplate templates.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var template in project.Faceplates)
        {
            if (template == null || string.IsNullOrWhiteSpace(template.Id) || template.Id.Length > 128 || !ids.Add(template.Id) ||
                string.IsNullOrWhiteSpace(template.Name) || template.Name.Length > 256 || template.Revision < 1 ||
                template.Elements is not { Count: > 0 and <= 1000 } || template.Slots is not { Count: <= 256 })
                throw new InvalidDataException("Invalid faceplate template identity or component/slot budget.");
            if (template.Slots.Any(t => t == null) || template.Elements.Any(e => e == null || e.Action == null || e.Action.Target == null || e.States == null ||
                e.Tag == null || e.VisibilityTag == null || e.EnabledTag == null || e.FaceplateTemplateId == null || e.FaceplateInstanceId == null ||
                e.States.Any(s => s == null || s.Tag == null)))
                throw new InvalidDataException("Faceplate contains null elements, slots, state rules, actions or bindings.");
            // Reuse normal graph validation, replacing slot tokens with a concrete isolated tag namespace.
            var validation = new HmiProject { StartScreenId = "template", Name = template.Name };
            // External navigation is checked against the real project below; do not inflate the synthetic screen budget.
            validation.Tags = template.Slots.Select(t => CloneTag(t, t.Name)).ToList();
            var elements = template.Elements.Select(e => e.Copy()).ToList();
            foreach (var element in elements)
            {
                if (element.FaceplateTemplateId.Length > 0 || element.FaceplateInstanceId.Length > 0) throw new InvalidDataException("Nested faceplates are not admitted.");
                if (element.Action.Kind == HmiActionKind.Navigate)
                {
                    if (!project.Screens.Any(s => s.Id == element.Action.Target)) throw new InvalidDataException("Faceplate navigation target does not exist.");
                    element.Action = new HmiAction(); // Validation-only clone; the real template keeps its action.
                }
                ReplaceBindings(element, token => token.StartsWith('$') ? token[1..] : throw new InvalidDataException("Faceplate tag bindings must use $slot names."));
            }
            validation.Screens.Add(new HmiScreen { Id = "template", Name = template.Name, Width = template.Width, Height = template.Height, Elements = elements });
            // Recipes are external project commands. Slot-only templates deliberately do not embed them.
            if (elements.Any(e => e.Action.Kind == HmiActionKind.ApplyRecipe)) throw new InvalidDataException("Faceplate recipes must be assigned after instantiation.");
            HmiProjectSerializer.Validate(validation);
        }
        foreach (var screen in project.Screens)
        {
            foreach (var group in screen.Elements.Where(e => e.FaceplateInstanceId.Length > 0).GroupBy(e => e.FaceplateInstanceId))
            {
                var first = group.First();
                if (!ids.Contains(first.FaceplateTemplateId) || group.Select(e => e.FaceplateSourceId).Distinct().Count() != group.Count() ||
                    group.Any(e => e.FaceplateTemplateId != first.FaceplateTemplateId || e.FaceplatePrefix != first.FaceplatePrefix || e.FaceplateSourceId.Length == 0))
                    throw new InvalidDataException("Invalid faceplate instance links.");
                ValidatePrefix(first.FaceplatePrefix);
            }
        }
    }
    private static List<HmiElement> Materialize(HmiFaceplateTemplate template, string prefix, string instance, float x, float y, Dictionary<string, string>? identities)
    {
        return template.Elements.Select(source =>
        {
            var element = source.Copy(newIdentity: true);
            if (identities != null && identities.TryGetValue(source.Id, out var id)) element.Id = id;
            element.X += x; element.Y += y;
            element.Name = prefix + "." + source.Name; element.Group = instance;
            element.FaceplateTemplateId = template.Id; element.FaceplateInstanceId = instance;
            element.FaceplateSourceId = source.Id; element.FaceplatePrefix = prefix;
            element.FaceplateSourceX = source.X; element.FaceplateSourceY = source.Y;
            ReplaceBindings(element, token => prefix + "." + token.TrimStart('$'));
            return element;
        }).ToList();
    }
    private static void EnsureSlots(HmiProject project, HmiFaceplateTemplate template, string prefix)
    {
        foreach (var slot in template.Slots)
        {
            string name = prefix + "." + slot.Name;
            var existing = project.Tags.SingleOrDefault(t => t.Name == name);
            if (existing == null) project.Tags.Add(CloneTag(slot, name));
            else if (existing.Type != slot.Type || slot.Writable && !existing.Writable)
                throw new InvalidDataException($"Faceplate slot {name} conflicts with its existing tag type or access.");
        }
    }
    private static IEnumerable<string> BindingNames(HmiElement element)
    {
        if (element.Tag.Length > 0) yield return element.Tag;
        if (element.VisibilityTag.Length > 0) yield return element.VisibilityTag;
        if (element.EnabledTag.Length > 0) yield return element.EnabledTag;
        if (element.Action.Kind is HmiActionKind.ToggleTag or HmiActionKind.WriteTag) yield return element.Action.Target;
        foreach (var state in element.States) if (state.Tag.Length > 0) yield return state.Tag;
    }
    private static void ReplaceBindings(HmiElement element, Func<string, string> map)
    {
        if (element.Tag.Length > 0) element.Tag = map(element.Tag);
        if (element.VisibilityTag.Length > 0) element.VisibilityTag = map(element.VisibilityTag);
        if (element.EnabledTag.Length > 0) element.EnabledTag = map(element.EnabledTag);
        if (element.Action.Kind is HmiActionKind.ToggleTag or HmiActionKind.WriteTag) element.Action.Target = map(element.Action.Target);
        foreach (var state in element.States) if (state.Tag.Length > 0) state.Tag = map(state.Tag);
    }
    private static HmiTagDefinition CloneTag(HmiTagDefinition source, string name) => new()
    {
        Name = name, Type = source.Type, InitialValue = source.InitialValue, Unit = source.Unit,
        Description = source.Description, Writable = source.Writable, Minimum = source.Minimum, Maximum = source.Maximum,
        StaleAfterMilliseconds = source.StaleAfterMilliseconds, Simulation = source.Simulation, PeriodSeconds = source.PeriodSeconds
    };
    private static void ValidatePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length > 128 || prefix.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new InvalidDataException("Faceplate prefixes use letters, digits, dot, underscore and dash (maximum 128 characters).");
    }
}
