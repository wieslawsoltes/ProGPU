using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CSMath;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Vector;

namespace ProGPU.CAD.Sample;

/// <summary>Shared interactive retained CAD surface used by desktop and browser hosts.</summary>
public sealed class CadSampleCanvas : FrameworkElement
{
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
    private readonly CadSnapshotOptions _snapshotOptions;
    private readonly HashSet<ulong> _selectedHandleSet = new();
    private GpuPicture? _picture;
    private GpuPicture? _constructionPicture;
    private GpuPicture? _pointMarkerPicture;
    private CadDocumentHistory? _history;
    private CadBounds3D _bounds;
    private CadBounds3D _selectedBounds;
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
    private int _selectedHandleCount;
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

    public int LastUnsupportedPrimitiveCount => _lastUnsupportedPrimitiveCount;

    public bool LastSelectionWasTruncated => _lastSelectionWasTruncated;

    public CadBoundsSelectionMode? LastSelectionMode { get; private set; }

    public CadPlanViewport CurrentViewport => CreateViewport();

    public int UndoCount => _history?.UndoCount ?? 0;

    public int RedoCount => _history?.RedoCount ?? 0;

    public event EventHandler? SelectionChanged;

    public event EventHandler? EditStateChanged;

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
        ulong[] selectedHandles = !resetViewSelectionAndHistory &&
            _selectedHandles.Length >= selectionCapacity
            ? _selectedHandles
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
        _history = replacementHistory;
        _pan = replacementPan;
        if (resetViewSelectionAndHistory)
        {
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
        ResetSelectionState(notify: true);
        Invalidate();
    }

    /// <summary>Moves all selected semantic model-space entities as one edit.</summary>
    public bool TranslateSelection(CadPoint3D translation)
    {
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
    /// Rotates all selected semantic model-space entities around the complete
    /// selection-bounds center and the WCS positive Z axis as one edit.
    /// </summary>
    public bool RotateSelection(double radians)
    {
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
    /// Deletes all selected semantic model-space roots as one reversible edit.
    /// The selection is cleared because ACadSharp assigns new handles when Undo
    /// restores detached objects.
    /// </summary>
    public bool DeleteSelection()
    {
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
}
