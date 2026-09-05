using System.Numerics;

namespace ProGPU.Samples.Suntrail.Game;

/// <summary>CPU-only editor with 64 bounded undo snapshots and one transaction per drag.</summary>
public sealed class LevelEditor
{
    private sealed record State(string Name, int Biome, LevelObject[] Objects);
    private readonly List<State> _undo = [], _redo = [];
    private readonly List<LevelObject> _objects = [];
    private State? _drag;
    public string Name { get; private set; } = "";
    public int Biome { get; private set; }
    public int Selected { get; private set; } = -1;
    public IReadOnlyList<LevelObject> Objects => _objects;
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public bool IsDragging => _drag is not null;
    public event Action? Changed;
    public LevelEditor(LevelDocument document) => Load(document);
    public void Load(LevelDocument document)
    {
        _drag = null; _undo.Clear(); _redo.Clear(); Selected = -1;
        Restore(new(document.Name, document.Biome, document.Objects.ToArray()));
    }
    public LevelDocument Snapshot() => new(Name, Biome, _objects.ToArray());
    private State Capture() => new(Name, Biome, _objects.ToArray());
    private void Restore(State state)
    {
        Name = state.Name; Biome = state.Biome;
        _objects.Clear(); _objects.AddRange(state.Objects);
        Selected = Math.Min(Selected, _objects.Count - 1); Changed?.Invoke();
    }
    private void Remember(State state)
    {
        if (_undo.Count == 64) _undo.RemoveAt(0);
        _undo.Add(state); _redo.Clear();
    }
    public void SetBiome(int biome)
    {
        if ((uint)biome >= 8 || Biome == biome) return;
        CancelDrag(); Remember(Capture()); Biome = biome; Changed?.Invoke();
    }
    public void Select(int index) { Selected = index >= 0 && index < _objects.Count ? index : -1; Changed?.Invoke(); }
    public int HitTest(Vector2 p)
    {
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            var b = SelectionBounds(_objects[i]);
            if (p.X >= b.X && p.X <= b.Right && p.Y >= b.Y && p.Y <= b.Bottom) return i;
        }
        return -1;
    }
    public static Box SelectionBounds(LevelObject item) => item.Kind switch
    {
        LevelObjectKind.Coin or LevelObjectKind.Relic => new(item.Bounds.X - 15, item.Bounds.Y - 15, 30, 30),
        LevelObjectKind.Checkpoint => new(item.Bounds.X, item.Bounds.Y - 100, 30, 100),
        LevelObjectKind.Exit => new(item.Bounds.X, item.Bounds.Y - 150, 70, 150),
        LevelObjectKind.Spawn => new(item.Bounds.X, item.Bounds.Y, 30, 48),
        _ => item.Bounds
    };
    public void Add(LevelObjectKind kind, Vector2 p)
    {
        CancelDrag();
        if (_objects.Count == LevelDocument.MaximumObjects) throw new FormatException("The editor object limit is reached.");
        Remember(Capture());
        // Spawn/exit tools relocate their existing marker, preserving exactly one.
        int existing = kind is LevelObjectKind.Spawn or LevelObjectKind.Exit ? _objects.FindIndex(o => o.Kind == kind) : -1;
        var size = kind switch
        {
            LevelObjectKind.Ground => new Vector2(320, 500), LevelObjectKind.Ledge or LevelObjectKind.Moving => new(160, 24),
            LevelObjectKind.Crate or LevelObjectKind.Stone => new(64, 64), LevelObjectKind.Pipe => new(96, 96),
            LevelObjectKind.Hazard => new(64, 24), LevelObjectKind.Enemy => new(42, 34),
            LevelObjectKind.Saw => new(42, 42), LevelObjectKind.Flame => new(30, 90), LevelObjectKind.Crusher => new(64, 80), _ => Vector2.Zero
        };
        var item = new LevelObject(kind, new(Snap(p.X, 0, 30_000), Snap(p.Y, 0, 944), size.X, size.Y),
            kind is LevelObjectKind.Moving or LevelObjectKind.Enemy or LevelObjectKind.Saw ? 64 : kind == LevelObjectKind.Crusher ? 160 : 0);
        if (existing >= 0) { _objects[existing] = item; Selected = existing; }
        else { Selected = _objects.Count; _objects.Add(item); }
        Changed?.Invoke();
    }
    private static float Snap(float value, float min, float max) => Math.Clamp(MathF.Round(value / 16) * 16, min, max);
    public void BeginDrag(int index) { CancelDrag(); Select(index); if (Selected >= 0) _drag = Capture(); }
    public void MoveSelected(Vector2 delta)
    {
        if (_drag is null || Selected < 0 || !float.IsFinite(delta.X) || !float.IsFinite(delta.Y)) return;
        var original = _drag.Objects[Selected];
        _objects[Selected] = original with { Bounds = original.Bounds with { X = Snap(original.Bounds.X + delta.X, 0, 30_000), Y = Snap(original.Bounds.Y + delta.Y, 0, 944) } };
        Changed?.Invoke();
    }
    public void CommitDrag()
    {
        if (_drag is not { } start) return;
        _drag = null;
        if (!start.Objects.AsSpan().SequenceEqual(_objects.ToArray())) Remember(start);
        Changed?.Invoke();
    }
    public void CancelDrag() { if (_drag is { } state) { _drag = null; Restore(state); } }
    public void DeleteSelected()
    {
        CancelDrag(); if (Selected < 0) return;
        Remember(Capture()); _objects.RemoveAt(Selected); Selected = -1; Changed?.Invoke();
    }
    public void ResizeSelected(float delta)
    {
        CancelDrag(); if (Selected < 0) return;
        var item = _objects[Selected];
        if (item.Kind is not (LevelObjectKind.Ground or LevelObjectKind.Ledge or LevelObjectKind.Moving or LevelObjectKind.Stone or LevelObjectKind.Hazard)) return;
        Remember(Capture()); _objects[Selected] = item with { Bounds = item.Bounds with { Width = Snap(item.Bounds.Width + delta, 16, 2000) } }; Changed?.Invoke();
    }
    public void Undo()
    {
        CancelDrag(); if (_undo.Count == 0) return;
        _redo.Add(Capture()); var state = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); Restore(state);
    }
    public void Redo()
    {
        CancelDrag(); if (_redo.Count == 0) return;
        _undo.Add(Capture()); var state = _redo[^1]; _redo.RemoveAt(_redo.Count - 1); Restore(state);
    }
}
