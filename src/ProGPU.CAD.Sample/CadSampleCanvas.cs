using System.Globalization;
using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Header;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using ProGPU.Backend;
using ProGPU.Fonts.Inter;
using ProGPU.Scene;
using ProGPU.Vector;
using Windows.Graphics.Display;
using ACadLayout = ACadSharp.Objects.Layout;

namespace ProGPU.CAD.Sample;

/// <summary>Selected-set operation driven by two WCS plan-view points.</summary>
public enum CadPointTransformOperation : byte
{
    Move = 0,
    Copy = 1,
}

/// <summary>Observable stage of a bounded two-point transform interaction.</summary>
public enum CadPointTransformStage : byte
{
    AwaitingBasePoint = 0,
    AwaitingSecondPoint = 1,
    Completed = 2,
    Canceled = 3,
    Failed = 4,
}

/// <summary>
/// Immutable state transition for one shared desktop/browser point transform.
/// </summary>
public sealed class CadPointTransformChangedEventArgs : EventArgs
{
    public CadPointTransformOperation Operation { get; }

    public CadPointTransformStage Stage { get; }

    public CadPoint3D? BasePoint { get; }

    public CadPoint3D? SecondPoint { get; }

    public CadPoint3D? Displacement { get; }

    public string? ErrorMessage { get; }

    internal CadPointTransformChangedEventArgs(
        CadPointTransformOperation operation,
        CadPointTransformStage stage,
        CadPoint3D? basePoint = null,
        CadPoint3D? secondPoint = null,
        CadPoint3D? displacement = null,
        string? errorMessage = null)
    {
        Operation = operation;
        Stage = stage;
        BasePoint = basePoint;
        SecondPoint = secondPoint;
        Displacement = displacement;
        ErrorMessage = errorMessage;
    }
}

/// <summary>Observable stage of one shared desktop/browser LINE command.</summary>
public enum CadLineAuthoringStage : byte
{
    AwaitingFirstPoint = 0,
    AwaitingNextPoint = 1,
    SegmentUndone = 2,
    Completed = 3,
    Failed = 4,
}

/// <summary>Immutable transition emitted by the bounded LINE authoring state.</summary>
public sealed class CadLineAuthoringChangedEventArgs : EventArgs
{
    public CadLineAuthoringStage Stage { get; }

    public int SegmentCount { get; }

    public CadPoint3D? CurrentPoint { get; }

    public bool IsClosed { get; }

    public string? ErrorMessage { get; }

    internal CadLineAuthoringChangedEventArgs(
        CadLineAuthoringStage stage,
        int segmentCount,
        CadPoint3D? currentPoint = null,
        bool isClosed = false,
        string? errorMessage = null)
    {
        Stage = stage;
        SegmentCount = segmentCount;
        CurrentPoint = currentPoint;
        IsClosed = isClosed;
        ErrorMessage = errorMessage;
    }
}

/// <summary>Observable stage of one shared desktop/browser RAY command.</summary>
public enum CadRayAuthoringStage : byte
{
    AwaitingStartPoint = 0,
    AwaitingThroughPoint = 1,
    RayUndone = 2,
    Completed = 3,
    Failed = 4,
}

/// <summary>Immutable transition emitted by bounded RAY authoring.</summary>
public sealed class CadRayAuthoringChangedEventArgs : EventArgs
{
    public CadRayAuthoringStage Stage { get; }

    public int RayCount { get; }

    public CadPoint3D? StartPoint { get; }

    public string? ErrorMessage { get; }

    internal CadRayAuthoringChangedEventArgs(
        CadRayAuthoringStage stage,
        int rayCount,
        CadPoint3D? startPoint = null,
        string? errorMessage = null)
    {
        Stage = stage;
        RayCount = rayCount;
        StartPoint = startPoint;
        ErrorMessage = errorMessage;
    }
}

/// <summary>Observable stage of default two-point XLINE authoring.</summary>
public enum CadXLineAuthoringStage : byte
{
    AwaitingFirstPoint = 0,
    AwaitingThroughPoint = 1,
    LineUndone = 2,
    Completed = 3,
    Failed = 4,
    AwaitingInput = 5,
}

/// <summary>Immutable transition emitted by bounded XLINE authoring.</summary>
public sealed class CadXLineAuthoringChangedEventArgs : EventArgs
{
    public CadXLineAuthoringStage Stage { get; }

    public int LineCount { get; }

    public CadPoint3D? FirstPoint { get; }

    public string? ErrorMessage { get; }

    public CadXLineAuthoringMode Mode { get; }

    public CadXLinePromptKind Prompt { get; }

    internal CadXLineAuthoringChangedEventArgs(
        CadXLineAuthoringStage stage,
        int lineCount,
        CadPoint3D? firstPoint = null,
        string? errorMessage = null,
        CadXLineAuthoringMode mode = CadXLineAuthoringMode.TwoPoint,
        CadXLinePromptKind prompt = CadXLinePromptKind.FirstPoint)
    {
        Stage = stage;
        LineCount = lineCount;
        FirstPoint = firstPoint;
        ErrorMessage = errorMessage;
        Mode = mode;
        Prompt = prompt;
    }
}

/// <summary>Observable stage of one shared desktop/browser POINT command.</summary>
public enum CadPointAuthoringStage : byte
{
    AwaitingPoint = 0,
    Completed = 1,
    Canceled = 2,
    Failed = 3,
}

/// <summary>Immutable transition emitted by bounded POINT authoring.</summary>
public sealed class CadPointAuthoringChangedEventArgs : EventArgs
{
    public CadPointAuthoringStage Stage { get; }

    public CadPoint3D? Location { get; }

    public CadPointAuthoringSnapshot? Snapshot { get; }

    public string? ErrorMessage { get; }

    internal CadPointAuthoringChangedEventArgs(
        CadPointAuthoringStage stage,
        CadPoint3D? location = null,
        CadPointAuthoringSnapshot? snapshot = null,
        string? errorMessage = null)
    {
        Stage = stage;
        Location = location;
        Snapshot = snapshot;
        ErrorMessage = errorMessage;
    }
}

/// <summary>Observable stage of one shared desktop/browser PLINE command.</summary>
public enum CadPolylineAuthoringStage : byte
{
    AwaitingFirstPoint = 0,
    AwaitingNextPoint = 1,
    ModeChanged = 2,
    SegmentUndone = 3,
    Completed = 4,
    Failed = 5,
    PromptChanged = 6,
}

/// <summary>Immutable transition emitted by bounded PLINE authoring.</summary>
public sealed class CadPolylineAuthoringChangedEventArgs : EventArgs
{
    public CadPolylineAuthoringStage Stage { get; }

    public CadPolylineAuthoringMode Mode { get; }

    public int SegmentCount { get; }

    public CadPoint3D? CurrentPoint { get; }

    public bool IsClosed { get; }

    public string? ErrorMessage { get; }

    public CadPolylineAuthoringPrompt Prompt { get; }

    public CadPolylineWidthInputMode WidthInputMode { get; }

    public double NextStartWidth { get; }

    public double NextEndWidth { get; }

    internal CadPolylineAuthoringChangedEventArgs(
        CadPolylineAuthoringStage stage,
        CadPolylineAuthoringMode mode,
        int segmentCount,
        CadPoint3D? currentPoint = null,
        bool isClosed = false,
        string? errorMessage = null,
        CadPolylineAuthoringPrompt prompt = CadPolylineAuthoringPrompt.Point,
        CadPolylineWidthInputMode widthInputMode = CadPolylineWidthInputMode.Width,
        double nextStartWidth = 0.0,
        double nextEndWidth = 0.0)
    {
        Stage = stage;
        Mode = mode;
        SegmentCount = segmentCount;
        CurrentPoint = currentPoint;
        IsClosed = isClosed;
        ErrorMessage = errorMessage;
        Prompt = prompt;
        WidthInputMode = widthInputMode;
        NextStartWidth = nextStartWidth;
        NextEndWidth = nextEndWidth;
    }
}

/// <summary>Observable stage of one shared desktop/browser CIRCLE command.</summary>
public enum CadCircleAuthoringStage : byte
{
    AwaitingFirstPoint = 0,
    AwaitingNextPoint = 1,
    Completed = 2,
    Canceled = 3,
    Failed = 4,
}

/// <summary>Immutable transition emitted by bounded CIRCLE authoring.</summary>
public sealed class CadCircleAuthoringChangedEventArgs : EventArgs
{
    public CadCircleAuthoringStage Stage { get; }

    public CadCircleAuthoringMode Mode { get; }

    public int PointCount { get; }

    public CadPoint3D? CurrentPoint { get; }

    public CadCircleAuthoringSnapshot? Snapshot { get; }

    public string? ErrorMessage { get; }

    internal CadCircleAuthoringChangedEventArgs(
        CadCircleAuthoringStage stage,
        CadCircleAuthoringMode mode,
        int pointCount,
        CadPoint3D? currentPoint = null,
        CadCircleAuthoringSnapshot? snapshot = null,
        string? errorMessage = null)
    {
        Stage = stage;
        Mode = mode;
        PointCount = pointCount;
        CurrentPoint = currentPoint;
        Snapshot = snapshot;
        ErrorMessage = errorMessage;
    }
}

/// <summary>Observable stage of one shared desktop/browser ARC command.</summary>
public enum CadArcAuthoringStage : byte
{
    AwaitingFirstPoint = 0,
    AwaitingNextInput = 1,
    Completed = 2,
    Canceled = 3,
    Failed = 4,
}

/// <summary>Immutable transition emitted by bounded ARC authoring.</summary>
public sealed class CadArcAuthoringChangedEventArgs : EventArgs
{
    public CadArcAuthoringStage Stage { get; }

    public CadArcAuthoringMode Mode { get; }

    public int PointCount { get; }

    public CadPoint3D? CurrentPoint { get; }

    public CadArcAuthoringSnapshot? Snapshot { get; }

    public string? ErrorMessage { get; }

    internal CadArcAuthoringChangedEventArgs(
        CadArcAuthoringStage stage,
        CadArcAuthoringMode mode,
        int pointCount,
        CadPoint3D? currentPoint = null,
        CadArcAuthoringSnapshot? snapshot = null,
        string? errorMessage = null)
    {
        Stage = stage;
        Mode = mode;
        PointCount = pointCount;
        CurrentPoint = currentPoint;
        Snapshot = snapshot;
        ErrorMessage = errorMessage;
    }
}

/// <summary>Observable stage of one shared desktop/browser ELLIPSE command.</summary>
public enum CadEllipseAuthoringStage : byte
{
    AwaitingFirstPoint = 0,
    AwaitingNextInput = 1,
    Completed = 2,
    Canceled = 3,
    Failed = 4,
}

/// <summary>Immutable transition emitted by bounded ELLIPSE authoring.</summary>
public sealed class CadEllipseAuthoringChangedEventArgs : EventArgs
{
    public CadEllipseAuthoringStage Stage { get; }

    public CadEllipseAuthoringMode Mode { get; }

    public CadEllipseArcInputMode ArcInputMode { get; }

    public CadEllipseAuthoringInputKind InputKind { get; }

    public int AcceptedInputCount { get; }

    public CadPoint3D? CurrentPoint { get; }

    public CadEllipseAuthoringSnapshot? Snapshot { get; }

    public string? ErrorMessage { get; }

    internal CadEllipseAuthoringChangedEventArgs(
        CadEllipseAuthoringStage stage,
        CadEllipseAuthoringMode mode,
        CadEllipseArcInputMode arcInputMode,
        CadEllipseAuthoringInputKind inputKind,
        int acceptedInputCount,
        CadPoint3D? currentPoint = null,
        CadEllipseAuthoringSnapshot? snapshot = null,
        string? errorMessage = null)
    {
        Stage = stage;
        Mode = mode;
        ArcInputMode = arcInputMode;
        InputKind = inputKind;
        AcceptedInputCount = acceptedInputCount;
        CurrentPoint = currentPoint;
        Snapshot = snapshot;
        ErrorMessage = errorMessage;
    }
}

/// <summary>Observable stage of one shared desktop/browser POLYGON command.</summary>
public enum CadPolygonAuthoringStage : byte
{
    AwaitingFirstPoint = 0,
    AwaitingFinalInput = 1,
    Completed = 2,
    Canceled = 3,
    Failed = 4,
}

/// <summary>Immutable transition emitted by bounded POLYGON authoring.</summary>
public sealed class CadPolygonAuthoringChangedEventArgs : EventArgs
{
    public CadPolygonAuthoringStage Stage { get; }

    public int SideCount { get; }

    public CadPolygonAuthoringMode Mode { get; }

    public CadPolygonAuthoringInputKind InputKind { get; }

    public int AcceptedInputCount { get; }

    public CadPoint3D? CurrentPoint { get; }

    public CadPolygonAuthoringSnapshot? Snapshot { get; }

    public string? ErrorMessage { get; }

    internal CadPolygonAuthoringChangedEventArgs(
        CadPolygonAuthoringStage stage,
        int sideCount,
        CadPolygonAuthoringMode mode,
        CadPolygonAuthoringInputKind inputKind,
        int acceptedInputCount,
        CadPoint3D? currentPoint = null,
        CadPolygonAuthoringSnapshot? snapshot = null,
        string? errorMessage = null)
    {
        Stage = stage;
        SideCount = sideCount;
        Mode = mode;
        InputKind = inputKind;
        AcceptedInputCount = acceptedInputCount;
        CurrentPoint = currentPoint;
        Snapshot = snapshot;
        ErrorMessage = errorMessage;
    }
}

/// <summary>Observable stage of one shared desktop/browser RECTANG command.</summary>
public enum CadRectangleAuthoringStage : byte
{
    AwaitingFirstCorner = 0,
    AwaitingPlacement = 1,
    Completed = 2,
    Canceled = 3,
    Failed = 4,
}

/// <summary>Immutable transition emitted by bounded RECTANG authoring.</summary>
public sealed class CadRectangleAuthoringChangedEventArgs : EventArgs
{
    public CadRectangleAuthoringStage Stage { get; }

    public CadRectangleConstruction Construction { get; }

    public CadRectangleCornerTreatment CornerTreatment { get; }

    public double RotationRadians { get; }

    public CadRectangleAuthoringInputKind InputKind { get; }

    public int AcceptedInputCount { get; }

    public CadPoint3D? CurrentPoint { get; }

    public CadRectangleAuthoringSnapshot? Snapshot { get; }

    public string? ErrorMessage { get; }

    internal CadRectangleAuthoringChangedEventArgs(
        CadRectangleAuthoringStage stage,
        CadRectangleConstruction construction,
        CadRectangleCornerTreatment cornerTreatment,
        double rotationRadians,
        CadRectangleAuthoringInputKind inputKind,
        int acceptedInputCount,
        CadPoint3D? currentPoint = null,
        CadRectangleAuthoringSnapshot? snapshot = null,
        string? errorMessage = null)
    {
        Stage = stage;
        Construction = construction;
        CornerTreatment = cornerTreatment;
        RotationRadians = rotationRadians;
        InputKind = inputKind;
        AcceptedInputCount = acceptedInputCount;
        CurrentPoint = currentPoint;
        Snapshot = snapshot;
        ErrorMessage = errorMessage;
    }
}

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
    private const double PolylinePreviewMaximumPhysicalError = 0.25;
    private const int PolylinePreviewMaximumStepCount = 512;

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
    private readonly Brush _gridBrush = new ThemeResourceBrush("TextSecondary")
    {
        Opacity = 0.45f,
    };
    private readonly CadSnapshotOptions _snapshotOptions;
    private readonly HashSet<ulong> _selectedHandleSet = new();
    private readonly HashSet<ulong> _drawOrderReferenceHandleSet = new();
    private GpuPicture? _picture;
    private GpuPicture? _constructionPicture;
    private GpuPicture? _pointMarkerPicture;
    private GpuPicture? _lineAuthoringPicture;
    private GpuPicture? _rayAuthoringPicture;
    private GpuPicture? _xlineAuthoringPicture;
    private GpuPicture? _polylineAuthoringPicture;
    private GpuPicture? _polygonAuthoringPicture;
    private CadDocumentHistory? _history;
    private CadLineAuthoringSession? _lineAuthoring;
    private CadRayAuthoringSession? _rayAuthoring;
    private CadXLineModeAuthoringSession? _xlineAuthoring;
    private CadPointAuthoringSession? _pointAuthoring;
    private CadPolylineAuthoringSession? _polylineAuthoring;
    private CadCircleAuthoringSession? _circleAuthoring;
    private CadArcAuthoringSession? _arcAuthoring;
    private CadEllipseAuthoringSession? _ellipseAuthoring;
    private CadPolygonAuthoringSession? _polygonAuthoring;
    private CadRectangleAuthoringSession? _rectangleAuthoring;
    private double _rectangleRotationRadians;
    private CadRectangleCornerTreatment _rectangleCornerTreatment;
    private CadBounds3D _bounds;
    private CadBounds3D _selectedBounds;
    private CadBounds3D _drawOrderReferenceBounds;
    private Vector2 _pan;
    private Vector2 _pointerOrigin;
    private Vector2 _panOrigin;
    private Vector2 _selectionCurrent;
    private Vector2 _pointTransformCurrent;
    private Vector2 _pointTransformPointerPosition;
    private CadPoint3D _pointTransformBasePoint;
    private CadPoint3D _pointTransformGridSnap;
    private CadPlanOrthoResult _pointTransformOrtho;
    private CadPlanPolarTrackingResult _pointTransformPolarTracking;
    private CadObjectSnapResult _pointTransformObjectSnap;
    private CadPlanGridSnapSettings _planGridSnapSettings =
        CadPlanGridSnapSettings.Disabled;
    private CadPlanGridDisplaySettings _planGridDisplaySettings =
        CadPlanGridDisplaySettings.Hidden;
    private CadPlanGridPresentationStyle _planGridPresentationStyle =
        CadPlanGridPresentationStyle.Lines;
    private CadPlanPolarTrackingSettings _planPolarTrackingSettings =
        CadPlanPolarTrackingSettings.Disabled;
    private CadPlanPolarSnapSettings _planPolarSnapSettings =
        CadPlanPolarSnapSettings.Disabled;
    private CadPlanSnapType _planSnapType = CadPlanSnapType.Grid;
    private CadObjectSnapModes _objectSnapModes = CadObjectSnapModes.Standard;
    private float _zoom = 1;
    private int[] _selectionEntityScratch = [];
    private int[] _selectionHandleScratch = [];
    private CadSelectionCandidate[] _selectionCandidates = [];
    private CadSelectionCandidate[] _selectionMatches = [];
    private CadSelectionCandidate _xlineSourceCandidate;
    private bool _hasXLineSourceCandidate;
    private ulong[] _selectedHandles = [];
    private ulong[] _drawOrderReferenceHandles = [];
    private ulong[] _drawOrderReferenceQueryHandles = [];
    private int _selectedHandleCount;
    private int _drawOrderReferenceHandleCount;
    private int _lastUnsupportedPrimitiveCount;
    private bool _lastSelectionWasTruncated;
    private bool _isPanning;
    private bool _isSelecting;
    private bool _isPointTransformPointerPressed;
    private bool _hasPointTransformPointerPosition;
    private bool _hasPointTransformBasePoint;
    private bool _hasPointTransformGridSnap;
    private bool _hasPointTransformOrtho;
    private bool _hasPointTransformPolarTracking;
    private bool _isPlanOrthoEnabled;
    private bool _hasSelectionDrag;
    private bool _needsFit = true;

    private const float SelectionDragThreshold = 4.0f;
    private const float PointSelectionTolerance = 5.0f;
    private const float PointTransformObjectSnapAperture = 10.0f;
    private const float PointTransformPolarTrackingAperture = 10.0f;
    private const float ObjectSnapMarkerRadius = 5.0f;

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

    public CadPointTransformOperation? PendingPointTransformOperation
    {
        get;
        private set;
    }

    public CadPointTransformStage? PendingPointTransformStage =>
        PendingPointTransformOperation is null
            ? null
            : _hasPointTransformBasePoint
                ? CadPointTransformStage.AwaitingSecondPoint
                : CadPointTransformStage.AwaitingBasePoint;

    public CadPoint3D? PendingPointTransformBasePoint =>
        _hasPointTransformBasePoint ? _pointTransformBasePoint : null;

    /// <summary>Whether one bounded model-space LINE sequence is active.</summary>
    public bool IsLineAuthoring => _lineAuthoring is not null;

    public int PendingLineSegmentCount => _lineAuthoring?.SegmentCount ?? 0;

    public bool CanCloseLineAuthoring => _lineAuthoring?.CanClose == true;

    public CadPoint3D? PendingLineFirstPoint => _lineAuthoring?.FirstPoint;

    public CadPoint3D? PendingLineCurrentPoint => _lineAuthoring?.CurrentPoint;

    /// <summary>Whether one bounded common-start model-space RAY sequence is active.</summary>
    public bool IsRayAuthoring => _rayAuthoring is not null;

    public int PendingRayCount => _rayAuthoring?.RayCount ?? 0;

    public CadPoint3D? PendingRayStartPoint => _rayAuthoring?.StartPoint;

    public bool IsXLineAuthoring => _xlineAuthoring is not null;

    public int PendingXLineCount => _xlineAuthoring?.LineCount ?? 0;

    public CadPoint3D? PendingXLineFirstPoint => _xlineAuthoring?.FirstPoint;

    public CadXLineAuthoringMode? PendingXLineMode => _xlineAuthoring?.Mode;

    public CadXLinePromptKind? PendingXLinePrompt => _xlineAuthoring?.Prompt;

    /// <summary>Whether one single-location model-space POINT is active.</summary>
    public bool IsPointAuthoring => _pointAuthoring is not null;

    /// <summary>Whether one bounded model-space PLINE is active.</summary>
    public bool IsPolylineAuthoring => _polylineAuthoring is not null;

    public int PendingPolylineSegmentCount =>
        _polylineAuthoring?.SegmentCount ?? 0;

    public bool CanClosePolylineAuthoring =>
        _polylineAuthoring?.CanClose == true;

    public bool CanBeginPolylineWidthInput =>
        _polylineAuthoring?.CanBeginWidthInput == true;

    public bool CanBeginPolylineLengthInput =>
        _polylineAuthoring?.CanBeginLengthInput == true;

    public bool CanUndoPolylineAuthoring =>
        _polylineAuthoring?.CanUndo == true;

    public CadPoint3D? PendingPolylineFirstPoint =>
        _polylineAuthoring?.FirstPoint;

    public CadPoint3D? PendingPolylineCurrentPoint =>
        _polylineAuthoring?.CurrentPoint;

    public CadPolylineAuthoringPrompt PendingPolylinePrompt =>
        _polylineAuthoring?.Prompt ?? CadPolylineAuthoringPrompt.Point;

    public CadPolylineWidthInputMode PendingPolylineWidthInputMode =>
        _polylineAuthoring?.WidthInputMode ?? CadPolylineWidthInputMode.Width;

    public double PendingPolylineNextStartWidth =>
        _polylineAuthoring?.NextStartWidth ?? 0.0;

    public double PendingPolylineNextEndWidth =>
        _polylineAuthoring?.NextEndWidth ?? 0.0;

    /// <summary>Whether one bounded plan-view CIRCLE is active.</summary>
    public bool IsCircleAuthoring => _circleAuthoring is not null;

    public CadCircleAuthoringMode? PendingCircleAuthoringMode =>
        _circleAuthoring?.Mode;

    public int PendingCirclePointCount => _circleAuthoring?.PointCount ?? 0;

    public CadPoint3D? PendingCircleFirstPoint => _circleAuthoring?.FirstPoint;

    public CadPoint3D? PendingCircleCurrentPoint => _circleAuthoring?.CurrentPoint;

    /// <summary>Whether one bounded plan-view ARC is active.</summary>
    public bool IsArcAuthoring => _arcAuthoring is not null;

    public CadArcAuthoringMode? PendingArcAuthoringMode =>
        _arcAuthoring?.Mode;

    public CadArcScalarInputKind PendingArcScalarInputKind =>
        _arcAuthoring?.ScalarInputKind ?? CadArcScalarInputKind.None;

    public int PendingArcPointCount => _arcAuthoring?.PointCount ?? 0;

    public CadPoint3D? PendingArcFirstPoint => _arcAuthoring?.FirstPoint;

    public CadPoint3D? PendingArcCurrentPoint => _arcAuthoring?.CurrentPoint;

    /// <summary>Whether one bounded plan-view ELLIPSE is active.</summary>
    public bool IsEllipseAuthoring => _ellipseAuthoring is not null;

    public CadEllipseAuthoringMode? PendingEllipseAuthoringMode =>
        _ellipseAuthoring?.Mode;

    public CadEllipseArcInputMode? PendingEllipseArcInputMode =>
        _ellipseAuthoring?.ArcInputMode;

    public CadEllipseAuthoringInputKind? PendingEllipseInputKind =>
        _ellipseAuthoring?.InputKind;

    public int PendingEllipseAcceptedInputCount =>
        _ellipseAuthoring?.AcceptedInputCount ?? 0;

    public int PendingEllipsePointCount =>
        _ellipseAuthoring?.PointCount ?? 0;

    public CadPoint3D? PendingEllipseCurrentPoint =>
        _ellipseAuthoring?.CurrentPoint;

    /// <summary>Whether one bounded regular plan-view POLYGON is active.</summary>
    public bool IsPolygonAuthoring => _polygonAuthoring is not null;

    public int PendingPolygonSideCount =>
        _polygonAuthoring?.SideCount ?? 0;

    public CadPolygonAuthoringMode? PendingPolygonAuthoringMode =>
        _polygonAuthoring?.Mode;

    public CadPolygonAuthoringInputKind? PendingPolygonInputKind =>
        _polygonAuthoring?.InputKind;

    public int PendingPolygonAcceptedInputCount =>
        _polygonAuthoring?.AcceptedInputCount ?? 0;

    public CadPoint3D? PendingPolygonCurrentPoint =>
        _polygonAuthoring?.CurrentPoint;

    /// <summary>Whether one bounded plan-view RECTANG is active.</summary>
    public bool IsRectangleAuthoring => _rectangleAuthoring is not null;

    public CadRectangleConstruction? PendingRectangleConstruction =>
        _rectangleAuthoring?.Construction;

    public CadRectangleAuthoringInputKind? PendingRectangleInputKind =>
        _rectangleAuthoring?.InputKind;

    public int PendingRectangleAcceptedInputCount =>
        _rectangleAuthoring?.AcceptedInputCount ?? 0;

    public CadPoint3D? PendingRectangleCurrentPoint =>
        _rectangleAuthoring?.CurrentPoint;

    /// <summary>Profile-scoped RECTANG rotation retained across commands.</summary>
    public double RectangleRotationRadians => _rectangleRotationRadians;

    /// <summary>Profile-scoped RECTANG corner treatment retained across commands.</summary>
    public CadRectangleCornerTreatment RectangleCornerTreatment =>
        _rectangleCornerTreatment;

    public CadPolylineAuthoringMode PolylineAuthoringMode
    {
        get => _polylineAuthoring?.Mode ?? CadPolylineAuthoringMode.Line;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            CadPolylineAuthoringSession? authoring = _polylineAuthoring;
            if (authoring is null || authoring.Mode == value)
            {
                return;
            }
            authoring.Mode = value;
            if (_hasPointTransformPointerPosition)
            {
                UpdatePointTransformPointer(_pointTransformPointerPosition);
            }
            RaisePolylineAuthoringChanged(
                CadPolylineAuthoringStage.ModeChanged,
                authoring);
            Invalidate();
        }
    }

    private bool IsPointAcquisitionActive =>
        PendingPointTransformOperation is not null ||
        _lineAuthoring is not null ||
        _rayAuthoring is not null ||
        _xlineAuthoring is not null ||
        _pointAuthoring is not null ||
        _polylineAuthoring is not null ||
        _circleAuthoring is not null ||
        _arcAuthoring is not null ||
        _ellipseAuthoring is not null ||
        _polygonAuthoring is not null ||
        _rectangleAuthoring is not null;

    /// <summary>Running object-snap modes used by MOVE/COPY point prompts.</summary>
    public CadObjectSnapModes ObjectSnapModes
    {
        get => _objectSnapModes;
        set
        {
            if ((value & ~(CadObjectSnapModes.Standard |
                           CadObjectSnapModes.Perpendicular |
                           CadObjectSnapModes.Tangent |
                           CadObjectSnapModes.Nearest)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (_objectSnapModes == value)
            {
                return;
            }

            _objectSnapModes = value;
            if (IsPointAcquisitionActive &&
                _hasPointTransformPointerPosition)
            {
                UpdatePointTransformPointer(_pointTransformPointerPosition);
            }
            else
            {
                _pointTransformObjectSnap = default;
            }
            Invalidate();
        }
    }

    /// <summary>The exact generation-tagged snap currently shown by the prompt.</summary>
    public CadObjectSnapResult? PendingPointTransformObjectSnap =>
        _pointTransformObjectSnap.IsSnapped
            ? _pointTransformObjectSnap
            : null;

    /// <summary>The active viewport's immutable point-grid settings.</summary>
    public CadPlanGridSnapSettings PlanGridSnapSettings =>
        _planGridSnapSettings;

    /// <summary>The active viewport's immutable drafting-grid display.</summary>
    public CadPlanGridDisplaySettings PlanGridDisplaySettings =>
        _planGridDisplaySettings;

    /// <summary>
    /// Gets or sets the host-level model-space grid style. AutoCAD stores the
    /// equivalent GRIDSTYLE bit in its profile registry rather than DXF/DWG.
    /// </summary>
    public CadPlanGridPresentationStyle PlanGridPresentationStyle
    {
        get => _planGridPresentationStyle;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (_planGridPresentationStyle == value)
            {
                return;
            }
            _planGridPresentationStyle = value;
            Invalidate();
        }
    }

    /// <summary>
    /// Returns the active VPORT's exact persisted GRIDMODE, SNAPUNIT, GRIDUNIT,
    /// GRIDDISPLAY, GRIDMAJOR, SNAPSTYL, and SNAPISOPAIR values.
    /// </summary>
    public CadPlanGridDisplayEditValues GetPlanGridDisplayEditValues()
    {
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        return session.Read(CadPlanGridDisplayEditValues.Capture);
    }

    /// <summary>
    /// Persists one active-VPORT drafting-grid display edit and recompiles one
    /// immutable generation. Point-grid acquisition remains unchanged.
    /// </summary>
    public void EditPlanGridDisplay(CadPlanGridDisplayEditValues values)
    {
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetPlanGridDisplayCommand(values));
        RecompileAfterEdit(session);
    }

    /// <summary>
    /// Cycles drawing-persisted SNAPISOPAIR Left, Top, and Right as one edit.
    /// The current snap style is retained; a rectangular drawing therefore
    /// remembers the selected plane without changing its active grid basis.
    /// </summary>
    public CadPlanIsoplane CyclePlanIsoplane()
    {
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        var command = new CadCyclePlanIsoplaneCommand();
        history.Execute(command);
        RecompileAfterEdit(session);
        return command.AppliedIsoplane ?? throw new InvalidOperationException(
            "The isoplane command did not produce an applied plane.");
    }

    /// <summary>
    /// Enables rectangular grid acquisition for pointer-driven MOVE/COPY prompts.
    /// </summary>
    public bool IsPlanGridSnapEnabled
    {
        get => _planSnapType == CadPlanSnapType.Grid &&
            _planGridSnapSettings.IsEnabled;
        set
        {
            if (!value && _planSnapType != CadPlanSnapType.Grid)
            {
                return;
            }
            SetPlanSnapState(CadPlanSnapType.Grid, value);
        }
    }

    /// <summary>Current interaction Snap Mode state, independent of SNAPTYPE.</summary>
    public bool IsPlanSnapEnabled
    {
        get => _planGridSnapSettings.IsEnabled ||
            _planPolarSnapSettings.IsEnabled;
        set => SetPlanSnapState(_planSnapType, value);
    }

    /// <summary>Current profile-scoped Grid or Polar SNAPTYPE choice.</summary>
    public CadPlanSnapType PlanSnapType
    {
        get => _planSnapType;
        set => SetPlanSnapState(value, IsPlanSnapEnabled);
    }

    /// <summary>The drawing-persisted active-VPORT SNAPMODE value.</summary>
    public bool PersistedPlanSnapMode =>
        CurrentSession?.Read(document =>
            document.VPorts[VPort.DefaultName].SnapOn) ?? false;

    /// <summary>
    /// Persists active-VPORT SNAPMODE as one reversible edit and synchronizes
    /// the current Grid or Polar interaction snap from the new snapshot.
    /// </summary>
    public void SetPlanSnapMode(bool isEnabled)
    {
        ThrowIfDrawOrderReferenceSelectionPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetPlanSnapModeCommand(isEnabled));
        RecompileAfterEdit(session, synchronizePlanSnapMode: true);
    }

    /// <summary>The exact grid point currently shown by the point prompt.</summary>
    public CadPoint3D? PendingPointTransformGridSnap =>
        _hasPointTransformGridSnap
            ? _pointTransformGridSnap
            : null;

    /// <summary>
    /// Gets or sets the current interaction Ortho state. Direct assignment is
    /// a session override; <see cref="SetPlanOrthoMode"/> persists ORTHOMODE.
    /// </summary>
    public bool IsPlanOrthoEnabled
    {
        get => _isPlanOrthoEnabled;
        set
        {
            if (_isPlanOrthoEnabled == value)
            {
                return;
            }

            _isPlanOrthoEnabled = value;
            if (value && _planPolarTrackingSettings.IsEnabled)
            {
                _planPolarTrackingSettings =
                    _planPolarTrackingSettings.WithEnabled(false);
            }
            if (IsPointAcquisitionActive &&
                _hasPointTransformPointerPosition)
            {
                UpdatePointTransformPointer(_pointTransformPointerPosition);
            }
            else
            {
                _hasPointTransformOrtho = false;
            }
            Invalidate();
        }
    }

    /// <summary>The drawing-persisted ORTHOMODE value.</summary>
    public bool PersistedPlanOrthoMode =>
        CurrentSession?.Read(document => document.Header.OrthoMode) ?? false;

    /// <summary>
    /// Persists ORTHOMODE as one reversible edit and synchronizes the current
    /// interaction constraint from the replacement immutable snapshot.
    /// </summary>
    public void SetPlanOrthoMode(bool isEnabled)
    {
        ThrowIfDrawOrderReferenceSelectionPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadSetOrthoModeCommand(isEnabled));
        RecompileAfterEdit(session, synchronizePlanOrthoMode: true);
    }

    public CadPlanOrthoResult? PendingPointTransformOrthoConstraint =>
        _hasPointTransformOrtho ? _pointTransformOrtho : null;

    public CadPlanPolarTrackingSettings PlanPolarTrackingSettings =>
        _planPolarTrackingSettings;

    /// <summary>Current profile-scoped PolarSnap settings.</summary>
    public CadPlanPolarSnapSettings PlanPolarSnapSettings =>
        _planPolarSnapSettings;

    /// <summary>
    /// Enables Snap Mode with Polar SNAPTYPE, or disables it when Polar is the
    /// selected type. Grid and Polar snap are mutually exclusive.
    /// </summary>
    public bool IsPlanPolarSnapEnabled
    {
        get => _planSnapType == CadPlanSnapType.Polar &&
            _planPolarSnapSettings.IsEnabled;
        set
        {
            if (!value && _planSnapType != CadPlanSnapType.Polar)
            {
                return;
            }
            SetPlanSnapState(CadPlanSnapType.Polar, value);
        }
    }

    /// <summary>
    /// Profile-scoped POLARDIST equivalent. Zero inherits Snap X spacing.
    /// </summary>
    public double PlanPolarSnapDistance
    {
        get => _planPolarSnapSettings.Distance;
        set
        {
            CadPlanPolarSnapSettings updated =
                _planPolarSnapSettings.WithDistance(value);
            if (updated == _planPolarSnapSettings)
            {
                return;
            }

            _planPolarSnapSettings = updated;
            if (IsPointAcquisitionActive &&
                _hasPointTransformPointerPosition)
            {
                UpdatePointTransformPointer(_pointTransformPointerPosition);
            }
            Invalidate();
        }
    }

    public bool IsPlanPolarTrackingEnabled
    {
        get => _planPolarTrackingSettings.IsEnabled;
        set
        {
            if (_planPolarTrackingSettings.IsEnabled == value)
            {
                return;
            }

            _planPolarTrackingSettings =
                _planPolarTrackingSettings.WithEnabled(value);
            if (_planPolarTrackingSettings.IsEnabled)
            {
                _isPlanOrthoEnabled = false;
            }
            if (IsPointAcquisitionActive &&
                _hasPointTransformPointerPosition)
            {
                UpdatePointTransformPointer(_pointTransformPointerPosition);
            }
            else
            {
                _hasPointTransformPolarTracking = false;
            }
            Invalidate();
        }
    }

    public double PlanPolarTrackingIncrementDegrees
    {
        get => _planPolarTrackingSettings.IncrementDegrees;
        set
        {
            double radians = value * (Math.PI / 180.0);
            CadPlanPolarTrackingSettings updated =
                _planPolarTrackingSettings.WithIncrementRadians(radians);
            if (updated == _planPolarTrackingSettings)
            {
                return;
            }

            _planPolarTrackingSettings = updated;
            if (IsPointAcquisitionActive &&
                _hasPointTransformPointerPosition)
            {
                UpdatePointTransformPointer(_pointTransformPointerPosition);
            }
            Invalidate();
        }
    }

    /// <summary>Profile-scoped POLARMODE bit-1 angle-measurement equivalent.</summary>
    public CadPlanPolarAngleMeasurement PlanPolarAngleMeasurement
    {
        get => _planPolarTrackingSettings.AngleMeasurement;
        set => SetPlanPolarTrackingProfile(
            _planPolarTrackingSettings.WithAngleMeasurement(value));
    }

    /// <summary>Whether the bounded POLARADDANG profile list participates.</summary>
    public bool UsePlanPolarAdditionalAngles
    {
        get => _planPolarTrackingSettings.UseAdditionalAngles;
        set
        {
            CadPlanPolarTrackingSettings updated =
                _planPolarTrackingSettings.WithAdditionalAnglesEnabled(value);
            SetPlanPolarTrackingProfile(updated);
        }
    }

    /// <summary>Current absolute, non-incremental POLARADDANG profile list.</summary>
    public CadPlanPolarAdditionalAngles PlanPolarAdditionalAngles =>
        _planPolarTrackingSettings.AdditionalAngles;

    public void SetPlanPolarAdditionalAngles(
        CadPlanPolarAdditionalAngles angles)
    {
        CadPlanPolarTrackingSettings updated =
            _planPolarTrackingSettings.WithAdditionalAngles(angles);
        SetPlanPolarTrackingProfile(updated);
    }

    public CadPlanPolarTrackingResult? PendingPointTransformPolarTracking =>
        _hasPointTransformPolarTracking
            ? _pointTransformPolarTracking
            : null;

    public int LastUnsupportedPrimitiveCount => _lastUnsupportedPrimitiveCount;

    public bool LastSelectionWasTruncated => _lastSelectionWasTruncated;

    public int LastDrawOrderReferenceUnsupportedPrimitiveCount { get; private set; }

    public bool LastDrawOrderReferenceSelectionWasTruncated { get; private set; }

    public CadBoundsSelectionMode? LastSelectionMode { get; private set; }

    public CadBoundsSelectionMode? LastDrawOrderReferenceSelectionMode { get; private set; }

    public CadPlanViewport CurrentViewport => CreateViewport();

    public int UndoCount => _history?.UndoCount ?? 0;

    public int RedoCount => _history?.RedoCount ?? 0;

    /// <summary>The persisted drawing ATTMODE consumed by every snapshot.</summary>
    public AttributeVisibilityMode AttributeDisplayMode =>
        CurrentSession?.Read(document => document.Header.AttributeVisibility) ??
        AttributeVisibilityMode.Normal;

    public event EventHandler? SelectionChanged;

    public event EventHandler? EditStateChanged;

    /// <summary>
    /// Raised when an Above/Under reference-pick session starts, accumulates
    /// references, commits, or is canceled.
    /// </summary>
    public event EventHandler? DrawOrderReferencePickChanged;

    /// <summary>
    /// Raised only when a point transform begins, accepts a point, completes,
    /// fails, or is canceled. Pointer-motion preview does not allocate events.
    /// </summary>
    public event EventHandler<CadPointTransformChangedEventArgs>?
        PointTransformChanged;

    /// <summary>
    /// Raised for accepted LINE points, segment Undo, completion, and failure.
    /// Pointer-motion rubber-band updates do not allocate events.
    /// </summary>
    public event EventHandler<CadLineAuthoringChangedEventArgs>?
        LineAuthoringChanged;

    /// <summary>
    /// Raised for accepted RAY points, ray Undo, completion, and failure.
    /// Pointer-motion preview does not allocate events.
    /// </summary>
    public event EventHandler<CadRayAuthoringChangedEventArgs>?
        RayAuthoringChanged;

    /// <summary>Raised for XLINE acceptance, local Undo, completion, and failure.</summary>
    public event EventHandler<CadXLineAuthoringChangedEventArgs>?
        XLineAuthoringChanged;

    /// <summary>
    /// Raised when POINT begins, completes, fails, or is canceled.
    /// Pointer motion does not allocate events.
    /// </summary>
    public event EventHandler<CadPointAuthoringChangedEventArgs>?
        PointAuthoringChanged;

    /// <summary>
    /// Raised for accepted PLINE points, mode changes, segment Undo,
    /// completion, and failure. Pointer motion does not allocate events.
    /// </summary>
    public event EventHandler<CadPolylineAuthoringChangedEventArgs>?
        PolylineAuthoringChanged;

    /// <summary>
    /// Raised for accepted CIRCLE points, completion, cancellation, and failure.
    /// Pointer motion does not allocate events.
    /// </summary>
    public event EventHandler<CadCircleAuthoringChangedEventArgs>?
        CircleAuthoringChanged;

    /// <summary>
    /// Raised for accepted ARC points, completion, cancellation, and failure.
    /// Pointer motion does not allocate events.
    /// </summary>
    public event EventHandler<CadArcAuthoringChangedEventArgs>?
        ArcAuthoringChanged;

    /// <summary>
    /// Raised for accepted ELLIPSE inputs, completion, cancellation, and failure.
    /// Pointer motion does not allocate events.
    /// </summary>
    public event EventHandler<CadEllipseAuthoringChangedEventArgs>?
        EllipseAuthoringChanged;

    /// <summary>
    /// Raised for accepted POLYGON inputs, completion, cancellation, and failure.
    /// Pointer motion does not allocate events.
    /// </summary>
    public event EventHandler<CadPolygonAuthoringChangedEventArgs>?
        PolygonAuthoringChanged;

    /// <summary>
    /// Raised for accepted RECTANG corners, completion, cancellation, and
    /// failure. Pointer-motion preview does not allocate events.
    /// </summary>
    public event EventHandler<CadRectangleAuthoringChangedEventArgs>?
        RectangleAuthoringChanged;

    /// <summary>
    /// Raised when cursor-direction availability changes for typed point input.
    /// </summary>
    public event EventHandler? PointTransformInputAvailabilityChanged;

    /// <summary>Raised after one complete immutable snapshot/picture replacement.</summary>
    public event EventHandler? SnapshotChanged;

    public CadShxFontCatalog ShxFonts { get; }

    /// <summary>
    /// Shared desktop/browser raster registry. Hosts register bounded encoded
    /// bytes or typed texture sources before loading documents with IMAGEDEFs.
    /// </summary>
    public CadRasterImageCatalog RasterImages { get; }

    public CadSampleCanvas()
        : this(null, null)
    {
    }

    public CadSampleCanvas(CadShxFontCatalog? shxFonts)
        : this(shxFonts, null)
    {
    }

    public CadSampleCanvas(
        CadShxFontCatalog? shxFonts,
        CadRasterImageCatalog? rasterImages)
    {
        ShxFonts = shxFonts ?? new CadShxFontCatalog();
        if (rasterImages is null)
        {
            RasterImages = new CadRasterImageCatalog();
            RasterImages.RegisterEncoded(
                "progpu-cad-sample.png",
                RepresentativeRasterImageBytes);
        }
        else
        {
            RasterImages = rasterImages;
        }
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
        bool resetViewSelectionAndHistory,
        bool synchronizePlanOrthoMode = false,
        bool synchronizePlanSnapMode = false)
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
        using CadRecordedPlanScene scene = new CadPlanSceneCompiler().Compile(
            snapshot,
            new CadPlanSceneOptions
            {
                RasterImageSourceResolver = RasterImages,
                RasterImageContext = WgpuContext.Current,
            });
        GpuPicture picture = scene.CreatePicture();
        GpuPicture? previous = _picture;
        _picture = picture;
        CurrentSession = session;
        CurrentSnapshot = snapshot;
        _planGridDisplaySettings = snapshot.PlanGridDisplaySettings;
        bool isPlanSnapEnabled =
            resetViewSelectionAndHistory || synchronizePlanSnapMode
            ? snapshot.PlanGridSnapSettings.IsEnabled
            : IsPlanSnapEnabled;
        _planGridSnapSettings = snapshot.PlanGridSnapSettings.WithEnabled(
            isPlanSnapEnabled && _planSnapType == CadPlanSnapType.Grid);
        _planPolarSnapSettings = _planPolarSnapSettings.WithEnabled(
            isPlanSnapEnabled && _planSnapType == CadPlanSnapType.Polar);
        _planPolarTrackingSettings = resetViewSelectionAndHistory
            ? snapshot.PlanPolarTrackingSettings
            : snapshot.PlanPolarTrackingSettings
                .WithIncrementRadians(
                    _planPolarTrackingSettings.IncrementRadians)
                .WithAngleMeasurement(
                    _planPolarTrackingSettings.AngleMeasurement)
                .WithAdditionalAngles(
                    _planPolarTrackingSettings.AdditionalAngles)
                .WithAdditionalAnglesEnabled(
                    _planPolarTrackingSettings.UseAdditionalAngles)
                .WithEnabled(_planPolarTrackingSettings.IsEnabled);
        _isPlanOrthoEnabled =
            resetViewSelectionAndHistory || synchronizePlanOrthoMode
            ? snapshot.IsOrthoModeEnabled
            : _isPlanOrthoEnabled;
        if (_isPlanOrthoEnabled)
        {
            _planPolarTrackingSettings =
                _planPolarTrackingSettings.WithEnabled(false);
        }
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
            ResetPointTransformState(notify: false);
            ResetLineAuthoringState();
            ResetRayAuthoringState();
            ResetXLineAuthoringState();
            ResetPointAuthoringState();
            ResetPolylineAuthoringState();
            ResetCircleAuthoringState();
            ResetArcAuthoringState();
            ResetEllipseAuthoringState();
            ResetPolygonAuthoringState();
            ResetRectangleAuthoringState();
            ResetSelectionState(notify: false);
            _needsFit = true;
        }
        else
        {
            _selectedHandleCount = preservedHandleCount;
            RefreshSelectionBounds(snapshot);
            if (_hasPointTransformPointerPosition)
            {
                UpdatePointTransformPointer(_pointTransformPointerPosition);
            }
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
        ClearPointTransformSnapState();
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
        DrawPlanGridDisplay(context, viewport);
        context.DrawPicture(_picture, viewport.CreateCameraMatrix());
        if (_constructionPicture is not null)
        {
            context.DrawPicture(_constructionPicture, viewport.CreateCameraMatrix());
        }
        if (_pointMarkerPicture is not null)
        {
            context.DrawPicture(_pointMarkerPicture, viewport.CreateCameraMatrix());
        }
        if (_lineAuthoringPicture is not null)
        {
            context.DrawPicture(_lineAuthoringPicture);
        }
        if (_rayAuthoringPicture is not null)
        {
            context.DrawPicture(_rayAuthoringPicture);
        }
        if (_xlineAuthoringPicture is not null)
        {
            context.DrawPicture(_xlineAuthoringPicture);
        }
        if (_polylineAuthoringPicture is not null)
        {
            context.DrawPicture(_polylineAuthoringPicture);
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
        bool drewPendingXLine = DrawPendingXLine(context, viewport);
        if (IsPointAcquisitionActive &&
            _hasPointTransformBasePoint &&
            !drewPendingXLine)
        {
            Vector2 basePoint = viewport.WorldToScreen(_pointTransformBasePoint);
            if (!DrawPendingRectangle(context, viewport) &&
                !DrawPendingPolygon(context, viewport) &&
                !DrawPendingEllipse(context, viewport) &&
                !DrawPendingArc(context, viewport) &&
                !DrawPendingCircle(context, viewport) &&
                !DrawPendingPolylineArc(context, viewport, basePoint))
            {
                context.DrawLine(
                    _drawOrderReferencePen,
                    basePoint,
                    _pointTransformCurrent);
            }
            if (!_selectedBounds.IsEmpty)
            {
                Vector2 displacement = _pointTransformCurrent - basePoint;
                Rect preview = ToScreenRect(viewport, _selectedBounds);
                context.DrawRectangle(
                    null,
                    _drawOrderReferencePen,
                    new Rect(
                        preview.X + displacement.X,
                        preview.Y + displacement.Y,
                        preview.Width,
                        preview.Height));
            }
        }
        DrawPlanPolarTrackingGuide(context);
        DrawPlanGridSnapMarker(context);
        DrawObjectSnapMarker(context, _pointTransformObjectSnap);
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

    private bool DrawPendingXLine(
        DrawingContext context,
        CadPlanViewport viewport)
    {
        CadXLineModeAuthoringSession? authoring = _xlineAuthoring;
        if (authoring is null)
        {
            return false;
        }

        if (authoring.Prompt is CadXLinePromptKind.AngleReferenceSource or
            CadXLinePromptKind.OffsetSource)
        {
            if (_hasXLineSourceCandidate && CurrentSnapshot is not null &&
                TryGetXLineSourceScreenSegment(
                    CurrentSnapshot,
                    _xlineSourceCandidate,
                    viewport,
                    out Vector2 sourceStart,
                    out Vector2 sourceEnd))
            {
                context.DrawLine(
                    _drawOrderReferencePen,
                    sourceStart,
                    sourceEnd);
            }
            return true;
        }

        if (authoring.Prompt is CadXLinePromptKind.AngleValue or
            CadXLinePromptKind.OffsetDistance or
            CadXLinePromptKind.FirstPoint)
        {
            return true;
        }

        double depth = authoring.AcquisitionBasePoint?.Z ??
            authoring.Context.Origin.Z;
        CadPoint3D current;
        try
        {
            current = viewport.ScreenToWorld(_pointTransformCurrent, depth);
        }
        catch (ArgumentException)
        {
            return true;
        }

        if (authoring.BisectorVertex is CadPoint3D vertex)
        {
            if (authoring.BisectorFirstRayPoint is CadPoint3D firstRay)
            {
                context.DrawLine(
                    _drawOrderReferencePen,
                    viewport.WorldToScreen(vertex),
                    viewport.WorldToScreen(firstRay));
            }
            context.DrawLine(
                _drawOrderReferencePen,
                viewport.WorldToScreen(vertex),
                _pointTransformCurrent);
        }

        if (authoring.TryPreviewPoint(
                current,
                out CadXLineDefinition definition))
        {
            DrawXLineDefinition(context, viewport, definition);
        }
        return true;
    }

    private void DrawXLineDefinition(
        DrawingContext context,
        CadPlanViewport viewport,
        CadXLineDefinition definition)
    {
        var primitive = new CadConstructionLinePrimitive(
            definition.FirstPoint,
            definition.Direction);
        if (CadConstructionSceneCompiler.TryClipPlan(
                primitive,
                viewport.CreatePlanClipBounds(),
                isRay: false,
                out CadPoint3D start,
                out CadPoint3D end))
        {
            context.DrawLine(
                _drawOrderReferencePen,
                viewport.WorldToScreen(start),
                viewport.WorldToScreen(end));
        }
    }

    private bool DrawPendingCircle(
        DrawingContext context,
        CadPlanViewport viewport)
    {
        CadCircleAuthoringSession? authoring = _circleAuthoring;
        if (authoring is null || !authoring.HasFirstPoint)
        {
            return false;
        }

        ReadOnlySpan<CadPoint3D> points = authoring.Points.Span;
        if (authoring.Mode == CadCircleAuthoringMode.ThreePoint &&
            points.Length == 2)
        {
            context.DrawLine(
                _drawOrderReferencePen,
                viewport.WorldToScreen(points[0]),
                viewport.WorldToScreen(points[1]));
        }

        CadPoint3D finalPoint;
        try
        {
            finalPoint = viewport.ScreenToWorld(
                _pointTransformCurrent,
                authoring.FirstPoint!.Value.Z);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (!authoring.TryCreateSnapshot(
                finalPoint,
                out CadCircleAuthoringSnapshot snapshot,
                out _))
        {
            return false;
        }

        try
        {
            Vector2 center = viewport.WorldToScreen(snapshot.Center);
            Vector2 edge = viewport.WorldToScreen(new CadPoint3D(
                snapshot.Center.X + snapshot.Radius,
                snapshot.Center.Y,
                snapshot.Center.Z));
            float radius = Vector2.Distance(center, edge);
            if (!float.IsFinite(radius) || radius <= 0.0f)
            {
                return false;
            }
            context.DrawEllipse(
                null,
                _drawOrderReferencePen,
                center,
                radius,
                radius);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool DrawPendingArc(
        DrawingContext context,
        CadPlanViewport viewport)
    {
        CadArcAuthoringSession? authoring = _arcAuthoring;
        if (authoring is null || !authoring.HasFirstPoint)
        {
            return false;
        }

        ReadOnlySpan<CadPoint3D> points = authoring.Points.Span;
        if (points.Length < 2)
        {
            return false;
        }

        try
        {
            context.DrawLine(
                _drawOrderReferencePen,
                viewport.WorldToScreen(points[0]),
                viewport.WorldToScreen(points[1]));
        }
        catch (ArgumentException)
        {
            return true;
        }

        if (!authoring.AcceptsPointFinalInput)
        {
            return true;
        }

        CadPoint3D finalPoint;
        try
        {
            finalPoint = viewport.ScreenToWorld(
                _pointTransformCurrent,
                authoring.FirstPoint!.Value.Z);
        }
        catch (ArgumentException)
        {
            return true;
        }
        if (!authoring.TryCreateSnapshot(
                finalPoint,
                out CadArcAuthoringSnapshot snapshot,
                out _))
        {
            return true;
        }

        try
        {
            Vector2 center = viewport.WorldToScreen(snapshot.Center);
            Vector2 xEdge = viewport.WorldToScreen(new CadPoint3D(
                snapshot.Center.X + snapshot.Radius,
                snapshot.Center.Y,
                snapshot.Center.Z));
            Vector2 yEdge = viewport.WorldToScreen(new CadPoint3D(
                snapshot.Center.X,
                snapshot.Center.Y + snapshot.Radius,
                snapshot.Center.Z));
            Vector2 start = viewport.WorldToScreen(snapshot.StartPoint);
            Vector2 end = viewport.WorldToScreen(snapshot.EndPoint);
            float radiusX = Vector2.Distance(center, xEdge);
            float radiusY = Vector2.Distance(center, yEdge);
            if (!float.IsFinite(radiusX) || radiusX <= 0.0f ||
                !float.IsFinite(radiusY) || radiusY <= 0.0f)
            {
                return true;
            }

            var path = new PathGeometry();
            var figure = new PathFigure(start)
            {
                IsFilled = false,
                IsClosed = false,
            };
            figure.Segments.Add(new ArcSegment(
                end,
                new Vector2(radiusX, radiusY),
                rotationAngle: 0.0f,
                isLargeArc: snapshot.SweepAngle > Math.PI,
                sweepDirection: SweepDirection.Clockwise));
            path.Figures.Add(figure);
            context.DrawPath(null, _drawOrderReferencePen, path);
        }
        catch (ArgumentException)
        {
            // The accepted guide remains valid even if this pointer preview
            // cannot be represented by the current float viewport.
        }
        return true;
    }

    private bool DrawPendingPolygon(
        DrawingContext context,
        CadPlanViewport viewport)
    {
        CadPolygonAuthoringSession? authoring = _polygonAuthoring;
        GpuPicture? picture = _polygonAuthoringPicture;
        if (authoring is null ||
            picture is null ||
            authoring.FirstPoint is not CadPoint3D firstPoint)
        {
            return false;
        }

        try
        {
            CadPoint3D previewPoint = viewport.ScreenToWorld(
                _pointTransformCurrent,
                firstPoint.Z);
            if (authoring.TryPreviewPoint(
                    previewPoint,
                    out CadPolygonAuthoringSnapshot snapshot))
            {
                DrawPolygonAuthoringSnapshot(
                    context,
                    viewport,
                    picture,
                    snapshot);
            }

            context.DrawLine(
                _drawOrderReferencePen,
                viewport.WorldToScreen(firstPoint),
                _pointTransformCurrent);
        }
        catch (ArgumentException)
        {
            // A non-representable live pointer never mutates accepted state.
        }
        return true;
    }

    private bool DrawPendingRectangle(
        DrawingContext context,
        CadPlanViewport viewport)
    {
        CadRectangleAuthoringSession? authoring = _rectangleAuthoring;
        if (authoring is null ||
            authoring.FirstCorner is not CadPoint3D firstCorner)
        {
            return false;
        }

        try
        {
            CadPoint3D previewPoint = viewport.ScreenToWorld(
                _pointTransformCurrent,
                firstCorner.Z);
            if (authoring.TryPreviewPoint(
                    previewPoint,
                    out CadRectangleAuthoringSnapshot snapshot))
            {
                DrawRectangleAuthoringSnapshot(context, viewport, snapshot);
            }
            context.DrawLine(
                _drawOrderReferencePen,
                viewport.WorldToScreen(firstCorner),
                _pointTransformCurrent);
        }
        catch (ArgumentException)
        {
            // A non-representable live pointer never mutates accepted state.
        }
        return true;
    }

    private void DrawRectangleAuthoringSnapshot(
        DrawingContext context,
        CadPlanViewport viewport,
        CadRectangleAuthoringSnapshot snapshot)
    {
        Span<CadPoint3D> points = stackalloc CadPoint3D[8];
        Span<double> bulges = stackalloc double[8];
        int count = snapshot.CopyContour(points, bulges);
        CadPoint3D first = points[0];
        var path = new PathGeometry();
        var figure = new PathFigure(viewport.WorldToScreen(first))
        {
            IsFilled = false,
            IsClosed = false,
        };
        CadPoint3D previous = first;
        for (int index = 1; index < count; index++)
        {
            CadPoint3D current = points[index];
            AppendPolylinePreviewSegment(
                figure,
                viewport,
                previous,
                current,
                bulges[index - 1]);
            previous = current;
        }
        AppendPolylinePreviewSegment(
            figure,
            viewport,
            previous,
            first,
            bulges[count - 1]);
        path.Figures.Add(figure);
        context.DrawPath(null, _drawOrderReferencePen, path);
    }

    private static void DrawPolygonAuthoringSnapshot(
        DrawingContext context,
        CadPlanViewport viewport,
        GpuPicture picture,
        CadPolygonAuthoringSnapshot snapshot)
    {
        Vector2 center = viewport.WorldToScreen(snapshot.Center);
        double cosine = Math.Cos(snapshot.FirstVertexAngle);
        double sine = Math.Sin(snapshot.FirstVertexAngle);
        Vector2 xEnd = viewport.WorldToScreen(new CadPoint3D(
            snapshot.Center.X + (snapshot.Circumradius * cosine),
            snapshot.Center.Y + (snapshot.Circumradius * sine),
            snapshot.Center.Z));
        Vector2 yEnd = viewport.WorldToScreen(new CadPoint3D(
            snapshot.Center.X - (snapshot.Circumradius * sine),
            snapshot.Center.Y + (snapshot.Circumradius * cosine),
            snapshot.Center.Z));
        Vector2 xAxis = xEnd - center;
        Vector2 yAxis = yEnd - center;
        if (!float.IsFinite(xAxis.X) || !float.IsFinite(xAxis.Y) ||
            !float.IsFinite(yAxis.X) || !float.IsFinite(yAxis.Y) ||
            xAxis.LengthSquared() <= 0.0f ||
            yAxis.LengthSquared() <= 0.0f)
        {
            return;
        }

        context.DrawPictureTransformed(
            picture,
            new Matrix4x4(
                xAxis.X, xAxis.Y, 0.0f, 0.0f,
                yAxis.X, yAxis.Y, 0.0f, 0.0f,
                0.0f, 0.0f, 1.0f, 0.0f,
                center.X, center.Y, 0.0f, 1.0f));
    }

    private bool DrawPendingEllipse(
        DrawingContext context,
        CadPlanViewport viewport)
    {
        CadEllipseAuthoringSession? authoring = _ellipseAuthoring;
        if (authoring is null || authoring.PointCount == 0)
        {
            return false;
        }

        ReadOnlySpan<CadPoint3D> points = authoring.Points.Span;
        if (authoring.PointCount == 1)
        {
            try
            {
                context.DrawLine(
                    _drawOrderReferencePen,
                    viewport.WorldToScreen(points[0]),
                    _pointTransformCurrent);
            }
            catch (ArgumentException)
            {
                // The accepted point remains valid if its live guide cannot be
                // represented by the current float viewport.
            }
            return true;
        }

        try
        {
            context.DrawLine(
                _drawOrderReferencePen,
                viewport.WorldToScreen(points[0]),
                viewport.WorldToScreen(points[1]));
        }
        catch (ArgumentException)
        {
            return true;
        }

        CadEllipseAuthoringSnapshot snapshot;
        bool hasSnapshot = false;
        if (authoring.AcceptsPointInput)
        {
            try
            {
                CadPoint3D previewPoint = viewport.ScreenToWorld(
                    _pointTransformCurrent,
                    authoring.FirstPoint!.Value.Z);
                hasSnapshot = authoring.TryPreviewPoint(
                    previewPoint,
                    out snapshot);
            }
            catch (ArgumentException)
            {
                snapshot = default;
            }
        }
        else
        {
            snapshot = default;
        }

        if (!hasSnapshot)
        {
            hasSnapshot = authoring.TryGetAxesSnapshot(out snapshot);
        }
        if (hasSnapshot)
        {
            DrawEllipseAuthoringSnapshot(context, viewport, snapshot);
        }

        if (authoring.AcceptsPointInput &&
            authoring.AcquisitionBasePoint is CadPoint3D acquisitionBase)
        {
            try
            {
                context.DrawLine(
                    _drawOrderReferencePen,
                    viewport.WorldToScreen(acquisitionBase),
                    _pointTransformCurrent);
            }
            catch (ArgumentException)
            {
                // The accepted first-axis guide remains valid.
            }
        }
        return true;
    }

    private void DrawEllipseAuthoringSnapshot(
        DrawingContext context,
        CadPlanViewport viewport,
        CadEllipseAuthoringSnapshot snapshot)
    {
        try
        {
            Vector2 center = viewport.WorldToScreen(snapshot.Center);
            Vector2 majorEnd = viewport.WorldToScreen(
                snapshot.Center + snapshot.MajorAxisEndPoint);
            Vector2 minorEnd = viewport.WorldToScreen(
                snapshot.Center + snapshot.MinorAxisEndPoint);
            Vector2 major = majorEnd - center;
            Vector2 minor = minorEnd - center;
            if (!float.IsFinite(major.X) || !float.IsFinite(major.Y) ||
                !float.IsFinite(minor.X) || !float.IsFinite(minor.Y) ||
                major.LengthSquared() <= 0.0f ||
                minor.LengthSquared() <= 0.0f)
            {
                return;
            }

            var transform = new Matrix4x4(
                major.X, major.Y, 0.0f, 0.0f,
                minor.X, minor.Y, 0.0f, 0.0f,
                0.0f, 0.0f, 1.0f, 0.0f,
                center.X, center.Y, 0.0f, 1.0f);
            if (snapshot.IsFullEllipse)
            {
                context.DrawEllipse(
                    null,
                    _drawOrderReferencePen,
                    Vector2.Zero,
                    1.0f,
                    1.0f,
                    transform);
                return;
            }

            Vector2 start = new(
                MathF.Cos((float)snapshot.StartParameter),
                MathF.Sin((float)snapshot.StartParameter));
            double endParameter =
                snapshot.StartParameter + snapshot.SweepParameter;
            Vector2 end = new(
                MathF.Cos((float)endParameter),
                MathF.Sin((float)endParameter));
            var path = new PathGeometry();
            var figure = new PathFigure(start)
            {
                IsFilled = false,
                IsClosed = false,
            };
            figure.Segments.Add(new ArcSegment(
                end,
                Vector2.One,
                rotationAngle: 0.0f,
                isLargeArc: snapshot.SweepParameter > Math.PI,
                sweepDirection: SweepDirection.Counterclockwise));
            path.Figures.Add(figure);
            context.DrawPath(
                null,
                _drawOrderReferencePen,
                path,
                transform);
        }
        catch (ArgumentException)
        {
            // A non-representable float preview never mutates accepted state.
        }
    }

    private bool DrawPendingPolylineArc(
        DrawingContext context,
        CadPlanViewport viewport,
        Vector2 screenStart)
    {
        CadPolylineAuthoringSession? authoring = _polylineAuthoring;
        if (authoring is null ||
            authoring.Mode != CadPolylineAuthoringMode.TangentArc ||
            !authoring.HasFirstPoint)
        {
            return false;
        }

        CadPoint3D endpoint;
        try
        {
            endpoint = viewport.ScreenToWorld(
                _pointTransformCurrent,
                authoring.CurrentPoint!.Value.Z);
        }
        catch (ArgumentException)
        {
            return false;
        }
        CadPoint3D start = authoring.CurrentPoint!.Value;
        if (!authoring.TryGetPendingBulge(endpoint, out double bulge) ||
            bulge == 0.0 ||
            !CadPolylineAuthoringSession.TryGetBulgeGeometry(
                start,
                endpoint,
                bulge,
                out CadPoint3D center,
                out double radius,
                out double startAngle,
                out double sweep))
        {
            return false;
        }

        int stepCount = GetPolylinePreviewStepCount(viewport, radius, sweep);
        Vector2 previous = screenStart;
        for (int step = 1; step <= stepCount; step++)
        {
            double angle = startAngle + (sweep * (step / (double)stepCount));
            var point = new CadPoint3D(
                center.X + (radius * Math.Cos(angle)),
                center.Y + (radius * Math.Sin(angle)),
                center.Z);
            Vector2 current;
            try
            {
                current = viewport.WorldToScreen(point);
            }
            catch (ArgumentException)
            {
                return true;
            }
            context.DrawLine(_drawOrderReferencePen, previous, current);
            previous = current;
        }
        return true;
    }

    private void DrawPlanGridDisplay(
        DrawingContext context,
        CadPlanViewport viewport)
    {
        if (!CadPlanGridDisplayPlan.TryCreate(
                _planGridDisplaySettings,
                viewport,
                out CadPlanGridDisplayPlan plan))
        {
            return;
        }

        Rect viewportClip = new(0.0f, 0.0f, Size.X, Size.Y);
        bool hasNarrowClip = plan.ScreenClip != viewportClip;
        if (hasNarrowClip)
        {
            context.PushClip(plan.ScreenClip);
        }
        // Autodesk's isometric drafting contract uses a dotted oblique lattice;
        // the lined GRIDSTYLE presentation does not follow isometric snap.
        if (_planGridDisplaySettings.Style == CadPlanGridDisplayStyle.Isometric ||
            _planGridPresentationStyle == CadPlanGridPresentationStyle.Dots)
        {
            context.DrawDeviceDotGrid(
                _gridBrush,
                plan.LocalBounds,
                plan.Spacing,
                0.75f,
                plan.Transform);
        }
        else
        {
            context.DrawDeviceLineGrid(
                _gridBrush,
                plan.LocalBounds,
                plan.Spacing,
                1.0f,
                plan.MinorLinesPerMajorLine,
                plan.Transform);
        }
        if (hasNarrowClip)
        {
            context.PopClip();
        }
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
        ClearPointTransformSnapState();
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
            ClearPointTransformSnapState();
            CapturePointer(args.Pointer);
            args.Handled = true;
            return;
        }
        if (!args.IsLeftButtonPressed || CurrentSnapshot is null)
        {
            return;
        }

        if (IsPointAcquisitionActive)
        {
            _isPointTransformPointerPressed = true;
            _isSelecting = false;
            _isPanning = false;
            UpdatePointTransformPointer(args.Position);
            CapturePointer(args.Pointer);
            Invalidate();
            args.Handled = true;
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
            ClearPointTransformSnapState();
            RefreshConstructionPicture();
            Invalidate();
            args.Handled = true;
            return;
        }
        if (_isPointTransformPointerPressed)
        {
            UpdatePointTransformPointer(args.Position);
            Invalidate();
            args.Handled = true;
            return;
        }
        if (IsPointAcquisitionActive)
        {
            UpdatePointTransformPointer(args.Position);
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
        bool handled =
            _isPanning || _isSelecting || _isPointTransformPointerPressed;
        if (_isPointTransformPointerPressed)
        {
            UpdatePointTransformPointer(args.Position);
            if (!args.IsCanceled)
            {
                AcceptPointTransformPointer(args.Position);
            }
        }
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
        _isPointTransformPointerPressed = false;
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
        _isPointTransformPointerPressed = false;
        _hasSelectionDrag = false;
        ClearPointTransformSnapState();
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
        ClearPointTransformSnapState();
        RefreshConstructionPicture();
        Invalidate();
        args.Handled = true;
    }

    public void ClearSelection()
    {
        ResetDrawOrderReferencePickState(notify: true);
        ResetPointTransformState(notify: true);
        ResetLineAuthoringState();
        ResetRayAuthoringState();
        ResetXLineAuthoringState();
        ResetPointAuthoringState();
        ResetPolylineAuthoringState();
        ResetCircleAuthoringState();
        ResetArcAuthoringState();
        ResetEllipseAuthoringState();
        ResetPolygonAuthoringState();
        ResetRectangleAuthoringState();
        ResetSelectionState(notify: true);
        Invalidate();
    }

    /// <summary>
    /// Starts one bounded WCS-XY base-point/second-point MOVE or COPY over the
    /// current semantic selection. The document remains unchanged until the
    /// second point is accepted.
    /// </summary>
    public bool BeginSelectionPointTransform(CadPointTransformOperation operation)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }
        if (_selectedHandleCount == 0)
        {
            return false;
        }
        if (PendingDrawOrderPlacement is not null)
        {
            throw new InvalidOperationException(
                "Commit or cancel the pending draw-order reference selection first.");
        }
        if (IsPointAcquisitionActive)
        {
            throw new InvalidOperationException(
                "Complete or cancel the pending point transform first.");
        }

        PendingPointTransformOperation = operation;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        _hasPointTransformPointerPosition = false;
        _pointTransformObjectSnap = default;
        _hasPointTransformGridSnap = false;
        _hasPointTransformOrtho = false;
        _hasPointTransformPolarTracking = false;
        PointTransformChanged?.Invoke(
            this,
            new CadPointTransformChangedEventArgs(
                operation,
                CadPointTransformStage.AwaitingBasePoint));
        Invalidate();
        return true;
    }

    /// <summary>Cancels a pending point transform without editing the document.</summary>
    public bool CancelSelectionPointTransform()
    {
        CadPointTransformOperation? operation = PendingPointTransformOperation;
        if (operation is null)
        {
            return false;
        }

        CadPoint3D? basePoint = PendingPointTransformBasePoint;
        ResetPointTransformState(notify: false);
        PointTransformChanged?.Invoke(
            this,
            new CadPointTransformChangedEventArgs(
                operation.Value,
                CadPointTransformStage.Canceled,
                basePoint));
        Invalidate();
        return true;
    }

    /// <summary>
    /// Returns whether one bounded invariant coordinate can be accepted by the
    /// current point-transform stage without changing interaction state.
    /// </summary>
    public bool CanAcceptSelectionPointTransformInput(string? text)
    {
        if (PendingPointTransformOperation is null)
        {
            return false;
        }

        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            return CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput distance) &&
                TryResolvePointTransformDirectDistance(distance, out _);
        }
        if (!_hasPointTransformBasePoint && coordinate.IsRelative)
        {
            return false;
        }

        CadPoint3D relativeOrigin = _hasPointTransformBasePoint
            ? _pointTransformBasePoint
            : CadPoint3D.Zero;
        if (!coordinate.TryResolve(relativeOrigin, out CadPoint3D point))
        {
            return false;
        }
        if (_hasPointTransformBasePoint)
        {
            return IsFinite(point - _pointTransformBasePoint);
        }

        try
        {
            _ = CreateViewport().WorldToScreen(point);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Accepts an absolute WCS first point, an absolute/relative WCS second
    /// point, or a positive second-point distance along the current raw,
    /// Ortho, or acquired polar cursor direction. Rejection leaves the prompt
    /// and document unchanged.
    /// </summary>
    public bool TryAcceptSelectionPointTransformInput(
        string? text,
        out string? errorMessage)
    {
        errorMessage = null;
        if (PendingPointTransformOperation is null)
        {
            errorMessage = "No point transform is awaiting coordinate input.";
            return false;
        }
        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            if (CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput directDistance))
            {
                if (!_hasPointTransformBasePoint)
                {
                    errorMessage =
                        "Accept an absolute first point before entering a direct distance.";
                    return false;
                }
                if (!_hasPointTransformPointerPosition)
                {
                    errorMessage =
                        "Move the cursor from the base point before entering a direct distance.";
                    return false;
                }
                if (!TryResolvePointTransformDirectDistance(
                        directDistance,
                        out CadPoint3D directPoint))
                {
                    errorMessage =
                        "The cursor direction and distance do not resolve to a finite WCS point.";
                    return false;
                }

                AcceptPointTransformPoint(directPoint, screenPoint: null);
                return true;
            }

            errorMessage =
                "Enter x,y[,z], @dx,dy[,dz], distance<angle, @distance<angle, or a positive direct distance using invariant numbers.";
            return false;
        }
        if (!_hasPointTransformBasePoint && coordinate.IsRelative)
        {
            errorMessage =
                "Enter an absolute first point before using a relative coordinate.";
            return false;
        }

        CadPoint3D relativeOrigin = _hasPointTransformBasePoint
            ? _pointTransformBasePoint
            : CadPoint3D.Zero;
        if (!coordinate.TryResolve(relativeOrigin, out CadPoint3D point))
        {
            errorMessage = "The coordinate resolves outside finite WCS values.";
            return false;
        }

        Vector2? screenPoint = null;
        if (!_hasPointTransformBasePoint)
        {
            try
            {
                screenPoint = CreateViewport().WorldToScreen(point);
            }
            catch (ArgumentException)
            {
                errorMessage =
                    "The first point cannot be represented by the current plan viewport.";
                return false;
            }
        }
        else if (!IsFinite(point - _pointTransformBasePoint))
        {
            errorMessage = "The coordinate produces a non-finite displacement.";
            return false;
        }

        AcceptPointTransformPoint(point, screenPoint);
        return true;
    }

    /// <summary>
    /// Starts a bounded contiguous LINE sequence. Accepted segments remain a
    /// retained transient overlay until Enter, Escape, or Close publishes one
    /// atomic document-history edit.
    /// </summary>
    public bool BeginLineAuthoring(
        int maximumSegmentCount =
            CadLineAuthoringSession.DefaultMaximumSegmentCount)
    {
        if (CurrentSession is null || CurrentSnapshot is null)
        {
            return false;
        }
        ThrowIfDrawOrderReferenceSelectionPending();
        if (IsPointAcquisitionActive)
        {
            throw new InvalidOperationException(
                "Complete the pending point-acquisition command first.");
        }

        _lineAuthoring = new CadLineAuthoringSession(maximumSegmentCount);
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        ClearPointTransformSnapState();
        RefreshLineAuthoringPicture();
        LineAuthoringChanged?.Invoke(
            this,
            new CadLineAuthoringChangedEventArgs(
                CadLineAuthoringStage.AwaitingFirstPoint,
                segmentCount: 0));
        Invalidate();
        return true;
    }

    public bool CanAcceptLineAuthoringInput(string? text)
    {
        if (_lineAuthoring is null)
        {
            return false;
        }

        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            return CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput distance) &&
                TryResolvePointTransformDirectDistance(distance, out _);
        }
        if (!_hasPointTransformBasePoint && coordinate.IsRelative)
        {
            return false;
        }

        CadPoint3D origin = _hasPointTransformBasePoint
            ? _pointTransformBasePoint
            : CadPoint3D.Zero;
        if (!coordinate.TryResolve(origin, out CadPoint3D point) ||
            (_hasPointTransformBasePoint && point == _pointTransformBasePoint))
        {
            return false;
        }

        try
        {
            _ = CreateViewport().WorldToScreen(point);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool TryAcceptLineAuthoringInput(
        string? text,
        out string? errorMessage)
    {
        errorMessage = null;
        if (_lineAuthoring is null)
        {
            errorMessage = "No LINE command is awaiting point input.";
            return false;
        }

        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            if (CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput directDistance))
            {
                if (!_hasPointTransformBasePoint)
                {
                    errorMessage =
                        "Accept an absolute first point before entering a direct distance.";
                    return false;
                }
                if (!_hasPointTransformPointerPosition)
                {
                    errorMessage =
                        "Move the cursor from the current LINE point before entering a direct distance.";
                    return false;
                }
                if (!TryResolvePointTransformDirectDistance(
                        directDistance,
                        out CadPoint3D directPoint))
                {
                    errorMessage =
                        "The cursor direction and distance do not resolve to a finite WCS point.";
                    return false;
                }

                return TryAcceptLineAuthoringPoint(
                    directPoint,
                    screenPoint: null,
                    out errorMessage);
            }

            errorMessage =
                "Enter x,y[,z], @dx,dy[,dz], distance<angle, @distance<angle, or a positive direct distance using invariant numbers.";
            return false;
        }
        if (!_hasPointTransformBasePoint && coordinate.IsRelative)
        {
            errorMessage =
                "Enter an absolute first point before using a relative coordinate.";
            return false;
        }

        CadPoint3D origin = _hasPointTransformBasePoint
            ? _pointTransformBasePoint
            : CadPoint3D.Zero;
        if (!coordinate.TryResolve(origin, out CadPoint3D point))
        {
            errorMessage = "The coordinate resolves outside finite WCS values.";
            return false;
        }

        Vector2 screenPoint;
        try
        {
            screenPoint = CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException)
        {
            errorMessage =
                "The LINE point cannot be represented by the current plan viewport.";
            return false;
        }
        return TryAcceptLineAuthoringPoint(point, screenPoint, out errorMessage);
    }

    /// <summary>Removes the latest accepted LINE segment without leaving LINE.</summary>
    public bool UndoLineAuthoringSegment()
    {
        CadLineAuthoringSession? authoring = _lineAuthoring;
        if (authoring is null || !authoring.TryUndoLastSegment())
        {
            return false;
        }

        _pointTransformBasePoint = authoring.CurrentPoint!.Value;
        _pointTransformCurrent = CreateViewport().WorldToScreen(
            _pointTransformBasePoint);
        _hasPointTransformBasePoint = true;
        ClearPointTransformSnapState();
        RefreshLineAuthoringPicture();
        LineAuthoringChanged?.Invoke(
            this,
            new CadLineAuthoringChangedEventArgs(
                CadLineAuthoringStage.SegmentUndone,
                authoring.SegmentCount,
                authoring.CurrentPoint));
        Invalidate();
        return true;
    }

    /// <summary>
    /// Ends LINE and publishes every accepted segment as one reversible edit.
    /// With no accepted segment, completion changes no document generation.
    /// </summary>
    public bool CompleteLineAuthoring(
        bool close,
        out string? errorMessage)
    {
        errorMessage = null;
        CadLineAuthoringSession? authoring = _lineAuthoring;
        if (authoring is null)
        {
            errorMessage = "No LINE command is active.";
            return false;
        }
        if (close && !authoring.CanClose)
        {
            errorMessage =
                "Close requires at least two accepted LINE segments.";
            return false;
        }

        int segmentCount = authoring.SegmentCount + (close ? 1 : 0);
        if (segmentCount == 0)
        {
            ResetLineAuthoringState();
            LineAuthoringChanged?.Invoke(
                this,
                new CadLineAuthoringChangedEventArgs(
                    CadLineAuthoringStage.Completed,
                    segmentCount: 0));
            Invalidate();
            return true;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException(
                "The CAD edit history is not initialized.");
        CadPoint3D[] points = authoring.CreatePointSnapshot(close);
        try
        {
            history.Execute(new CadAddLineSequenceCommand(
                points,
                description: segmentCount == 1
                    ? "LINE: add 1 segment"
                    : $"LINE: add {segmentCount} segments"));
            ResetLineAuthoringState();
            RecompileAfterEdit(session);
            LineAuthoringChanged?.Invoke(
                this,
                new CadLineAuthoringChangedEventArgs(
                    CadLineAuthoringStage.Completed,
                    segmentCount,
                    points[^1],
                    isClosed: close));
            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            LineAuthoringChanged?.Invoke(
                this,
                new CadLineAuthoringChangedEventArgs(
                    CadLineAuthoringStage.Failed,
                    authoring.SegmentCount,
                    authoring.CurrentPoint,
                    errorMessage: exception.Message));
            return false;
        }
    }

    /// <summary>Starts one AutoCAD-compatible single-location POINT command.</summary>
    public bool BeginPointAuthoring()
    {
        if (CurrentSession is null || CurrentSnapshot is null)
        {
            return false;
        }
        ThrowIfDrawOrderReferenceSelectionPending();
        if (IsPointAcquisitionActive)
        {
            throw new InvalidOperationException(
                "Complete the pending point-acquisition command first.");
        }

        _pointAuthoring = new CadPointAuthoringSession();
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        ClearPointTransformSnapState();
        PointAuthoringChanged?.Invoke(
            this,
            new CadPointAuthoringChangedEventArgs(
                CadPointAuthoringStage.AwaitingPoint));
        Invalidate();
        return true;
    }

    public bool CanAcceptPointAuthoringInput(string? text)
    {
        CadPointAuthoringSession? authoring = _pointAuthoring;
        if (authoring is null ||
            !CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate) ||
            coordinate.IsRelative ||
            !coordinate.TryResolve(CadPoint3D.Zero, out CadPoint3D point) ||
            !authoring.TryCreateSnapshot(point, out _, out _))
        {
            return false;
        }

        try
        {
            _ = CreateViewport().WorldToScreen(point);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool TryAcceptPointAuthoringInput(
        string? text,
        out string? errorMessage)
    {
        errorMessage = null;
        if (_pointAuthoring is null)
        {
            errorMessage = "No POINT command is awaiting point input.";
            return false;
        }
        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            errorMessage =
                "Enter an absolute x,y[,z] or distance<angle coordinate using invariant numbers.";
            return false;
        }
        if (coordinate.IsRelative)
        {
            errorMessage =
                "POINT requires an absolute coordinate because no command base point has been accepted.";
            return false;
        }
        if (!coordinate.TryResolve(CadPoint3D.Zero, out CadPoint3D point))
        {
            errorMessage = "The POINT coordinate resolves outside finite WCS values.";
            return false;
        }

        Vector2 screenPoint;
        try
        {
            screenPoint = CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException)
        {
            errorMessage =
                "The POINT location cannot be represented by the current plan viewport.";
            return false;
        }
        return TryAcceptPointAuthoringPoint(point, screenPoint, out errorMessage);
    }

    /// <summary>Cancels POINT without changing the document or history.</summary>
    public bool CancelPointAuthoring()
    {
        if (_pointAuthoring is null)
        {
            return false;
        }

        ResetPointAuthoringState();
        PointAuthoringChanged?.Invoke(
            this,
            new CadPointAuthoringChangedEventArgs(
                CadPointAuthoringStage.Canceled));
        Invalidate();
        return true;
    }

    /// <summary>
    /// Starts an AutoCAD-compatible bounded RAY sequence. The first accepted
    /// WCS point remains the start of every subsequently accepted ray.
    /// </summary>
    public bool BeginRayAuthoring(
        int maximumRayCount = CadRayAuthoringSession.DefaultMaximumRayCount)
    {
        if (CurrentSession is null || CurrentSnapshot is null)
        {
            return false;
        }
        ThrowIfDrawOrderReferenceSelectionPending();
        if (IsPointAcquisitionActive)
        {
            throw new InvalidOperationException(
                "Complete the pending point-acquisition command first.");
        }

        _rayAuthoring = new CadRayAuthoringSession(maximumRayCount);
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        ClearPointTransformSnapState();
        RefreshRayAuthoringPicture();
        RayAuthoringChanged?.Invoke(
            this,
            new CadRayAuthoringChangedEventArgs(
                CadRayAuthoringStage.AwaitingStartPoint,
                rayCount: 0));
        Invalidate();
        return true;
    }

    public bool CanAcceptRayAuthoringInput(string? text)
    {
        if (_rayAuthoring is null)
        {
            return false;
        }

        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            return CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput distance) &&
                TryResolvePointTransformDirectDistance(distance, out _);
        }
        if (!_hasPointTransformBasePoint && coordinate.IsRelative)
        {
            return false;
        }

        CadPoint3D origin = _hasPointTransformBasePoint
            ? _pointTransformBasePoint
            : CadPoint3D.Zero;
        if (!coordinate.TryResolve(origin, out CadPoint3D point) ||
            (_hasPointTransformBasePoint && point == _pointTransformBasePoint))
        {
            return false;
        }

        try
        {
            _ = CreateViewport().WorldToScreen(point);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool TryAcceptRayAuthoringInput(
        string? text,
        out string? errorMessage)
    {
        errorMessage = null;
        if (_rayAuthoring is null)
        {
            errorMessage = "No RAY command is awaiting point input.";
            return false;
        }

        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            if (CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput directDistance))
            {
                if (!_hasPointTransformBasePoint)
                {
                    errorMessage =
                        "Accept an absolute RAY start point before entering a direct distance.";
                    return false;
                }
                if (!_hasPointTransformPointerPosition)
                {
                    errorMessage =
                        "Move the cursor from the RAY start point before entering a direct distance.";
                    return false;
                }
                if (!TryResolvePointTransformDirectDistance(
                        directDistance,
                        out CadPoint3D directPoint))
                {
                    errorMessage =
                        "The cursor direction and distance do not resolve to a finite WCS point.";
                    return false;
                }

                return TryAcceptRayAuthoringPoint(
                    directPoint,
                    screenPoint: null,
                    out errorMessage);
            }

            errorMessage =
                "Enter x,y[,z], @dx,dy[,dz], distance<angle, @distance<angle, or a positive direct distance using invariant numbers.";
            return false;
        }
        if (!_hasPointTransformBasePoint && coordinate.IsRelative)
        {
            errorMessage =
                "Enter an absolute RAY start point before using a relative coordinate.";
            return false;
        }

        CadPoint3D origin = _hasPointTransformBasePoint
            ? _pointTransformBasePoint
            : CadPoint3D.Zero;
        if (!coordinate.TryResolve(origin, out CadPoint3D point))
        {
            errorMessage = "The coordinate resolves outside finite WCS values.";
            return false;
        }

        Vector2 screenPoint;
        try
        {
            screenPoint = CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException)
        {
            errorMessage =
                "The RAY point cannot be represented by the current plan viewport.";
            return false;
        }
        return TryAcceptRayAuthoringPoint(point, screenPoint, out errorMessage);
    }

    /// <summary>Removes the latest accepted ray while retaining its common start.</summary>
    public bool UndoRayAuthoringRay()
    {
        CadRayAuthoringSession? authoring = _rayAuthoring;
        if (authoring is null || !authoring.TryUndoLastRay())
        {
            return false;
        }

        _pointTransformBasePoint = authoring.StartPoint!.Value;
        _pointTransformCurrent = CreateViewport().WorldToScreen(
            _pointTransformBasePoint);
        _hasPointTransformBasePoint = true;
        ClearPointTransformSnapState();
        RefreshRayAuthoringPicture();
        RayAuthoringChanged?.Invoke(
            this,
            new CadRayAuthoringChangedEventArgs(
                CadRayAuthoringStage.RayUndone,
                authoring.RayCount,
                authoring.StartPoint));
        Invalidate();
        return true;
    }

    /// <summary>
    /// Ends RAY and publishes every accepted direction as one reversible edit.
    /// With no accepted ray, completion changes no document generation.
    /// </summary>
    public bool CompleteRayAuthoring(out string? errorMessage)
    {
        errorMessage = null;
        CadRayAuthoringSession? authoring = _rayAuthoring;
        if (authoring is null)
        {
            errorMessage = "No RAY command is active.";
            return false;
        }

        int rayCount = authoring.RayCount;
        CadPoint3D? startPoint = authoring.StartPoint;
        if (rayCount == 0)
        {
            ResetRayAuthoringState();
            RayAuthoringChanged?.Invoke(
                this,
                new CadRayAuthoringChangedEventArgs(
                    CadRayAuthoringStage.Completed,
                    rayCount: 0,
                    startPoint));
            Invalidate();
            return true;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException(
                "The CAD edit history is not initialized.");
        CadPoint3D[] directions = authoring.CreateDirectionSnapshot();
        try
        {
            history.Execute(new CadAddRaySequenceCommand(
                startPoint!.Value,
                directions,
                description: rayCount == 1
                    ? "RAY: add 1 ray"
                    : $"RAY: add {rayCount} rays"));
            ResetRayAuthoringState();
            RecompileAfterEdit(session);
            RayAuthoringChanged?.Invoke(
                this,
                new CadRayAuthoringChangedEventArgs(
                    CadRayAuthoringStage.Completed,
                    rayCount,
                    startPoint));
            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            RayAuthoringChanged?.Invoke(
                this,
                new CadRayAuthoringChangedEventArgs(
                    CadRayAuthoringStage.Failed,
                    authoring.RayCount,
                    authoring.StartPoint,
                    exception.Message));
            return false;
        }
    }

    /// <summary>Starts the default common-point two-point XLINE mode.</summary>
    public bool BeginXLineAuthoring(
        int maximumLineCount = CadXLineAuthoringSession.DefaultMaximumLineCount)
        => BeginXLineAuthoring(
            CadXLineAuthoringMode.TwoPoint,
            maximumLineCount);

    /// <summary>Starts one bounded XLINE command in the selected mode.</summary>
    public bool BeginXLineAuthoring(
        CadXLineAuthoringMode mode,
        int maximumLineCount = CadXLineAuthoringSession.DefaultMaximumLineCount)
    {
        CadDocumentSnapshot? snapshot = CurrentSnapshot;
        if (CurrentSession is null || snapshot is null ||
            !snapshot.PlanAuthoringContext.IsSupported)
        {
            return false;
        }
        ThrowIfDrawOrderReferenceSelectionPending();
        if (IsPointAcquisitionActive)
        {
            throw new InvalidOperationException(
                "Complete the pending point-acquisition command first.");
        }

        _xlineAuthoring = new CadXLineModeAuthoringSession(
            mode,
            snapshot.PlanAuthoringContext,
            snapshot.ContentGeneration,
            maximumLineCount);
        _hasXLineSourceCandidate = false;
        SynchronizeXLineAcquisitionBase();
        _isPointTransformPointerPressed = false;
        ClearPointTransformSnapState();
        RefreshXLineAuthoringPicture();
        NotifyXLineAuthoringChanged();
        Invalidate();
        return true;
    }

    public bool CanAcceptXLineAuthoringInput(string? text)
    {
        if (_xlineAuthoring is null)
        {
            return false;
        }
        string value = text?.Trim() ?? string.Empty;
        switch (_xlineAuthoring.Prompt)
        {
            case CadXLinePromptKind.AngleValue:
                return (!_xlineAuthoring.UsesReferenceAngle &&
                        IsXLineKeyword(value, "Reference", "R")) ||
                    TryParseFiniteInvariantDouble(value, out _);
            case CadXLinePromptKind.OffsetDistance:
                return IsXLineKeyword(value, "Through", "T") ||
                    (TryParseFiniteInvariantDouble(value, out double distance) &&
                        distance > 0.0);
            case CadXLinePromptKind.AngleReferenceSource:
            case CadXLinePromptKind.OffsetSource:
                return false;
        }
        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            return CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput distance) &&
                TryResolvePointTransformDirectDistance(distance, out _);
        }
        if (!_hasPointTransformBasePoint && coordinate.IsRelative)
        {
            return false;
        }
        CadPoint3D origin = _hasPointTransformBasePoint
            ? _pointTransformBasePoint
            : CadPoint3D.Zero;
        if (!coordinate.TryResolve(origin, out CadPoint3D point) ||
            (_hasPointTransformBasePoint && point == _pointTransformBasePoint))
        {
            return false;
        }
        try
        {
            _ = CreateViewport().WorldToScreen(point);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool TryAcceptXLineAuthoringInput(
        string? text,
        out string? errorMessage)
    {
        errorMessage = null;
        if (_xlineAuthoring is null)
        {
            errorMessage = "No XLINE command is awaiting point input.";
            return false;
        }
        string value = text?.Trim() ?? string.Empty;
        if (_xlineAuthoring.Prompt == CadXLinePromptKind.AngleValue)
        {
            bool accepted;
            if (!_xlineAuthoring.UsesReferenceAngle &&
                IsXLineKeyword(value, "Reference", "R"))
            {
                accepted = _xlineAuthoring.TryChooseAngleReference(
                    out errorMessage);
            }
            else if (!TryParseFiniteInvariantDouble(value, out double degrees))
            {
                errorMessage =
                    "Enter a finite invariant angle in degrees or Reference (R).";
                return false;
            }
            else
            {
                accepted = _xlineAuthoring.TryAcceptValue(
                    degrees * (Math.PI / 180.0),
                    out errorMessage);
            }
            return CompleteXLinePromptTransition(accepted, errorMessage);
        }
        if (_xlineAuthoring.Prompt == CadXLinePromptKind.OffsetDistance)
        {
            bool accepted;
            if (IsXLineKeyword(value, "Through", "T"))
            {
                accepted = _xlineAuthoring.TryChooseOffsetThrough(
                    out errorMessage);
            }
            else if (!TryParseFiniteInvariantDouble(value, out double distance))
            {
                errorMessage =
                    "Enter a positive invariant offset distance or Through (T).";
                return false;
            }
            else
            {
                accepted = _xlineAuthoring.TryAcceptValue(
                    distance,
                    out errorMessage);
            }
            return CompleteXLinePromptTransition(accepted, errorMessage);
        }
        if (_xlineAuthoring.Prompt is
            CadXLinePromptKind.AngleReferenceSource or
            CadXLinePromptKind.OffsetSource)
        {
            errorMessage = "Select a LINE, RAY, or XLINE source with the pointer.";
            return false;
        }
        CadPoint3D point;
        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            if (!CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput distance))
            {
                errorMessage =
                    "Enter x,y[,z], @dx,dy[,dz], distance<angle, @distance<angle, or a positive direct distance using invariant numbers.";
                return false;
            }
            if (!_hasPointTransformBasePoint)
            {
                errorMessage =
                    "Accept an absolute XLINE first point before entering a direct distance.";
                return false;
            }
            if (!_hasPointTransformPointerPosition ||
                !TryResolvePointTransformDirectDistance(distance, out point))
            {
                errorMessage =
                    "Move the cursor from the XLINE first point so the distance resolves to a finite WCS point.";
                return false;
            }
        }
        else
        {
            if (!_hasPointTransformBasePoint && coordinate.IsRelative)
            {
                errorMessage =
                    "Enter an absolute XLINE first point before using a relative coordinate.";
                return false;
            }
            CadPoint3D origin = _hasPointTransformBasePoint
                ? _pointTransformBasePoint
                : CadPoint3D.Zero;
            if (!coordinate.TryResolve(origin, out point))
            {
                errorMessage = "The coordinate resolves outside finite WCS values.";
                return false;
            }
        }

        Vector2 screenPoint;
        try
        {
            screenPoint = CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException)
        {
            errorMessage =
                "The XLINE point cannot be represented by the current plan viewport.";
            return false;
        }
        return TryAcceptXLineAuthoringPoint(point, screenPoint, out errorMessage);
    }

    public bool UndoXLineAuthoringLine()
    {
        CadXLineModeAuthoringSession? authoring = _xlineAuthoring;
        if (authoring is null || !authoring.TryUndoLastLine())
        {
            return false;
        }
        SynchronizeXLineAcquisitionBase();
        ClearPointTransformSnapState();
        RefreshXLineAuthoringPicture();
        XLineAuthoringChanged?.Invoke(
            this,
            new CadXLineAuthoringChangedEventArgs(
                CadXLineAuthoringStage.LineUndone,
                authoring.LineCount,
                authoring.FirstPoint,
                mode: authoring.Mode,
                prompt: authoring.Prompt));
        Invalidate();
        return true;
    }

    private bool CompleteXLinePromptTransition(
        bool accepted,
        string? errorMessage)
    {
        CadXLineModeAuthoringSession? authoring = _xlineAuthoring;
        if (authoring is null)
        {
            return false;
        }
        if (!accepted)
        {
            XLineAuthoringChanged?.Invoke(
                this,
                new CadXLineAuthoringChangedEventArgs(
                    CadXLineAuthoringStage.Failed,
                    authoring.LineCount,
                    authoring.FirstPoint,
                    errorMessage,
                    authoring.Mode,
                    authoring.Prompt));
            Invalidate();
            return false;
        }

        _hasXLineSourceCandidate = false;
        SynchronizeXLineAcquisitionBase();
        ClearPointTransformSnapState();
        RefreshXLineAuthoringPicture();
        NotifyXLineAuthoringChanged();
        Invalidate();
        return true;
    }

    private void SynchronizeXLineAcquisitionBase()
    {
        CadPoint3D? basePoint = _xlineAuthoring?.AcquisitionBasePoint;
        _hasPointTransformBasePoint = basePoint.HasValue;
        _pointTransformBasePoint = basePoint.GetValueOrDefault();
        if (basePoint.HasValue)
        {
            _pointTransformCurrent = CreateViewport().WorldToScreen(
                basePoint.Value);
        }
    }

    private void NotifyXLineAuthoringChanged()
    {
        CadXLineModeAuthoringSession? authoring = _xlineAuthoring;
        if (authoring is null)
        {
            return;
        }
        CadXLineAuthoringStage stage = authoring.Prompt switch
        {
            CadXLinePromptKind.FirstPoint =>
                CadXLineAuthoringStage.AwaitingFirstPoint,
            CadXLinePromptKind.ThroughPoint =>
                CadXLineAuthoringStage.AwaitingThroughPoint,
            _ => CadXLineAuthoringStage.AwaitingInput,
        };
        XLineAuthoringChanged?.Invoke(
            this,
            new CadXLineAuthoringChangedEventArgs(
                stage,
                authoring.LineCount,
                authoring.FirstPoint,
                mode: authoring.Mode,
                prompt: authoring.Prompt));
    }

    private static bool IsXLineKeyword(
        string value,
        string keyword,
        string abbreviation) =>
        value.Equals(keyword, StringComparison.OrdinalIgnoreCase) ||
        value.Equals(abbreviation, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseFiniteInvariantDouble(
        string value,
        out double result) =>
        double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out result) &&
        double.IsFinite(result);

    public bool CompleteXLineAuthoring(out string? errorMessage)
    {
        errorMessage = null;
        CadXLineModeAuthoringSession? authoring = _xlineAuthoring;
        if (authoring is null)
        {
            errorMessage = "No XLINE command is active.";
            return false;
        }
        int lineCount = authoring.LineCount;
        CadPoint3D? firstPoint = authoring.FirstPoint;
        if (lineCount == 0)
        {
            ResetXLineAuthoringState();
            XLineAuthoringChanged?.Invoke(
                this,
                new CadXLineAuthoringChangedEventArgs(
                    CadXLineAuthoringStage.Completed,
                    lineCount: 0,
                    firstPoint,
                    mode: authoring.Mode,
                    prompt: authoring.Prompt));
            Invalidate();
            return true;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException(
                "The CAD edit history is not initialized.");
        try
        {
            history.Execute(new CadAddXLineSequenceCommand(
                authoring.CreateDefinitionSnapshot(),
                description: lineCount == 1
                    ? "XLINE: add 1 line"
                    : $"XLINE: add {lineCount} lines"));
            ResetXLineAuthoringState();
            RecompileAfterEdit(session);
            XLineAuthoringChanged?.Invoke(
                this,
                new CadXLineAuthoringChangedEventArgs(
                    CadXLineAuthoringStage.Completed,
                    lineCount,
                    firstPoint,
                    mode: authoring.Mode,
                    prompt: authoring.Prompt));
            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            XLineAuthoringChanged?.Invoke(
                this,
                new CadXLineAuthoringChangedEventArgs(
                    CadXLineAuthoringStage.Failed,
                    authoring.LineCount,
                    authoring.FirstPoint,
                    exception.Message,
                    authoring.Mode,
                    authoring.Prompt));
            return false;
        }
    }

    /// <summary>
    /// Starts one bounded planar lightweight-polyline command. Accepted line
    /// and analytic tangent-arc segments remain transient until completion.
    /// </summary>
    public bool BeginPolylineAuthoring(
        int maximumSegmentCount =
            CadPolylineAuthoringSession.DefaultMaximumSegmentCount)
    {
        if (CurrentSession is null || CurrentSnapshot is null)
        {
            return false;
        }
        ThrowIfDrawOrderReferenceSelectionPending();
        if (IsPointAcquisitionActive)
        {
            throw new InvalidOperationException(
                "Complete the pending point-acquisition command first.");
        }

        double initialWidth = CurrentSession.Read(
            document => document.Header.PolylineWidthDefault);
        _polylineAuthoring = new CadPolylineAuthoringSession(
            maximumSegmentCount,
            initialWidth);
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        ClearPointTransformSnapState();
        RefreshPolylineAuthoringPicture();
        PolylineAuthoringChanged?.Invoke(
            this,
            new CadPolylineAuthoringChangedEventArgs(
                CadPolylineAuthoringStage.AwaitingFirstPoint,
                CadPolylineAuthoringMode.Line,
                segmentCount: 0,
                nextStartWidth: initialWidth,
                nextEndWidth: initialWidth));
        Invalidate();
        return true;
    }

    public bool BeginPolylineWidthInput(
        CadPolylineWidthInputMode mode,
        out string? errorMessage)
    {
        CadPolylineAuthoringSession? authoring = _polylineAuthoring;
        if (authoring is null)
        {
            errorMessage = "No PLINE command is active.";
            return false;
        }
        if (!authoring.TryBeginWidthInput(mode, out errorMessage))
        {
            return false;
        }

        RaisePolylineAuthoringChanged(
            CadPolylineAuthoringStage.PromptChanged,
            authoring);
        Invalidate();
        return true;
    }

    public bool BeginPolylineLengthInput(out string? errorMessage)
    {
        CadPolylineAuthoringSession? authoring = _polylineAuthoring;
        if (authoring is null)
        {
            errorMessage = "No PLINE command is active.";
            return false;
        }
        if (!authoring.TryBeginLengthInput(out errorMessage))
        {
            return false;
        }

        RaisePolylineAuthoringChanged(
            CadPolylineAuthoringStage.PromptChanged,
            authoring);
        Invalidate();
        return true;
    }

    public bool CanAcceptPolylineAuthoringInput(string? text)
    {
        CadPolylineAuthoringSession? authoring = _polylineAuthoring;
        if (authoring is null)
        {
            return false;
        }

        string value = text?.Trim() ?? string.Empty;
        if (authoring.Prompt is CadPolylineAuthoringPrompt.StartingWidth or
            CadPolylineAuthoringPrompt.EndingWidth)
        {
            if (value.Length == 0)
            {
                return true;
            }
            if (!TryParseFiniteInvariantDouble(value, out double width) || width < 0.0)
            {
                return false;
            }
            double fullWidth = authoring.WidthInputMode ==
                CadPolylineWidthInputMode.Halfwidth
                ? width * 2.0
                : width;
            return double.IsFinite(fullWidth) && fullWidth <= float.MaxValue;
        }
        if (authoring.Prompt == CadPolylineAuthoringPrompt.Length)
        {
            return TryParseFiniteInvariantDouble(value, out double length) &&
                length > 0.0;
        }
        if (IsPolylineKeyword(value, "Width", "W") ||
            IsPolylineKeyword(value, "Halfwidth", "H"))
        {
            return authoring.CanBeginWidthInput;
        }
        if (IsPolylineKeyword(value, "Arc", "A"))
        {
            return authoring.SegmentCount > 0;
        }
        if (IsPolylineKeyword(value, "Undo", "U"))
        {
            return authoring.CanUndo;
        }
        if (IsPolylineKeyword(value, "Close", "C"))
        {
            return authoring.CanClose;
        }
        if (IsPolylineKeyword(value, "Length", "L"))
        {
            return authoring.Mode == CadPolylineAuthoringMode.TangentArc ||
                authoring.CanBeginLengthInput;
        }
        if (value.Equals("Line", StringComparison.OrdinalIgnoreCase))
        {
            return authoring.Mode == CadPolylineAuthoringMode.TangentArc;
        }

        CadPoint3D point;
        if (!CadCoordinateInput.TryParse(value, out CadCoordinateInput coordinate))
        {
            if (!CadDirectDistanceInput.TryParse(
                    value,
                    out CadDirectDistanceInput distance) ||
                !TryResolvePointTransformDirectDistance(distance, out point))
            {
                return false;
            }
        }
        else
        {
            if (!_hasPointTransformBasePoint && coordinate.IsRelative)
            {
                return false;
            }
            CadPoint3D origin = _hasPointTransformBasePoint
                ? _pointTransformBasePoint
                : CadPoint3D.Zero;
            if (!coordinate.TryResolve(origin, out point))
            {
                return false;
            }
        }

        if (authoring.HasFirstPoint &&
            !authoring.TryGetPendingBulge(point, out _))
        {
            return false;
        }
        try
        {
            _ = CreateViewport().WorldToScreen(point);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool TryAcceptPolylineAuthoringInput(
        string? text,
        out string? errorMessage)
    {
        errorMessage = null;
        CadPolylineAuthoringSession? authoring = _polylineAuthoring;
        if (authoring is null)
        {
            errorMessage = "No PLINE command is awaiting point input.";
            return false;
        }

        string value = text?.Trim() ?? string.Empty;
        if (authoring.Prompt is CadPolylineAuthoringPrompt.StartingWidth or
            CadPolylineAuthoringPrompt.EndingWidth)
        {
            bool accepted;
            if (value.Length == 0)
            {
                accepted = authoring.TryAcceptDefaultWidthValue(out errorMessage);
            }
            else if (!TryParseFiniteInvariantDouble(value, out double width))
            {
                errorMessage =
                    "Enter a finite non-negative PLINE width using an invariant number, or leave the input empty for the displayed default.";
                return false;
            }
            else
            {
                accepted = authoring.TryAcceptWidthValue(width, out errorMessage);
            }
            if (!accepted)
            {
                return false;
            }
            RaisePolylineAuthoringChanged(
                authoring.Prompt == CadPolylineAuthoringPrompt.Point
                    ? CadPolylineAuthoringStage.AwaitingNextPoint
                    : CadPolylineAuthoringStage.PromptChanged,
                authoring);
            Invalidate();
            return true;
        }
        if (authoring.Prompt == CadPolylineAuthoringPrompt.Length)
        {
            if (!TryParseFiniteInvariantDouble(value, out double length))
            {
                errorMessage =
                    "Enter a finite positive PLINE line length using an invariant number.";
                return false;
            }
            if (!authoring.TryGetLengthEndpoint(
                    length,
                    out CadPoint3D endpoint,
                    out errorMessage))
            {
                return false;
            }
            Vector2 endpointScreen;
            try
            {
                endpointScreen = CreateViewport().WorldToScreen(endpoint);
            }
            catch (ArgumentException)
            {
                errorMessage =
                    "The PLINE length endpoint cannot be represented by the current plan viewport.";
                return false;
            }
            if (!authoring.TryAcceptLength(length, out errorMessage))
            {
                return false;
            }
            return SynchronizeAcceptedPolylinePoint(
                authoring,
                out errorMessage,
                endpointScreen);
        }
        if (IsPolylineKeyword(value, "Width", "W"))
        {
            return BeginPolylineWidthInput(
                CadPolylineWidthInputMode.Width,
                out errorMessage);
        }
        if (IsPolylineKeyword(value, "Halfwidth", "H"))
        {
            return BeginPolylineWidthInput(
                CadPolylineWidthInputMode.Halfwidth,
                out errorMessage);
        }
        if (IsPolylineKeyword(value, "Arc", "A"))
        {
            if (authoring.SegmentCount == 0)
            {
                errorMessage = "Accept one PLINE segment before entering tangent Arc mode.";
                return false;
            }
            PolylineAuthoringMode = CadPolylineAuthoringMode.TangentArc;
            return true;
        }
        if (value.Equals("Line", StringComparison.OrdinalIgnoreCase) ||
            (value.Equals("L", StringComparison.OrdinalIgnoreCase) &&
             authoring.Mode == CadPolylineAuthoringMode.TangentArc))
        {
            PolylineAuthoringMode = CadPolylineAuthoringMode.Line;
            return true;
        }
        if (IsPolylineKeyword(value, "Length", "L"))
        {
            return BeginPolylineLengthInput(out errorMessage);
        }
        if (IsPolylineKeyword(value, "Undo", "U"))
        {
            if (!UndoPolylineAuthoringSegment())
            {
                errorMessage = "PLINE has no accepted segment to undo.";
                return false;
            }
            return true;
        }
        if (IsPolylineKeyword(value, "Close", "C"))
        {
            return CompletePolylineAuthoring(close: true, out errorMessage);
        }

        CadPoint3D point;
        if (!CadCoordinateInput.TryParse(value, out CadCoordinateInput coordinate))
        {
            if (!CadDirectDistanceInput.TryParse(
                    value,
                    out CadDirectDistanceInput directDistance))
            {
                errorMessage =
                    "Enter x,y[,z], @dx,dy[,dz], distance<angle, @distance<angle, or a positive direct distance using invariant numbers.";
                return false;
            }
            if (!_hasPointTransformBasePoint)
            {
                errorMessage =
                    "Accept an absolute first point before entering a direct distance.";
                return false;
            }
            if (!_hasPointTransformPointerPosition)
            {
                errorMessage =
                    "Move the cursor from the current PLINE point before entering a direct distance.";
                return false;
            }
            if (!TryResolvePointTransformDirectDistance(
                    directDistance,
                    out point))
            {
                errorMessage =
                    "The cursor direction and distance do not resolve to a finite WCS point.";
                return false;
            }
        }
        else
        {
            if (!_hasPointTransformBasePoint && coordinate.IsRelative)
            {
                errorMessage =
                    "Enter an absolute first point before using a relative coordinate.";
                return false;
            }
            CadPoint3D origin = _hasPointTransformBasePoint
                ? _pointTransformBasePoint
                : CadPoint3D.Zero;
            if (!coordinate.TryResolve(origin, out point))
            {
                errorMessage = "The coordinate resolves outside finite WCS values.";
                return false;
            }
        }

        Vector2 screenPoint;
        try
        {
            screenPoint = CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException)
        {
            errorMessage =
                "The PLINE point cannot be represented by the current plan viewport.";
            return false;
        }
        return TryAcceptPolylineAuthoringPoint(
            point,
            screenPoint,
            out errorMessage);
    }

    private static bool IsPolylineKeyword(
        string value,
        string keyword,
        string abbreviation) =>
        value.Equals(keyword, StringComparison.OrdinalIgnoreCase) ||
        value.Equals(abbreviation, StringComparison.OrdinalIgnoreCase);

    public bool UndoPolylineAuthoringSegment()
    {
        CadPolylineAuthoringSession? authoring = _polylineAuthoring;
        if (authoring is null || !authoring.TryUndoLastSegment())
        {
            return false;
        }

        _pointTransformBasePoint = authoring.CurrentPoint!.Value;
        _pointTransformCurrent = CreateViewport().WorldToScreen(
            _pointTransformBasePoint);
        _hasPointTransformBasePoint = true;
        ClearPointTransformSnapState();
        RefreshPolylineAuthoringPicture();
        RaisePolylineAuthoringChanged(
            CadPolylineAuthoringStage.SegmentUndone,
            authoring);
        Invalidate();
        return true;
    }

    public bool CompletePolylineAuthoring(
        bool close,
        out string? errorMessage)
    {
        errorMessage = null;
        CadPolylineAuthoringSession? authoring = _polylineAuthoring;
        if (authoring is null)
        {
            errorMessage = "No PLINE command is active.";
            return false;
        }
        if (authoring.SegmentCount == 0 && !close)
        {
            ResetPolylineAuthoringState();
            PolylineAuthoringChanged?.Invoke(
                this,
                new CadPolylineAuthoringChangedEventArgs(
                    CadPolylineAuthoringStage.Completed,
                    authoring.Mode,
                    segmentCount: 0));
            Invalidate();
            return true;
        }
        if (!authoring.TryCreateSnapshot(
                close,
                out CadPolylineAuthoringSnapshot? snapshot,
                out errorMessage))
        {
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException(
                "The CAD edit history is not initialized.");
        try
        {
            history.Execute(new CadAddPolylineCommand(
                snapshot!,
                description: $"PLINE: add {snapshot!.SegmentCount} segments"));
            int segmentCount = snapshot.SegmentCount;
            CadPolylineAuthoringMode mode = authoring.Mode;
            CadPoint3D currentPoint = authoring.CurrentPoint!.Value;
            ResetPolylineAuthoringState();
            RecompileAfterEdit(session);
            PolylineAuthoringChanged?.Invoke(
                this,
                new CadPolylineAuthoringChangedEventArgs(
                    CadPolylineAuthoringStage.Completed,
                    mode,
                    segmentCount,
                    currentPoint,
                    isClosed: close));
            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            PolylineAuthoringChanged?.Invoke(
                this,
                new CadPolylineAuthoringChangedEventArgs(
                    CadPolylineAuthoringStage.Failed,
                    authoring.Mode,
                    authoring.SegmentCount,
                    authoring.CurrentPoint,
                    errorMessage: exception.Message));
            return false;
        }
    }

    /// <summary>
    /// Starts one bounded plan-view CIRCLE using an exact center/radius,
    /// center/diameter, two-diameter-point, or three-circumference-point solve.
    /// </summary>
    public bool BeginCircleAuthoring(CadCircleAuthoringMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (CurrentSession is null || CurrentSnapshot is null)
        {
            return false;
        }
        ThrowIfDrawOrderReferenceSelectionPending();
        if (IsPointAcquisitionActive)
        {
            throw new InvalidOperationException(
                "Complete the pending point-acquisition command first.");
        }

        _circleAuthoring = new CadCircleAuthoringSession(mode);
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        ClearPointTransformSnapState();
        CircleAuthoringChanged?.Invoke(
            this,
            new CadCircleAuthoringChangedEventArgs(
                CadCircleAuthoringStage.AwaitingFirstPoint,
                mode,
                pointCount: 0));
        Invalidate();
        return true;
    }

    public bool CanAcceptCircleAuthoringInput(string? text)
    {
        CadCircleAuthoringSession? authoring = _circleAuthoring;
        if (authoring is null)
        {
            return false;
        }

        CadPoint3D point;
        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            if (!CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput distance))
            {
                return false;
            }
            if (authoring.TryCreateSnapshotFromScalar(
                    distance.Distance,
                    out CadCircleAuthoringSnapshot scalarSnapshot,
                    out _))
            {
                try
                {
                    _ = CreateViewport().WorldToScreen(scalarSnapshot.Center);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
            if (!TryResolvePointTransformDirectDistance(distance, out point))
            {
                return false;
            }
        }
        else
        {
            if (!_hasPointTransformBasePoint && coordinate.IsRelative)
            {
                return false;
            }
            CadPoint3D origin = _hasPointTransformBasePoint
                ? _pointTransformBasePoint
                : CadPoint3D.Zero;
            if (!coordinate.TryResolve(origin, out point))
            {
                return false;
            }
        }

        if (!authoring.CanAcceptPoint(point))
        {
            return false;
        }
        try
        {
            _ = CreateViewport().WorldToScreen(point);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool TryAcceptCircleAuthoringInput(
        string? text,
        out string? errorMessage)
    {
        errorMessage = null;
        CadCircleAuthoringSession? authoring = _circleAuthoring;
        if (authoring is null)
        {
            errorMessage = "No CIRCLE command is awaiting point input.";
            return false;
        }

        CadPoint3D point;
        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            if (!CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput directDistance))
            {
                errorMessage =
                    "Enter x,y[,z], @dx,dy[,dz], distance<angle, @distance<angle, or a positive direct distance using invariant numbers.";
                return false;
            }
            if (authoring.PointCount == 1 &&
                authoring.Mode is (CadCircleAuthoringMode.CenterRadius or
                    CadCircleAuthoringMode.CenterDiameter))
            {
                if (!authoring.TryCreateSnapshotFromScalar(
                        directDistance.Distance,
                        out CadCircleAuthoringSnapshot scalarSnapshot,
                        out errorMessage))
                {
                    return false;
                }
                return TryCommitCircleAuthoringSnapshot(
                    authoring,
                    scalarSnapshot,
                    finalPoint: authoring.CurrentPoint,
                    out errorMessage);
            }
            if (!_hasPointTransformBasePoint)
            {
                errorMessage =
                    "Accept an absolute first point before entering a direct distance.";
                return false;
            }
            if (!_hasPointTransformPointerPosition)
            {
                errorMessage =
                    "Move the cursor from the current CIRCLE point before entering a direct distance.";
                return false;
            }
            if (!TryResolvePointTransformDirectDistance(
                    directDistance,
                    out point))
            {
                errorMessage =
                    "The cursor direction and distance do not resolve to a finite WCS point.";
                return false;
            }
        }
        else
        {
            if (!_hasPointTransformBasePoint && coordinate.IsRelative)
            {
                errorMessage =
                    "Enter an absolute first point before using a relative coordinate.";
                return false;
            }
            CadPoint3D origin = _hasPointTransformBasePoint
                ? _pointTransformBasePoint
                : CadPoint3D.Zero;
            if (!coordinate.TryResolve(origin, out point))
            {
                errorMessage = "The coordinate resolves outside finite WCS values.";
                return false;
            }
        }

        Vector2 screenPoint;
        try
        {
            screenPoint = CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException)
        {
            errorMessage =
                "The CIRCLE point cannot be represented by the current plan viewport.";
            return false;
        }
        return TryAcceptCircleAuthoringPoint(point, screenPoint, out errorMessage);
    }

    /// <summary>Cancels CIRCLE without changing the document or history.</summary>
    public bool CancelCircleAuthoring()
    {
        CadCircleAuthoringSession? authoring = _circleAuthoring;
        if (authoring is null)
        {
            return false;
        }

        CadCircleAuthoringMode mode = authoring.Mode;
        int pointCount = authoring.PointCount;
        CadPoint3D? currentPoint = authoring.CurrentPoint;
        ResetCircleAuthoringState();
        CircleAuthoringChanged?.Invoke(
            this,
            new CadCircleAuthoringChangedEventArgs(
                CadCircleAuthoringStage.Canceled,
                mode,
                pointCount,
                currentPoint));
        Invalidate();
        return true;
    }

    /// <summary>
    /// Starts one bounded plan-view ARC using any independent point, center,
    /// included-angle, chord, tangent-direction, or radius construction.
    /// </summary>
    public bool BeginArcAuthoring(CadArcAuthoringMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (CurrentSession is null || CurrentSnapshot is null)
        {
            return false;
        }
        ThrowIfDrawOrderReferenceSelectionPending();
        if (IsPointAcquisitionActive)
        {
            throw new InvalidOperationException(
                "Complete the pending point-acquisition command first.");
        }

        _arcAuthoring = new CadArcAuthoringSession(mode);
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        ClearPointTransformSnapState();
        ArcAuthoringChanged?.Invoke(
            this,
            new CadArcAuthoringChangedEventArgs(
                CadArcAuthoringStage.AwaitingFirstPoint,
                mode,
                pointCount: 0));
        Invalidate();
        return true;
    }

    public bool CanAcceptArcAuthoringInput(string? text)
    {
        CadArcAuthoringSession? authoring = _arcAuthoring;
        if (authoring is null)
        {
            return false;
        }

        if (authoring.PointCount == 2 &&
            authoring.AcceptsScalarFinalInput &&
            CadArcScalarInput.TryParse(text, out CadArcScalarInput scalar))
        {
            return TryResolveArcScalarSnapshot(
                    authoring,
                    scalar.Value,
                    out CadArcAuthoringSnapshot snapshot,
                    out _) &&
                CanRepresentArcSnapshot(snapshot);
        }

        CadPoint3D point;
        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            if (!CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput distance) ||
                !TryResolvePointTransformDirectDistance(distance, out point))
            {
                return false;
            }
        }
        else
        {
            if (!_hasPointTransformBasePoint && coordinate.IsRelative)
            {
                return false;
            }
            CadPoint3D origin = _hasPointTransformBasePoint
                ? _pointTransformBasePoint
                : CadPoint3D.Zero;
            if (!coordinate.TryResolve(origin, out point))
            {
                return false;
            }
        }

        if (!authoring.CanAcceptPoint(point))
        {
            return false;
        }
        try
        {
            _ = CreateViewport().WorldToScreen(point);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool TryAcceptArcAuthoringInput(
        string? text,
        out string? errorMessage)
    {
        errorMessage = null;
        CadArcAuthoringSession? authoring = _arcAuthoring;
        if (authoring is null)
        {
            errorMessage = "No ARC command is awaiting input.";
            return false;
        }

        if (authoring.PointCount == 2 &&
            authoring.AcceptsScalarFinalInput &&
            CadArcScalarInput.TryParse(text, out CadArcScalarInput scalar))
        {
            if (!TryResolveArcScalarSnapshot(
                    authoring,
                    scalar.Value,
                    out CadArcAuthoringSnapshot scalarSnapshot,
                    out errorMessage))
            {
                NotifyArcAuthoringFailure(authoring, errorMessage);
                return false;
            }
            return TryCommitArcAuthoringSnapshot(
                authoring,
                scalarSnapshot,
                finalPoint: null,
                out errorMessage);
        }

        CadPoint3D point;
        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            if (!CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput directDistance))
            {
                errorMessage = authoring.PointCount == 2 &&
                    authoring.AcceptsScalarFinalInput
                    ? "Enter the requested signed ARC angle in degrees or signed chord/radius using invariant numbers."
                    : "Enter x,y[,z], @dx,dy[,dz], distance<angle, @distance<angle, or a positive direct distance using invariant numbers.";
                return false;
            }
            if (!_hasPointTransformBasePoint)
            {
                errorMessage =
                    "Accept an absolute first point before entering a direct distance.";
                return false;
            }
            if (!_hasPointTransformPointerPosition)
            {
                errorMessage =
                    "Move the cursor from the current ARC point before entering a direct distance.";
                return false;
            }
            if (!TryResolvePointTransformDirectDistance(
                    directDistance,
                    out point))
            {
                errorMessage =
                    "The cursor direction and distance do not resolve to a finite WCS point.";
                return false;
            }
        }
        else
        {
            if (!_hasPointTransformBasePoint && coordinate.IsRelative)
            {
                errorMessage =
                    "Enter an absolute first point before using a relative coordinate.";
                return false;
            }
            CadPoint3D origin = _hasPointTransformBasePoint
                ? _pointTransformBasePoint
                : CadPoint3D.Zero;
            if (!coordinate.TryResolve(origin, out point))
            {
                errorMessage = "The coordinate resolves outside finite WCS values.";
                return false;
            }
        }

        Vector2 screenPoint;
        try
        {
            screenPoint = CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException)
        {
            errorMessage =
                "The ARC point cannot be represented by the current plan viewport.";
            return false;
        }
        return TryAcceptArcAuthoringPoint(point, screenPoint, out errorMessage);
    }

    /// <summary>Cancels ARC without changing the document or history.</summary>
    public bool CancelArcAuthoring()
    {
        CadArcAuthoringSession? authoring = _arcAuthoring;
        if (authoring is null)
        {
            return false;
        }

        CadArcAuthoringMode mode = authoring.Mode;
        int pointCount = authoring.PointCount;
        CadPoint3D? currentPoint = authoring.CurrentPoint;
        ResetArcAuthoringState();
        ArcAuthoringChanged?.Invoke(
            this,
            new CadArcAuthoringChangedEventArgs(
                CadArcAuthoringStage.Canceled,
                mode,
                pointCount,
                currentPoint));
        Invalidate();
        return true;
    }

    private bool TryResolveArcScalarSnapshot(
        CadArcAuthoringSession authoring,
        double value,
        out CadArcAuthoringSnapshot snapshot,
        out string? errorMessage)
    {
        if (authoring.ScalarInputKind ==
            CadArcScalarInputKind.DirectionAngleRadians)
        {
            double reducedDegrees = Math.IEEERemainder(value, 360.0);
            double radians = reducedDegrees * (Math.PI / 180.0);
            double sine = Math.Sin(radians);
            if (_planPolarTrackingSettings.IsClockwise)
            {
                sine = -sine;
            }
            CadPoint3D direction =
                (_planPolarTrackingSettings.XAxis * Math.Cos(radians)) +
                (_planPolarTrackingSettings.YAxis * sine);
            return authoring.TryCreateSnapshotFromDirection(
                direction,
                out snapshot,
                out errorMessage);
        }

        double scalar = value;
        if (authoring.ScalarInputKind ==
            CadArcScalarInputKind.IncludedAngleRadians)
        {
            scalar = value * (Math.PI / 180.0);
            if (_planPolarTrackingSettings.IsClockwise)
            {
                scalar = -scalar;
            }
        }
        return authoring.TryCreateSnapshotFromScalar(
            scalar,
            out snapshot,
            out errorMessage);
    }

    private bool CanRepresentArcSnapshot(CadArcAuthoringSnapshot snapshot)
    {
        try
        {
            CadPlanViewport viewport = CreateViewport();
            _ = viewport.WorldToScreen(snapshot.Center);
            _ = viewport.WorldToScreen(snapshot.StartPoint);
            _ = viewport.WorldToScreen(snapshot.EndPoint);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Starts one bounded regular plan-view POLYGON.</summary>
    public bool BeginPolygonAuthoring(
        int sideCount,
        CadPolygonAuthoringMode mode)
    {
        _ = new CadPolygonSideCount(sideCount);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (CurrentSession is null || CurrentSnapshot is null)
        {
            return false;
        }
        ThrowIfDrawOrderReferenceSelectionPending();
        if (IsPointAcquisitionActive)
        {
            throw new InvalidOperationException(
                "Complete the pending point-acquisition command first.");
        }

        GpuPicture preview = CreatePolygonAuthoringPicture(sideCount);
        _polygonAuthoring = new CadPolygonAuthoringSession(sideCount, mode);
        _polygonAuthoringPicture = preview;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        ClearPointTransformSnapState();
        PolygonAuthoringChanged?.Invoke(
            this,
            new CadPolygonAuthoringChangedEventArgs(
                CadPolygonAuthoringStage.AwaitingFirstPoint,
                sideCount,
                mode,
                _polygonAuthoring.InputKind,
                acceptedInputCount: 0));
        Invalidate();
        return true;
    }

    public bool CanAcceptPolygonAuthoringInput(string? text)
    {
        CadPolygonAuthoringSession? authoring = _polygonAuthoring;
        if (authoring is null)
        {
            return false;
        }

        if (authoring.AcceptsScalarInput &&
            CadDirectDistanceInput.TryParse(
                text,
                out CadDirectDistanceInput radius) &&
            TryGetPolygonBottomDirection(out CadPoint3D bottomDirection) &&
            authoring.TryCreateFromRadius(
                radius.Distance,
                bottomDirection,
                out CadPolygonAuthoringSnapshot scalarSnapshot,
                out _))
        {
            return CanRepresentPolygonSnapshot(scalarSnapshot);
        }

        if (!TryResolvePolygonPointInput(text, out CadPoint3D point, out _))
        {
            return false;
        }
        if (!authoring.CanAcceptPoint(point))
        {
            return false;
        }
        if (authoring.TryPreviewPoint(point, out CadPolygonAuthoringSnapshot snapshot))
        {
            return CanRepresentPolygonSnapshot(snapshot);
        }
        try
        {
            _ = CreateViewport().WorldToScreen(point);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool TryAcceptPolygonAuthoringInput(
        string? text,
        out string? errorMessage)
    {
        errorMessage = null;
        CadPolygonAuthoringSession? authoring = _polygonAuthoring;
        if (authoring is null)
        {
            errorMessage = "No POLYGON command is awaiting input.";
            return false;
        }

        if (authoring.AcceptsScalarInput &&
            CadDirectDistanceInput.TryParse(
                text,
                out CadDirectDistanceInput radius))
        {
            if (!TryGetPolygonBottomDirection(out CadPoint3D bottomDirection) ||
                !authoring.TryCreateFromRadius(
                    radius.Distance,
                    bottomDirection,
                    out CadPolygonAuthoringSnapshot scalarSnapshot,
                    out errorMessage))
            {
                NotifyPolygonAuthoringFailure(authoring, errorMessage);
                return false;
            }
            return TryCommitPolygonAuthoringSnapshot(
                authoring,
                scalarSnapshot,
                finalPoint: null,
                out errorMessage);
        }

        if (!TryResolvePolygonPointInput(text, out CadPoint3D point, out errorMessage))
        {
            return false;
        }
        Vector2 screenPoint;
        try
        {
            screenPoint = CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException)
        {
            errorMessage =
                "The POLYGON point cannot be represented by the current plan viewport.";
            return false;
        }
        return TryAcceptPolygonAuthoringPoint(point, screenPoint, out errorMessage);
    }

    /// <summary>Cancels POLYGON without changing the document or history.</summary>
    public bool CancelPolygonAuthoring()
    {
        CadPolygonAuthoringSession? authoring = _polygonAuthoring;
        if (authoring is null)
        {
            return false;
        }

        int sideCount = authoring.SideCount;
        CadPolygonAuthoringMode mode = authoring.Mode;
        CadPolygonAuthoringInputKind inputKind = authoring.InputKind;
        int acceptedInputCount = authoring.AcceptedInputCount;
        CadPoint3D? currentPoint = authoring.CurrentPoint;
        ResetPolygonAuthoringState();
        PolygonAuthoringChanged?.Invoke(
            this,
            new CadPolygonAuthoringChangedEventArgs(
                CadPolygonAuthoringStage.Canceled,
                sideCount,
                mode,
                inputKind,
                acceptedInputCount,
                currentPoint));
        Invalidate();
        return true;
    }

    private bool TryResolvePolygonPointInput(
        string? text,
        out CadPoint3D point,
        out string? errorMessage)
    {
        point = default;
        errorMessage = null;
        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            if (!CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput distance))
            {
                errorMessage =
                    "Enter x,y[,z], @dx,dy[,dz], distance<angle, @distance<angle, or a positive direct distance using invariant numbers.";
                return false;
            }
            if (!_hasPointTransformBasePoint)
            {
                errorMessage =
                    "Accept an absolute first point before entering a direct distance.";
                return false;
            }
            if (!_hasPointTransformPointerPosition)
            {
                errorMessage =
                    "Move the cursor from the current POLYGON point before entering an edge distance.";
                return false;
            }
            if (!TryResolvePointTransformDirectDistance(distance, out point))
            {
                errorMessage =
                    "The cursor direction and distance do not resolve to a finite WCS point.";
                return false;
            }
            return true;
        }

        if (!_hasPointTransformBasePoint && coordinate.IsRelative)
        {
            errorMessage =
                "Enter an absolute first point before using a relative coordinate.";
            return false;
        }
        CadPoint3D origin = _hasPointTransformBasePoint
            ? _pointTransformBasePoint
            : CadPoint3D.Zero;
        if (!coordinate.TryResolve(origin, out point))
        {
            errorMessage = "The coordinate resolves outside finite WCS values.";
            return false;
        }
        return true;
    }

    private bool TryGetPolygonBottomDirection(out CadPoint3D direction)
    {
        CadPlanGridSnapSettings settings = _planGridSnapSettings;
        CadPoint3D rectangularY;
        if (settings.Style == CadPlanGridSnapStyle.Rectangular)
        {
            rectangularY = settings.YAxis;
        }
        else
        {
            rectangularY = settings.Isoplane switch
            {
                CadPlanIsoplane.Left => settings.XAxis,
                CadPlanIsoplane.Top => settings.XAxis + settings.YAxis,
                CadPlanIsoplane.Right => settings.YAxis,
                _ => default,
            };
        }
        direction = rectangularY * -1.0;
        return double.IsFinite(direction.X) &&
            double.IsFinite(direction.Y) &&
            ((direction.X * direction.X) +
                (direction.Y * direction.Y)) > 0.0;
    }

    private GpuPicture CreatePolygonAuthoringPicture(int sideCount)
    {
        var recorder = new GpuPictureRecorder();
        DrawingContext target = recorder.BeginRecording(new Rect(-2, -2, 4, 4));
        try
        {
            double step = (Math.PI * 2.0) / sideCount;
            var path = new PathGeometry();
            var figure = new PathFigure(new Vector2(1.0f, 0.0f))
            {
                IsFilled = false,
                IsClosed = true,
            };
            for (int index = 1; index < sideCount; index++)
            {
                double angle = index * step;
                figure.Segments.Add(new LineSegment(new Vector2(
                    (float)Math.Cos(angle),
                    (float)Math.Sin(angle))));
            }
            path.Figures.Add(figure);
            target.DrawPath(null, _drawOrderReferencePen, path);
            return recorder.EndRecording();
        }
        catch
        {
            target.Clear();
            throw;
        }
    }

    private bool CanRepresentPolygonSnapshot(CadPolygonAuthoringSnapshot snapshot)
    {
        try
        {
            CadPlanViewport viewport = CreateViewport();
            _ = viewport.WorldToScreen(snapshot.Center);
            _ = viewport.WorldToScreen(snapshot.VertexAt(0));
            _ = viewport.WorldToScreen(snapshot.VertexAt(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts one bounded plan-view RECTANG using typed scalar construction
    /// and profile-scoped corner/rotation settings.
    /// </summary>
    public bool BeginRectangleAuthoring(
        CadRectangleConstruction construction,
        CadRectangleCornerTreatment cornerTreatment,
        double rotationDegrees)
    {
        if (!double.IsFinite(rotationDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(rotationDegrees));
        }
        if (CurrentSession is null || CurrentSnapshot is null)
        {
            return false;
        }
        ThrowIfDrawOrderReferenceSelectionPending();
        if (IsPointAcquisitionActive)
        {
            throw new InvalidOperationException(
                "Complete the pending point-acquisition command first.");
        }

        double rotationRadians = Math.Atan2(
            _planPolarTrackingSettings.XAxis.Y,
            _planPolarTrackingSettings.XAxis.X);
        double enteredRotation = rotationDegrees * (Math.PI / 180.0);
        if (_planPolarTrackingSettings.IsClockwise)
        {
            enteredRotation = -enteredRotation;
        }
        rotationRadians += enteredRotation;
        var authoring = new CadRectangleAuthoringSession(
            rotationRadians,
            cornerTreatment,
            construction);
        _rectangleAuthoring = authoring;
        _rectangleRotationRadians = authoring.RotationRadians;
        _rectangleCornerTreatment = cornerTreatment;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        ClearPointTransformSnapState();
        RectangleAuthoringChanged?.Invoke(
            this,
            new CadRectangleAuthoringChangedEventArgs(
                CadRectangleAuthoringStage.AwaitingFirstCorner,
                construction,
                cornerTreatment,
                authoring.RotationRadians,
                authoring.InputKind,
                acceptedInputCount: 0));
        Invalidate();
        return true;
    }

    public bool CanAcceptRectangleAuthoringInput(string? text)
    {
        CadRectangleAuthoringSession? authoring = _rectangleAuthoring;
        if (authoring is null ||
            !TryResolveRectanglePointInput(text, out CadPoint3D point, out _) ||
            !authoring.CanAcceptPoint(point))
        {
            return false;
        }
        if (authoring.TryPreviewPoint(
                point,
                out CadRectangleAuthoringSnapshot snapshot))
        {
            return CanRepresentRectangleSnapshot(snapshot);
        }
        try
        {
            _ = CreateViewport().WorldToScreen(point);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool TryAcceptRectangleAuthoringInput(
        string? text,
        out string? errorMessage)
    {
        errorMessage = null;
        if (_rectangleAuthoring is null)
        {
            errorMessage = "No RECTANG command is awaiting input.";
            return false;
        }
        if (!TryResolveRectanglePointInput(
                text,
                out CadPoint3D point,
                out errorMessage))
        {
            return false;
        }
        Vector2 screenPoint;
        try
        {
            screenPoint = CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException)
        {
            errorMessage =
                "The RECTANG point cannot be represented by the current plan viewport.";
            return false;
        }
        return TryAcceptRectangleAuthoringPoint(
            point,
            screenPoint,
            out errorMessage);
    }

    /// <summary>Cancels RECTANG without changing the document or history.</summary>
    public bool CancelRectangleAuthoring()
    {
        CadRectangleAuthoringSession? authoring = _rectangleAuthoring;
        if (authoring is null)
        {
            return false;
        }

        CadRectangleAuthoringInputKind inputKind = authoring.InputKind;
        int acceptedInputCount = authoring.AcceptedInputCount;
        CadPoint3D? currentPoint = authoring.CurrentPoint;
        CadRectangleConstruction construction = authoring.Construction;
        CadRectangleCornerTreatment cornerTreatment =
            authoring.CornerTreatment;
        double rotationRadians = authoring.RotationRadians;
        ResetRectangleAuthoringState();
        RectangleAuthoringChanged?.Invoke(
            this,
            new CadRectangleAuthoringChangedEventArgs(
                CadRectangleAuthoringStage.Canceled,
                construction,
                cornerTreatment,
                rotationRadians,
                inputKind,
                acceptedInputCount,
                currentPoint));
        Invalidate();
        return true;
    }

    private bool TryResolveRectanglePointInput(
        string? text,
        out CadPoint3D point,
        out string? errorMessage)
    {
        point = default;
        errorMessage = null;
        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            if (!CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput distance))
            {
                errorMessage =
                    "Enter x,y[,z], @dx,dy[,dz], distance<angle, @distance<angle, or a positive direct distance using invariant numbers.";
                return false;
            }
            if (!_hasPointTransformBasePoint)
            {
                errorMessage =
                    "Accept an absolute first corner before entering a direct distance.";
                return false;
            }
            if (!_hasPointTransformPointerPosition)
            {
                errorMessage =
                    "Move the cursor from the first RECTANG corner before entering a direct distance.";
                return false;
            }
            if (!TryResolvePointTransformDirectDistance(distance, out point))
            {
                errorMessage =
                    "The cursor direction and distance do not resolve to a finite WCS point.";
                return false;
            }
            return true;
        }

        if (!_hasPointTransformBasePoint && coordinate.IsRelative)
        {
            errorMessage =
                "Enter an absolute first corner before using a relative coordinate.";
            return false;
        }
        CadPoint3D origin = _hasPointTransformBasePoint
            ? _pointTransformBasePoint
            : CadPoint3D.Zero;
        if (!coordinate.TryResolve(origin, out point))
        {
            errorMessage = "The coordinate resolves outside finite WCS values.";
            return false;
        }
        return true;
    }

    private bool CanRepresentRectangleSnapshot(
        CadRectangleAuthoringSnapshot snapshot)
    {
        try
        {
            CadPlanViewport viewport = CreateViewport();
            Span<CadPoint3D> points = stackalloc CadPoint3D[8];
            Span<double> bulges = stackalloc double[8];
            int count = snapshot.CopyContour(points, bulges);
            for (int index = 0; index < count; index++)
            {
                _ = viewport.WorldToScreen(points[index]);
            }
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts one bounded plan-view ELLIPSE or elliptical arc using the complete
    /// Axis/Center, Distance/Rotation, and endpoint-interpretation matrix.
    /// </summary>
    public bool BeginEllipseAuthoring(
        CadEllipseAuthoringMode mode,
        CadEllipseArcInputMode arcInputMode = CadEllipseArcInputMode.Full)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (!Enum.IsDefined(arcInputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(arcInputMode));
        }
        if (CurrentSession is null || CurrentSnapshot is null)
        {
            return false;
        }
        ThrowIfDrawOrderReferenceSelectionPending();
        if (IsPointAcquisitionActive)
        {
            throw new InvalidOperationException(
                "Complete the pending point-acquisition command first.");
        }

        _ellipseAuthoring = new CadEllipseAuthoringSession(
            mode,
            arcInputMode);
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        ClearPointTransformSnapState();
        EllipseAuthoringChanged?.Invoke(
            this,
            new CadEllipseAuthoringChangedEventArgs(
                CadEllipseAuthoringStage.AwaitingFirstPoint,
                mode,
                arcInputMode,
                _ellipseAuthoring.InputKind,
                acceptedInputCount: 0));
        Invalidate();
        return true;
    }

    public bool CanAcceptEllipseAuthoringInput(string? text)
    {
        CadEllipseAuthoringSession? authoring = _ellipseAuthoring;
        if (authoring is null)
        {
            return false;
        }

        if (authoring.AcceptsScalarInput &&
            CadEllipseScalarInput.TryParse(
                text,
                out CadEllipseScalarInput scalar) &&
            TryConvertEllipseScalar(
                authoring.InputKind,
                scalar.Value,
                out double converted,
                out CadPoint3D direction,
                out bool isDirection))
        {
            bool accepted = isDirection
                ? authoring.TryPreviewDirection(
                    direction,
                    out CadEllipseAuthoringSnapshot preview,
                    out bool completed)
                : authoring.TryPreviewScalar(
                    converted,
                    out preview,
                    out completed);
            return accepted &&
                (!completed || CanRepresentEllipseSnapshot(preview));
        }

        CadPoint3D point;
        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            if (!CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput distance) ||
                !TryResolvePointTransformDirectDistance(distance, out point))
            {
                return false;
            }
        }
        else
        {
            if (!_hasPointTransformBasePoint && coordinate.IsRelative)
            {
                return false;
            }
            CadPoint3D origin = _hasPointTransformBasePoint
                ? _pointTransformBasePoint
                : CadPoint3D.Zero;
            if (!coordinate.TryResolve(origin, out point))
            {
                return false;
            }
        }

        if (!authoring.CanAcceptPoint(point))
        {
            return false;
        }
        try
        {
            _ = CreateViewport().WorldToScreen(point);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public bool TryAcceptEllipseAuthoringInput(
        string? text,
        out string? errorMessage)
    {
        errorMessage = null;
        CadEllipseAuthoringSession? authoring = _ellipseAuthoring;
        if (authoring is null)
        {
            errorMessage = "No ELLIPSE command is awaiting input.";
            return false;
        }

        if (authoring.AcceptsScalarInput &&
            CadEllipseScalarInput.TryParse(
                text,
                out CadEllipseScalarInput scalar))
        {
            if (!TryAcceptEllipseScalar(
                    authoring,
                    scalar.Value,
                    out CadEllipseAuthoringSnapshot scalarSnapshot,
                    out bool scalarCompleted,
                    out errorMessage))
            {
                NotifyEllipseAuthoringFailure(authoring, errorMessage);
                return false;
            }
            if (scalarCompleted)
            {
                return TryCommitEllipseAuthoringSnapshot(
                    authoring,
                    scalarSnapshot,
                    finalPoint: null,
                    out errorMessage);
            }

            UpdateEllipseAcquisitionBase(authoring);
            EllipseAuthoringChanged?.Invoke(
                this,
                new CadEllipseAuthoringChangedEventArgs(
                    CadEllipseAuthoringStage.AwaitingNextInput,
                    authoring.Mode,
                    authoring.ArcInputMode,
                    authoring.InputKind,
                    authoring.AcceptedInputCount,
                    authoring.CurrentPoint));
            Invalidate();
            return true;
        }

        CadPoint3D point;
        if (!CadCoordinateInput.TryParse(text, out CadCoordinateInput coordinate))
        {
            if (!CadDirectDistanceInput.TryParse(
                    text,
                    out CadDirectDistanceInput directDistance))
            {
                errorMessage = authoring.AcceptsScalarInput
                    ? "Enter the requested ELLIPSE angle/parameter in degrees using a bounded invariant number, or enter an accepted point coordinate."
                    : "Enter x,y[,z], @dx,dy[,dz], distance<angle, @distance<angle, or a positive direct distance using invariant numbers.";
                return false;
            }
            if (!_hasPointTransformBasePoint)
            {
                errorMessage =
                    "Accept an absolute first point before entering a direct distance.";
                return false;
            }
            if (!_hasPointTransformPointerPosition)
            {
                errorMessage =
                    "Move the cursor from the current ELLIPSE base before entering a direct distance.";
                return false;
            }
            if (!TryResolvePointTransformDirectDistance(
                    directDistance,
                    out point))
            {
                errorMessage =
                    "The cursor direction and distance do not resolve to a finite WCS point.";
                return false;
            }
        }
        else
        {
            if (!_hasPointTransformBasePoint && coordinate.IsRelative)
            {
                errorMessage =
                    "Enter an absolute first point before using a relative coordinate.";
                return false;
            }
            CadPoint3D origin = _hasPointTransformBasePoint
                ? _pointTransformBasePoint
                : CadPoint3D.Zero;
            if (!coordinate.TryResolve(origin, out point))
            {
                errorMessage = "The coordinate resolves outside finite WCS values.";
                return false;
            }
        }

        Vector2 screenPoint;
        try
        {
            screenPoint = CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException)
        {
            errorMessage =
                "The ELLIPSE point cannot be represented by the current plan viewport.";
            return false;
        }
        return TryAcceptEllipseAuthoringPoint(
            point,
            screenPoint,
            out errorMessage);
    }

    /// <summary>Cancels ELLIPSE without changing the document or history.</summary>
    public bool CancelEllipseAuthoring()
    {
        CadEllipseAuthoringSession? authoring = _ellipseAuthoring;
        if (authoring is null)
        {
            return false;
        }

        CadEllipseAuthoringMode mode = authoring.Mode;
        CadEllipseArcInputMode arcInputMode = authoring.ArcInputMode;
        CadEllipseAuthoringInputKind inputKind = authoring.InputKind;
        int acceptedInputCount = authoring.AcceptedInputCount;
        CadPoint3D? currentPoint = authoring.CurrentPoint;
        ResetEllipseAuthoringState();
        EllipseAuthoringChanged?.Invoke(
            this,
            new CadEllipseAuthoringChangedEventArgs(
                CadEllipseAuthoringStage.Canceled,
                mode,
                arcInputMode,
                inputKind,
                acceptedInputCount,
                currentPoint));
        Invalidate();
        return true;
    }

    private bool TryAcceptEllipseScalar(
        CadEllipseAuthoringSession authoring,
        double value,
        out CadEllipseAuthoringSnapshot snapshot,
        out bool completed,
        out string? errorMessage)
    {
        snapshot = default;
        completed = false;
        if (!TryConvertEllipseScalar(
                authoring.InputKind,
                value,
                out double converted,
                out CadPoint3D direction,
                out bool isDirection))
        {
            errorMessage = "The ELLIPSE scalar does not match the current prompt.";
            return false;
        }

        return isDirection
            ? authoring.TryAcceptDirection(
                direction,
                out snapshot,
                out completed,
                out errorMessage)
            : authoring.TryAcceptScalar(
                converted,
                out snapshot,
                out completed,
                out errorMessage);
    }

    private bool TryConvertEllipseScalar(
        CadEllipseAuthoringInputKind inputKind,
        double value,
        out double converted,
        out CadPoint3D direction,
        out bool isDirection)
    {
        converted = 0.0;
        direction = default;
        isDirection = false;
        if (!double.IsFinite(value))
        {
            return false;
        }

        double reducedDegrees = Math.IEEERemainder(value, 360.0);
        double radians = reducedDegrees * (Math.PI / 180.0);
        switch (inputKind)
        {
            case CadEllipseAuthoringInputKind.RotationRadians:
                converted = radians;
                return true;
            case CadEllipseAuthoringInputKind.StartDirection:
            case CadEllipseAuthoringInputKind.EndDirection:
            {
                double sine = Math.Sin(radians);
                if (_planPolarTrackingSettings.IsClockwise)
                {
                    sine = -sine;
                }
                direction =
                    (_planPolarTrackingSettings.XAxis * Math.Cos(radians)) +
                    (_planPolarTrackingSettings.YAxis * sine);
                isDirection = true;
                return true;
            }
            case CadEllipseAuthoringInputKind.StartParameterRadians:
            case CadEllipseAuthoringInputKind.EndParameterRadians:
            case CadEllipseAuthoringInputKind.IncludedAngleRadians:
                converted = _planPolarTrackingSettings.IsClockwise
                    ? -radians
                    : radians;
                return true;
            default:
                return false;
        }
    }

    private bool CanRepresentEllipseSnapshot(
        CadEllipseAuthoringSnapshot snapshot)
    {
        try
        {
            CadPlanViewport viewport = CreateViewport();
            _ = viewport.WorldToScreen(snapshot.Center);
            _ = viewport.WorldToScreen(snapshot.StartPoint);
            _ = viewport.WorldToScreen(snapshot.EndPoint);
            _ = viewport.WorldToScreen(
                snapshot.Center + snapshot.MajorAxisEndPoint);
            _ = viewport.WorldToScreen(
                snapshot.Center + snapshot.MinorAxisEndPoint);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts a bounded multi-gesture reference selection for an Above or Under
    /// draw-order edit. The edited selection remains unchanged until commit.
    /// </summary>
    public bool BeginSelectionDrawOrderReferencePick(
        CadDrawOrderPlacement placement)
    {
        ThrowIfPointTransformPending();
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
    /// Replaces selected persisted plot fields on one layout or named override
    /// through the generation-safe reversible document history.
    /// </summary>
    public void EditPageSetupFields(
        CadPageSetupSourceKind targetKind,
        string targetName,
        CadPageSetupFieldPatch patch)
    {
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        history.Execute(new CadEditPageSetupFieldsCommand(
            targetKind,
            targetName,
            patch,
            $"Edit {DescribePageSetupTarget(targetKind)} '{targetName}'"));
        RecompileAfterEdit(session);
    }

    private static string DescribePageSetupTarget(
        CadPageSetupSourceKind targetKind) => targetKind switch
    {
        CadPageSetupSourceKind.Layout => "layout page setup",
        CadPageSetupSourceKind.NamedOverride => "named page setup",
        _ => throw new ArgumentOutOfRangeException(nameof(targetKind)),
    };

    /// <summary>
    /// Changes persisted ATTMODE and replaces all snapshot-derived consumers as
    /// one generation-safe edit.
    /// </summary>
    public bool SetAttributeDisplayMode(AttributeVisibilityMode mode)
    {
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException("The CAD edit history is not initialized.");
        string description =
            $"Set ATTMODE to {DescribeAttributeDisplayMode(mode)}";
        if (session.Read(document => document.Header.AttributeVisibility) == mode)
        {
            return false;
        }

        history.Execute(new CadSetAttributeVisibilityModeCommand(
            mode,
            description));
        RecompileAfterEdit(session);
        return true;
    }

    private static string DescribeAttributeDisplayMode(
        AttributeVisibilityMode mode) => mode switch
    {
        AttributeVisibilityMode.None => "Off",
        AttributeVisibilityMode.Normal => "Normal",
        AttributeVisibilityMode.All => "On",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

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

        var compilerOptions = new CadPageSetupPrintOptionsCompilerOptions
        {
            OutputDpi = outputDpi,
            DisabledLineWeightPolicy =
                CadDisabledLineWeightPolicy.DeviceHairline,
            UnavailableTransparencyPolicy =
                CadUnavailablePlotTransparencyPolicy.PreserveRetainedAlpha,
        };
        if (pageSetup.TargetSpace == CadPageTargetSpace.Paper)
        {
            CadDocumentSession session = CurrentSession ??
                throw new InvalidOperationException("No CAD document is loaded.");
            CadLayoutSnapshot layoutSnapshot = new CadLayoutSnapshotCompiler().Compile(
                session,
                pageSetup.Name,
                CreatePlottingSnapshotOptions());
            return new CadLayoutPrintPlanCompiler().Compile(
                layoutSnapshot,
                pageSetup,
                compilerOptions);
        }

        CadPageSetupPrintOptionsResult lowering =
            new CadPageSetupPrintOptionsCompiler().Compile(pageSetup, compilerOptions);
        return new CadPrintPlanCompiler().CompileFromPageSetup(
            CreatePlottingSnapshot(),
            lowering);
    }

    private CadDocumentSnapshot CreatePlottingSnapshot()
    {
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        return new CadSnapshotCompiler().Compile(session, CreatePlottingSnapshotOptions());
    }

    private CadSnapshotOptions CreatePlottingSnapshotOptions() =>
        new()
        {
            TextFontResolver = new CadFontManagerTextResolver(
                InterFontFamily.Regular),
            ShxFontResolver = ShxFonts,
            DrawOrderPurpose = CadDrawOrderPurpose.Plotting,
            DrawingBackgroundColor = new CadColor32(
                byte.MaxValue,
                byte.MaxValue,
                byte.MaxValue),
        };

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
    /// Copies all selected semantic model-space roots into one bounded linear
    /// array while preserving the source selection across Apply/Undo/Redo.
    /// </summary>
    /// <param name="displacement">
    /// Incremental item spacing or source-to-final-item displacement according
    /// to <paramref name="mode"/>.
    /// </param>
    /// <param name="itemCount">Array items including the source selection.</param>
    /// <param name="mode">Incremental-spacing or Fit placement semantics.</param>
    public bool DuplicateSelectionLinearArray(
        CadPoint3D displacement,
        int itemCount,
        CadLinearCopyMode mode)
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
        int selectedCount = _selectedHandleCount;
        history.Execute(new CadLinearCopyModelSpaceEntitiesCommand(
            new ArraySegment<ulong>(_selectedHandles, 0, selectedCount),
            displacement,
            itemCount,
            mode,
            selectedCount == 1
                ? "Create linear array from selected entity"
                : $"Create linear array from {selectedCount} selected entities"));
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
        if (history is null || session is null)
        {
            EditStateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
        bool previousOrthoMode = session.Read(
            document => document.Header.OrthoMode);
        bool previousSnapMode = session.Read(document =>
            document.VPorts[VPort.DefaultName].SnapOn);
        if (!history.TryUndo(out _))
        {
            EditStateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        bool synchronizePlanOrthoMode = previousOrthoMode != session.Read(
            document => document.Header.OrthoMode);
        bool synchronizePlanSnapMode = previousSnapMode != session.Read(
            document => document.VPorts[VPort.DefaultName].SnapOn);
        RecompileAfterEdit(
            session,
            synchronizePlanOrthoMode,
            synchronizePlanSnapMode);
        return true;
    }

    public bool TryRedo()
    {
        ThrowIfDrawOrderReferencePickPending();
        CadDocumentHistory? history = _history;
        CadDocumentSession? session = CurrentSession;
        if (history is null || session is null)
        {
            EditStateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
        bool previousOrthoMode = session.Read(
            document => document.Header.OrthoMode);
        bool previousSnapMode = session.Read(document =>
            document.VPorts[VPort.DefaultName].SnapOn);
        if (!history.TryRedo(out _))
        {
            EditStateChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }

        bool synchronizePlanOrthoMode = previousOrthoMode != session.Read(
            document => document.Header.OrthoMode);
        bool synchronizePlanSnapMode = previousSnapMode != session.Read(
            document => document.VPorts[VPort.DefaultName].SnapOn);
        RecompileAfterEdit(
            session,
            synchronizePlanOrthoMode,
            synchronizePlanSnapMode);
        return true;
    }

    private void RecompileAfterEdit(
        CadDocumentSession session,
        bool synchronizePlanOrthoMode = false,
        bool synchronizePlanSnapMode = false)
    {
        try
        {
            CompileAndReplace(
                session,
                resetViewSelectionAndHistory: false,
                synchronizePlanOrthoMode: synchronizePlanOrthoMode,
                synchronizePlanSnapMode: synchronizePlanSnapMode);
        }
        catch
        {
            EditStateChanged?.Invoke(this, EventArgs.Empty);
            throw;
        }
    }

    private void UpdatePointTransformPointer(Vector2 screenPoint)
    {
        bool notifyInputAvailability = !_hasPointTransformPointerPosition;
        _pointTransformPointerPosition = screenPoint;
        _hasPointTransformPointerPosition = true;
        CadDocumentSnapshot? snapshot = CurrentSnapshot;
        if (snapshot is null)
        {
            _pointTransformObjectSnap = default;
            _hasPointTransformGridSnap = false;
            _hasPointTransformOrtho = false;
            _hasPointTransformPolarTracking = false;
            _pointTransformCurrent = screenPoint;
            NotifyPointTransformInputAvailability(notifyInputAvailability);
            return;
        }

        if (_xlineAuthoring?.Prompt is
            CadXLinePromptKind.AngleReferenceSource or
            CadXLinePromptKind.OffsetSource)
        {
            _pointTransformObjectSnap = default;
            _hasPointTransformGridSnap = false;
            _hasPointTransformOrtho = false;
            _hasPointTransformPolarTracking = false;
            _pointTransformCurrent = screenPoint;
            _hasXLineSourceCandidate = TryResolveXLineSourceCandidate(
                screenPoint,
                out _xlineSourceCandidate,
                out _);
            NotifyPointTransformInputAvailability(notifyInputAvailability);
            return;
        }
        if (_xlineAuthoring?.Prompt is
            CadXLinePromptKind.AngleValue or
            CadXLinePromptKind.OffsetDistance)
        {
            _pointTransformObjectSnap = default;
            _hasPointTransformGridSnap = false;
            _hasPointTransformOrtho = false;
            _hasPointTransformPolarTracking = false;
            _hasXLineSourceCandidate = false;
            _pointTransformCurrent = screenPoint;
            NotifyPointTransformInputAvailability(notifyInputAvailability);
            return;
        }

        CadPlanViewport viewport = CreateViewport();
        CadPoint3D pointerWorld = (_rayAuthoring is not null ||
                _xlineAuthoring is not null ||
                _polylineAuthoring is not null ||
                _circleAuthoring is not null ||
                _arcAuthoring is not null ||
                _ellipseAuthoring is not null ||
                _polygonAuthoring is not null ||
                _rectangleAuthoring is not null) &&
            _hasPointTransformBasePoint
            ? viewport.ScreenToWorld(screenPoint, _pointTransformBasePoint.Z)
            : viewport.ScreenToWorld(screenPoint);
        _pointTransformObjectSnap = default;
        _hasPointTransformGridSnap = false;
        _hasPointTransformOrtho = false;
        _hasPointTransformPolarTracking = false;
        if (_objectSnapModes != CadObjectSnapModes.None)
        {
            _pointTransformObjectSnap = CadObjectSnapQuery.Query(
                snapshot,
                viewport,
                screenPoint,
                PointTransformObjectSnapAperture,
                _objectSnapModes,
                _selectionEntityScratch,
                _hasPointTransformBasePoint
                    ? _pointTransformBasePoint
                    : null);
            if (_pointTransformObjectSnap.AreCandidatesTruncated ||
                _pointTransformObjectSnap.AreIntersectionPairsTruncated)
            {
                _pointTransformObjectSnap = default;
            }
            if (_pointTransformObjectSnap.IsSnapped)
            {
                _pointTransformCurrent =
                    viewport.WorldToScreen(_pointTransformObjectSnap.Point);
                NotifyPointTransformInputAvailability(notifyInputAvailability);
                return;
            }
        }

        if (_isPlanOrthoEnabled &&
            _hasPointTransformBasePoint &&
            CadPlanOrthoConstraint.TryConstrain(
                _pointTransformBasePoint,
                pointerWorld,
                _planGridSnapSettings,
                out _pointTransformOrtho))
        {
            _hasPointTransformOrtho = true;
            _pointTransformCurrent =
                viewport.WorldToScreen(_pointTransformOrtho.Point);
            if (_pointTransformOrtho.IsGridSnapped)
            {
                _pointTransformGridSnap = _pointTransformOrtho.Point;
                _hasPointTransformGridSnap = true;
            }
            NotifyPointTransformInputAvailability(notifyInputAvailability);
            return;
        }

        if (_planPolarTrackingSettings.IsEnabled &&
            _hasPointTransformBasePoint &&
            TryTrackActivePolar(
                _pointTransformBasePoint,
                pointerWorld,
                out CadPlanPolarTrackingResult polarTracking))
        {
            Vector2 trackedScreen = viewport.WorldToScreen(polarTracking.Point);
            if (Vector2.Distance(trackedScreen, screenPoint) <=
                PointTransformPolarTrackingAperture)
            {
                bool hasPolarSnap = _planPolarSnapSettings.IsEnabled;
                if (!hasPolarSnap || _planPolarSnapSettings.TrySnap(
                        _pointTransformBasePoint,
                        polarTracking,
                        _planGridSnapSettings.SpacingX,
                        out polarTracking))
                {
                    _pointTransformPolarTracking = polarTracking;
                    _hasPointTransformPolarTracking = true;
                    _pointTransformCurrent = hasPolarSnap
                        ? viewport.WorldToScreen(polarTracking.Point)
                        : trackedScreen;
                    NotifyPointTransformInputAvailability(notifyInputAvailability);
                    return;
                }
            }
        }

        _hasPointTransformGridSnap = _planGridSnapSettings.TrySnap(
            pointerWorld,
            out _pointTransformGridSnap);
        _pointTransformCurrent = _hasPointTransformGridSnap
            ? viewport.WorldToScreen(_pointTransformGridSnap)
            : screenPoint;
        NotifyPointTransformInputAvailability(notifyInputAvailability);
    }

    private void SetPlanSnapState(CadPlanSnapType type, bool isEnabled)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }
        if (_planSnapType == type && IsPlanSnapEnabled == isEnabled)
        {
            return;
        }

        _planSnapType = type;
        _planGridSnapSettings = _planGridSnapSettings.WithEnabled(
            isEnabled && type == CadPlanSnapType.Grid);
        _planPolarSnapSettings = _planPolarSnapSettings.WithEnabled(
            isEnabled && type == CadPlanSnapType.Polar);
        if (IsPointAcquisitionActive &&
            _hasPointTransformPointerPosition)
        {
            UpdatePointTransformPointer(_pointTransformPointerPosition);
        }
        else
        {
            _hasPointTransformGridSnap = false;
            _hasPointTransformPolarTracking = false;
        }
        Invalidate();
    }

    private void SetPlanPolarTrackingProfile(
        CadPlanPolarTrackingSettings updated)
    {
        if (updated == _planPolarTrackingSettings)
        {
            return;
        }

        _planPolarTrackingSettings = updated;
        if (IsPointAcquisitionActive &&
            _hasPointTransformPointerPosition)
        {
            UpdatePointTransformPointer(_pointTransformPointerPosition);
        }
        else
        {
            _hasPointTransformPolarTracking = false;
        }
        Invalidate();
    }

    private bool TryResolvePointTransformDirectDistance(
        CadDirectDistanceInput input,
        out CadPoint3D point)
    {
        point = default;
        if (!_hasPointTransformBasePoint ||
            !_hasPointTransformPointerPosition)
        {
            return false;
        }

        CadPlanViewport viewport;
        CadPoint3D pointerPoint;
        try
        {
            viewport = CreateViewport();
            pointerPoint = viewport.ScreenToWorld(
                _pointTransformPointerPosition,
                _pointTransformBasePoint.Z);
        }
        catch (ArgumentException)
        {
            return false;
        }

        CadPoint3D direction = pointerPoint - _pointTransformBasePoint;
        if (_isPlanOrthoEnabled &&
            CadPlanOrthoConstraint.TryConstrain(
                _pointTransformBasePoint,
                pointerPoint,
                _planGridSnapSettings.WithEnabled(false),
                out CadPlanOrthoResult ortho))
        {
            direction = ortho.Point - _pointTransformBasePoint;
        }
        else if (_planPolarTrackingSettings.IsEnabled &&
            TryTrackActivePolar(
                _pointTransformBasePoint,
                pointerPoint,
                out CadPlanPolarTrackingResult polar))
        {
            Vector2 trackedScreen;
            try
            {
                trackedScreen = viewport.WorldToScreen(polar.Point);
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (Vector2.Distance(
                    trackedScreen,
                    _pointTransformPointerPosition) <=
                PointTransformPolarTrackingAperture)
            {
                direction = polar.Point - _pointTransformBasePoint;
            }
        }

        return input.TryResolve(
            _pointTransformBasePoint,
            direction,
            out point);
    }

    private bool TryTrackActivePolar(
        CadPoint3D basePoint,
        CadPoint3D pointerPoint,
        out CadPlanPolarTrackingResult result)
    {
        CadPoint3D? activeReferenceDirection =
            _lineAuthoring?.PreviousSegmentDirection ??
            _polylineAuthoring?.PreviousSegmentDirection;
        if (activeReferenceDirection is CadPoint3D referenceDirection)
        {
            return _planPolarTrackingSettings.TryTrack(
                basePoint,
                pointerPoint,
                referenceDirection,
                out result);
        }

        return _planPolarTrackingSettings.TryTrack(
            basePoint,
            pointerPoint,
            out result);
    }

    private void NotifyPointTransformInputAvailability(bool notify)
    {
        if (notify)
        {
            PointTransformInputAvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ClearPointTransformSnapState()
    {
        bool notifyInputAvailability = _hasPointTransformPointerPosition;
        _pointTransformObjectSnap = default;
        _hasPointTransformGridSnap = false;
        _hasPointTransformOrtho = false;
        _hasPointTransformPolarTracking = false;
        _hasPointTransformPointerPosition = false;
        NotifyPointTransformInputAvailability(notifyInputAvailability);
    }

    private void DrawPlanPolarTrackingGuide(DrawingContext context)
    {
        if (!_hasPointTransformPolarTracking)
        {
            return;
        }

        Vector2 basePoint = CreateViewport().WorldToScreen(_pointTransformBasePoint);
        Vector2 delta = _pointTransformCurrent - basePoint;
        float length = delta.Length();
        if (!float.IsFinite(length) || length <= float.Epsilon)
        {
            return;
        }

        float extension = (Size.X + Size.Y) * 2.0f;
        context.DrawLine(
            _drawOrderReferencePen,
            basePoint,
            basePoint + ((delta / length) * extension));
    }

    private void DrawPlanGridSnapMarker(DrawingContext context)
    {
        if (!_hasPointTransformGridSnap)
        {
            return;
        }

        Vector2 center = _pointTransformCurrent;
        float radius = ObjectSnapMarkerRadius * 0.8f;
        Vector2 horizontal = new(radius, 0.0f);
        Vector2 vertical = new(0.0f, radius);
        context.DrawLine(
            _crossingPen,
            center - horizontal,
            center + horizontal);
        context.DrawLine(
            _crossingPen,
            center - vertical,
            center + vertical);
    }

    private void DrawObjectSnapMarker(
        DrawingContext context,
        CadObjectSnapResult snap)
    {
        if (!snap.IsSnapped)
        {
            return;
        }

        Vector2 center = _pointTransformCurrent;
        float radius = ObjectSnapMarkerRadius;
        Vector2 horizontal = new(radius, 0);
        Vector2 vertical = new(0, radius);
        switch (snap.Kind)
        {
            case CadObjectSnapKind.Endpoint:
                context.DrawRectangle(
                    null,
                    _drawOrderReferencePen,
                    new Rect(
                        center.X - radius,
                        center.Y - radius,
                        radius * 2,
                        radius * 2));
                break;
            case CadObjectSnapKind.Midpoint:
            {
                Vector2 top = center - vertical;
                Vector2 lowerLeft = center +
                    new Vector2(-radius, radius);
                Vector2 lowerRight = center +
                    new Vector2(radius, radius);
                context.DrawLine(_drawOrderReferencePen, top, lowerLeft);
                context.DrawLine(_drawOrderReferencePen, lowerLeft, lowerRight);
                context.DrawLine(_drawOrderReferencePen, lowerRight, top);
                break;
            }
            case CadObjectSnapKind.Center:
                context.DrawEllipse(
                    null,
                    _drawOrderReferencePen,
                    center,
                    radius,
                    radius);
                context.DrawLine(
                    _drawOrderReferencePen,
                    center - horizontal,
                    center + horizontal);
                context.DrawLine(
                    _drawOrderReferencePen,
                    center - vertical,
                    center + vertical);
                break;
            case CadObjectSnapKind.Node:
            {
                context.DrawLine(
                    _drawOrderReferencePen,
                    center - horizontal,
                    center + horizontal);
                context.DrawLine(
                    _drawOrderReferencePen,
                    center - vertical,
                    center + vertical);
                break;
            }
            case CadObjectSnapKind.Intersection:
            {
                Vector2 diagonal = new(radius, radius);
                Vector2 opposite = new(radius, -radius);
                context.DrawLine(
                    _drawOrderReferencePen,
                    center - diagonal,
                    center + diagonal);
                context.DrawLine(
                    _drawOrderReferencePen,
                    center - opposite,
                    center + opposite);
                break;
            }
            case CadObjectSnapKind.Quadrant:
            {
                Vector2 top = center - vertical;
                Vector2 right = center + horizontal;
                Vector2 bottom = center + vertical;
                Vector2 left = center - horizontal;
                context.DrawLine(_drawOrderReferencePen, top, right);
                context.DrawLine(_drawOrderReferencePen, right, bottom);
                context.DrawLine(_drawOrderReferencePen, bottom, left);
                context.DrawLine(_drawOrderReferencePen, left, top);
                break;
            }
            case CadObjectSnapKind.Nearest:
            {
                Vector2 topLeft = center - horizontal - vertical;
                Vector2 topRight = center + horizontal - vertical;
                Vector2 bottomLeft = center - horizontal + vertical;
                Vector2 bottomRight = center + horizontal + vertical;
                context.DrawLine(_drawOrderReferencePen, topLeft, center);
                context.DrawLine(_drawOrderReferencePen, center, bottomLeft);
                context.DrawLine(_drawOrderReferencePen, topRight, center);
                context.DrawLine(_drawOrderReferencePen, center, bottomRight);
                context.DrawLine(_drawOrderReferencePen, topLeft, bottomLeft);
                context.DrawLine(_drawOrderReferencePen, topRight, bottomRight);
                break;
            }
            case CadObjectSnapKind.Perpendicular:
            {
                Vector2 corner = center - horizontal + vertical;
                Vector2 top = center - horizontal - vertical;
                Vector2 right = center + horizontal + vertical;
                Vector2 innerCorner = center - (horizontal * 0.35f) +
                    (vertical * 0.35f);
                context.DrawLine(_drawOrderReferencePen, top, corner);
                context.DrawLine(_drawOrderReferencePen, corner, right);
                context.DrawLine(
                    _drawOrderReferencePen,
                    innerCorner - (vertical * 0.7f),
                    innerCorner + (horizontal * 0.7f));
                break;
            }
            case CadObjectSnapKind.Tangent:
            {
                Vector2 circleCenter = center - (vertical * 0.25f);
                float circleRadius = radius * 0.65f;
                context.DrawEllipse(
                    null,
                    _drawOrderReferencePen,
                    circleCenter,
                    circleRadius,
                    circleRadius);
                Vector2 tangentOffset = vertical * 0.4f;
                context.DrawLine(
                    _drawOrderReferencePen,
                    center - horizontal + tangentOffset,
                    center + horizontal + tangentOffset);
                break;
            }
        }
    }

    private bool TryResolveXLineSourceCandidate(
        Vector2 screenPoint,
        out CadSelectionCandidate candidate,
        out CadXLineLinearSource source)
    {
        candidate = default;
        source = default;
        CadDocumentSnapshot? snapshot = CurrentSnapshot;
        if (snapshot is null)
        {
            return false;
        }

        CadPlanViewport viewport = CreateViewport();
        CadBounds3D bounds = viewport.CreatePlanSelectionBounds(
            screenPoint,
            screenPoint,
            PointSelectionTolerance);
        CadBoundsSelectionQueryResult query =
            CadSelectionQuery.QueryExactBounds(
                snapshot,
                bounds,
                CadBoundsSelectionMode.Crossing,
                _selectionEntityScratch,
                _selectionCandidates,
                _selectionMatches,
                _selectionHandleScratch,
                _drawOrderReferenceQueryHandles);
        if (query.AreCandidatesTruncated || query.AreHandlesTruncated)
        {
            return false;
        }

        float bestDistanceSquared = float.PositiveInfinity;
        int bestEntityIndex = int.MaxValue;
        for (int i = 0; i < query.MatchedPrimitiveCount; i++)
        {
            CadSelectionCandidate current = _selectionMatches[i];
            CadXLineLinearSourceResult resolved =
                CadXLineLinearSourceResolver.Resolve(snapshot, current);
            if (!resolved.IsSuccess ||
                !TryGetXLineSourceScreenSegment(
                    snapshot,
                    current,
                    viewport,
                    out Vector2 start,
                    out Vector2 end))
            {
                continue;
            }
            float distanceSquared = DistanceSquaredToSegment(
                screenPoint,
                start,
                end);
            if (distanceSquared > bestDistanceSquared ||
                (distanceSquared == bestDistanceSquared &&
                    current.EntityIndex >= bestEntityIndex))
            {
                continue;
            }
            bestDistanceSquared = distanceSquared;
            bestEntityIndex = current.EntityIndex;
            candidate = current;
            source = resolved.Source;
        }
        return bestEntityIndex != int.MaxValue;
    }

    private static bool TryGetXLineSourceScreenSegment(
        CadDocumentSnapshot snapshot,
        CadSelectionCandidate candidate,
        CadPlanViewport viewport,
        out Vector2 start,
        out Vector2 end)
    {
        start = default;
        end = default;
        if (!CadXLineLinearSourceResolver.Resolve(
                snapshot,
                candidate).IsSuccess)
        {
            return false;
        }
        CadEntityHeader header = snapshot.Entities.Span[candidate.EntityIndex];
        if (header.Kind == CadEntityKind.Line)
        {
            CadLinePrimitive line = snapshot.Lines.Span[header.PrimitiveIndex];
            start = viewport.WorldToScreen(line.Start);
            end = viewport.WorldToScreen(line.End);
            return true;
        }

        CadConstructionLinePrimitive construction =
            snapshot.ConstructionLines.Span[header.PrimitiveIndex];
        if (!CadConstructionSceneCompiler.TryClipPlan(
                construction,
                viewport.CreatePlanClipBounds(),
                isRay: header.Kind == CadEntityKind.Ray,
                out CadPoint3D clippedStart,
                out CadPoint3D clippedEnd))
        {
            return false;
        }
        start = viewport.WorldToScreen(clippedStart);
        end = viewport.WorldToScreen(clippedEnd);
        return true;
    }

    private static float DistanceSquaredToSegment(
        Vector2 point,
        Vector2 start,
        Vector2 end)
    {
        Vector2 delta = end - start;
        float lengthSquared = delta.LengthSquared();
        if (!(lengthSquared > 0.0f) || !float.IsFinite(lengthSquared))
        {
            return Vector2.DistanceSquared(point, start);
        }
        float parameter = Math.Clamp(
            Vector2.Dot(point - start, delta) / lengthSquared,
            0.0f,
            1.0f);
        return Vector2.DistanceSquared(point, start + (delta * parameter));
    }

    private void AcceptPointTransformPointer(Vector2 screenPoint)
    {
        if (_xlineAuthoring?.Prompt is
            CadXLinePromptKind.AngleReferenceSource or
            CadXLinePromptKind.OffsetSource)
        {
            string? sourceError = null;
            bool accepted = TryResolveXLineSourceCandidate(
                    screenPoint,
                    out _xlineSourceCandidate,
                    out CadXLineLinearSource source) &&
                _xlineAuthoring.TryAcceptLinearSource(
                    source,
                    out sourceError);
            _ = CompleteXLinePromptTransition(
                accepted,
                accepted
                    ? null
                    : sourceError ??
                        "Select a visible LINE, RAY, or XLINE source.");
            return;
        }
        if (_xlineAuthoring?.Prompt is
            CadXLinePromptKind.AngleValue or
            CadXLinePromptKind.OffsetDistance)
        {
            return;
        }

        CadDocumentSnapshot? snapshot = CurrentSnapshot;
        if (_pointTransformObjectSnap.IsSnapped &&
            snapshot is not null &&
            _pointTransformObjectSnap.ContentGeneration ==
                snapshot.ContentGeneration)
        {
            CadPoint3D point = _pointTransformObjectSnap.Point;
            Vector2 snappedScreen = _pointTransformCurrent;
            AcceptActivePoint(point, snappedScreen);
            return;
        }

        if (_hasPointTransformGridSnap)
        {
            CadPoint3D point = _pointTransformGridSnap;
            Vector2 snappedScreen = _pointTransformCurrent;
            AcceptActivePoint(point, snappedScreen);
            return;
        }

        if (_hasPointTransformOrtho)
        {
            CadPoint3D point = _pointTransformOrtho.Point;
            Vector2 constrainedScreen = _pointTransformCurrent;
            AcceptActivePoint(point, constrainedScreen);
            return;
        }

        if (_hasPointTransformPolarTracking)
        {
            CadPoint3D point = _pointTransformPolarTracking.Point;
            Vector2 trackedScreen = _pointTransformCurrent;
            AcceptActivePoint(point, trackedScreen);
            return;
        }

        AcceptActivePoint(screenPoint);
    }

    private void AcceptActivePoint(Vector2 screenPoint)
    {
        CadPointTransformOperation? operation = PendingPointTransformOperation;
        if (operation is null &&
            _lineAuthoring is null &&
            _rayAuthoring is null &&
            _xlineAuthoring is null &&
            _pointAuthoring is null &&
            _polylineAuthoring is null &&
                _circleAuthoring is null &&
                _arcAuthoring is null &&
                _ellipseAuthoring is null &&
                _polygonAuthoring is null &&
                _rectangleAuthoring is null)
        {
            return;
        }

        CadPoint3D point;
        try
        {
            point = (_rayAuthoring is not null ||
                    _xlineAuthoring is not null ||
                    _polylineAuthoring is not null ||
                    _circleAuthoring is not null ||
                    _arcAuthoring is not null ||
                    _ellipseAuthoring is not null ||
                    _polygonAuthoring is not null ||
                    _rectangleAuthoring is not null) &&
                _hasPointTransformBasePoint
                ? CreateViewport().ScreenToWorld(
                    screenPoint,
                    _pointTransformBasePoint.Z)
                : CreateViewport().ScreenToWorld(screenPoint);
        }
        catch (Exception exception)
        {
            if (operation is CadPointTransformOperation value)
            {
                ResetPointTransformState(notify: false);
                PointTransformChanged?.Invoke(
                    this,
                    new CadPointTransformChangedEventArgs(
                        value,
                        CadPointTransformStage.Failed,
                        errorMessage: exception.Message));
            }
            else if (_lineAuthoring is not null)
            {
                LineAuthoringChanged?.Invoke(
                    this,
                    new CadLineAuthoringChangedEventArgs(
                        CadLineAuthoringStage.Failed,
                        _lineAuthoring?.SegmentCount ?? 0,
                        _lineAuthoring?.CurrentPoint,
                        errorMessage: exception.Message));
            }
            else if (_rayAuthoring is not null)
            {
                RayAuthoringChanged?.Invoke(
                    this,
                    new CadRayAuthoringChangedEventArgs(
                        CadRayAuthoringStage.Failed,
                        _rayAuthoring.RayCount,
                        _rayAuthoring.StartPoint,
                        exception.Message));
            }
            else if (_xlineAuthoring is not null)
            {
                XLineAuthoringChanged?.Invoke(
                    this,
                    new CadXLineAuthoringChangedEventArgs(
                        CadXLineAuthoringStage.Failed,
                        _xlineAuthoring.LineCount,
                        _xlineAuthoring.FirstPoint,
                        exception.Message,
                        _xlineAuthoring.Mode,
                        _xlineAuthoring.Prompt));
            }
            else if (_pointAuthoring is not null)
            {
                PointAuthoringChanged?.Invoke(
                    this,
                    new CadPointAuthoringChangedEventArgs(
                        CadPointAuthoringStage.Failed,
                        errorMessage: exception.Message));
            }
            else if (_polylineAuthoring is not null)
            {
                PolylineAuthoringChanged?.Invoke(
                    this,
                    new CadPolylineAuthoringChangedEventArgs(
                        CadPolylineAuthoringStage.Failed,
                        _polylineAuthoring?.Mode ?? CadPolylineAuthoringMode.Line,
                        _polylineAuthoring?.SegmentCount ?? 0,
                        _polylineAuthoring?.CurrentPoint,
                        errorMessage: exception.Message));
            }
            else if (_circleAuthoring is not null)
            {
                CircleAuthoringChanged?.Invoke(
                    this,
                    new CadCircleAuthoringChangedEventArgs(
                        CadCircleAuthoringStage.Failed,
                        _circleAuthoring?.Mode ??
                            CadCircleAuthoringMode.CenterRadius,
                        _circleAuthoring?.PointCount ?? 0,
                        _circleAuthoring?.CurrentPoint,
                        errorMessage: exception.Message));
            }
            else if (_arcAuthoring is not null)
            {
                ArcAuthoringChanged?.Invoke(
                    this,
                    new CadArcAuthoringChangedEventArgs(
                        CadArcAuthoringStage.Failed,
                        _arcAuthoring?.Mode ??
                            CadArcAuthoringMode.ThreePoint,
                        _arcAuthoring?.PointCount ?? 0,
                        _arcAuthoring?.CurrentPoint,
                        errorMessage: exception.Message));
            }
            else if (_ellipseAuthoring is not null)
            {
                EllipseAuthoringChanged?.Invoke(
                    this,
                    new CadEllipseAuthoringChangedEventArgs(
                        CadEllipseAuthoringStage.Failed,
                        _ellipseAuthoring?.Mode ??
                            CadEllipseAuthoringMode.AxisEndpointsDistance,
                        _ellipseAuthoring?.ArcInputMode ??
                            CadEllipseArcInputMode.Full,
                        _ellipseAuthoring?.InputKind ??
                            CadEllipseAuthoringInputKind.FirstAxisPoint,
                        _ellipseAuthoring?.AcceptedInputCount ?? 0,
                        _ellipseAuthoring?.CurrentPoint,
                        errorMessage: exception.Message));
            }
            else if (_polygonAuthoring is not null)
            {
                PolygonAuthoringChanged?.Invoke(
                    this,
                    new CadPolygonAuthoringChangedEventArgs(
                        CadPolygonAuthoringStage.Failed,
                        _polygonAuthoring?.SideCount ?? 0,
                        _polygonAuthoring?.Mode ??
                            CadPolygonAuthoringMode.Inscribed,
                        _polygonAuthoring?.InputKind ??
                            CadPolygonAuthoringInputKind.CenterPoint,
                        _polygonAuthoring?.AcceptedInputCount ?? 0,
                        _polygonAuthoring?.CurrentPoint,
                        errorMessage: exception.Message));
            }
            else
            {
                RectangleAuthoringChanged?.Invoke(
                    this,
                    new CadRectangleAuthoringChangedEventArgs(
                        CadRectangleAuthoringStage.Failed,
                        _rectangleAuthoring?.Construction ?? default,
                        _rectangleAuthoring?.CornerTreatment ?? default,
                        _rectangleAuthoring?.RotationRadians ?? 0.0,
                        _rectangleAuthoring?.InputKind ??
                            CadRectangleAuthoringInputKind.FirstCorner,
                        _rectangleAuthoring?.AcceptedInputCount ?? 0,
                        _rectangleAuthoring?.CurrentPoint,
                        errorMessage: exception.Message));
            }
            Invalidate();
            return;
        }

        AcceptActivePoint(point, screenPoint);
    }

    private void AcceptActivePoint(
        CadPoint3D point,
        Vector2? screenPoint)
    {
        if (_lineAuthoring is not null)
        {
            _ = TryAcceptLineAuthoringPoint(point, screenPoint, out _);
            return;
        }
        if (_rayAuthoring is not null)
        {
            _ = TryAcceptRayAuthoringPoint(point, screenPoint, out _);
            return;
        }
        if (_xlineAuthoring is not null)
        {
            _ = TryAcceptXLineAuthoringPoint(point, screenPoint, out _);
            return;
        }
        if (_pointAuthoring is not null)
        {
            _ = TryAcceptPointAuthoringPoint(point, screenPoint, out _);
            return;
        }
        if (_polylineAuthoring is not null)
        {
            _ = TryAcceptPolylineAuthoringPoint(point, screenPoint, out _);
            return;
        }
        if (_circleAuthoring is not null)
        {
            _ = TryAcceptCircleAuthoringPoint(point, screenPoint, out _);
            return;
        }
        if (_arcAuthoring is not null)
        {
            _ = TryAcceptArcAuthoringPoint(point, screenPoint, out _);
            return;
        }
        if (_ellipseAuthoring is not null)
        {
            _ = TryAcceptEllipseAuthoringPoint(point, screenPoint, out _);
            return;
        }
        if (_polygonAuthoring is not null)
        {
            _ = TryAcceptPolygonAuthoringPoint(point, screenPoint, out _);
            return;
        }
        if (_rectangleAuthoring is not null)
        {
            _ = TryAcceptRectangleAuthoringPoint(point, screenPoint, out _);
            return;
        }

        AcceptPointTransformPoint(point, screenPoint);
    }

    private bool TryAcceptPointAuthoringPoint(
        CadPoint3D point,
        Vector2? screenPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadPointAuthoringSession? authoring = _pointAuthoring;
        if (authoring is null)
        {
            errorMessage = "No POINT command is active.";
            return false;
        }

        try
        {
            _ = screenPoint ?? CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            PointAuthoringChanged?.Invoke(
                this,
                new CadPointAuthoringChangedEventArgs(
                    CadPointAuthoringStage.Failed,
                    point,
                    errorMessage: errorMessage));
            Invalidate();
            return false;
        }

        if (!authoring.TryCreateSnapshot(
                point,
                out CadPointAuthoringSnapshot snapshot,
                out errorMessage))
        {
            PointAuthoringChanged?.Invoke(
                this,
                new CadPointAuthoringChangedEventArgs(
                    CadPointAuthoringStage.Failed,
                    point,
                    errorMessage: errorMessage));
            Invalidate();
            return false;
        }

        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException(
                "The CAD edit history is not initialized.");
        try
        {
            history.Execute(new CadAddPointCommand(
                snapshot,
                description: "POINT: add point"));
            ResetPointAuthoringState();
            RecompileAfterEdit(session);
            PointAuthoringChanged?.Invoke(
                this,
                new CadPointAuthoringChangedEventArgs(
                    CadPointAuthoringStage.Completed,
                    point,
                    snapshot));
            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            PointAuthoringChanged?.Invoke(
                this,
                new CadPointAuthoringChangedEventArgs(
                    CadPointAuthoringStage.Failed,
                    point,
                    snapshot,
                    exception.Message));
            Invalidate();
            return false;
        }
    }

    private bool TryAcceptLineAuthoringPoint(
        CadPoint3D point,
        Vector2? screenPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadLineAuthoringSession? authoring = _lineAuthoring;
        if (authoring is null)
        {
            errorMessage = "No LINE command is active.";
            return false;
        }
        Vector2 resolvedScreen;
        try
        {
            resolvedScreen = screenPoint ??
                CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            LineAuthoringChanged?.Invoke(
                this,
                new CadLineAuthoringChangedEventArgs(
                    CadLineAuthoringStage.Failed,
                    authoring.SegmentCount,
                    authoring.CurrentPoint,
                    errorMessage: errorMessage));
            return false;
        }
        if (!authoring.TryAcceptPoint(point, out errorMessage))
        {
            LineAuthoringChanged?.Invoke(
                this,
                new CadLineAuthoringChangedEventArgs(
                    CadLineAuthoringStage.Failed,
                    authoring.SegmentCount,
                    authoring.CurrentPoint,
                    errorMessage: errorMessage));
            return false;
        }

        ClearPointTransformSnapState();
        _pointTransformBasePoint = point;
        _pointTransformCurrent = resolvedScreen;
        _hasPointTransformBasePoint = true;
        RefreshLineAuthoringPicture();
        LineAuthoringChanged?.Invoke(
            this,
            new CadLineAuthoringChangedEventArgs(
                CadLineAuthoringStage.AwaitingNextPoint,
                authoring.SegmentCount,
                point));
        Invalidate();
        return true;
    }

    private bool TryAcceptRayAuthoringPoint(
        CadPoint3D point,
        Vector2? screenPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadRayAuthoringSession? authoring = _rayAuthoring;
        if (authoring is null)
        {
            errorMessage = "No RAY command is active.";
            return false;
        }

        Vector2 resolvedScreen;
        try
        {
            resolvedScreen = screenPoint ??
                CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            RayAuthoringChanged?.Invoke(
                this,
                new CadRayAuthoringChangedEventArgs(
                    CadRayAuthoringStage.Failed,
                    authoring.RayCount,
                    authoring.StartPoint,
                    errorMessage));
            return false;
        }
        if (!authoring.TryAcceptPoint(point, out errorMessage))
        {
            RayAuthoringChanged?.Invoke(
                this,
                new CadRayAuthoringChangedEventArgs(
                    CadRayAuthoringStage.Failed,
                    authoring.RayCount,
                    authoring.StartPoint,
                    errorMessage));
            return false;
        }

        ClearPointTransformSnapState();
        _pointTransformBasePoint = authoring.StartPoint!.Value;
        _pointTransformCurrent = resolvedScreen;
        _hasPointTransformBasePoint = true;
        RefreshRayAuthoringPicture();
        RayAuthoringChanged?.Invoke(
            this,
            new CadRayAuthoringChangedEventArgs(
                CadRayAuthoringStage.AwaitingThroughPoint,
                authoring.RayCount,
                authoring.StartPoint));
        Invalidate();
        return true;
    }

    private bool TryAcceptXLineAuthoringPoint(
        CadPoint3D point,
        Vector2? screenPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadXLineModeAuthoringSession? authoring = _xlineAuthoring;
        if (authoring is null)
        {
            errorMessage = "No XLINE command is active.";
            return false;
        }
        Vector2 resolvedScreen;
        try
        {
            resolvedScreen = screenPoint ?? CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            XLineAuthoringChanged?.Invoke(
                this,
                new CadXLineAuthoringChangedEventArgs(
                    CadXLineAuthoringStage.Failed,
                    authoring.LineCount,
                    authoring.FirstPoint,
                    errorMessage,
                    authoring.Mode,
                    authoring.Prompt));
            return false;
        }
        if (!authoring.TryAcceptPoint(point, out errorMessage))
        {
            XLineAuthoringChanged?.Invoke(
                this,
                new CadXLineAuthoringChangedEventArgs(
                    CadXLineAuthoringStage.Failed,
                    authoring.LineCount,
                    authoring.FirstPoint,
                    errorMessage,
                    authoring.Mode,
                    authoring.Prompt));
            return false;
        }

        ClearPointTransformSnapState();
        _pointTransformCurrent = resolvedScreen;
        _hasXLineSourceCandidate = false;
        SynchronizeXLineAcquisitionBase();
        RefreshXLineAuthoringPicture();
        NotifyXLineAuthoringChanged();
        Invalidate();
        return true;
    }

    private bool TryAcceptPolylineAuthoringPoint(
        CadPoint3D point,
        Vector2? screenPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadPolylineAuthoringSession? authoring = _polylineAuthoring;
        if (authoring is null)
        {
            errorMessage = "No PLINE command is active.";
            return false;
        }
        Vector2 resolvedScreen;
        try
        {
            resolvedScreen = screenPoint ??
                CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            RaisePolylineAuthoringChanged(
                CadPolylineAuthoringStage.Failed,
                authoring,
                errorMessage: errorMessage);
            return false;
        }
        if (!authoring.TryAcceptPoint(point, out errorMessage))
        {
            RaisePolylineAuthoringChanged(
                CadPolylineAuthoringStage.Failed,
                authoring,
                errorMessage: errorMessage);
            return false;
        }

        return SynchronizeAcceptedPolylinePoint(
            authoring,
            out errorMessage,
            resolvedScreen);
    }

    private bool SynchronizeAcceptedPolylinePoint(
        CadPolylineAuthoringSession authoring,
        out string? errorMessage,
        Vector2? screenPoint = null)
    {
        errorMessage = null;
        CadPoint3D point = authoring.CurrentPoint!.Value;
        Vector2 resolvedScreen;
        try
        {
            resolvedScreen = screenPoint ?? CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            RaisePolylineAuthoringChanged(
                CadPolylineAuthoringStage.Failed,
                authoring,
                errorMessage: errorMessage);
            return false;
        }
        ClearPointTransformSnapState();
        _pointTransformBasePoint = point;
        _pointTransformCurrent = resolvedScreen;
        _hasPointTransformBasePoint = true;
        RefreshPolylineAuthoringPicture();
        RaisePolylineAuthoringChanged(
            CadPolylineAuthoringStage.AwaitingNextPoint,
            authoring);
        Invalidate();
        return true;
    }

    private void RaisePolylineAuthoringChanged(
        CadPolylineAuthoringStage stage,
        CadPolylineAuthoringSession authoring,
        bool isClosed = false,
        string? errorMessage = null)
    {
        PolylineAuthoringChanged?.Invoke(
            this,
            new CadPolylineAuthoringChangedEventArgs(
                stage,
                authoring.Mode,
                authoring.SegmentCount,
                authoring.CurrentPoint,
                isClosed,
                errorMessage,
                authoring.Prompt,
                authoring.WidthInputMode,
                authoring.NextStartWidth,
                authoring.NextEndWidth));
    }

    private bool TryAcceptCircleAuthoringPoint(
        CadPoint3D point,
        Vector2? screenPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadCircleAuthoringSession? authoring = _circleAuthoring;
        if (authoring is null)
        {
            errorMessage = "No CIRCLE command is active.";
            return false;
        }

        Vector2 resolvedScreen;
        try
        {
            resolvedScreen = screenPoint ?? CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            NotifyCircleAuthoringFailure(authoring, errorMessage);
            return false;
        }

        if (authoring.PointCount < authoring.RequiredPointCount - 1)
        {
            if (!authoring.TryAcceptIntermediatePoint(point, out errorMessage))
            {
                NotifyCircleAuthoringFailure(authoring, errorMessage);
                return false;
            }

            ClearPointTransformSnapState();
            _pointTransformBasePoint = point;
            _pointTransformCurrent = resolvedScreen;
            _hasPointTransformBasePoint = true;
            CircleAuthoringChanged?.Invoke(
                this,
                new CadCircleAuthoringChangedEventArgs(
                    CadCircleAuthoringStage.AwaitingNextPoint,
                    authoring.Mode,
                    authoring.PointCount,
                    point));
            Invalidate();
            return true;
        }

        if (!authoring.TryCreateSnapshot(
                point,
                out CadCircleAuthoringSnapshot snapshot,
                out errorMessage))
        {
            NotifyCircleAuthoringFailure(authoring, errorMessage);
            return false;
        }

        return TryCommitCircleAuthoringSnapshot(
            authoring,
            snapshot,
            point,
            out errorMessage);
    }

    private bool TryCommitCircleAuthoringSnapshot(
        CadCircleAuthoringSession authoring,
        CadCircleAuthoringSnapshot snapshot,
        CadPoint3D? finalPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException(
                "The CAD edit history is not initialized.");
        try
        {
            history.Execute(new CadAddCircleCommand(
                snapshot,
                description: $"CIRCLE: add {authoring.Mode}"));
            CadCircleAuthoringMode mode = authoring.Mode;
            int pointCount = authoring.RequiredPointCount;
            ResetCircleAuthoringState();
            RecompileAfterEdit(session);
            CircleAuthoringChanged?.Invoke(
                this,
                new CadCircleAuthoringChangedEventArgs(
                    CadCircleAuthoringStage.Completed,
                    mode,
                    pointCount,
                    finalPoint,
                    snapshot));
            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            NotifyCircleAuthoringFailure(authoring, errorMessage);
            return false;
        }
    }

    private void NotifyCircleAuthoringFailure(
        CadCircleAuthoringSession authoring,
        string? errorMessage)
    {
        CircleAuthoringChanged?.Invoke(
            this,
            new CadCircleAuthoringChangedEventArgs(
                CadCircleAuthoringStage.Failed,
                authoring.Mode,
                authoring.PointCount,
                authoring.CurrentPoint,
                errorMessage: errorMessage));
        Invalidate();
    }

    private bool TryAcceptArcAuthoringPoint(
        CadPoint3D point,
        Vector2? screenPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadArcAuthoringSession? authoring = _arcAuthoring;
        if (authoring is null)
        {
            errorMessage = "No ARC command is active.";
            return false;
        }

        Vector2 resolvedScreen;
        try
        {
            resolvedScreen = screenPoint ?? CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            NotifyArcAuthoringFailure(authoring, errorMessage);
            return false;
        }

        if (authoring.PointCount < 2)
        {
            if (!authoring.TryAcceptIntermediatePoint(point, out errorMessage))
            {
                NotifyArcAuthoringFailure(authoring, errorMessage);
                return false;
            }

            ClearPointTransformSnapState();
            _pointTransformBasePoint = point;
            _pointTransformCurrent = resolvedScreen;
            _hasPointTransformBasePoint = true;
            ArcAuthoringChanged?.Invoke(
                this,
                new CadArcAuthoringChangedEventArgs(
                    CadArcAuthoringStage.AwaitingNextInput,
                    authoring.Mode,
                    authoring.PointCount,
                    point));
            Invalidate();
            return true;
        }

        if (!authoring.TryCreateSnapshot(
                point,
                out CadArcAuthoringSnapshot snapshot,
                out errorMessage))
        {
            NotifyArcAuthoringFailure(authoring, errorMessage);
            return false;
        }

        return TryCommitArcAuthoringSnapshot(
            authoring,
            snapshot,
            point,
            out errorMessage);
    }

    private bool TryCommitArcAuthoringSnapshot(
        CadArcAuthoringSession authoring,
        CadArcAuthoringSnapshot snapshot,
        CadPoint3D? finalPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException(
                "The CAD edit history is not initialized.");
        try
        {
            history.Execute(new CadAddArcCommand(
                snapshot,
                description: $"ARC: add {authoring.Mode}"));
            CadArcAuthoringMode mode = authoring.Mode;
            int pointCount = authoring.RequiredPointCount;
            ResetArcAuthoringState();
            RecompileAfterEdit(session);
            ArcAuthoringChanged?.Invoke(
                this,
                new CadArcAuthoringChangedEventArgs(
                    CadArcAuthoringStage.Completed,
                    mode,
                    pointCount,
                    finalPoint,
                    snapshot));
            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            NotifyArcAuthoringFailure(authoring, errorMessage);
            return false;
        }
    }

    private void NotifyArcAuthoringFailure(
        CadArcAuthoringSession authoring,
        string? errorMessage)
    {
        ArcAuthoringChanged?.Invoke(
            this,
            new CadArcAuthoringChangedEventArgs(
                CadArcAuthoringStage.Failed,
                authoring.Mode,
                authoring.PointCount,
                authoring.CurrentPoint,
                errorMessage: errorMessage));
        Invalidate();
    }

    private bool TryAcceptEllipseAuthoringPoint(
        CadPoint3D point,
        Vector2? screenPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadEllipseAuthoringSession? authoring = _ellipseAuthoring;
        if (authoring is null)
        {
            errorMessage = "No ELLIPSE command is active.";
            return false;
        }

        try
        {
            _ = screenPoint ?? CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            NotifyEllipseAuthoringFailure(authoring, errorMessage);
            return false;
        }

        if (!authoring.TryAcceptPoint(
                point,
                out CadEllipseAuthoringSnapshot snapshot,
                out bool completed,
                out errorMessage))
        {
            NotifyEllipseAuthoringFailure(authoring, errorMessage);
            return false;
        }
        if (completed)
        {
            return TryCommitEllipseAuthoringSnapshot(
                authoring,
                snapshot,
                point,
                out errorMessage);
        }

        UpdateEllipseAcquisitionBase(authoring);
        EllipseAuthoringChanged?.Invoke(
            this,
            new CadEllipseAuthoringChangedEventArgs(
                CadEllipseAuthoringStage.AwaitingNextInput,
                authoring.Mode,
                authoring.ArcInputMode,
                authoring.InputKind,
                authoring.AcceptedInputCount,
                point));
        Invalidate();
        return true;
    }

    private bool TryCommitEllipseAuthoringSnapshot(
        CadEllipseAuthoringSession authoring,
        CadEllipseAuthoringSnapshot snapshot,
        CadPoint3D? finalPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException(
                "The CAD edit history is not initialized.");
        try
        {
            history.Execute(new CadAddEllipseCommand(
                snapshot,
                description:
                    $"ELLIPSE: add {authoring.Mode}/{authoring.ArcInputMode}"));
            CadEllipseAuthoringMode mode = authoring.Mode;
            CadEllipseArcInputMode arcInputMode = authoring.ArcInputMode;
            CadEllipseAuthoringInputKind inputKind = authoring.InputKind;
            int acceptedInputCount = authoring.AcceptedInputCount + 1;
            ResetEllipseAuthoringState();
            RecompileAfterEdit(session);
            EllipseAuthoringChanged?.Invoke(
                this,
                new CadEllipseAuthoringChangedEventArgs(
                    CadEllipseAuthoringStage.Completed,
                    mode,
                    arcInputMode,
                    inputKind,
                    acceptedInputCount,
                    finalPoint,
                    snapshot));
            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            NotifyEllipseAuthoringFailure(authoring, errorMessage);
            return false;
        }
    }

    private void NotifyEllipseAuthoringFailure(
        CadEllipseAuthoringSession authoring,
        string? errorMessage)
    {
        EllipseAuthoringChanged?.Invoke(
            this,
            new CadEllipseAuthoringChangedEventArgs(
                CadEllipseAuthoringStage.Failed,
                authoring.Mode,
                authoring.ArcInputMode,
                authoring.InputKind,
                authoring.AcceptedInputCount,
                authoring.CurrentPoint,
                errorMessage: errorMessage));
        Invalidate();
    }

    private void UpdateEllipseAcquisitionBase(
        CadEllipseAuthoringSession authoring)
    {
        ClearPointTransformSnapState();
        CadPoint3D? acquisitionBase = authoring.AcquisitionBasePoint;
        if (acquisitionBase is not CadPoint3D point)
        {
            _hasPointTransformBasePoint = false;
            return;
        }

        _pointTransformBasePoint = point;
        _pointTransformCurrent = CreateViewport().WorldToScreen(point);
        _hasPointTransformBasePoint = true;
    }

    private bool TryAcceptPolygonAuthoringPoint(
        CadPoint3D point,
        Vector2? screenPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadPolygonAuthoringSession? authoring = _polygonAuthoring;
        if (authoring is null)
        {
            errorMessage = "No POLYGON command is active.";
            return false;
        }

        try
        {
            _ = screenPoint ?? CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            NotifyPolygonAuthoringFailure(authoring, errorMessage);
            return false;
        }

        if (!authoring.TryAcceptPoint(
                point,
                out CadPolygonAuthoringSnapshot snapshot,
                out bool completed,
                out errorMessage))
        {
            NotifyPolygonAuthoringFailure(authoring, errorMessage);
            return false;
        }
        if (completed)
        {
            return TryCommitPolygonAuthoringSnapshot(
                authoring,
                snapshot,
                point,
                out errorMessage);
        }

        UpdatePolygonAcquisitionBase(authoring);
        PolygonAuthoringChanged?.Invoke(
            this,
            new CadPolygonAuthoringChangedEventArgs(
                CadPolygonAuthoringStage.AwaitingFinalInput,
                authoring.SideCount,
                authoring.Mode,
                authoring.InputKind,
                authoring.AcceptedInputCount,
                point));
        Invalidate();
        return true;
    }

    private bool TryCommitPolygonAuthoringSnapshot(
        CadPolygonAuthoringSession authoring,
        CadPolygonAuthoringSnapshot snapshot,
        CadPoint3D? finalPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException(
                "The CAD edit history is not initialized.");
        try
        {
            history.Execute(new CadAddPolylineCommand(
                snapshot.CreatePolylineSnapshot(),
                description:
                    $"POLYGON: add {authoring.SideCount} {authoring.Mode}"));
            int sideCount = authoring.SideCount;
            CadPolygonAuthoringMode mode = authoring.Mode;
            CadPolygonAuthoringInputKind inputKind = authoring.InputKind;
            int acceptedInputCount = authoring.AcceptedInputCount + 1;
            ResetPolygonAuthoringState();
            RecompileAfterEdit(session);
            PolygonAuthoringChanged?.Invoke(
                this,
                new CadPolygonAuthoringChangedEventArgs(
                    CadPolygonAuthoringStage.Completed,
                    sideCount,
                    mode,
                    inputKind,
                    acceptedInputCount,
                    finalPoint,
                    snapshot));
            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            NotifyPolygonAuthoringFailure(authoring, errorMessage);
            return false;
        }
    }

    private void NotifyPolygonAuthoringFailure(
        CadPolygonAuthoringSession authoring,
        string? errorMessage)
    {
        PolygonAuthoringChanged?.Invoke(
            this,
            new CadPolygonAuthoringChangedEventArgs(
                CadPolygonAuthoringStage.Failed,
                authoring.SideCount,
                authoring.Mode,
                authoring.InputKind,
                authoring.AcceptedInputCount,
                authoring.CurrentPoint,
                errorMessage: errorMessage));
        Invalidate();
    }

    private void UpdatePolygonAcquisitionBase(
        CadPolygonAuthoringSession authoring)
    {
        ClearPointTransformSnapState();
        CadPoint3D? acquisitionBase = authoring.AcquisitionBasePoint;
        if (acquisitionBase is not CadPoint3D point)
        {
            _hasPointTransformBasePoint = false;
            return;
        }

        _pointTransformBasePoint = point;
        _pointTransformCurrent = CreateViewport().WorldToScreen(point);
        _hasPointTransformBasePoint = true;
    }

    private bool TryAcceptRectangleAuthoringPoint(
        CadPoint3D point,
        Vector2? screenPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadRectangleAuthoringSession? authoring = _rectangleAuthoring;
        if (authoring is null)
        {
            errorMessage = "No RECTANG command is active.";
            return false;
        }

        try
        {
            _ = screenPoint ?? CreateViewport().WorldToScreen(point);
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            NotifyRectangleAuthoringFailure(authoring, errorMessage);
            return false;
        }

        if (!authoring.TryAcceptPoint(
                point,
                out CadRectangleAuthoringSnapshot snapshot,
                out bool completed,
                out errorMessage))
        {
            NotifyRectangleAuthoringFailure(authoring, errorMessage);
            return false;
        }
        if (completed)
        {
            return TryCommitRectangleAuthoringSnapshot(
                authoring,
                snapshot,
                point,
                out errorMessage);
        }

        UpdateRectangleAcquisitionBase(authoring);
        RectangleAuthoringChanged?.Invoke(
            this,
            new CadRectangleAuthoringChangedEventArgs(
                CadRectangleAuthoringStage.AwaitingPlacement,
                authoring.Construction,
                authoring.CornerTreatment,
                authoring.RotationRadians,
                authoring.InputKind,
                authoring.AcceptedInputCount,
                point));
        Invalidate();
        return true;
    }

    private bool TryCommitRectangleAuthoringSnapshot(
        CadRectangleAuthoringSession authoring,
        CadRectangleAuthoringSnapshot snapshot,
        CadPoint3D? finalPoint,
        out string? errorMessage)
    {
        errorMessage = null;
        CadDocumentSession session = CurrentSession ??
            throw new InvalidOperationException("No CAD document is loaded.");
        CadDocumentHistory history = _history ??
            throw new InvalidOperationException(
                "The CAD edit history is not initialized.");
        try
        {
            history.Execute(new CadAddPolylineCommand(
                snapshot.CreatePolylineSnapshot(),
                description:
                    $"RECTANG: add {authoring.Construction.Mode} {authoring.CornerTreatment.Mode}"));
            CadRectangleConstruction construction = authoring.Construction;
            CadRectangleCornerTreatment cornerTreatment =
                authoring.CornerTreatment;
            double rotationRadians = authoring.RotationRadians;
            CadRectangleAuthoringInputKind inputKind = authoring.InputKind;
            int acceptedInputCount = authoring.AcceptedInputCount + 1;
            ResetRectangleAuthoringState();
            RecompileAfterEdit(session);
            RectangleAuthoringChanged?.Invoke(
                this,
                new CadRectangleAuthoringChangedEventArgs(
                    CadRectangleAuthoringStage.Completed,
                    construction,
                    cornerTreatment,
                    rotationRadians,
                    inputKind,
                    acceptedInputCount,
                    finalPoint,
                    snapshot));
            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            NotifyRectangleAuthoringFailure(authoring, errorMessage);
            return false;
        }
    }

    private void NotifyRectangleAuthoringFailure(
        CadRectangleAuthoringSession authoring,
        string? errorMessage)
    {
        RectangleAuthoringChanged?.Invoke(
            this,
            new CadRectangleAuthoringChangedEventArgs(
                CadRectangleAuthoringStage.Failed,
                authoring.Construction,
                authoring.CornerTreatment,
                authoring.RotationRadians,
                authoring.InputKind,
                authoring.AcceptedInputCount,
                authoring.CurrentPoint,
                errorMessage: errorMessage));
        Invalidate();
    }

    private void UpdateRectangleAcquisitionBase(
        CadRectangleAuthoringSession authoring)
    {
        ClearPointTransformSnapState();
        CadPoint3D? acquisitionBase = authoring.AcquisitionBasePoint;
        if (acquisitionBase is not CadPoint3D point)
        {
            _hasPointTransformBasePoint = false;
            return;
        }

        _pointTransformBasePoint = point;
        _pointTransformCurrent = CreateViewport().WorldToScreen(point);
        _hasPointTransformBasePoint = true;
    }

    private void AcceptPointTransformPoint(
        CadPoint3D point,
        Vector2? screenPoint)
    {
        CadPointTransformOperation? operation = PendingPointTransformOperation;
        if (operation is null)
        {
            return;
        }

        ClearPointTransformSnapState();

        if (!_hasPointTransformBasePoint)
        {
            _pointTransformBasePoint = point;
            _pointTransformCurrent = screenPoint ??
                CreateViewport().WorldToScreen(point);
            _hasPointTransformBasePoint = true;
            PointTransformChanged?.Invoke(
                this,
                new CadPointTransformChangedEventArgs(
                    operation.Value,
                    CadPointTransformStage.AwaitingSecondPoint,
                    point));
            Invalidate();
            return;
        }

        CadPoint3D basePoint = _pointTransformBasePoint;
        var displacement = new CadPoint3D(
            point.X - basePoint.X,
            point.Y - basePoint.Y,
            point.Z - basePoint.Z);
        ResetPointTransformState(notify: false);
        try
        {
            bool applied = operation == CadPointTransformOperation.Copy
                ? DuplicateSelection(displacement)
                : displacement != CadPoint3D.Zero &&
                    TranslateSelection(displacement);
            PointTransformChanged?.Invoke(
                this,
                new CadPointTransformChangedEventArgs(
                    operation.Value,
                    CadPointTransformStage.Completed,
                    basePoint,
                    point,
                    displacement,
                    applied
                        ? null
                        : "The point transform did not change the selection."));
        }
        catch (Exception exception)
        {
            PointTransformChanged?.Invoke(
                this,
                new CadPointTransformChangedEventArgs(
                    operation.Value,
                    CadPointTransformStage.Failed,
                    basePoint,
                    point,
                    displacement,
                    exception.Message));
        }
        Invalidate();
    }

    private static bool IsFinite(CadPoint3D point) =>
        double.IsFinite(point.X) &&
        double.IsFinite(point.Y) &&
        double.IsFinite(point.Z);

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
        ThrowIfDrawOrderReferenceSelectionPending();
        ThrowIfPointTransformPending();
    }

    private void ThrowIfDrawOrderReferenceSelectionPending()
    {
        if (PendingDrawOrderPlacement is not null)
        {
            throw new InvalidOperationException(
                "Commit or cancel the pending draw-order reference selection first.");
        }
    }

    private void ThrowIfPointTransformPending()
    {
        if (IsPointAcquisitionActive)
        {
            throw new InvalidOperationException(
                "Complete the pending point-acquisition command first.");
        }
    }

    private void ResetPointTransformState(bool notify)
    {
        CadPointTransformOperation? operation = PendingPointTransformOperation;
        CadPoint3D? basePoint = PendingPointTransformBasePoint;
        PendingPointTransformOperation = null;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        _pointTransformBasePoint = default;
        _pointTransformCurrent = default;
        _pointTransformPointerPosition = default;
        _pointTransformObjectSnap = default;
        _pointTransformGridSnap = default;
        _hasPointTransformGridSnap = false;
        _pointTransformOrtho = default;
        _hasPointTransformOrtho = false;
        _pointTransformPolarTracking = default;
        _hasPointTransformPolarTracking = false;
        _hasPointTransformPointerPosition = false;
        if (notify && operation is CadPointTransformOperation value)
        {
            PointTransformChanged?.Invoke(
                this,
                new CadPointTransformChangedEventArgs(
                    value,
                    CadPointTransformStage.Canceled,
                    basePoint));
        }
    }

    private void ResetLineAuthoringState()
    {
        _lineAuthoring = null;
        _lineAuthoringPicture?.Dispose();
        _lineAuthoringPicture = null;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        _pointTransformBasePoint = default;
        _pointTransformCurrent = default;
        ClearPointTransformSnapState();
    }

    private void ResetRayAuthoringState()
    {
        _rayAuthoring = null;
        _rayAuthoringPicture?.Dispose();
        _rayAuthoringPicture = null;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        _pointTransformBasePoint = default;
        _pointTransformCurrent = default;
        ClearPointTransformSnapState();
    }

    private void ResetXLineAuthoringState()
    {
        _xlineAuthoring = null;
        _xlineAuthoringPicture?.Dispose();
        _xlineAuthoringPicture = null;
        _xlineSourceCandidate = default;
        _hasXLineSourceCandidate = false;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        _pointTransformBasePoint = default;
        _pointTransformCurrent = default;
        ClearPointTransformSnapState();
    }

    private void ResetPointAuthoringState()
    {
        _pointAuthoring = null;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        _pointTransformBasePoint = default;
        _pointTransformCurrent = default;
        ClearPointTransformSnapState();
    }

    private void ResetPolylineAuthoringState()
    {
        _polylineAuthoring = null;
        _polylineAuthoringPicture?.Dispose();
        _polylineAuthoringPicture = null;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        _pointTransformBasePoint = default;
        _pointTransformCurrent = default;
        ClearPointTransformSnapState();
    }

    private void ResetCircleAuthoringState()
    {
        _circleAuthoring = null;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        _pointTransformBasePoint = default;
        _pointTransformCurrent = default;
        ClearPointTransformSnapState();
    }

    private void ResetArcAuthoringState()
    {
        _arcAuthoring = null;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        _pointTransformBasePoint = default;
        _pointTransformCurrent = default;
        ClearPointTransformSnapState();
    }

    private void ResetEllipseAuthoringState()
    {
        _ellipseAuthoring = null;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        _pointTransformBasePoint = default;
        _pointTransformCurrent = default;
        ClearPointTransformSnapState();
    }

    private void ResetPolygonAuthoringState()
    {
        _polygonAuthoring = null;
        _polygonAuthoringPicture?.Dispose();
        _polygonAuthoringPicture = null;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        _pointTransformBasePoint = default;
        _pointTransformCurrent = default;
        ClearPointTransformSnapState();
    }

    private void ResetRectangleAuthoringState()
    {
        _rectangleAuthoring = null;
        _hasPointTransformBasePoint = false;
        _isPointTransformPointerPressed = false;
        _pointTransformBasePoint = default;
        _pointTransformCurrent = default;
        ClearPointTransformSnapState();
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
            _lineAuthoringPicture?.Dispose();
            _lineAuthoringPicture = null;
            _rayAuthoringPicture?.Dispose();
            _rayAuthoringPicture = null;
            _xlineAuthoringPicture?.Dispose();
            _xlineAuthoringPicture = null;
            _polylineAuthoringPicture?.Dispose();
            _polylineAuthoringPicture = null;
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
        RefreshLineAuthoringPicture();
        RefreshRayAuthoringPicture();
        RefreshXLineAuthoringPicture();
        RefreshPolylineAuthoringPicture();
    }

    private void RefreshLineAuthoringPicture()
    {
        GpuPicture? replacement = null;
        CadLineAuthoringSession? authoring = _lineAuthoring;
        if (authoring is not null &&
            authoring.SegmentCount > 0 &&
            CurrentSnapshot is not null &&
            Size.X > 0.0f &&
            Size.Y > 0.0f)
        {
            CadPlanViewport viewport = CreateViewport();
            var recorder = new GpuPictureRecorder();
            DrawingContext target = recorder.BeginRecording(
                new Rect(0, 0, Size.X, Size.Y));
            try
            {
                ReadOnlySpan<CadPoint3D> points = authoring.Points.Span;
                for (int i = 1; i < points.Length; i++)
                {
                    target.DrawLine(
                        _drawOrderReferencePen,
                        viewport.WorldToScreen(points[i - 1]),
                        viewport.WorldToScreen(points[i]));
                }
                replacement = recorder.EndRecording();
            }
            catch
            {
                target.Clear();
                throw;
            }
        }

        GpuPicture? previous = _lineAuthoringPicture;
        _lineAuthoringPicture = replacement;
        previous?.Dispose();
    }

    private void RefreshRayAuthoringPicture()
    {
        GpuPicture? replacement = null;
        CadRayAuthoringSession? authoring = _rayAuthoring;
        if (authoring is not null &&
            authoring.RayCount > 0 &&
            CurrentSnapshot is not null &&
            Size.X > 0.0f &&
            Size.Y > 0.0f)
        {
            CadPlanViewport viewport = CreateViewport();
            CadBounds3D clipBounds = viewport.CreatePlanClipBounds();
            var recorder = new GpuPictureRecorder();
            DrawingContext target = recorder.BeginRecording(
                new Rect(0, 0, Size.X, Size.Y));
            try
            {
                CadPoint3D startPoint = authoring.StartPoint!.Value;
                ReadOnlySpan<CadPoint3D> directions = authoring.Directions.Span;
                var path = new PathGeometry();
                for (int i = 0; i < directions.Length; i++)
                {
                    var primitive = new CadConstructionLinePrimitive(
                        startPoint,
                        directions[i]);
                    if (CadConstructionSceneCompiler.TryClipPlan(
                            primitive,
                            clipBounds,
                            isRay: true,
                            out CadPoint3D clippedStart,
                            out CadPoint3D clippedEnd))
                    {
                        var figure = new PathFigure(
                            viewport.WorldToScreen(clippedStart))
                        {
                            IsFilled = false,
                            IsClosed = false,
                        };
                        figure.Segments.Add(new LineSegment(
                            viewport.WorldToScreen(clippedEnd)));
                        path.Figures.Add(figure);
                    }
                }
                if (path.Figures.Count > 0)
                {
                    target.DrawPath(null, _drawOrderReferencePen, path);
                }
                replacement = recorder.EndRecording();
            }
            catch
            {
                target.Clear();
                throw;
            }
        }

        GpuPicture? previous = _rayAuthoringPicture;
        _rayAuthoringPicture = replacement;
        previous?.Dispose();
    }

    private void RefreshXLineAuthoringPicture()
    {
        GpuPicture? replacement = null;
        CadXLineModeAuthoringSession? authoring = _xlineAuthoring;
        if (authoring is not null &&
            authoring.LineCount > 0 &&
            CurrentSnapshot is not null &&
            Size.X > 0.0f &&
            Size.Y > 0.0f)
        {
            CadPlanViewport viewport = CreateViewport();
            CadBounds3D clipBounds = viewport.CreatePlanClipBounds();
            var recorder = new GpuPictureRecorder();
            DrawingContext target = recorder.BeginRecording(
                new Rect(0, 0, Size.X, Size.Y));
            try
            {
                ReadOnlySpan<CadXLineDefinition> definitions =
                    authoring.Definitions.Span;
                var path = new PathGeometry();
                for (int i = 0; i < definitions.Length; i++)
                {
                    CadXLineDefinition definition = definitions[i];
                    var primitive = new CadConstructionLinePrimitive(
                        definition.FirstPoint,
                        definition.Direction);
                    if (CadConstructionSceneCompiler.TryClipPlan(
                            primitive,
                            clipBounds,
                            isRay: false,
                            out CadPoint3D clippedStart,
                            out CadPoint3D clippedEnd))
                    {
                        var figure = new PathFigure(
                            viewport.WorldToScreen(clippedStart))
                        {
                            IsFilled = false,
                            IsClosed = false,
                        };
                        figure.Segments.Add(new LineSegment(
                            viewport.WorldToScreen(clippedEnd)));
                        path.Figures.Add(figure);
                    }
                }
                if (path.Figures.Count > 0)
                {
                    target.DrawPath(null, _drawOrderReferencePen, path);
                }
                replacement = recorder.EndRecording();
            }
            catch
            {
                target.Clear();
                throw;
            }
        }

        GpuPicture? previous = _xlineAuthoringPicture;
        _xlineAuthoringPicture = replacement;
        previous?.Dispose();
    }

    private void RefreshPolylineAuthoringPicture()
    {
        GpuPicture? replacement = null;
        CadPolylineAuthoringSession? authoring = _polylineAuthoring;
        if (authoring is not null &&
            authoring.SegmentCount > 0 &&
            CurrentSnapshot is not null &&
            Size.X > 0.0f &&
            Size.Y > 0.0f)
        {
            CadPlanViewport viewport = CreateViewport();
            var recorder = new GpuPictureRecorder();
            DrawingContext target = recorder.BeginRecording(
                new Rect(0, 0, Size.X, Size.Y));
            try
            {
                ReadOnlySpan<CadPoint3D> points = authoring.Points.Span;
                ReadOnlySpan<double> bulges = authoring.Bulges.Span;
                var path = new PathGeometry();
                var figure = new PathFigure(viewport.WorldToScreen(points[0]))
                {
                    IsFilled = false,
                    IsClosed = false,
                };
                for (int i = 1; i < points.Length; i++)
                {
                    AppendPolylinePreviewSegment(
                        figure,
                        viewport,
                        points[i - 1],
                        points[i],
                        bulges[i - 1]);
                }
                path.Figures.Add(figure);
                target.DrawPath(null, _drawOrderReferencePen, path);
                replacement = recorder.EndRecording();
            }
            catch
            {
                target.Clear();
                throw;
            }
        }

        GpuPicture? previous = _polylineAuthoringPicture;
        _polylineAuthoringPicture = replacement;
        previous?.Dispose();
    }

    private static void AppendPolylinePreviewSegment(
        PathFigure figure,
        CadPlanViewport viewport,
        CadPoint3D start,
        CadPoint3D end,
        double bulge)
    {
        if (bulge == 0.0 ||
            !CadPolylineAuthoringSession.TryGetBulgeGeometry(
                start,
                end,
                bulge,
                out _,
                out double radius,
                out _,
                out double sweep))
        {
            figure.Segments.Add(new LineSegment(viewport.WorldToScreen(end)));
            return;
        }

        double screenRadius = radius * viewport.Zoom;
        if (!double.IsFinite(screenRadius) || screenRadius > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bulge),
                "The projected PLINE preview arc radius exceeds finite screen coordinates.");
        }

        figure.Segments.Add(new ArcSegment(
            viewport.WorldToScreen(end),
            new Vector2((float)screenRadius, (float)screenRadius),
            rotationAngle: 0.0f,
            isLargeArc: Math.Abs(sweep) > Math.PI,
            sweepDirection: sweep > 0.0
                ? SweepDirection.Clockwise
                : SweepDirection.Counterclockwise));
    }

    private static int GetPolylinePreviewStepCount(
        CadPlanViewport viewport,
        double worldRadius,
        double sweep)
    {
        double rasterScale = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
        if (!double.IsFinite(rasterScale) || rasterScale <= 0.0)
        {
            rasterScale = 1.0;
        }

        double physicalRadius = worldRadius * viewport.Zoom * rasterScale;
        if (!double.IsFinite(physicalRadius))
        {
            return PolylinePreviewMaximumStepCount;
        }
        if (physicalRadius <= PolylinePreviewMaximumPhysicalError)
        {
            return 1;
        }

        double maximumStepAngle = 2.0 * Math.Acos(Math.Clamp(
            1.0 - (PolylinePreviewMaximumPhysicalError / physicalRadius),
            -1.0,
            1.0));
        if (!double.IsFinite(maximumStepAngle) || maximumStepAngle <= 0.0)
        {
            return PolylinePreviewMaximumStepCount;
        }

        double requiredStepCount = Math.Ceiling(Math.Abs(sweep) / maximumStepAngle);
        return (int)Math.Clamp(
            requiredStepCount,
            1.0,
            PolylinePreviewMaximumStepCount);
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
        ResetPointTransformState(notify: false);
        ResetLineAuthoringState();
        ResetRayAuthoringState();
        ResetXLineAuthoringState();
        ResetPointAuthoringState();
        ResetPolylineAuthoringState();
        ResetCircleAuthoringState();
        ResetArcAuthoringState();
        ResetEllipseAuthoringState();
        ResetPolygonAuthoringState();
        ResetRectangleAuthoringState();
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
            var wipeout = new Wipeout
            {
                InsertPoint = new XYZ(-18, -50, 0),
                UVector = new XYZ(1, 0, 0),
                VVector = new XYZ(0, 1, 0),
                Size = new XY(36, 12),
                ClippingState = true,
                ClipMode = ClipMode.Outside,
            };
            wipeout.ClipBoundaryVertices.AddRange([
                new XY(-0.5, -0.5),
                new XY(35.5, -0.5),
                new XY(31.5, 11.5),
                new XY(3.5, 11.5),
            ]);
            document.Entities.Add(wipeout);
            var imageDefinition = new ImageDefinition
            {
                Name = "ProGPU sample raster",
                FileName = "progpu-cad-sample.png",
                Size = new XY(1, 1),
                DefaultSize = new XY(1, 1),
                IsLoaded = true,
            };
            document.Entities.Add(new RasterImage(imageDefinition)
            {
                InsertPoint = new XYZ(22, -48, 0),
                UVector = new XYZ(32, 0, 0),
                VVector = new XYZ(0, 18, 0),
                Size = new XY(1, 1),
                Flags = ImageDisplayFlags.ShowImage |
                    ImageDisplayFlags.ShowNotAlignedImage |
                    ImageDisplayFlags.TransparencyIsOn,
                Brightness = 55,
                Contrast = 60,
                Fade = 12,
            });
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

    private static ReadOnlySpan<byte> RepresentativeRasterImageBytes =>
    [
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
        0x00, 0x00, 0x00, 0x0d, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x04, 0x00, 0x00, 0x00, 0xb5, 0x1c, 0x0c,
        0x02, 0x00, 0x00, 0x00, 0x0b, 0x49, 0x44, 0x41,
        0x54, 0x78, 0xda, 0x63, 0xfc, 0xff, 0x1f, 0x00,
        0x02, 0xeb, 0x01, 0xf5, 0x8f, 0x59, 0x73, 0xe8,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4e, 0x44,
        0xae, 0x42, 0x60, 0x82,
    ];

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
