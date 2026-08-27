using ACadSharp;
using ACadSharp.Entities;
using CSMath;

namespace ProGPU.CAD;

public sealed class CadEditHistoryDivergedException : InvalidOperationException
{
    public ulong ExpectedGeneration { get; }

    public ulong ActualGeneration { get; }

    internal CadEditHistoryDivergedException(
        ulong expectedGeneration,
        ulong actualGeneration)
        : base(
            $"CAD edit history expected generation {expectedGeneration}, " +
            $"but the document is at generation {actualGeneration}.")
    {
        ExpectedGeneration = expectedGeneration;
        ActualGeneration = actualGeneration;
    }
}

/// <summary>A typed reversible edit that can be executed by <see cref="CadDocumentHistory"/>.</summary>
public abstract class CadEditCommand
{
    private CadEditCommandState _state;

    public string Description { get; }

    protected CadEditCommand(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Description = description;
    }

    internal void ExecuteFirst(CadDocument document)
    {
        if (_state != CadEditCommandState.New)
        {
            throw new InvalidOperationException("A CAD edit command instance can be executed only once.");
        }

        Apply(document, isRedo: false);
        _state = CadEditCommandState.Applied;
    }

    internal void Undo(CadDocument document)
    {
        if (_state != CadEditCommandState.Applied)
        {
            throw new InvalidOperationException("Only an applied CAD edit command can be undone.");
        }

        Revert(document);
        _state = CadEditCommandState.Reverted;
    }

    internal void Redo(CadDocument document)
    {
        if (_state != CadEditCommandState.Reverted)
        {
            throw new InvalidOperationException("Only a reverted CAD edit command can be redone.");
        }

        Apply(document, isRedo: true);
        _state = CadEditCommandState.Applied;
    }

    internal abstract void Apply(CadDocument document, bool isRedo);

    internal abstract void Revert(CadDocument document);

    protected static Entity[] ResolveModelSpaceEntities(
        CadDocument document,
        ReadOnlySpan<ulong> handles)
    {
        var entities = new Entity[handles.Length];
        for (int i = 0; i < handles.Length; i++)
        {
            Entity? entity = document.GetCadObject<Entity>(handles[i]);
            if (entity is null || !ReferenceEquals(entity.Owner, document.ModelSpace))
            {
                throw new InvalidOperationException(
                    $"Model-space entity handle {handles[i]:X} does not exist.");
            }

            entities[i] = entity;
        }

        return entities;
    }

    private enum CadEditCommandState : byte
    {
        New,
        Applied,
        Reverted,
    }
}

/// <summary>
/// Bounded undo/redo history synchronized with one document session generation.
/// </summary>
public sealed class CadDocumentHistory
{
    public const int DefaultCapacity = 256;

    private readonly object _gate = new();
    private readonly CadDocumentSession _session;
    private readonly List<CadEditCommand> _undo = new();
    private readonly List<CadEditCommand> _redo = new();
    private readonly int _capacity;
    private ulong _expectedGeneration;

    public int UndoCount
    {
        get
        {
            lock (_gate)
            {
                return _undo.Count;
            }
        }
    }

    public int RedoCount
    {
        get
        {
            lock (_gate)
            {
                return _redo.Count;
            }
        }
    }

    public CadDocumentHistory(
        CadDocumentSession session,
        int capacity = DefaultCapacity)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _expectedGeneration = session.ContentGeneration;
    }

    public ulong Execute(CadEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_gate)
        {
            SynchronizeForNewEdit();
            try
            {
                ulong generation = _session.Edit(
                    command.Description,
                    _expectedGeneration,
                    command.ExecuteFirst);
                _redo.Clear();
                _undo.Add(command);
                if (_undo.Count > _capacity)
                {
                    _undo.RemoveAt(0);
                }

                _expectedGeneration = generation;
                return generation;
            }
            catch (CadEditHistoryDivergedException)
            {
                ResetToCurrentGeneration();
                throw;
            }
        }
    }

    public bool TryUndo(out ulong generation)
    {
        lock (_gate)
        {
            if (!IsSynchronized() || _undo.Count == 0)
            {
                generation = _expectedGeneration;
                return false;
            }

            CadEditCommand command = _undo[^1];
            try
            {
                generation = _session.Edit(
                    $"Undo: {command.Description}",
                    _expectedGeneration,
                    command.Undo);
            }
            catch (CadEditHistoryDivergedException)
            {
                ResetToCurrentGeneration();
                generation = _expectedGeneration;
                return false;
            }

            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(command);
            _expectedGeneration = generation;
            return true;
        }
    }

    public bool TryRedo(out ulong generation)
    {
        lock (_gate)
        {
            if (!IsSynchronized() || _redo.Count == 0)
            {
                generation = _expectedGeneration;
                return false;
            }

            CadEditCommand command = _redo[^1];
            try
            {
                generation = _session.Edit(
                    $"Redo: {command.Description}",
                    _expectedGeneration,
                    command.Redo);
            }
            catch (CadEditHistoryDivergedException)
            {
                ResetToCurrentGeneration();
                generation = _expectedGeneration;
                return false;
            }

            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(command);
            _expectedGeneration = generation;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ResetToCurrentGeneration();
        }
    }

    private bool IsSynchronized()
    {
        if (_session.ContentGeneration == _expectedGeneration)
        {
            return true;
        }

        ResetToCurrentGeneration();
        return false;
    }

    private void SynchronizeForNewEdit()
    {
        if (_session.ContentGeneration != _expectedGeneration)
        {
            ResetToCurrentGeneration();
        }
    }

    private void ResetToCurrentGeneration()
    {
        _undo.Clear();
        _redo.Clear();
        _expectedGeneration = _session.ContentGeneration;
    }
}

/// <summary>Translates a stable set of entity handles with exact inverse undo.</summary>
public sealed class CadTranslateEntitiesCommand : CadEditCommand
{
    private readonly ulong[] _handles;
    private readonly XYZ _translation;
    private readonly XYZ _inverseTranslation;

    public ReadOnlyMemory<ulong> Handles => _handles;

    public CadPoint3D Translation { get; }

    public CadTranslateEntitiesCommand(
        IEnumerable<ulong> handles,
        CadPoint3D translation,
        string description = "Translate entities")
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (!double.IsFinite(translation.X) ||
            !double.IsFinite(translation.Y) ||
            !double.IsFinite(translation.Z) ||
            translation == CadPoint3D.Zero)
        {
            throw new ArgumentException(
                "A translation must be finite and non-zero.",
                nameof(translation));
        }

        _handles = handles.Distinct().ToArray();
        if (_handles.Length == 0 || _handles.Any(static handle => handle == 0))
        {
            throw new ArgumentException(
                "At least one non-zero entity handle is required.",
                nameof(handles));
        }

        Translation = translation;
        _translation = new XYZ(translation.X, translation.Y, translation.Z);
        _inverseTranslation = new XYZ(-translation.X, -translation.Y, -translation.Z);
    }

    internal override void Apply(CadDocument document, bool isRedo) =>
        TranslateTransactional(document, _translation, _inverseTranslation);

    internal override void Revert(CadDocument document) =>
        TranslateTransactional(document, _inverseTranslation, _translation);

    private void TranslateTransactional(
        CadDocument document,
        XYZ translation,
        XYZ rollbackTranslation)
    {
        Entity[] entities = ResolveModelSpaceEntities(document, _handles);
        int applied = 0;
        try
        {
            for (; applied < entities.Length; applied++)
            {
                entities[applied].ApplyTranslation(translation);
            }
        }
        catch
        {
            for (int i = applied - 1; i >= 0; i--)
            {
                entities[i].ApplyTranslation(rollbackTranslation);
            }

            throw;
        }
    }

}

/// <summary>Sets visibility for a stable set of model-space entity handles.</summary>
public sealed class CadSetEntityVisibilityCommand : CadEditCommand
{
    private readonly ulong[] _handles;
    private bool[]? _previousValues;

    public bool IsInvisible { get; }

    public CadSetEntityVisibilityCommand(
        IEnumerable<ulong> handles,
        bool isInvisible,
        string description = "Set entity visibility")
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(handles);
        _handles = handles.Distinct().ToArray();
        if (_handles.Length == 0 || _handles.Any(static handle => handle == 0))
        {
            throw new ArgumentException(
                "At least one non-zero entity handle is required.",
                nameof(handles));
        }

        IsInvisible = isInvisible;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity[] entities = ResolveModelSpaceEntities(document, _handles);
        if (!isRedo)
        {
            _previousValues = entities.Select(static entity => entity.IsInvisible).ToArray();
        }

        foreach (Entity entity in entities)
        {
            entity.IsInvisible = IsInvisible;
        }
    }

    internal override void Revert(CadDocument document)
    {
        Entity[] entities = ResolveModelSpaceEntities(document, _handles);
        bool[] values = _previousValues ??
            throw new InvalidOperationException("The visibility command has not been applied.");
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i].IsInvisible = values[i];
        }
    }

}
