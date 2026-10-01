namespace ProGPU.Hmi;

/// <summary>Reference validation and transactional topology helpers shared by authoring and persistence.</summary>
public static class HmiDiagram
{
    public const int MaximumLinksPerScreen = 256;

    internal static void Validate(HmiScreen screen, IReadOnlyDictionary<string, HmiTagDefinition> tags)
    {
        ValidateTopology(screen);
        foreach (var link in screen.Links)
            if (link.ActivityTag.Length > 0 && (!tags.TryGetValue(link.ActivityTag, out var tag) || tag.Type != HmiTagType.Boolean))
                throw new InvalidDataException("Diagram activity bindings require an existing Boolean tag.");
    }

    public static void ValidateTopology(HmiScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        if (screen.Elements is null || screen.Elements.Count > 5000 || screen.Elements.Any(e => e is null ||
            string.IsNullOrWhiteSpace(e.Id) || e.Appearance is null || !float.IsFinite(e.X) || !float.IsFinite(e.Y) ||
            Math.Abs(e.X) > 65536 || Math.Abs(e.Y) > 65536 || !float.IsFinite(e.Width) || !float.IsFinite(e.Height) || e.Width is < 8 or > 16384 || e.Height is < 8 or > 16384))
            throw new InvalidDataException("Diagram elements require valid geometry and appearance.");
        if (!float.IsFinite(screen.Width) || !float.IsFinite(screen.Height) || screen.Width is < 64 or > 16384 || screen.Height is < 64 or > 16384)
            throw new InvalidDataException("Diagram screen dimensions must be finite and in 64..16384.");
        foreach (var element in screen.Elements) element.Appearance.Validate();
        if (screen.Links is null || screen.Links.Count > MaximumLinksPerScreen)
            throw new InvalidDataException($"A screen supports at most {MaximumLinksPerScreen} diagram links.");
        var elements = new Dictionary<string, HmiElement>(StringComparer.Ordinal);
        foreach (var element in screen.Elements)
            if (!Enum.IsDefined(element.Symbol) || !elements.TryAdd(element.Id, element))
                throw new InvalidDataException("Diagram elements require known symbols and unique identities.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in screen.Links)
        {
            if (link is null || string.IsNullOrWhiteSpace(link.Id) || link.Id.Length > 256 || !ids.Add(link.Id))
                throw new InvalidDataException("Diagram link IDs must be nonempty and unique within a screen.");
            if (link.Name is null || link.Name.Length > 256 || link.ActivityTag is null || link.ActivityTag.Length > 256 ||
                !Enum.IsDefined(link.Kind) || !float.IsFinite(link.Thickness) || link.Thickness is < 1 or > 12 ||
                !float.IsFinite(link.Clearance) || link.Clearance is < 2 or > 128)
                throw new InvalidDataException($"Invalid diagram link appearance: {link.Id}.");
            HmiRouteSegments.Validate(link.Waypoints, link.StraightSegments);
            CheckEnd(link.Source); CheckEnd(link.Target);
            if (link.Source.ElementId == link.Target.ElementId && link.Source.PortId == link.Target.PortId)
                throw new InvalidDataException("A link requires two different ports.");

        }
        void CheckEnd(HmiLinkEndpoint? endpoint)
        {
            if (endpoint is null || endpoint.ElementId is null || endpoint.PortId is null ||
                !elements.TryGetValue(endpoint.ElementId, out var element) ||
                !HmiSymbolPorts.GetPorts(element.Symbol).Any(p => p.Id == endpoint.PortId))
                throw new InvalidDataException("Diagram link refers to a missing element or nozzle port.");
        }
    }

    /// <summary>Delete incident unlocked links with removed equipment, never silently remove a locked connection.</summary>
    public static void RemoveDanglingLinks(HmiScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        var ids = screen.Elements.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        bool Dangling(HmiDiagramLink link) => !ids.Contains(link.Source.ElementId) || !ids.Contains(link.Target.ElementId);
        if (screen.Links.Any(link => link.IsLocked && Dangling(link)))
            throw new InvalidOperationException("Unlock or remove incident diagram links before deleting their equipment.");
        screen.Links.RemoveAll(link => Dangling(link));
    }

    /// <summary>Clone only internal links; connections to unselected external equipment are intentionally not copied.</summary>
    public static List<HmiDiagramLink> CopyInternalLinks(IEnumerable<HmiDiagramLink> links, IReadOnlyDictionary<string, string> identities)
    {
        ArgumentNullException.ThrowIfNull(links); ArgumentNullException.ThrowIfNull(identities);
        var result = new List<HmiDiagramLink>();
        foreach (var link in links)
        {
            if (!identities.TryGetValue(link.Source.ElementId, out var source) || !identities.TryGetValue(link.Target.ElementId, out var target)) continue;
            var copy = link.Copy(newIdentity: true);
            copy.Source.ElementId = source; copy.Target.ElementId = target;
            result.Add(copy);
        }
        return result;
    }
}
