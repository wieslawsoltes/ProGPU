using ProGPU.Hmi;
using ProGPU.WinUI.Designer;

namespace ProGPU.WinUI.Hmi.Designer;

/// <summary>Transactional project editing. Preview runtime state is never part of a journal entry.</summary>
public sealed class HmiDesignerSession
{
    private readonly DesignerHistory<string> _history;
    internal HmiProject Document { get; private set; }
    public string ActiveScreenId { get; private set; }
    public bool IsDirty => _history.IsDirty;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public string UndoDescription => _history.UndoDescription;
    public event Action? Changed;
    public event Action? ScreenChanged;
    internal HmiScreen ActiveScreen => Document.Screens.Single(s => s.Id == ActiveScreenId);

    public HmiDesignerSession(HmiProject? project = null)
    {
        Document = HmiProjectSerializer.Clone(project ?? HmiDemoProject.Create());
        ActiveScreenId = Document.StartScreenId;
        _history = new DesignerHistory<string>(HmiProjectSerializer.Serialize(Document), measure: json => json.Length * 2L);
    }
    public HmiProject GetProject() => HmiProjectSerializer.Clone(Document);
    public string ExportJson() => _history.Current;
    public void MarkSaved(string savedJson)
    {
        if (savedJson == _history.Current) _history.MarkSaved();
        Changed?.Invoke();
    }
    public void Open(HmiProject project)
    {
        var copy = HmiProjectSerializer.Clone(project);
        string json = HmiProjectSerializer.Serialize(copy);
        _history.Reset(json);
        Document = copy;
        ActiveScreenId = copy.StartScreenId;
        Changed?.Invoke();
        ScreenChanged?.Invoke();
    }
    public void ImportJson(string json) => Replace(HmiProjectSerializer.Deserialize(json), "Import project");
    public void Edit(string description, Action<HmiProject> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var copy = GetProject();
        edit(copy);
        Replace(copy, description);
    }
    public void Replace(HmiProject project, string description)
    {
        string json = HmiProjectSerializer.Serialize(project);
        if (!_history.Record(json, description)) return;
        Document = HmiProjectSerializer.Deserialize(json);
        EnsureActiveScreen();
        Changed?.Invoke();
    }
    public void SelectScreen(string id)
    {
        if (!Document.Screens.Any(s => s.Id == id)) throw new ArgumentException("Unknown screen.", nameof(id));
        if (ActiveScreenId == id) return;
        ActiveScreenId = id;
        ScreenChanged?.Invoke();
    }
    public void AddScreen()
    {
        string id = Guid.NewGuid().ToString("N");
        Edit("Add screen", p => p.Screens.Add(new HmiScreen { Id = id, Name = $"Screen {p.Screens.Count + 1}" }));
        SelectScreen(id);
    }
    public void DuplicateScreen()
    {
        string source = ActiveScreenId, id = Guid.NewGuid().ToString("N");
        Edit("Duplicate screen", p =>
        {
            var screen = p.Screens.Single(s => s.Id == source);
            var elements = screen.Elements.Select(e => e.Copy(newIdentity: true)).ToList();
            var identities = screen.Elements.Select((e, i) => (e.Id, NewId: elements[i].Id)).ToDictionary(e => e.Id, e => e.NewId, StringComparer.Ordinal);
            p.Screens.Add(new HmiScreen
            {
                Id = id, Name = screen.Name + " copy", Width = screen.Width, Height = screen.Height,
                Elements = elements, Links = HmiDiagram.CopyInternalLinks(screen.Links, identities)
            });
        });
        SelectScreen(id);
    }
    public void DeleteScreen()
    {
        string id = ActiveScreenId;
        Edit("Delete screen", p =>
        {
            if (p.Screens.Count == 1) throw new InvalidOperationException("A project must retain at least one screen.");
            if (p.Screens.Where(s => s.Id != id).SelectMany(s => s.Elements).Any(e => e.Action.Kind == HmiActionKind.Navigate && e.Action.Target == id))
                throw new InvalidOperationException("Change navigation actions targeting this screen before deleting it.");
            p.Screens.RemoveAll(s => s.Id == id);
            if (p.StartScreenId == id) p.StartScreenId = p.Screens[0].Id;
        });
        ScreenChanged?.Invoke();
    }
    public void RenameTag(string oldName, string newName)
    {
        Edit("Rename tag", p =>
        {
            p.Tags.Single(t => t.Name == oldName).Name = newName;
            foreach (var element in p.Screens.SelectMany(s => s.Elements))
            {
                foreach (var rule in element.States) if (rule.Tag == oldName) rule.Tag = newName;
                if (element.Tag == oldName) element.Tag = newName;
                if (element.VisibilityTag == oldName) element.VisibilityTag = newName;
                if (element.EnabledTag == oldName) element.EnabledTag = newName;
                if (element.Action.Kind is (HmiActionKind.ToggleTag or HmiActionKind.WriteTag) && element.Action.Target == oldName) element.Action.Target = newName;
            }
            foreach (var mapping in p.Connections.SelectMany(c => c.Mappings))
            {
                if (mapping.Tag == oldName) mapping.Tag = newName;
                if (mapping.InterlockTag == oldName) mapping.InterlockTag = newName;
            }
            foreach (var link in p.Screens.SelectMany(s => s.Links)) if (link.ActivityTag == oldName) link.ActivityTag = newName;
            foreach (var alarm in p.Alarms) if (alarm.Tag == oldName) alarm.Tag = newName;
            foreach (var recipe in p.Recipes)
                if (recipe.Values.Remove(oldName, out var value)) recipe.Values.Add(newName, value);
        });
    }
    public void Undo()
    {
        if (!_history.CanUndo) return;
        Document = HmiProjectSerializer.Deserialize(_history.Undo());
        EnsureActiveScreen(); Changed?.Invoke();
    }
    public void Redo()
    {
        if (!_history.CanRedo) return;
        Document = HmiProjectSerializer.Deserialize(_history.Redo());
        EnsureActiveScreen(); Changed?.Invoke();
    }
    private void EnsureActiveScreen()
    {
        if (!Document.Screens.Any(s => s.Id == ActiveScreenId)) ActiveScreenId = Document.StartScreenId;
    }
}
