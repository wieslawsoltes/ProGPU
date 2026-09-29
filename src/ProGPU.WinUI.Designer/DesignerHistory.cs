namespace ProGPU.WinUI.Designer;

/// <summary>Bounded, framework-independent snapshot journal shared by specialized designer workbenches.</summary>
public sealed class DesignerHistory<T>
{
    private readonly List<(T State, string Description)> _states = [];
    private readonly IEqualityComparer<T> _comparer;
    private readonly Func<T, long> _measure;
    private readonly int _capacity;
    private readonly long _byteBudget;
    private int _position;
    private T _saved = default!;
    public T Current => _states[_position].State;
    public bool CanUndo => _position > 0;
    public bool CanRedo => _position + 1 < _states.Count;
    public bool IsDirty => !_comparer.Equals(Current, _saved);
    public string UndoDescription => CanUndo ? _states[_position].Description : "";
    public string RedoDescription => CanRedo ? _states[_position + 1].Description : "";

    public DesignerHistory(T initial, int capacity = 80, long byteBudget = 32 * 1024 * 1024,
        Func<T, long>? measure = null, IEqualityComparer<T>? comparer = null)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (byteBudget < 1) throw new ArgumentOutOfRangeException(nameof(byteBudget));
        _capacity = capacity;
        _byteBudget = byteBudget;
        _measure = measure ?? (_ => 1);
        _comparer = comparer ?? EqualityComparer<T>.Default;
        Reset(initial);
    }
    public void Reset(T state)
    {
        EnsureSize(state);
        _states.Clear();
        _states.Add((state, "Initial state"));
        _position = 0;
        _saved = state;
    }
    public void MarkSaved() => _saved = Current;
    public bool Record(T state, string description)
    {
        if (_comparer.Equals(state, Current)) return false;
        EnsureSize(state);
        if (CanRedo) _states.RemoveRange(_position + 1, _states.Count - _position - 1);
        _states.Add((state, description));
        _position++;
        long size = _states.Sum(s => _measure(s.State));
        while (_states.Count > 1 && (_states.Count > _capacity || size > _byteBudget))
        {
            size -= _measure(_states[0].State);
            _states.RemoveAt(0);
            _position--;
        }
        return true;
    }
    public T Undo() { if (CanUndo) _position--; return Current; }
    public T Redo() { if (CanRedo) _position++; return Current; }
    private void EnsureSize(T state)
    {
        long size = _measure(state);
        if (size < 0 || size > _byteBudget) throw new InvalidOperationException("The designer snapshot exceeds the configured history budget.");
    }
}
