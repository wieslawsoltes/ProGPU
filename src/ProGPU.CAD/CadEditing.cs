using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
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
            entities[i] = ResolveModelSpaceEntity(document, handles[i]);
        }

        return entities;
    }

    protected static Entity ResolveModelSpaceEntity(
        CadDocument document,
        ulong handle)
    {
        Entity? entity = document.GetCadObject<Entity>(handle);
        if (entity is null || !ReferenceEquals(entity.Owner, document.ModelSpace))
        {
            throw new InvalidOperationException(
                $"Model-space entity handle {handle:X} does not exist.");
        }
        return entity;
    }

    protected static void ValidateModelSpaceEntities(
        CadDocument document,
        ReadOnlySpan<Entity> entities)
    {
        foreach (Entity entity in entities)
        {
            ValidateModelSpaceEntity(document, entity);
        }
    }

    protected static void ValidateModelSpaceEntity(
        CadDocument document,
        Entity entity)
    {
        if (!ReferenceEquals(entity.Owner, document.ModelSpace))
        {
            throw new InvalidOperationException(
                "A retained edit entity is no longer owned by this document's model space.");
        }
    }

    protected static TValue[] CaptureEntityValues<TValue>(
        Entity[] entities,
        Func<Entity, TValue> getter) =>
        entities.Select(getter).ToArray();

    protected static void SetEntityValuesTransactional<TValue>(
        Entity[] entities,
        TValue target,
        Func<Entity, TValue> getter,
        Action<Entity, TValue> setter)
    {
        TValue[] rollback = CaptureEntityValues(entities, getter);
        int applied = 0;
        try
        {
            for (; applied < entities.Length; applied++)
            {
                setter(entities[applied], target);
            }
        }
        catch
        {
            for (int i = applied - 1; i >= 0; i--)
            {
                setter(entities[i], rollback[i]);
            }
            throw;
        }
    }

    protected static void SetEntityValuesTransactional<TValue>(
        Entity[] entities,
        TValue[] targets,
        Func<Entity, TValue> getter,
        Action<Entity, TValue> setter)
    {
        if (targets.Length != entities.Length)
        {
            throw new InvalidOperationException(
                "Retained entity property state does not match the entity set.");
        }
        TValue[] rollback = CaptureEntityValues(entities, getter);
        int applied = 0;
        try
        {
            for (; applied < entities.Length; applied++)
            {
                setter(entities[applied], targets[applied]);
            }
        }
        catch
        {
            for (int i = applied - 1; i >= 0; i--)
            {
                setter(entities[i], rollback[i]);
            }
            throw;
        }
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
    private Entity[]? _entities;

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

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity[] entities;
        if (isRedo)
        {
            entities = _entities ??
                throw new InvalidOperationException("The translation command has not been applied.");
            ValidateModelSpaceEntities(document, entities);
        }
        else
        {
            entities = ResolveModelSpaceEntities(document, _handles);
            _entities = entities;
        }
        TranslateTransactional(entities, _translation, _inverseTranslation);
    }

    internal override void Revert(CadDocument document) =>
        TranslateTransactional(
            GetRetainedEntities(document),
            _inverseTranslation,
            _translation);

    private Entity[] GetRetainedEntities(CadDocument document)
    {
        Entity[] entities = _entities ??
            throw new InvalidOperationException("The translation command has not been applied.");
        ValidateModelSpaceEntities(document, entities);
        return entities;
    }

    private void TranslateTransactional(
        Entity[] entities,
        XYZ translation,
        XYZ rollbackTranslation)
    {
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

/// <summary>Rotates a stable set of entity handles around an origin axis.</summary>
public sealed class CadRotateEntitiesCommand : CadEditCommand
{
    private readonly ulong[] _handles;
    private readonly XYZ _axis;
    private readonly double _radians;
    private Entity[]? _entities;

    public ReadOnlyMemory<ulong> Handles => _handles;

    public CadPoint3D Axis { get; }

    public double Radians => _radians;

    public CadRotateEntitiesCommand(
        IEnumerable<ulong> handles,
        CadPoint3D axis,
        double radians,
        string description = "Rotate entities")
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (!double.IsFinite(axis.X) ||
            !double.IsFinite(axis.Y) ||
            !double.IsFinite(axis.Z))
        {
            throw new ArgumentException("A rotation axis must be finite.", nameof(axis));
        }
        if (!double.IsFinite(radians) || radians == 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(radians),
                "A rotation angle must be finite and non-zero.");
        }

        double largestComponent = Math.Max(
            Math.Abs(axis.X),
            Math.Max(Math.Abs(axis.Y), Math.Abs(axis.Z)));
        if (largestComponent == 0.0)
        {
            throw new ArgumentException("A rotation axis must be non-zero.", nameof(axis));
        }
        double scaledX = axis.X / largestComponent;
        double scaledY = axis.Y / largestComponent;
        double scaledZ = axis.Z / largestComponent;
        double scaledLength = Math.Sqrt(
            (scaledX * scaledX) +
            (scaledY * scaledY) +
            (scaledZ * scaledZ));

        _handles = handles.Distinct().ToArray();
        if (_handles.Length == 0 || _handles.Any(static handle => handle == 0))
        {
            throw new ArgumentException(
                "At least one non-zero entity handle is required.",
                nameof(handles));
        }

        Axis = axis;
        _axis = new XYZ(
            scaledX / scaledLength,
            scaledY / scaledLength,
            scaledZ / scaledLength);
        _radians = radians;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity[] entities;
        if (isRedo)
        {
            entities = GetRetainedEntities(document);
        }
        else
        {
            entities = ResolveModelSpaceEntities(document, _handles);
            _entities = entities;
        }
        RotateTransactional(entities, _radians);
    }

    internal override void Revert(CadDocument document) =>
        RotateTransactional(GetRetainedEntities(document), -_radians);

    private Entity[] GetRetainedEntities(CadDocument document)
    {
        Entity[] entities = _entities ??
            throw new InvalidOperationException("The rotation command has not been applied.");
        ValidateModelSpaceEntities(document, entities);
        return entities;
    }

    private void RotateTransactional(Entity[] entities, double radians)
    {
        int applied = 0;
        try
        {
            for (; applied < entities.Length; applied++)
            {
                entities[applied].ApplyRotation(_axis, radians);
            }
        }
        catch
        {
            for (int i = applied - 1; i >= 0; i--)
            {
                entities[i].ApplyRotation(_axis, -radians);
            }
            throw;
        }
    }
}

/// <summary>Uniformly scales a stable set of entity handles around an origin.</summary>
public sealed class CadScaleEntitiesCommand : CadEditCommand
{
    private readonly ulong[] _handles;
    private readonly XYZ _origin;
    private readonly XYZ _scale;
    private readonly XYZ _inverseScale;
    private Entity[]? _entities;

    public ReadOnlyMemory<ulong> Handles => _handles;

    public double Factor { get; }

    public CadPoint3D Origin { get; }

    public CadScaleEntitiesCommand(
        IEnumerable<ulong> handles,
        double factor,
        CadPoint3D origin = default,
        string description = "Scale entities")
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(handles);
        double inverseFactor = 1.0 / factor;
        if (!double.IsFinite(factor) ||
            factor <= 0.0 ||
            factor == 1.0 ||
            !double.IsFinite(inverseFactor))
        {
            throw new ArgumentOutOfRangeException(
                nameof(factor),
                "A scale factor must be positive, finite, non-unit, and have a finite inverse.");
        }
        if (!double.IsFinite(origin.X) ||
            !double.IsFinite(origin.Y) ||
            !double.IsFinite(origin.Z))
        {
            throw new ArgumentException("A scale origin must be finite.", nameof(origin));
        }

        _handles = handles.Distinct().ToArray();
        if (_handles.Length == 0 || _handles.Any(static handle => handle == 0))
        {
            throw new ArgumentException(
                "At least one non-zero entity handle is required.",
                nameof(handles));
        }

        Factor = factor;
        Origin = origin;
        _origin = new XYZ(origin.X, origin.Y, origin.Z);
        _scale = new XYZ(factor, factor, factor);
        _inverseScale = new XYZ(inverseFactor, inverseFactor, inverseFactor);
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity[] entities;
        if (isRedo)
        {
            entities = GetRetainedEntities(document);
        }
        else
        {
            entities = ResolveModelSpaceEntities(document, _handles);
            _entities = entities;
        }
        ScaleTransactional(entities, _scale, _inverseScale);
    }

    internal override void Revert(CadDocument document) =>
        ScaleTransactional(
            GetRetainedEntities(document),
            _inverseScale,
            _scale);

    private Entity[] GetRetainedEntities(CadDocument document)
    {
        Entity[] entities = _entities ??
            throw new InvalidOperationException("The scale command has not been applied.");
        ValidateModelSpaceEntities(document, entities);
        return entities;
    }

    private void ScaleTransactional(
        Entity[] entities,
        XYZ scale,
        XYZ rollbackScale)
    {
        int applied = 0;
        try
        {
            for (; applied < entities.Length; applied++)
            {
                entities[applied].ApplyScaling(scale, _origin);
            }
        }
        catch
        {
            for (int i = applied - 1; i >= 0; i--)
            {
                entities[i].ApplyScaling(rollbackScale, _origin);
            }
            throw;
        }
    }
}

/// <summary>Sets visibility for a stable set of model-space entity handles.</summary>
public sealed class CadSetEntityVisibilityCommand : CadEditCommand
{
    private readonly ulong[] _handles;
    private Entity[]? _entities;
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
        Entity[] entities;
        if (!isRedo)
        {
            entities = ResolveModelSpaceEntities(document, _handles);
            _entities = entities;
            _previousValues = CaptureEntityValues(
                entities,
                static entity => entity.IsInvisible);
        }
        else
        {
            entities = GetRetainedEntities(document);
        }

        SetEntityValuesTransactional(
            entities,
            IsInvisible,
            static entity => entity.IsInvisible,
            static (entity, value) => entity.IsInvisible = value);
    }

    internal override void Revert(CadDocument document)
    {
        Entity[] entities = GetRetainedEntities(document);
        bool[] values = _previousValues ??
            throw new InvalidOperationException("The visibility command has not been applied.");
        SetEntityValuesTransactional(
            entities,
            values,
            static entity => entity.IsInvisible,
            static (entity, value) => entity.IsInvisible = value);
    }

    private Entity[] GetRetainedEntities(CadDocument document)
    {
        Entity[] entities = _entities ??
            throw new InvalidOperationException("The visibility command has not been applied.");
        ValidateModelSpaceEntities(document, entities);
        return entities;
    }

}

/// <summary>Assigns one existing layer to a stable set of model-space entities.</summary>
public sealed class CadSetEntityLayerCommand : CadEditCommand
{
    private readonly ulong[] _handles;
    private Entity[]? _entities;
    private Layer[]? _previousLayers;
    private Layer? _targetLayer;

    public ReadOnlyMemory<ulong> Handles => _handles;

    public string LayerName { get; }

    public CadSetEntityLayerCommand(
        IEnumerable<ulong> handles,
        string layerName,
        string description = "Set entity layer")
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        _handles = handles.Distinct().ToArray();
        if (_handles.Length == 0 || _handles.Any(static handle => handle == 0))
        {
            throw new ArgumentException(
                "At least one non-zero entity handle is required.",
                nameof(handles));
        }
        LayerName = layerName;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity[] entities;
        Layer target;
        if (isRedo)
        {
            entities = GetRetainedEntities(document);
            target = _targetLayer ??
                throw new InvalidOperationException("The layer command has not been applied.");
            ValidateLayer(document, target);
        }
        else
        {
            entities = ResolveModelSpaceEntities(document, _handles);
            if (!document.Layers.TryGetValue(LayerName, out Layer? targetLayer))
            {
                throw new InvalidOperationException(
                    $"Document layer '{LayerName}' does not exist.");
            }
            target = targetLayer;
            _entities = entities;
            _previousLayers = CaptureEntityValues(
                entities,
                static entity => entity.Layer);
            _targetLayer = target;
        }

        SetEntityValuesTransactional(
            entities,
            target,
            static entity => entity.Layer,
            static (entity, value) => entity.Layer = value);
    }

    internal override void Revert(CadDocument document)
    {
        Entity[] entities = GetRetainedEntities(document);
        Layer[] previous = _previousLayers ??
            throw new InvalidOperationException("The layer command has not been applied.");
        foreach (Layer layer in previous)
        {
            ValidateLayer(document, layer);
        }
        SetEntityValuesTransactional(
            entities,
            previous,
            static entity => entity.Layer,
            static (entity, value) => entity.Layer = value);
    }

    private Entity[] GetRetainedEntities(CadDocument document)
    {
        Entity[] entities = _entities ??
            throw new InvalidOperationException("The layer command has not been applied.");
        ValidateModelSpaceEntities(document, entities);
        return entities;
    }

    private static void ValidateLayer(CadDocument document, Layer layer)
    {
        if (!document.Layers.TryGetValue(layer.Name, out Layer? registered) ||
            !ReferenceEquals(registered, layer))
        {
            throw new InvalidOperationException(
                $"Retained document layer '{layer.Name}' is no longer registered.");
        }
    }

}

/// <summary>Assigns one existing linetype to a stable set of model-space entities.</summary>
public sealed class CadSetEntityLineTypeCommand : CadEditCommand
{
    private readonly ulong[] _handles;
    private Entity[]? _entities;
    private LineType[]? _previousLineTypes;
    private LineType? _targetLineType;

    public ReadOnlyMemory<ulong> Handles => _handles;

    public string LineTypeName { get; }

    public CadSetEntityLineTypeCommand(
        IEnumerable<ulong> handles,
        string lineTypeName,
        string description = "Set entity linetype")
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentException.ThrowIfNullOrWhiteSpace(lineTypeName);
        _handles = handles.Distinct().ToArray();
        if (_handles.Length == 0 || _handles.Any(static handle => handle == 0))
        {
            throw new ArgumentException(
                "At least one non-zero entity handle is required.",
                nameof(handles));
        }
        LineTypeName = lineTypeName;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity[] entities;
        LineType target;
        if (isRedo)
        {
            entities = GetRetainedEntities(document);
            target = _targetLineType ??
                throw new InvalidOperationException("The linetype command has not been applied.");
            ValidateLineType(document, target);
        }
        else
        {
            entities = ResolveModelSpaceEntities(document, _handles);
            if (!document.LineTypes.TryGetValue(LineTypeName, out LineType? targetLineType))
            {
                throw new InvalidOperationException(
                    $"Document linetype '{LineTypeName}' does not exist.");
            }
            target = targetLineType;
            _entities = entities;
            _previousLineTypes = CaptureEntityValues(
                entities,
                static entity => entity.LineType);
            _targetLineType = target;
        }

        SetEntityValuesTransactional(
            entities,
            target,
            static entity => entity.LineType,
            static (entity, value) => entity.LineType = value);
    }

    internal override void Revert(CadDocument document)
    {
        Entity[] entities = GetRetainedEntities(document);
        LineType[] previous = _previousLineTypes ??
            throw new InvalidOperationException("The linetype command has not been applied.");
        foreach (LineType lineType in previous)
        {
            ValidateLineType(document, lineType);
        }
        SetEntityValuesTransactional(
            entities,
            previous,
            static entity => entity.LineType,
            static (entity, value) => entity.LineType = value);
    }

    private Entity[] GetRetainedEntities(CadDocument document)
    {
        Entity[] entities = _entities ??
            throw new InvalidOperationException("The linetype command has not been applied.");
        ValidateModelSpaceEntities(document, entities);
        return entities;
    }

    private static void ValidateLineType(CadDocument document, LineType lineType)
    {
        if (!document.LineTypes.TryGetValue(lineType.Name, out LineType? registered) ||
            !ReferenceEquals(registered, lineType))
        {
            throw new InvalidOperationException(
                $"Retained document linetype '{lineType.Name}' is no longer registered.");
        }
    }
}

/// <summary>Assigns an explicit, ByLayer, or ByBlock lineweight to model-space entities.</summary>
public sealed class CadSetEntityLineWeightCommand : CadEditCommand
{
    private readonly ulong[] _handles;
    private Entity[]? _entities;
    private LineWeightType[]? _previousValues;

    public ReadOnlyMemory<ulong> Handles => _handles;

    public LineWeightType LineWeight { get; }

    public CadSetEntityLineWeightCommand(
        IEnumerable<ulong> handles,
        LineWeightType lineWeight,
        string description = "Set entity lineweight")
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (!Enum.IsDefined(lineWeight))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lineWeight),
                "The entity lineweight must be a defined CAD lineweight value.");
        }
        _handles = handles.Distinct().ToArray();
        if (_handles.Length == 0 || _handles.Any(static handle => handle == 0))
        {
            throw new ArgumentException(
                "At least one non-zero entity handle is required.",
                nameof(handles));
        }
        LineWeight = lineWeight;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity[] entities;
        if (isRedo)
        {
            entities = GetRetainedEntities(document);
        }
        else
        {
            entities = ResolveModelSpaceEntities(document, _handles);
            _entities = entities;
            _previousValues = CaptureEntityValues(
                entities,
                static entity => entity.LineWeight);
        }
        SetEntityValuesTransactional(
            entities,
            LineWeight,
            static entity => entity.LineWeight,
            static (entity, value) => entity.LineWeight = value);
    }

    internal override void Revert(CadDocument document)
    {
        Entity[] entities = GetRetainedEntities(document);
        LineWeightType[] previous = _previousValues ??
            throw new InvalidOperationException("The lineweight command has not been applied.");
        SetEntityValuesTransactional(
            entities,
            previous,
            static entity => entity.LineWeight,
            static (entity, value) => entity.LineWeight = value);
    }

    private Entity[] GetRetainedEntities(CadDocument document)
    {
        Entity[] entities = _entities ??
            throw new InvalidOperationException("The lineweight command has not been applied.");
        ValidateModelSpaceEntities(document, entities);
        return entities;
    }

}

/// <summary>Assigns indexed, true, ByLayer, or ByBlock color to model-space entities.</summary>
public sealed class CadSetEntityColorCommand : CadEditCommand
{
    private readonly ulong[] _handles;
    private Entity[]? _entities;
    private ACadSharp.Color[]? _previousValues;

    public ReadOnlyMemory<ulong> Handles => _handles;

    public ACadSharp.Color Color { get; }

    public CadSetEntityColorCommand(
        IEnumerable<ulong> handles,
        ACadSharp.Color color,
        string description = "Set entity color")
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (!color.IsTrueColor && color.Index == ACadSharp.Color.ByEntity.Index)
        {
            throw new ArgumentOutOfRangeException(
                nameof(color),
                "The ByEntity color sentinel is not valid for CAD entities.");
        }
        _handles = handles.Distinct().ToArray();
        if (_handles.Length == 0 || _handles.Any(static handle => handle == 0))
        {
            throw new ArgumentException(
                "At least one non-zero entity handle is required.",
                nameof(handles));
        }
        Color = color;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity[] entities;
        if (isRedo)
        {
            entities = GetRetainedEntities(document);
        }
        else
        {
            entities = ResolveModelSpaceEntities(document, _handles);
            _entities = entities;
            _previousValues = CaptureEntityValues(
                entities,
                static entity => entity.Color);
        }
        SetEntityValuesTransactional(
            entities,
            Color,
            static entity => entity.Color,
            static (entity, value) => entity.Color = value);
    }

    internal override void Revert(CadDocument document)
    {
        Entity[] entities = GetRetainedEntities(document);
        ACadSharp.Color[] previous = _previousValues ??
            throw new InvalidOperationException("The color command has not been applied.");
        SetEntityValuesTransactional(
            entities,
            previous,
            static entity => entity.Color,
            static (entity, value) => entity.Color = value);
    }

    private Entity[] GetRetainedEntities(CadDocument document)
    {
        Entity[] entities = _entities ??
            throw new InvalidOperationException("The color command has not been applied.");
        ValidateModelSpaceEntities(document, entities);
        return entities;
    }

}

/// <summary>Assigns explicit, ByLayer, or ByBlock transparency to model-space entities.</summary>
public sealed class CadSetEntityTransparencyCommand : CadEditCommand
{
    private readonly ulong[] _handles;
    private Entity[]? _entities;
    private Transparency[]? _previousValues;

    public ReadOnlyMemory<ulong> Handles => _handles;

    public Transparency Transparency { get; }

    public CadSetEntityTransparencyCommand(
        IEnumerable<ulong> handles,
        Transparency transparency,
        string description = "Set entity transparency")
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
        Transparency = transparency;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity[] entities;
        if (isRedo)
        {
            entities = GetRetainedEntities(document);
        }
        else
        {
            entities = ResolveModelSpaceEntities(document, _handles);
            _entities = entities;
            _previousValues = CaptureEntityValues(
                entities,
                static entity => entity.Transparency);
        }
        SetEntityValuesTransactional(
            entities,
            Transparency,
            static entity => entity.Transparency,
            static (entity, value) => entity.Transparency = value);
    }

    internal override void Revert(CadDocument document)
    {
        Entity[] entities = GetRetainedEntities(document);
        Transparency[] previous = _previousValues ??
            throw new InvalidOperationException(
                "The transparency command has not been applied.");
        SetEntityValuesTransactional(
            entities,
            previous,
            static entity => entity.Transparency,
            static (entity, value) => entity.Transparency = value);
    }

    private Entity[] GetRetainedEntities(CadDocument document)
    {
        Entity[] entities = _entities ??
            throw new InvalidOperationException(
                "The transparency command has not been applied.");
        ValidateModelSpaceEntities(document, entities);
        return entities;
    }
}

/// <summary>Adds one detached entity to model space with reversible ownership.</summary>
public sealed class CadAddModelSpaceEntityCommand : CadEditCommand
{
    public Entity Entity { get; }

    public ulong CurrentHandle => Entity.Handle;

    public CadAddModelSpaceEntityCommand(
        Entity entity,
        string description = "Add entity")
        : base(description)
    {
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        if (entity.Owner is not null || entity.Handle != 0)
        {
            throw new ArgumentException(
                "An added entity must be detached and have no assigned handle.",
                nameof(entity));
        }
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        if (Entity.Owner is not null || Entity.Handle != 0)
        {
            throw new InvalidOperationException(
                "The entity is not detached and cannot be added to model space.");
        }
        document.Entities.Add(Entity);
    }

    internal override void Revert(CadDocument document)
    {
        ValidateModelSpaceEntity(document, Entity);
        if (!document.Entities.Remove(Entity))
        {
            throw new InvalidOperationException(
                "The added entity could not be removed from model space.");
        }
    }
}

/// <summary>Removes one model-space entity while retaining it for semantic undo.</summary>
public sealed class CadRemoveModelSpaceEntityCommand : CadEditCommand
{
    private readonly ulong _initialHandle;
    private Entity? _entity;

    public ulong InitialHandle => _initialHandle;

    public ulong CurrentHandle => _entity?.Handle ?? _initialHandle;

    public CadRemoveModelSpaceEntityCommand(
        ulong handle,
        string description = "Remove entity")
        : base(description)
    {
        if (handle == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(handle),
                "A non-zero model-space entity handle is required.");
        }
        _initialHandle = handle;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity entity;
        if (isRedo)
        {
            entity = _entity ??
                throw new InvalidOperationException("The remove command has not been applied.");
            ValidateModelSpaceEntity(document, entity);
        }
        else
        {
            entity = ResolveModelSpaceEntity(document, _initialHandle);
            _entity = entity;
        }

        if (!document.Entities.Remove(entity))
        {
            throw new InvalidOperationException(
                "The entity could not be removed from model space.");
        }
    }

    internal override void Revert(CadDocument document)
    {
        Entity entity = _entity ??
            throw new InvalidOperationException("The remove command has not been applied.");
        if (entity.Owner is not null || entity.Handle != 0)
        {
            throw new InvalidOperationException(
                "The removed entity is not detached and cannot be restored.");
        }
        document.Entities.Add(entity);
    }
}
