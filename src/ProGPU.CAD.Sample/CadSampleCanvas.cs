using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Vector;
using ACadLayout = ACadSharp.Objects.Layout;

namespace ProGPU.CAD.Sample;

/// <summary>
/// Common general properties for the current semantic model-space selection.
/// A null common property means selected entities have different persisted values;
/// SOLID thickness also carries an explicit family-applicability flag.
/// </summary>
public readonly record struct CadSelectionGeneralProperties(
    int SelectionCount,
    ACadSharp.Color? CommonColor,
    LineWeightType? CommonLineWeight,
    string? CommonLayerName,
    string? CommonLineTypeName,
    double? CommonLineTypeScale,
    Transparency? CommonTransparency,
    bool? CommonIsInvisible,
    bool AllSelectedEntitiesAreUnlocked,
    bool AllSelectedEntitiesAreSolids,
    double? CommonSolidThickness);

/// <summary>Detached persisted state for one document layer.</summary>
public readonly record struct CadLayerGeneralProperties(
    string Name,
    bool IsOn,
    bool IsPlottable,
    bool IsFrozen,
    bool IsLocked,
    bool IsCurrent,
    bool IsDefault,
    bool IsDefpoints,
    bool IsXrefDependent,
    ACadSharp.Color Color,
    LineWeightType LineWeight,
    string LineTypeName);

/// <summary>
/// Generation-tagged names that can be assigned by the shared property shell.
/// The catalog owns no mutable ACadSharp objects.
/// </summary>
public sealed class CadSelectionPropertyCatalog
{
    private readonly string[] _layerNames;
    private readonly string[] _lineTypeNames;

    public ulong ContentGeneration { get; }

    public ReadOnlyMemory<string> LayerNames => _layerNames;

    public ReadOnlyMemory<string> LineTypeNames => _lineTypeNames;

    internal CadSelectionPropertyCatalog(
        ulong contentGeneration,
        string[] layerNames,
        string[] lineTypeNames)
    {
        ContentGeneration = contentGeneration;
        _layerNames = layerNames;
        _lineTypeNames = lineTypeNames;
    }
}

/// <summary>Shared interactive retained CAD surface used by desktop and browser hosts.</summary>
public sealed class CadSampleCanvas : FrameworkElement
{
    private const int MaxSelectionPropertyCatalogEntries = 65_536;
    private const int MaxSelectionPropertyCatalogCharacters = 1_048_576;
    private readonly Brush _background = new ThemeResourceBrush("CardBackground");
    private readonly Pen _border = new(
        new ThemeResourceBrush("ControlBorder"),
        1,
        strokeTransformMode: PenStrokeTransformMode.Fixed);
    private readonly Pen _selectionPen = new(
        new ThemeResourceBrush("SystemAccentColor"),
        2,
        strokeTransformMode: PenStrokeTransformMode.Fixed);
    private readonly Pen _crossingPen = new(
        new ThemeResourceBrush("TextPrimary"),
        1,
        strokeTransformMode: PenStrokeTransformMode.Fixed);
    private readonly Pen _drawOrderReferencePen = new(
        new ThemeResourceBrush("SystemAccentColor"),
        1,
        strokeTransformMode: PenStrokeTransformMode.Fixed);
    private readonly CadSnapshotOptions _snapshotOptions;
    private readonly HashSet<ulong> _selectedHandleSet = new();
    private readonly HashSet<ulong> _drawOrderReferenceHandleSet = new();
    private GpuPicture? _picture;
    private GpuPicture? _constructionPicture;
    private GpuPicture? _pointMarkerPicture;
    private CadDocumentHistory? _history;
    private CadBounds3D _bounds;
    private CadBounds3D _selectedBounds;
    private CadBounds3D _drawOrderReferenceBounds;
    private Vector2 _pan;
    private Vector2 _pointerOrigin;
    private Vector2 _panOrigin;
    private Vector2 _selectionCurrent;
    private float _zoom = 1;
    private int[] _selectionEntityScratch = [];
    private int[] _selectionHandleScratch = [];
    private CadSelectionCandidate[] _selectionCandidates = [];
    private CadSelectionCandidate[] _selectionMatches = [];
    private ulong[] _selectedHandles = [];
    private ulong[] _drawOrderReferenceHandles = [];
    private ulong[] _drawOrderReferenceQueryHandles = [];
    private int _selectedHandleCount;
    private int _drawOrderReferenceHandleCount;
    private int _lastUnsupportedPrimitiveCount;
    private bool _lastSelectionWasTruncated;
    private bool _isPanning;
    private bool _isSelecting;
    private bool _hasSelectionDrag;
    private bool _needsFit = true;

    private const float SelectionDragThreshold = 4.0f;
    private const float PointSelectionTolerance = 5.0f;

    public CadDocumentSession? CurrentSession { get; private set; }

    public CadDocumentSnapshot? CurrentSnapshot { get; private set; }

    public ReadOnlyMemory<ulong> SelectedHandles =>
        _selectedHandles.AsMemory(0, _selectedHandleCount);

    public int SelectedHandleCount => _selectedHandleCount;

    public bool CanSynchronizeSelectedBlockAttributeProperties
    {
        get
        {
            CadDocumentSession? session = CurrentSession;
            if (_selectedHandleCount != 1 || session is null)
            {
                return false;
            }

            ulong handle = _selectedHandles[0];
            return session.Read(document =>
                document.TryGetCadObject(handle, out Insert? insert) &&
                insert is not null &&
                ReferenceEquals(insert.Owner, document.ModelSpace));
        }
    }

    public ReadOnlyMemory<ulong> DrawOrderReferenceHandles =>
        _drawOrderReferenceHandles.AsMemory(0, _drawOrderReferenceHandleCount);

    public int DrawOrderReferenceHandleCount => _drawOrderReferenceHandleCount;

    public CadDrawOrderPlacement? PendingDrawOrderPlacement { get; private set; }

    public int LastUnsupportedPrimitiveCount => _lastUnsupportedPrimitiveCount;

    public bool LastSelectionWasTruncated => _lastSelectionWasTruncated;

    public int LastDrawOrderReferenceUnsupportedPrimitiveCount { get; private set; }

    public bool LastDrawOrderReferenceSelectionWasTruncated { get; private set; }

    public CadBoundsSelectionMode? LastSelectionMode { get; private set; }

    public CadBoundsSelectionMode? LastDrawOrderReferenceSelectionMode { get; private set; }

    public CadPlanViewport CurrentViewport => CreateViewport();

    public int UndoCount => _history?.UndoCount ?? 0;

    public int RedoCount => _history?.RedoCount ?? 0;

    public event EventHandler? SelectionChanged;

    public event EventHandler? EditStateChanged;

    /// <summary>
    /// Raised when an Above/Under reference-pick session starts, accumulates
    /// references, commits, or is canceled.
    /// </summary>
    public event EventHandler? DrawOrderReferencePickChanged;

    /// <summary>Raised after one complete immutable snapshot/picture replacement.</summary>
    public event EventHandler? SnapshotChanged;

    public CadShxFontCatalog ShxFonts { get; }

    public CadSampleCanvas()
        : this(null)
    {
    }

    public CadSampleCanvas(CadShxFontCatalog? shxFonts)
    {
        ShxFonts = shxFonts ?? new CadShxFontCatalog();
        _snapshotOptions = new CadSnapshotOptions
        {
            TextFontResolver = new CadFontManagerTextResolver(InterFontFamily.Regular),
            ShxFontResolver = ShxFonts,
        };
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerCanceled;
        PointerWheelChanged += OnPointerWheelChanged;
        Unloaded += (_, _) => ReleaseResources();
        Load(CreateRepresentativeDocument());
    }

    public void Load(CadDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        CompileAndReplace(session, resetViewSelectionAndHistory: true);
    }

    private void CompileAndReplace(
        CadDocumentSession session,
        bool resetViewSelectionAndHistory)
    {
        if (!resetViewSelectionAndHistory && !ReferenceEquals(session, CurrentSession))
        {
            throw new InvalidOperationException(
                "An edited CAD scene can only replace the current document session.");
        }

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(session, _snapshotOptions);
        int selectionCapacity = snapshot.Entities.Length;
        int requiredHandleScratch = CadSelectionQuery.GetUniqueHandleScratchLength(
            selectionCapacity);
        int[] selectionEntityScratch = !resetViewSelectionAndHistory &&
            _selectionEntityScratch.Length >= selectionCapacity
            ? _selectionEntityScratch
            : new int[selectionCapacity];
        CadSelectionCandidate[] selectionCandidates = !resetViewSelectionAndHistory &&
            _selectionCandidates.Length >= selectionCapacity
            ? _selectionCandidates
            : new CadSelectionCandidate[selectionCapacity];
        CadSelectionCandidate[] selectionMatches = !resetViewSelectionAndHistory &&
            _selectionMatches.Length >= selectionCapacity
            ? _selectionMatches
            : new CadSelectionCandidate[selectionCapacity];
        int[] selectionHandleScratch = !resetViewSelectionAndHistory &&
            _selectionHandleScratch.Length >= requiredHandleScratch
            ? _selectionHandleScratch
            : new int[requiredHandleScratch];
        int requiredSelectedHandleCapacity = resetViewSelectionAndHistory
            ? selectionCapacity
            : Math.Max(selectionCapacity, _selectedHandleCount);
        ulong[] selectedHandles = !resetViewSelectionAndHistory &&
            _selectedHandles.Length >= requiredSelectedHandleCapacity
            ? _selectedHandles
            : new ulong[requiredSelectedHandleCapacity];
        ulong[] drawOrderReferenceHandles = !resetViewSelectionAndHistory &&
            _drawOrderReferenceHandles.Length >= selectionCapacity
            ? _drawOrderReferenceHandles
            : new ulong[selectionCapacity];
        ulong[] drawOrderReferenceQueryHandles = !resetViewSelectionAndHistory &&
            _drawOrderReferenceQueryHandles.Length >= selectionCapacity
            ? _drawOrderReferenceQueryHandles
            : new ulong[selectionCapacity];
        int preservedHandleCount = resetViewSelectionAndHistory
            ? 0
            : Math.Min(_selectedHandleCount, selectedHandles.Length);
        if (!resetViewSelectionAndHistory &&
            !ReferenceEquals(selectedHandles, _selectedHandles))
        {
            _selectedHandles.AsSpan(0, preservedHandleCount).CopyTo(selectedHandles);
        }

        Vector2 replacementPan = _pan;
        if (!resetViewSelectionAndHistory && CurrentSnapshot is not null)
        {
            replacementPan = CreateViewport()
                .WithRebaseOrigin(snapshot.RebaseOrigin)
                .Pan;
        }
        CadDocumentHistory? replacementHistory = resetViewSelectionAndHistory
            ? new CadDocumentHistory(session)
            : _history;
        CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(snapshot);
        GpuPicture picture = scene.CreatePicture();
        GpuPicture? previous = _picture;
        _picture = picture;
        CurrentSession = session;
        CurrentSnapshot = snapshot;
        _bounds = snapshot.Bounds;
        _selectionEntityScratch = selectionEntityScratch;
        _selectionCandidates = selectionCandidates;
        _selectionMatches = selectionMatches;
        _selectionHandleScratch = selectionHandleScratch;
        _selectedHandles = selectedHandles;
        _drawOrderReferenceHandles = drawOrderReferenceHandles;
        _drawOrderReferenceQueryHandles = drawOrderReferenceQueryHandles;
        _history = replacementHistory;
        _pan = replacementPan;
        if (resetViewSelectionAndHistory)
        {
            ResetDrawOrderReferencePickState(notify: false);
            ResetSelectionState(notify: false);
            _needsFit = true;
        }
        else
        {
            _selectedHandleCount = preservedHandleCount;
            RefreshSelectionBounds(snapshot);
        }
        RefreshConstructionPicture();
        previous?.Dispose();
        Invalidate();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        EditStateChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        if (_needsFit && Size.X > 0 && Size.Y > 0)
        {
            FitToView();
        }
        else
        {
            RefreshConstructionPicture();
        }
    }

    public override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(_background, _border, new Rect(0, 0, Size.X, Size.Y));
        if (_picture is null || Size.X <= 0 || Size.Y <= 0)
        {
            return;
        }

        CadPlanViewport viewport = CreateViewport();
        context.PushClip(new Rect(0, 0, Size.X, Size.Y));
        context.DrawPicture(_picture, viewport.CreateCameraMatrix());
        if (_constructionPicture is not null)
        {
            context.DrawPicture(_constructionPicture, viewport.CreateCameraMatrix());
        }
        if (_pointMarkerPicture is not null)
        {
            context.DrawPicture(_pointMarkerPicture, viewport.CreateCameraMatrix());
        }
        if (!_selectedBounds.IsEmpty)
        {
            context.DrawRectangle(
                null,
                _selectionPen,
                ToScreenRect(viewport, _selectedBounds));
        }
        if (!_drawOrderReferenceBounds.IsEmpty)
        {
            context.DrawRectangle(
                null,
                _drawOrderReferencePen,
                ToScreenRect(viewport, _drawOrderReferenceBounds));
        }
        if (_isSelecting && _hasSelectionDrag)
        {
            context.DrawRectangle(
                null,
                _selectionCurrent.X >= _pointerOrigin.X
                    ? _selectionPen
                    : _crossingPen,
                ToScreenRect(_pointerOrigin, _selectionCurrent));
        }
        context.PopClip();
    }

    public void FitToView()
    {
        if (_bounds.IsEmpty || Size.X <= 0 || Size.Y <= 0)
        {
            return;
        }

        double width = Math.Max(_bounds.Max.X - _bounds.Min.X, 1e-6);
        double height = Math.Max(_bounds.Max.Y - _bounds.Min.Y, 1e-6);
        _zoom = (float)Math.Min((Size.X * 0.88) / width, (Size.Y * 0.88) / height);
        _zoom = Math.Clamp(_zoom, 0.00001f, 1_000_000f);
        _pan = Vector2.Zero;
        _needsFit = false;
        RefreshConstructionPicture();
        Invalidate();
    }

    private void OnPointerPressed(object? sender, PointerRoutedEventArgs args)
    {
        if (args.IsMiddleButtonPressed || args.IsRightButtonPressed)
        {
            _isPanning = true;
            _isSelecting = false;
            _pointerOrigin = args.Position;
            _panOrigin = _pan;
            CapturePointer(args.Pointer);
            args.Handled = true;
            return;
        }
        if (!args.IsLeftButtonPressed || CurrentSnapshot is null)
        {
            return;
        }

        _isSelecting = true;
        _isPanning = false;
        _hasSelectionDrag = false;
        _pointerOrigin = args.Position;
        _selectionCurrent = args.Position;
        CapturePointer(args.Pointer);
        Invalidate();
        args.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerRoutedEventArgs args)
    {
        if (_isPanning)
        {
            _pan = _panOrigin + (args.Position - _pointerOrigin);
            RefreshConstructionPicture();
            Invalidate();
            args.Handled = true;
            return;
        }
        if (!_isSelecting)
        {
            return;
        }

        _selectionCurrent = args.Position;
        Vector2 delta = _selectionCurrent - _pointerOrigin;
        _hasSelectionDrag = delta.LengthSquared() >=
            SelectionDragThreshold * SelectionDragThreshold;
        Invalidate();
        args.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerRoutedEventArgs args)
    {
        bool handled = _isPanning || _isSelecting;
        if (_isSelecting)
        {
            _selectionCurrent = args.Position;
            if (!args.IsCanceled)
            {
                CompleteSelection();
            }
        }
        _isPanning = false;
        _isSelecting = false;
        _hasSelectionDrag = false;
        ReleasePointerCapture(args.Pointer);
        if (handled)
        {
            Invalidate();
            args.Handled = true;
        }
    }

    private void OnPointerCanceled(object? sender, PointerRoutedEventArgs args)
    {
        _isPanning = false;
        _isSelecting = false;
        _hasSelectionDrag = false;
        ReleasePointerCapture(args.Pointer);
        Invalidate();
        args.Handled = true;
    }

    private void OnPointerWheelChanged(object? sender, PointerRoutedEventArgs args)
    {
        float factor = args.IsPreciseScrolling
            ? MathF.Exp(args.WheelDelta / 120f)
            : args.WheelDelta > 0 ? 1.15f : 0.85f;
        Vector2 center = Size * 0.5f;
        Vector2 local = (args.Position - center - _pan) / _zoom;
        _zoom = Math.Clamp(_zoom * factor, 0.00001f, 1_000_000f);
        _pan = args.Position - center - (local * _zoom);
        RefreshConstructionPicture();
        Invalidate();
        args.Handled = true;
    }

    public void ClearSelection()
    {
        ResetDrawOrderReferencePickState(notify: true);
        ResetSelectionState(notify: true);
        Invalidate();
    }

    /// <summary>
    /// Starts a bounded multi-gesture reference selection for an Above or Under
    /// draw-order edit. The edited selection remains unchanged until commit.
    /// </summary>
    public bool BeginSelectionDrawOrderReferencePick(
        CadDrawOrderPlacement placement)
    {
        if (_selectedHandleCount == 0)
        {
            return false;
        }
        if (placement is not
            (CadDrawOrderPlacement.BringAbove or
             CadDrawOrderPlacement.SendUnder))
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }

        ResetDrawOrderReferencePickState(notify: false);
        PendingDrawOrderPlacement = placement;
        DrawOrderReferencePickChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
        return true;
    }

    /// <summary>
    /// Commits all accumulated semantic reference roots as one reversible
    /// persisted draw-order edit. An empty reference selection remains active.
    /// </summary>
    public bool CommitSelectionDrawOrderReferencePick()
    {
        CadDrawOrderPlacement? placement = PendingDrawOrderPlacement;
        if (placement is null || _drawOrderReferenceHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        int selectedCount = _selectedHandleCount;
        int referenceCount = _drawOrderReferenceHandleCount;
        history.Execute(new CadSetModelSpaceDrawOrderCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, selectedCount),
            placement.Value,
            new ArraySegment<ulong>(
                _drawOrderReferenceHandles,
                0,
                referenceCount),
            description: placement == CadDrawOrderPlacement.BringAbove
                ? $"Bring {selectedCount} selected {(selectedCount == 1 ? "entity" : "entities")} above {referenceCount} reference {(referenceCount == 1 ? "entity" : "entities")}"
                : $"Send {selectedCount} selected {(selectedCount == 1 ? "entity" : "entities")} under {referenceCount} reference {(referenceCount == 1 ? "entity" : "entities")}"));
        ResetDrawOrderReferencePickState(notify: false);
        RecompileAfterEdit(session);
        DrawOrderReferencePickChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Cancels a pending reference selection without editing the document.</summary>
    public bool CancelSelectionDrawOrderReferencePick()
    {
        if (PendingDrawOrderPlacement is null)
        {
            return false;
        }

        ResetDrawOrderReferencePickState(notify: true);
        Invalidate();
        return true;
    }

    /// <summary>
    /// Compiles one retained A4 model-extents page for the shared preview host.
    /// The plotting snapshot deliberately resolves default CAD color against
    /// white paper and applies the document's Plotting SORTENTS policy.
    /// </summary>
    public CadPrintPlan CreateA4PrintPlan(float outputDpi)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (!float.IsFinite(outputDpi) || outputDpi <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputDpi),
                "Print-preview DPI must be finite and positive.");
        }

        CadDocumentSnapshot snapshot = CreatePlottingSnapshot();
        return new CadPrintPlanCompiler().Compile(
            snapshot,
            new CadPrintPlanOptions
            {
                OutputDpi = outputDpi,
            });
    }

    /// <summary>
    /// Captures the current drawing's detached layout and named page-setup
    /// catalog for shared desktop/browser selection UI.
    /// </summary>
    public CadPageSetupCatalog CreatePageSetupCatalog()
    {
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        return new CadPageSetupCatalogCompiler().Compile(session);
    }

    /// <summary>
    /// Applies a named page setup to a layout as one generation-safe,
    /// reversible document edit.
    /// </summary>
    public void ApplyNamedPageSetup(
        string targetLayoutName,
        string namedPageSetupName)
    {
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadApplyNamedPageSetupCommand(
            targetLayoutName,
            namedPageSetupName,
            $"Apply page setup '{namedPageSetupName}' to layout '{targetLayoutName}'"));
        RecompileAfterEdit(session);
    }

    /// <summary>
    /// Captures one layout's plot contract as a named page setup through the
    /// generation-safe reversible document history.
    /// </summary>
    public void CreateNamedPageSetupFromLayout(
        string sourceLayoutName,
        string newPageSetupName)
    {
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadCreateNamedPageSetupCommand(
            sourceLayoutName,
            newPageSetupName,
            $"Create page setup '{newPageSetupName}' from layout '{sourceLayoutName}'"));
        RecompileAfterEdit(session);
    }

    /// <summary>
    /// Replaces one named page setup's plot contract from a layout through the
    /// generation-safe reversible document history.
    /// </summary>
    public void UpdateNamedPageSetupFromLayout(
        string sourceLayoutName,
        string targetPageSetupName)
    {
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadUpdateNamedPageSetupFromLayoutCommand(
            sourceLayoutName,
            targetPageSetupName,
            $"Update page setup '{targetPageSetupName}' from layout '{sourceLayoutName}'"));
        RecompileAfterEdit(session);
    }

    /// <summary>
    /// Deletes one unassigned named page setup through the generation-safe
    /// reversible document history.
    /// </summary>
    public void DeleteNamedPageSetup(string pageSetupName)
    {
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadDeleteNamedPageSetupCommand(
            pageSetupName,
            $"Delete page setup '{pageSetupName}'"));
        RecompileAfterEdit(session);
    }

    /// <summary>
    /// Renames one named page setup and its referring layout markers through
    /// the generation-safe reversible document history.
    /// </summary>
    public void RenameNamedPageSetup(string oldName, string newName)
    {
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadRenameNamedPageSetupCommand(
            oldName,
            newName,
            $"Rename page setup '{oldName}' to '{newName}'"));
        RecompileAfterEdit(session);
    }

    /// <summary>
    /// Imports every named page setup captured from another session through
    /// one generation-safe reversible document edit.
    /// </summary>
    public CadPageSetupImportResult ImportNamedPageSetups(
        CadDocumentSession source,
        CadPageSetupImportConflictPolicy conflictPolicy)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        CadImportNamedPageSetupsCommand command =
            CadImportNamedPageSetupsCommand.CaptureAll(
                source,
                conflictPolicy,
                "Import named page setups");
        ulong generation = history.Execute(command);
        RecompileAfterEdit(session);
        return new CadPageSetupImportResult(
            generation,
            command.ImportedCount,
            command.CreatedCount,
            command.ReplacedCount);
    }

    /// <summary>
    /// Compiles one retained page from a generation-matched drawing page setup.
    /// Unsupported page policies fail with their typed CADPAGE diagnostic.
    /// </summary>
    public CadPrintPlan CreatePageSetupPrintPlan(
        CadPageSetupSnapshot pageSetup,
        float outputDpi)
    {
        ArgumentNullException.ThrowIfNull(pageSetup);
        ThrowIfDrawOrderReferencePickPending();
        if (!float.IsFinite(outputDpi) || outputDpi <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputDpi),
                "Print-preview DPI must be finite and positive.");
        }

        CadPageSetupPrintOptionsResult lowering =
            new CadPageSetupPrintOptionsCompiler().Compile(
                pageSetup,
                new CadPageSetupPrintOptionsCompilerOptions
                {
                    OutputDpi = outputDpi,
                });
        return new CadPrintPlanCompiler().CompileFromPageSetup(
            CreatePlottingSnapshot(),
            lowering);
    }

    private CadDocumentSnapshot CreatePlottingSnapshot()
    {
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        return new CadSnapshotCompiler().Compile(
            session,
            new CadSnapshotOptions
            {
                TextFontResolver = new CadFontManagerTextResolver(
                    InterFontFamily.Regular),
                ShxFontResolver = ShxFonts,
                DrawOrderPurpose = CadDrawOrderPurpose.Plotting,
                DrawingBackgroundColor = new CadColor32(
                    byte.MaxValue,
                    byte.MaxValue,
                    byte.MaxValue),
            });
    }

    /// <summary>Moves all selected semantic model-space entities as one edit.</summary>
    public bool TranslateSelection(CadPoint3D translation)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadTranslateEntitiesCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            translation,
            _selectedHandleCount == 1
                ? "Move selected entity"
                : $"Move {_selectedHandleCount} selected entities"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>
    /// Copies all selected semantic model-space roots by one WCS displacement
    /// while preserving the source selection for repeated copy operations.
    /// </summary>
    public bool DuplicateSelection(CadPoint3D translation)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadDuplicateModelSpaceEntitiesCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            translation,
            _selectedHandleCount == 1
                ? "Copy selected entity"
                : $"Copy {_selectedHandleCount} selected entities"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>
    /// Rotates all selected semantic model-space entities around the complete
    /// selection-bounds center and the WCS positive Z axis as one edit.
    /// </summary>
    public bool RotateSelection(double radians)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadRotateEntitiesCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            new CadPoint3D(0, 0, 1),
            radians,
            GetSelectionCenter(),
            _selectedHandleCount == 1
                ? "Rotate selected entity"
                : $"Rotate {_selectedHandleCount} selected entities"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>
    /// Uniformly scales all selected semantic model-space entities around the
    /// complete selection-bounds center as one edit.
    /// </summary>
    public bool ScaleSelection(double factor)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadScaleEntitiesCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            factor,
            GetSelectionCenter(),
            _selectedHandleCount == 1
                ? "Scale selected entity"
                : $"Scale {_selectedHandleCount} selected entities"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>
    /// Captures common persisted general properties of the semantic selection
    /// without retaining ACadSharp objects.
    /// </summary>
    public CadSelectionGeneralProperties CaptureSelectionGeneralProperties()
    {
        if (_selectedHandleCount == 0)
        {
            return default;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        return session.Read(document =>
        {
            ACadSharp.Color? commonColor = null;
            LineWeightType? commonLineWeight = null;
            string? commonLayerName = null;
            string? commonLineTypeName = null;
            double? commonLineTypeScale = null;
            Transparency? commonTransparency = null;
            bool? commonIsInvisible = null;
            bool allSelectedEntitiesAreUnlocked = true;
            bool allSelectedEntitiesAreSolids = true;
            double? commonSolidThickness = null;
            for (int i = 0; i < _selectedHandleCount; i++)
            {
                ulong handle = _selectedHandles[i];
                Entity? entity = document.GetCadObject<Entity>(handle);
                if (entity is null ||
                    !ReferenceEquals(entity.Owner, document.ModelSpace))
                {
                    throw new InvalidOperationException(
                        $"Selected model-space entity handle {handle:X} no longer exists.");
                }
                if ((entity.Layer.Flags & LayerFlags.Locked) != 0)
                {
                    allSelectedEntitiesAreUnlocked = false;
                }

                if (i == 0)
                {
                    commonColor = entity.Color;
                    commonLineWeight = entity.LineWeight;
                    commonLayerName = entity.Layer.Name;
                    commonLineTypeName = entity.LineType.Name;
                    commonLineTypeScale = entity.LineTypeScale;
                    commonTransparency = entity.Transparency;
                    commonIsInvisible = entity.IsInvisible;
                    if (entity is Solid firstSolid)
                    {
                        commonSolidThickness = firstSolid.Thickness;
                    }
                    else
                    {
                        allSelectedEntitiesAreSolids = false;
                    }
                    continue;
                }
                if (commonColor is ACadSharp.Color color &&
                    !color.Equals(entity.Color))
                {
                    commonColor = null;
                }
                if (commonLineWeight is LineWeightType lineWeight &&
                    lineWeight != entity.LineWeight)
                {
                    commonLineWeight = null;
                }
                if (commonLayerName is not null &&
                    !commonLayerName.Equals(
                        entity.Layer.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    commonLayerName = null;
                }
                if (commonLineTypeName is not null &&
                    !commonLineTypeName.Equals(
                        entity.LineType.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    commonLineTypeName = null;
                }
                if (commonLineTypeScale is double lineTypeScale &&
                    lineTypeScale != entity.LineTypeScale)
                {
                    commonLineTypeScale = null;
                }
                if (commonTransparency is Transparency transparency &&
                    transparency.Value != entity.Transparency.Value)
                {
                    commonTransparency = null;
                }
                if (commonIsInvisible is bool isInvisible &&
                    isInvisible != entity.IsInvisible)
                {
                    commonIsInvisible = null;
                }
                if (entity is not Solid solid)
                {
                    allSelectedEntitiesAreSolids = false;
                    commonSolidThickness = null;
                }
                else if (commonSolidThickness is double thickness &&
                    thickness != solid.Thickness)
                {
                    commonSolidThickness = null;
                }
            }
            return new CadSelectionGeneralProperties(
                _selectedHandleCount,
                commonColor,
                commonLineWeight,
                commonLayerName,
                commonLineTypeName,
                commonLineTypeScale,
                commonTransparency,
                commonIsInvisible,
                allSelectedEntitiesAreUnlocked,
                allSelectedEntitiesAreSolids,
                allSelectedEntitiesAreSolids ? commonSolidThickness : null);
        });
    }

    /// <summary>
    /// Captures bounded, deterministically ordered layer and linetype names
    /// without retaining their mutable table entries.
    /// </summary>
    public CadSelectionPropertyCatalog CaptureSelectionPropertyCatalog()
    {
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        return session.Capture((document, generation) =>
        {
            string[] layers = document.Layers
                .Select(static layer => layer.Name)
                .ToArray();
            string[] lineTypes = document.LineTypes
                .Select(static lineType => lineType.Name)
                .ToArray();
            ValidateSelectionPropertyCatalog(layers, "layer");
            ValidateSelectionPropertyCatalog(lineTypes, "linetype");
            Array.Sort(layers, StringComparer.OrdinalIgnoreCase);
            Array.Sort(lineTypes, StringComparer.OrdinalIgnoreCase);
            return new CadSelectionPropertyCatalog(
                generation,
                layers,
                lineTypes);
        });
    }

    /// <summary>Captures persisted state for one exact document layer name.</summary>
    public CadLayerGeneralProperties CaptureLayerGeneralProperties(string layerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        return session.Read(document =>
        {
            if (!document.Layers.TryGetValue(layerName, out Layer? layer))
            {
                throw new InvalidOperationException(
                    $"Document layer '{layerName}' does not exist.");
            }
            return new CadLayerGeneralProperties(
                layer.Name,
                layer.IsOn,
                layer.PlotFlag,
                (layer.Flags & LayerFlags.Frozen) != 0,
                (layer.Flags & LayerFlags.Locked) != 0,
                ReferenceEquals(document.Header.CurrentLayer, layer),
                layer.Name.Equals(
                    Layer.DefaultName,
                    StringComparison.OrdinalIgnoreCase),
                layer.Name.Equals(
                    Layer.DefpointsName,
                    StringComparison.OrdinalIgnoreCase),
                (layer.Flags & LayerFlags.XrefDependent) != 0,
                layer.Color,
                layer.LineWeight,
                layer.LineType.Name);
        });
    }

    /// <summary>Returns whether a persisted layer name is available for creation.</summary>
    public bool CanCreateLayer(string layerName)
    {
        CadDocumentSession? session = CurrentSession;
        return session is not null && session.Read(document =>
            CadLayerNameRules.IsValid(layerName, document.Header.Version) &&
            !document.Layers.Contains(layerName));
    }

    /// <summary>Returns whether one retained layer can be renamed to a new name.</summary>
    public bool CanRenameLayer(string layerName, string newName)
    {
        CadDocumentSession? session = CurrentSession;
        return session is not null && session.Read(document =>
        {
            if (!document.Layers.TryGetValue(layerName, out Layer? layer) ||
                layer.Name.Equals(
                    Layer.DefaultName,
                    StringComparison.OrdinalIgnoreCase) ||
                (layer.Flags & LayerFlags.XrefDependent) != 0 ||
                layer.Name.Equals(newName, StringComparison.OrdinalIgnoreCase) ||
                !CadLayerNameRules.IsValid(newName, document.Header.Version))
            {
                return false;
            }
            return !document.Layers.Contains(newName);
        });
    }

    /// <summary>
    /// Returns whether one source layer can be merged into one retained target.
    /// </summary>
    public bool CanMergeLayer(string sourceLayerName, string targetLayerName) =>
        CanMergeLayers([sourceLayerName], targetLayerName);

    /// <summary>
    /// Returns whether a bounded source set can be merged into one retained
    /// target. This O(S) host predicate for S sources omits the command's
    /// definitive registered-reference scan.
    /// </summary>
    public bool CanMergeLayers(
        IEnumerable<string> sourceLayerNames,
        string targetLayerName)
    {
        if (sourceLayerNames is null ||
            string.IsNullOrWhiteSpace(targetLayerName))
        {
            return false;
        }
        string[] bounded = sourceLayerNames
            .Take(CadMergeLayerCommand.MaximumSourceLayerCount + 1)
            .ToArray();
        if (bounded.Length == 0 ||
            bounded.Length > CadMergeLayerCommand.MaximumSourceLayerCount ||
            bounded.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }
        string[] sourceNames = bounded
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        CadDocumentSession? session = CurrentSession;
        return session is not null && session.Read(document =>
        {
            if (!document.Layers.TryGetValue(
                    targetLayerName,
                    out Layer? target) ||
                (target.Flags & LayerFlags.XrefDependent) != 0)
            {
                return false;
            }
            foreach (string sourceName in sourceNames)
            {
                if (!document.Layers.TryGetValue(sourceName, out Layer? source) ||
                    ReferenceEquals(source, target) ||
                    source.Name.Equals(
                        Layer.DefaultName,
                        StringComparison.OrdinalIgnoreCase) ||
                    source.Name.Equals(
                        Layer.DefpointsName,
                        StringComparison.OrdinalIgnoreCase) ||
                    ReferenceEquals(document.Header.CurrentLayer, source) ||
                    (source.Flags & LayerFlags.XrefDependent) != 0)
                {
                    return false;
                }
            }
            return true;
        });
    }

    /// <summary>
    /// Creates a layer by copying the selected template layer's editable state.
    /// The detached copy retains table names rather than mutable document objects.
    /// </summary>
    public bool CreateLayer(string layerName, string templateLayerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateLayerName);
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        Layer detached = session.Read(document =>
        {
            if (!document.Layers.TryGetValue(templateLayerName, out Layer? template))
            {
                throw new InvalidOperationException(
                    $"Document layer '{templateLayerName}' does not exist.");
            }
            return new Layer(layerName)
            {
                IsOn = template.IsOn,
                PlotFlag = template.PlotFlag,
                Flags = template.Flags &
                    (LayerFlags.Frozen |
                     LayerFlags.FrozenNewViewports |
                     LayerFlags.Locked),
                Color = template.Color,
                LineWeight = template.LineWeight,
                LineType = new LineType(template.LineType.Name),
            };
        });
        history.Execute(new CadAddLayerCommand(
            detached,
            $"Create layer {layerName} from {templateLayerName}"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Renames one retained document layer as one reversible edit.</summary>
    public bool RenameLayer(string layerName, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadRenameLayerCommand(
            layerName,
            newName,
            $"Rename layer {layerName} to {newName}"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Removes one preflighted unreferenced layer as one reversible edit.</summary>
    public bool RemoveLayer(string layerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadRemoveLayerCommand(
            layerName,
            $"Remove unused layer {layerName}"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>
    /// Reassigns every registered source-layer entity to the target and purges
    /// the source layer as one reversible edit.
    /// </summary>
    public bool MergeLayer(string sourceLayerName, string targetLayerName)
        => MergeLayers([sourceLayerName], targetLayerName);

    /// <summary>
    /// Reassigns every registered entity on any source layer to the target and
    /// purges all sources as one reversible edit.
    /// </summary>
    public bool MergeLayers(
        IEnumerable<string> sourceLayerNames,
        string targetLayerName)
    {
        ArgumentNullException.ThrowIfNull(sourceLayerNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLayerName);
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadMergeLayerCommand(
            sourceLayerNames,
            targetLayerName));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>
    /// Imports the supported definitions from one detached LIN library as one
    /// reversible document generation. Replace preserves registered linetype
    /// identity so existing layer and entity references remain exact.
    /// </summary>
    public CadLineTypeImportResult ImportLineTypes(
        CadLinFile file,
        CadLineTypeImportConflictPolicy conflictPolicy)
    {
        ArgumentNullException.ThrowIfNull(file);
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        CadImportLineTypesCommand command =
            CadImportLineTypesCommand.CaptureSupported(
                file,
                conflictPolicy,
                ShxFonts);
        ulong generation = history.Execute(command);
        RecompileAfterEdit(session);
        return new CadLineTypeImportResult(
            generation,
            command.ImportedCount,
            command.CreatedCount,
            command.ReplacedCount,
            command.UnsupportedCount);
    }

    /// <summary>
    /// Captures editable values when the complete selection is one INSERT.
    /// </summary>
    public CadAttributeValueCatalog? CaptureSelectedAttributeValueCatalog()
    {
        if (_selectedHandleCount != 1)
        {
            return null;
        }
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        try
        {
            return new CadAttributeValueCatalogCompiler().Compile(
                session,
                _selectedHandles[0]);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sets one selected INSERT attribute through its explicit ownership path.
    /// </summary>
    public bool SetSelectedAttributeValue(
        CadAttributeValueOwner owner,
        string tag,
        int occurrence,
        string value)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount != 1)
        {
            return false;
        }
        if (!Enum.IsDefined(owner))
        {
            throw new ArgumentOutOfRangeException(nameof(owner));
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        ulong insertHandle = _selectedHandles[0];
        CadEditCommand command = owner switch
        {
            CadAttributeValueOwner.Reference => new CadSetAttributeValueCommand(
                insertHandle,
                tag,
                value,
                occurrence,
                $"Set INSERT attribute '{tag}'"),
            CadAttributeValueOwner.Definition =>
                new CadSetConstantAttributeDefinitionValueCommand(
                    insertHandle,
                    tag,
                    value,
                    occurrence,
                    $"Set constant block attribute '{tag}'"),
            CadAttributeValueOwner.VariableDefinition =>
                new CadSetVariableAttributeDefinitionDefaultCommand(
                    insertHandle,
                    tag,
                    value,
                    occurrence,
                    $"Set variable block attribute default '{tag}'"),
            _ => throw new ArgumentOutOfRangeException(nameof(owner)),
        };
        history.Execute(command);
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>
    /// Sets one definition-owned ATTDEF prompt reached through the selected
    /// INSERT without changing any assigned ATTRIB value.
    /// </summary>
    public bool SetSelectedAttributeDefinitionPrompt(
        string tag,
        int occurrence,
        string prompt)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount != 1)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        var command = new CadSetAttributeDefinitionPromptCommand(
            _selectedHandles[0],
            tag,
            prompt,
            occurrence,
            $"Set block attribute prompt '{tag}'");
        history.Execute(command);
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>
    /// Sets one definition-owned ATTDEF tag reached through the selected
    /// INSERT without changing existing ATTRIB tags or assigned values.
    /// </summary>
    public bool SetSelectedAttributeDefinitionTag(
        string currentTag,
        int occurrence,
        string newTag)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount != 1)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        var command = new CadSetAttributeDefinitionTagCommand(
            _selectedHandles[0],
            currentTag,
            newTag,
            occurrence,
            $"Rename block attribute tag '{currentTag}'");
        history.Execute(command);
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>
    /// Sets the non-structural modes of one ATTDEF reached through the selected
    /// INSERT without changing constant or multiline ownership.
    /// </summary>
    public bool SetSelectedAttributeDefinitionModes(
        string tag,
        int occurrence,
        bool isInvisible,
        bool isVerifiable,
        bool isPreset,
        bool isPositionLocked)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount != 1)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        var command = new CadSetAttributeDefinitionModesCommand(
            _selectedHandles[0],
            tag,
            isInvisible,
            isVerifiable,
            isPreset,
            isPositionLocked,
            occurrence,
            $"Set block attribute modes '{tag}'");
        history.Execute(command);
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>
    /// Changes one selected ATTDEF between constant and variable ownership and
    /// synchronizes every retained reference to its block as one edit.
    /// </summary>
    public CadAttributeSynchronizationResult?
        SetSelectedAttributeDefinitionConstantMode(
            string tag,
            int occurrence,
            bool isConstant)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount != 1)
        {
            return null;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        var command = new CadSetAttributeDefinitionConstantModeCommand(
            _selectedHandles[0],
            tag,
            isConstant,
            occurrence,
            $"Set block attribute constant mode '{tag}'");
        ulong generation = history.Execute(command);
        RecompileAfterEdit(session);
        return new CadAttributeSynchronizationResult(
            generation,
            command.InsertCount,
            command.AttributeCount,
            command.AddedAttributeCount,
            command.RemovedAttributeCount,
            command.ClearedExtendedDataEntryCount);
    }

    /// <summary>
    /// Synchronizes definition-owned properties across every reference to the
    /// block selected through exactly one INSERT, preserving assigned values.
    /// </summary>
    public CadAttributeSynchronizationResult?
        SynchronizeSelectedBlockAttributeProperties()
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount != 1)
        {
            return null;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        var command = new CadSynchronizeBlockAttributePropertiesCommand(
            _selectedHandles[0]);
        ulong generation = history.Execute(command);
        RecompileAfterEdit(session);
        return new CadAttributeSynchronizationResult(
            generation,
            command.InsertCount,
            command.AttributeCount,
            command.AddedAttributeCount,
            command.RemovedAttributeCount,
            command.ClearedExtendedDataEntryCount);
    }

    /// <summary>Sets the complete selection to one CAD color as one edit.</summary>
    public bool SetSelectionColor(ACadSharp.Color color)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetEntityColorCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            color,
            _selectedHandleCount == 1
                ? "Set selected entity color"
                : $"Set {_selectedHandleCount} selected entity colors"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Sets the complete selection to one CAD lineweight as one edit.</summary>
    public bool SetSelectionLineWeight(LineWeightType lineWeight)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetEntityLineWeightCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            lineWeight,
            _selectedHandleCount == 1
                ? "Set selected entity lineweight"
                : $"Set {_selectedHandleCount} selected entity lineweights"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Assigns one existing layer to the complete selection.</summary>
    public bool SetSelectionLayer(string layerName)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetEntityLayerCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            layerName,
            _selectedHandleCount == 1
                ? "Set selected entity layer"
                : $"Set {_selectedHandleCount} selected entity layers"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Assigns one loaded linetype to the complete selection.</summary>
    public bool SetSelectionLineType(string lineTypeName)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetEntityLineTypeCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            lineTypeName,
            _selectedHandleCount == 1
                ? "Set selected entity linetype"
                : $"Set {_selectedHandleCount} selected entity linetypes"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Sets one positive linetype scale on the complete selection.</summary>
    public bool SetSelectionLineTypeScale(double lineTypeScale)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetEntityLineTypeScaleCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            lineTypeScale,
            _selectedHandleCount == 1
                ? "Set selected entity linetype scale"
                : $"Set {_selectedHandleCount} selected entity linetype scales"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Sets one authored transparency on the complete selection.</summary>
    public bool SetSelectionTransparency(Transparency transparency)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetEntityTransparencyCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            transparency,
            _selectedHandleCount == 1
                ? "Set selected entity transparency"
                : $"Set {_selectedHandleCount} selected entity transparencies"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Sets persisted visibility on the complete selection.</summary>
    public bool SetSelectionVisibility(bool isVisible)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetEntityVisibilityCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            isInvisible: !isVisible,
            _selectedHandleCount == 1
                ? "Set selected entity visibility"
                : $"Set {_selectedHandleCount} selected entity visibilities"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Sets one finite signed thickness on an all-SOLID selection.</summary>
    public bool SetSelectionSolidThickness(double thickness)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetSolidThicknessCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            thickness,
            _selectedHandleCount == 1
                ? "Set selected SOLID thickness"
                : $"Set {_selectedHandleCount} selected SOLID thicknesses"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Sets persisted display visibility for one document layer.</summary>
    public bool SetLayerVisibility(string layerName, bool isOn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetLayerVisibilityCommand(
            [layerName],
            isOn,
            $"Set layer {layerName} {(isOn ? "on" : "off")}"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Sets persisted plot eligibility for one document layer.</summary>
    public bool SetLayerPlotFlag(string layerName, bool isPlottable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetLayerPlotFlagCommand(
            [layerName],
            isPlottable,
            $"Set layer {layerName} {(isPlottable ? "plottable" : "non-plottable")}"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Sets persisted freeze state for one document layer.</summary>
    public bool SetLayerFreeze(string layerName, bool isFrozen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetLayerFreezeCommand(
            [layerName],
            isFrozen,
            $"Set layer {layerName} {(isFrozen ? "frozen" : "thawed")}"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Sets persisted edit-lock state for one document layer.</summary>
    public bool SetLayerLock(string layerName, bool isLocked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetLayerLockCommand(
            [layerName],
            isLocked,
            $"Set layer {layerName} {(isLocked ? "locked" : "unlocked")}"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Sets one explicit persisted color for one document layer.</summary>
    public bool SetLayerColor(string layerName, ACadSharp.Color color)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetLayerColorCommand(
            [layerName],
            color,
            $"Set layer {layerName} color"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Sets one fixed/default persisted lineweight for one document layer.</summary>
    public bool SetLayerLineWeight(string layerName, LineWeightType lineWeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetLayerLineWeightCommand(
            [layerName],
            lineWeight,
            $"Set layer {layerName} lineweight"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>Sets one drawing-resident explicit linetype for one document layer.</summary>
    public bool SetLayerLineType(string layerName, string lineTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lineTypeName);
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetLayerLineTypeCommand(
            [layerName],
            lineTypeName,
            $"Set layer {layerName} linetype"));
        RecompileAfterEdit(session);
        return true;
    }

    private static void ValidateSelectionPropertyCatalog(
        string[] names,
        string kind)
    {
        if (names.Length > MaxSelectionPropertyCatalogEntries)
        {
            throw new InvalidOperationException(
                $"The CAD {kind} catalog exceeds the supported " +
                $"{MaxSelectionPropertyCatalogEntries:N0}-entry limit.");
        }

        int characters = 0;
        foreach (string name in names)
        {
            if (name.Length > MaxSelectionPropertyCatalogCharacters - characters)
            {
                throw new InvalidOperationException(
                    $"The CAD {kind} catalog exceeds the supported " +
                    $"{MaxSelectionPropertyCatalogCharacters:N0}-character limit.");
            }
            characters += name.Length;
        }
    }

    /// <summary>
    /// Moves the complete semantic selection to the front or back of persisted
    /// model-space draw order as one reversible edit.
    /// </summary>
    public bool SetSelectionDrawOrder(CadDrawOrderPlacement placement)
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }
        if (placement is not
            (CadDrawOrderPlacement.BringToFront or
             CadDrawOrderPlacement.SendToBack))
        {
            throw new ArgumentOutOfRangeException(nameof(placement));
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        string action = placement == CadDrawOrderPlacement.BringToFront
            ? "Bring"
            : "Send";
        string destination = placement == CadDrawOrderPlacement.BringToFront
            ? "front"
            : "back";
        history.Execute(new CadSetModelSpaceDrawOrderCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            placement,
            description: _selectedHandleCount == 1
                ? $"{action} selected entity to {destination}"
                : $"{action} {_selectedHandleCount} selected entities to {destination}"));
        RecompileAfterEdit(session);
        return true;
    }

    /// <summary>
    /// Deletes all selected semantic model-space roots as one reversible edit.
    /// The selection is cleared because ACadSharp assigns new handles when Undo
    /// restores detached objects.
    /// </summary>
    public bool DeleteSelection()
    {
        ThrowIfDrawOrderReferencePickPending();
        if (_selectedHandleCount == 0)
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        int removedCount = _selectedHandleCount;
        history.Execute(new CadRemoveModelSpaceEntitiesCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, _selectedHandleCount),
            removedCount == 1
                ? "Delete selected entity"
                : $"Delete {removedCount} selected entities"));
        ResetSelectionState(notify: false);
        RecompileAfterEdit(session);
        return true;
    }

    public bool TryUndo()
    {
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentHistory? history = _history;
        CadDocumentSession? session = CurrentSession;
        if (history is null || session is null || !history.TryUndo(out _))
        {
            EditStateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        RecompileAfterEdit(session);
        return true;
    }

    public bool TryRedo()
    {
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentHistory? history = _history;
        CadDocumentSession? session = CurrentSession;
        if (history is null || session is null || !history.TryRedo(out _))
        {
            EditStateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        RecompileAfterEdit(session);
        return true;
    }

    private void RecompileAfterEdit(CadDocumentSession session)
    {
        try
        {
            CompileAndReplace(session, resetViewSelectionAndHistory: false);
        }
        catch
        {
            EditStateChanged?.Invoke(this, EventArgs.Empty);
            throw;
        }
    }

    private void CompleteSelection()
    {
        CadDocumentSnapshot? snapshot = CurrentSnapshot;
        if (snapshot is null)
        {
            return;
        }

        if (PendingDrawOrderPlacement is not null)
        {
            AccumulateDrawOrderReferences(snapshot);
            return;
        }

        CadBoundsSelectionMode mode = _hasSelectionDrag &&
            _selectionCurrent.X >= _pointerOrigin.X
            ? CadBoundsSelectionMode.Window
            : CadBoundsSelectionMode.Crossing;
        float inflation = _hasSelectionDrag ? 0.0f : PointSelectionTolerance;
        CadBounds3D selectionBounds = CreateViewport().CreatePlanSelectionBounds(
            _pointerOrigin,
            _selectionCurrent,
            inflation);
        CadBoundsSelectionQueryResult result = CadSelectionQuery.QueryExactBounds(
            snapshot,
            selectionBounds,
            mode,
            _selectionEntityScratch,
            _selectionCandidates,
            _selectionMatches,
            _selectionHandleScratch,
            _selectedHandles);

        _selectedHandleCount = result.HandleWrittenCount;
        _selectedHandleSet.Clear();
        for (int i = 0; i < _selectedHandleCount; i++)
        {
            _selectedHandleSet.Add(_selectedHandles[i]);
        }
        _lastUnsupportedPrimitiveCount = result.UnsupportedPrimitiveCount;
        _lastSelectionWasTruncated =
            result.AreCandidatesTruncated || result.AreHandlesTruncated;
        LastSelectionMode = mode;
        RefreshSelectionBounds(snapshot);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AccumulateDrawOrderReferences(CadDocumentSnapshot snapshot)
    {
        CadBoundsSelectionMode mode = _hasSelectionDrag &&
            _selectionCurrent.X >= _pointerOrigin.X
            ? CadBoundsSelectionMode.Window
            : CadBoundsSelectionMode.Crossing;
        float inflation = _hasSelectionDrag ? 0.0f : PointSelectionTolerance;
        CadBounds3D selectionBounds = CreateViewport().CreatePlanSelectionBounds(
            _pointerOrigin,
            _selectionCurrent,
            inflation);
        CadBoundsSelectionQueryResult result = CadSelectionQuery.QueryExactBounds(
            snapshot,
            selectionBounds,
            mode,
            _selectionEntityScratch,
            _selectionCandidates,
            _selectionMatches,
            _selectionHandleScratch,
            _drawOrderReferenceQueryHandles);

        for (int i = 0; i < result.HandleWrittenCount; i++)
        {
            ulong handle = _drawOrderReferenceQueryHandles[i];
            if (_selectedHandleSet.Contains(handle) ||
                !_drawOrderReferenceHandleSet.Add(handle))
            {
                continue;
            }
            if (_drawOrderReferenceHandleCount >= _drawOrderReferenceHandles.Length)
            {
                throw new InvalidOperationException(
                    "The draw-order reference buffer cannot represent the complete snapshot selection.");
            }
            _drawOrderReferenceHandles[_drawOrderReferenceHandleCount++] = handle;
        }

        LastDrawOrderReferenceUnsupportedPrimitiveCount =
            result.UnsupportedPrimitiveCount;
        LastDrawOrderReferenceSelectionWasTruncated =
            result.AreCandidatesTruncated || result.AreHandlesTruncated;
        LastDrawOrderReferenceSelectionMode = mode;
        RefreshDrawOrderReferenceBounds(snapshot);
        DrawOrderReferencePickChanged?.Invoke(this, EventArgs.Empty);
    }

    private CadPoint3D GetSelectionCenter()
    {
        if (_selectedBounds.IsEmpty)
        {
            throw new InvalidOperationException(
                "The selected CAD entities do not have retained finite bounds.");
        }

        return _selectedBounds.Center;
    }

    private void RefreshSelectionBounds(CadDocumentSnapshot snapshot)
    {
        _selectedBounds = CadBounds3D.Empty;
        foreach (CadEntityHeader entity in snapshot.Entities.Span)
        {
            if (_selectedHandleSet.Contains(entity.Handle))
            {
                _selectedBounds = _selectedBounds.Union(entity.Bounds);
            }
        }
    }

    private void RefreshDrawOrderReferenceBounds(CadDocumentSnapshot snapshot)
    {
        _drawOrderReferenceBounds = CadBounds3D.Empty;
        foreach (CadEntityHeader entity in snapshot.Entities.Span)
        {
            if (_drawOrderReferenceHandleSet.Contains(entity.Handle))
            {
                _drawOrderReferenceBounds =
                    _drawOrderReferenceBounds.Union(entity.Bounds);
            }
        }
    }

    private void ResetDrawOrderReferencePickState(bool notify)
    {
        PendingDrawOrderPlacement = null;
        _drawOrderReferenceHandleCount = 0;
        _drawOrderReferenceHandleSet.Clear();
        _drawOrderReferenceBounds = CadBounds3D.Empty;
        LastDrawOrderReferenceUnsupportedPrimitiveCount = 0;
        LastDrawOrderReferenceSelectionWasTruncated = false;
        LastDrawOrderReferenceSelectionMode = null;
        if (notify)
        {
            DrawOrderReferencePickChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ThrowIfDrawOrderReferencePickPending()
    {
        if (PendingDrawOrderPlacement is not null)
        {
            throw new InvalidOperationException(
                "Commit or cancel the pending draw-order reference selection first.");
        }
    }

    private void ResetSelectionState(bool notify)
    {
        _selectedHandleCount = 0;
        _selectedHandleSet.Clear();
        _lastUnsupportedPrimitiveCount = 0;
        _lastSelectionWasTruncated = false;
        LastSelectionMode = null;
        _selectedBounds = CadBounds3D.Empty;
        if (notify)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private CadPlanViewport CreateViewport()
    {
        CadPoint3D rebaseOrigin = CurrentSnapshot?.RebaseOrigin ?? CadPoint3D.Zero;
        return new CadPlanViewport(rebaseOrigin, Size, _pan, _zoom);
    }

    private void RefreshConstructionPicture()
    {
        CadDocumentSnapshot? snapshot = CurrentSnapshot;
        if (snapshot is null || Size.X <= 0.0f || Size.Y <= 0.0f)
        {
            _constructionPicture?.Dispose();
            _constructionPicture = null;
            _pointMarkerPicture?.Dispose();
            _pointMarkerPicture = null;
            return;
        }

        CadPlanViewport viewport = CreateViewport();
        GpuPicture? constructionReplacement = null;
        if (!snapshot.ConstructionLines.IsEmpty)
        {
            CadRecordedConstructionScene scene =
                new CadConstructionSceneCompiler().Compile(
                    snapshot,
                    viewport.CreatePlanClipBounds());
            constructionReplacement = scene.CreatePicture();
        }
        GpuPicture? previousConstruction = _constructionPicture;
        _constructionPicture = constructionReplacement;
        previousConstruction?.Dispose();

        CadRecordedPointMarkerScene markerScene =
            new CadPointMarkerSceneCompiler().Compile(
                snapshot,
                CadPointMarkerView.FromViewport(viewport));
        GpuPicture? markerReplacement = markerScene.Statistics.RecordedPointCount == 0
            ? null
            : markerScene.CreatePicture();
        GpuPicture? previousMarkers = _pointMarkerPicture;
        _pointMarkerPicture = markerReplacement;
        previousMarkers?.Dispose();
    }

    private static Rect ToScreenRect(
        CadPlanViewport viewport,
        CadBounds3D bounds)
    {
        Vector2 first = viewport.WorldToScreen(bounds.Min);
        Vector2 second = viewport.WorldToScreen(bounds.Max);
        return ToScreenRect(first, second);
    }

    private static Rect ToScreenRect(Vector2 first, Vector2 second) => new(
        Math.Min(first.X, second.X),
        Math.Min(first.Y, second.Y),
        Math.Abs(second.X - first.X),
        Math.Abs(second.Y - first.Y));

    private void ReleaseResources()
    {
        ReleasePointerCaptures();
        ResetDrawOrderReferencePickState(notify: false);
        _picture?.Dispose();
        _picture = null;
        _constructionPicture?.Dispose();
        _constructionPicture = null;
        _pointMarkerPicture?.Dispose();
        _pointMarkerPicture = null;
    }

    private static CadDocumentSession CreateRepresentativeDocument()
    {
        CadDocumentSession session = CadDocumentSession.CreateNew(ACadVersion.AC1032);
        session.Edit("Create representative CAD scene", document =>
        {
            ConfigureRepresentativePageSetup(
                document.Layouts[ACadLayout.ModelLayoutName],
                name: "ProGPU A3 landscape",
                paperWidthMillimeters: 420,
                paperHeightMillimeters: 297,
                rotation: PlotRotation.NoRotation);
            CadDictionary pageSetups =
                document.RootDictionary.GetEntry<CadDictionary>(
                    CadDictionary.AcadPlotSettings);
            var a4Portrait = new PlotSettings("A4 portrait");
            ConfigureRepresentativePageSetup(
                a4Portrait,
                name: "A4 portrait",
                paperWidthMillimeters: 210,
                paperHeightMillimeters: 297,
                rotation: PlotRotation.NoRotation);
            a4Portrait.Flags |= PlotFlags.ModelType;
            pageSetups.Add(a4Portrait);

            document.Entities.Add(new Line(new XYZ(-80, -45, 0), new XYZ(80, -45, 0)));
            document.Entities.Add(new Circle(new XYZ(-38, 8, 0), 27));
            document.Entities.Add(new Arc(new XYZ(30, 8, 0), 30, 0.2, 5.1));
            document.Entities.Add(new Ray
            {
                StartPoint = new XYZ(-38, 8, 0),
                Direction = new XYZ(1, 0.2, 0),
            });
            document.Entities.Add(new XLine
            {
                FirstPoint = new XYZ(30, 8, 0),
                Direction = new XYZ(-0.15, 1, 0),
            });
            document.Entities.Add(new Ellipse
            {
                Center = new XYZ(30, 8, 0),
                MajorAxisEndPoint = new XYZ(22, 9, 0),
                RadiusRatio = 0.38,
                StartParameter = 0.35,
                EndParameter = 5.65,
            });
            document.Entities.Add(new Solid(
                new XYZ(-9, -8, 0),
                new XYZ(7, -8, 0),
                new XYZ(-9, 4, 0),
                new XYZ(7, 4, 0))
            {
                Thickness = 8,
            });
            document.Entities.Add(new Face3D
            {
                FirstCorner = new XYZ(-6, 12, 0),
                SecondCorner = new XYZ(9, 15, 2),
                ThirdCorner = new XYZ(4, 29, 5),
                FourthCorner = new XYZ(-11, 25, 2),
                Flags = InvisibleEdgeFlags.Third,
            });

            var mesh = new Mesh();
            mesh.Vertices.AddRange([
                new XYZ(-18, 3, 0),
                new XYZ(-2, 3, 0),
                new XYZ(-2, 19, 0),
                new XYZ(-18, 19, 0),
                new XYZ(-18, 3, 16),
                new XYZ(-2, 3, 16),
                new XYZ(-2, 19, 16),
                new XYZ(-18, 19, 16),
            ]);
            mesh.Faces.AddRange([
                [0, 3, 2, 1],
                [4, 5, 6, 7],
                [0, 1, 5, 4],
                [1, 2, 6, 5],
                [2, 3, 7, 6],
                [3, 0, 4, 7],
            ]);
            document.Entities.Add(mesh);

            var polyline = new LwPolyline { IsClosed = true };
            polyline.Vertices.Add(new LwPolyline.Vertex(-72, -30));
            polyline.Vertices.Add(new LwPolyline.Vertex(-12, 42) { Bulge = -0.32 });
            polyline.Vertices.Add(new LwPolyline.Vertex(58, 35));
            polyline.Vertices.Add(new LwPolyline.Vertex(76, -22) { Bulge = 0.22 });
            document.Entities.Add(polyline);

            var spline = new Spline { Degree = 3 };
            spline.ControlPoints.AddRange([
                new XYZ(-75, -22, 0),
                new XYZ(-25, 68, 0),
                new XYZ(35, -68, 0),
                new XYZ(78, 18, 0),
            ]);
            spline.Knots.AddRange([0, 0, 0, 0, 1, 1, 1, 1]);
            document.Entities.Add(spline);

            var textStyle = new TextStyle("INTER") { Filename = "Inter.ttf" };
            document.TextStyles.Add(textStyle);
            document.Entities.Add(new TextEntity("%%uProGPU%%u %%oCAD%%o")
            {
                Style = textStyle,
                InsertPoint = new XYZ(-34, -31, 0),
                Height = 7,
                WidthFactor = 0.9,
                ObliqueAngle = 0.08,
            });

            var block = new BlockRecord("ANALYTIC_SYMBOL");
            block.BlockEntity.BasePoint = new XYZ(5, 5, 0);
            block.Entities.Add(new Circle(new XYZ(5, 5, 0), 5));
            block.Entities.Add(new Line(new XYZ(0, 5, 0), new XYZ(10, 5, 0)));
            document.Entities.Add(new Insert(block)
            {
                InsertPoint = new XYZ(45, -13, 0),
                XScale = 1.7,
                YScale = 0.8,
                Rotation = 0.42,
                ColumnCount = 2,
                ColumnSpacing = 16,
            });
        });
        return session;
    }

    private static void ConfigureRepresentativePageSetup(
        PlotSettings pageSetup,
        string name,
        double paperWidthMillimeters,
        double paperHeightMillimeters,
        PlotRotation rotation)
    {
        pageSetup.PageName = name;
        pageSetup.SystemPrinterName = "ProGPU retained preview";
        pageSetup.PaperSize = name;
        pageSetup.PaperWidth = paperWidthMillimeters;
        pageSetup.PaperHeight = paperHeightMillimeters;
        pageSetup.UnprintableMargin = new PaperMargin(5, 5, 5, 5);
        pageSetup.PlotOriginX = 0;
        pageSetup.PlotOriginY = 0;
        pageSetup.PaperUnits = PlotPaperUnits.Millimeters;
        pageSetup.PaperRotation = rotation;
        pageSetup.PlotType = PlotType.DrawingExtents;
        pageSetup.NumeratorScale = 1;
        pageSetup.DenominatorScale = 1;
        pageSetup.ScaledFit = ScaledType.ScaledToFit;
        pageSetup.ShadePlotMode = ShadePlotMode.Wireframe;
        pageSetup.StyleSheet = string.Empty;
        pageSetup.Flags |=
            PlotFlags.PrintLineweights |
            PlotFlags.PlotCentered |
            PlotFlags.UseStandardScale;
    }
}
