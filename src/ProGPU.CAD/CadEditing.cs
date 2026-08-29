using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Extensions;
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

/// <summary>Shared persisted-name rules for editable CAD layer records.</summary>
public static class CadLayerNameRules
{
    public static bool IsValid(string? layerName, ACadVersion version)
    {
        if (string.IsNullOrWhiteSpace(layerName) ||
            layerName.IndexOfAny(INamedCadObjectExtensions.InvalidCharacters) >= 0)
        {
            return false;
        }
        return new Layer(layerName).HasValidDxfName(version);
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
        Entity entity = ResolveModelSpaceEntityForReference(document, handle);
        if (HasLayerFlag(entity.Layer, LayerFlags.Locked))
        {
            throw new InvalidOperationException(
                $"Model-space entity handle {handle:X} is on locked layer " +
                $"'{entity.Layer.Name}' and cannot be edited.");
        }
        return entity;
    }

    protected static Entity[] ResolveModelSpaceEntitiesForReference(
        CadDocument document,
        ReadOnlySpan<ulong> handles)
    {
        var entities = new Entity[handles.Length];
        for (int i = 0; i < handles.Length; i++)
        {
            entities[i] = ResolveModelSpaceEntityForReference(
                document,
                handles[i]);
        }
        return entities;
    }

    protected static Entity ResolveModelSpaceEntityForReference(
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

    /// <summary>
    /// Applies a WCS translation while preserving OCS SOLID coordinates and a
    /// DIMENSION's persisted-picture displacement contract.
    /// </summary>
    /// <remarks>
    /// SOLID corners are OCS values, so their WCS displacement is projected into
    /// the entity basis. ACadSharp transforms semantic dimension definition points
    /// but leaves group 12 unchanged. AutoCAD stores that group in OCS and uses it
    /// as the relative WCS displacement of the already-authored anonymous picture.
    /// Work and storage are O(1), and no dimension layout regeneration is performed.
    /// </remarks>
    protected static void ApplyEntityTranslation(Entity entity, XYZ translation)
    {
        if (entity is Solid solid)
        {
            CadCoordinateSystem solidBasis = CreateEntityBasis(solid.Normal);
            XYZ solidObjectTranslation = WorldToObjectVector(
                solidBasis,
                new CadPoint3D(translation.X, translation.Y, translation.Z));
            solid.FirstCorner += solidObjectTranslation;
            solid.SecondCorner += solidObjectTranslation;
            solid.ThirdCorner += solidObjectTranslation;
            solid.FourthCorner += solidObjectTranslation;
            return;
        }
        if (entity is not Dimension dimension)
        {
            entity.ApplyTranslation(translation);
            return;
        }

        CadCoordinateSystem basis = CadCoordinateSystem.FromNormal(new CadPoint3D(
            dimension.Normal.X,
            dimension.Normal.Y,
            dimension.Normal.Z));
        var worldTranslation = new CadPoint3D(
            translation.X,
            translation.Y,
            translation.Z);
        var objectTranslation = new XYZ(
            CadPoint3D.Dot(worldTranslation, basis.XAxis),
            CadPoint3D.Dot(worldTranslation, basis.YAxis),
            CadPoint3D.Dot(worldTranslation, basis.ZAxis));
        XYZ previousInsertionPoint = dimension.InsertionPoint;
        dimension.ApplyTranslation(translation);
        dimension.InsertionPoint = previousInsertionPoint + objectTranslation;
    }

    /// <summary>
    /// Rotates OCS SOLID geometry and semantic dimension data plus its persisted
    /// picture without invoking dimension-layout generation.
    /// </summary>
    protected static void ApplyEntityRotation(Entity entity, XYZ axis, double radians) =>
        ApplyEntityRotation(
            entity,
            axis,
            radians,
            new HashSet<BlockRecord>(ReferenceEqualityComparer.Instance),
            0);

    private static void ApplyEntityRotation(
        Entity entity,
        XYZ axis,
        double radians,
        HashSet<BlockRecord> activePictures,
        int depth)
    {
        if (entity is Solid solid)
        {
            ApplySolidRotation(solid, axis, radians);
            return;
        }
        if (entity is Point point)
        {
            ApplyPointRotation(point, axis, radians);
            return;
        }
        if (entity is not Dimension dimension)
        {
            entity.ApplyRotation(axis, radians);
            return;
        }

        XYZ previousNormal = dimension.Normal;
        XYZ previousInsertion = dimension.InsertionPoint;
        CadPoint3D worldDisplacement = ObjectToWorldVector(
            previousNormal,
            previousInsertion);
        BlockRecord? picture = dimension.Block;
        if (picture is not null &&
            (depth >= CadSnapshotOptions.DefaultMaxBlockNestingDepth ||
             !activePictures.Add(picture)))
        {
            throw new InvalidOperationException(
                "Dimension-picture editing encountered cyclic or excessive nesting.");
        }

        int transformedChildren = 0;
        bool dimensionTransformed = false;
        try
        {
            if (picture is not null)
            {
                for (; transformedChildren < picture.Entities.Count; transformedChildren++)
                {
                    ApplyEntityRotation(
                        picture.Entities[transformedChildren],
                        axis,
                        radians,
                        activePictures,
                        depth + 1);
                }
            }

            dimension.ApplyRotation(axis, radians);
            dimensionTransformed = true;
            Transform rotation = Transform.CreateRotation(axis, radians);
            XYZ rotated = rotation.ApplyRotation(new XYZ(
                worldDisplacement.X,
                worldDisplacement.Y,
                worldDisplacement.Z));
            dimension.InsertionPoint = WorldToObjectVector(
                dimension.Normal,
                new CadPoint3D(rotated.X, rotated.Y, rotated.Z));
        }
        catch
        {
            if (dimensionTransformed)
            {
                dimension.ApplyRotation(axis, -radians);
                dimension.InsertionPoint = previousInsertion;
            }
            if (picture is not null)
            {
                for (int i = transformedChildren - 1; i >= 0; i--)
                {
                    ApplyEntityRotation(
                        picture.Entities[i],
                        axis,
                        -radians,
                        activePictures,
                        depth + 1);
                }
            }
            throw;
        }
        finally
        {
            if (picture is not null)
            {
                activePictures.Remove(picture);
            }
        }
    }

    /// <summary>
    /// Uniformly scales OCS SOLID geometry/thickness and semantic dimension data
    /// plus its persisted picture without invoking dimension-layout generation.
    /// </summary>
    protected static void ApplyEntityScaling(
        Entity entity,
        XYZ scale,
        XYZ origin,
        XYZ rollbackScale) =>
        ApplyEntityScaling(
            entity,
            scale,
            origin,
            rollbackScale,
            new HashSet<BlockRecord>(ReferenceEqualityComparer.Instance),
            0);

    private static void ApplyEntityScaling(
        Entity entity,
        XYZ scale,
        XYZ origin,
        XYZ rollbackScale,
        HashSet<BlockRecord> activePictures,
        int depth)
    {
        if (entity is Solid solid)
        {
            ApplySolidScaling(solid, scale, origin);
            return;
        }
        if (entity is not Dimension dimension)
        {
            entity.ApplyScaling(scale, origin);
            return;
        }

        XYZ previousNormal = dimension.Normal;
        XYZ previousInsertion = dimension.InsertionPoint;
        CadPoint3D worldDisplacement = ObjectToWorldVector(
            previousNormal,
            previousInsertion);
        BlockRecord? picture = dimension.Block;
        if (picture is not null &&
            (depth >= CadSnapshotOptions.DefaultMaxBlockNestingDepth ||
             !activePictures.Add(picture)))
        {
            throw new InvalidOperationException(
                "Dimension-picture editing encountered cyclic or excessive nesting.");
        }

        int transformedChildren = 0;
        bool dimensionTransformed = false;
        try
        {
            if (picture is not null)
            {
                for (; transformedChildren < picture.Entities.Count; transformedChildren++)
                {
                    ApplyEntityScaling(
                        picture.Entities[transformedChildren],
                        scale,
                        origin,
                        rollbackScale,
                        activePictures,
                        depth + 1);
                }
            }

            dimension.ApplyScaling(scale, origin);
            dimensionTransformed = true;
            var scaledDisplacement = new CadPoint3D(
                worldDisplacement.X * scale.X,
                worldDisplacement.Y * scale.Y,
                worldDisplacement.Z * scale.Z);
            dimension.InsertionPoint = WorldToObjectVector(
                dimension.Normal,
                scaledDisplacement);
        }
        catch
        {
            if (dimensionTransformed)
            {
                dimension.ApplyScaling(rollbackScale, origin);
                dimension.InsertionPoint = previousInsertion;
            }
            if (picture is not null)
            {
                for (int i = transformedChildren - 1; i >= 0; i--)
                {
                    ApplyEntityScaling(
                        picture.Entities[i],
                        rollbackScale,
                        origin,
                        scale,
                        activePictures,
                        depth + 1);
                }
            }
            throw;
        }
        finally
        {
            if (picture is not null)
            {
                activePictures.Remove(picture);
            }
        }
    }

    private static CadPoint3D ObjectToWorldVector(XYZ normal, XYZ value)
    {
        CadCoordinateSystem basis = CreateEntityBasis(normal);
        return basis.Transform(new CadPoint3D(value.X, value.Y, value.Z));
    }

    private static XYZ WorldToObjectVector(XYZ normal, CadPoint3D value)
    {
        CadCoordinateSystem basis = CreateEntityBasis(normal);
        return WorldToObjectVector(basis, value);
    }

    private static CadCoordinateSystem CreateEntityBasis(XYZ normal) =>
        CadCoordinateSystem.FromNormal(new CadPoint3D(
            normal.X,
            normal.Y,
            normal.Z));

    private static XYZ WorldToObjectVector(
        CadCoordinateSystem basis,
        CadPoint3D value)
    {
        return new XYZ(
            CadPoint3D.Dot(value, basis.XAxis),
            CadPoint3D.Dot(value, basis.YAxis),
            CadPoint3D.Dot(value, basis.ZAxis));
    }

    private static void ApplySolidRotation(Solid solid, XYZ axis, double radians)
    {
        CadCoordinateSystem sourceBasis = CreateEntityBasis(solid.Normal);
        Transform rotation = Transform.CreateRotation(axis, radians);
        XYZ rotatedNormal = rotation.ApplyRotation(new XYZ(
            sourceBasis.ZAxis.X,
            sourceBasis.ZAxis.Y,
            sourceBasis.ZAxis.Z)).Normalize();
        CadCoordinateSystem destinationBasis = CreateEntityBasis(rotatedNormal);
        solid.FirstCorner = RotateSolidCorner(
            solid.FirstCorner,
            sourceBasis,
            destinationBasis,
            rotation);
        solid.SecondCorner = RotateSolidCorner(
            solid.SecondCorner,
            sourceBasis,
            destinationBasis,
            rotation);
        solid.ThirdCorner = RotateSolidCorner(
            solid.ThirdCorner,
            sourceBasis,
            destinationBasis,
            rotation);
        solid.FourthCorner = RotateSolidCorner(
            solid.FourthCorner,
            sourceBasis,
            destinationBasis,
            rotation);
        solid.Normal = rotatedNormal;
    }

    private static void ApplyPointRotation(Point point, XYZ axis, double radians)
    {
        CadCoordinateSystem sourceBasis = CreateEntityBasis(point.Normal);
        CadPoint3D sourceXAxis =
            (sourceBasis.XAxis * Math.Cos(point.Rotation)) +
            (sourceBasis.YAxis * Math.Sin(point.Rotation));
        Transform rotation = Transform.CreateRotation(axis, radians);
        XYZ rotatedXAxisValue = rotation.ApplyRotation(new XYZ(
            sourceXAxis.X,
            sourceXAxis.Y,
            sourceXAxis.Z));
        point.ApplyRotation(axis, radians);
        CadCoordinateSystem destinationBasis = CreateEntityBasis(point.Normal);
        var rotatedXAxis = new CadPoint3D(
            rotatedXAxisValue.X,
            rotatedXAxisValue.Y,
            rotatedXAxisValue.Z).Normalize();
        point.Rotation = Math.Atan2(
            CadPoint3D.Dot(rotatedXAxis, destinationBasis.YAxis),
            CadPoint3D.Dot(rotatedXAxis, destinationBasis.XAxis));
    }

    private static XYZ RotateSolidCorner(
        XYZ corner,
        CadCoordinateSystem sourceBasis,
        CadCoordinateSystem destinationBasis,
        Transform rotation)
    {
        CadPoint3D world = sourceBasis.Transform(new CadPoint3D(
            corner.X,
            corner.Y,
            corner.Z));
        XYZ rotated = rotation.ApplyTransform(new XYZ(world.X, world.Y, world.Z));
        return WorldToObjectVector(
            destinationBasis,
            new CadPoint3D(rotated.X, rotated.Y, rotated.Z));
    }

    private static void ApplySolidScaling(Solid solid, XYZ scale, XYZ origin)
    {
        if (scale.X != scale.Y || scale.X != scale.Z)
        {
            throw new InvalidOperationException(
                "SOLID editing requires a uniform scale to preserve one thickness value.");
        }

        CadCoordinateSystem basis = CreateEntityBasis(solid.Normal);
        solid.FirstCorner = ScaleSolidCorner(solid.FirstCorner, basis, scale.X, origin);
        solid.SecondCorner = ScaleSolidCorner(solid.SecondCorner, basis, scale.X, origin);
        solid.ThirdCorner = ScaleSolidCorner(solid.ThirdCorner, basis, scale.X, origin);
        solid.FourthCorner = ScaleSolidCorner(solid.FourthCorner, basis, scale.X, origin);
        solid.Thickness *= scale.X;
    }

    private static XYZ ScaleSolidCorner(
        XYZ corner,
        CadCoordinateSystem basis,
        double factor,
        XYZ origin)
    {
        CadPoint3D world = basis.Transform(new CadPoint3D(
            corner.X,
            corner.Y,
            corner.Z));
        var scaled = new CadPoint3D(
            origin.X + ((world.X - origin.X) * factor),
            origin.Y + ((world.Y - origin.Y) * factor),
            origin.Z + ((world.Z - origin.Z) * factor));
        return WorldToObjectVector(basis, scaled);
    }

    protected static Layer[] ResolveLayers(
        CadDocument document,
        ReadOnlySpan<string> layerNames)
    {
        var layers = new Layer[layerNames.Length];
        for (int i = 0; i < layerNames.Length; i++)
        {
            if (!document.Layers.TryGetValue(layerNames[i], out Layer? layer))
            {
                throw new InvalidOperationException(
                    $"Document layer '{layerNames[i]}' does not exist.");
            }
            layers[i] = layer;
        }
        return layers;
    }

    protected static void ValidateLayerNameAvailable(
        CadDocument document,
        string layerName,
        Layer? retainedLayer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        if (!CadLayerNameRules.IsValid(layerName, document.Header.Version))
        {
            throw new ArgumentException(
                $"Layer name '{layerName}' is not valid for " +
                $"{document.Header.Version} DXF/DWG persistence.",
                nameof(layerName));
        }
        if (document.Layers.TryGetValue(layerName, out Layer? existing) &&
            !ReferenceEquals(existing, retainedLayer))
        {
            throw new InvalidOperationException(
                $"Document layer '{layerName}' already exists.");
        }
    }

    protected static void ValidateLayerCanBeRenamed(Layer layer)
    {
        if (layer.Name.Equals(
                Layer.DefaultName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Default layer 0 cannot be renamed.");
        }
        if ((layer.Flags & LayerFlags.XrefDependent) != 0)
        {
            throw new InvalidOperationException(
                $"Xref-dependent layer '{layer.Name}' cannot be renamed.");
        }
    }

    protected static void ValidateLayerCanBeRemoved(
        CadDocument document,
        Layer layer)
    {
        if (layer.Name.Equals(
                Layer.DefaultName,
                StringComparison.OrdinalIgnoreCase) ||
            layer.Name.Equals(
                Layer.DefpointsName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Protected layer '{layer.Name}' cannot be removed.");
        }
        if ((layer.Flags & LayerFlags.XrefDependent) != 0)
        {
            throw new InvalidOperationException(
                $"Xref-dependent layer '{layer.Name}' cannot be removed.");
        }
        if (ReferenceEquals(document.Header.CurrentLayer, layer))
        {
            throw new InvalidOperationException(
                $"Current layer '{layer.Name}' cannot be removed.");
        }
        foreach (Entity entity in document.GetCadObjects<Entity>())
        {
            if (ReferenceEquals(entity.Layer, layer))
            {
                throw new InvalidOperationException(
                    $"Layer '{layer.Name}' is referenced by entity handle " +
                    $"{entity.Handle:X} and cannot be removed.");
            }
        }
        foreach (Viewport viewport in document.GetCadObjects<Viewport>())
        {
            if (viewport.FrozenLayers.Any(candidate =>
                    ReferenceEquals(candidate, layer)))
            {
                throw new InvalidOperationException(
                    $"Layer '{layer.Name}' is frozen in viewport handle " +
                    $"{viewport.Handle:X} and cannot be removed.");
            }
        }
    }

    protected static string[] NormalizeLayerNames(
        IEnumerable<string> layerNames,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(layerNames, parameterName);
        string[] names = layerNames.ToArray();
        if (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "At least one non-empty layer name is required.",
                parameterName);
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    protected static void ValidateLayers(
        CadDocument document,
        ReadOnlySpan<Layer> layers)
    {
        foreach (Layer layer in layers)
        {
            ValidateLayer(document, layer);
        }
    }

    protected static void ValidateLayer(CadDocument document, Layer layer)
    {
        if (!document.Layers.TryGetValue(layer.Name, out Layer? registered) ||
            !ReferenceEquals(registered, layer))
        {
            throw new InvalidOperationException(
                $"Retained document layer '{layer.Name}' is no longer registered.");
        }
    }

    protected static bool HasLayerFlag(Layer layer, LayerFlags flag) =>
        (layer.Flags & flag) != 0;

    protected static void SetLayerFlag(
        Layer layer,
        LayerFlags flag,
        bool value) =>
        layer.Flags = value
            ? layer.Flags | flag
            : layer.Flags & ~flag;

    protected static LineType ResolveLineType(
        CadDocument document,
        string lineTypeName)
    {
        if (!document.LineTypes.TryGetValue(lineTypeName, out LineType? lineType))
        {
            throw new InvalidOperationException(
                $"Document linetype '{lineTypeName}' does not exist.");
        }
        return lineType;
    }

    protected static void ValidateLineTypes(
        CadDocument document,
        ReadOnlySpan<LineType> lineTypes)
    {
        foreach (LineType lineType in lineTypes)
        {
            ValidateLineType(document, lineType);
        }
    }

    protected static void ValidateLineType(
        CadDocument document,
        LineType lineType)
    {
        if (!document.LineTypes.TryGetValue(lineType.Name, out LineType? registered) ||
            !ReferenceEquals(registered, lineType))
        {
            throw new InvalidOperationException(
                $"Retained document linetype '{lineType.Name}' is no longer registered.");
        }
    }

    protected static TValue[] CaptureEntityValues<TValue>(
        Entity[] entities,
        Func<Entity, TValue> getter) => CaptureValues(entities, getter);

    protected static void SetEntityValuesTransactional<TValue>(
        Entity[] entities,
        TValue target,
        Func<Entity, TValue> getter,
        Action<Entity, TValue> setter) =>
        SetValuesTransactional(entities, target, getter, setter);

    protected static void SetEntityValuesTransactional<TValue>(
        Entity[] entities,
        TValue[] targets,
        Func<Entity, TValue> getter,
        Action<Entity, TValue> setter) =>
        SetValuesTransactional(entities, targets, getter, setter);

    protected static TValue[] CaptureValues<TItem, TValue>(
        TItem[] items,
        Func<TItem, TValue> getter) => items.Select(getter).ToArray();

    protected static void SetValuesTransactional<TItem, TValue>(
        TItem[] items,
        TValue target,
        Func<TItem, TValue> getter,
        Action<TItem, TValue> setter)
    {
        TValue[] rollback = CaptureValues(items, getter);
        int applied = 0;
        try
        {
            for (; applied < items.Length; applied++)
            {
                setter(items[applied], target);
            }
        }
        catch
        {
            for (int i = applied - 1; i >= 0; i--)
            {
                setter(items[i], rollback[i]);
            }
            throw;
        }
    }

    protected static void SetValuesTransactional<TItem, TValue>(
        TItem[] items,
        TValue[] targets,
        Func<TItem, TValue> getter,
        Action<TItem, TValue> setter)
    {
        if (targets.Length != items.Length)
        {
            throw new InvalidOperationException(
                "Retained property state does not match the target set.");
        }
        TValue[] rollback = CaptureValues(items, getter);
        int applied = 0;
        try
        {
            for (; applied < items.Length; applied++)
            {
                setter(items[applied], targets[applied]);
            }
        }
        catch
        {
            for (int i = applied - 1; i >= 0; i--)
            {
                setter(items[i], rollback[i]);
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
                ApplyEntityTranslation(entities[applied], translation);
            }
        }
        catch
        {
            for (int i = applied - 1; i >= 0; i--)
            {
                ApplyEntityTranslation(entities[i], rollbackTranslation);
            }

            throw;
        }
    }

}

/// <summary>Rotates a stable set of entity handles around an axis through a pivot.</summary>
public sealed class CadRotateEntitiesCommand : CadEditCommand
{
    private readonly ulong[] _handles;
    private readonly XYZ _axis;
    private readonly XYZ _pivot;
    private readonly XYZ _inversePivot;
    private readonly bool _hasPivot;
    private readonly double _radians;
    private Entity[]? _entities;

    public ReadOnlyMemory<ulong> Handles => _handles;

    public CadPoint3D Axis { get; }

    public CadPoint3D Pivot { get; }

    public double Radians => _radians;

    public CadRotateEntitiesCommand(
        IEnumerable<ulong> handles,
        CadPoint3D axis,
        double radians,
        string description = "Rotate entities")
        : this(handles, axis, radians, CadPoint3D.Zero, description)
    {
    }

    public CadRotateEntitiesCommand(
        IEnumerable<ulong> handles,
        CadPoint3D axis,
        double radians,
        CadPoint3D pivot,
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
        if (!double.IsFinite(pivot.X) ||
            !double.IsFinite(pivot.Y) ||
            !double.IsFinite(pivot.Z))
        {
            throw new ArgumentException("A rotation pivot must be finite.", nameof(pivot));
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
        Pivot = pivot;
        _pivot = new XYZ(pivot.X, pivot.Y, pivot.Z);
        _inversePivot = new XYZ(-pivot.X, -pivot.Y, -pivot.Z);
        _hasPivot = pivot != CadPoint3D.Zero;
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
                RotateEntity(entities[applied], radians);
            }
        }
        catch
        {
            for (int i = applied - 1; i >= 0; i--)
            {
                RotateEntity(entities[i], -radians);
            }
            throw;
        }
    }

    private void RotateEntity(Entity entity, double radians)
    {
        if (!_hasPivot)
        {
            ApplyEntityRotation(entity, _axis, radians);
            return;
        }

        bool translatedToPivot = false;
        bool rotated = false;
        try
        {
            ApplyEntityTranslation(entity, _inversePivot);
            translatedToPivot = true;
            ApplyEntityRotation(entity, _axis, radians);
            rotated = true;
            ApplyEntityTranslation(entity, _pivot);
            translatedToPivot = false;
        }
        catch
        {
            if (rotated)
            {
                ApplyEntityRotation(entity, _axis, -radians);
            }
            if (translatedToPivot)
            {
                ApplyEntityTranslation(entity, _pivot);
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
                ApplyEntityScaling(
                    entities[applied],
                    scale,
                    _origin,
                    rollbackScale);
            }
        }
        catch
        {
            for (int i = applied - 1; i >= 0; i--)
            {
                ApplyEntityScaling(
                    entities[i],
                    rollbackScale,
                    _origin,
                    scale);
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

/// <summary>Sets signed extrusion thickness for a stable set of SOLID entities.</summary>
public sealed class CadSetSolidThicknessCommand : CadEditCommand
{
    public const int MaximumEntityCount = 65_536;

    private readonly ulong[] _handles;
    private Solid[]? _solids;
    private double[]? _previousValues;

    public ReadOnlyMemory<ulong> Handles => _handles;

    public double Thickness { get; }

    public CadSetSolidThicknessCommand(
        IEnumerable<ulong> handles,
        double thickness,
        string description = "Set SOLID thickness")
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (!double.IsFinite(thickness))
        {
            throw new ArgumentOutOfRangeException(
                nameof(thickness),
                "SOLID thickness must be finite.");
        }

        _handles = handles
            .Distinct()
            .Take(MaximumEntityCount + 1)
            .ToArray();
        if (_handles.Length == 0 || _handles.Any(static handle => handle == 0))
        {
            throw new ArgumentException(
                "At least one non-zero entity handle is required.",
                nameof(handles));
        }
        if (_handles.Length > MaximumEntityCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(handles),
                $"At most {MaximumEntityCount:N0} distinct SOLID handles are supported.");
        }

        Thickness = thickness;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Solid[] solids;
        if (isRedo)
        {
            solids = GetRetainedSolids(document);
        }
        else
        {
            Entity[] entities = ResolveModelSpaceEntities(document, _handles);
            solids = new Solid[entities.Length];
            for (int i = 0; i < entities.Length; i++)
            {
                solids[i] = entities[i] as Solid ??
                    throw new InvalidOperationException(
                        $"Model-space entity handle {_handles[i]:X} is not a SOLID.");
            }
            _solids = solids;
            _previousValues = CaptureValues(
                solids,
                static solid => solid.Thickness);
        }

        SetValuesTransactional(
            solids,
            Thickness,
            static solid => solid.Thickness,
            static (solid, value) => solid.Thickness = value);
    }

    internal override void Revert(CadDocument document)
    {
        Solid[] solids = GetRetainedSolids(document);
        double[] previous = _previousValues ??
            throw new InvalidOperationException(
                "The SOLID-thickness command has not been applied.");
        SetValuesTransactional(
            solids,
            previous,
            static solid => solid.Thickness,
            static (solid, value) => solid.Thickness = value);
    }

    private Solid[] GetRetainedSolids(CadDocument document)
    {
        Solid[] solids = _solids ??
            throw new InvalidOperationException(
                "The SOLID-thickness command has not been applied.");
        foreach (Solid solid in solids)
        {
            ValidateModelSpaceEntity(document, solid);
        }
        return solids;
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

}

/// <summary>Sets model visibility for a stable set of existing layers.</summary>
public sealed class CadSetLayerVisibilityCommand : CadEditCommand
{
    private readonly string[] _layerNames;
    private Layer[]? _layers;
    private bool[]? _previousValues;

    public ReadOnlyMemory<string> LayerNames => _layerNames;

    public bool IsOn { get; }

    public CadSetLayerVisibilityCommand(
        IEnumerable<string> layerNames,
        bool isOn,
        string description = "Set layer visibility")
        : base(description)
    {
        _layerNames = NormalizeLayerNames(layerNames, nameof(layerNames));
        IsOn = isOn;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Layer[] layers;
        if (isRedo)
        {
            layers = GetRetainedLayers(document);
        }
        else
        {
            layers = ResolveLayers(document, _layerNames);
            _layers = layers;
            _previousValues = CaptureValues(
                layers,
                static layer => layer.IsOn);
        }
        SetValuesTransactional(
            layers,
            IsOn,
            static layer => layer.IsOn,
            static (layer, value) => layer.IsOn = value);
    }

    internal override void Revert(CadDocument document)
    {
        Layer[] layers = GetRetainedLayers(document);
        bool[] previous = _previousValues ??
            throw new InvalidOperationException(
                "The layer-visibility command has not been applied.");
        SetValuesTransactional(
            layers,
            previous,
            static layer => layer.IsOn,
            static (layer, value) => layer.IsOn = value);
    }

    private Layer[] GetRetainedLayers(CadDocument document)
    {
        Layer[] layers = _layers ??
            throw new InvalidOperationException(
                "The layer-visibility command has not been applied.");
        ValidateLayers(document, layers);
        return layers;
    }
}

/// <summary>Sets model-space regeneration eligibility for existing layers.</summary>
public sealed class CadSetLayerFreezeCommand : CadEditCommand
{
    private readonly string[] _layerNames;
    private Layer[]? _layers;
    private bool[]? _previousValues;

    public ReadOnlyMemory<string> LayerNames => _layerNames;

    public bool IsFrozen { get; }

    public CadSetLayerFreezeCommand(
        IEnumerable<string> layerNames,
        bool isFrozen,
        string description = "Set layer freeze state")
        : base(description)
    {
        _layerNames = NormalizeLayerNames(layerNames, nameof(layerNames));
        IsFrozen = isFrozen;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Layer[] layers;
        if (isRedo)
        {
            layers = GetRetainedLayers(document);
        }
        else
        {
            layers = ResolveLayers(document, _layerNames);
            _layers = layers;
            _previousValues = CaptureValues(
                layers,
                static layer => HasLayerFlag(layer, LayerFlags.Frozen));
        }
        SetValuesTransactional(
            layers,
            IsFrozen,
            static layer => HasLayerFlag(layer, LayerFlags.Frozen),
            static (layer, value) =>
                SetLayerFlag(layer, LayerFlags.Frozen, value));
    }

    internal override void Revert(CadDocument document)
    {
        Layer[] layers = GetRetainedLayers(document);
        bool[] previous = _previousValues ??
            throw new InvalidOperationException(
                "The layer-freeze command has not been applied.");
        SetValuesTransactional(
            layers,
            previous,
            static layer => HasLayerFlag(layer, LayerFlags.Frozen),
            static (layer, value) =>
                SetLayerFlag(layer, LayerFlags.Frozen, value));
    }

    private Layer[] GetRetainedLayers(CadDocument document)
    {
        Layer[] layers = _layers ??
            throw new InvalidOperationException(
                "The layer-freeze command has not been applied.");
        ValidateLayers(document, layers);
        return layers;
    }
}

/// <summary>Sets entity-edit authorization for existing layers.</summary>
public sealed class CadSetLayerLockCommand : CadEditCommand
{
    private readonly string[] _layerNames;
    private Layer[]? _layers;
    private bool[]? _previousValues;

    public ReadOnlyMemory<string> LayerNames => _layerNames;

    public bool IsLocked { get; }

    public CadSetLayerLockCommand(
        IEnumerable<string> layerNames,
        bool isLocked,
        string description = "Set layer lock state")
        : base(description)
    {
        _layerNames = NormalizeLayerNames(layerNames, nameof(layerNames));
        IsLocked = isLocked;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Layer[] layers;
        if (isRedo)
        {
            layers = GetRetainedLayers(document);
        }
        else
        {
            layers = ResolveLayers(document, _layerNames);
            _layers = layers;
            _previousValues = CaptureValues(
                layers,
                static layer => HasLayerFlag(layer, LayerFlags.Locked));
        }
        SetValuesTransactional(
            layers,
            IsLocked,
            static layer => HasLayerFlag(layer, LayerFlags.Locked),
            static (layer, value) =>
                SetLayerFlag(layer, LayerFlags.Locked, value));
    }

    internal override void Revert(CadDocument document)
    {
        Layer[] layers = GetRetainedLayers(document);
        bool[] previous = _previousValues ??
            throw new InvalidOperationException(
                "The layer-lock command has not been applied.");
        SetValuesTransactional(
            layers,
            previous,
            static layer => HasLayerFlag(layer, LayerFlags.Locked),
            static (layer, value) =>
                SetLayerFlag(layer, LayerFlags.Locked, value));
    }

    private Layer[] GetRetainedLayers(CadDocument document)
    {
        Layer[] layers = _layers ??
            throw new InvalidOperationException(
                "The layer-lock command has not been applied.");
        ValidateLayers(document, layers);
        return layers;
    }
}

/// <summary>Sets plot eligibility for a stable set of existing layers.</summary>
public sealed class CadSetLayerPlotFlagCommand : CadEditCommand
{
    private readonly string[] _layerNames;
    private Layer[]? _layers;
    private bool[]? _previousValues;

    public ReadOnlyMemory<string> LayerNames => _layerNames;

    public bool PlotFlag { get; }

    public CadSetLayerPlotFlagCommand(
        IEnumerable<string> layerNames,
        bool plotFlag,
        string description = "Set layer plot eligibility")
        : base(description)
    {
        _layerNames = NormalizeLayerNames(layerNames, nameof(layerNames));
        PlotFlag = plotFlag;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Layer[] layers;
        if (isRedo)
        {
            layers = GetRetainedLayers(document);
        }
        else
        {
            layers = ResolveLayers(document, _layerNames);
            _layers = layers;
            _previousValues = CaptureValues(
                layers,
                static layer => layer.PlotFlag);
        }
        SetValuesTransactional(
            layers,
            PlotFlag,
            static layer => layer.PlotFlag,
            static (layer, value) => layer.PlotFlag = value);
    }

    internal override void Revert(CadDocument document)
    {
        Layer[] layers = GetRetainedLayers(document);
        bool[] previous = _previousValues ??
            throw new InvalidOperationException(
                "The layer-plot command has not been applied.");
        SetValuesTransactional(
            layers,
            previous,
            static layer => layer.PlotFlag,
            static (layer, value) => layer.PlotFlag = value);
    }

    private Layer[] GetRetainedLayers(CadDocument document)
    {
        Layer[] layers = _layers ??
            throw new InvalidOperationException(
                "The layer-plot command has not been applied.");
        ValidateLayers(document, layers);
        return layers;
    }
}

/// <summary>Assigns an indexed or true color to a stable set of existing layers.</summary>
public sealed class CadSetLayerColorCommand : CadEditCommand
{
    private readonly string[] _layerNames;
    private Layer[]? _layers;
    private ACadSharp.Color[]? _previousValues;

    public ReadOnlyMemory<string> LayerNames => _layerNames;

    public ACadSharp.Color Color { get; }

    public CadSetLayerColorCommand(
        IEnumerable<string> layerNames,
        ACadSharp.Color color,
        string description = "Set layer color")
        : base(description)
    {
        if (!color.IsTrueColor &&
            color.Index is 0 or 256 or 257)
        {
            throw new ArgumentOutOfRangeException(
                nameof(color),
                "Layer color must be an indexed or true explicit color.");
        }
        _layerNames = NormalizeLayerNames(layerNames, nameof(layerNames));
        Color = color;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Layer[] layers;
        if (isRedo)
        {
            layers = GetRetainedLayers(document);
        }
        else
        {
            layers = ResolveLayers(document, _layerNames);
            _layers = layers;
            _previousValues = CaptureValues(
                layers,
                static layer => layer.Color);
        }
        SetValuesTransactional(
            layers,
            Color,
            static layer => layer.Color,
            static (layer, value) => layer.Color = value);
    }

    internal override void Revert(CadDocument document)
    {
        Layer[] layers = GetRetainedLayers(document);
        ACadSharp.Color[] previous = _previousValues ??
            throw new InvalidOperationException(
                "The layer-color command has not been applied.");
        SetValuesTransactional(
            layers,
            previous,
            static layer => layer.Color,
            static (layer, value) => layer.Color = value);
    }

    private Layer[] GetRetainedLayers(CadDocument document)
    {
        Layer[] layers = _layers ??
            throw new InvalidOperationException(
                "The layer-color command has not been applied.");
        ValidateLayers(document, layers);
        return layers;
    }
}

/// <summary>Assigns an explicit or default lineweight to existing layers.</summary>
public sealed class CadSetLayerLineWeightCommand : CadEditCommand
{
    private readonly string[] _layerNames;
    private Layer[]? _layers;
    private LineWeightType[]? _previousValues;

    public ReadOnlyMemory<string> LayerNames => _layerNames;

    public LineWeightType LineWeight { get; }

    public CadSetLayerLineWeightCommand(
        IEnumerable<string> layerNames,
        LineWeightType lineWeight,
        string description = "Set layer lineweight")
        : base(description)
    {
        if (!Enum.IsDefined(lineWeight) ||
            lineWeight is
                LineWeightType.ByDIPs or
                LineWeightType.ByLayer or
                LineWeightType.ByBlock)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lineWeight),
                "Layer lineweight must be an explicit or default CAD lineweight value.");
        }
        _layerNames = NormalizeLayerNames(layerNames, nameof(layerNames));
        LineWeight = lineWeight;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Layer[] layers;
        if (isRedo)
        {
            layers = GetRetainedLayers(document);
        }
        else
        {
            layers = ResolveLayers(document, _layerNames);
            _layers = layers;
            _previousValues = CaptureValues(
                layers,
                static layer => layer.LineWeight);
        }
        SetValuesTransactional(
            layers,
            LineWeight,
            static layer => layer.LineWeight,
            static (layer, value) => layer.LineWeight = value);
    }

    internal override void Revert(CadDocument document)
    {
        Layer[] layers = GetRetainedLayers(document);
        LineWeightType[] previous = _previousValues ??
            throw new InvalidOperationException(
                "The layer-lineweight command has not been applied.");
        SetValuesTransactional(
            layers,
            previous,
            static layer => layer.LineWeight,
            static (layer, value) => layer.LineWeight = value);
    }

    private Layer[] GetRetainedLayers(CadDocument document)
    {
        Layer[] layers = _layers ??
            throw new InvalidOperationException(
                "The layer-lineweight command has not been applied.");
        ValidateLayers(document, layers);
        return layers;
    }
}

/// <summary>Assigns one existing explicit linetype to existing layers.</summary>
public sealed class CadSetLayerLineTypeCommand : CadEditCommand
{
    private readonly string[] _layerNames;
    private Layer[]? _layers;
    private LineType[]? _previousValues;
    private LineType? _targetLineType;

    public ReadOnlyMemory<string> LayerNames => _layerNames;

    public string LineTypeName { get; }

    public CadSetLayerLineTypeCommand(
        IEnumerable<string> layerNames,
        string lineTypeName,
        string description = "Set layer linetype")
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lineTypeName);
        if (lineTypeName.Equals(LineType.ByLayerName, StringComparison.OrdinalIgnoreCase) ||
            lineTypeName.Equals(LineType.ByBlockName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lineTypeName),
                "A layer linetype cannot be ByLayer or ByBlock.");
        }
        _layerNames = NormalizeLayerNames(layerNames, nameof(layerNames));
        LineTypeName = lineTypeName;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Layer[] layers;
        LineType target;
        if (isRedo)
        {
            layers = GetRetainedLayers(document);
            target = _targetLineType ??
                throw new InvalidOperationException(
                    "The layer-linetype command has not been applied.");
            ValidateLineType(document, target);
        }
        else
        {
            layers = ResolveLayers(document, _layerNames);
            target = ResolveLineType(document, LineTypeName);
            _layers = layers;
            _previousValues = CaptureValues(
                layers,
                static layer => layer.LineType);
            _targetLineType = target;
        }
        SetValuesTransactional(
            layers,
            target,
            static layer => layer.LineType,
            static (layer, value) => layer.LineType = value);
    }

    internal override void Revert(CadDocument document)
    {
        Layer[] layers = GetRetainedLayers(document);
        LineType[] previous = _previousValues ??
            throw new InvalidOperationException(
                "The layer-linetype command has not been applied.");
        ValidateLineTypes(document, previous);
        SetValuesTransactional(
            layers,
            previous,
            static layer => layer.LineType,
            static (layer, value) => layer.LineType = value);
    }

    private Layer[] GetRetainedLayers(CadDocument document)
    {
        Layer[] layers = _layers ??
            throw new InvalidOperationException(
                "The layer-linetype command has not been applied.");
        ValidateLayers(document, layers);
        return layers;
    }
}

/// <summary>Adds one detached layer with reversible table ownership.</summary>
public sealed class CadAddLayerCommand : CadEditCommand
{
    public Layer Layer { get; }

    public ulong CurrentHandle => Layer.Handle;

    public CadAddLayerCommand(
        Layer layer,
        string description = "Add layer")
        : base(description)
    {
        Layer = layer ?? throw new ArgumentNullException(nameof(layer));
        if (layer.Owner is not null || layer.Handle != 0)
        {
            throw new ArgumentException(
                "An added layer must be detached and have no assigned handle.",
                nameof(layer));
        }
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        if (Layer.Owner is not null || Layer.Handle != 0)
        {
            throw new InvalidOperationException(
                "The layer is not detached and cannot be added to the document.");
        }
        ValidateLayerNameAvailable(document, Layer.Name);
        if ((Layer.Flags & LayerFlags.XrefDependent) != 0)
        {
            throw new InvalidOperationException(
                "A user-created layer cannot be xref-dependent.");
        }
        document.Layers.Add(Layer);
    }

    internal override void Revert(CadDocument document)
    {
        ValidateLayer(document, Layer);
        Layer removed = document.Layers.Remove(Layer.Name);
        if (!ReferenceEquals(removed, Layer))
        {
            throw new InvalidOperationException(
                "The added layer could not be removed from the document.");
        }
    }
}

/// <summary>Renames one retained layer while preserving table-entry identity.</summary>
public sealed class CadRenameLayerCommand : CadEditCommand
{
    private Layer? _layer;
    private string? _originalName;

    public string LayerName { get; }

    public string NewName { get; }

    public CadRenameLayerCommand(
        string layerName,
        string newName,
        string description = "Rename layer")
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        LayerName = layerName;
        NewName = newName;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Layer layer;
        if (isRedo)
        {
            layer = GetRetainedLayer(document);
        }
        else
        {
            layer = ResolveLayers(document, [LayerName])[0];
            _layer = layer;
            _originalName = layer.Name;
        }

        ValidateLayerCanBeRenamed(layer);
        if (layer.Name.Equals(NewName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The new layer name must differ from the current name.");
        }
        ValidateLayerNameAvailable(document, NewName, layer);
        layer.Name = NewName;
    }

    internal override void Revert(CadDocument document)
    {
        Layer layer = GetRetainedLayer(document);
        string originalName = _originalName ??
            throw new InvalidOperationException(
                "The layer-rename command has not been applied.");
        ValidateLayerCanBeRenamed(layer);
        ValidateLayerNameAvailable(document, originalName, layer);
        layer.Name = originalName;
    }

    private Layer GetRetainedLayer(CadDocument document)
    {
        Layer layer = _layer ??
            throw new InvalidOperationException(
                "The layer-rename command has not been applied.");
        ValidateLayer(document, layer);
        return layer;
    }
}

/// <summary>
/// Removes one unreferenced layer and restores the same detached table entry on
/// Undo. Entity, current-layer, viewport-freeze, default, and xref references
/// are rejected before the table is mutated.
/// </summary>
public sealed class CadRemoveLayerCommand : CadEditCommand
{
    private Layer? _layer;

    public string LayerName { get; }

    public ulong CurrentHandle => _layer?.Handle ?? 0;

    public CadRemoveLayerCommand(
        string layerName,
        string description = "Remove layer")
        : base(description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        LayerName = layerName;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Layer layer;
        if (isRedo)
        {
            layer = _layer ??
                throw new InvalidOperationException(
                    "The layer-removal command has not been applied.");
            ValidateLayer(document, layer);
        }
        else
        {
            layer = ResolveLayers(document, [LayerName])[0];
            _layer = layer;
        }

        ValidateLayerCanBeRemoved(document, layer);
        Layer removed = document.Layers.Remove(layer.Name) ??
            throw new InvalidOperationException(
                $"Layer '{layer.Name}' could not be removed.");
        if (!ReferenceEquals(removed, layer))
        {
            throw new InvalidOperationException(
                $"Removing layer '{layer.Name}' returned a different table entry.");
        }
    }

    internal override void Revert(CadDocument document)
    {
        Layer layer = _layer ??
            throw new InvalidOperationException(
                "The layer-removal command has not been applied.");
        if (layer.Owner is not null || layer.Handle != 0)
        {
            throw new InvalidOperationException(
                "The removed layer is not detached and cannot be restored.");
        }
        ValidateLayerNameAvailable(document, layer.Name);
        document.Layers.Add(layer);
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
            target = ResolveLineType(document, LineTypeName);
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
        ValidateLineTypes(document, previous);
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

}

/// <summary>Assigns a positive finite linetype scale to model-space entities.</summary>
public sealed class CadSetEntityLineTypeScaleCommand : CadEditCommand
{
    private readonly ulong[] _handles;
    private Entity[]? _entities;
    private double[]? _previousValues;

    public ReadOnlyMemory<ulong> Handles => _handles;

    public double LineTypeScale { get; }

    public CadSetEntityLineTypeScaleCommand(
        IEnumerable<ulong> handles,
        double lineTypeScale,
        string description = "Set entity linetype scale")
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (!double.IsFinite(lineTypeScale) || lineTypeScale <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lineTypeScale),
                "A linetype scale must be finite and positive.");
        }
        _handles = handles.Distinct().ToArray();
        if (_handles.Length == 0 || _handles.Any(static handle => handle == 0))
        {
            throw new ArgumentException(
                "At least one non-zero entity handle is required.",
                nameof(handles));
        }
        LineTypeScale = lineTypeScale;
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
                static entity => entity.LineTypeScale);
        }
        SetEntityValuesTransactional(
            entities,
            LineTypeScale,
            static entity => entity.LineTypeScale,
            static (entity, value) => entity.LineTypeScale = value);
    }

    internal override void Revert(CadDocument document)
    {
        Entity[] entities = GetRetainedEntities(document);
        double[] previous = _previousValues ??
            throw new InvalidOperationException(
                "The linetype-scale command has not been applied.");
        SetEntityValuesTransactional(
            entities,
            previous,
            static entity => entity.LineTypeScale,
            static (entity, value) => entity.LineTypeScale = value);
    }

    private Entity[] GetRetainedEntities(CadDocument document)
    {
        Entity[] entities = _entities ??
            throw new InvalidOperationException(
                "The linetype-scale command has not been applied.");
        ValidateModelSpaceEntities(document, entities);
        return entities;
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
        if (!Enum.IsDefined(lineWeight) ||
            lineWeight == LineWeightType.ByDIPs)
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

/// <summary>
/// Atomically removes a bounded stable set of model-space entities while
/// retaining their object graphs for semantic Undo/Redo.
/// </summary>
/// <remarks>
/// Initial resolution and every cancellable collection removal are preflighted
/// before the ACadSharp collection is structurally mutated. One history action
/// therefore publishes either the complete selection-set erase or no edit at
/// all. Work and retained storage are O(N) for N unique handles; the default
/// bound prevents an untrusted enumerable from creating unbounded edit state.
/// </remarks>
public sealed class CadRemoveModelSpaceEntitiesCommand : CadEditCommand
{
    public const int DefaultMaximumEntityCount = 65_536;

    private readonly ulong[] _initialHandles;
    private Entity[]? _entities;

    public ReadOnlyMemory<ulong> InitialHandles => _initialHandles;

    public int EntityCount => _initialHandles.Length;

    public int MaximumEntityCount { get; }

    public CadRemoveModelSpaceEntitiesCommand(
        IEnumerable<ulong> handles,
        string description = "Remove entities",
        int maximumEntityCount = DefaultMaximumEntityCount)
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntityCount);

        var unique = new HashSet<ulong>();
        var retainedHandles = new List<ulong>();
        foreach (ulong handle in handles)
        {
            if (handle == 0)
            {
                throw new ArgumentException(
                    "Every model-space entity handle must be non-zero.",
                    nameof(handles));
            }
            if (!unique.Add(handle))
            {
                continue;
            }
            if (retainedHandles.Count == maximumEntityCount)
            {
                throw new ArgumentException(
                    $"The removal set exceeds the configured limit of {maximumEntityCount} unique entities.",
                    nameof(handles));
            }
            retainedHandles.Add(handle);
        }

        if (retainedHandles.Count == 0)
        {
            throw new ArgumentException(
                "At least one non-zero model-space entity handle is required.",
                nameof(handles));
        }

        MaximumEntityCount = maximumEntityCount;
        _initialHandles = retainedHandles.ToArray();
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity[] entities;
        if (isRedo)
        {
            entities = GetAttachedEntities(document);
        }
        else
        {
            // Resolve the complete semantic selection before invoking any
            // cancellable collection-removal callback.
            entities = ResolveModelSpaceEntities(document, _initialHandles);
        }

        if (!document.Entities.TryRemoveRange(entities))
        {
            throw new InvalidOperationException(
                "The model-space removal batch was cancelled before mutation.");
        }

        if (!isRedo)
        {
            _entities = entities;
        }
    }

    internal override void Revert(CadDocument document)
    {
        Entity[] entities = _entities ??
            throw new InvalidOperationException(
                "The removal command has not been applied.");
        foreach (Entity entity in entities)
        {
            if (entity.Owner is not null || entity.Handle != 0)
            {
                throw new InvalidOperationException(
                    "A removed entity is not detached and the batch cannot be restored.");
            }
        }

        document.Entities.AddRange(entities);
    }

    private Entity[] GetAttachedEntities(CadDocument document)
    {
        Entity[] entities = _entities ??
            throw new InvalidOperationException(
                "The removal command has not been applied.");
        ValidateModelSpaceEntities(document, entities);
        return entities;
    }
}

/// <summary>
/// Duplicates a bounded stable set of model-space entities with one optional
/// WCS displacement as a single reversible edit.
/// </summary>
/// <remarks>
/// The complete source selection is resolved before cloning, and every clone
/// is detached and transformed before ACadSharp publishes the structurally
/// complete addition batch. Undo removes the same retained object graphs as
/// one preflighted batch; Redo restores those graphs rather than cloning the
/// potentially changed sources again. Work and retained storage are O(N) for
/// N unique source handles.
/// </remarks>
public sealed class CadDuplicateModelSpaceEntitiesCommand : CadEditCommand
{
    public const int DefaultMaximumEntityCount = 65_536;

    private readonly ulong[] _sourceHandles;
    private readonly ulong[] _currentHandles;
    private readonly XYZ? _translation;
    private Entity[]? _duplicates;

    public ReadOnlyMemory<ulong> SourceHandles => _sourceHandles;

    public ReadOnlyMemory<ulong> CurrentHandles => _currentHandles;

    public ReadOnlyMemory<Entity> Duplicates =>
        _duplicates ?? ReadOnlyMemory<Entity>.Empty;

    public int EntityCount => _sourceHandles.Length;

    public int MaximumEntityCount { get; }

    public CadPoint3D? Translation { get; }

    public CadDuplicateModelSpaceEntitiesCommand(
        IEnumerable<ulong> sourceHandles,
        CadPoint3D? translation = null,
        string description = "Duplicate entities",
        int maximumEntityCount = DefaultMaximumEntityCount)
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(sourceHandles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntityCount);
        if (translation is CadPoint3D value &&
            (!double.IsFinite(value.X) ||
             !double.IsFinite(value.Y) ||
             !double.IsFinite(value.Z)))
        {
            throw new ArgumentException(
                "A duplicate translation must be finite.",
                nameof(translation));
        }

        var unique = new HashSet<ulong>();
        var retainedHandles = new List<ulong>();
        foreach (ulong handle in sourceHandles)
        {
            if (handle == 0)
            {
                throw new ArgumentException(
                    "Every model-space source handle must be non-zero.",
                    nameof(sourceHandles));
            }
            if (!unique.Add(handle))
            {
                continue;
            }
            if (retainedHandles.Count == maximumEntityCount)
            {
                throw new ArgumentException(
                    $"The duplicate set exceeds the configured limit of {maximumEntityCount} unique entities.",
                    nameof(sourceHandles));
            }
            retainedHandles.Add(handle);
        }

        if (retainedHandles.Count == 0)
        {
            throw new ArgumentException(
                "At least one non-zero model-space source handle is required.",
                nameof(sourceHandles));
        }

        MaximumEntityCount = maximumEntityCount;
        Translation = translation;
        _translation = translation is CadPoint3D point
            ? new XYZ(point.X, point.Y, point.Z)
            : null;
        _sourceHandles = retainedHandles.ToArray();
        _currentHandles = new ulong[_sourceHandles.Length];
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity[] duplicates;
        if (isRedo)
        {
            duplicates = _duplicates ??
                throw new InvalidOperationException(
                    "The duplicate command has not been applied.");
        }
        else
        {
            Entity[] sources = ResolveModelSpaceEntities(
                document,
                _sourceHandles);
            duplicates = new Entity[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                Entity duplicate = (Entity)sources[i].Clone();
                ValidateDetachedDuplicate(duplicate);
                if (_translation is XYZ displacement &&
                    displacement != XYZ.Zero)
                {
                    ApplyEntityTranslation(duplicate, displacement);
                }
                duplicates[i] = duplicate;
            }
            _duplicates = duplicates;
        }

        foreach (Entity duplicate in duplicates)
        {
            ValidateDetachedDuplicate(duplicate);
        }
        document.Entities.AddRange(duplicates);
        for (int i = 0; i < duplicates.Length; i++)
        {
            _currentHandles[i] = duplicates[i].Handle;
        }
    }

    internal override void Revert(CadDocument document)
    {
        Entity[] duplicates = _duplicates ??
            throw new InvalidOperationException(
                "The duplicate command has not been applied.");
        ValidateModelSpaceEntities(document, duplicates);
        if (!document.Entities.TryRemoveRange(duplicates))
        {
            throw new InvalidOperationException(
                "The model-space duplicate batch removal was cancelled before mutation.");
        }
        Array.Clear(_currentHandles);
    }

    private static void ValidateDetachedDuplicate(Entity duplicate)
    {
        if (duplicate.Owner is not null ||
            duplicate.Document is not null ||
            duplicate.Handle != 0)
        {
            throw new InvalidOperationException(
                "A duplicated entity is not detached and cannot be added to model space.");
        }
    }
}

/// <summary>Duplicates one model-space entity with optional translation.</summary>
public sealed class CadDuplicateModelSpaceEntityCommand : CadEditCommand
{
    private readonly ulong _sourceHandle;
    private readonly XYZ? _translation;
    private Entity? _duplicate;

    public ulong SourceHandle => _sourceHandle;

    public CadPoint3D? Translation { get; }

    public Entity? Duplicate => _duplicate;

    public ulong CurrentHandle => _duplicate?.Handle ?? 0;

    public CadDuplicateModelSpaceEntityCommand(
        ulong sourceHandle,
        CadPoint3D? translation = null,
        string description = "Duplicate entity")
        : base(description)
    {
        if (sourceHandle == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceHandle),
                "A non-zero model-space entity handle is required.");
        }
        if (translation is CadPoint3D value &&
            (!double.IsFinite(value.X) ||
             !double.IsFinite(value.Y) ||
             !double.IsFinite(value.Z)))
        {
            throw new ArgumentException(
                "A duplicate translation must be finite.",
                nameof(translation));
        }

        _sourceHandle = sourceHandle;
        Translation = translation;
        _translation = translation is CadPoint3D point
            ? new XYZ(point.X, point.Y, point.Z)
            : null;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        Entity duplicate;
        if (isRedo)
        {
            duplicate = _duplicate ??
                throw new InvalidOperationException(
                    "The duplicate command has not been applied.");
        }
        else
        {
            Entity source = ResolveModelSpaceEntity(document, _sourceHandle);
            duplicate = (Entity)source.Clone();
            if (duplicate.Owner is not null || duplicate.Handle != 0)
            {
                throw new InvalidOperationException(
                    "The cloned entity is not detached from its source document.");
            }
            if (_translation is XYZ translation && translation != XYZ.Zero)
            {
                ApplyEntityTranslation(duplicate, translation);
            }
            _duplicate = duplicate;
        }

        if (duplicate.Owner is not null || duplicate.Handle != 0)
        {
            throw new InvalidOperationException(
                "The duplicated entity is not detached and cannot be restored.");
        }
        document.Entities.Add(duplicate);
    }

    internal override void Revert(CadDocument document)
    {
        Entity duplicate = _duplicate ??
            throw new InvalidOperationException(
                "The duplicate command has not been applied.");
        ValidateModelSpaceEntity(document, duplicate);
        if (!document.Entities.Remove(duplicate))
        {
            throw new InvalidOperationException(
                "The duplicated entity could not be removed from model space.");
        }
    }
}

/// <summary>
/// Replaces one variable block-attribute value selected by insert handle, tag,
/// and zero-based duplicate-tag occurrence.
/// </summary>
public sealed class CadSetAttributeValueCommand : CadEditCommand
{
    private readonly ulong _insertHandle;
    private Insert? _insert;
    private AttributeEntity? _attribute;
    private string? _previousValue;
    private string? _previousMTextValue;

    public ulong InsertHandle => _insertHandle;

    public string Tag { get; }

    public int Occurrence { get; }

    public string Value { get; }

    public CadSetAttributeValueCommand(
        ulong insertHandle,
        string tag,
        string value,
        int occurrence = 0,
        string description = "Set attribute value")
        : base(description)
    {
        if (insertHandle == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(insertHandle),
                "A non-zero model-space INSERT handle is required.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(occurrence);

        _insertHandle = insertHandle;
        Tag = tag;
        Value = value;
        Occurrence = occurrence;
    }

    internal override void Apply(CadDocument document, bool isRedo)
    {
        AttributeEntity attribute;
        if (isRedo)
        {
            attribute = GetRetainedAttribute(document);
        }
        else
        {
            Entity entity = ResolveModelSpaceEntity(document, _insertHandle);
            Insert insert = entity as Insert ?? throw new InvalidOperationException(
                $"Model-space entity handle {_insertHandle:X} is not an INSERT.");
            attribute = ResolveAttribute(insert, Tag, Occurrence);
            if ((attribute.Flags & AttributeFlags.Constant) != 0 ||
                attribute.AttributeType == AttributeType.ConstantMultiLine)
            {
                throw new InvalidOperationException(
                    $"Attribute '{Tag}' occurrence {Occurrence} is constant and definition-owned.");
            }
            if (attribute.AttributeType != AttributeType.SingleLine &&
                attribute.MText is null)
            {
                throw new InvalidOperationException(
                    $"Attribute '{Tag}' occurrence {Occurrence} has no embedded MTEXT payload.");
            }

            _insert = insert;
            _attribute = attribute;
            _previousValue = attribute.Value;
            _previousMTextValue = attribute.MText?.Value;
        }

        SetValueTransactional(attribute, Value, Value);
    }

    internal override void Revert(CadDocument document)
    {
        AttributeEntity attribute = GetRetainedAttribute(document);
        string previous = _previousValue ?? throw new InvalidOperationException(
            "The attribute-value command has not been applied.");
        SetValueTransactional(attribute, previous, _previousMTextValue);
    }

    private AttributeEntity GetRetainedAttribute(CadDocument document)
    {
        Insert insert = _insert ?? throw new InvalidOperationException(
            "The attribute-value command has not been applied.");
        AttributeEntity attribute = _attribute ?? throw new InvalidOperationException(
            "The attribute-value command has not been applied.");
        ValidateModelSpaceEntity(document, insert);
        AttributeEntity current = ResolveAttribute(insert, Tag, Occurrence);
        if (!ReferenceEquals(current, attribute))
        {
            throw new InvalidOperationException(
                $"Attribute '{Tag}' occurrence {Occurrence} is no longer the retained attribute.");
        }
        return attribute;
    }

    private static AttributeEntity ResolveAttribute(
        Insert insert,
        string tag,
        int occurrence)
    {
        int currentOccurrence = 0;
        foreach (AttributeEntity attribute in insert.Attributes)
        {
            if (!string.Equals(attribute.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (currentOccurrence == occurrence)
            {
                return attribute;
            }
            currentOccurrence++;
        }

        throw new InvalidOperationException(
            $"INSERT handle {insert.Handle:X} has no attribute '{tag}' occurrence {occurrence}.");
    }

    private static void SetValueTransactional(
        AttributeEntity attribute,
        string value,
        string? mtextValue)
    {
        string rollbackValue = attribute.Value;
        string? rollbackMTextValue = attribute.MText?.Value;
        try
        {
            attribute.Value = value;
            if (attribute.MText is MText mtext)
            {
                mtext.Value = mtextValue ?? value;
            }
        }
        catch
        {
            attribute.Value = rollbackValue;
            if (attribute.MText is MText mtext && rollbackMTextValue is not null)
            {
                mtext.Value = rollbackMTextValue;
            }
            throw;
        }
    }
}
