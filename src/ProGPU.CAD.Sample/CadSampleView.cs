using System.Globalization;
using ACadSharp.Header;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Media3D;
using ProGPU.Fonts.Inter;
using ProGPU.Scene.Extensions;
using ProGPU.Text;
using ProGPU.Vector;
using Windows.Storage;
using ACadLayout = ACadSharp.Objects.Layout;
using Key = Silk.NET.Input.Key;

namespace ProGPU.CAD.Sample;

/// <summary>Shared desktop/browser CAD shell with real stream open and save workflows.</summary>
public sealed class CadSampleView : Grid
{
    private const int MeshSelectionCycleCapacity = 64;
    private const float MeshSelectionCyclePointTolerance = 4.0f;

    private readonly CadDocumentStore _store = new();
    private readonly CadSampleCanvas _canvas;
    private readonly Grid _contentHost;
    private readonly Viewport3D _viewport3D;
    private readonly CadMesh3DSubobjectOverlay _meshSubobjectOverlay;
    private readonly CadMesh3DViewCoordinator _mesh3DView = new();
    private readonly Brush _meshSelectionBrush =
        new ThemeResourceBrush("SystemAccentColor");
    private readonly List<MeshMaterialBinding> _meshMaterialBindings = new();
    private readonly CadMesh3DSelectionResult[] _meshSelectionHits =
        new CadMesh3DSelectionResult[MeshSelectionCycleCapacity];
    private readonly CadMesh3DSubobjectSelectionResult[] _meshSubobjectHits =
        new CadMesh3DSubobjectSelectionResult[MeshSelectionCycleCapacity];
    private readonly CadMesh3DSubobjectId[] _meshSubobjectRegionHits =
        new CadMesh3DSubobjectId[CadMesh3DSubobjectOverlay.MaximumSelectionCount];
    private readonly List<CadMesh3DSubobjectId> _selectedMeshSubobjects =
        new(CadMesh3DSubobjectOverlay.MaximumSelectionCount);
    private int[] _meshRegionRootScratch = [];
    private ulong[] _meshRegionHandles = [];
    private int[] _meshSubobjectPrimitiveScratch = [];
    private PerspectiveCamera? _observedMeshCamera;
    private CadMesh3DSelectionResult? _lastMeshSelection;
    private CadMesh3DViewport? _meshSelectionCycleViewport;
    private System.Numerics.Vector2 _meshSelectionCyclePoint;
    private ulong _meshSelectionCycleGeneration;
    private int _meshSelectionCycleIndex = -1;
    private CadMesh3DSubobjectSelectionResult? _lastMeshSubobjectSelection;
    private System.Numerics.Vector2 _meshSubobjectCyclePoint;
    private ulong _meshSubobjectCycleGeneration;
    private int _meshSubobjectCycleHitCount;
    private int _meshSubobjectCycleIndex = -1;
    private CadMesh3DSubobjectFilter _meshSubobjectFilter;
    private float _meshPickTargetHeight =
        CadMesh3DSelectionIndex.DefaultPickTargetHeight;
    private readonly CadPrintPreviewCanvas _printPreview;
    private readonly Button _viewModeButton;
    private readonly TextBlock _viewModeText;
    private readonly ComboBox _meshPickTargetSelector;
    private readonly ComboBox _meshRegionSelectionSelector;
    private readonly ComboBox _meshSubobjectSelector;
    private readonly ComboBox _attributeDisplaySelector;
    private readonly Button _printPreviewButton;
    private readonly TextBlock _printPreviewText;
    private readonly Button _exportPdfButton;
    private readonly Button _exportPngButton;
    private readonly ComboBox _pageSetupSelector;
    private readonly Button _applyPageSetupButton;
    private readonly TextBox _pageSetupNameInput;
    private readonly Button _createPageSetupButton;
    private readonly Button _updatePageSetupButton;
    private readonly Button _renamePageSetupButton;
    private readonly Button _deletePageSetupButton;
    private readonly TextBox _pageSetupPaperWidthInput;
    private readonly TextBox _pageSetupPaperHeightInput;
    private readonly ComboBox _pageSetupRotationSelector;
    private readonly ComboBox _pageSetupPlotAreaSelector;
    private readonly CheckBox _pageSetupCenterCheckBox;
    private readonly CheckBox _pageSetupLineweightsCheckBox;
    private readonly Button _editPageSetupFieldsButton;
    private readonly TextBlock _status;
    private readonly Button _openButton;
    private readonly Button _loadLineTypesButton;
    private readonly Button _reloadLineTypesButton;
    private readonly Button _importPageSetupsButton;
    private readonly Button _importReplacePageSetupsButton;
    private readonly Button _saveButton;
    private readonly Button _fitButton;
    private readonly Button _clearSelectionButton;
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private readonly Button _deleteButton;
    private readonly Button _lineButton;
    private readonly Button _lineUndoButton;
    private readonly Button _lineCloseButton;
    private readonly Button _lineFinishButton;
    private readonly Button _rayButton;
    private readonly Button _rayUndoButton;
    private readonly Button _rayFinishButton;
    private readonly ComboBox _xlineModeSelector;
    private readonly Button _xlineButton;
    private readonly Button _xlineUndoButton;
    private readonly Button _xlineFinishButton;
    private readonly Button _pointButton;
    private readonly Button _polylineButton;
    private readonly Button _polylineUndoButton;
    private readonly Button _polylineLineModeButton;
    private readonly Button _polylineArcModeButton;
    private readonly ComboBox _polylineArcConstructionSelector;
    private readonly Button _polylineArcConstructionButton;
    private readonly Button _polylineWidthButton;
    private readonly Button _polylineHalfwidthButton;
    private readonly Button _polylineLengthButton;
    private readonly Button _polylineCloseButton;
    private readonly Button _polylineFinishButton;
    private readonly Button _circleButton;
    private readonly Button _circleDiameterButton;
    private readonly Button _circleTwoPointButton;
    private readonly Button _circleThreePointButton;
    private readonly ComboBox _arcModeSelector;
    private readonly Button _arcButton;
    private readonly ComboBox _ellipseModeSelector;
    private readonly ComboBox _ellipseArcInputSelector;
    private readonly Button _ellipseButton;
    private readonly TextBox _polygonSideCountInput;
    private readonly ComboBox _polygonModeSelector;
    private readonly Button _polygonButton;
    private readonly ComboBox _rectangleConstructionSelector;
    private readonly ComboBox _rectangleAreaDimensionSelector;
    private readonly TextBox _rectangleValuesInput;
    private readonly ComboBox _rectangleCornerSelector;
    private readonly TextBox _rectangleCornerValuesInput;
    private readonly TextBox _rectangleRotationInput;
    private readonly Button _rectangleButton;
    private readonly Button[] _drawOrderButtons;
    private readonly Button[] _moveButtons;
    private readonly Button[] _copyButtons;
    private readonly Button _moveByPointsButton;
    private readonly Button _copyByPointsButton;
    private readonly ComboBox _objectSnapSelector;
    private readonly CheckBox _planGridSnapCheckBox;
    private readonly CheckBox _planGridDisplayCheckBox;
    private readonly CheckBox _planGridDotsCheckBox;
    private readonly CheckBox _planGridIsometricCheckBox;
    private readonly ComboBox _planGridIsoplaneSelector;
    private readonly TextBox _planSnapUnitXInput;
    private readonly TextBox _planSnapUnitYInput;
    private readonly TextBox _planGridUnitXInput;
    private readonly TextBox _planGridUnitYInput;
    private readonly CheckBox _planGridAdaptiveCheckBox;
    private readonly CheckBox _planGridSubdivisionCheckBox;
    private readonly CheckBox _planGridBeyondLimitsCheckBox;
    private readonly TextBox _planGridMajorInput;
    private readonly Button _applyPlanGridDisplayButton;
    private readonly CheckBox _planOrthoCheckBox;
    private readonly CheckBox _planPolarTrackingCheckBox;
    private readonly ComboBox _planPolarTrackingIncrementSelector;
    private readonly CheckBox _planPolarRelativeCheckBox;
    private readonly CheckBox _planPolarAdditionalAnglesCheckBox;
    private readonly TextBox _planPolarAdditionalAnglesInput;
    private readonly CheckBox _planPolarSnapCheckBox;
    private readonly TextBox _planPolarSnapDistanceInput;
    private readonly TextBox _pointTransformInput;
    private readonly Button _acceptPointTransformInputButton;
    private readonly Button[] _rotateButtons;
    private readonly Button[] _scaleButtons;
    private readonly Button _meshSmoothMoreButton;
    private readonly Button _meshSmoothLessButton;
    private readonly TextBox _meshCreaseInput;
    private readonly Button _setMeshCreaseButton;
    private readonly Button _removeMeshCreaseButton;
    private readonly TextBox _selectionColorInput;
    private readonly ComboBox _selectionLineWeightSelector;
    private readonly Button _setSelectionColorButton;
    private readonly Button _setSelectionLineWeightButton;
    private readonly ComboBox _selectionLayerSelector;
    private readonly ComboBox _selectionLineTypeSelector;
    private readonly TextBox _selectionLineTypeScaleInput;
    private readonly TextBox _selectionTransparencyInput;
    private readonly ComboBox _selectionVisibilitySelector;
    private readonly TextBox _selectionSolidThicknessInput;
    private readonly ComboBox _selectionAttributeSelector;
    private readonly TextBox _selectionAttributeValueInput;
    private readonly TextBox _selectionAttributePromptInput;
    private readonly TextBox _selectionAttributeTagInput;
    private readonly CheckBox _selectionAttributeInvisibleCheckBox;
    private readonly CheckBox _selectionAttributeVerifyCheckBox;
    private readonly CheckBox _selectionAttributePresetCheckBox;
    private readonly CheckBox _selectionAttributePositionLockedCheckBox;
    private readonly CheckBox _selectionAttributeConstantCheckBox;
    private readonly Button _setSelectionLayerButton;
    private readonly Button _setSelectionLineTypeButton;
    private readonly Button _setSelectionLineTypeScaleButton;
    private readonly Button _setSelectionTransparencyButton;
    private readonly Button _setSelectionVisibilityButton;
    private readonly Button _setSelectionSolidThicknessButton;
    private readonly Button _setSelectionAttributeValueButton;
    private readonly Button _setSelectionAttributePromptButton;
    private readonly Button _setSelectionAttributeTagButton;
    private readonly Button _setSelectionAttributeModesButton;
    private readonly Button _setSelectionAttributeConstantButton;
    private readonly Button _synchronizeSelectionAttributePropertiesButton;
    private readonly ComboBox _layerStateSelector;
    private readonly ComboBox _layerVisibilitySelector;
    private readonly ComboBox _layerPlotSelector;
    private readonly ComboBox _layerFreezeSelector;
    private readonly ComboBox _layerLockSelector;
    private readonly TextBox _layerColorInput;
    private readonly ComboBox _layerLineWeightSelector;
    private readonly ComboBox _layerLineTypeSelector;
    private readonly Button _setLayerVisibilityButton;
    private readonly Button _setLayerPlotButton;
    private readonly Button _setLayerFreezeButton;
    private readonly Button _setLayerLockButton;
    private readonly Button _setLayerColorButton;
    private readonly Button _setLayerLineWeightButton;
    private readonly Button _setLayerLineTypeButton;
    private readonly TextBox _layerNameInput;
    private readonly Button _createLayerButton;
    private readonly Button _renameLayerButton;
    private readonly Button _removeLayerButton;
    private readonly Button _queueLayerMergeSourceButton;
    private readonly Button _clearLayerMergeSourcesButton;
    private readonly TextBlock _layerMergeSourceCountText;
    private readonly ComboBox _layerMergeTargetSelector;
    private readonly Button _mergeLayerButton;
    private readonly TextBox _moveStepInput;
    private readonly TextBox _copyArrayItemsInput;
    private readonly ComboBox _copyArrayModeSelector;
    private readonly TextBox _rotationStepInput;
    private readonly TextBox _scaleFactorInput;
    private readonly List<string> _shxSupportDirectories = new();
    private readonly List<string> _layerMergeSourceNames = new();
    private readonly HashSet<string> _layerMergeSourceNameSet =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _isBusy;
    private string _currentDocumentName = "Representative analytic scene";
    private int _currentDiagnosticCount;
    private bool _is3DView;
    private bool _isPrintPreview;
    private bool _isRefreshingPageSetups;
    private bool _isRefreshingPageSetupFields;
    private bool _isRefreshingAttributeDisplay;
    private bool _isRefreshingPlanGridDisplay;
    private bool _isRefreshingPlanConstraints;
    private bool _isRefreshingSelectionProperties;
    private bool _isSelectionEditable;
    private bool _isMeshSelection;
    private int? _commonMeshSubdivisionLevel;
    private bool _isSolidThicknessSelection;
    private bool _selectedLayerCanRename;
    private bool _selectedLayerCanRemove;
    private CadDocumentSession? _selectionPropertyCatalogSession;
    private ulong _selectionPropertyCatalogGeneration = ulong.MaxValue;
    private CadDocumentSession? _layerMergeSourceSession;
    private ulong _layerMergeSourceGeneration = ulong.MaxValue;

    public CadShxFontCatalog ShxFonts => _canvas.ShxFonts;

    public CadSampleCanvas Canvas => _canvas;

    public Viewport3D MeshViewport => _viewport3D;

    public CadMesh3DViewStatistics MeshViewStatistics =>
        _mesh3DView.Statistics;

    public CadMesh3DViewport? MeshViewportState =>
        _mesh3DView.Viewport;

    public CadRecordedMesh3DScene? MeshScene => _mesh3DView.Scene;

    public CadMesh3DSelectionIndex? MeshSelectionIndex =>
        _mesh3DView.SelectionIndex;

    public CadMesh3DSelectionResult? LastMeshSelection =>
        _lastMeshSelection;

    public CadMesh3DSubobjectSelectionResult? LastMeshSubobjectSelection =>
        _lastMeshSubobjectSelection;

    public IReadOnlyList<CadMesh3DSubobjectId> SelectedMeshSubobjects =>
        _selectedMeshSubobjects;

    public int MeshSubobjectCycleIndex => _meshSubobjectCycleIndex;

    public int MeshSubobjectCycleHitCount => _meshSubobjectCycleHitCount;

    public ComboBox MeshPickTargetSelector => _meshPickTargetSelector;

    public ComboBox MeshRegionSelectionSelector =>
        _meshRegionSelectionSelector;

    public ComboBox MeshSubobjectSelector => _meshSubobjectSelector;

    public float MeshPickTargetHeight
    {
        get => _meshPickTargetHeight;
        set
        {
            if (!float.IsFinite(value) ||
                value < 0.0f ||
                value > CadMesh3DSelectionIndex.MaximumPickTargetHeight)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(value),
                    $"The Mesh3D pick target must be between 0 and {CadMesh3DSelectionIndex.MaximumPickTargetHeight} logical pixels.");
            }
            _meshPickTargetHeight = value;
            for (int index = 0;
                 index < _meshPickTargetSelector.Items.Count;
                 index++)
            {
                if (_meshPickTargetSelector.Items[index] is ComboBoxItem
                    {
                        Tag: float height,
                    } && height == value)
                {
                    _meshPickTargetSelector.SelectedIndex = index;
                    return;
                }
            }
            _meshPickTargetSelector.SelectedIndex = -1;
        }
    }

    public CadPrintPreviewCanvas PrintPreview => _printPreview;

    public ComboBox PageSetupSelector => _pageSetupSelector;

    public ComboBox AttributeDisplaySelector => _attributeDisplaySelector;

    public TextBox PageSetupNameInput => _pageSetupNameInput;

    public TextBox PageSetupPaperWidthInput => _pageSetupPaperWidthInput;

    public TextBox PageSetupPaperHeightInput => _pageSetupPaperHeightInput;

    public ComboBox PageSetupRotationSelector => _pageSetupRotationSelector;

    public ComboBox PageSetupPlotAreaSelector => _pageSetupPlotAreaSelector;

    public CheckBox PageSetupCenterCheckBox => _pageSetupCenterCheckBox;

    public CheckBox PageSetupLineweightsCheckBox =>
        _pageSetupLineweightsCheckBox;

    public TextBox SelectionColorInput => _selectionColorInput;

    public ComboBox SelectionLineWeightSelector =>
        _selectionLineWeightSelector;

    public ComboBox SelectionLayerSelector => _selectionLayerSelector;

    public ComboBox SelectionLineTypeSelector => _selectionLineTypeSelector;

    public TextBox SelectionLineTypeScaleInput =>
        _selectionLineTypeScaleInput;

    public TextBox SelectionTransparencyInput =>
        _selectionTransparencyInput;

    public ComboBox SelectionVisibilitySelector =>
        _selectionVisibilitySelector;

    public TextBox SelectionSolidThicknessInput =>
        _selectionSolidThicknessInput;

    public TextBox CopyArrayItemsInput => _copyArrayItemsInput;

    public ComboBox CopyArrayModeSelector => _copyArrayModeSelector;

    public ComboBox ObjectSnapSelector => _objectSnapSelector;

    public CheckBox PlanGridSnapCheckBox => _planGridSnapCheckBox;

    public CheckBox PlanGridDisplayCheckBox => _planGridDisplayCheckBox;

    public CheckBox PlanGridDotsCheckBox => _planGridDotsCheckBox;

    public CheckBox PlanGridIsometricCheckBox => _planGridIsometricCheckBox;

    public ComboBox PlanGridIsoplaneSelector => _planGridIsoplaneSelector;

    public TextBox PlanSnapUnitXInput => _planSnapUnitXInput;

    public TextBox PlanSnapUnitYInput => _planSnapUnitYInput;

    public TextBox PlanGridUnitXInput => _planGridUnitXInput;

    public TextBox PlanGridUnitYInput => _planGridUnitYInput;

    public CheckBox PlanGridAdaptiveCheckBox => _planGridAdaptiveCheckBox;

    public CheckBox PlanGridSubdivisionCheckBox =>
        _planGridSubdivisionCheckBox;

    public CheckBox PlanGridBeyondLimitsCheckBox =>
        _planGridBeyondLimitsCheckBox;

    public TextBox PlanGridMajorInput => _planGridMajorInput;

    public Button ApplyPlanGridDisplayButton => _applyPlanGridDisplayButton;

    public CheckBox PlanOrthoCheckBox => _planOrthoCheckBox;

    public CheckBox PlanPolarTrackingCheckBox =>
        _planPolarTrackingCheckBox;

    public ComboBox PlanPolarTrackingIncrementSelector =>
        _planPolarTrackingIncrementSelector;

    public CheckBox PlanPolarRelativeCheckBox =>
        _planPolarRelativeCheckBox;

    public CheckBox PlanPolarAdditionalAnglesCheckBox =>
        _planPolarAdditionalAnglesCheckBox;

    public TextBox PlanPolarAdditionalAnglesInput =>
        _planPolarAdditionalAnglesInput;

    public CheckBox PlanPolarSnapCheckBox => _planPolarSnapCheckBox;

    public TextBox PlanPolarSnapDistanceInput => _planPolarSnapDistanceInput;

    public TextBox PointTransformInput => _pointTransformInput;

    public Button LineButton => _lineButton;

    public Button LineUndoButton => _lineUndoButton;

    public Button LineCloseButton => _lineCloseButton;

    public Button LineFinishButton => _lineFinishButton;

    public Button RayButton => _rayButton;

    public Button RayUndoButton => _rayUndoButton;

    public Button RayFinishButton => _rayFinishButton;

    public ComboBox XLineModeSelector => _xlineModeSelector;

    public Button XLineButton => _xlineButton;

    public Button XLineUndoButton => _xlineUndoButton;

    public Button XLineFinishButton => _xlineFinishButton;

    public Button PointButton => _pointButton;

    public Button PolylineButton => _polylineButton;

    public Button PolylineUndoButton => _polylineUndoButton;

    public Button PolylineLineModeButton => _polylineLineModeButton;

    public Button PolylineArcModeButton => _polylineArcModeButton;

    public ComboBox PolylineArcConstructionSelector =>
        _polylineArcConstructionSelector;

    public Button PolylineArcConstructionButton =>
        _polylineArcConstructionButton;

    public Button PolylineWidthButton => _polylineWidthButton;

    public Button PolylineHalfwidthButton => _polylineHalfwidthButton;

    public Button PolylineLengthButton => _polylineLengthButton;

    public Button PolylineCloseButton => _polylineCloseButton;

    public Button PolylineFinishButton => _polylineFinishButton;

    public Button CircleButton => _circleButton;

    public Button CircleDiameterButton => _circleDiameterButton;

    public Button CircleTwoPointButton => _circleTwoPointButton;

    public Button CircleThreePointButton => _circleThreePointButton;

    public ComboBox ArcModeSelector => _arcModeSelector;

    public Button ArcButton => _arcButton;

    public ComboBox EllipseModeSelector => _ellipseModeSelector;

    public ComboBox EllipseArcInputSelector => _ellipseArcInputSelector;

    public Button EllipseButton => _ellipseButton;

    public TextBox PolygonSideCountInput => _polygonSideCountInput;

    public ComboBox PolygonModeSelector => _polygonModeSelector;

    public Button PolygonButton => _polygonButton;

    public ComboBox RectangleConstructionSelector =>
        _rectangleConstructionSelector;

    public ComboBox RectangleAreaDimensionSelector =>
        _rectangleAreaDimensionSelector;

    public TextBox RectangleValuesInput => _rectangleValuesInput;

    public ComboBox RectangleCornerSelector => _rectangleCornerSelector;

    public TextBox RectangleCornerValuesInput =>
        _rectangleCornerValuesInput;

    public TextBox RectangleRotationInput => _rectangleRotationInput;

    public Button RectangleButton => _rectangleButton;

    public ComboBox SelectionAttributeSelector =>
        _selectionAttributeSelector;

    public TextBox SelectionAttributeValueInput =>
        _selectionAttributeValueInput;

    public TextBox SelectionAttributePromptInput =>
        _selectionAttributePromptInput;

    public TextBox SelectionAttributeTagInput =>
        _selectionAttributeTagInput;

    public CheckBox SelectionAttributeInvisibleCheckBox =>
        _selectionAttributeInvisibleCheckBox;

    public CheckBox SelectionAttributeVerifyCheckBox =>
        _selectionAttributeVerifyCheckBox;

    public CheckBox SelectionAttributePresetCheckBox =>
        _selectionAttributePresetCheckBox;

    public CheckBox SelectionAttributePositionLockedCheckBox =>
        _selectionAttributePositionLockedCheckBox;

    public CheckBox SelectionAttributeConstantCheckBox =>
        _selectionAttributeConstantCheckBox;

    public ComboBox LayerStateSelector => _layerStateSelector;

    public ComboBox LayerVisibilitySelector => _layerVisibilitySelector;

    public ComboBox LayerPlotSelector => _layerPlotSelector;

    public ComboBox LayerFreezeSelector => _layerFreezeSelector;

    public ComboBox LayerLockSelector => _layerLockSelector;

    public TextBox LayerColorInput => _layerColorInput;

    public ComboBox LayerLineWeightSelector => _layerLineWeightSelector;

    public ComboBox LayerLineTypeSelector => _layerLineTypeSelector;

    public TextBox LayerNameInput => _layerNameInput;

    public ComboBox LayerMergeTargetSelector => _layerMergeTargetSelector;

    public TextBlock LayerMergeSourceCountText => _layerMergeSourceCountText;

    /// <summary>
    /// Ordered fully-qualified desktop support directories probed after the
    /// opened drawing's directory. Browser hosts should register bundled SHX
    /// bytes through <see cref="ShxFonts"/> instead.
    /// </summary>
    public IList<string> ShxSupportDirectories => _shxSupportDirectories;

    public CadSampleView()
        : this(null)
    {
    }

    public CadSampleView(CadShxFontCatalog? shxFonts)
    {
        _canvas = new CadSampleCanvas(shxFonts);
        _viewport3D = new Viewport3D
        {
            EnableRetainedSceneCache = true,
            Visibility = Visibility.Collapsed,
            RenderMode = RenderMode3D.Solid,
            ShadingMode = ShadingMode3D.Flat,
            LightDirection = new System.Numerics.Vector3(0.25f, -0.5f, -1.0f),
            AmbientIntensity = 0.25f,
        };
        _viewport3D.ViewportClicked += OnMeshViewportClicked;
        _viewport3D.SubobjectCycleRequested += OnMeshSubobjectCycleRequested;
        _viewport3D.SelectionDragStarting += OnMeshSelectionDragStarting;
        _viewport3D.RegionSelectionCompleted += OnMeshRegionSelectionCompleted;
        _printPreview = new CadPrintPreviewCanvas
        {
            Visibility = Visibility.Collapsed,
        };
        TtfFont font = InterFontFamily.Regular;
        RowDefinitions.Add(new GridLength(532, GridUnitType.Absolute));
        RowDefinitions.Add(GridLength.Star(1));
        RowDefinitions.Add(new GridLength(30, GridUnitType.Absolute));

        var toolbar = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6),
        };
        var toolbarRows = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var editActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var transformActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var draftingGridActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var selectionPropertyActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var selectionEntityActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var selectionStyleActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var selectionAttributeActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var layerStateActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var layerStyleActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var layerLifecycleActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var layerMergeActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var printActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var pageSetupCreateActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var pageSetupFieldActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        toolbarRows.AddChild(actions);
        toolbarRows.AddChild(editActions);
        toolbarRows.AddChild(transformActions);
        toolbarRows.AddChild(draftingGridActions);
        toolbarRows.AddChild(selectionPropertyActions);
        toolbarRows.AddChild(selectionEntityActions);
        toolbarRows.AddChild(selectionAttributeActions);
        toolbarRows.AddChild(selectionStyleActions);
        toolbarRows.AddChild(layerStateActions);
        toolbarRows.AddChild(layerStyleActions);
        toolbarRows.AddChild(layerLifecycleActions);
        toolbarRows.AddChild(layerMergeActions);
        toolbarRows.AddChild(printActions);
        toolbarRows.AddChild(pageSetupCreateActions);
        toolbarRows.AddChild(pageSetupFieldActions);
        toolbar.Child = toolbarRows;

        _openButton = CreateButton("Open DXF/DWG", font, 132);
        _loadLineTypesButton = CreateButton("Load LIN", font, 88);
        _reloadLineTypesButton = CreateButton("Reload LIN", font, 96);
        _importPageSetupsButton = CreateButton("Import setups", font, 112);
        _importReplacePageSetupsButton = CreateButton("Import / replace", font, 128);
        _saveButton = CreateButton("Save As", font, 92);
        _fitButton = CreateButton("Fit", font, 68);
        _viewModeButton = CreateButton("3D surfaces", font, 104);
        _viewModeText = (TextBlock)_viewModeButton.Content!;
        _meshPickTargetSelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 104,
            HeightConstraint = 34,
            Margin = new Thickness(0, 0, 8, 0),
        };
        foreach (float height in new[] { 0.0f, 3.0f, 5.0f, 9.0f, 15.0f })
        {
            _meshPickTargetSelector.Items.Add(new ComboBoxItem(
                $"Pickbox {height:0}")
            {
                Tag = height,
            });
        }
        _meshPickTargetSelector.SelectedIndex = 1;
        _meshRegionSelectionSelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 112,
            HeightConstraint = 34,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _meshRegionSelectionSelector.Items.Add(new ComboBoxItem(
            "Region: Box")
        {
            Tag = false,
        });
        _meshRegionSelectionSelector.Items.Add(new ComboBoxItem(
            "Region: Lasso")
        {
            Tag = true,
        });
        _meshRegionSelectionSelector.SelectedIndex = 0;
        _meshSubobjectSelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 128,
            HeightConstraint = 34,
            Margin = new Thickness(0, 0, 8, 0),
        };
        AddMeshSubobjectChoice("Subobject: Off", CadMesh3DSubobjectFilter.None);
        AddMeshSubobjectChoice("Subobject: Vertex", CadMesh3DSubobjectFilter.Vertex);
        AddMeshSubobjectChoice("Subobject: Edge", CadMesh3DSubobjectFilter.Edge);
        AddMeshSubobjectChoice("Subobject: Face", CadMesh3DSubobjectFilter.Face);
        _meshSubobjectSelector.SelectedIndex = 0;
        _attributeDisplaySelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 150,
            HeightConstraint = 34,
            Margin = new Thickness(0, 0, 8, 0),
        };
        AddAttributeDisplayChoice(
            "Attributes: Normal",
            AttributeVisibilityMode.Normal);
        AddAttributeDisplayChoice(
            "Attributes: On",
            AttributeVisibilityMode.All);
        AddAttributeDisplayChoice(
            "Attributes: Off",
            AttributeVisibilityMode.None);
        _printPreviewButton = CreateButton("Print preview", font, 112);
        _printPreviewText = (TextBlock)_printPreviewButton.Content!;
        _exportPdfButton = CreateButton("Export PDF", font, 96, 30);
        _exportPngButton = CreateButton("Export PNG", font, 96, 30);
        _clearSelectionButton = CreateButton("Clear selection", font, 112);
        _openButton.Margin = new Thickness(0, 0, 8, 0);
        _loadLineTypesButton.Margin = new Thickness(0, 0, 8, 0);
        _reloadLineTypesButton.Margin = new Thickness(0, 0, 8, 0);
        _importPageSetupsButton.Margin = new Thickness(0, 0, 8, 0);
        _importReplacePageSetupsButton.Margin = new Thickness(0, 0, 8, 0);
        _saveButton.Margin = new Thickness(0, 0, 8, 0);
        _fitButton.Margin = new Thickness(0, 0, 8, 0);
        _viewModeButton.Margin = new Thickness(0, 0, 8, 0);
        actions.AddChild(_openButton);
        actions.AddChild(_loadLineTypesButton);
        actions.AddChild(_reloadLineTypesButton);
        actions.AddChild(_importPageSetupsButton);
        actions.AddChild(_importReplacePageSetupsButton);
        actions.AddChild(_saveButton);
        actions.AddChild(_fitButton);
        actions.AddChild(_viewModeButton);
        actions.AddChild(_meshPickTargetSelector);
        actions.AddChild(_meshRegionSelectionSelector);
        actions.AddChild(_meshSubobjectSelector);
        actions.AddChild(_attributeDisplaySelector);
        actions.AddChild(_clearSelectionButton);

        _undoButton = CreateButton("Undo", font, 68, 30);
        _redoButton = CreateButton("Redo", font, 68, 30);
        _deleteButton = CreateButton("Delete", font, 76, 30);
        _lineButton = CreateButton("Line", font, 64, 30);
        _lineUndoButton = CreateButton("Line U", font, 68, 30);
        _lineCloseButton = CreateButton("Close", font, 68, 30);
        _lineFinishButton = CreateButton("Finish", font, 68, 30);
        _rayButton = CreateButton("Ray", font, 64, 30);
        _rayUndoButton = CreateButton("Ray U", font, 68, 30);
        _rayFinishButton = CreateButton("Ray finish", font, 84, 30);
        _xlineModeSelector = new ComboBox
        {
            WidthConstraint = 142,
            HeightConstraint = 30,
            Font = font,
            FontSize = 11,
            Margin = new Thickness(0, 0, 4, 0),
        };
        foreach ((CadXLineAuthoringMode mode, string label) in new[]
        {
            (CadXLineAuthoringMode.TwoPoint, "XLine 2P"),
            (CadXLineAuthoringMode.Horizontal, "XLine Horizontal"),
            (CadXLineAuthoringMode.Vertical, "XLine Vertical"),
            (CadXLineAuthoringMode.Angle, "XLine Angle"),
            (CadXLineAuthoringMode.Bisect, "XLine Bisect"),
            (CadXLineAuthoringMode.Offset, "XLine Offset"),
        })
        {
            _xlineModeSelector.Items.Add(new ComboBoxItem(label)
            {
                Tag = mode,
            });
        }
        _xlineModeSelector.SelectedIndex = 0;
        _xlineButton = CreateButton("XLine", font, 68, 30);
        _xlineUndoButton = CreateButton("XLine U", font, 76, 30);
        _xlineFinishButton = CreateButton("XLine finish", font, 92, 30);
        _pointButton = CreateButton("Point", font, 68, 30);
        _polylineButton = CreateButton("PLine", font, 68, 30);
        _polylineUndoButton = CreateButton("PLine U", font, 72, 30);
        _polylineLineModeButton = CreateButton("PLine line", font, 82, 30);
        _polylineArcModeButton = CreateButton("PLine arc", font, 82, 30);
        _polylineArcConstructionSelector = new ComboBox
        {
            WidthConstraint = 132,
            HeightConstraint = 30,
            Font = font,
            FontSize = 11,
            Margin = new Thickness(0, 0, 4, 0),
        };
        foreach ((CadPolylineArcConstruction construction, string label) in new[]
        {
            (CadPolylineArcConstruction.IncludedAngle, "PLine Angle"),
            (CadPolylineArcConstruction.Center, "PLine Center"),
            (CadPolylineArcConstruction.Direction, "PLine Direction"),
            (CadPolylineArcConstruction.Radius, "PLine Radius"),
            (CadPolylineArcConstruction.ThreePoint, "PLine Second pt"),
        })
        {
            _polylineArcConstructionSelector.Items.Add(new ComboBoxItem(label)
            {
                Tag = construction,
            });
        }
        _polylineArcConstructionSelector.SelectedIndex = 0;
        _polylineArcConstructionButton =
            CreateButton("PLine option", font, 96, 30);
        _polylineWidthButton = CreateButton("PLine width", font, 92, 30);
        _polylineHalfwidthButton = CreateButton("PLine half", font, 88, 30);
        _polylineLengthButton = CreateButton("PLine length", font, 96, 30);
        _polylineCloseButton = CreateButton("PLine close", font, 92, 30);
        _polylineFinishButton = CreateButton("PLine finish", font, 92, 30);
        _circleButton = CreateButton("Circle", font, 72, 30);
        _circleDiameterButton = CreateButton("Circle D", font, 78, 30);
        _circleTwoPointButton = CreateButton("Circle 2P", font, 84, 30);
        _circleThreePointButton = CreateButton("Circle 3P", font, 84, 30);
        _arcModeSelector = new ComboBox
        {
            WidthConstraint = 158,
            HeightConstraint = 30,
            Font = font,
            FontSize = 11,
            Margin = new Thickness(0, 0, 4, 0),
        };
        foreach ((CadArcAuthoringMode mode, string label) in new[]
        {
            (CadArcAuthoringMode.ThreePoint, "Arc 3P"),
            (CadArcAuthoringMode.CenterStartEnd, "Arc Center/Start/End"),
            (CadArcAuthoringMode.CenterStartAngle, "Arc Center/Start/Angle"),
            (CadArcAuthoringMode.CenterStartChord, "Arc Center/Start/Chord"),
            (CadArcAuthoringMode.StartCenterEnd, "Arc Start/Center/End"),
            (CadArcAuthoringMode.StartCenterAngle, "Arc Start/Center/Angle"),
            (CadArcAuthoringMode.StartCenterChord, "Arc Start/Center/Chord"),
            (CadArcAuthoringMode.StartEndAngle, "Arc Start/End/Angle"),
            (CadArcAuthoringMode.StartEndDirection, "Arc Start/End/Direction"),
            (CadArcAuthoringMode.StartEndRadius, "Arc Start/End/Radius"),
        })
        {
            _arcModeSelector.Items.Add(new ComboBoxItem(label)
            {
                Tag = mode,
            });
        }
        _arcModeSelector.SelectedIndex = 0;
        _arcButton = CreateButton("Arc", font, 64, 30);
        _ellipseModeSelector = new ComboBox
        {
            WidthConstraint = 166,
            HeightConstraint = 30,
            Font = font,
            FontSize = 11,
            Margin = new Thickness(0, 0, 4, 0),
        };
        foreach ((CadEllipseAuthoringMode mode, string label) in new[]
        {
            (CadEllipseAuthoringMode.AxisEndpointsDistance, "Ellipse Axis/Distance"),
            (CadEllipseAuthoringMode.AxisEndpointsRotation, "Ellipse Axis/Rotation"),
            (CadEllipseAuthoringMode.CenterDistance, "Ellipse Center/Distance"),
            (CadEllipseAuthoringMode.CenterRotation, "Ellipse Center/Rotation"),
            (CadEllipseAuthoringMode.IsocircleRadius, "Isocircle Radius"),
            (CadEllipseAuthoringMode.IsocircleDiameter, "Isocircle Diameter"),
        })
        {
            _ellipseModeSelector.Items.Add(new ComboBoxItem(label)
            {
                Tag = mode,
            });
        }
        _ellipseModeSelector.SelectedIndex = 0;
        _ellipseArcInputSelector = new ComboBox
        {
            WidthConstraint = 150,
            HeightConstraint = 30,
            Font = font,
            FontSize = 11,
            Margin = new Thickness(0, 0, 4, 0),
        };
        foreach ((CadEllipseArcInputMode mode, string label) in new[]
        {
            (CadEllipseArcInputMode.Full, "Ellipse Full"),
            (CadEllipseArcInputMode.Angle, "Ellipse Arc Angle"),
            (CadEllipseArcInputMode.Parameter, "Ellipse Arc Parameter"),
            (CadEllipseArcInputMode.IncludedAngle, "Ellipse Arc Included"),
        })
        {
            _ellipseArcInputSelector.Items.Add(new ComboBoxItem(label)
            {
                Tag = mode,
            });
        }
        _ellipseArcInputSelector.SelectedIndex = 0;
        _ellipseButton = CreateButton("Ellipse", font, 76, 30);
        _polygonSideCountInput = new TextBox
        {
            Text = "4",
            Font = font,
            WidthConstraint = 52,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 4, 0),
        };
        _polygonModeSelector = new ComboBox
        {
            WidthConstraint = 138,
            HeightConstraint = 30,
            Font = font,
            FontSize = 11,
            Margin = new Thickness(0, 0, 4, 0),
        };
        foreach ((CadPolygonAuthoringMode mode, string label) in new[]
        {
            (CadPolygonAuthoringMode.Inscribed, "Polygon Inscribed"),
            (CadPolygonAuthoringMode.Circumscribed, "Polygon Circumscribed"),
            (CadPolygonAuthoringMode.Edge, "Polygon Edge"),
        })
        {
            _polygonModeSelector.Items.Add(new ComboBoxItem(label)
            {
                Tag = mode,
            });
        }
        _polygonModeSelector.SelectedIndex = 0;
        _polygonButton = CreateButton("Polygon", font, 78, 30);
        _rectangleConstructionSelector = new ComboBox
        {
            WidthConstraint = 142,
            HeightConstraint = 30,
            Font = font,
            FontSize = 11,
            Margin = new Thickness(0, 0, 4, 0),
        };
        foreach ((CadRectangleConstructionMode mode, string label) in new[]
        {
            (CadRectangleConstructionMode.DiagonalCorners, "Rect 2 corners"),
            (CadRectangleConstructionMode.Dimensions, "Rect Dimensions"),
            (CadRectangleConstructionMode.Area, "Rect Area"),
        })
        {
            _rectangleConstructionSelector.Items.Add(new ComboBoxItem(label)
            {
                Tag = mode,
            });
        }
        _rectangleConstructionSelector.SelectedIndex = 0;
        _rectangleAreaDimensionSelector = new ComboBox
        {
            WidthConstraint = 106,
            HeightConstraint = 30,
            Font = font,
            FontSize = 11,
            Margin = new Thickness(0, 0, 4, 0),
        };
        foreach ((CadRectangleKnownDimension dimension, string label) in new[]
        {
            (CadRectangleKnownDimension.Length, "Area + length"),
            (CadRectangleKnownDimension.Width, "Area + width"),
        })
        {
            _rectangleAreaDimensionSelector.Items.Add(new ComboBoxItem(label)
            {
                Tag = dimension,
            });
        }
        _rectangleAreaDimensionSelector.SelectedIndex = 0;
        _rectangleValuesInput = new TextBox
        {
            Text = "10,6",
            Font = font,
            WidthConstraint = 76,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 4, 0),
        };
        _rectangleCornerSelector = new ComboBox
        {
            WidthConstraint = 104,
            HeightConstraint = 30,
            Font = font,
            FontSize = 11,
            Margin = new Thickness(0, 0, 4, 0),
        };
        foreach ((CadRectangleCornerMode mode, string label) in new[]
        {
            (CadRectangleCornerMode.Sharp, "Rect Sharp"),
            (CadRectangleCornerMode.Chamfer, "Rect Chamfer"),
            (CadRectangleCornerMode.Fillet, "Rect Fillet"),
        })
        {
            _rectangleCornerSelector.Items.Add(new ComboBoxItem(label)
            {
                Tag = mode,
            });
        }
        _rectangleCornerSelector.SelectedIndex = 0;
        _rectangleCornerValuesInput = new TextBox
        {
            Text = "0",
            Font = font,
            WidthConstraint = 66,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 4, 0),
        };
        _rectangleRotationInput = new TextBox
        {
            Text = "0",
            Font = font,
            WidthConstraint = 54,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 4, 0),
        };
        _rectangleButton = CreateButton("Rectang", font, 78, 30);
        Button sendToBack = CreateButton("To back", font, 76, 30);
        Button bringToFront = CreateButton("To front", font, 76, 30);
        Button bringAbove = CreateButton("Above…", font, 82, 30);
        Button sendUnder = CreateButton("Under…", font, 82, 30);
        _undoButton.Margin = new Thickness(0, 0, 8, 0);
        _redoButton.Margin = new Thickness(0, 0, 8, 0);
        _deleteButton.Margin = new Thickness(0, 0, 8, 0);
        _lineButton.Margin = new Thickness(0, 0, 4, 0);
        _lineUndoButton.Margin = new Thickness(0, 0, 4, 0);
        _lineCloseButton.Margin = new Thickness(0, 0, 4, 0);
        _lineFinishButton.Margin = new Thickness(0, 0, 12, 0);
        _rayButton.Margin = new Thickness(0, 0, 4, 0);
        _rayUndoButton.Margin = new Thickness(0, 0, 4, 0);
        _rayFinishButton.Margin = new Thickness(0, 0, 12, 0);
        _xlineButton.Margin = new Thickness(0, 0, 4, 0);
        _xlineUndoButton.Margin = new Thickness(0, 0, 4, 0);
        _xlineFinishButton.Margin = new Thickness(0, 0, 12, 0);
        _pointButton.Margin = new Thickness(0, 0, 12, 0);
        _polylineButton.Margin = new Thickness(0, 0, 4, 0);
        _polylineUndoButton.Margin = new Thickness(0, 0, 4, 0);
        _polylineLineModeButton.Margin = new Thickness(0, 0, 4, 0);
        _polylineArcModeButton.Margin = new Thickness(0, 0, 4, 0);
        _polylineArcConstructionButton.Margin = new Thickness(0, 0, 4, 0);
        _polylineWidthButton.Margin = new Thickness(0, 0, 4, 0);
        _polylineHalfwidthButton.Margin = new Thickness(0, 0, 4, 0);
        _polylineLengthButton.Margin = new Thickness(0, 0, 4, 0);
        _polylineCloseButton.Margin = new Thickness(0, 0, 4, 0);
        _polylineFinishButton.Margin = new Thickness(0, 0, 12, 0);
        _circleButton.Margin = new Thickness(0, 0, 4, 0);
        _circleDiameterButton.Margin = new Thickness(0, 0, 4, 0);
        _circleTwoPointButton.Margin = new Thickness(0, 0, 4, 0);
        _circleThreePointButton.Margin = new Thickness(0, 0, 4, 0);
        _arcButton.Margin = new Thickness(0, 0, 12, 0);
        _ellipseButton.Margin = new Thickness(0, 0, 12, 0);
        _polygonButton.Margin = new Thickness(0, 0, 12, 0);
        _rectangleButton.Margin = new Thickness(0, 0, 12, 0);
        sendToBack.Margin = new Thickness(0, 0, 4, 0);
        bringToFront.Margin = new Thickness(0, 0, 4, 0);
        bringAbove.Margin = new Thickness(0, 0, 4, 0);
        sendUnder.Margin = new Thickness(0, 0, 12, 0);
        _drawOrderButtons = [sendToBack, bringToFront, bringAbove, sendUnder];
        editActions.AddChild(_undoButton);
        editActions.AddChild(_redoButton);
        editActions.AddChild(_deleteButton);
        editActions.AddChild(_lineButton);
        editActions.AddChild(_lineUndoButton);
        editActions.AddChild(_lineCloseButton);
        editActions.AddChild(_lineFinishButton);
        editActions.AddChild(_rayButton);
        editActions.AddChild(_rayUndoButton);
        editActions.AddChild(_rayFinishButton);
        editActions.AddChild(_xlineModeSelector);
        editActions.AddChild(_xlineButton);
        editActions.AddChild(_xlineUndoButton);
        editActions.AddChild(_xlineFinishButton);
        editActions.AddChild(_pointButton);
        editActions.AddChild(_polylineButton);
        editActions.AddChild(_polylineUndoButton);
        editActions.AddChild(_polylineLineModeButton);
        editActions.AddChild(_polylineArcModeButton);
        editActions.AddChild(_polylineArcConstructionSelector);
        editActions.AddChild(_polylineArcConstructionButton);
        editActions.AddChild(_polylineWidthButton);
        editActions.AddChild(_polylineHalfwidthButton);
        editActions.AddChild(_polylineLengthButton);
        editActions.AddChild(_polylineCloseButton);
        editActions.AddChild(_polylineFinishButton);
        editActions.AddChild(_circleButton);
        editActions.AddChild(_circleDiameterButton);
        editActions.AddChild(_circleTwoPointButton);
        editActions.AddChild(_circleThreePointButton);
        editActions.AddChild(_arcModeSelector);
        editActions.AddChild(_arcButton);
        editActions.AddChild(_ellipseModeSelector);
        editActions.AddChild(_ellipseArcInputSelector);
        editActions.AddChild(_ellipseButton);
        editActions.AddChild(_polygonSideCountInput);
        editActions.AddChild(_polygonModeSelector);
        editActions.AddChild(_polygonButton);
        editActions.AddChild(_rectangleConstructionSelector);
        editActions.AddChild(_rectangleAreaDimensionSelector);
        editActions.AddChild(_rectangleValuesInput);
        editActions.AddChild(_rectangleCornerSelector);
        editActions.AddChild(_rectangleCornerValuesInput);
        editActions.AddChild(_rectangleRotationInput);
        editActions.AddChild(_rectangleButton);
        editActions.AddChild(sendToBack);
        editActions.AddChild(bringToFront);
        editActions.AddChild(bringAbove);
        editActions.AddChild(sendUnder);
        editActions.AddChild(new TextBlock
        {
            Text = "Move step (WCS)",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _moveStepInput = new TextBox
        {
            Text = "1",
            Font = font,
            WidthConstraint = 76,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        editActions.AddChild(_moveStepInput);
        Button moveNegativeX = CreateButton("−X", font, 48, 30);
        Button movePositiveX = CreateButton("+X", font, 48, 30);
        Button moveNegativeY = CreateButton("−Y", font, 48, 30);
        Button movePositiveY = CreateButton("+Y", font, 48, 30);
        _moveByPointsButton = CreateButton("Move points…", font, 104, 30);
        moveNegativeX.Margin = new Thickness(0, 0, 4, 0);
        movePositiveX.Margin = new Thickness(0, 0, 8, 0);
        moveNegativeY.Margin = new Thickness(0, 0, 4, 0);
        movePositiveY.Margin = new Thickness(0, 0, 8, 0);
        _moveButtons = [
            moveNegativeX,
            movePositiveX,
            moveNegativeY,
            movePositiveY,
            _moveByPointsButton,
        ];
        foreach (Button moveButton in _moveButtons)
        {
            editActions.AddChild(moveButton);
        }

        transformActions.AddChild(new TextBlock
        {
            Text = "Rotate (degrees, selection center)",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _rotationStepInput = new TextBox
        {
            Text = "15",
            Font = font,
            WidthConstraint = 76,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        transformActions.AddChild(_rotationStepInput);
        Button rotateCounterclockwise = CreateButton("↺", font, 48, 30);
        Button rotateClockwise = CreateButton("↻", font, 48, 30);
        rotateCounterclockwise.Margin = new Thickness(0, 0, 4, 0);
        rotateClockwise.Margin = new Thickness(0, 0, 12, 0);
        _rotateButtons = [rotateCounterclockwise, rotateClockwise];
        transformActions.AddChild(rotateCounterclockwise);
        transformActions.AddChild(rotateClockwise);
        transformActions.AddChild(new TextBlock
        {
            Text = "Scale factor (selection center)",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _scaleFactorInput = new TextBox
        {
            Text = "2",
            Font = font,
            WidthConstraint = 76,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        transformActions.AddChild(_scaleFactorInput);
        Button scaleUp = CreateButton("×", font, 48, 30);
        Button scaleDown = CreateButton("÷", font, 48, 30);
        scaleUp.Margin = new Thickness(0, 0, 4, 0);
        _scaleButtons = [scaleUp, scaleDown];
        transformActions.AddChild(scaleUp);
        transformActions.AddChild(scaleDown);
        transformActions.AddChild(new TextBlock
        {
            Text = "Mesh smoothing / crease",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(12, 6, 8, 0),
        });
        _meshSmoothMoreButton = CreateButton("Smooth +", font, 78, 30);
        _meshSmoothLessButton = CreateButton("Smooth −", font, 78, 30);
        _meshCreaseInput = new TextBox
        {
            Text = "-1",
            Font = font,
            WidthConstraint = 58,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 4, 0),
        };
        _setMeshCreaseButton = CreateButton("Set crease", font, 86, 30);
        _removeMeshCreaseButton = CreateButton("Uncrease", font, 82, 30);
        _meshSmoothMoreButton.Margin = new Thickness(0, 0, 4, 0);
        _meshSmoothLessButton.Margin = new Thickness(0, 0, 8, 0);
        _setMeshCreaseButton.Margin = new Thickness(0, 0, 4, 0);
        transformActions.AddChild(_meshSmoothMoreButton);
        transformActions.AddChild(_meshSmoothLessButton);
        transformActions.AddChild(_meshCreaseInput);
        transformActions.AddChild(_setMeshCreaseButton);
        transformActions.AddChild(_removeMeshCreaseButton);
        transformActions.AddChild(new TextBlock
        {
            Text = "Copy items (including source)",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(12, 6, 8, 0),
        });
        _copyArrayItemsInput = new TextBox
        {
            Text = "2",
            Font = font,
            WidthConstraint = 56,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        transformActions.AddChild(_copyArrayItemsInput);
        _copyArrayModeSelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 96,
            HeightConstraint = 30,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _copyArrayModeSelector.Items.Add(new ComboBoxItem("Step")
        {
            Tag = CadLinearCopyMode.Incremental,
        });
        _copyArrayModeSelector.Items.Add(new ComboBoxItem("Fit")
        {
            Tag = CadLinearCopyMode.Fit,
        });
        _copyArrayModeSelector.SelectedIndex = 0;
        transformActions.AddChild(_copyArrayModeSelector);
        Button copyNegativeX = CreateButton("Copy −X", font, 72, 30);
        Button copyPositiveX = CreateButton("Copy +X", font, 72, 30);
        Button copyNegativeY = CreateButton("Copy −Y", font, 72, 30);
        Button copyPositiveY = CreateButton("Copy +Y", font, 72, 30);
        _copyByPointsButton = CreateButton("Copy points…", font, 104, 30);
        copyNegativeX.Margin = new Thickness(0, 0, 4, 0);
        copyPositiveX.Margin = new Thickness(0, 0, 8, 0);
        copyNegativeY.Margin = new Thickness(0, 0, 4, 0);
        copyPositiveY.Margin = new Thickness(0, 0, 8, 0);
        _copyButtons = [
            copyNegativeX,
            copyPositiveX,
            copyNegativeY,
            copyPositiveY,
            _copyByPointsButton,
        ];
        foreach (Button copyButton in _copyButtons)
        {
            transformActions.AddChild(copyButton);
        }
        _objectSnapSelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 136,
            HeightConstraint = 30,
            Margin = new Thickness(12, 0, 8, 0),
        };
        _objectSnapSelector.Items.Add(new ComboBoxItem("Snap: Off")
        {
            Tag = CadObjectSnapModes.None,
        });
        _objectSnapSelector.Items.Add(new ComboBoxItem("Snap: End")
        {
            Tag = CadObjectSnapModes.Endpoint,
        });
        _objectSnapSelector.Items.Add(new ComboBoxItem("Snap: Mid")
        {
            Tag = CadObjectSnapModes.Midpoint,
        });
        _objectSnapSelector.Items.Add(new ComboBoxItem("Snap: Center")
        {
            Tag = CadObjectSnapModes.Center,
        });
        _objectSnapSelector.Items.Add(new ComboBoxItem("Snap: Node")
        {
            Tag = CadObjectSnapModes.Node,
        });
        _objectSnapSelector.Items.Add(new ComboBoxItem("Snap: Intersection")
        {
            Tag = CadObjectSnapModes.Intersection,
        });
        _objectSnapSelector.Items.Add(new ComboBoxItem("Snap: Quadrant")
        {
            Tag = CadObjectSnapModes.Quadrant,
        });
        _objectSnapSelector.Items.Add(new ComboBoxItem("Snap: Perpendicular")
        {
            Tag = CadObjectSnapModes.Perpendicular,
        });
        _objectSnapSelector.Items.Add(new ComboBoxItem("Snap: Tangent")
        {
            Tag = CadObjectSnapModes.Tangent,
        });
        _objectSnapSelector.Items.Add(new ComboBoxItem("Snap: Nearest")
        {
            Tag = CadObjectSnapModes.Nearest,
        });
        _objectSnapSelector.Items.Add(new ComboBoxItem("Snap: Standard")
        {
            Tag = CadObjectSnapModes.Standard,
        });
        _objectSnapSelector.SelectedIndex = 10;
        transformActions.AddChild(_objectSnapSelector);
        _planGridSnapCheckBox = CreateAttributeModeCheckBox(
            "Grid snap",
            font);
        _planGridSnapCheckBox.IsChecked = _canvas.IsPlanGridSnapEnabled;
        transformActions.AddChild(_planGridSnapCheckBox);
        _planOrthoCheckBox = CreateAttributeModeCheckBox("Ortho", font);
        _planOrthoCheckBox.IsChecked = _canvas.IsPlanOrthoEnabled;
        transformActions.AddChild(_planOrthoCheckBox);
        _planPolarTrackingCheckBox = CreateAttributeModeCheckBox("Polar", font);
        _planPolarTrackingCheckBox.IsChecked =
            _canvas.IsPlanPolarTrackingEnabled;
        transformActions.AddChild(_planPolarTrackingCheckBox);
        _planPolarTrackingIncrementSelector = new ComboBox
        {
            WidthConstraint = 86,
            HeightConstraint = 30,
            Font = font,
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0),
        };
        foreach (double increment in new[]
        {
            90.0,
            45.0,
            30.0,
            22.5,
            18.0,
            15.0,
            10.0,
            5.0,
        })
        {
            _planPolarTrackingIncrementSelector.Items.Add(
                new ComboBoxItem($"{increment:0.#}°")
                {
                    Tag = increment,
                });
        }
        _planPolarTrackingIncrementSelector.SelectedIndex = 0;
        transformActions.AddChild(_planPolarTrackingIncrementSelector);
        _planPolarRelativeCheckBox = CreateAttributeModeCheckBox(
            "Relative to last segment",
            font);
        _planPolarRelativeCheckBox.IsChecked =
            _canvas.PlanPolarAngleMeasurement ==
                CadPlanPolarAngleMeasurement.RelativeToLastSegment;
        transformActions.AddChild(_planPolarRelativeCheckBox);
        _planPolarAdditionalAnglesCheckBox = CreateAttributeModeCheckBox(
            "Additional angles",
            font);
        _planPolarAdditionalAnglesCheckBox.IsChecked =
            _canvas.UsePlanPolarAdditionalAngles;
        transformActions.AddChild(_planPolarAdditionalAnglesCheckBox);
        transformActions.AddChild(new TextBlock
        {
            Text = "Angles ° (; max 10)",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _planPolarAdditionalAnglesInput = new TextBox
        {
            Text = string.Empty,
            Font = font,
            WidthConstraint = 132,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        transformActions.AddChild(_planPolarAdditionalAnglesInput);
        _planPolarSnapCheckBox = CreateAttributeModeCheckBox(
            "PolarSnap",
            font);
        _planPolarSnapCheckBox.IsChecked =
            _canvas.IsPlanPolarSnapEnabled;
        transformActions.AddChild(_planPolarSnapCheckBox);
        transformActions.AddChild(new TextBlock
        {
            Text = "Polar distance (0 = Snap X)",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _planPolarSnapDistanceInput = new TextBox
        {
            Text = "0",
            Font = font,
            WidthConstraint = 72,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        transformActions.AddChild(_planPolarSnapDistanceInput);
        transformActions.AddChild(new TextBlock
        {
            Text = "Point / displacement",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(12, 6, 8, 0),
        });
        _pointTransformInput = new TextBox
        {
            Text = string.Empty,
            Font = font,
            WidthConstraint = 180,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _acceptPointTransformInputButton = CreateButton(
            "Enter point",
            font,
            92,
            30);
        transformActions.AddChild(_pointTransformInput);
        transformActions.AddChild(_acceptPointTransformInputButton);

        draftingGridActions.AddChild(new TextBlock
        {
            Text = "Drafting grid",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _planGridDisplayCheckBox = CreateAttributeModeCheckBox("Visible", font);
        draftingGridActions.AddChild(_planGridDisplayCheckBox);
        _planGridDotsCheckBox = CreateAttributeModeCheckBox(
            "Dots (GRIDSTYLE)",
            font);
        draftingGridActions.AddChild(_planGridDotsCheckBox);
        _planGridIsometricCheckBox = CreateAttributeModeCheckBox(
            "Isometric",
            font);
        draftingGridActions.AddChild(_planGridIsometricCheckBox);
        _planGridIsoplaneSelector = new ComboBox
        {
            WidthConstraint = 82,
            HeightConstraint = 30,
            Font = font,
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0),
        };
        foreach ((CadPlanIsoplane isoplane, string label) in new[]
        {
            (CadPlanIsoplane.Left, "Left"),
            (CadPlanIsoplane.Top, "Top"),
            (CadPlanIsoplane.Right, "Right"),
        })
        {
            _planGridIsoplaneSelector.Items.Add(new ComboBoxItem(label)
            {
                Tag = isoplane,
            });
        }
        _planGridIsoplaneSelector.SelectedIndex = 0;
        draftingGridActions.AddChild(_planGridIsoplaneSelector);
        draftingGridActions.AddChild(new TextBlock
        {
            Text = "SNAPUNIT X",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(8, 6, 6, 0),
        });
        _planSnapUnitXInput = new TextBox
        {
            Font = font,
            WidthConstraint = 76,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        draftingGridActions.AddChild(_planSnapUnitXInput);
        draftingGridActions.AddChild(new TextBlock
        {
            Text = "Y",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 6, 0),
        });
        _planSnapUnitYInput = new TextBox
        {
            Font = font,
            WidthConstraint = 76,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        draftingGridActions.AddChild(_planSnapUnitYInput);
        draftingGridActions.AddChild(new TextBlock
        {
            Text = "GRIDUNIT X",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(8, 6, 6, 0),
        });
        _planGridUnitXInput = new TextBox
        {
            Font = font,
            WidthConstraint = 76,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        draftingGridActions.AddChild(_planGridUnitXInput);
        draftingGridActions.AddChild(new TextBlock
        {
            Text = "Y",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 6, 0),
        });
        _planGridUnitYInput = new TextBox
        {
            Font = font,
            WidthConstraint = 76,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        draftingGridActions.AddChild(_planGridUnitYInput);
        _planGridAdaptiveCheckBox = CreateAttributeModeCheckBox("Adaptive", font);
        _planGridSubdivisionCheckBox = CreateAttributeModeCheckBox(
            "Subdivide",
            font);
        _planGridBeyondLimitsCheckBox = CreateAttributeModeCheckBox(
            "Beyond limits",
            font);
        draftingGridActions.AddChild(_planGridAdaptiveCheckBox);
        draftingGridActions.AddChild(_planGridSubdivisionCheckBox);
        draftingGridActions.AddChild(_planGridBeyondLimitsCheckBox);
        draftingGridActions.AddChild(new TextBlock
        {
            Text = "GRIDMAJOR",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(8, 6, 6, 0),
        });
        _planGridMajorInput = new TextBox
        {
            Font = font,
            WidthConstraint = 56,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        draftingGridActions.AddChild(_planGridMajorInput);
        _applyPlanGridDisplayButton = CreateButton(
            "Apply grid",
            font,
            88,
            30);
        draftingGridActions.AddChild(_applyPlanGridDisplayButton);

        selectionPropertyActions.AddChild(new TextBlock
        {
            Text = "Selection properties",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        selectionPropertyActions.AddChild(new TextBlock
        {
            Text = "Color",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _selectionColorInput = new TextBox
        {
            Font = font,
            WidthConstraint = 132,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _setSelectionColorButton = CreateButton("Set color", font, 84, 30);
        _setSelectionColorButton.Margin = new Thickness(0, 0, 12, 0);
        selectionPropertyActions.AddChild(_selectionColorInput);
        selectionPropertyActions.AddChild(_setSelectionColorButton);
        selectionPropertyActions.AddChild(new TextBlock
        {
            Text = "Lineweight",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _selectionLineWeightSelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 150,
            HeightConstraint = 30,
            MaxDropDownHeight = 256,
            Margin = new Thickness(0, 0, 8, 0),
        };
        PopulateLineWeightChoices(_selectionLineWeightSelector);
        _setSelectionLineWeightButton = CreateButton(
            "Set lineweight",
            font,
            112,
            30);
        selectionPropertyActions.AddChild(_selectionLineWeightSelector);
        selectionPropertyActions.AddChild(_setSelectionLineWeightButton);
        selectionEntityActions.AddChild(new TextBlock
        {
            Text = "Entity state",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        selectionEntityActions.AddChild(new TextBlock
        {
            Text = "Visibility",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _selectionVisibilitySelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 100,
            HeightConstraint = 30,
            Margin = new Thickness(0, 0, 8, 0),
        };
        PopulateVisibilityChoices(_selectionVisibilitySelector);
        _setSelectionVisibilityButton = CreateButton(
            "Set visibility",
            font,
            104,
            30);
        _setSelectionVisibilityButton.Margin = new Thickness(0, 0, 12, 0);
        selectionEntityActions.AddChild(_selectionVisibilitySelector);
        selectionEntityActions.AddChild(_setSelectionVisibilityButton);
        selectionEntityActions.AddChild(new TextBlock
        {
            Text = "SOLID thickness",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _selectionSolidThicknessInput = new TextBox
        {
            Font = font,
            WidthConstraint = 90,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _setSelectionSolidThicknessButton = CreateButton(
            "Set thickness",
            font,
            104,
            30);
        selectionEntityActions.AddChild(_selectionSolidThicknessInput);
        selectionEntityActions.AddChild(_setSelectionSolidThicknessButton);

        selectionAttributeActions.AddChild(new TextBlock
        {
            Text = "Block attribute",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _selectionAttributeSelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 260,
            HeightConstraint = 30,
            MaxDropDownHeight = 256,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _selectionAttributeSelector.Items.Add(new ComboBoxItem { Text = "—" });
        _selectionAttributeSelector.SelectedIndex = 0;
        _selectionAttributeValueInput = new TextBox
        {
            Font = font,
            WidthConstraint = 320,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _setSelectionAttributeValueButton = CreateButton(
            "Set attribute",
            font,
            108,
            30);
        _selectionAttributePromptInput = new TextBox
        {
            Font = font,
            WidthConstraint = 240,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _setSelectionAttributePromptButton = CreateButton(
            "Set prompt",
            font,
            96,
            30);
        _selectionAttributeTagInput = new TextBox
        {
            Font = font,
            WidthConstraint = 160,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _setSelectionAttributeTagButton = CreateButton(
            "Set tag",
            font,
            80,
            30);
        _selectionAttributeInvisibleCheckBox = CreateAttributeModeCheckBox(
            "Invisible",
            font);
        _selectionAttributeVerifyCheckBox = CreateAttributeModeCheckBox(
            "Verify",
            font);
        _selectionAttributePresetCheckBox = CreateAttributeModeCheckBox(
            "Preset",
            font);
        _selectionAttributePositionLockedCheckBox = CreateAttributeModeCheckBox(
            "Lock position",
            font);
        _selectionAttributeConstantCheckBox = CreateAttributeModeCheckBox(
            "Constant (sync all)",
            font);
        _setSelectionAttributeModesButton = CreateButton(
            "Set modes",
            font,
            92,
            30);
        _setSelectionAttributeConstantButton = CreateButton(
            "Set constant",
            font,
            104,
            30);
        _synchronizeSelectionAttributePropertiesButton = CreateButton(
            "Sync properties",
            font,
            116,
            30);
        selectionAttributeActions.AddChild(_selectionAttributeSelector);
        selectionAttributeActions.AddChild(_selectionAttributeValueInput);
        selectionAttributeActions.AddChild(_setSelectionAttributeValueButton);
        selectionAttributeActions.AddChild(_selectionAttributePromptInput);
        selectionAttributeActions.AddChild(_setSelectionAttributePromptButton);
        selectionAttributeActions.AddChild(_selectionAttributeTagInput);
        selectionAttributeActions.AddChild(_setSelectionAttributeTagButton);
        selectionAttributeActions.AddChild(_selectionAttributeInvisibleCheckBox);
        selectionAttributeActions.AddChild(_selectionAttributeVerifyCheckBox);
        selectionAttributeActions.AddChild(_selectionAttributePresetCheckBox);
        selectionAttributeActions.AddChild(
            _selectionAttributePositionLockedCheckBox);
        selectionAttributeActions.AddChild(_setSelectionAttributeModesButton);
        selectionAttributeActions.AddChild(_selectionAttributeConstantCheckBox);
        selectionAttributeActions.AddChild(_setSelectionAttributeConstantButton);
        selectionAttributeActions.AddChild(
            _synchronizeSelectionAttributePropertiesButton);

        selectionStyleActions.AddChild(new TextBlock
        {
            Text = "Layer",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _selectionLayerSelector = CreatePropertySelector(font, 150);
        _setSelectionLayerButton = CreateButton("Set layer", font, 84, 30);
        _setSelectionLayerButton.Margin = new Thickness(0, 0, 12, 0);
        selectionStyleActions.AddChild(_selectionLayerSelector);
        selectionStyleActions.AddChild(_setSelectionLayerButton);
        selectionStyleActions.AddChild(new TextBlock
        {
            Text = "Linetype",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _selectionLineTypeSelector = CreatePropertySelector(font, 150);
        _setSelectionLineTypeButton = CreateButton(
            "Set linetype",
            font,
            100,
            30);
        _setSelectionLineTypeButton.Margin = new Thickness(0, 0, 12, 0);
        selectionStyleActions.AddChild(_selectionLineTypeSelector);
        selectionStyleActions.AddChild(_setSelectionLineTypeButton);
        selectionStyleActions.AddChild(new TextBlock
        {
            Text = "LT scale",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _selectionLineTypeScaleInput = new TextBox
        {
            Font = font,
            WidthConstraint = 90,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _setSelectionLineTypeScaleButton = CreateButton(
            "Set scale",
            font,
            88,
            30);
        _setSelectionLineTypeScaleButton.Margin = new Thickness(0, 0, 12, 0);
        selectionStyleActions.AddChild(_selectionLineTypeScaleInput);
        selectionStyleActions.AddChild(_setSelectionLineTypeScaleButton);
        selectionStyleActions.AddChild(new TextBlock
        {
            Text = "Transparency",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _selectionTransparencyInput = new TextBox
        {
            Font = font,
            WidthConstraint = 90,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _setSelectionTransparencyButton = CreateButton(
            "Set transparency",
            font,
            128,
            30);
        selectionStyleActions.AddChild(_selectionTransparencyInput);
        selectionStyleActions.AddChild(_setSelectionTransparencyButton);

        layerStateActions.AddChild(new TextBlock
        {
            Text = "Layer state",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _layerStateSelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 130,
            HeightConstraint = 30,
            MaxDropDownHeight = 256,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _layerStateSelector.Items.Add(new ComboBoxItem { Text = "—" });
        _layerStateSelector.SelectedIndex = 0;
        layerStateActions.AddChild(_layerStateSelector);
        layerStateActions.AddChild(new TextBlock
        {
            Text = "Visibility",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _layerVisibilitySelector = CreateBooleanPropertySelector(
            font,
            "On",
            "Off",
            84);
        _setLayerVisibilityButton = CreateButton(
            "Set layer visibility",
            font,
            118,
            30);
        _setLayerVisibilityButton.Margin = new Thickness(0, 0, 8, 0);
        layerStateActions.AddChild(_layerVisibilitySelector);
        layerStateActions.AddChild(_setLayerVisibilityButton);
        layerStateActions.AddChild(new TextBlock
        {
            Text = "Plot",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _layerPlotSelector = CreateBooleanPropertySelector(
            font,
            "Plot",
            "No plot",
            84);
        _setLayerPlotButton = CreateButton(
            "Set layer plot",
            font,
            96,
            30);
        _setLayerPlotButton.Margin = new Thickness(0, 0, 8, 0);
        layerStateActions.AddChild(_layerPlotSelector);
        layerStateActions.AddChild(_setLayerPlotButton);
        layerStateActions.AddChild(new TextBlock
        {
            Text = "Freeze",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _layerFreezeSelector = CreateBooleanPropertySelector(
            font,
            "Frozen",
            "Thawed",
            88);
        _setLayerFreezeButton = CreateButton(
            "Set layer freeze",
            font,
            106,
            30);
        _setLayerFreezeButton.Margin = new Thickness(0, 0, 8, 0);
        layerStateActions.AddChild(_layerFreezeSelector);
        layerStateActions.AddChild(_setLayerFreezeButton);
        layerStateActions.AddChild(new TextBlock
        {
            Text = "Lock",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _layerLockSelector = CreateBooleanPropertySelector(
            font,
            "Locked",
            "Unlocked",
            88);
        _setLayerLockButton = CreateButton(
            "Set layer lock",
            font,
            96,
            30);
        layerStateActions.AddChild(_layerLockSelector);
        layerStateActions.AddChild(_setLayerLockButton);

        layerStyleActions.AddChild(new TextBlock
        {
            Text = "Layer style",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        layerStyleActions.AddChild(new TextBlock
        {
            Text = "Color",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _layerColorInput = new TextBox
        {
            Font = font,
            WidthConstraint = 132,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _setLayerColorButton = CreateButton("Set layer color", font, 112, 30);
        _setLayerColorButton.Margin = new Thickness(0, 0, 12, 0);
        layerStyleActions.AddChild(_layerColorInput);
        layerStyleActions.AddChild(_setLayerColorButton);
        layerStyleActions.AddChild(new TextBlock
        {
            Text = "Lineweight",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _layerLineWeightSelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 150,
            HeightConstraint = 30,
            MaxDropDownHeight = 256,
            Margin = new Thickness(0, 0, 8, 0),
        };
        PopulateLayerLineWeightChoices(_layerLineWeightSelector);
        _setLayerLineWeightButton = CreateButton(
            "Set layer lineweight",
            font,
            132,
            30);
        _setLayerLineWeightButton.Margin = new Thickness(0, 0, 12, 0);
        layerStyleActions.AddChild(_layerLineWeightSelector);
        layerStyleActions.AddChild(_setLayerLineWeightButton);
        layerStyleActions.AddChild(new TextBlock
        {
            Text = "Linetype",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _layerLineTypeSelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 150,
            HeightConstraint = 30,
            MaxDropDownHeight = 256,
            Margin = new Thickness(0, 0, 8, 0),
        };
        PopulateLayerLineTypeChoices(
            _layerLineTypeSelector,
            ReadOnlySpan<string>.Empty);
        _setLayerLineTypeButton = CreateButton(
            "Set layer linetype",
            font,
            132,
            30);
        layerStyleActions.AddChild(_layerLineTypeSelector);
        layerStyleActions.AddChild(_setLayerLineTypeButton);

        layerLifecycleActions.AddChild(new TextBlock
        {
            Text = "Layer name (new / rename)",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _layerNameInput = new TextBox
        {
            Font = font,
            WidthConstraint = 220,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _createLayerButton = CreateButton("New layer", font, 92, 30);
        _createLayerButton.Margin = new Thickness(0, 0, 8, 0);
        _renameLayerButton = CreateButton("Rename layer", font, 108, 30);
        _renameLayerButton.Margin = new Thickness(0, 0, 8, 0);
        _removeLayerButton = CreateButton("Delete unused", font, 112, 30);
        _removeLayerButton.Margin = new Thickness(0, 0, 12, 0);
        layerLifecycleActions.AddChild(_layerNameInput);
        layerLifecycleActions.AddChild(_createLayerButton);
        layerLifecycleActions.AddChild(_renameLayerButton);
        layerLifecycleActions.AddChild(_removeLayerButton);

        layerMergeActions.AddChild(new TextBlock
        {
            Text = "Layer merge sources",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _queueLayerMergeSourceButton = CreateButton(
            "Queue selected",
            font,
            112,
            30);
        _queueLayerMergeSourceButton.Margin = new Thickness(0, 0, 8, 0);
        _layerMergeSourceCountText = new TextBlock
        {
            Text = "Sources: 0",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            WidthConstraint = 76,
            Margin = new Thickness(0, 6, 8, 0),
        };
        _clearLayerMergeSourcesButton = CreateButton(
            "Clear sources",
            font,
            104,
            30);
        _clearLayerMergeSourcesButton.Margin = new Thickness(0, 0, 12, 0);
        var mergeTargetLabel = new TextBlock
        {
            Text = "Target",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        };
        _layerMergeTargetSelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 150,
            HeightConstraint = 30,
            MaxDropDownHeight = 256,
            Margin = new Thickness(0, 0, 8, 0),
        };
        PopulateLayerStateChoices(
            _layerMergeTargetSelector,
            ReadOnlySpan<string>.Empty,
            previousName: null);
        _mergeLayerButton = CreateButton("Merge queued", font, 112, 30);
        layerMergeActions.AddChild(_queueLayerMergeSourceButton);
        layerMergeActions.AddChild(_layerMergeSourceCountText);
        layerMergeActions.AddChild(_clearLayerMergeSourcesButton);
        layerMergeActions.AddChild(mergeTargetLabel);
        layerMergeActions.AddChild(_layerMergeTargetSelector);
        layerMergeActions.AddChild(_mergeLayerButton);

        printActions.AddChild(new TextBlock
        {
            Text = "Page setup",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _pageSetupSelector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = 280,
            HeightConstraint = 30,
            MaxDropDownHeight = 256,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _applyPageSetupButton = CreateButton("Apply to Model", font, 120, 30);
        _applyPageSetupButton.Margin = new Thickness(0, 0, 8, 0);
        _printPreviewButton.Margin = new Thickness(0, 0, 8, 0);
        _exportPdfButton.Margin = new Thickness(0, 0, 8, 0);
        _exportPngButton.Margin = new Thickness(0, 0, 8, 0);
        printActions.AddChild(_pageSetupSelector);
        printActions.AddChild(_applyPageSetupButton);
        printActions.AddChild(_printPreviewButton);
        printActions.AddChild(_exportPdfButton);
        printActions.AddChild(_exportPngButton);

        pageSetupCreateActions.AddChild(new TextBlock
        {
            Text = "Setup name (Model / rename)",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _pageSetupNameInput = new TextBox
        {
            Text = "Model output",
            Font = font,
            WidthConstraint = 220,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _createPageSetupButton = CreateButton("Save named setup", font, 132, 30);
        _createPageSetupButton.Margin = new Thickness(0, 0, 8, 0);
        _updatePageSetupButton = CreateButton("Update selected", font, 120, 30);
        _updatePageSetupButton.Margin = new Thickness(0, 0, 8, 0);
        _renamePageSetupButton = CreateButton("Rename selected", font, 116, 30);
        _renamePageSetupButton.Margin = new Thickness(0, 0, 8, 0);
        _deletePageSetupButton = CreateButton("Delete setup", font, 104, 30);
        pageSetupCreateActions.AddChild(_pageSetupNameInput);
        pageSetupCreateActions.AddChild(_createPageSetupButton);
        pageSetupCreateActions.AddChild(_updatePageSetupButton);
        pageSetupCreateActions.AddChild(_renamePageSetupButton);
        pageSetupCreateActions.AddChild(_deletePageSetupButton);

        pageSetupFieldActions.AddChild(new TextBlock
        {
            Text = "Selected setup fields",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 6, 8, 0),
        });
        _pageSetupPaperWidthInput = CreatePageSetupFieldInput(
            "Width mm",
            font,
            76);
        _pageSetupPaperHeightInput = CreatePageSetupFieldInput(
            "Height mm",
            font,
            76);
        _pageSetupRotationSelector = CreatePageSetupFieldSelector(font, 116);
        AddPageSetupFieldChoice(
            _pageSetupRotationSelector,
            "0°",
            CadPageRotation.Degrees0);
        AddPageSetupFieldChoice(
            _pageSetupRotationSelector,
            "90° CCW",
            CadPageRotation.CounterClockwise90);
        AddPageSetupFieldChoice(
            _pageSetupRotationSelector,
            "180°",
            CadPageRotation.Degrees180);
        AddPageSetupFieldChoice(
            _pageSetupRotationSelector,
            "270° CCW",
            CadPageRotation.CounterClockwise270);
        _pageSetupPlotAreaSelector = CreatePageSetupFieldSelector(font, 112);
        foreach (CadPlotAreaKind area in Enum.GetValues<CadPlotAreaKind>())
        {
            if (area != CadPlotAreaKind.Unknown)
            {
                AddPageSetupFieldChoice(
                    _pageSetupPlotAreaSelector,
                    area.ToString(),
                    area);
            }
        }
        _pageSetupCenterCheckBox = CreateAttributeModeCheckBox("Center", font);
        _pageSetupLineweightsCheckBox = CreateAttributeModeCheckBox(
            "Lineweights",
            font);
        _editPageSetupFieldsButton = CreateButton(
            "Apply fields",
            font,
            104,
            30);
        pageSetupFieldActions.AddChild(_pageSetupPaperWidthInput);
        pageSetupFieldActions.AddChild(_pageSetupPaperHeightInput);
        pageSetupFieldActions.AddChild(_pageSetupRotationSelector);
        pageSetupFieldActions.AddChild(_pageSetupPlotAreaSelector);
        pageSetupFieldActions.AddChild(_pageSetupCenterCheckBox);
        pageSetupFieldActions.AddChild(_pageSetupLineweightsCheckBox);
        pageSetupFieldActions.AddChild(_editPageSetupFieldsButton);

        _status = new TextBlock
        {
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Padding = new Thickness(10, 6),
            Text = DescribeCurrentDocument(_currentDocumentName),
        };
        var statusBorder = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _status,
        };

        _contentHost = new Grid();
        _contentHost.AddChild(_canvas);
        _contentHost.AddChild(_viewport3D);
        _meshSubobjectOverlay = new CadMesh3DSubobjectOverlay
        {
            Visibility = Visibility.Collapsed,
        };
        _contentHost.AddChild(_meshSubobjectOverlay);
        _contentHost.AddChild(_printPreview);
        AddChild(toolbar);
        AddChild(_contentHost);
        AddChild(statusBorder);
        SetRow(toolbar, 0);
        SetRow(_contentHost, 1);
        SetRow(statusBorder, 2);

        _openButton.Click += async (_, _) => await OpenAsync();
        _loadLineTypesButton.Click += async (_, _) =>
            await ImportLineTypesAsync(CadLineTypeImportConflictPolicy.Reject);
        _reloadLineTypesButton.Click += async (_, _) =>
            await ImportLineTypesAsync(
                CadLineTypeImportConflictPolicy.ReplaceExisting);
        _importPageSetupsButton.Click += async (_, _) =>
            await ImportPageSetupsAsync(CadPageSetupImportConflictPolicy.Reject);
        _importReplacePageSetupsButton.Click += async (_, _) =>
            await ImportPageSetupsAsync(
                CadPageSetupImportConflictPolicy.ReplaceExisting);
        _saveButton.Click += async (_, _) => await SaveAsAsync();
        _fitButton.Click += (_, _) =>
        {
            if (_is3DView)
            {
                FitMesh3DView();
            }
            else
            {
                _canvas.FitToView();
            }
        };
        _viewModeButton.Click += (_, _) => ToggleViewMode();
        _meshPickTargetSelector.SelectionChanged += (_, _) =>
        {
            if ((_meshPickTargetSelector.SelectedItem as ComboBoxItem)?.Tag is
                float height)
            {
                _meshPickTargetHeight = height;
            }
        };
        _meshRegionSelectionSelector.SelectionChanged += (_, _) =>
        {
            if ((_meshRegionSelectionSelector.SelectedItem as ComboBoxItem)?
                    .Tag is bool useLasso)
            {
                _viewport3D.UseLassoSelection = useLasso;
            }
        };
        _meshSubobjectSelector.SelectionChanged += (_, _) =>
        {
            if ((_meshSubobjectSelector.SelectedItem as ComboBoxItem)?.Tag is
                CadMesh3DSubobjectFilter filter)
            {
                _meshSubobjectFilter = filter;
                ResetMeshSubobjectCycle();
                RefreshMeshSubobjectOverlay();
            }
        };
        _attributeDisplaySelector.SelectionChanged += (_, _) =>
            OnAttributeDisplaySelectionChanged();
        _printPreviewButton.Click += (_, _) => TogglePrintPreview();
        _exportPdfButton.Click += async (_, _) =>
            await ExportSelectedPageAsync(CadPrintOutputFormat.Pdf);
        _exportPngButton.Click += async (_, _) =>
            await ExportSelectedPageAsync(CadPrintOutputFormat.Png);
        _pageSetupSelector.SelectionChanged += (_, _) =>
            OnPageSetupSelectionChanged();
        _applyPageSetupButton.Click += (_, _) =>
            ApplySelectedPageSetupToModel();
        _pageSetupNameInput.TextChanged += (_, _) => UpdateEditControls();
        _pageSetupPaperWidthInput.TextChanged += (_, _) =>
            UpdatePageSetupFieldEditControls();
        _pageSetupPaperHeightInput.TextChanged += (_, _) =>
            UpdatePageSetupFieldEditControls();
        _pageSetupRotationSelector.SelectionChanged += (_, _) =>
            UpdatePageSetupFieldEditControls();
        _pageSetupPlotAreaSelector.SelectionChanged += (_, _) =>
            UpdatePageSetupFieldEditControls();
        _pageSetupCenterCheckBox.CheckedChanged += (_, _) =>
            UpdatePageSetupFieldEditControls();
        _pageSetupLineweightsCheckBox.CheckedChanged += (_, _) =>
            UpdatePageSetupFieldEditControls();
        _selectionColorInput.TextChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _selectionLineWeightSelector.SelectionChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _selectionLayerSelector.SelectionChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _selectionLineTypeSelector.SelectionChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _selectionLineTypeScaleInput.TextChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _selectionTransparencyInput.TextChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _selectionVisibilitySelector.SelectionChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _selectionSolidThicknessInput.TextChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _selectionAttributeSelector.SelectionChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                RefreshSelectedAttributeValue();
                UpdateEditControls();
            }
        };
        _selectionAttributeValueInput.TextChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _selectionAttributePromptInput.TextChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _selectionAttributeTagInput.TextChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _selectionAttributeInvisibleCheckBox.CheckedChanged += (_, _) =>
            UpdateAttributeModeControls();
        _selectionAttributeVerifyCheckBox.CheckedChanged += (_, _) =>
            UpdateAttributeModeControls();
        _selectionAttributePresetCheckBox.CheckedChanged += (_, _) =>
            UpdateAttributeModeControls();
        _selectionAttributePositionLockedCheckBox.CheckedChanged += (_, _) =>
            UpdateAttributeModeControls();
        _selectionAttributeConstantCheckBox.CheckedChanged += (_, _) =>
            UpdateAttributeModeControls();
        _layerStateSelector.SelectionChanged += (_, _) =>
        {
            if (_isRefreshingSelectionProperties)
            {
                return;
            }
            _isRefreshingSelectionProperties = true;
            try
            {
                RefreshLayerStateControls();
            }
            finally
            {
                _isRefreshingSelectionProperties = false;
            }
            UpdateEditControls();
        };
        _layerVisibilitySelector.SelectionChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _layerPlotSelector.SelectionChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _layerFreezeSelector.SelectionChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _layerLockSelector.SelectionChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _layerColorInput.TextChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _layerLineWeightSelector.SelectionChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _layerLineTypeSelector.SelectionChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _layerNameInput.TextChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _layerMergeTargetSelector.SelectionChanged += (_, _) =>
        {
            if (!_isRefreshingSelectionProperties)
            {
                UpdateEditControls();
            }
        };
        _setSelectionColorButton.Click += (_, _) => SetSelectionColor();
        _setSelectionLineWeightButton.Click += (_, _) =>
            SetSelectionLineWeight();
        _setSelectionLayerButton.Click += (_, _) => SetSelectionLayer();
        _setSelectionLineTypeButton.Click += (_, _) => SetSelectionLineType();
        _setSelectionLineTypeScaleButton.Click += (_, _) =>
            SetSelectionLineTypeScale();
        _setSelectionTransparencyButton.Click += (_, _) =>
            SetSelectionTransparency();
        _setSelectionVisibilityButton.Click += (_, _) =>
            SetSelectionVisibility();
        _setSelectionSolidThicknessButton.Click += (_, _) =>
            SetSelectionSolidThickness();
        _setSelectionAttributeValueButton.Click += (_, _) =>
            SetSelectionAttributeValue();
        _setSelectionAttributePromptButton.Click += (_, _) =>
            SetSelectionAttributePrompt();
        _setSelectionAttributeTagButton.Click += (_, _) =>
            SetSelectionAttributeTag();
        _setSelectionAttributeModesButton.Click += (_, _) =>
            SetSelectionAttributeModes();
        _setSelectionAttributeConstantButton.Click += (_, _) =>
            SetSelectionAttributeConstantMode();
        _synchronizeSelectionAttributePropertiesButton.Click += (_, _) =>
            SynchronizeSelectionAttributeProperties();
        _setLayerVisibilityButton.Click += (_, _) => SetLayerVisibility();
        _setLayerPlotButton.Click += (_, _) => SetLayerPlotFlag();
        _setLayerFreezeButton.Click += (_, _) => SetLayerFreeze();
        _setLayerLockButton.Click += (_, _) => SetLayerLock();
        _setLayerColorButton.Click += (_, _) => SetLayerColor();
        _setLayerLineWeightButton.Click += (_, _) => SetLayerLineWeight();
        _setLayerLineTypeButton.Click += (_, _) => SetLayerLineType();
        _createLayerButton.Click += (_, _) => CreateLayer();
        _renameLayerButton.Click += (_, _) => RenameLayer();
        _removeLayerButton.Click += (_, _) => RemoveLayer();
        _queueLayerMergeSourceButton.Click += (_, _) => QueueLayerMergeSource();
        _clearLayerMergeSourcesButton.Click += (_, _) =>
            ClearLayerMergeSources(setStatus: true);
        _mergeLayerButton.Click += (_, _) => MergeLayer();
        _createPageSetupButton.Click += (_, _) =>
            CreateNamedPageSetupFromModel();
        _updatePageSetupButton.Click += (_, _) =>
            UpdateSelectedPageSetupFromModel();
        _renamePageSetupButton.Click += (_, _) =>
            RenameSelectedNamedPageSetup();
        _deletePageSetupButton.Click += (_, _) =>
            DeleteSelectedNamedPageSetup();
        _editPageSetupFieldsButton.Click += (_, _) =>
            EditSelectedPageSetupFields();
        _clearSelectionButton.Click += (_, _) =>
        {
            _canvas.ClearSelection();
            ClearMeshSubobjectSelection();
        };
        _undoButton.Click += (_, _) => PerformUndo();
        _redoButton.Click += (_, _) => PerformRedo();
        _deleteButton.Click += (_, _) => PerformDelete();
        _meshSmoothMoreButton.Click += (_, _) =>
            AdjustSelectedMeshSmoothness(1);
        _meshSmoothLessButton.Click += (_, _) =>
            AdjustSelectedMeshSmoothness(-1);
        _meshCreaseInput.TextChanged += (_, _) => UpdateEditControls();
        _setMeshCreaseButton.Click += (_, _) => SetSelectedMeshCrease();
        _removeMeshCreaseButton.Click += (_, _) =>
            SetSelectedMeshCrease(0.0);
        _lineButton.Click += (_, _) => BeginLineAuthoring();
        _lineUndoButton.Click += (_, _) => UndoLineAuthoringSegment();
        _lineCloseButton.Click += (_, _) => CompleteLineAuthoring(close: true);
        _lineFinishButton.Click += (_, _) => CompleteLineAuthoring(close: false);
        _rayButton.Click += (_, _) => BeginRayAuthoring();
        _rayUndoButton.Click += (_, _) => UndoRayAuthoringRay();
        _rayFinishButton.Click += (_, _) => CompleteRayAuthoring();
        _xlineButton.Click += (_, _) => BeginXLineAuthoring();
        _xlineModeSelector.SelectionChanged += (_, _) => UpdateEditControls();
        _xlineUndoButton.Click += (_, _) => UndoXLineAuthoringLine();
        _xlineFinishButton.Click += (_, _) => CompleteXLineAuthoring();
        _pointButton.Click += (_, _) => BeginPointAuthoring();
        _polylineButton.Click += (_, _) => BeginPolylineAuthoring();
        _polylineUndoButton.Click += (_, _) => UndoPolylineAuthoringSegment();
        _polylineLineModeButton.Click += (_, _) =>
            SetPolylineAuthoringMode(CadPolylineAuthoringMode.Line);
        _polylineArcModeButton.Click += (_, _) =>
            SetPolylineAuthoringMode(CadPolylineAuthoringMode.TangentArc);
        _polylineArcConstructionSelector.SelectionChanged += (_, _) =>
            UpdateEditControls();
        _polylineArcConstructionButton.Click += (_, _) =>
        {
            if ((_polylineArcConstructionSelector.SelectedItem as ComboBoxItem)?.Tag is
                CadPolylineArcConstruction construction)
            {
                BeginPolylineArcConstruction(construction);
            }
        };
        _polylineWidthButton.Click += (_, _) =>
            BeginPolylineWidthInput(CadPolylineWidthInputMode.Width);
        _polylineHalfwidthButton.Click += (_, _) =>
            BeginPolylineWidthInput(CadPolylineWidthInputMode.Halfwidth);
        _polylineLengthButton.Click += (_, _) => BeginPolylineLengthInput();
        _polylineCloseButton.Click += (_, _) =>
            CompletePolylineAuthoring(close: true);
        _polylineFinishButton.Click += (_, _) =>
            CompletePolylineAuthoring(close: false);
        _circleButton.Click += (_, _) =>
            BeginCircleAuthoring(CadCircleAuthoringMode.CenterRadius);
        _circleDiameterButton.Click += (_, _) =>
            BeginCircleAuthoring(CadCircleAuthoringMode.CenterDiameter);
        _circleTwoPointButton.Click += (_, _) =>
            BeginCircleAuthoring(CadCircleAuthoringMode.TwoPoint);
        _circleThreePointButton.Click += (_, _) =>
            BeginCircleAuthoring(CadCircleAuthoringMode.ThreePoint);
        _arcButton.Click += (_, _) =>
        {
            if ((_arcModeSelector.SelectedItem as ComboBoxItem)?.Tag is
                CadArcAuthoringMode mode)
            {
                BeginArcAuthoring(mode);
            }
        };
        _arcModeSelector.SelectionChanged += (_, _) => UpdateEditControls();
        _ellipseButton.Click += (_, _) =>
        {
            if ((_ellipseModeSelector.SelectedItem as ComboBoxItem)?.Tag is
                    CadEllipseAuthoringMode mode)
            {
                CadEllipseArcInputMode arcInputMode = IsIsocircleMode(mode)
                    ? CadEllipseArcInputMode.Full
                    : (_ellipseArcInputSelector.SelectedItem as ComboBoxItem)?.Tag
                        is CadEllipseArcInputMode selectedArcInputMode
                            ? selectedArcInputMode
                            : CadEllipseArcInputMode.Full;
                BeginEllipseAuthoring(mode, arcInputMode);
            }
        };
        _ellipseModeSelector.SelectionChanged += (_, _) => UpdateEditControls();
        _ellipseArcInputSelector.SelectionChanged += (_, _) =>
            UpdateEditControls();
        _polygonButton.Click += (_, _) =>
        {
            if (CadPolygonSideCount.TryParse(
                    _polygonSideCountInput.Text,
                    out CadPolygonSideCount sideCount) &&
                (_polygonModeSelector.SelectedItem as ComboBoxItem)?.Tag is
                    CadPolygonAuthoringMode mode)
            {
                BeginPolygonAuthoring(sideCount.Value, mode);
            }
        };
        _polygonSideCountInput.TextChanged += (_, _) => UpdateEditControls();
        _polygonModeSelector.SelectionChanged += (_, _) => UpdateEditControls();
        _rectangleButton.Click += (_, _) => BeginRectangleAuthoring();
        _rectangleConstructionSelector.SelectionChanged += (_, _) =>
            UpdateEditControls();
        _rectangleAreaDimensionSelector.SelectionChanged += (_, _) =>
            UpdateEditControls();
        _rectangleValuesInput.TextChanged += (_, _) => UpdateEditControls();
        _rectangleCornerSelector.SelectionChanged += (_, _) =>
            UpdateEditControls();
        _rectangleCornerValuesInput.TextChanged += (_, _) =>
            UpdateEditControls();
        _rectangleRotationInput.TextChanged += (_, _) => UpdateEditControls();
        sendToBack.Click += (_, _) =>
            SetSelectionDrawOrder(CadDrawOrderPlacement.SendToBack);
        bringToFront.Click += (_, _) =>
            SetSelectionDrawOrder(CadDrawOrderPlacement.BringToFront);
        bringAbove.Click += (_, _) =>
            BeginSelectionDrawOrderReferencePick(CadDrawOrderPlacement.BringAbove);
        sendUnder.Click += (_, _) =>
            BeginSelectionDrawOrderReferencePick(CadDrawOrderPlacement.SendUnder);
        moveNegativeX.Click += (_, _) => MoveSelection(-1, 0);
        movePositiveX.Click += (_, _) => MoveSelection(1, 0);
        moveNegativeY.Click += (_, _) => MoveSelection(0, -1);
        movePositiveY.Click += (_, _) => MoveSelection(0, 1);
        _moveByPointsButton.Click += (_, _) =>
            BeginSelectionPointTransform(CadPointTransformOperation.Move);
        copyNegativeX.Click += (_, _) => CopySelection(-1, 0);
        copyPositiveX.Click += (_, _) => CopySelection(1, 0);
        copyNegativeY.Click += (_, _) => CopySelection(0, -1);
        copyPositiveY.Click += (_, _) => CopySelection(0, 1);
        _copyByPointsButton.Click += (_, _) =>
            BeginSelectionPointTransform(CadPointTransformOperation.Copy);
        _objectSnapSelector.SelectionChanged += (_, _) =>
        {
            if ((_objectSnapSelector.SelectedItem as ComboBoxItem)?.Tag is
                CadObjectSnapModes modes)
            {
                _canvas.ObjectSnapModes = modes;
            }
        };
        _planGridSnapCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_isRefreshingPlanConstraints)
            {
                SetPlanGridSnapFromInteraction(
                    _planGridSnapCheckBox.IsChecked,
                    "Grid snap control");
            }
        };
        _planGridDisplayCheckBox.CheckedChanged += (_, _) =>
            UpdatePlanGridDisplayEditControls();
        _planGridDotsCheckBox.CheckedChanged += (_, _) =>
            _canvas.PlanGridPresentationStyle =
                _planGridDotsCheckBox.IsChecked
                    ? CadPlanGridPresentationStyle.Dots
                    : CadPlanGridPresentationStyle.Lines;
        _planGridIsometricCheckBox.CheckedChanged += (_, _) =>
            UpdatePlanGridDisplayEditControls();
        _planGridIsoplaneSelector.SelectionChanged += (_, _) =>
            UpdatePlanGridDisplayEditControls();
        _planSnapUnitXInput.TextChanged += (_, _) =>
            UpdatePlanGridDisplayEditControls();
        _planSnapUnitYInput.TextChanged += (_, _) =>
            UpdatePlanGridDisplayEditControls();
        _planGridAdaptiveCheckBox.CheckedChanged += (_, _) =>
            UpdatePlanGridDisplayEditControls();
        _planGridSubdivisionCheckBox.CheckedChanged += (_, _) =>
            UpdatePlanGridDisplayEditControls();
        _planGridBeyondLimitsCheckBox.CheckedChanged += (_, _) =>
            UpdatePlanGridDisplayEditControls();
        _planGridUnitXInput.TextChanged += (_, _) =>
            UpdatePlanGridDisplayEditControls();
        _planGridUnitYInput.TextChanged += (_, _) =>
            UpdatePlanGridDisplayEditControls();
        _planGridMajorInput.TextChanged += (_, _) =>
            UpdatePlanGridDisplayEditControls();
        _applyPlanGridDisplayButton.Click += (_, _) =>
            ApplyPlanGridDisplaySettings();
        _planOrthoCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_isRefreshingPlanConstraints)
            {
                SetPlanOrthoModeFromInteraction(
                    _planOrthoCheckBox.IsChecked,
                    "Ortho control");
            }
        };
        _planPolarTrackingCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_isRefreshingPlanConstraints)
            {
                SetPlanPolarTrackingFromInteraction(
                    _planPolarTrackingCheckBox.IsChecked,
                    "Polar control");
            }
        };
        _planPolarTrackingIncrementSelector.SelectionChanged += (_, _) =>
        {
            if (!_isRefreshingPlanConstraints &&
                _planPolarTrackingIncrementSelector.SelectedItem is
                ComboBoxItem { Tag: double increment })
            {
                _canvas.PlanPolarTrackingIncrementDegrees = increment;
            }
        };
        _planPolarRelativeCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_isRefreshingPlanConstraints)
            {
                _canvas.PlanPolarAngleMeasurement =
                    _planPolarRelativeCheckBox.IsChecked
                        ? CadPlanPolarAngleMeasurement.RelativeToLastSegment
                        : CadPlanPolarAngleMeasurement.Absolute;
                SetStatus(_planPolarRelativeCheckBox.IsChecked
                    ? "Polar angles now measure relative to the last accepted LINE segment."
                    : "Polar angles now measure from the current UCS basis.");
                UpdateEditControls();
            }
        };
        _planPolarAdditionalAnglesCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_isRefreshingPlanConstraints)
            {
                SetPlanPolarAdditionalAnglesFromInteraction(
                    _planPolarAdditionalAnglesCheckBox.IsChecked);
            }
        };
        _planPolarAdditionalAnglesInput.TextChanged += (_, _) =>
        {
            if (!_isRefreshingPlanConstraints)
            {
                if (CadPlanPolarAdditionalAngles.TryParseInvariantDegrees(
                    _planPolarAdditionalAnglesInput.Text,
                    out CadPlanPolarAdditionalAngles angles))
                {
                    _canvas.SetPlanPolarAdditionalAngles(angles);
                }
                else if (_canvas.UsePlanPolarAdditionalAngles)
                {
                    _canvas.UsePlanPolarAdditionalAngles = false;
                    SetStatus(
                        "Additional polar angles require at most 10 finite invariant values separated by semicolons.");
                    RefreshPlanConstraintControls();
                }
            }
            UpdateEditControls();
        };
        _planPolarSnapCheckBox.CheckedChanged += (_, _) =>
        {
            if (!_isRefreshingPlanConstraints)
            {
                SetPlanPolarSnapFromInteraction(
                    _planPolarSnapCheckBox.IsChecked,
                    "PolarSnap control");
            }
        };
        _planPolarSnapDistanceInput.TextChanged += (_, _) =>
        {
            if (!_isRefreshingPlanConstraints &&
                TryParseNonNegativeInvariantDouble(
                    _planPolarSnapDistanceInput.Text,
                    out double distance))
            {
                _canvas.PlanPolarSnapDistance = distance;
            }
            UpdateEditControls();
        };
        _pointTransformInput.TextChanged += (_, _) => UpdateEditControls();
        _pointTransformInput.KeyDown += (_, args) =>
        {
            if (!args.Handled && args.Key == Key.Enter)
            {
                if ((_canvas.IsLineAuthoring || _canvas.IsRayAuthoring ||
                        _canvas.IsXLineAuthoring ||
                        _canvas.IsPolylineAuthoring) &&
                    string.IsNullOrWhiteSpace(_pointTransformInput.Text))
                {
                    if (_canvas.IsXLineAuthoring)
                    {
                        CompleteXLineAuthoring();
                    }
                    else if (_canvas.IsRayAuthoring)
                    {
                        CompleteRayAuthoring();
                    }
                    else if (_canvas.IsPolylineAuthoring)
                    {
                        CompletePolylineAuthoring(close: false);
                    }
                    else
                    {
                        CompleteLineAuthoring(close: false);
                    }
                }
                else
                {
                    AcceptPointInput();
                }
                args.Handled = true;
            }
        };
        _acceptPointTransformInputButton.Click += (_, _) =>
            AcceptPointInput();
        rotateCounterclockwise.Click += (_, _) => RotateSelection(1);
        rotateClockwise.Click += (_, _) => RotateSelection(-1);
        scaleUp.Click += (_, _) => ScaleSelection(useReciprocal: false);
        scaleDown.Click += (_, _) => ScaleSelection(useReciprocal: true);
        _canvas.SelectionChanged += (_, _) =>
        {
            RefreshMeshSelectionMaterials();
            RefreshSelectionPropertyControls();
            if (!_isBusy)
            {
                SetStatus(DescribeCurrentDocument(
                    _currentDocumentName,
                    _currentDiagnosticCount));
            }
            UpdateEditControls();
        };
        _canvas.EditStateChanged += (_, _) => UpdateEditControls();
        _canvas.DrawOrderReferencePickChanged += (_, _) =>
        {
            if (!_isBusy && _canvas.PendingDrawOrderPlacement is not null)
            {
                SetStatus(DescribeDrawOrderReferencePick());
            }
            UpdateEditControls();
        };
        _canvas.PointTransformChanged += (_, args) =>
        {
            _pointTransformInput.Text = string.Empty;
            SetStatus(DescribePointTransform(args));
            UpdateEditControls();
        };
        _canvas.LineAuthoringChanged += (_, args) =>
        {
            _pointTransformInput.Text = string.Empty;
            SetStatus(DescribeLineAuthoring(args));
            UpdateEditControls();
        };
        _canvas.RayAuthoringChanged += (_, args) =>
        {
            _pointTransformInput.Text = string.Empty;
            SetStatus(DescribeRayAuthoring(args));
            UpdateEditControls();
        };
        _canvas.XLineAuthoringChanged += (_, args) =>
        {
            _pointTransformInput.Text = string.Empty;
            SetStatus(DescribeXLineAuthoring(args));
            UpdateEditControls();
        };
        _canvas.PointAuthoringChanged += (_, args) =>
        {
            _pointTransformInput.Text = string.Empty;
            SetStatus(DescribePointAuthoring(args));
            UpdateEditControls();
        };
        _canvas.PolylineAuthoringChanged += (_, args) =>
        {
            _pointTransformInput.Text = string.Empty;
            SetStatus(DescribePolylineAuthoring(args));
            UpdateEditControls();
        };
        _canvas.CircleAuthoringChanged += (_, args) =>
        {
            _pointTransformInput.Text = string.Empty;
            SetStatus(DescribeCircleAuthoring(args));
            UpdateEditControls();
        };
        _canvas.ArcAuthoringChanged += (_, args) =>
        {
            _pointTransformInput.Text = string.Empty;
            SetStatus(DescribeArcAuthoring(args));
            UpdateEditControls();
        };
        _canvas.EllipseAuthoringChanged += (_, args) =>
        {
            _pointTransformInput.Text = string.Empty;
            SetStatus(DescribeEllipseAuthoring(args));
            UpdateEditControls();
        };
        _canvas.PolygonAuthoringChanged += (_, args) =>
        {
            _pointTransformInput.Text = string.Empty;
            SetStatus(DescribePolygonAuthoring(args));
            UpdateEditControls();
        };
        _canvas.RectangleAuthoringChanged += (_, args) =>
        {
            _pointTransformInput.Text = string.Empty;
            SetStatus(DescribeRectangleAuthoring(args));
            UpdateEditControls();
        };
        _canvas.PointTransformInputAvailabilityChanged += (_, _) =>
            UpdateEditControls();
        _canvas.SnapshotChanged += (_, args) =>
        {
            RefreshPlanGridDisplayControls();
            RefreshPlanConstraintControls();
            EnsureLayerMergeSourcesAreCurrent();
            RebuildMesh3DView(args.ResetsView);
            if (_isPrintPreview)
            {
                ShowPlanView(clearPreview: true);
            }
            RefreshPageSetups(preserveSelection: true);
            RefreshAttributeDisplayMode();
            RefreshSelectionPropertyControls();
            UpdateEditControls();
        };
        RebuildMesh3DView(resetCamera: true);
        RefreshPlanGridDisplayControls();
        RefreshPageSetups(preserveSelection: false);
        RefreshAttributeDisplayMode();
        RefreshSelectionPropertyControls();
        UpdateEditControls();
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key is Key.ControlLeft or Key.ControlRight)
        {
            _canvas.RefreshPolylineArcClockwiseOverride();
            _canvas.RefreshArcClockwiseOverride();
        }
        if (!e.Handled &&
            _isPrintPreview &&
            e.Key == Key.Escape &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            ShowPlanView(clearPreview: true);
            SetStatus(DescribeCurrentDocument(
                _currentDocumentName,
                _currentDiagnosticCount));
            UpdateEditControls();
            e.Handled = true;
            return;
        }

        if (!e.Handled &&
            _canvas.IsRectangleAuthoring &&
            e.Key == Key.Escape &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            _canvas.CancelRectangleAuthoring();
            e.Handled = true;
            return;
        }

        if (!e.Handled &&
            _canvas.IsPolygonAuthoring &&
            e.Key == Key.Escape &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            _canvas.CancelPolygonAuthoring();
            e.Handled = true;
            return;
        }

        if (!e.Handled &&
            _canvas.IsEllipseAuthoring &&
            e.Key == Key.Escape &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            _canvas.CancelEllipseAuthoring();
            e.Handled = true;
            return;
        }

        if (!e.Handled &&
            _canvas.IsArcAuthoring &&
            e.Key == Key.Escape &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            _canvas.CancelArcAuthoring();
            e.Handled = true;
            return;
        }

        if (!e.Handled &&
            _canvas.IsCircleAuthoring &&
            e.Key == Key.Escape &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            _canvas.CancelCircleAuthoring();
            e.Handled = true;
            return;
        }

        if (!e.Handled &&
            _canvas.IsPointAuthoring &&
            e.Key == Key.Escape &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            _canvas.CancelPointAuthoring();
            e.Handled = true;
            return;
        }

        if (!e.Handled &&
            _canvas.IsXLineAuthoring &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                CompleteXLineAuthoring();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.U)
            {
                UndoXLineAuthoringLine();
                e.Handled = true;
                return;
            }
        }

        if (!e.Handled &&
            _canvas.IsRayAuthoring &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                CompleteRayAuthoring();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.U)
            {
                UndoRayAuthoringRay();
                e.Handled = true;
                return;
            }
        }

        if (!e.Handled &&
            _canvas.IsLineAuthoring &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                CompleteLineAuthoring(close: false);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.U)
            {
                UndoLineAuthoringSegment();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.C && _canvas.CanCloseLineAuthoring)
            {
                CompleteLineAuthoring(close: true);
                e.Handled = true;
                return;
            }
        }

        if (!e.Handled &&
            _canvas.IsPolylineAuthoring &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            if (e.Key is Key.Enter or Key.Escape &&
                _canvas.PendingPolylinePrompt == CadPolylineAuthoringPrompt.Point)
            {
                CompletePolylineAuthoring(close: false);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.U && _canvas.CanUndoPolylineAuthoring)
            {
                UndoPolylineAuthoringSegment();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.C && _canvas.CanClosePolylineAuthoring)
            {
                CompletePolylineAuthoring(close: true);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.A &&
                _canvas.PendingPolylinePrompt == CadPolylineAuthoringPrompt.Point &&
                _canvas.PendingPolylineCurrentPoint is not null)
            {
                if (_canvas.PolylineAuthoringMode ==
                    CadPolylineAuthoringMode.TangentArc)
                {
                    BeginPolylineArcConstruction(
                        CadPolylineArcConstruction.IncludedAngle);
                }
                else
                {
                    SetPolylineAuthoringMode(
                        CadPolylineAuthoringMode.TangentArc);
                }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.A &&
                _canvas.CanBeginPolylineArcConstructionOption(
                    CadPolylineArcConstruction.IncludedAngle))
            {
                BeginPolylineArcConstruction(
                    CadPolylineArcConstruction.IncludedAngle);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.D &&
                _canvas.CanBeginPolylineArcConstructionOption(
                    CadPolylineArcConstruction.Direction))
            {
                BeginPolylineArcConstruction(CadPolylineArcConstruction.Direction);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.R &&
                _canvas.CanBeginPolylineArcConstructionOption(
                    CadPolylineArcConstruction.Radius))
            {
                BeginPolylineArcConstruction(CadPolylineArcConstruction.Radius);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.S &&
                _canvas.CanBeginPolylineArcConstructionOption(
                    CadPolylineArcConstruction.ThreePoint))
            {
                BeginPolylineArcConstruction(CadPolylineArcConstruction.ThreePoint);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.L)
            {
                if (_canvas.PendingPolylinePrompt ==
                        CadPolylineAuthoringPrompt.ArcEndpoint &&
                    _canvas.CanBeginPolylineLengthInput)
                {
                    BeginPolylineLengthInput();
                    e.Handled = true;
                    return;
                }
                if (_canvas.PendingPolylinePrompt != CadPolylineAuthoringPrompt.Point)
                {
                    return;
                }
                if (_canvas.PolylineAuthoringMode == CadPolylineAuthoringMode.TangentArc)
                {
                    SetPolylineAuthoringMode(CadPolylineAuthoringMode.Line);
                }
                else
                {
                    BeginPolylineLengthInput();
                }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.W && _canvas.CanBeginPolylineWidthInput)
            {
                BeginPolylineWidthInput(CadPolylineWidthInputMode.Width);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.H && _canvas.CanBeginPolylineWidthInput)
            {
                BeginPolylineWidthInput(CadPolylineWidthInputMode.Halfwidth);
                e.Handled = true;
                return;
            }
        }

        if (!e.Handled &&
            _canvas.PendingPointTransformOperation is not null &&
            e.Key == Key.Escape &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            _canvas.CancelSelectionPointTransform();
            e.Handled = true;
            return;
        }

        if (!e.Handled &&
            _canvas.PendingDrawOrderPlacement is not null &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            if (e.Key == Key.Enter)
            {
                CommitSelectionDrawOrderReferencePick();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                CancelSelectionDrawOrderReferencePick();
                e.Handled = true;
                return;
            }
        }

        bool isIsoplaneCycleKey =
            e.Key == Key.F5 ||
            (e.Key == Key.E && InputSystem.Current.IsControlPressed);
        if (!e.Handled &&
            isIsoplaneCycleKey &&
            !_isBusy &&
            !_isPrintPreview &&
            !_is3DView &&
            _canvas.CurrentSession is not null)
        {
            CyclePlanIsoplaneFromKeyboard();
            e.Handled = true;
            return;
        }

        if (!e.Handled &&
            e.Key is Key.F8 or Key.F9 or Key.F10 &&
            !_isBusy &&
            !_isPrintPreview &&
            !_is3DView &&
            _canvas.CurrentSession is not null)
        {
            if (e.Key == Key.F8)
            {
                SetPlanOrthoModeFromInteraction(
                    !_canvas.IsPlanOrthoEnabled,
                    "F8");
            }
            else if (e.Key == Key.F9)
            {
                SetPlanSnapModeFromInteraction(
                    !_canvas.IsPlanSnapEnabled,
                    "F9");
            }
            else
            {
                SetPlanPolarTrackingFromInteraction(
                    !_canvas.IsPlanPolarTrackingEnabled,
                    "F10");
            }
            e.Handled = true;
            return;
        }

        if (!e.Handled &&
            !_isPrintPreview &&
            e.Key == Key.Delete &&
            FocusManager.GetFocusedElement() is not TextBox)
        {
            PerformDelete();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    public override void OnKeyUp(KeyRoutedEventArgs e)
    {
        if (e.Key is Key.ControlLeft or Key.ControlRight)
        {
            _canvas.RefreshPolylineArcClockwiseOverride();
            _canvas.RefreshArcClockwiseOverride();
        }
        base.OnKeyUp(e);
    }

    private void CyclePlanIsoplaneFromKeyboard()
    {
        if (HasStagedPlanGridDisplayEdit())
        {
            SetStatus(
                "Apply or revert the staged drafting-grid values before cycling SNAPISOPAIR.");
            return;
        }

        try
        {
            CadPlanIsoplane isoplane = _canvas.CyclePlanIsoplane();
            SetStatus(
                $"Cycled SNAPISOPAIR to {(int)isoplane} ({isoplane}) as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Cycle isoplane failed: {exception.Message}");
            RefreshPlanGridDisplayControls();
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private bool HasStagedPlanGridDisplayEdit()
    {
        if (!TryCreatePlanGridDisplayEditValues(out var values))
        {
            return true;
        }

        try
        {
            return _canvas.GetPlanGridDisplayEditValues() != values;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException)
        {
            return true;
        }
    }

    private void SetPlanOrthoModeFromInteraction(
        bool isEnabled,
        string source)
    {
        bool changesPersistedMode =
            _canvas.PersistedPlanOrthoMode != isEnabled;
        if (changesPersistedMode && HasStagedPlanGridDisplayEdit())
        {
            SetStatus(
                "Apply or revert the staged drafting-grid values before changing ORTHOMODE.");
            RefreshPlanConstraintControls();
            return;
        }

        try
        {
            if (changesPersistedMode)
            {
                _canvas.SetPlanOrthoMode(isEnabled);
            }
            else
            {
                _canvas.IsPlanOrthoEnabled = isEnabled;
            }
            SetStatus(
                $"{source}: ORTHOMODE={(isEnabled ? 1 : 0)}" +
                (changesPersistedMode ? " as one edit." : "."));
        }
        catch (Exception exception)
        {
            SetStatus($"Set Ortho mode failed: {exception.Message}");
        }
        finally
        {
            RefreshPlanConstraintControls();
            UpdateEditControls();
        }
    }

    private void SetPlanGridSnapFromInteraction(
        bool isEnabled,
        string source)
    {
        SetPlanSnapStateFromInteraction(
            CadPlanSnapType.Grid,
            isEnabled,
            source);
    }

    private void SetPlanPolarSnapFromInteraction(
        bool isEnabled,
        string source)
    {
        bool hasValidDistance = TryParseNonNegativeInvariantDouble(
            _planPolarSnapDistanceInput.Text,
            out double distance);
        if (isEnabled && !hasValidDistance)
        {
            SetStatus(
                "PolarSnap requires a finite non-negative invariant distance.");
            RefreshPlanConstraintControls();
            UpdateEditControls();
            return;
        }

        SetPlanSnapStateFromInteraction(
            CadPlanSnapType.Polar,
            isEnabled,
            source,
            isEnabled ? distance : null);
    }

    private void SetPlanSnapModeFromInteraction(
        bool isEnabled,
        string source)
    {
        bool hasValidDistance = TryParseNonNegativeInvariantDouble(
            _planPolarSnapDistanceInput.Text,
            out double distance);
        if (isEnabled && _canvas.PlanSnapType == CadPlanSnapType.Polar &&
            !hasValidDistance)
        {
            SetStatus(
                "F9 cannot enable PolarSnap until Polar Distance is valid.");
            RefreshPlanConstraintControls();
            UpdateEditControls();
            return;
        }

        CadPlanSnapType type = _canvas.PlanSnapType;
        SetPlanSnapStateFromInteraction(
            type,
            isEnabled,
            source,
            isEnabled && type == CadPlanSnapType.Polar
                ? distance
                : null);
    }

    private void SetPlanSnapStateFromInteraction(
        CadPlanSnapType type,
        bool isEnabled,
        string source,
        double? polarSnapDistance = null)
    {
        bool changesPersistedMode =
            _canvas.PersistedPlanSnapMode != isEnabled;
        if (changesPersistedMode && HasStagedPlanGridDisplayEdit())
        {
            SetStatus(
                "Apply or revert the staged drafting-grid values before changing SNAPMODE.");
            RefreshPlanConstraintControls();
            UpdateEditControls();
            return;
        }

        try
        {
            if (polarSnapDistance.HasValue)
            {
                _canvas.PlanPolarSnapDistance = polarSnapDistance.Value;
            }
            _canvas.PlanSnapType = type;
            if (changesPersistedMode)
            {
                _canvas.SetPlanSnapMode(isEnabled);
            }
            else
            {
                _canvas.IsPlanSnapEnabled = isEnabled;
            }
            SetStatus(
                $"{source}: SNAPMODE={(isEnabled ? 1 : 0)}; " +
                $"SNAPTYPE={type}" +
                (changesPersistedMode ? " as one edit." : "."));
        }
        catch (Exception exception)
        {
            SetStatus($"Set Snap mode failed: {exception.Message}");
        }
        finally
        {
            RefreshPlanConstraintControls();
            UpdateEditControls();
        }
    }

    private void SetPlanPolarTrackingFromInteraction(
        bool isEnabled,
        string source)
    {
        bool disablesPersistedOrtho =
            isEnabled && _canvas.PersistedPlanOrthoMode;
        if (disablesPersistedOrtho && HasStagedPlanGridDisplayEdit())
        {
            SetStatus(
                "Apply or revert the staged drafting-grid values before Polar Tracking disables ORTHOMODE.");
            RefreshPlanConstraintControls();
            return;
        }

        try
        {
            if (disablesPersistedOrtho)
            {
                _canvas.SetPlanOrthoMode(false);
            }
            _canvas.IsPlanPolarTrackingEnabled = isEnabled;
            SetStatus(
                $"{source}: Polar Tracking {(isEnabled ? "on" : "off")}" +
                (disablesPersistedOrtho
                    ? "; ORTHOMODE=0 as one edit."
                    : "."));
        }
        catch (Exception exception)
        {
            SetStatus($"Set Polar Tracking failed: {exception.Message}");
        }
        finally
        {
            RefreshPlanConstraintControls();
            UpdateEditControls();
        }
    }

    private void SetPlanPolarAdditionalAnglesFromInteraction(bool isEnabled)
    {
        CadPlanPolarAdditionalAngles angles = default;
        if (isEnabled &&
            !CadPlanPolarAdditionalAngles.TryParseInvariantDegrees(
                _planPolarAdditionalAnglesInput.Text,
                out angles))
        {
            SetStatus(
                "Additional polar angles require at most 10 finite invariant values separated by semicolons.");
            RefreshPlanConstraintControls();
            UpdateEditControls();
            return;
        }

        if (isEnabled)
        {
            _canvas.SetPlanPolarAdditionalAngles(angles);
        }
        _canvas.UsePlanPolarAdditionalAngles = isEnabled;
        SetStatus(
            isEnabled
                ? $"Additional polar angles enabled ({angles.Count}/10)."
                : "Additional polar angles disabled.");
        RefreshPlanConstraintControls();
        UpdateEditControls();
    }

    private void ToggleViewMode()
    {
        if (!_viewModeButton.IsEnabled || _isPrintPreview)
        {
            return;
        }
        _is3DView = !_is3DView;
        _canvas.Visibility = _is3DView ? Visibility.Collapsed : Visibility.Visible;
        _viewport3D.Visibility = _is3DView ? Visibility.Visible : Visibility.Collapsed;
        _meshSubobjectOverlay.Visibility = _viewport3D.Visibility;
        _viewModeText.Text = _is3DView ? "Plan view" : "3D surfaces";
        if (_is3DView)
        {
            _viewport3D.Invalidate();
        }
        UpdateEditControls();
    }

    private void TogglePrintPreview()
    {
        if (_isBusy || _canvas.PendingDrawOrderPlacement is not null)
        {
            return;
        }
        if (_isPrintPreview)
        {
            ShowPlanView(clearPreview: true);
            SetStatus(DescribeCurrentDocument(
                _currentDocumentName,
                _currentDiagnosticCount));
            UpdateEditControls();
            return;
        }

        ShowSelectedPrintPreview();
    }

    private void OnPageSetupSelectionChanged()
    {
        if (_isRefreshingPageSetups)
        {
            return;
        }
        RefreshPageSetupFieldControls();
        if (_isPrintPreview)
        {
            ShowSelectedPrintPreview();
        }
        else
        {
            UpdateEditControls();
        }
    }

    private void RefreshPageSetupFieldControls()
    {
        _isRefreshingPageSetupFields = true;
        try
        {
            CadPageSetupSnapshot? setup =
                (_pageSetupSelector.SelectedItem as ComboBoxItem)?.Tag is
                    PageSetupChoice choice
                    ? choice.PageSetup
                    : null;
            _pageSetupPaperWidthInput.Text = setup is null
                ? string.Empty
                : setup.PaperWidthMillimeters.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture);
            _pageSetupPaperHeightInput.Text = setup is null
                ? string.Empty
                : setup.PaperHeightMillimeters.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture);
            SelectPageSetupFieldChoice(
                _pageSetupRotationSelector,
                setup?.Rotation);
            SelectPageSetupFieldChoice(
                _pageSetupPlotAreaSelector,
                setup?.PlotArea);
            _pageSetupCenterCheckBox.IsChecked = setup?.CenterPlot == true;
            _pageSetupLineweightsCheckBox.IsChecked =
                setup?.PrintLineweights == true;
        }
        finally
        {
            _isRefreshingPageSetupFields = false;
        }
    }

    private void UpdatePageSetupFieldEditControls()
    {
        if (!_isRefreshingPageSetupFields)
        {
            UpdateEditControls();
        }
    }

    private void EditSelectedPageSetupFields()
    {
        if (_isBusy || !TryCreatePageSetupFieldPatch(
                out CadPageSetupSnapshot setup,
                out CadPageSetupFieldPatch patch))
        {
            return;
        }

        try
        {
            _canvas.EditPageSetupFields(
                setup.SourceKind,
                setup.Name,
                patch);
            SetStatus(
                $"Edited {DescribePageSetupSource(setup.SourceKind)} " +
                $"'{setup.Name}' fields as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Edit page setup fields failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private bool TryCreatePageSetupFieldPatch(
        out CadPageSetupSnapshot setup,
        out CadPageSetupFieldPatch patch)
    {
        CadPageSetupSnapshot? selectedSetup =
            (_pageSetupSelector.SelectedItem as ComboBoxItem)?.Tag is
                PageSetupChoice choice
                ? choice.PageSetup
                : null;
        setup = null!;
        patch = null!;
        if (selectedSetup is null ||
            !TryParsePositivePageSetupValue(
                _pageSetupPaperWidthInput.Text,
                out double paperWidth) ||
            !TryParsePositivePageSetupValue(
                _pageSetupPaperHeightInput.Text,
                out double paperHeight) ||
            (_pageSetupRotationSelector.SelectedItem as ComboBoxItem)?.Tag is not
                CadPageRotation rotation ||
            (_pageSetupPlotAreaSelector.SelectedItem as ComboBoxItem)?.Tag is not
                CadPlotAreaKind plotArea)
        {
            return false;
        }

        setup = selectedSetup;

        if (paperWidth == setup.PaperWidthMillimeters &&
            paperHeight == setup.PaperHeightMillimeters &&
            rotation == setup.Rotation &&
            plotArea == setup.PlotArea &&
            _pageSetupCenterCheckBox.IsChecked == setup.CenterPlot &&
            _pageSetupLineweightsCheckBox.IsChecked == setup.PrintLineweights)
        {
            return false;
        }

        patch = new CadPageSetupFieldPatch
        {
            PaperWidthMillimeters = paperWidth,
            PaperHeightMillimeters = paperHeight,
            Rotation = rotation,
            PlotArea = plotArea,
            CenterPlot = _pageSetupCenterCheckBox.IsChecked,
            PrintLineweights = _pageSetupLineweightsCheckBox.IsChecked,
        };
        return true;
    }

    private static bool TryParsePositivePageSetupValue(
        string text,
        out double value) =>
        double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) &&
        double.IsFinite(value) &&
        value > 0.0;

    private static string DescribePageSetupSource(
        CadPageSetupSourceKind sourceKind) => sourceKind switch
    {
        CadPageSetupSourceKind.Layout => "layout",
        CadPageSetupSourceKind.NamedOverride => "named page setup",
        _ => throw new ArgumentOutOfRangeException(nameof(sourceKind)),
    };

    private void ApplySelectedPageSetupToModel()
    {
        if (_isBusy)
        {
            return;
        }

        var item = _pageSetupSelector.SelectedItem as ComboBoxItem;
        var choice = item?.Tag as PageSetupChoice;
        if (choice?.PageSetup is not CadPageSetupSnapshot pageSetup ||
            pageSetup.SourceKind != CadPageSetupSourceKind.NamedOverride)
        {
            SetStatus("Applying a page setup requires a named setup selection.");
            UpdateEditControls();
            return;
        }

        try
        {
            _canvas.ApplyNamedPageSetup(
                ACadLayout.ModelLayoutName,
                pageSetup.Name);
            SetStatus(
                $"Applied named page setup '{pageSetup.Name}' to Model as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Apply page setup failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void CreateNamedPageSetupFromModel()
    {
        if (_isBusy)
        {
            return;
        }

        string name = _pageSetupNameInput.Text;
        try
        {
            _canvas.CreateNamedPageSetupFromLayout(
                ACadLayout.ModelLayoutName,
                name);
            SelectPageSetup(new PageSetupKey(
                false,
                CadPageSetupSourceKind.NamedOverride,
                name));
            SetStatus(
                $"Saved Model plot settings as named page setup '{name}' in one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Create page setup failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void UpdateSelectedPageSetupFromModel()
    {
        if (_isBusy)
        {
            return;
        }

        var item = _pageSetupSelector.SelectedItem as ComboBoxItem;
        var choice = item?.Tag as PageSetupChoice;
        if (choice?.PageSetup is not CadPageSetupSnapshot pageSetup ||
            !choice.CanApplyToModel)
        {
            SetStatus("Updating from Model requires a model-compatible named setup selection.");
            UpdateEditControls();
            return;
        }

        try
        {
            _canvas.UpdateNamedPageSetupFromLayout(
                ACadLayout.ModelLayoutName,
                pageSetup.Name);
            SetStatus(
                $"Updated named page setup '{pageSetup.Name}' from Model as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Update page setup failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void DeleteSelectedNamedPageSetup()
    {
        if (_isBusy)
        {
            return;
        }

        var item = _pageSetupSelector.SelectedItem as ComboBoxItem;
        var choice = item?.Tag as PageSetupChoice;
        if (choice?.PageSetup is not CadPageSetupSnapshot pageSetup ||
            pageSetup.SourceKind != CadPageSetupSourceKind.NamedOverride)
        {
            SetStatus("Deleting a page setup requires a named setup selection.");
            UpdateEditControls();
            return;
        }

        try
        {
            _canvas.DeleteNamedPageSetup(pageSetup.Name);
            SetStatus(
                $"Deleted named page setup '{pageSetup.Name}' as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Delete page setup failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void RenameSelectedNamedPageSetup()
    {
        if (_isBusy)
        {
            return;
        }

        var item = _pageSetupSelector.SelectedItem as ComboBoxItem;
        var choice = item?.Tag as PageSetupChoice;
        if (choice?.PageSetup is not CadPageSetupSnapshot pageSetup ||
            pageSetup.SourceKind != CadPageSetupSourceKind.NamedOverride)
        {
            SetStatus("Renaming a page setup requires a named setup selection.");
            UpdateEditControls();
            return;
        }

        string newName = _pageSetupNameInput.Text;
        try
        {
            _canvas.RenameNamedPageSetup(pageSetup.Name, newName);
            SelectPageSetup(new PageSetupKey(
                false,
                CadPageSetupSourceKind.NamedOverride,
                newName));
            SetStatus(
                $"Renamed page setup '{pageSetup.Name}' to '{newName}' as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Rename page setup failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void ShowSelectedPrintPreview()
    {
        var item = _pageSetupSelector.SelectedItem as ComboBoxItem;
        var choice = item?.Tag as PageSetupChoice;
        if (choice is null)
        {
            ShowPlanView(clearPreview: true);
            SetStatus("Print preview failed: no page setup is selected.");
            UpdateEditControls();
            return;
        }

        try
        {
            CadPrintPlan plan;
            if (choice.IsFallback)
            {
                float outputDpi = CadPrintPreviewCanvas.CalculateFitOutputDpi(
                    _contentHost.Size);
                plan = _canvas.CreateA4PrintPlan(outputDpi);
            }
            else
            {
                CadPageSetupPrintOptionsResult lowering = choice.Lowering!;
                if (!lowering.IsSupported || lowering.PrintOptions is null)
                {
                    CadDiagnostic diagnostic = lowering.Diagnostics.Span[0];
                    throw new NotSupportedException(
                        $"{diagnostic.Code}: {diagnostic.Message}");
                }
                float outputDpi = CadPrintPreviewCanvas.CalculateFitOutputDpi(
                    _contentHost.Size,
                    lowering.PrintOptions);
                plan = _canvas.CreatePageSetupPrintPlan(
                    choice.PageSetup!,
                    outputDpi);
            }
            using (plan)
            {
                _printPreview.Load(plan);
                _is3DView = false;
                _isPrintPreview = true;
                _canvas.Visibility = Visibility.Collapsed;
                _viewport3D.Visibility = Visibility.Collapsed;
                _meshSubobjectOverlay.Visibility = Visibility.Collapsed;
                _printPreview.Visibility = Visibility.Visible;
                _viewModeText.Text = "3D surfaces";
                _printPreviewText.Text = "Plan view";
                SetStatus(
                    $"{choice.StatusName} print preview | " +
                    $"{plan.OutputDpi:N1} DPI | " +
                    $"{plan.PageSizePixels.Width:N0} × {plan.PageSizePixels.Height:N0} px | " +
                    $"generation {plan.ContentGeneration:N0}");
            }
        }
        catch (Exception exception)
        {
            ShowPlanView(clearPreview: true);
            SetStatus($"Print preview failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private async Task ExportSelectedPageAsync(CadPrintOutputFormat format)
    {
        CadDocumentSession? session = _canvas.CurrentSession;
        string label = format == CadPrintOutputFormat.Pdf ? "PDF" : "PNG";
        if (session is null ||
            !TryBeginOperation($"Choose a {label} output destination..."))
        {
            return;
        }

        try
        {
            string extension = format == CadPrintOutputFormat.Pdf
                ? ".pdf"
                : ".png";
            var picker = new FileSavePicker
            {
                SuggestedFileName = SuggestedOutputFileName(session, extension),
            };
            picker.FileTypeChoices.Add(label, new List<string> { extension });
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                SetStatus($"{label} export cancelled.");
                return;
            }

            const float outputDpi = 300f;
            using CadPrintPlan plan = CreateSelectedOutputPlan(outputDpi);
            using CadPrintJob job = new CadPrintJobCompiler().Compile(
            [
                new CadPrintJobPageSource(
                    plan.SourcePageSetupName ?? "Model",
                    plan),
            ]);
            using var destination = new MemoryStream();
            var writer = new CadPrintOutputWriter();
            CadPrintOutputResult result = format == CadPrintOutputFormat.Pdf
                ? writer.WritePdf(job, destination)
                : writer.WritePng(job, 0, destination);
            await file.WriteBytesAsync(destination.ToArray());
            string dpi = result.HasUniformRasterDpi
                ? $"{result.MinimumRasterDpi:N0} DPI"
                : $"{result.MinimumRasterDpi:N0}–{result.MaximumRasterDpi:N0} DPI";
            SetStatus(
                $"Exported {file.Name} | {dpi} | " +
                $"{result.RasterPixelCount:N0} px | " +
                $"{result.EncodedByteCount:N0} bytes");
        }
        catch (Exception exception)
        {
            SetStatus($"{label} export failed: {exception.Message}");
        }
        finally
        {
            EndOperation();
        }
    }

    private CadPrintPlan CreateSelectedOutputPlan(float outputDpi)
    {
        var item = _pageSetupSelector.SelectedItem as ComboBoxItem;
        var choice = item?.Tag as PageSetupChoice;
        if (choice is null)
        {
            throw new InvalidOperationException("No page setup is selected.");
        }
        if (choice.IsFallback)
        {
            return _canvas.CreateA4PrintPlan(outputDpi);
        }

        CadPageSetupPrintOptionsResult lowering = choice.Lowering!;
        if (!lowering.IsSupported || lowering.PrintOptions is null)
        {
            CadDiagnostic diagnostic = lowering.Diagnostics.Span[0];
            throw new NotSupportedException(
                $"{diagnostic.Code}: {diagnostic.Message}");
        }
        return _canvas.CreatePageSetupPrintPlan(choice.PageSetup!, outputDpi);
    }

    private void RefreshPageSetups(bool preserveSelection)
    {
        PageSetupKey? previousKey = preserveSelection
            ? (_pageSetupSelector.SelectedItem as ComboBoxItem)?.Tag is
                PageSetupChoice previous
                ? previous.Key
                : null
            : null;

        _isRefreshingPageSetups = true;
        try
        {
            _pageSetupSelector.Items.Clear();
            var fallback = PageSetupChoice.CreateFallback();
            var fallbackItem = CreatePageSetupItem(fallback);
            _pageSetupSelector.Items.Add(fallbackItem);
            ComboBoxItem? preferredItem = null;
            ComboBoxItem? preservedItem = previousKey == fallback.Key
                ? fallbackItem
                : null;

            CadPageSetupCatalog catalog = _canvas.CreatePageSetupCatalog();
            var loweringCompiler = new CadPageSetupPrintOptionsCompiler();
            var loweringOptions = new CadPageSetupPrintOptionsCompilerOptions
            {
                DisabledLineWeightPolicy =
                    CadDisabledLineWeightPolicy.DeviceHairline,
                UnavailableTransparencyPolicy =
                    CadUnavailablePlotTransparencyPolicy.PreserveRetainedAlpha,
            };
            foreach (CadPageSetupSnapshot pageSetup in catalog.Setups.Span)
            {
                CadPageSetupPrintOptionsResult lowering =
                    loweringCompiler.Compile(pageSetup, loweringOptions);
                var choice = PageSetupChoice.Create(pageSetup, lowering);
                ComboBoxItem item = CreatePageSetupItem(choice);
                _pageSetupSelector.Items.Add(item);
                if (previousKey == choice.Key)
                {
                    preservedItem = item;
                }
                if (preferredItem is null &&
                    lowering.IsSupported &&
                    pageSetup.SourceKind == CadPageSetupSourceKind.Layout &&
                    pageSetup.TargetSpace == CadPageTargetSpace.Model)
                {
                    preferredItem = item;
                }
            }

            _pageSetupSelector.SelectedItem =
                preservedItem ?? preferredItem ?? fallbackItem;
        }
        catch (Exception exception)
        {
            var fallback = PageSetupChoice.CreateFallback();
            var fallbackItem = CreatePageSetupItem(fallback);
            _pageSetupSelector.Items.Clear();
            _pageSetupSelector.Items.Add(fallbackItem);
            _pageSetupSelector.SelectedItem = fallbackItem;
            SetStatus($"Page setup discovery failed: {exception.Message}");
        }
        finally
        {
            _isRefreshingPageSetups = false;
            RefreshPageSetupFieldControls();
        }
    }

    private static ComboBoxItem CreatePageSetupItem(PageSetupChoice choice)
    {
        var item = new ComboBoxItem(choice.DisplayName)
        {
            Tag = choice,
            Content = new TextBlock
            {
                Text = choice.DisplayName,
                Font = InterFontFamily.Regular,
                FontSize = 11,
                Foreground = new ThemeResourceBrush("TextPrimary"),
            },
        };
        return item;
    }

    private void SelectPageSetup(PageSetupKey key)
    {
        ComboBoxItem? item = _pageSetupSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(candidate =>
                candidate.Tag is PageSetupChoice choice && choice.Key == key);
        if (item is not null)
        {
            _pageSetupSelector.SelectedItem = item;
        }
    }

    private void ShowPlanView(bool clearPreview)
    {
        _is3DView = false;
        _isPrintPreview = false;
        _canvas.Visibility = Visibility.Visible;
        _viewport3D.Visibility = Visibility.Collapsed;
        _meshSubobjectOverlay.Visibility = Visibility.Collapsed;
        _printPreview.Visibility = Visibility.Collapsed;
        _viewModeText.Text = "3D surfaces";
        _printPreviewText.Text = "Print preview";
        if (clearPreview)
        {
            _printPreview.Clear();
        }
    }

    private void AddAttributeDisplayChoice(
        string label,
        AttributeVisibilityMode mode)
    {
        _attributeDisplaySelector.Items.Add(new ComboBoxItem(label)
        {
            Tag = mode,
        });
    }

    private void AddMeshSubobjectChoice(
        string label,
        CadMesh3DSubobjectFilter filter)
    {
        _meshSubobjectSelector.Items.Add(new ComboBoxItem(label)
        {
            Tag = filter,
        });
    }

    private void OnAttributeDisplaySelectionChanged()
    {
        if (_isRefreshingAttributeDisplay || _isBusy || _isPrintPreview ||
            _canvas.PendingDrawOrderPlacement is not null ||
            (_attributeDisplaySelector.SelectedItem as ComboBoxItem)?.Tag is not
                AttributeVisibilityMode mode)
        {
            return;
        }

        try
        {
            if (_canvas.SetAttributeDisplayMode(mode))
            {
                SetStatus(
                    $"Set ATTMODE to {DescribeAttributeDisplayMode(mode)} as one edit.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Set ATTMODE failed: {exception.Message}");
        }
        finally
        {
            RefreshAttributeDisplayMode();
            UpdateEditControls();
        }
    }

    private void RefreshAttributeDisplayMode()
    {
        _isRefreshingAttributeDisplay = true;
        try
        {
            AttributeVisibilityMode mode = _canvas.AttributeDisplayMode;
            _attributeDisplaySelector.SelectedItem =
                _attributeDisplaySelector.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item =>
                        item.Tag is AttributeVisibilityMode candidate &&
                        candidate == mode);
        }
        finally
        {
            _isRefreshingAttributeDisplay = false;
        }
    }

    private static string DescribeAttributeDisplayMode(
        AttributeVisibilityMode mode) => mode switch
    {
        AttributeVisibilityMode.None => "Off",
        AttributeVisibilityMode.Normal => "Normal",
        AttributeVisibilityMode.All => "On",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private void RefreshPlanGridDisplayControls()
    {
        _isRefreshingPlanGridDisplay = true;
        try
        {
            CadPlanGridDisplayEditValues values =
                _canvas.GetPlanGridDisplayEditValues();
            _planGridDisplayCheckBox.IsChecked = values.IsVisible;
            _planSnapUnitXInput.Text = values.SnapUnitX.ToString(
                "G17",
                CultureInfo.InvariantCulture);
            _planSnapUnitYInput.Text = values.SnapUnitY.ToString(
                "G17",
                CultureInfo.InvariantCulture);
            _planGridUnitXInput.Text = values.GridUnitX.ToString(
                "G17",
                CultureInfo.InvariantCulture);
            _planGridUnitYInput.Text = values.GridUnitY.ToString(
                "G17",
                CultureInfo.InvariantCulture);
            _planGridAdaptiveCheckBox.IsChecked = values.IsAdaptive;
            _planGridSubdivisionCheckBox.IsChecked =
                values.AllowsSubdivision;
            _planGridBeyondLimitsCheckBox.IsChecked =
                values.ShowsBeyondLimits;
            _planGridMajorInput.Text =
                values.MinorLinesPerMajorLine.ToString(
                    CultureInfo.InvariantCulture);
            _planGridIsometricCheckBox.IsChecked =
                values.Style == CadPlanGridSnapStyle.Isometric;
            _planGridIsoplaneSelector.SelectedItem =
                _planGridIsoplaneSelector.Items
                    .OfType<ComboBoxItem>()
                    .First(item => item.Tag is CadPlanIsoplane candidate &&
                        candidate == values.Isoplane);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException)
        {
            _planGridDisplayCheckBox.IsChecked = false;
            _planSnapUnitXInput.Text = string.Empty;
            _planSnapUnitYInput.Text = string.Empty;
            _planGridUnitXInput.Text = string.Empty;
            _planGridUnitYInput.Text = string.Empty;
            _planGridAdaptiveCheckBox.IsChecked = false;
            _planGridSubdivisionCheckBox.IsChecked = false;
            _planGridBeyondLimitsCheckBox.IsChecked = false;
            _planGridMajorInput.Text = string.Empty;
            _planGridIsometricCheckBox.IsChecked = false;
            _planGridIsoplaneSelector.SelectedIndex = -1;
        }
        finally
        {
            _isRefreshingPlanGridDisplay = false;
        }
    }

    private void UpdatePlanGridDisplayEditControls()
    {
        if (!_isRefreshingPlanGridDisplay)
        {
            UpdateEditControls();
        }
    }

    private void ApplyPlanGridDisplaySettings()
    {
        if (_isRefreshingPlanGridDisplay ||
            !TryCreatePlanGridDisplayEditValues(out var values))
        {
            return;
        }

        try
        {
            _canvas.EditPlanGridDisplay(values);
            SetStatus(
                $"Updated active VPORT grid display: " +
                $"GRIDMODE={(values.IsVisible ? 1 : 0)}, " +
                $"SNAPSTYL={(values.Style == CadPlanGridSnapStyle.Isometric ? 1 : 0)}, " +
                $"SNAPISOPAIR={(int)values.Isoplane}, " +
                $"SNAPUNIT={values.SnapUnitX.ToString("G17", CultureInfo.InvariantCulture)}," +
                $"{values.SnapUnitY.ToString("G17", CultureInfo.InvariantCulture)}, " +
                $"GRIDUNIT={values.GridUnitX.ToString("G17", CultureInfo.InvariantCulture)}," +
                $"{values.GridUnitY.ToString("G17", CultureInfo.InvariantCulture)}, " +
                $"GRIDMAJOR={values.MinorLinesPerMajorLine}.");
        }
        catch (Exception exception)
        {
            SetStatus($"Update drafting grid failed: {exception.Message}");
            RefreshPlanGridDisplayControls();
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private bool TryCreatePlanGridDisplayEditValues(
        out CadPlanGridDisplayEditValues values)
    {
        values = default;
        if (!TryParsePositiveInvariantDouble(
                _planSnapUnitXInput.Text,
                out double snapUnitX) ||
            !TryParsePositiveInvariantDouble(
                _planSnapUnitYInput.Text,
                out double snapUnitY) ||
            !TryParseNonNegativeInvariantDouble(
                _planGridUnitXInput.Text,
                out double gridUnitX) ||
            !TryParseNonNegativeInvariantDouble(
                _planGridUnitYInput.Text,
                out double gridUnitY) ||
            !int.TryParse(
                _planGridMajorInput.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int cadence) ||
            cadence is < 1 or > 100)
        {
            return false;
        }
        if ((_planGridIsoplaneSelector.SelectedItem as ComboBoxItem)?.Tag is not
            CadPlanIsoplane isoplane)
        {
            return false;
        }
        bool isIsometric = _planGridIsometricCheckBox.IsChecked;
        if (isIsometric &&
            (snapUnitX != snapUnitY || gridUnitX != gridUnitY))
        {
            return false;
        }

        values = new CadPlanGridDisplayEditValues(
            _planGridDisplayCheckBox.IsChecked,
            snapUnitX,
            snapUnitY,
            gridUnitX,
            gridUnitY,
            _planGridAdaptiveCheckBox.IsChecked,
            _planGridSubdivisionCheckBox.IsChecked,
            _planGridBeyondLimitsCheckBox.IsChecked,
            cadence,
            isIsometric
                ? CadPlanGridSnapStyle.Isometric
                : CadPlanGridSnapStyle.Rectangular,
            isoplane);
        return true;
    }

    private void RebuildMesh3DView(bool resetCamera)
    {
        _viewport3D.Children.Clear();
        _meshMaterialBindings.Clear();
        _lastMeshSelection = null;
        _lastMeshSubobjectSelection = null;
        _selectedMeshSubobjects.Clear();
        ResetMeshSelectionCycle();
        ResetMeshSubobjectCycle();
        CadDocumentSnapshot? snapshot = _canvas.CurrentSnapshot;
        if (snapshot is null)
        {
            _viewport3D.InvalidateScene();
            RefreshMeshSubobjectOverlay();
            SetMeshViewAvailability(false);
            return;
        }

        CadRecordedMesh3DScene scene = _mesh3DView.ReplaceSnapshot(
            snapshot,
            resetCamera);
        int semanticRootCount = _mesh3DView.SelectionIndex!.SemanticRootCount;
        if (_meshRegionRootScratch.Length < semanticRootCount)
        {
            _meshRegionRootScratch = new int[semanticRootCount];
        }
        if (_meshRegionHandles.Length < semanticRootCount)
        {
            _meshRegionHandles = new ulong[semanticRootCount];
        }
        int subobjectCount = _mesh3DView.SelectionIndex!.SubobjectCount;
        if (_meshSubobjectPrimitiveScratch.Length < subobjectCount)
        {
            _meshSubobjectPrimitiveScratch = new int[subobjectCount];
        }
        foreach (CadMesh3DDrawBatch batch in scene.DrawBatches.Span)
        {
            uint[] sourceIndices = batch.Indices.ToArray();
            var indices = new int[sourceIndices.Length];
            for (int i = 0; i < sourceIndices.Length; i++)
            {
                indices[i] = checked((int)sourceIndices[i]);
            }
            CadColor32 color = batch.Color;
            var material = new DiffuseMaterial
            {
                Color = new System.Numerics.Vector4(
                    color.Red / 255.0f,
                    color.Green / 255.0f,
                    color.Blue / 255.0f,
                    color.Alpha / 255.0f),
                AmbientColor = new System.Numerics.Vector3(0.2f),
                SpecularColor = new System.Numerics.Vector3(0.15f),
                Shininess = 16.0f,
            };
            _meshMaterialBindings.Add(new MeshMaterialBinding(
                batch.Handle,
                material,
                material.Brush,
                material.Color));
            var geometry = new MeshGeometry3D
            {
                Positions = batch.Positions.ToArray(),
                Normals = batch.Normals.ToArray(),
                TextureCoordinates = batch.TextureCoordinates.ToArray(),
                TriangleIndices = indices,
            };
            _viewport3D.Children.Add(new ModelVisual3D
            {
                Content = new GeometryModel3D
                {
                    Geometry = geometry,
                    Material = material,
                    BackMaterial = material,
                },
            });
        }

        RefreshMeshSelectionMaterials(invalidate: false);

        _viewport3D.InvalidateScene();

        bool hasMeshes = scene.DrawBatches.Length != 0;
        SetMeshViewAvailability(hasMeshes);
        if (!hasMeshes)
        {
            return;
        }
        ApplyMeshCamera(_mesh3DView.Viewport!.Value);
        RefreshMeshSubobjectOverlay();
        _viewport3D.Invalidate();
    }

    private void OnMeshSelectionDragStarting(
        object? sender,
        Viewport3DSelectionDragStartingEventArgs args)
    {
        if (!_is3DView || _isBusy || _mesh3DView.SelectionIndex is null)
        {
            return;
        }
        CadMesh3DSelectionResult origin =
            _mesh3DView.QuerySelectionAperture(
                _viewport3D.Size,
                args.Origin,
                _meshPickTargetHeight);
        args.UseRegionSelection = !origin.IsHit;
        if (args.UseRegionSelection)
        {
            ResetMeshSelectionCycle();
        }
    }

    private void OnMeshRegionSelectionCompleted(
        object? sender,
        Viewport3DRegionSelectionEventArgs args)
    {
        if (!_is3DView || _isBusy || _mesh3DView.SelectionIndex is null)
        {
            return;
        }
        CadBoundsSelectionMode mode = args.IsWindow
            ? CadBoundsSelectionMode.Window
            : CadBoundsSelectionMode.Crossing;
        if (args.WasTruncated)
        {
            SetStatus(
                $"3D lasso exceeded {Viewport3D.MaximumLassoPointCount:N0} sampled points; selection was not changed.");
            return;
        }

        if (_meshSubobjectFilter != CadMesh3DSubobjectFilter.None)
        {
            HandleMeshSubobjectRegionSelection(args);
            return;
        }

        CadMesh3DRegionQueryResult query;
        string selectionName;
        if (!args.IsLasso)
        {
            query = _mesh3DView.QuerySelectionRegion(
                _viewport3D.Size,
                args.Origin,
                args.Position,
                mode,
                _meshRegionRootScratch,
                _meshRegionHandles);
            selectionName = mode.ToString();
        }
        else if (args.Mode == Viewport3DRegionSelectionMode.Fence)
        {
            query = _mesh3DView.QuerySelectionFence(
                _viewport3D.Size,
                args.Points,
                _meshRegionRootScratch,
                _meshRegionHandles);
            mode = CadBoundsSelectionMode.Crossing;
            selectionName = "Fence lasso";
        }
        else
        {
            if (args.Points.Length < 3)
            {
                SetStatus(
                    "3D Window/Crossing lasso requires at least three sampled points; selection was not changed.");
                return;
            }
            try
            {
                query = _mesh3DView.QuerySelectionLasso(
                    _viewport3D.Size,
                    args.Points,
                    mode,
                    _meshRegionRootScratch,
                    _meshRegionHandles);
            }
            catch (ArgumentException exception)
            {
                SetStatus(
                    $"3D lasso was not applied: {exception.Message}");
                return;
            }
            selectionName = $"Lasso {mode}";
        }
        if (!_canvas.SelectSemanticHandles(
                query.ContentGeneration,
                _meshRegionHandles.AsSpan(0, query.HandleWrittenCount),
                args.IsControlPressed,
                mode))
        {
            throw new InvalidOperationException(
                "The projected Mesh3D region contains a stale semantic root.");
        }
        if (!args.IsControlPressed)
        {
            ClearMeshSubobjectSelection();
        }
        _lastMeshSelection = null;
        SetStatus(
            $"3D {selectionName} {(args.IsControlPressed ? "toggled" : "selected")} " +
            $"{query.HandleWrittenCount:N0} semantic roots; " +
            $"tested {query.TestedTriangleCount:N0} triangles in " +
            $"{query.VisitedNodeCount:N0} BVH nodes" +
            (query.AreHandlesTruncated ? "; results truncated." : "."));
    }

    private void HandleMeshSubobjectRegionSelection(
        Viewport3DRegionSelectionEventArgs args)
    {
        CadBoundsSelectionMode mode = args.IsWindow
            ? CadBoundsSelectionMode.Window
            : CadBoundsSelectionMode.Crossing;
        CadMesh3DSubobjectRegionQueryResult query;
        string selectionName;
        if (!args.IsLasso)
        {
            query = _mesh3DView.QuerySubobjectRegion(
                _viewport3D.Size,
                args.Origin,
                args.Position,
                mode,
                _meshSubobjectFilter,
                _meshSubobjectPrimitiveScratch,
                _meshSubobjectRegionHits);
            selectionName = mode.ToString();
        }
        else if (args.Mode == Viewport3DRegionSelectionMode.Fence)
        {
            query = _mesh3DView.QuerySubobjectFence(
                _viewport3D.Size,
                args.Points,
                _meshSubobjectFilter,
                _meshSubobjectPrimitiveScratch,
                _meshSubobjectRegionHits);
            mode = CadBoundsSelectionMode.Crossing;
            selectionName = "Fence lasso";
        }
        else
        {
            if (args.Points.Length < 3)
            {
                SetStatus(
                    "3D subobject Window/Crossing lasso requires at least three sampled points; selection was not changed.");
                return;
            }
            try
            {
                query = _mesh3DView.QuerySubobjectLasso(
                    _viewport3D.Size,
                    args.Points,
                    mode,
                    _meshSubobjectFilter,
                    _meshSubobjectPrimitiveScratch,
                    _meshSubobjectRegionHits);
            }
            catch (ArgumentException exception)
            {
                SetStatus(
                    $"3D subobject lasso was not applied: {exception.Message}");
                return;
            }
            selectionName = $"Lasso {mode}";
        }
        if (query.ContentGeneration != _mesh3DView.Scene?.ContentGeneration)
        {
            throw new InvalidOperationException(
                "The projected Mesh3D subobject region contains a stale generation.");
        }

        if (!args.IsControlPressed)
        {
            _selectedMeshSubobjects.Clear();
            _canvas.ClearSelection();
        }
        int addedCount = 0;
        for (int index = 0; index < query.SubobjectWrittenCount; index++)
        {
            CadMesh3DSubobjectId id = _meshSubobjectRegionHits[index];
            if (_selectedMeshSubobjects.Contains(id))
            {
                continue;
            }
            if (_selectedMeshSubobjects.Count >=
                CadMesh3DSubobjectOverlay.MaximumSelectionCount)
            {
                break;
            }
            _selectedMeshSubobjects.Add(id);
            addedCount++;
        }
        _lastMeshSelection = null;
        _lastMeshSubobjectSelection = null;
        ResetMeshSelectionCycle();
        ResetMeshSubobjectCycle();
        RefreshMeshSubobjectOverlay();
        UpdateEditControls();
        SetStatus(
            $"3D subobject {selectionName} " +
            $"{(args.IsControlPressed ? "added" : "selected")} " +
            $"{query.SubobjectWrittenCount:N0} {_meshSubobjectFilter} entries" +
            (args.IsControlPressed ? $" ({addedCount:N0} new)" : string.Empty) +
            $"; tested {query.TestedTriangleCount:N0} triangles in " +
            $"{query.VisitedNodeCount:N0} BVH nodes" +
            (query.AreSubobjectsTruncated
                ? $"; {query.SubobjectTotalCount:N0} total entries truncated to the {CadMesh3DSubobjectOverlay.MaximumSelectionCount:N0}-entry overlay bound."
                : "."));
    }

    private void OnMeshViewportClicked(
        object? sender,
        Viewport3DClickEventArgs args)
    {
        if (!_is3DView || _isBusy || _mesh3DView.SelectionIndex is null)
        {
            return;
        }

        if (!args.IsAltPressed &&
            (_meshSubobjectFilter != CadMesh3DSubobjectFilter.None ||
             args.IsControlPressed) &&
            TryHandleMeshSubobjectClick(args))
        {
            return;
        }

        CadMesh3DSelectionHitQueryResult cycleQuery = default;
        CadMesh3DSelectionResult result;
        if (args.IsAltPressed)
        {
            CadMesh3DViewport viewport = _mesh3DView.Viewport!.Value;
            cycleQuery = _mesh3DView.QuerySelectionApertureHits(
                _viewport3D.Size,
                args.Position,
                _meshSelectionHits,
                _meshPickTargetHeight);
            bool continueCycle =
                cycleQuery.HitCount > 0 &&
                _meshSelectionCycleGeneration == cycleQuery.ContentGeneration &&
                _meshSelectionCycleViewport == viewport &&
                System.Numerics.Vector2.DistanceSquared(
                    _meshSelectionCyclePoint,
                    args.Position) <=
                MeshSelectionCyclePointTolerance *
                MeshSelectionCyclePointTolerance;
            _meshSelectionCycleIndex = cycleQuery.HitCount == 0
                ? -1
                : continueCycle
                    ? (_meshSelectionCycleIndex + 1) % cycleQuery.HitCount
                    : 0;
            _meshSelectionCycleGeneration = cycleQuery.ContentGeneration;
            _meshSelectionCycleViewport = viewport;
            if (!continueCycle)
            {
                _meshSelectionCyclePoint = args.Position;
            }
            result = _meshSelectionCycleIndex < 0
                ? new CadMesh3DSelectionResult(
                    false,
                    cycleQuery.ContentGeneration,
                    0,
                    -1,
                    -1,
                    default,
                    double.PositiveInfinity,
                    default,
                    false,
                    cycleQuery.VisitedNodeCount,
                    cycleQuery.TestedTriangleCount)
                : _meshSelectionHits[_meshSelectionCycleIndex];
        }
        else
        {
            ResetMeshSelectionCycle();
            result = _mesh3DView.QuerySelectionAperture(
                _viewport3D.Size,
                args.Position,
                _meshPickTargetHeight);
        }
        _lastMeshSelection = result;
        if (result.IsHit)
        {
            if (!args.IsControlPressed)
            {
                ClearMeshSubobjectSelection();
            }
            if (!_canvas.SelectSemanticHandle(
                    result.Handle,
                    toggle: args.IsControlPressed))
            {
                throw new InvalidOperationException(
                    "The retained Mesh3D hit does not identify the active CAD generation.");
            }
            SetStatus(
                $"3D selected {result.Handle:X}" +
                (args.IsAltPressed
                    ? $" (depth {_meshSelectionCycleIndex + 1}/{cycleQuery.HitCount}" +
                      (cycleQuery.WasTruncated ? "+" : string.Empty) + ")"
                    : string.Empty) +
                " at " +
                $"({result.Point.X:G8}, {result.Point.Y:G8}, {result.Point.Z:G8}); " +
                $"tested {result.TestedTriangleCount:N0} triangles in " +
                $"{result.VisitedNodeCount:N0} BVH nodes.");
        }
        else if (!args.IsControlPressed)
        {
            _canvas.ClearSelection();
            ClearMeshSubobjectSelection();
            SetStatus("3D selection cleared.");
        }
    }

    private bool TryHandleMeshSubobjectClick(Viewport3DClickEventArgs args)
    {
        CadMesh3DSubobjectFilter filter = _meshSubobjectFilter ==
            CadMesh3DSubobjectFilter.None
                ? CadMesh3DSubobjectFilter.All
                : _meshSubobjectFilter;
        CadMesh3DSubobjectQueryResult query = _mesh3DView.QuerySubobjects(
            _viewport3D.Size,
            args.Position,
            filter,
            _meshSubobjectHits,
            _meshPickTargetHeight);
        if (query.HitCount == 0)
        {
            _lastMeshSubobjectSelection = null;
            ResetMeshSubobjectCycle();
            if (_meshSubobjectFilter == CadMesh3DSubobjectFilter.None)
            {
                return false;
            }
            if (!args.IsControlPressed && !args.IsShiftPressed)
            {
                _canvas.ClearSelection();
                ClearMeshSubobjectSelection();
                SetStatus("3D subobject selection cleared.");
            }
            return true;
        }

        int selectedHitIndex = IsContinuingMeshSubobjectCycle(args.Position)
            ? Math.Clamp(_meshSubobjectCycleIndex, 0, query.HitCount - 1)
            : 0;
        CadMesh3DSubobjectSelectionResult hit =
            _meshSubobjectHits[selectedHitIndex];
        _lastMeshSubobjectSelection = hit;
        if (args.IsShiftPressed)
        {
            int existing = _selectedMeshSubobjects.IndexOf(hit.Id);
            if (existing >= 0)
            {
                _selectedMeshSubobjects.RemoveAt(existing);
            }
        }
        else
        {
            if (!args.IsControlPressed)
            {
                _selectedMeshSubobjects.Clear();
                _canvas.ClearSelection();
            }
            if (!_selectedMeshSubobjects.Contains(hit.Id))
            {
                if (_selectedMeshSubobjects.Count >=
                    CadMesh3DSubobjectOverlay.MaximumSelectionCount)
                {
                    SetStatus(
                        $"3D subobject selection is bounded to {CadMesh3DSubobjectOverlay.MaximumSelectionCount:N0} entries; selection was not changed.");
                    ResetMeshSubobjectCycle();
                    RefreshMeshSubobjectOverlay();
                    return true;
                }
                _selectedMeshSubobjects.Add(hit.Id);
            }
        }
        ResetMeshSubobjectCycle();
        RefreshMeshSubobjectOverlay();
        UpdateEditControls();
        SetStatus(
            $"3D {(args.IsShiftPressed ? "removed" : "selected")} " +
            $"{hit.Id.Kind} {hit.Id.Index + 1:N0} on {hit.Id.Handle:X}; " +
            $"tested {query.TestedTriangleCount:N0} triangles in " +
            $"{query.VisitedNodeCount:N0} BVH nodes" +
            (query.WasTruncated ? "; candidates truncated." : "."));
        return true;
    }

    private void OnMeshSubobjectCycleRequested(
        object? sender,
        Viewport3DSubobjectCycleEventArgs args)
    {
        if (!_is3DView || _isBusy || _mesh3DView.SelectionIndex is null ||
            _mesh3DView.Viewport is null)
        {
            return;
        }
        CadMesh3DSubobjectFilter filter = _meshSubobjectFilter ==
            CadMesh3DSubobjectFilter.None
                ? CadMesh3DSubobjectFilter.All
                : _meshSubobjectFilter;
        CadMesh3DSubobjectQueryResult query = _mesh3DView.QuerySubobjects(
            _viewport3D.Size,
            args.Position,
            filter,
            _meshSubobjectHits,
            _meshPickTargetHeight);
        bool continueCycle = query.HitCount > 0 &&
            _meshSubobjectCycleGeneration == query.ContentGeneration &&
            System.Numerics.Vector2.DistanceSquared(
                _meshSubobjectCyclePoint,
                args.Position) <=
            MeshSelectionCyclePointTolerance *
            MeshSelectionCyclePointTolerance;
        _meshSubobjectCycleIndex = query.HitCount == 0
            ? -1
            : continueCycle
                ? (_meshSubobjectCycleIndex + 1) % query.HitCount
                : 0;
        _meshSubobjectCycleGeneration = query.ContentGeneration;
        _meshSubobjectCyclePoint = args.Position;
        _meshSubobjectCycleHitCount = query.HitCount;
        _lastMeshSubobjectSelection = _meshSubobjectCycleIndex < 0
            ? null
            : _meshSubobjectHits[_meshSubobjectCycleIndex];
        RefreshMeshSubobjectOverlay();
        if (_lastMeshSubobjectSelection is
            CadMesh3DSubobjectSelectionResult candidate)
        {
            SetStatus(
                $"3D subobject candidate {candidate.Id.Kind} " +
                $"{candidate.Id.Index + 1:N0} on {candidate.Id.Handle:X} " +
                $"({_meshSubobjectCycleIndex + 1:N0}/{query.HitCount:N0}" +
                (query.WasTruncated ? "+" : string.Empty) +
                "); click to select.");
        }
        else
        {
            SetStatus("No modern MESH subobject is inside the pickbox.");
        }
    }

    private bool IsContinuingMeshSubobjectCycle(
        System.Numerics.Vector2 point) =>
        _meshSubobjectCycleIndex >= 0 &&
        _meshSubobjectCycleIndex < _meshSubobjectCycleHitCount &&
        _meshSubobjectCycleGeneration ==
            _mesh3DView.Scene?.ContentGeneration &&
        System.Numerics.Vector2.DistanceSquared(
            _meshSubobjectCyclePoint,
            point) <=
        MeshSelectionCyclePointTolerance * MeshSelectionCyclePointTolerance;

    private void ResetMeshSelectionCycle()
    {
        _meshSelectionCycleViewport = null;
        _meshSelectionCyclePoint = default;
        _meshSelectionCycleGeneration = 0;
        _meshSelectionCycleIndex = -1;
    }

    private void ResetMeshSubobjectCycle()
    {
        _meshSubobjectCyclePoint = default;
        _meshSubobjectCycleGeneration = 0;
        _meshSubobjectCycleHitCount = 0;
        _meshSubobjectCycleIndex = -1;
    }

    private void ClearMeshSubobjectSelection()
    {
        _selectedMeshSubobjects.Clear();
        _lastMeshSubobjectSelection = null;
        ResetMeshSubobjectCycle();
        RefreshMeshSubobjectOverlay();
        UpdateEditControls();
    }

    private void RefreshMeshSubobjectOverlay()
    {
        CadMesh3DSubobjectId? candidate =
            _meshSubobjectCycleIndex >= 0 &&
            _lastMeshSubobjectSelection is
                CadMesh3DSubobjectSelectionResult selection
                    ? selection.Id
                    : null;
        _meshSubobjectOverlay.Update(
            _mesh3DView.Scene,
            _mesh3DView.Viewport,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(
                _selectedMeshSubobjects),
            candidate);
    }

    private void RefreshMeshSelectionMaterials(bool invalidate = true)
    {
        bool changed = false;
        for (int index = 0; index < _meshMaterialBindings.Count; index++)
        {
            MeshMaterialBinding binding = _meshMaterialBindings[index];
            bool selected = _canvas.IsSemanticHandleSelected(binding.Handle);
            if (binding.IsSelected == selected)
            {
                continue;
            }

            binding.IsSelected = selected;
            binding.Material.Brush = selected
                ? _meshSelectionBrush
                : binding.AuthoredBrush;
            binding.Material.Color = selected
                ? new System.Numerics.Vector4(
                    1.0f,
                    1.0f,
                    1.0f,
                    binding.AuthoredColor.W)
                : binding.AuthoredColor;
            changed = true;
        }
        if (changed && invalidate)
        {
            _viewport3D.InvalidateScene();
        }
    }

    private void FitMesh3DView()
    {
        if (_mesh3DView.Scene?.DrawBatches.IsEmpty != false)
        {
            return;
        }

        ApplyMeshCamera(_mesh3DView.FitCamera());
        _viewport3D.Invalidate();
    }

    private void ApplyMeshCamera(CadMesh3DViewport viewport)
    {
        CadMesh3DProjectionCamera state = viewport.CreateProjectionCamera();
        var camera = new PerspectiveCamera
        {
            Position = state.Position,
            LookDirection = state.LookDirection,
            UpDirection = state.UpDirection,
            NearPlaneDistance = state.NearPlaneDistance,
            FarPlaneDistance = state.FarPlaneDistance,
            FieldOfView = state.FieldOfView,
        };

        if (_observedMeshCamera is not null)
        {
            _observedMeshCamera.Changed -= OnMeshCameraChanged;
        }
        _observedMeshCamera = camera;
        _observedMeshCamera.Changed += OnMeshCameraChanged;
        _viewport3D.Camera = camera;
        RefreshMeshSubobjectOverlay();
    }

    private void OnMeshCameraChanged(object? sender, EventArgs args)
    {
        if (sender is not PerspectiveCamera camera ||
            _mesh3DView.Viewport is null)
        {
            return;
        }

        var state = new CadMesh3DProjectionCamera(
            camera.Position,
            camera.LookDirection,
            camera.UpDirection,
            camera.NearPlaneDistance,
            camera.FarPlaneDistance,
            camera.FieldOfView);
        _mesh3DView.CaptureCamera(state);
        ResetMeshSubobjectCycle();
        RefreshMeshSubobjectOverlay();
    }

    private void SetMeshViewAvailability(bool isAvailable)
    {
        _viewModeButton.IsEnabled =
            !_isBusy && !_isPrintPreview &&
            _canvas.PendingDrawOrderPlacement is null &&
            isAvailable;
        if (isAvailable || !_is3DView)
        {
            return;
        }
        _is3DView = false;
        _canvas.Visibility = Visibility.Visible;
        _viewport3D.Visibility = Visibility.Collapsed;
        _meshSubobjectOverlay.Visibility = Visibility.Collapsed;
        _viewModeText.Text = "3D surfaces";
    }

    private readonly record struct MeshSubobjectEditSelection(
        ulong SourceHandle,
        CadMesh3DSubobjectKind Kind,
        int Index);

    private sealed class MeshMaterialBinding
    {
        internal ulong Handle { get; }
        internal DiffuseMaterial Material { get; }
        internal Brush AuthoredBrush { get; }
        internal System.Numerics.Vector4 AuthoredColor { get; }
        internal bool IsSelected { get; set; }

        internal MeshMaterialBinding(
            ulong handle,
            DiffuseMaterial material,
            Brush authoredBrush,
            System.Numerics.Vector4 authoredColor)
        {
            Handle = handle;
            Material = material;
            AuthoredBrush = authoredBrush;
            AuthoredColor = authoredColor;
        }
    }

    private void RefreshSelectionPropertyControls()
    {
        _isRefreshingSelectionProperties = true;
        try
        {
            RefreshSelectionPropertyCatalog();
            CadSelectionGeneralProperties properties =
                _canvas.CaptureSelectionGeneralProperties();
            _selectionColorInput.Text = properties.SelectionCount == 0
                ? string.Empty
                : properties.CommonColor is ACadSharp.Color color
                    ? FormatSelectionColor(color)
                    : "*VARIES*";
            if (properties.SelectionCount == 0)
            {
                _selectionLineWeightSelector.SelectedIndex = 0;
            }
            else if (properties.CommonLineWeight is null)
            {
                _selectionLineWeightSelector.SelectedIndex = 1;
            }
            else if (properties.CommonLineWeight ==
                ACadSharp.LineWeightType.ByDIPs)
            {
                _selectionLineWeightSelector.SelectedIndex = 2;
            }
            else
            {
                _selectionLineWeightSelector.SelectedItem =
                    _selectionLineWeightSelector.Items
                        .OfType<ComboBoxItem>()
                        .First(item => item.Tag is ACadSharp.LineWeightType value &&
                            value == properties.CommonLineWeight.Value);
            }
            SelectNamedPropertyChoice(
                _selectionLayerSelector,
                properties.SelectionCount,
                properties.CommonLayerName);
            SelectNamedPropertyChoice(
                _selectionLineTypeSelector,
                properties.SelectionCount,
                properties.CommonLineTypeName);
            _selectionLineTypeScaleInput.Text = properties.SelectionCount == 0
                ? string.Empty
                : properties.CommonLineTypeScale is double lineTypeScale
                    ? lineTypeScale.ToString("G17", CultureInfo.InvariantCulture)
                    : "*VARIES*";
            _selectionTransparencyInput.Text = properties.SelectionCount == 0
                ? string.Empty
                : properties.CommonTransparency is ACadSharp.Transparency transparency
                    ? FormatTransparency(transparency)
                    : "*VARIES*";
            if (properties.SelectionCount == 0)
            {
                _selectionVisibilitySelector.SelectedIndex = 0;
            }
            else if (properties.CommonIsInvisible is null)
            {
                _selectionVisibilitySelector.SelectedIndex = 1;
            }
            else
            {
                bool isVisible = !properties.CommonIsInvisible.Value;
                _selectionVisibilitySelector.SelectedItem =
                    _selectionVisibilitySelector.Items
                        .OfType<ComboBoxItem>()
                        .First(item => item.Tag is bool value && value == isVisible);
            }
            _isSolidThicknessSelection =
                properties.SelectionCount > 0 &&
                properties.AllSelectedEntitiesAreSolids;
            _isSelectionEditable =
                properties.SelectionCount > 0 &&
                properties.AllSelectedEntitiesAreUnlocked;
            _isMeshSelection =
                properties.SelectionCount > 0 &&
                properties.AllSelectedEntitiesAreMeshes;
            _commonMeshSubdivisionLevel =
                properties.CommonMeshSubdivisionLevel;
            _selectionSolidThicknessInput.Text = properties.SelectionCount == 0
                ? string.Empty
                : !_isSolidThicknessSelection
                    ? "N/A"
                    : properties.CommonSolidThickness is double thickness
                        ? thickness.ToString("G17", CultureInfo.InvariantCulture)
                        : "*VARIES*";
            RefreshSelectionAttributeControls();
            RefreshLayerStateControls();
        }
        finally
        {
            _isRefreshingSelectionProperties = false;
        }
    }

    private void RefreshSelectionAttributeControls()
    {
        CadAttributeValueEntry? previous =
            (_selectionAttributeSelector.SelectedItem as ComboBoxItem)?.Tag is
                CadAttributeValueEntry entry
                ? entry
                : null;
        _selectionAttributeSelector.Items.Clear();
        _selectionAttributeSelector.Items.Add(new ComboBoxItem { Text = "—" });
        _selectionAttributeSelector.SelectedIndex = 0;
        _selectionAttributeValueInput.Text = string.Empty;
        _selectionAttributePromptInput.Text = string.Empty;
        _selectionAttributeTagInput.Text = string.Empty;
        SetAttributeModeChecks(null);

        CadAttributeValueCatalog? catalog;
        try
        {
            catalog = _canvas.CaptureSelectedAttributeValueCatalog();
        }
        catch (Exception exception)
        {
            _selectionAttributeSelector.Items.Add(new ComboBoxItem
            {
                Text = $"Unavailable: {exception.Message}",
            });
            return;
        }
        if (catalog is null)
        {
            return;
        }

        foreach (CadAttributeValueEntry candidate in catalog.Entries.Span)
        {
            string ownership = candidate.Owner switch
            {
                CadAttributeValueOwner.Definition => "constant",
                CadAttributeValueOwner.VariableDefinition => "variable default",
                _ => "reference",
            };
            string multiline = candidate.IsMultiline ? ", multiline" : string.Empty;
            string hidden = candidate.IsInvisible ? ", hidden" : string.Empty;
            var item = new ComboBoxItem
            {
                Text = $"{candidate.Tag} #{candidate.Occurrence + 1} " +
                    $"[{ownership}{multiline}{hidden}]",
                Tag = candidate,
            };
            _selectionAttributeSelector.Items.Add(item);
            if (previous is CadAttributeValueEntry prior &&
                (prior.Owner == candidate.Owner ||
                    (prior.Owner != CadAttributeValueOwner.Reference &&
                        candidate.Owner != CadAttributeValueOwner.Reference)) &&
                prior.Occurrence == candidate.Occurrence &&
                string.Equals(
                    prior.Tag,
                    candidate.Tag,
                    StringComparison.OrdinalIgnoreCase))
            {
                _selectionAttributeSelector.SelectedItem = item;
            }
        }
        if (_selectionAttributeSelector.SelectedIndex == 0 &&
            _selectionAttributeSelector.Items.Count > 1)
        {
            _selectionAttributeSelector.SelectedIndex = 1;
        }
        RefreshSelectedAttributeValue();
    }

    private void RefreshSelectedAttributeValue()
    {
        CadAttributeValueEntry? selected =
            (_selectionAttributeSelector.SelectedItem as ComboBoxItem)?.Tag is
                CadAttributeValueEntry entry
                ? entry
                : null;
        _selectionAttributeValueInput.Text = selected?.Value ?? string.Empty;
        _selectionAttributePromptInput.Text = selected is
            { Owner: not CadAttributeValueOwner.Reference } definition
            ? definition.Prompt
            : string.Empty;
        _selectionAttributeTagInput.Text = selected is
            { Owner: not CadAttributeValueOwner.Reference } definitionTag
            ? definitionTag.Tag
            : string.Empty;
        SetAttributeModeChecks(selected is
            { Owner: not CadAttributeValueOwner.Reference } definitionModes
            ? definitionModes
            : null);
    }

    private void RefreshSelectionPropertyCatalog()
    {
        EnsureLayerMergeSourcesAreCurrent();
        CadDocumentSnapshot? snapshot = _canvas.CurrentSnapshot;
        CadDocumentSession? session = _canvas.CurrentSession;
        if (snapshot is null || session is null ||
            (ReferenceEquals(session, _selectionPropertyCatalogSession) &&
                snapshot.ContentGeneration == _selectionPropertyCatalogGeneration))
        {
            return;
        }

        CadSelectionPropertyCatalog catalog =
            _canvas.CaptureSelectionPropertyCatalog();
        string? previousLayerStateName =
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        string? previousLayerMergeTargetName =
            (_layerMergeTargetSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        PopulateNamedPropertyChoices(
            _selectionLayerSelector,
            catalog.LayerNames.Span);
        PopulateNamedPropertyChoices(
            _selectionLineTypeSelector,
            catalog.LineTypeNames.Span);
        PopulateLayerLineTypeChoices(
            _layerLineTypeSelector,
            catalog.LineTypeNames.Span);
        PopulateLayerStateChoices(
            _layerStateSelector,
            catalog.LayerNames.Span,
            previousLayerStateName);
        PopulateLayerStateChoices(
            _layerMergeTargetSelector,
            catalog.LayerNames.Span,
            previousLayerMergeTargetName);
        _selectionPropertyCatalogSession = session;
        _selectionPropertyCatalogGeneration = catalog.ContentGeneration;
    }

    private void RefreshLayerStateControls()
    {
        if ((_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag is not
            string layerName)
        {
            _layerVisibilitySelector.SelectedIndex = 0;
            _layerPlotSelector.SelectedIndex = 0;
            _layerFreezeSelector.SelectedIndex = 0;
            _layerLockSelector.SelectedIndex = 0;
            _layerColorInput.Text = string.Empty;
            _layerLineWeightSelector.SelectedIndex = 0;
            _layerLineTypeSelector.SelectedIndex = 0;
            _layerNameInput.Text = string.Empty;
            _selectedLayerCanRename = false;
            _selectedLayerCanRemove = false;
            return;
        }

        CadLayerGeneralProperties properties =
            _canvas.CaptureLayerGeneralProperties(layerName);
        SelectBooleanPropertyChoice(
            _layerVisibilitySelector,
            properties.IsOn);
        SelectBooleanPropertyChoice(
            _layerPlotSelector,
            properties.IsPlottable);
        SelectBooleanPropertyChoice(
            _layerFreezeSelector,
            properties.IsFrozen);
        SelectBooleanPropertyChoice(
            _layerLockSelector,
            properties.IsLocked);
        _layerColorInput.Text = FormatSelectionColor(properties.Color);
        _layerLineWeightSelector.SelectedItem =
            _layerLineWeightSelector.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item =>
                    item.Tag is ACadSharp.LineWeightType value &&
                    value == properties.LineWeight);
        if (_layerLineWeightSelector.SelectedItem is null)
        {
            _layerLineWeightSelector.SelectedIndex = 0;
        }
        _layerLineTypeSelector.SelectedItem =
            _layerLineTypeSelector.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is string name &&
                    name.Equals(
                        properties.LineTypeName,
                        StringComparison.OrdinalIgnoreCase));
        if (_layerLineTypeSelector.SelectedItem is null)
        {
            _layerLineTypeSelector.SelectedIndex = 0;
        }
        _layerNameInput.Text = properties.Name;
        _selectedLayerCanRename =
            !properties.IsDefault &&
            !properties.IsXrefDependent;
        _selectedLayerCanRemove =
            !properties.IsDefault &&
            !properties.IsDefpoints &&
            !properties.IsCurrent &&
            !properties.IsXrefDependent;
    }

    private static void SelectBooleanPropertyChoice(
        ComboBox selector,
        bool value) =>
        selector.SelectedItem = selector.Items
            .OfType<ComboBoxItem>()
            .First(item => item.Tag is bool candidate && candidate == value);

    private static void SelectNamedPropertyChoice(
        ComboBox selector,
        int selectionCount,
        string? commonName)
    {
        if (selectionCount == 0)
        {
            selector.SelectedIndex = 0;
            return;
        }
        if (commonName is null)
        {
            selector.SelectedIndex = 1;
            return;
        }

        selector.SelectedItem = selector.Items
            .OfType<ComboBoxItem>()
            .First(item => item.Tag is string name &&
                name.Equals(commonName, StringComparison.OrdinalIgnoreCase));
    }

    private void SetSelectionColor()
    {
        if (_isBusy ||
            !TryParseSelectionColor(
                _selectionColorInput.Text,
                out ACadSharp.Color color))
        {
            return;
        }

        int selectedCount = _canvas.SelectedHandleCount;
        try
        {
            if (!_canvas.SetSelectionColor(color))
            {
                SetStatus("Setting color requires at least one selected entity.");
                return;
            }
            SetStatus(
                $"Set color {FormatSelectionColor(color)} on " +
                $"{selectedCount:N0} selected entity(s) as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set color failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetSelectionLineWeight()
    {
        if (_isBusy ||
            (_selectionLineWeightSelector.SelectedItem as ComboBoxItem)?.Tag is not
                ACadSharp.LineWeightType lineWeight)
        {
            return;
        }

        int selectedCount = _canvas.SelectedHandleCount;
        try
        {
            if (!_canvas.SetSelectionLineWeight(lineWeight))
            {
                SetStatus("Setting lineweight requires at least one selected entity.");
                return;
            }
            SetStatus(
                $"Set lineweight {FormatLineWeight(lineWeight)} on " +
                $"{selectedCount:N0} selected entity(s) as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set lineweight failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetSelectionLayer()
    {
        if (_isBusy ||
            (_selectionLayerSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string layerName)
        {
            return;
        }

        int selectedCount = _canvas.SelectedHandleCount;
        try
        {
            if (!_canvas.SetSelectionLayer(layerName))
            {
                SetStatus("Setting layer requires at least one selected entity.");
                return;
            }
            SetStatus(
                $"Set layer {layerName} on {selectedCount:N0} " +
                "selected entity(s) as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set layer failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetSelectionLineType()
    {
        if (_isBusy ||
            (_selectionLineTypeSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string lineTypeName)
        {
            return;
        }

        int selectedCount = _canvas.SelectedHandleCount;
        try
        {
            if (!_canvas.SetSelectionLineType(lineTypeName))
            {
                SetStatus("Setting linetype requires at least one selected entity.");
                return;
            }
            SetStatus(
                $"Set linetype {lineTypeName} on {selectedCount:N0} " +
                "selected entity(s) as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set linetype failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetSelectionLineTypeScale()
    {
        if (_isBusy ||
            !TryParsePositiveInvariantDouble(
                _selectionLineTypeScaleInput.Text,
                out double lineTypeScale))
        {
            return;
        }

        int selectedCount = _canvas.SelectedHandleCount;
        try
        {
            if (!_canvas.SetSelectionLineTypeScale(lineTypeScale))
            {
                SetStatus(
                    "Setting linetype scale requires at least one selected entity.");
                return;
            }
            SetStatus(
                $"Set linetype scale " +
                $"{lineTypeScale.ToString("G17", CultureInfo.InvariantCulture)} " +
                $"on {selectedCount:N0} selected entity(s) as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set linetype scale failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetSelectionTransparency()
    {
        if (_isBusy ||
            !TryParseTransparency(
                _selectionTransparencyInput.Text,
                out ACadSharp.Transparency transparency))
        {
            return;
        }

        int selectedCount = _canvas.SelectedHandleCount;
        try
        {
            if (!_canvas.SetSelectionTransparency(transparency))
            {
                SetStatus(
                    "Setting transparency requires at least one selected entity.");
                return;
            }
            SetStatus(
                $"Set transparency {FormatTransparency(transparency)} on " +
                $"{selectedCount:N0} selected entity(s) as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set transparency failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetSelectionVisibility()
    {
        if (_isBusy ||
            (_selectionVisibilitySelector.SelectedItem as ComboBoxItem)?.Tag is not
                bool isVisible)
        {
            return;
        }

        int selectedCount = _canvas.SelectedHandleCount;
        try
        {
            if (!_canvas.SetSelectionVisibility(isVisible))
            {
                SetStatus(
                    "Setting visibility requires at least one selected entity.");
                return;
            }
            SetStatus(
                $"Set visibility {(isVisible ? "Visible" : "Hidden")} on " +
                $"{selectedCount:N0} selected entity(s) as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set visibility failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetSelectionSolidThickness()
    {
        if (_isBusy ||
            !_isSolidThicknessSelection ||
            !TryParseFiniteInvariantDouble(
                _selectionSolidThicknessInput.Text,
                out double thickness))
        {
            return;
        }

        int selectedCount = _canvas.SelectedHandleCount;
        try
        {
            if (!_canvas.SetSelectionSolidThickness(thickness))
            {
                SetStatus(
                    "Setting SOLID thickness requires an all-SOLID selection.");
                return;
            }
            SetStatus(
                $"Set SOLID thickness " +
                $"{thickness.ToString("G17", CultureInfo.InvariantCulture)} on " +
                $"{selectedCount:N0} selected entity(s) as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set SOLID thickness failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetSelectionAttributeValue()
    {
        if (_isBusy ||
            (_selectionAttributeSelector.SelectedItem as ComboBoxItem)?.Tag is not
                CadAttributeValueEntry entry ||
            _selectionAttributeValueInput.Text.Length >
                CadSetAttributeValueCommand.MaximumValueCodeUnits)
        {
            return;
        }

        try
        {
            if (!_canvas.SetSelectedAttributeValue(
                    entry.Owner,
                    entry.Tag,
                    entry.Occurrence,
                    _selectionAttributeValueInput.Text))
            {
                SetStatus("Attribute editing requires exactly one selected INSERT.");
                return;
            }
            string ownership = entry.Owner switch
            {
                CadAttributeValueOwner.Definition => "constant definition",
                CadAttributeValueOwner.VariableDefinition => "variable default",
                _ => "reference",
            };
            SetStatus(
                $"Set {ownership} attribute {entry.Tag} " +
                $"#{entry.Occurrence + 1} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set attribute failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetSelectionAttributePrompt()
    {
        if (_isBusy ||
            (_selectionAttributeSelector.SelectedItem as ComboBoxItem)?.Tag is not
                CadAttributeValueEntry
                {
                    Owner: not CadAttributeValueOwner.Reference,
                } entry ||
            _selectionAttributePromptInput.Text.Length >
                CadSetAttributeDefinitionPromptCommand.MaximumPromptCodeUnits)
        {
            return;
        }

        try
        {
            if (!_canvas.SetSelectedAttributeDefinitionPrompt(
                    entry.Tag,
                    entry.Occurrence,
                    _selectionAttributePromptInput.Text))
            {
                SetStatus(
                    "Attribute prompt editing requires exactly one selected INSERT.");
                return;
            }
            SetStatus(
                $"Set definition prompt {entry.Tag} " +
                $"#{entry.Occurrence + 1} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set attribute prompt failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetSelectionAttributeTag()
    {
        if (_isBusy ||
            (_selectionAttributeSelector.SelectedItem as ComboBoxItem)?.Tag is not
                CadAttributeValueEntry
                {
                    Owner: not CadAttributeValueOwner.Reference,
                } entry ||
            !CadSetAttributeDefinitionTagCommand.IsValidNewTag(
                _selectionAttributeTagInput.Text) ||
            string.Equals(
                entry.Tag,
                _selectionAttributeTagInput.Text,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string newTag = _selectionAttributeTagInput.Text.ToUpperInvariant();
        try
        {
            if (!_canvas.SetSelectedAttributeDefinitionTag(
                    entry.Tag,
                    entry.Occurrence,
                    newTag))
            {
                SetStatus(
                    "Attribute tag editing requires exactly one selected INSERT.");
                return;
            }
            SetStatus(
                $"Renamed definition tag {entry.Tag} " +
                $"#{entry.Occurrence + 1} to {newTag} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set attribute tag failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetSelectionAttributeModes()
    {
        if (_isBusy ||
            (_selectionAttributeSelector.SelectedItem as ComboBoxItem)?.Tag is not
                CadAttributeValueEntry
                {
                    Owner: not CadAttributeValueOwner.Reference,
                } entry ||
            !HaveSelectedAttributeModesChanged(entry))
        {
            return;
        }

        try
        {
            if (!_canvas.SetSelectedAttributeDefinitionModes(
                    entry.Tag,
                    entry.Occurrence,
                    _selectionAttributeInvisibleCheckBox.IsChecked,
                    _selectionAttributeVerifyCheckBox.IsChecked,
                    _selectionAttributePresetCheckBox.IsChecked,
                    _selectionAttributePositionLockedCheckBox.IsChecked))
            {
                SetStatus(
                    "Attribute mode editing requires exactly one selected INSERT.");
                return;
            }
            SetStatus(
                $"Set definition modes {entry.Tag} " +
                $"#{entry.Occurrence + 1} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set attribute modes failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void UpdateAttributeModeControls()
    {
        if (!_isRefreshingSelectionProperties)
        {
            UpdateEditControls();
        }
    }

    private void SetAttributeModeChecks(CadAttributeValueEntry? entry)
    {
        _selectionAttributeInvisibleCheckBox.IsChecked =
            entry?.IsInvisible == true;
        _selectionAttributeVerifyCheckBox.IsChecked =
            entry?.IsVerifiable == true;
        _selectionAttributePresetCheckBox.IsChecked =
            entry?.IsPreset == true;
        _selectionAttributePositionLockedCheckBox.IsChecked =
            entry?.IsPositionLocked == true;
        _selectionAttributeConstantCheckBox.IsChecked =
            entry?.Owner == CadAttributeValueOwner.Definition;
    }

    private bool HaveSelectedAttributeModesChanged(
        CadAttributeValueEntry entry) =>
        _selectionAttributeInvisibleCheckBox.IsChecked != entry.IsInvisible ||
        _selectionAttributeVerifyCheckBox.IsChecked != entry.IsVerifiable ||
        _selectionAttributePresetCheckBox.IsChecked != entry.IsPreset ||
        _selectionAttributePositionLockedCheckBox.IsChecked !=
            entry.IsPositionLocked;

    private bool HasSelectedAttributeConstantModeChanged(
        CadAttributeValueEntry entry) =>
        _selectionAttributeConstantCheckBox.IsChecked !=
            (entry.Owner == CadAttributeValueOwner.Definition);

    private void SetSelectionAttributeConstantMode()
    {
        if (_isBusy ||
            (_selectionAttributeSelector.SelectedItem as ComboBoxItem)?.Tag is not
                CadAttributeValueEntry
                {
                    Owner: not CadAttributeValueOwner.Reference,
                } entry ||
            !HasSelectedAttributeConstantModeChanged(entry))
        {
            return;
        }

        bool isConstant = _selectionAttributeConstantCheckBox.IsChecked;
        try
        {
            CadAttributeSynchronizationResult? result =
                _canvas.SetSelectedAttributeDefinitionConstantMode(
                    entry.Tag,
                    entry.Occurrence,
                    isConstant);
            if (result is not CadAttributeSynchronizationResult synchronized)
            {
                SetStatus(
                    "Attribute constant editing requires exactly one selected INSERT.");
                return;
            }
            SetStatus(
                $"Set definition {entry.Tag} #{entry.Occurrence + 1} to " +
                $"{(isConstant ? "constant" : "variable")} and synchronized " +
                $"{synchronized.InsertCount:N0} INSERT(s) as one edit; added " +
                $"{synchronized.AddedAttributeCount:N0}, removed " +
                $"{synchronized.RemovedAttributeCount:N0}, cleared " +
                $"{synchronized.ClearedExtendedDataEntryCount:N0} reference XData " +
                "app payload(s).");
        }
        catch (Exception exception)
        {
            SetStatus($"Set attribute constant mode failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SynchronizeSelectionAttributeProperties()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            CadAttributeSynchronizationResult? result =
                _canvas.SynchronizeSelectedBlockAttributeProperties();
            if (result is not CadAttributeSynchronizationResult synchronized)
            {
                SetStatus(
                    "Attribute synchronization requires exactly one selected INSERT.");
                return;
            }
            SetStatus(
                $"Synchronized {synchronized.AttributeCount:N0} attribute(s) " +
                $"across {synchronized.InsertCount:N0} INSERT(s) as one edit; " +
                $"added {synchronized.AddedAttributeCount:N0}, removed " +
                $"{synchronized.RemovedAttributeCount:N0}, cleared " +
                $"{synchronized.ClearedExtendedDataEntryCount:N0} reference XData " +
                "app payload(s); assigned values were preserved.");
        }
        catch (Exception exception)
        {
            SetStatus($"Sync attribute properties failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetLayerVisibility()
    {
        if (_isBusy ||
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string layerName ||
            (_layerVisibilitySelector.SelectedItem as ComboBoxItem)?.Tag is not
                bool isOn)
        {
            return;
        }

        try
        {
            _canvas.SetLayerVisibility(layerName, isOn);
            SetStatus(
                $"Set layer {layerName} {(isOn ? "On" : "Off")} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set layer visibility failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetLayerPlotFlag()
    {
        if (_isBusy ||
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string layerName ||
            (_layerPlotSelector.SelectedItem as ComboBoxItem)?.Tag is not
                bool isPlottable)
        {
            return;
        }

        try
        {
            _canvas.SetLayerPlotFlag(layerName, isPlottable);
            SetStatus(
                $"Set layer {layerName} " +
                $"{(isPlottable ? "Plot" : "No plot")} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set layer plot failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetLayerFreeze()
    {
        if (_isBusy ||
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string layerName ||
            (_layerFreezeSelector.SelectedItem as ComboBoxItem)?.Tag is not
                bool isFrozen)
        {
            return;
        }

        try
        {
            _canvas.SetLayerFreeze(layerName, isFrozen);
            SetStatus(
                $"Set layer {layerName} " +
                $"{(isFrozen ? "Frozen" : "Thawed")} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set layer freeze failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetLayerLock()
    {
        if (_isBusy ||
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string layerName ||
            (_layerLockSelector.SelectedItem as ComboBoxItem)?.Tag is not
                bool isLocked)
        {
            return;
        }

        try
        {
            _canvas.SetLayerLock(layerName, isLocked);
            SetStatus(
                $"Set layer {layerName} " +
                $"{(isLocked ? "Locked" : "Unlocked")} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set layer lock failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetLayerColor()
    {
        if (_isBusy ||
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string layerName ||
            !TryParseLayerColor(_layerColorInput.Text, out ACadSharp.Color color))
        {
            return;
        }

        try
        {
            _canvas.SetLayerColor(layerName, color);
            SetStatus(
                $"Set layer {layerName} color {FormatSelectionColor(color)} " +
                "as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set layer color failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetLayerLineWeight()
    {
        if (_isBusy ||
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string layerName ||
            (_layerLineWeightSelector.SelectedItem as ComboBoxItem)?.Tag is not
                ACadSharp.LineWeightType lineWeight)
        {
            return;
        }

        try
        {
            _canvas.SetLayerLineWeight(layerName, lineWeight);
            SetStatus(
                $"Set layer {layerName} lineweight " +
                $"{FormatLineWeight(lineWeight)} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set layer lineweight failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetLayerLineType()
    {
        if (_isBusy ||
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string layerName ||
            (_layerLineTypeSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string lineTypeName)
        {
            return;
        }

        try
        {
            _canvas.SetLayerLineType(layerName, lineTypeName);
            SetStatus(
                $"Set layer {layerName} linetype {lineTypeName} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Set layer linetype failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void CreateLayer()
    {
        if (_isBusy ||
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string templateLayerName ||
            !_canvas.CanCreateLayer(_layerNameInput.Text))
        {
            return;
        }

        string layerName = _layerNameInput.Text;
        try
        {
            _canvas.CreateLayer(layerName, templateLayerName);
            SelectLayerStateChoice(layerName);
            SetStatus(
                $"Created layer {layerName} from {templateLayerName} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Create layer failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void RenameLayer()
    {
        if (_isBusy ||
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string layerName ||
            !_canvas.CanRenameLayer(layerName, _layerNameInput.Text))
        {
            return;
        }

        string newName = _layerNameInput.Text;
        try
        {
            _canvas.RenameLayer(layerName, newName);
            SelectLayerStateChoice(newName);
            SetStatus($"Renamed layer {layerName} to {newName} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Rename layer failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void RemoveLayer()
    {
        if (_isBusy ||
            !_selectedLayerCanRemove ||
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string layerName)
        {
            return;
        }

        try
        {
            _canvas.RemoveLayer(layerName);
            SetStatus($"Deleted unused layer {layerName} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Delete layer failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void MergeLayer()
    {
        EnsureLayerMergeSourcesAreCurrent();
        if (_isBusy ||
            _layerMergeSourceNames.Count == 0 ||
            (_layerMergeTargetSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string targetLayerName ||
            !_canvas.CanMergeLayers(_layerMergeSourceNames, targetLayerName))
        {
            return;
        }

        string[] sourceLayerNames = _layerMergeSourceNames.ToArray();
        try
        {
            _canvas.MergeLayers(sourceLayerNames, targetLayerName);
            ClearLayerMergeSources(setStatus: false);
            SelectLayerStateChoice(targetLayerName);
            SetStatus(sourceLayerNames.Length == 1
                ? $"Merged layer {sourceLayerNames[0]} into " +
                    $"{targetLayerName} as one edit."
                : $"Merged {sourceLayerNames.Length} layers into " +
                    $"{targetLayerName} as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Merge layer failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void QueueLayerMergeSource()
    {
        EnsureLayerMergeSourcesAreCurrent();
        if (_isBusy ||
            !_selectedLayerCanRemove ||
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag is not
                string sourceLayerName ||
            !_layerMergeSourceNameSet.Add(sourceLayerName))
        {
            return;
        }

        _layerMergeSourceNames.Add(sourceLayerName);
        _layerMergeSourceCountText.Text =
            $"Sources: {_layerMergeSourceNames.Count:N0}";
        SetStatus(
            $"Queued layer {sourceLayerName} for merge " +
            $"({_layerMergeSourceNames.Count:N0} sources).");
        UpdateEditControls();
    }

    private void ClearLayerMergeSources(bool setStatus)
    {
        bool hadSources = _layerMergeSourceNames.Count > 0;
        _layerMergeSourceNames.Clear();
        _layerMergeSourceNameSet.Clear();
        _layerMergeSourceSession = _canvas.CurrentSession;
        _layerMergeSourceGeneration =
            _canvas.CurrentSnapshot?.ContentGeneration ?? ulong.MaxValue;
        _layerMergeSourceCountText.Text = "Sources: 0";
        if (setStatus && hadSources)
        {
            SetStatus("Cleared queued layer merge sources.");
        }
        UpdateEditControls();
    }

    private void EnsureLayerMergeSourcesAreCurrent()
    {
        CadDocumentSession? session = _canvas.CurrentSession;
        ulong generation =
            _canvas.CurrentSnapshot?.ContentGeneration ?? ulong.MaxValue;
        if (ReferenceEquals(session, _layerMergeSourceSession) &&
            generation == _layerMergeSourceGeneration)
        {
            return;
        }

        _layerMergeSourceNames.Clear();
        _layerMergeSourceNameSet.Clear();
        _layerMergeSourceSession = session;
        _layerMergeSourceGeneration = generation;
        _layerMergeSourceCountText.Text = "Sources: 0";
    }

    private void SelectLayerStateChoice(string layerName)
    {
        _layerStateSelector.SelectedItem =
            _layerStateSelector.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is string candidate &&
                    candidate.Equals(
                        layerName,
                        StringComparison.OrdinalIgnoreCase));
    }

    private void BeginSelectionPointTransform(
        CadPointTransformOperation operation)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _pointTransformInput.Text = string.Empty;
            if (!_canvas.BeginSelectionPointTransform(operation))
            {
                SetStatus(
                    $"{DescribePointTransformOperation(operation)} requires at least one selected entity.");
            }
        }
        catch (Exception exception)
        {
            SetStatus(
                $"{DescribePointTransformOperation(operation)} failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void BeginLineAuthoring()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _pointTransformInput.Text = string.Empty;
            if (!_canvas.BeginLineAuthoring())
            {
                SetStatus("LINE requires a loaded plan-view document.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"LINE failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void BeginPointAuthoring()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _pointTransformInput.Text = string.Empty;
            if (!_canvas.BeginPointAuthoring())
            {
                SetStatus("POINT requires a loaded plan-view document.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"POINT failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void BeginRayAuthoring()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _pointTransformInput.Text = string.Empty;
            if (!_canvas.BeginRayAuthoring())
            {
                SetStatus("RAY requires a loaded plan-view document.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"RAY failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void BeginXLineAuthoring()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _pointTransformInput.Text = string.Empty;
            CadXLineAuthoringMode mode =
                (_xlineModeSelector.SelectedItem as ComboBoxItem)?.Tag is
                    CadXLineAuthoringMode selectedMode
                    ? selectedMode
                    : CadXLineAuthoringMode.TwoPoint;
            if (!_canvas.BeginXLineAuthoring(mode))
            {
                SetStatus("XLINE requires a loaded plan-view document.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"XLINE failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void BeginPolylineAuthoring()
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _pointTransformInput.Text = string.Empty;
            if (!_canvas.BeginPolylineAuthoring())
            {
                SetStatus("PLINE requires a loaded plan-view document.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"PLINE could not start: {exception.Message}");
        }
        UpdateEditControls();
    }

    private void BeginCircleAuthoring(CadCircleAuthoringMode mode)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _pointTransformInput.Text = string.Empty;
            if (!_canvas.BeginCircleAuthoring(mode))
            {
                SetStatus("CIRCLE requires a loaded plan-view document.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"CIRCLE could not start: {exception.Message}");
        }
        UpdateEditControls();
    }

    private void BeginArcAuthoring(CadArcAuthoringMode mode)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _pointTransformInput.Text = string.Empty;
            if (!_canvas.BeginArcAuthoring(mode))
            {
                SetStatus("ARC requires a loaded plan-view document.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"ARC could not start: {exception.Message}");
        }
        UpdateEditControls();
    }

    private void BeginEllipseAuthoring(
        CadEllipseAuthoringMode mode,
        CadEllipseArcInputMode arcInputMode)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _pointTransformInput.Text = string.Empty;
            if (!_canvas.BeginEllipseAuthoring(mode, arcInputMode))
            {
                SetStatus(IsIsocircleMode(mode)
                    ? "ELLIPSE Isocircle requires a loaded plan-view document with SNAPSTYL=1."
                    : "ELLIPSE requires a loaded plan-view document.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"ELLIPSE could not start: {exception.Message}");
        }
        UpdateEditControls();
    }

    private void BeginPolygonAuthoring(
        int sideCount,
        CadPolygonAuthoringMode mode)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _pointTransformInput.Text = string.Empty;
            if (!_canvas.BeginPolygonAuthoring(sideCount, mode))
            {
                SetStatus("POLYGON requires a loaded plan-view document.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"POLYGON could not start: {exception.Message}");
        }
        UpdateEditControls();
    }

    private void BeginRectangleAuthoring()
    {
        if (_isBusy)
        {
            return;
        }

        if (!TryCreateRectangleConfiguration(
                out CadRectangleConstruction construction,
                out CadRectangleCornerTreatment cornerTreatment,
                out double rotationDegrees,
                out string? errorMessage))
        {
            SetStatus(errorMessage ?? "RECTANG settings are invalid.");
            UpdateEditControls();
            return;
        }

        try
        {
            _pointTransformInput.Text = string.Empty;
            if (!_canvas.BeginRectangleAuthoring(
                    construction,
                    cornerTreatment,
                    rotationDegrees))
            {
                SetStatus("RECTANG requires a loaded plan-view document.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"RECTANG could not start: {exception.Message}");
        }
        UpdateEditControls();
    }

    private bool TryCreateRectangleConfiguration(
        out CadRectangleConstruction construction,
        out CadRectangleCornerTreatment cornerTreatment,
        out double rotationDegrees,
        out string? errorMessage)
    {
        construction = default;
        cornerTreatment = default;
        rotationDegrees = 0.0;
        errorMessage = null;
        if ((_rectangleConstructionSelector.SelectedItem as ComboBoxItem)?.Tag
                is not CadRectangleConstructionMode constructionMode ||
            (_rectangleCornerSelector.SelectedItem as ComboBoxItem)?.Tag
                is not CadRectangleCornerMode cornerMode ||
            !TryParseFiniteInvariantDouble(
                _rectangleRotationInput.Text,
                out rotationDegrees))
        {
            errorMessage =
                "Choose RECTANG construction/corners and enter a finite rotation in degrees.";
            return false;
        }

        try
        {
            switch (constructionMode)
            {
                case CadRectangleConstructionMode.DiagonalCorners:
                    construction = CadRectangleConstruction.DiagonalCorners;
                    break;
                case CadRectangleConstructionMode.Dimensions:
                    if (!TryParseInvariantPair(
                            _rectangleValuesInput.Text,
                            positive: true,
                            out double length,
                            out double width))
                    {
                        errorMessage =
                            "Enter positive RECTANG Dimensions as length,width.";
                        return false;
                    }
                    construction = CadRectangleConstruction.Dimensions(
                        length,
                        width);
                    break;
                case CadRectangleConstructionMode.Area:
                    if ((_rectangleAreaDimensionSelector.SelectedItem as
                                ComboBoxItem)?.Tag is not
                            CadRectangleKnownDimension knownDimension ||
                        !TryParseInvariantPair(
                            _rectangleValuesInput.Text,
                            positive: true,
                            out double area,
                            out double knownValue))
                    {
                        errorMessage =
                            "Enter positive RECTANG Area values as area,known-dimension.";
                        return false;
                    }
                    construction = CadRectangleConstruction.FromArea(
                        area,
                        knownDimension,
                        knownValue);
                    break;
                default:
                    errorMessage = "The RECTANG construction mode is invalid.";
                    return false;
            }

            switch (cornerMode)
            {
                case CadRectangleCornerMode.Sharp:
                    cornerTreatment = CadRectangleCornerTreatment.Sharp;
                    break;
                case CadRectangleCornerMode.Chamfer:
                    if (!TryParseInvariantPair(
                            _rectangleCornerValuesInput.Text,
                            positive: false,
                            out double firstDistance,
                            out double secondDistance))
                    {
                        errorMessage =
                            "Enter non-negative RECTANG chamfers as first,second.";
                        return false;
                    }
                    cornerTreatment = CadRectangleCornerTreatment.Chamfer(
                        firstDistance,
                        secondDistance);
                    break;
                case CadRectangleCornerMode.Fillet:
                    if (!TryParseNonNegativeInvariantDouble(
                            _rectangleCornerValuesInput.Text,
                            out double radius))
                    {
                        errorMessage =
                            "Enter one non-negative RECTANG fillet radius.";
                        return false;
                    }
                    cornerTreatment =
                        CadRectangleCornerTreatment.Fillet(radius);
                    break;
                default:
                    errorMessage = "The RECTANG corner mode is invalid.";
                    return false;
            }
            _ = new CadRectangleAuthoringSession(
                0.0,
                cornerTreatment,
                construction);
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private void AcceptPointInput()
    {
        if (_isBusy)
        {
            return;
        }

        string input = _pointTransformInput.Text;
        bool accepted;
        string? errorMessage;
        if (_canvas.IsPointAuthoring)
        {
            accepted = _canvas.TryAcceptPointAuthoringInput(
                input,
                out errorMessage);
        }
        else if (_canvas.IsRectangleAuthoring)
        {
            accepted = _canvas.TryAcceptRectangleAuthoringInput(
                input,
                out errorMessage);
        }
        else if (_canvas.IsPolygonAuthoring)
        {
            accepted = _canvas.TryAcceptPolygonAuthoringInput(
                input,
                out errorMessage);
        }
        else if (_canvas.IsEllipseAuthoring)
        {
            accepted = _canvas.TryAcceptEllipseAuthoringInput(
                input,
                out errorMessage);
        }
        else if (_canvas.IsArcAuthoring)
        {
            accepted = _canvas.TryAcceptArcAuthoringInput(
                input,
                out errorMessage);
        }
        else if (_canvas.IsCircleAuthoring)
        {
            accepted = _canvas.TryAcceptCircleAuthoringInput(
                input,
                out errorMessage);
        }
        else if (_canvas.IsPolylineAuthoring)
        {
            accepted = _canvas.TryAcceptPolylineAuthoringInput(
                input,
                out errorMessage);
        }
        else if (_canvas.IsLineAuthoring)
        {
            accepted = _canvas.TryAcceptLineAuthoringInput(
                input,
                out errorMessage);
        }
        else if (_canvas.IsRayAuthoring)
        {
            accepted = _canvas.TryAcceptRayAuthoringInput(
                input,
                out errorMessage);
        }
        else if (_canvas.IsXLineAuthoring)
        {
            accepted = _canvas.TryAcceptXLineAuthoringInput(
                input,
                out errorMessage);
        }
        else if (_canvas.PendingPointTransformOperation is not null)
        {
            accepted = _canvas.TryAcceptSelectionPointTransformInput(
                input,
                out errorMessage);
        }
        else
        {
            return;
        }

        if (!accepted)
        {
            SetStatus(errorMessage ?? "The coordinate input was rejected.");
        }
        UpdateEditControls();
    }

    private void UndoLineAuthoringSegment()
    {
        if (!_canvas.UndoLineAuthoringSegment())
        {
            SetStatus("LINE has no accepted segment to undo.");
        }
        UpdateEditControls();
    }

    private void CompleteLineAuthoring(bool close)
    {
        if (!_canvas.CompleteLineAuthoring(close, out string? errorMessage))
        {
            SetStatus(errorMessage ?? "LINE completion failed.");
        }
        UpdateEditControls();
    }

    private void UndoRayAuthoringRay()
    {
        if (!_canvas.UndoRayAuthoringRay())
        {
            SetStatus("RAY has no accepted ray to undo.");
        }
        UpdateEditControls();
    }

    private void CompleteRayAuthoring()
    {
        if (!_canvas.CompleteRayAuthoring(out string? errorMessage))
        {
            SetStatus(errorMessage ?? "RAY completion failed.");
        }
        UpdateEditControls();
    }

    private void UndoXLineAuthoringLine()
    {
        if (!_canvas.UndoXLineAuthoringLine())
        {
            SetStatus("XLINE has no accepted line to undo.");
        }
        UpdateEditControls();
    }

    private void CompleteXLineAuthoring()
    {
        if (!_canvas.CompleteXLineAuthoring(out string? errorMessage))
        {
            SetStatus(errorMessage ?? "XLINE completion failed.");
        }
        UpdateEditControls();
    }

    private void UndoPolylineAuthoringSegment()
    {
        if (!_canvas.UndoPolylineAuthoringSegment())
        {
            SetStatus("PLINE has no accepted segment to undo.");
        }
        UpdateEditControls();
    }

    private void SetPolylineAuthoringMode(CadPolylineAuthoringMode mode)
    {
        if (!_canvas.IsPolylineAuthoring)
        {
            return;
        }
        _canvas.PolylineAuthoringMode = mode;
        UpdateEditControls();
    }

    private void BeginPolylineWidthInput(CadPolylineWidthInputMode mode)
    {
        if (!_canvas.BeginPolylineWidthInput(mode, out string? errorMessage))
        {
            SetStatus(errorMessage ?? "PLINE width input could not start.");
        }
        else
        {
            _pointTransformInput.Text = string.Empty;
        }
        UpdateEditControls();
    }

    private void BeginPolylineLengthInput()
    {
        if (!_canvas.BeginPolylineLengthInput(out string? errorMessage))
        {
            SetStatus(errorMessage ?? "PLINE length input could not start.");
        }
        else
        {
            _pointTransformInput.Text = string.Empty;
        }
        UpdateEditControls();
    }

    private void BeginPolylineArcConstruction(
        CadPolylineArcConstruction construction)
    {
        if (!_canvas.BeginPolylineArcConstruction(
                construction,
                out string? errorMessage))
        {
            SetStatus(errorMessage ?? "PLINE arc option could not start.");
        }
        else
        {
            _pointTransformInput.Text = string.Empty;
        }
        UpdateEditControls();
    }

    private void CompletePolylineAuthoring(bool close)
    {
        if (!_canvas.CompletePolylineAuthoring(close, out string? errorMessage))
        {
            SetStatus(errorMessage ?? "PLINE completion failed.");
        }
        UpdateEditControls();
    }

    private static string DescribePointTransform(
        CadPointTransformChangedEventArgs args)
    {
        string operation = DescribePointTransformOperation(args.Operation);
        return args.Stage switch
        {
            CadPointTransformStage.AwaitingBasePoint =>
                $"{operation}: click (object snap overrides grid/Ortho/polar) or enter absolute WCS x,y[,z] / distance<angle; Escape cancels.",
            CadPointTransformStage.AwaitingSecondPoint =>
                $"{operation}: base {FormatPoint(args.BasePoint!.Value)}; " +
                "click (object snap overrides grid/Ortho/polar), enter an absolute point or relative @dx,dy[,dz] / @distance<angle, or move the cursor and enter a positive distance; Escape cancels.",
            CadPointTransformStage.Completed when args.ErrorMessage is null =>
                $"{operation} completed with WCS displacement " +
                $"{FormatPoint(args.Displacement!.Value)}.",
            CadPointTransformStage.Completed =>
                $"{operation} completed without changes: {args.ErrorMessage}",
            CadPointTransformStage.Canceled => $"{operation} canceled.",
            CadPointTransformStage.Failed =>
                $"{operation} failed: {args.ErrorMessage}",
            _ => throw new ArgumentOutOfRangeException(nameof(args)),
        };
    }

    private static string DescribeLineAuthoring(
        CadLineAuthoringChangedEventArgs args) => args.Stage switch
    {
        CadLineAuthoringStage.AwaitingFirstPoint =>
            "LINE: specify first point by click or absolute WCS coordinate; Escape ends.",
        CadLineAuthoringStage.AwaitingNextPoint when args.SegmentCount == 0 =>
            $"LINE: first point {FormatPoint(args.CurrentPoint!.Value)}; specify next point.",
        CadLineAuthoringStage.AwaitingNextPoint =>
            $"LINE: {args.SegmentCount} accepted segment(s); next point, U, Close, Enter, or Escape.",
        CadLineAuthoringStage.SegmentUndone =>
            $"LINE: latest segment removed; {args.SegmentCount} segment(s) remain.",
        CadLineAuthoringStage.Completed when args.SegmentCount == 0 =>
            "LINE ended without creating a segment.",
        CadLineAuthoringStage.Completed =>
            $"LINE created {args.SegmentCount} separate segment(s)" +
            (args.IsClosed ? " and closed the sequence." : "."),
        CadLineAuthoringStage.Failed =>
            $"LINE failed: {args.ErrorMessage}",
        _ => throw new ArgumentOutOfRangeException(nameof(args)),
    };

    private static string DescribeRayAuthoring(
        CadRayAuthoringChangedEventArgs args) => args.Stage switch
    {
        CadRayAuthoringStage.AwaitingStartPoint =>
            "RAY: specify start point by click or absolute WCS coordinate; Escape ends.",
        CadRayAuthoringStage.AwaitingThroughPoint when args.RayCount == 0 =>
            $"RAY: start {FormatPoint(args.StartPoint!.Value)}; specify through point.",
        CadRayAuthoringStage.AwaitingThroughPoint =>
            $"RAY: {args.RayCount} accepted ray(s) from {FormatPoint(args.StartPoint!.Value)}; next through point, U, Enter, or Escape.",
        CadRayAuthoringStage.RayUndone =>
            $"RAY: latest ray removed; {args.RayCount} ray(s) remain.",
        CadRayAuthoringStage.Completed when args.RayCount == 0 =>
            "RAY ended without creating an entity.",
        CadRayAuthoringStage.Completed =>
            $"RAY created {args.RayCount} ray(s) with one common start point.",
        CadRayAuthoringStage.Failed =>
            $"RAY failed: {args.ErrorMessage}",
        _ => throw new ArgumentOutOfRangeException(nameof(args)),
    };

    private static string DescribeXLineAuthoring(
        CadXLineAuthoringChangedEventArgs args) => args.Stage switch
    {
        CadXLineAuthoringStage.AwaitingFirstPoint =>
            "XLINE: specify common first point by click or absolute WCS coordinate; Escape ends.",
        CadXLineAuthoringStage.AwaitingThroughPoint when args.LineCount == 0 =>
            $"XLINE: first point {FormatPoint(args.FirstPoint!.Value)}; specify through point.",
        CadXLineAuthoringStage.AwaitingThroughPoint =>
            $"XLINE: {args.LineCount} accepted line(s) through {FormatPoint(args.FirstPoint!.Value)}; next through point, U, Enter, or Escape.",
        CadXLineAuthoringStage.AwaitingInput =>
            DescribeXLinePrompt(args),
        CadXLineAuthoringStage.LineUndone =>
            $"XLINE: latest line removed; {args.LineCount} line(s) remain.",
        CadXLineAuthoringStage.Completed when args.LineCount == 0 =>
            "XLINE ended without creating an entity.",
        CadXLineAuthoringStage.Completed =>
            $"XLINE created {args.LineCount} line(s) in {args.Mode} mode.",
        CadXLineAuthoringStage.Failed =>
            $"XLINE failed: {args.ErrorMessage}",
        _ => throw new ArgumentOutOfRangeException(nameof(args)),
    };

    private static string DescribeXLinePrompt(
        CadXLineAuthoringChangedEventArgs args) => args.Prompt switch
    {
        CadXLinePromptKind.PlacementPoint =>
            $"XLINE {args.Mode}: specify a placement point; U, Enter, or Escape ends.",
        CadXLinePromptKind.AngleValue =>
            "XLINE Angle: enter angle in degrees or Reference (R) when available.",
        CadXLinePromptKind.AngleReferenceSource =>
            "XLINE Angle/Reference: select a visible LINE, RAY, or XLINE.",
        CadXLinePromptKind.BisectorVertex =>
            "XLINE Bisect: specify angle vertex.",
        CadXLinePromptKind.BisectorFirstRayPoint =>
            "XLINE Bisect: specify a point on the first ray.",
        CadXLinePromptKind.BisectorSecondRayPoint =>
            "XLINE Bisect: specify a point on the second ray.",
        CadXLinePromptKind.OffsetDistance =>
            "XLINE Offset: enter a positive distance or Through (T).",
        CadXLinePromptKind.OffsetSource =>
            "XLINE Offset: select a visible LINE, RAY, or XLINE source.",
        CadXLinePromptKind.OffsetSidePoint =>
            "XLINE Offset: specify the side to offset.",
        CadXLinePromptKind.OffsetThroughPoint =>
            "XLINE Offset/Through: specify the through point.",
        _ => "XLINE: specify the requested input.",
    };

    private static string DescribePointAuthoring(
        CadPointAuthoringChangedEventArgs args) => args.Stage switch
    {
        CadPointAuthoringStage.AwaitingPoint =>
            "POINT: specify one point by click or absolute WCS coordinate; Escape cancels.",
        CadPointAuthoringStage.Completed =>
            $"POINT created at {FormatPoint(args.Location!.Value)}.",
        CadPointAuthoringStage.Canceled => "POINT canceled.",
        CadPointAuthoringStage.Failed =>
            $"POINT failed: {args.ErrorMessage}",
        _ => throw new ArgumentOutOfRangeException(nameof(args)),
    };

    private static string DescribePolylineAuthoring(
        CadPolylineAuthoringChangedEventArgs args) => args.Stage switch
    {
        CadPolylineAuthoringStage.AwaitingFirstPoint =>
            $"PLINE: specify first point by click or absolute WCS coordinate; " +
            $"current width {FormatScalar(args.NextStartWidth)}; Escape ends.",
        CadPolylineAuthoringStage.AwaitingNextPoint when args.SegmentCount == 0 =>
            $"PLINE: first point {FormatPoint(args.CurrentPoint!.Value)}; specify next point, Arc (A), Width (W), or Halfwidth (H); " +
            $"next width {FormatWidthRange(args)}.",
        CadPolylineAuthoringStage.AwaitingNextPoint =>
            $"PLINE: {args.SegmentCount} accepted segment(s); " +
            $"{(args.Mode == CadPolylineAuthoringMode.Line ? "Line" : "tangent Arc")} mode; " +
            $"next width {FormatWidthRange(args)}; next point, W, H, " +
            (args.Mode == CadPolylineAuthoringMode.Line ? "Length (L), " : "Line (L), ") +
            "U, Close, Enter, or Escape.",
        CadPolylineAuthoringStage.ModeChanged =>
            args.Mode == CadPolylineAuthoringMode.Line
                ? "PLINE: Line mode."
                : "PLINE: Arc mode; specify an endpoint for tangent continuation, or Angle, Center, Direction, Radius, or Second pt.",
        CadPolylineAuthoringStage.PromptChanged when
            args.Prompt == CadPolylineAuthoringPrompt.StartingWidth =>
            $"PLINE {DescribeWidthInputMode(args.WidthInputMode)}: enter starting " +
            $"{DescribeWidthInputMode(args.WidthInputMode).ToLowerInvariant()} " +
            $"<{FormatWidthPromptValue(args.NextStartWidth, args.WidthInputMode)}>; empty accepts the default.",
        CadPolylineAuthoringStage.PromptChanged when
            args.Prompt == CadPolylineAuthoringPrompt.EndingWidth =>
            $"PLINE {DescribeWidthInputMode(args.WidthInputMode)}: enter ending " +
            $"{DescribeWidthInputMode(args.WidthInputMode).ToLowerInvariant()} " +
            $"<{FormatWidthPromptValue(args.NextEndWidth, args.WidthInputMode)}>; empty accepts the default.",
        CadPolylineAuthoringStage.PromptChanged when
            args.Prompt == CadPolylineAuthoringPrompt.Length =>
            "PLINE Length: enter a finite positive length; the new line follows the previous segment tangent.",
        CadPolylineAuthoringStage.PromptChanged when
            args.Prompt == CadPolylineAuthoringPrompt.ArcIncludedAngle =>
            "PLINE Arc/Angle: enter a finite nonzero included angle in degrees; after Center this completes the fixed-center arc.",
        CadPolylineAuthoringStage.PromptChanged when
            args.Prompt == CadPolylineAuthoringPrompt.ArcCenter =>
            "PLINE Arc/Center: specify the arc center point.",
        CadPolylineAuthoringStage.PromptChanged when
            args.Prompt == CadPolylineAuthoringPrompt.ArcDirection =>
            "PLINE Arc/Direction: specify a point establishing the start tangent.",
        CadPolylineAuthoringStage.PromptChanged when
            args.Prompt == CadPolylineAuthoringPrompt.ArcRadius =>
            "PLINE Arc/Radius: enter a nonzero signed radius (negative selects major); after Angle, radius must be positive and next specifies chord direction.",
        CadPolylineAuthoringStage.PromptChanged when
            args.Prompt == CadPolylineAuthoringPrompt.ArcChordDirection =>
            "PLINE Arc/Angle/Radius: specify a point establishing the chord direction.",
        CadPolylineAuthoringStage.PromptChanged when
            args.Prompt == CadPolylineAuthoringPrompt.ArcChordLength =>
            "PLINE Arc/Center/Length: enter a signed chord length; positive selects the minor CCW arc and negative the major CCW arc.",
        CadPolylineAuthoringStage.PromptChanged when
            args.Prompt == CadPolylineAuthoringPrompt.ArcSecondPoint =>
            "PLINE Arc/Second pt: specify a second point on the arc.",
        CadPolylineAuthoringStage.PromptChanged when
            args.Prompt == CadPolylineAuthoringPrompt.ArcEndpoint =>
            "PLINE Arc: specify the endpoint, or choose a contextual Angle, Center, Radius, or Length option; hold Ctrl for the clockwise Center, Direction, or Radius solution.",
        CadPolylineAuthoringStage.PromptChanged =>
            "PLINE: specify the requested input.",
        CadPolylineAuthoringStage.SegmentUndone =>
            $"PLINE: latest segment removed; {args.SegmentCount} segment(s) remain.",
        CadPolylineAuthoringStage.Completed when args.SegmentCount == 0 =>
            "PLINE ended without creating an entity.",
        CadPolylineAuthoringStage.Completed =>
            $"PLINE created one lightweight polyline with {args.SegmentCount} segment(s)" +
            (args.IsClosed ? " and closed it." : "."),
        CadPolylineAuthoringStage.Failed =>
            $"PLINE failed: {args.ErrorMessage}",
        _ => throw new ArgumentOutOfRangeException(nameof(args)),
    };

    private static string FormatWidthRange(CadPolylineAuthoringChangedEventArgs args) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{args.NextStartWidth:G17}->{args.NextEndWidth:G17}");

    private static string FormatWidthPromptValue(
        double fullWidth,
        CadPolylineWidthInputMode mode) =>
        FormatScalar(mode == CadPolylineWidthInputMode.Halfwidth ? fullWidth * 0.5 : fullWidth);

    private static string DescribeWidthInputMode(CadPolylineWidthInputMode mode) =>
        mode switch
        {
            CadPolylineWidthInputMode.Width => "Width",
            CadPolylineWidthInputMode.Halfwidth => "Halfwidth",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static string FormatScalar(double value) =>
        value.ToString("G17", CultureInfo.InvariantCulture);

    private static string DescribeCircleAuthoring(
        CadCircleAuthoringChangedEventArgs args) => args.Stage switch
    {
        CadCircleAuthoringStage.AwaitingFirstPoint =>
            $"CIRCLE {DescribeCircleMode(args.Mode)}: specify the first point by click or absolute WCS coordinate; Escape cancels.",
        CadCircleAuthoringStage.AwaitingNextPoint =>
            $"CIRCLE {DescribeCircleMode(args.Mode)}: accepted point {args.PointCount}; specify the next point; Escape cancels.",
        CadCircleAuthoringStage.Completed =>
            $"CIRCLE {DescribeCircleMode(args.Mode)} created center " +
            $"{FormatPoint(args.Snapshot!.Value.Center)} radius " +
            $"{args.Snapshot.Value.Radius:G17}.",
        CadCircleAuthoringStage.Canceled => "CIRCLE canceled.",
        CadCircleAuthoringStage.Failed =>
            $"CIRCLE failed: {args.ErrorMessage}",
        _ => throw new ArgumentOutOfRangeException(nameof(args)),
    };

    private static string DescribeCircleMode(CadCircleAuthoringMode mode) =>
        mode switch
        {
            CadCircleAuthoringMode.CenterRadius => "center/radius",
            CadCircleAuthoringMode.CenterDiameter => "center/diameter",
            CadCircleAuthoringMode.TwoPoint => "2P diameter",
            CadCircleAuthoringMode.ThreePoint => "3P circumference",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static string DescribeArcAuthoring(
        CadArcAuthoringChangedEventArgs args) => args.Stage switch
    {
        CadArcAuthoringStage.AwaitingFirstPoint =>
            $"ARC {DescribeArcMode(args.Mode)}: specify {DescribeArcPointPrompt(args.Mode, 0)} by click or absolute WCS coordinate; Escape cancels.",
        CadArcAuthoringStage.AwaitingNextInput when args.PointCount < 2 =>
            $"ARC {DescribeArcMode(args.Mode)}: specify {DescribeArcPointPrompt(args.Mode, args.PointCount)}; Escape cancels.",
        CadArcAuthoringStage.AwaitingNextInput =>
            $"ARC {DescribeArcMode(args.Mode)}: {DescribeArcFinalPrompt(args.Mode)}; Escape cancels.",
        CadArcAuthoringStage.Completed =>
            $"ARC {DescribeArcMode(args.Mode)} created center " +
            $"{FormatPoint(args.Snapshot!.Value.Center)} radius " +
            $"{args.Snapshot.Value.Radius:G17}, sweep " +
            $"{args.Snapshot.Value.SweepAngle * (180.0 / Math.PI):G17}°.",
        CadArcAuthoringStage.Canceled => "ARC canceled.",
        CadArcAuthoringStage.Failed =>
            $"ARC failed: {args.ErrorMessage}",
        _ => throw new ArgumentOutOfRangeException(nameof(args)),
    };

    private static string DescribeArcMode(CadArcAuthoringMode mode) =>
        mode switch
        {
            CadArcAuthoringMode.ThreePoint => "3P",
            CadArcAuthoringMode.CenterStartEnd => "Center/Start/End",
            CadArcAuthoringMode.CenterStartAngle => "Center/Start/Angle",
            CadArcAuthoringMode.CenterStartChord => "Center/Start/Chord",
            CadArcAuthoringMode.StartCenterEnd => "Start/Center/End",
            CadArcAuthoringMode.StartCenterAngle => "Start/Center/Angle",
            CadArcAuthoringMode.StartCenterChord => "Start/Center/Chord",
            CadArcAuthoringMode.StartEndAngle => "Start/End/Angle",
            CadArcAuthoringMode.StartEndDirection => "Start/End/Direction",
            CadArcAuthoringMode.StartEndRadius => "Start/End/Radius",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static string DescribeArcPointPrompt(
        CadArcAuthoringMode mode,
        int pointIndex) => (mode, pointIndex) switch
        {
            (CadArcAuthoringMode.ThreePoint, 0) => "start point",
            (CadArcAuthoringMode.ThreePoint, 1) => "second circumference point",
            (CadArcAuthoringMode.CenterStartEnd or
                CadArcAuthoringMode.CenterStartAngle or
                CadArcAuthoringMode.CenterStartChord, 0) => "center point",
            (CadArcAuthoringMode.CenterStartEnd or
                CadArcAuthoringMode.CenterStartAngle or
                CadArcAuthoringMode.CenterStartChord, 1) => "start point",
            (CadArcAuthoringMode.StartCenterEnd or
                CadArcAuthoringMode.StartCenterAngle or
                CadArcAuthoringMode.StartCenterChord, 0) => "start point",
            (CadArcAuthoringMode.StartCenterEnd or
                CadArcAuthoringMode.StartCenterAngle or
                CadArcAuthoringMode.StartCenterChord, 1) => "center point",
            (_, 0) => "start point",
            (_, 1) => "end point",
            _ => "next input",
        };

    private static string DescribeArcFinalPrompt(CadArcAuthoringMode mode) =>
        mode switch
        {
            CadArcAuthoringMode.ThreePoint =>
                "specify endpoint by click or coordinate",
            CadArcAuthoringMode.CenterStartEnd or
            CadArcAuthoringMode.StartCenterEnd =>
                "specify endpoint ray by click or coordinate; hold Ctrl for clockwise",
            CadArcAuthoringMode.CenterStartAngle or
            CadArcAuthoringMode.StartCenterAngle or
            CadArcAuthoringMode.StartEndAngle =>
                "enter a signed included angle in degrees",
            CadArcAuthoringMode.CenterStartChord or
            CadArcAuthoringMode.StartCenterChord =>
                "enter a signed chord length (positive minor, negative major)",
            CadArcAuthoringMode.StartEndDirection =>
                "specify a tangent-direction point (hold Ctrl for clockwise) or enter its angle in degrees",
            CadArcAuthoringMode.StartEndRadius =>
                "enter a signed radius (positive minor, negative major)",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static string DescribeEllipseAuthoring(
        CadEllipseAuthoringChangedEventArgs args) => args.Stage switch
    {
        CadEllipseAuthoringStage.AwaitingFirstPoint or
        CadEllipseAuthoringStage.AwaitingNextInput =>
            $"ELLIPSE {DescribeEllipseMode(args.Mode)} " +
            $"{DescribeEllipseArcInputMode(args.ArcInputMode)}: " +
            $"{DescribeEllipsePrompt(args.Mode, args.InputKind)}; Escape cancels.",
        CadEllipseAuthoringStage.Completed =>
            $"ELLIPSE {DescribeEllipseMode(args.Mode)} " +
            $"{DescribeEllipseArcInputMode(args.ArcInputMode)} created center " +
            $"{FormatPoint(args.Snapshot!.Value.Center)}, major axis " +
            $"{args.Snapshot.Value.MajorRadius:G17}, ratio " +
            $"{args.Snapshot.Value.RadiusRatio:G17}.",
        CadEllipseAuthoringStage.Canceled => "ELLIPSE canceled.",
        CadEllipseAuthoringStage.Failed =>
            $"ELLIPSE failed: {args.ErrorMessage}",
        _ => throw new ArgumentOutOfRangeException(nameof(args)),
    };

    private static string DescribeEllipseMode(CadEllipseAuthoringMode mode) =>
        mode switch
        {
            CadEllipseAuthoringMode.AxisEndpointsDistance => "Axis/Distance",
            CadEllipseAuthoringMode.AxisEndpointsRotation => "Axis/Rotation",
            CadEllipseAuthoringMode.CenterDistance => "Center/Distance",
            CadEllipseAuthoringMode.CenterRotation => "Center/Rotation",
            CadEllipseAuthoringMode.IsocircleRadius => "Isocircle/Radius",
            CadEllipseAuthoringMode.IsocircleDiameter => "Isocircle/Diameter",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static bool IsIsocircleMode(CadEllipseAuthoringMode mode) =>
        mode is
            CadEllipseAuthoringMode.IsocircleRadius or
            CadEllipseAuthoringMode.IsocircleDiameter;

    private static string DescribeEllipseArcInputMode(
        CadEllipseArcInputMode mode) => mode switch
    {
        CadEllipseArcInputMode.Full => "Full",
        CadEllipseArcInputMode.Angle => "Arc/Angle",
        CadEllipseArcInputMode.Parameter => "Arc/Parameter",
        CadEllipseArcInputMode.IncludedAngle => "Arc/Included",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string DescribeEllipsePrompt(
        CadEllipseAuthoringMode mode,
        CadEllipseAuthoringInputKind inputKind) => inputKind switch
    {
        CadEllipseAuthoringInputKind.FirstAxisPoint when mode is
            CadEllipseAuthoringMode.CenterDistance or
            CadEllipseAuthoringMode.CenterRotation =>
                "specify the center point by click or coordinate",
        CadEllipseAuthoringInputKind.IsocircleCenter =>
            "specify the isocircle center by click or coordinate",
        CadEllipseAuthoringInputKind.IsocircleRadius =>
            "specify the isocircle radius by click, coordinate, or positive distance",
        CadEllipseAuthoringInputKind.IsocircleDiameter =>
            "specify the isocircle diameter by click, coordinate, or positive distance",
        CadEllipseAuthoringInputKind.FirstAxisPoint =>
            "specify the first endpoint of the first axis by click or coordinate",
        CadEllipseAuthoringInputKind.SecondAxisPoint when mode is
            CadEllipseAuthoringMode.CenterDistance or
            CadEllipseAuthoringMode.CenterRotation =>
                "specify the endpoint of the first axis by click or coordinate",
        CadEllipseAuthoringInputKind.SecondAxisPoint =>
            "specify the second endpoint of the first axis by click or coordinate",
        CadEllipseAuthoringInputKind.OtherAxisPoint =>
            "specify the other-axis distance by click, coordinate, or direct distance",
        CadEllipseAuthoringInputKind.RotationRadians =>
            "enter the rotation angle in degrees (0 is circular; edge-on is invalid)",
        CadEllipseAuthoringInputKind.StartDirection =>
            "specify the start direction by point or angle in degrees",
        CadEllipseAuthoringInputKind.StartParameterRadians =>
            "enter the start parameter in degrees",
        CadEllipseAuthoringInputKind.EndDirection =>
            "specify the end direction by point or angle in degrees",
        CadEllipseAuthoringInputKind.EndParameterRadians =>
            "enter the end parameter in degrees",
        CadEllipseAuthoringInputKind.IncludedAngleRadians =>
            "enter the signed included angle in degrees",
        _ => throw new ArgumentOutOfRangeException(nameof(inputKind)),
    };

    private static string DescribePolygonAuthoring(
        CadPolygonAuthoringChangedEventArgs args) => args.Stage switch
    {
        CadPolygonAuthoringStage.AwaitingFirstPoint =>
            $"POLYGON {args.SideCount} {DescribePolygonMode(args.Mode)}: " +
            $"{DescribePolygonPrompt(args.InputKind)}; Escape cancels.",
        CadPolygonAuthoringStage.AwaitingFinalInput =>
            $"POLYGON {args.SideCount} {DescribePolygonMode(args.Mode)}: " +
            $"{DescribePolygonPrompt(args.InputKind)}; Escape cancels.",
        CadPolygonAuthoringStage.Completed =>
            $"POLYGON {args.SideCount} {DescribePolygonMode(args.Mode)} created " +
            $"center {FormatPoint(args.Snapshot!.Value.Center)}, circumradius " +
            $"{args.Snapshot.Value.Circumradius:G17}.",
        CadPolygonAuthoringStage.Canceled => "POLYGON canceled.",
        CadPolygonAuthoringStage.Failed =>
            $"POLYGON failed: {args.ErrorMessage}",
        _ => throw new ArgumentOutOfRangeException(nameof(args)),
    };

    private static string DescribeRectangleAuthoring(
        CadRectangleAuthoringChangedEventArgs args) => args.Stage switch
    {
        CadRectangleAuthoringStage.AwaitingFirstCorner =>
            $"RECTANG {DescribeRectangleConstruction(args.Construction)} " +
            $"{args.CornerTreatment.Mode}: specify the first corner by click " +
            "or absolute WCS coordinate; Escape cancels.",
        CadRectangleAuthoringStage.AwaitingPlacement =>
            $"RECTANG {DescribeRectangleConstruction(args.Construction)} " +
            $"{args.CornerTreatment.Mode}: specify the other corner/placement " +
            "quadrant by click, coordinate, or direct distance; Escape cancels.",
        CadRectangleAuthoringStage.Completed =>
            $"RECTANG {args.CornerTreatment.Mode} created length " +
            $"{args.Snapshot!.Value.Length:G17}, width " +
            $"{args.Snapshot.Value.Width:G17}, area " +
            $"{args.Snapshot.Value.EnclosedArea:G17}.",
        CadRectangleAuthoringStage.Canceled => "RECTANG canceled.",
        CadRectangleAuthoringStage.Failed =>
            $"RECTANG failed: {args.ErrorMessage}",
        _ => throw new ArgumentOutOfRangeException(nameof(args)),
    };

    private static string DescribeRectangleConstruction(
        CadRectangleConstruction construction) => construction.Mode switch
    {
        CadRectangleConstructionMode.DiagonalCorners => "2 Corners",
        CadRectangleConstructionMode.Dimensions =>
            $"Dimensions {construction.Length:G17} x {construction.Width:G17}",
        CadRectangleConstructionMode.Area =>
            $"Area {construction.Area:G17} with " +
            $"{construction.KnownDimension} {construction.KnownValue:G17}",
        _ => throw new ArgumentOutOfRangeException(nameof(construction)),
    };

    private static string DescribePolygonMode(CadPolygonAuthoringMode mode) =>
        mode switch
        {
            CadPolygonAuthoringMode.Inscribed => "Inscribed",
            CadPolygonAuthoringMode.Circumscribed => "Circumscribed",
            CadPolygonAuthoringMode.Edge => "Edge",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static string DescribePolygonPrompt(
        CadPolygonAuthoringInputKind inputKind) => inputKind switch
    {
        CadPolygonAuthoringInputKind.CenterPoint =>
            "specify the center point by click or absolute WCS coordinate",
        CadPolygonAuthoringInputKind.FirstEdgePoint =>
            "specify the first edge point by click or absolute WCS coordinate",
        CadPolygonAuthoringInputKind.RadiusPoint =>
            "specify a radius point by click/coordinate, or enter a positive radius",
        CadPolygonAuthoringInputKind.SecondEdgePoint =>
            "specify the second edge point by click, coordinate, or direct distance",
        _ => throw new ArgumentOutOfRangeException(nameof(inputKind)),
    };

    private void SelectPolarTrackingIncrement()
    {
        double target = _canvas.PlanPolarTrackingIncrementDegrees;
        for (int i = 0; i < _planPolarTrackingIncrementSelector.Items.Count; i++)
        {
            if (_planPolarTrackingIncrementSelector.Items[i] is
                    ComboBoxItem { Tag: double increment } &&
                Math.Abs(increment - target) <= 1e-10)
            {
                _planPolarTrackingIncrementSelector.SelectedIndex = i;
                return;
            }
        }
    }

    private void RefreshPlanConstraintControls()
    {
        _isRefreshingPlanConstraints = true;
        try
        {
            _planGridSnapCheckBox.IsChecked =
                _canvas.IsPlanGridSnapEnabled;
            _planOrthoCheckBox.IsChecked = _canvas.IsPlanOrthoEnabled;
            _planPolarTrackingCheckBox.IsChecked =
                _canvas.IsPlanPolarTrackingEnabled;
            _planPolarRelativeCheckBox.IsChecked =
                _canvas.PlanPolarAngleMeasurement ==
                    CadPlanPolarAngleMeasurement.RelativeToLastSegment;
            _planPolarAdditionalAnglesCheckBox.IsChecked =
                _canvas.UsePlanPolarAdditionalAngles;
            _planPolarSnapCheckBox.IsChecked =
                _canvas.IsPlanPolarSnapEnabled;
            SelectPolarTrackingIncrement();
        }
        finally
        {
            _isRefreshingPlanConstraints = false;
        }
    }

    private static string DescribePointTransformOperation(
        CadPointTransformOperation operation) => operation switch
    {
        CadPointTransformOperation.Move => "Move by points",
        CadPointTransformOperation.Copy => "Copy by points",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static string FormatPoint(CadPoint3D point) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"({point.X:G17}, {point.Y:G17}, {point.Z:G17})");

    private void MoveSelection(double xDirection, double yDirection)
    {
        if (_isBusy)
        {
            return;
        }
        if (!double.TryParse(
                _moveStepInput.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double step) ||
            !double.IsFinite(step) ||
            step <= 0.0)
        {
            SetStatus("Move failed: the WCS step must be a finite positive invariant number.");
            return;
        }

        try
        {
            var translation = new CadPoint3D(
                xDirection * step,
                yDirection * step,
                0);
            if (_is3DView && _selectedMeshSubobjects.Count > 0)
            {
                TranslateSelectedMeshSubobjects(translation);
            }
            else if (!_canvas.TranslateSelection(translation))
            {
                SetStatus("Move requires at least one selected entity.");
                return;
            }
            SetStatus(DescribeCurrentDocument(
                _currentDocumentName,
                _currentDiagnosticCount));
        }
        catch (Exception exception)
        {
            SetStatus($"Move failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void TranslateSelectedMeshSubobjects(CadPoint3D translation)
    {
        CadRecordedMesh3DScene scene = _mesh3DView.Scene ??
            throw new InvalidOperationException("No retained Mesh3D scene is available.");
        MeshSubobjectEditSelection[] remap =
            CaptureMeshSubobjectEditSelection(scene);
        _canvas.TranslateMeshSubobjects(
            scene,
            _selectedMeshSubobjects,
            translation);
        RemapMeshSubobjectEditSelection(remap);
    }

    private MeshSubobjectEditSelection[] CaptureMeshSubobjectEditSelection(
        CadRecordedMesh3DScene scene)
    {
        var remap = new MeshSubobjectEditSelection[
            _selectedMeshSubobjects.Count];
        for (int index = 0; index < _selectedMeshSubobjects.Count; index++)
        {
            CadMesh3DSubobjectId id = _selectedMeshSubobjects[index];
            if (!scene.TryGetSubobjectComponent(
                    id,
                    out CadMesh3DSubobjectComponent? component) ||
                component is null)
            {
                throw new InvalidOperationException(
                    "The selected mesh subobject belongs to a stale scene generation.");
            }
            remap[index] = new MeshSubobjectEditSelection(
                component.SourceHandle,
                id.Kind,
                id.Index);
        }
        return remap;
    }

    private void RemapMeshSubobjectEditSelection(
        ReadOnlySpan<MeshSubobjectEditSelection> remap)
    {
        CadRecordedMesh3DScene replacement = _mesh3DView.Scene ??
            throw new InvalidOperationException(
                "The edited Mesh3D scene was not rebuilt.");
        _selectedMeshSubobjects.Clear();
        foreach (MeshSubobjectEditSelection selection in remap)
        {
            foreach (CadMesh3DSubobjectComponent component in
                     replacement.SubobjectComponents.Span)
            {
                if (!component.IsDirectModelSpaceSource ||
                    component.SourceHandle != selection.SourceHandle)
                {
                    continue;
                }
                int count = selection.Kind switch
                {
                    CadMesh3DSubobjectKind.Vertex =>
                        component.VertexPositions.Length,
                    CadMesh3DSubobjectKind.Edge => component.Edges.Length,
                    CadMesh3DSubobjectKind.Face => component.Faces.Length,
                    _ => 0,
                };
                if ((uint)selection.Index >= (uint)count)
                {
                    break;
                }
                _selectedMeshSubobjects.Add(new CadMesh3DSubobjectId(
                    replacement.ContentGeneration,
                    component.Handle,
                    component.ComponentIndex,
                    selection.Kind,
                    selection.Index));
                break;
            }
        }
        ResetMeshSubobjectCycle();
        RefreshMeshSubobjectOverlay();
    }

    private void CopySelection(double xDirection, double yDirection)
    {
        if (_isBusy)
        {
            return;
        }
        if (!double.TryParse(
                _moveStepInput.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double step) ||
            !double.IsFinite(step) ||
            step <= 0.0)
        {
            SetStatus("Copy failed: the WCS step must be a finite positive invariant number.");
            return;
        }
        if (!int.TryParse(
                _copyArrayItemsInput.Text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int itemCount) ||
            itemCount < 2)
        {
            SetStatus("Copy failed: array items must be an invariant integer of at least 2, including the source.");
            return;
        }
        if ((_copyArrayModeSelector.SelectedItem as ComboBoxItem)?.Tag is not
            CadLinearCopyMode mode)
        {
            SetStatus("Copy failed: select Step or Fit spacing.");
            return;
        }

        int selectedCount = _canvas.SelectedHandleCount;
        try
        {
            var displacement = new CadPoint3D(
                xDirection * step,
                yDirection * step,
                0);
            if (!_canvas.DuplicateSelectionLinearArray(
                    displacement,
                    itemCount,
                    mode))
            {
                SetStatus("Copy requires at least one selected entity.");
                return;
            }
            SetStatus(
                $"Created {(itemCount - 1) * selectedCount:N0} copy/copies " +
                $"from {selectedCount:N0} selected entity(s) using {mode} " +
                $"displacement ({displacement.X:G}, {displacement.Y:G}, 0) WCS.");
        }
        catch (Exception exception)
        {
            SetStatus($"Copy failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void RotateSelection(double direction)
    {
        if (_isBusy)
        {
            return;
        }
        if (!double.TryParse(
                _rotationStepInput.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double degrees) ||
            !double.IsFinite(degrees) ||
            degrees <= 0.0)
        {
            SetStatus(
                "Rotate failed: degrees must be a finite positive invariant number.");
            return;
        }

        double radians = direction * degrees * (Math.PI / 180.0);
        if (!double.IsFinite(radians))
        {
            SetStatus("Rotate failed: the angle exceeds finite rotation coordinates.");
            return;
        }

        try
        {
            if (_is3DView && _selectedMeshSubobjects.Count > 0)
            {
                CadRecordedMesh3DScene scene = _mesh3DView.Scene ??
                    throw new InvalidOperationException(
                        "No retained Mesh3D scene is available.");
                MeshSubobjectEditSelection[] remap =
                    CaptureMeshSubobjectEditSelection(scene);
                _canvas.RotateMeshSubobjects(
                    scene,
                    _selectedMeshSubobjects,
                    new CadPoint3D(0, 0, 1),
                    radians);
                RemapMeshSubobjectEditSelection(remap);
            }
            else if (!_canvas.RotateSelection(radians))
            {
                SetStatus("Rotate requires at least one selected entity.");
                return;
            }
            SetStatus(DescribeCurrentDocument(
                _currentDocumentName,
                _currentDiagnosticCount));
        }
        catch (Exception exception)
        {
            SetStatus($"Rotate failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void ScaleSelection(bool useReciprocal)
    {
        if (_isBusy)
        {
            return;
        }
        if (!double.TryParse(
                _scaleFactorInput.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double factor) ||
            !double.IsFinite(factor) ||
            factor <= 0.0 ||
            factor == 1.0)
        {
            SetStatus(
                "Scale failed: the factor must be positive, finite, non-unit, and invariant.");
            return;
        }

        double appliedFactor = useReciprocal ? 1.0 / factor : factor;
        if (!double.IsFinite(appliedFactor) || appliedFactor <= 0.0)
        {
            SetStatus("Scale failed: the factor does not have a finite positive reciprocal.");
            return;
        }

        try
        {
            if (_is3DView && _selectedMeshSubobjects.Count > 0)
            {
                CadRecordedMesh3DScene scene = _mesh3DView.Scene ??
                    throw new InvalidOperationException(
                        "No retained Mesh3D scene is available.");
                MeshSubobjectEditSelection[] remap =
                    CaptureMeshSubobjectEditSelection(scene);
                _canvas.ScaleMeshSubobjects(
                    scene,
                    _selectedMeshSubobjects,
                    appliedFactor);
                RemapMeshSubobjectEditSelection(remap);
            }
            else if (!_canvas.ScaleSelection(appliedFactor))
            {
                SetStatus("Scale requires at least one selected entity.");
                return;
            }
            SetStatus(DescribeCurrentDocument(
                _currentDocumentName,
                _currentDiagnosticCount));
        }
        catch (Exception exception)
        {
            SetStatus($"Scale failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void AdjustSelectedMeshSmoothness(int delta)
    {
        if (_isBusy)
        {
            return;
        }
        try
        {
            CadMesh3DSmoothnessSummary summary =
                _canvas.AdjustSelectedMeshSubdivisionLevel(delta);
            SetStatus(
                $"{(delta > 0 ? "Increased" : "Decreased")} smoothness for " +
                $"{summary.AffectedMeshCount:N0} modern mesh(es); result levels " +
                $"{summary.MinimumResultLevel:N0}-{summary.MaximumResultLevel:N0}.");
        }
        catch (Exception exception)
        {
            SetStatus($"Mesh smoothing failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void SetSelectedMeshCrease()
    {
        if (!TryParseMeshCrease(_meshCreaseInput.Text, out double creaseValue))
        {
            SetStatus(
                "Mesh crease failed: enter -1 (Always), zero (None), or a finite positive invariant level.");
            return;
        }
        SetSelectedMeshCrease(creaseValue);
    }

    private void SetSelectedMeshCrease(double creaseValue)
    {
        if (_isBusy)
        {
            return;
        }
        try
        {
            CadRecordedMesh3DScene scene = _mesh3DView.Scene ??
                throw new InvalidOperationException(
                    "No retained Mesh3D scene is available.");
            MeshSubobjectEditSelection[] remap =
                CaptureMeshSubobjectEditSelection(scene);
            CadMesh3DCreaseSummary summary = _canvas.SetMeshSubobjectCrease(
                scene,
                _selectedMeshSubobjects,
                creaseValue);
            RemapMeshSubobjectEditSelection(remap);
            SetStatus(
                creaseValue == 0.0
                    ? $"Removed crease from {summary.AffectedEdgeCount:N0} authored mesh edge(s)."
                    : $"Set crease {creaseValue:G17} on {summary.AffectedEdgeCount:N0} authored mesh edge(s).");
        }
        catch (Exception exception)
        {
            SetStatus($"Mesh crease failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private static bool TryParseMeshCrease(
        string text,
        out double creaseValue) =>
        double.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out creaseValue) &&
        double.IsFinite(creaseValue) &&
        (creaseValue >= 0.0 || creaseValue == -1.0);

    private void SetSelectionDrawOrder(CadDrawOrderPlacement placement)
    {
        if (_isBusy)
        {
            return;
        }
        try
        {
            if (!_canvas.SetSelectionDrawOrder(placement))
            {
                SetStatus("Draw order requires at least one selected entity.");
                return;
            }
            SetStatus(placement == CadDrawOrderPlacement.BringToFront
                ? "Moved the selection to the front."
                : "Moved the selection to the back.");
        }
        catch (Exception exception)
        {
            SetStatus($"Draw order failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void BeginSelectionDrawOrderReferencePick(
        CadDrawOrderPlacement placement)
    {
        if (_isBusy)
        {
            return;
        }
        try
        {
            if (!_canvas.BeginSelectionDrawOrderReferencePick(placement))
            {
                SetStatus("Draw order requires at least one selected entity.");
                return;
            }
            SetStatus(DescribeDrawOrderReferencePick());
        }
        catch (Exception exception)
        {
            SetStatus($"Draw order failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void CommitSelectionDrawOrderReferencePick()
    {
        if (_isBusy || _canvas.PendingDrawOrderPlacement is null)
        {
            return;
        }

        CadDrawOrderPlacement placement =
            _canvas.PendingDrawOrderPlacement.Value;
        int referenceCount = _canvas.DrawOrderReferenceHandleCount;
        try
        {
            if (!_canvas.CommitSelectionDrawOrderReferencePick())
            {
                SetStatus(
                    "Select at least one unselected reference object, then press Enter; Escape cancels.");
                return;
            }
            SetStatus(placement == CadDrawOrderPlacement.BringAbove
                ? $"Moved the selection immediately above {referenceCount:N0} reference object(s)."
                : $"Moved the selection immediately under {referenceCount:N0} reference object(s).");
        }
        catch (Exception exception)
        {
            SetStatus($"Draw order failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void CancelSelectionDrawOrderReferencePick()
    {
        if (!_canvas.CancelSelectionDrawOrderReferencePick())
        {
            return;
        }
        SetStatus("Draw-order reference selection canceled.");
        UpdateEditControls();
    }

    private string DescribeDrawOrderReferencePick()
    {
        string placement = _canvas.PendingDrawOrderPlacement ==
            CadDrawOrderPlacement.BringAbove
            ? "above"
            : "under";
        string unsupported =
            _canvas.LastDrawOrderReferenceUnsupportedPrimitiveCount == 0
                ? string.Empty
                : $" | {_canvas.LastDrawOrderReferenceUnsupportedPrimitiveCount:N0} unsupported candidates";
        string truncated = _canvas.LastDrawOrderReferenceSelectionWasTruncated
            ? " | truncated"
            : string.Empty;
        return $"Select reference objects for {placement}: " +
            $"{_canvas.DrawOrderReferenceHandleCount:N0} accumulated; " +
            "click/drag adds, Enter commits, Escape cancels" +
            unsupported + truncated;
    }

    private void PerformUndo()
    {
        if (_isBusy)
        {
            return;
        }
        try
        {
            if (!_canvas.TryUndo())
            {
                SetStatus("There is no synchronized CAD edit to undo.");
                return;
            }
            SetStatus(DescribeCurrentDocument(
                _currentDocumentName,
                _currentDiagnosticCount));
        }
        catch (Exception exception)
        {
            SetStatus($"Undo failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void PerformDelete()
    {
        if (_isBusy)
        {
            return;
        }
        try
        {
            if (_is3DView && _selectedMeshSubobjects.Count > 0)
            {
                CadRecordedMesh3DScene scene = _mesh3DView.Scene ??
                    throw new InvalidOperationException(
                        "No retained Mesh3D scene is available.");
                CadMesh3DDeletionSummary summary =
                    _canvas.DeleteMeshSubobjects(
                        scene,
                        _selectedMeshSubobjects);
                ClearMeshSubobjectSelection();
                SetStatus(
                    $"Deleted {summary.DeletedFaceCount:N0} authored mesh face(s) " +
                    $"from {summary.AffectedMeshCount:N0} mesh(es)" +
                    (summary.CompactedControlVertexCount == 0
                        ? string.Empty
                        : $" and compacted {summary.CompactedControlVertexCount:N0} control vertices") +
                    (summary.RemovedMeshEntityCount == 0
                        ? "."
                        : $"; removed {summary.RemovedMeshEntityCount:N0} empty mesh entity/entities."));
                return;
            }
            int selectedCount = _canvas.SelectedHandleCount;
            if (!_canvas.DeleteSelection())
            {
                SetStatus("Delete requires at least one selected entity.");
                return;
            }
            SetStatus(
                selectedCount == 1
                    ? "Deleted one entity."
                    : $"Deleted {selectedCount} entities as one edit.");
        }
        catch (Exception exception)
        {
            SetStatus($"Delete failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private void PerformRedo()
    {
        if (_isBusy)
        {
            return;
        }
        try
        {
            if (!_canvas.TryRedo())
            {
                SetStatus("There is no synchronized CAD edit to redo.");
                return;
            }
            SetStatus(DescribeCurrentDocument(
                _currentDocumentName,
                _currentDiagnosticCount));
        }
        catch (Exception exception)
        {
            SetStatus($"Redo failed: {exception.Message}");
        }
        finally
        {
            UpdateEditControls();
        }
    }

    private async Task OpenAsync()
    {
        if (!TryBeginOperation("Choose a DXF or DWG file..."))
        {
            return;
        }

        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".dxf");
            picker.FileTypeFilter.Add(".dwg");
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                SetStatus("Open cancelled.");
                return;
            }

            SetStatus($"Opening {file.Name}...");
            byte[] bytes = await file.ReadBytesAsync();
            using var source = new MemoryStream(bytes, writable: false);
            CadLoadResult result = await _store.LoadAsync(
                source,
                CadDocumentFormat.Auto,
                sourceName: file.Name);
            CadShxFontDiscoveryResult? shxDiscovery =
                await DiscoverLocalShxFontsAsync(file, result.Session);
            _canvas.Load(result.Session);
            int diagnosticCount = result.Diagnostics.Count +
                (shxDiscovery?.Diagnostics.Length ?? 0);
            _currentDocumentName = file.Name;
            _currentDiagnosticCount = diagnosticCount;
            string status = DescribeCurrentDocument(file.Name, diagnosticCount);
            if (shxDiscovery is not null)
            {
                status += $" | SHX {shxDiscovery.LoadedFontNames.Length:N0} loaded, " +
                    $"{shxDiscovery.MissingFontCount:N0} missing, " +
                    $"{shxDiscovery.InvalidFontCount:N0} rejected";
            }
            SetStatus(status);
        }
        catch (Exception exception)
        {
            SetStatus($"Open failed: {exception.Message}");
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task ImportPageSetupsAsync(
        CadPageSetupImportConflictPolicy conflictPolicy)
    {
        if (_canvas.CurrentSession is null ||
            !TryBeginOperation(
                conflictPolicy == CadPageSetupImportConflictPolicy.Reject
                    ? "Choose a DXF or DWG page-setup source..."
                    : "Choose a DXF or DWG page-setup source to import and replace..."))
        {
            return;
        }

        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".dxf");
            picker.FileTypeFilter.Add(".dwg");
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                SetStatus("Page-setup import cancelled.");
                return;
            }

            SetStatus($"Reading page setups from {file.Name}...");
            byte[] bytes = await file.ReadBytesAsync();
            using var source = new MemoryStream(bytes, writable: false);
            CadLoadResult loaded = await _store.LoadAsync(
                source,
                CadDocumentFormat.Auto,
                sourceName: file.Name);
            ImportPageSetups(
                loaded.Session,
                conflictPolicy,
                file.Name,
                loaded.Diagnostics.Count);
        }
        catch (Exception exception)
        {
            SetStatus($"Page-setup import failed: {exception.Message}");
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task ImportLineTypesAsync(
        CadLineTypeImportConflictPolicy conflictPolicy)
    {
        if (_canvas.CurrentSession is null ||
            !TryBeginOperation(
                conflictPolicy == CadLineTypeImportConflictPolicy.Reject
                    ? "Choose a LIN linetype library..."
                    : "Choose a LIN linetype library to reload..."))
        {
            return;
        }

        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".lin");
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                SetStatus("LIN import cancelled.");
                return;
            }

            SetStatus($"Reading linetypes from {file.Name}...");
            byte[] bytes = await file.ReadBytesAsync();
            ImportLineTypes(
                CadLinFile.Parse(bytes),
                conflictPolicy,
                file.Name);
        }
        catch (Exception exception)
        {
            SetStatus($"LIN import failed: {exception.Message}");
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Imports one parsed LIN library. This typed seam is shared by the
    /// desktop/browser picker and deterministic host tests.
    /// </summary>
    public CadLineTypeImportResult ImportLineTypes(
        CadLinFile file,
        CadLineTypeImportConflictPolicy conflictPolicy,
        string sourceName = "library.lin")
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        CadLineTypeImportResult result = _canvas.ImportLineTypes(
            file,
            conflictPolicy);
        SetStatus(
            $"Imported {result.ImportedCount:N0} linetype(s) from " +
            $"{sourceName} as one edit: {result.CreatedCount:N0} new, " +
            $"{result.ReplacedCount:N0} reloaded" +
            (result.UnsupportedCount == 0
                ? "."
                : $"; {result.UnsupportedCount:N0} upright U= definition(s) " +
                  "were left unchanged."));
        UpdateEditControls();
        return result;
    }

    /// <summary>
    /// Imports all named setups from a loaded source. This typed seam is shared
    /// by desktop/browser pickers and deterministic host tests.
    /// </summary>
    public CadPageSetupImportResult ImportPageSetups(
        CadDocumentSession source,
        CadPageSetupImportConflictPolicy conflictPolicy,
        string sourceName = "drawing",
        int diagnosticCount = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentOutOfRangeException.ThrowIfNegative(diagnosticCount);
        CadPageSetupImportResult result = _canvas.ImportNamedPageSetups(
            source,
            conflictPolicy);
        SetStatus(
            $"Imported {result.ImportedCount:N0} named page setup(s) from " +
            $"{sourceName} as one edit: {result.CreatedCount:N0} new, " +
            $"{result.ReplacedCount:N0} replaced" +
            (diagnosticCount == 0
                ? "."
                : $"; source reported {diagnosticCount:N0} diagnostic(s)."));
        UpdateEditControls();
        return result;
    }

    private async Task<CadShxFontDiscoveryResult?> DiscoverLocalShxFontsAsync(
        StorageFile file,
        CadDocumentSession session)
    {
        if (!Path.IsPathFullyQualified(file.Path) || !File.Exists(file.Path))
        {
            return null;
        }
        string? drawingDirectory = Path.GetDirectoryName(file.Path);
        if (string.IsNullOrEmpty(drawingDirectory))
        {
            return null;
        }

        return await CadShxFontDiscovery.DiscoverAsync(
            session,
            ShxFonts,
            new CadShxFontDiscoveryOptions
            {
                DrawingDirectory = drawingDirectory,
                SupportDirectories = _shxSupportDirectories.ToArray(),
            });
    }

    private async Task SaveAsAsync()
    {
        CadDocumentSession? session = _canvas.CurrentSession;
        if (session is null || !TryBeginOperation("Choose a DXF or DWG destination..."))
        {
            return;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = SuggestedFileName(session),
            };
            picker.FileTypeChoices.Add("AutoCAD DXF", new List<string> { ".dxf" });
            picker.FileTypeChoices.Add("AutoCAD DWG", new List<string> { ".dwg" });
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                SetStatus("Save cancelled.");
                return;
            }

            CadDocumentFormat format = file.FileType.ToLowerInvariant() switch
            {
                ".dxf" => CadDocumentFormat.Dxf,
                ".dwg" => CadDocumentFormat.Dwg,
                _ => throw new InvalidOperationException(
                    "The destination must use a .dxf or .dwg extension."),
            };
            SetStatus($"Saving {file.Name} using uncertified development output...");
            using var destination = new MemoryStream();
            CadSaveResult result = await _store.SaveAsync(
                session,
                destination,
                format,
                new CadSaveOptions
                {
                    AllowUncertifiedWrite = true,
                    DeferSavedGenerationCommit = true,
                });
            await file.WriteBytesAsync(destination.ToArray());
            if (!result.CommitSavedGeneration())
            {
                throw new InvalidOperationException(
                    "The saved generation was superseded before it could be committed.");
            }
            SetStatus(
                $"Saved {file.Name} at generation {result.SavedGeneration}; " +
                $"writer certification remains pending ({result.Diagnostics.Count} diagnostics).");
        }
        catch (Exception exception)
        {
            SetStatus($"Save failed: {exception.Message}");
        }
        finally
        {
            EndOperation();
        }
    }

    private bool TryBeginOperation(string status)
    {
        if (_isBusy)
        {
            return false;
        }

        _isBusy = true;
        _openButton.IsEnabled = false;
        _loadLineTypesButton.IsEnabled = false;
        _reloadLineTypesButton.IsEnabled = false;
        _importPageSetupsButton.IsEnabled = false;
        _importReplacePageSetupsButton.IsEnabled = false;
        _saveButton.IsEnabled = false;
        UpdateEditControls();
        SetStatus(status);
        return true;
    }

    private void EndOperation()
    {
        _isBusy = false;
        _openButton.IsEnabled = true;
        _loadLineTypesButton.IsEnabled = true;
        _reloadLineTypesButton.IsEnabled = true;
        _importPageSetupsButton.IsEnabled = true;
        _importReplacePageSetupsButton.IsEnabled = true;
        _saveButton.IsEnabled = true;
        _exportPdfButton.IsEnabled = true;
        _exportPngButton.IsEnabled = true;
        UpdateEditControls();
    }

    private void UpdateEditControls()
    {
        EnsureLayerMergeSourcesAreCurrent();
        bool isReferencePicking =
            _canvas.PendingDrawOrderPlacement is not null;
        bool isPointTransformPicking =
            _canvas.PendingPointTransformOperation is not null;
        bool isLineAuthoring = _canvas.IsLineAuthoring;
        bool isRayAuthoring = _canvas.IsRayAuthoring;
        bool isXLineAuthoring = _canvas.IsXLineAuthoring;
        bool isPointAuthoring = _canvas.IsPointAuthoring;
        bool isPolylineAuthoring = _canvas.IsPolylineAuthoring;
        bool isCircleAuthoring = _canvas.IsCircleAuthoring;
        bool isArcAuthoring = _canvas.IsArcAuthoring;
        bool isEllipseAuthoring = _canvas.IsEllipseAuthoring;
        bool isPolygonAuthoring = _canvas.IsPolygonAuthoring;
        bool isRectangleAuthoring = _canvas.IsRectangleAuthoring;
        bool isPointInputActive =
            isPointTransformPicking || isLineAuthoring || isRayAuthoring ||
            isXLineAuthoring ||
            isPointAuthoring ||
            isPolylineAuthoring || isCircleAuthoring || isArcAuthoring ||
            isEllipseAuthoring || isPolygonAuthoring || isRectangleAuthoring;
        bool isInteractivePicking =
            isReferencePicking || isPointInputActive;
        bool canUsePlanTools =
            !_isBusy && !_isPrintPreview && !isInteractivePicking;
        _openButton.IsEnabled = canUsePlanTools;
        bool canImportLineTypes = canUsePlanTools &&
            _canvas.CurrentSession is not null;
        _loadLineTypesButton.IsEnabled = canImportLineTypes;
        _reloadLineTypesButton.IsEnabled = canImportLineTypes;
        bool canImportPageSetups = canUsePlanTools &&
            _canvas.CurrentSession is not null;
        _importPageSetupsButton.IsEnabled = canImportPageSetups;
        _importReplacePageSetupsButton.IsEnabled = canImportPageSetups;
        _saveButton.IsEnabled = !_isBusy && !isInteractivePicking;
        _fitButton.IsEnabled = canUsePlanTools &&
            (!_is3DView || _viewport3D.Children.Count > 0);
        _clearSelectionButton.IsEnabled = canUsePlanTools;
        _attributeDisplaySelector.IsEnabled =
            canUsePlanTools && _canvas.CurrentSession is not null;
        _printPreviewButton.IsEnabled =
            !_isBusy && !isInteractivePicking &&
            (_isPrintPreview || _canvas.CurrentSnapshot is not null);
        bool canExport = !_isBusy && !isInteractivePicking &&
            _canvas.CurrentSnapshot is not null &&
            (_pageSetupSelector.SelectedItem as ComboBoxItem)?.Tag is
                PageSetupChoice exportChoice &&
            (exportChoice.IsFallback || exportChoice.Lowering?.IsSupported == true);
        _exportPdfButton.IsEnabled = canExport;
        _exportPngButton.IsEnabled = canExport;
        _pageSetupSelector.IsEnabled =
            !_isBusy && !isInteractivePicking &&
            _canvas.CurrentSession is not null;
        _applyPageSetupButton.IsEnabled =
            canUsePlanTools &&
            (_pageSetupSelector.SelectedItem as ComboBoxItem)?.Tag is
                PageSetupChoice { CanApplyToModel: true };
        _pageSetupNameInput.IsEnabled = canUsePlanTools;
        _createPageSetupButton.IsEnabled =
            canUsePlanTools && CanCreateNamedPageSetup(_pageSetupNameInput.Text);
        _updatePageSetupButton.IsEnabled =
            canUsePlanTools &&
            (_pageSetupSelector.SelectedItem as ComboBoxItem)?.Tag is
                PageSetupChoice { CanApplyToModel: true };
        _deletePageSetupButton.IsEnabled =
            canUsePlanTools && CanDeleteSelectedNamedPageSetup();
        _renamePageSetupButton.IsEnabled =
            canUsePlanTools && CanRenameSelectedNamedPageSetup();
        bool canEditPageSetupFields = canUsePlanTools &&
            (_pageSetupSelector.SelectedItem as ComboBoxItem)?.Tag is
                PageSetupChoice { PageSetup: not null };
        _pageSetupPaperWidthInput.IsEnabled = canEditPageSetupFields;
        _pageSetupPaperHeightInput.IsEnabled = canEditPageSetupFields;
        _pageSetupRotationSelector.IsEnabled = canEditPageSetupFields;
        _pageSetupPlotAreaSelector.IsEnabled = canEditPageSetupFields;
        _pageSetupCenterCheckBox.IsEnabled = canEditPageSetupFields;
        _pageSetupLineweightsCheckBox.IsEnabled = canEditPageSetupFields;
        _editPageSetupFieldsButton.IsEnabled =
            canEditPageSetupFields &&
            TryCreatePageSetupFieldPatch(out _, out _);
        _undoButton.IsEnabled =
            canUsePlanTools && _canvas.UndoCount > 0;
        _redoButton.IsEnabled =
            canUsePlanTools && _canvas.RedoCount > 0;
        _viewModeButton.IsEnabled =
            canUsePlanTools && _viewport3D.Children.Count > 0;
        _meshPickTargetSelector.IsEnabled =
            canUsePlanTools && _is3DView;
        _meshRegionSelectionSelector.IsEnabled =
            canUsePlanTools && _is3DView;
        _meshSubobjectSelector.IsEnabled =
            canUsePlanTools && _is3DView;
        bool canTransform = canUsePlanTools &&
            _canvas.SelectedHandleCount > 0 &&
            _isSelectionEditable;
        bool canEditMeshSubobjects = canUsePlanTools && _is3DView &&
            _selectedMeshSubobjects.Count > 0;
        _deleteButton.IsEnabled = canTransform || canEditMeshSubobjects;
        bool canEditSelectedMeshes = canUsePlanTools && _is3DView &&
            _isSelectionEditable && _isMeshSelection;
        _meshSmoothMoreButton.IsEnabled = canEditSelectedMeshes &&
            (_commonMeshSubdivisionLevel is null or <
                CadSnapshotOptions.DefaultMaxMeshSubdivisionLevel);
        _meshSmoothLessButton.IsEnabled = canEditSelectedMeshes &&
            (_commonMeshSubdivisionLevel is null or > 0);
        _meshCreaseInput.IsEnabled = canEditMeshSubobjects;
        _setMeshCreaseButton.IsEnabled = canEditMeshSubobjects &&
            TryParseMeshCrease(_meshCreaseInput.Text, out _);
        _removeMeshCreaseButton.IsEnabled = canEditMeshSubobjects;
        _lineButton.IsEnabled =
            canUsePlanTools && !_is3DView &&
            _canvas.CurrentSession is not null;
        _lineUndoButton.IsEnabled =
            !_isBusy && isLineAuthoring &&
            _canvas.PendingLineSegmentCount > 0;
        _lineCloseButton.IsEnabled =
            !_isBusy && isLineAuthoring &&
            _canvas.CanCloseLineAuthoring;
        _lineFinishButton.IsEnabled =
            !_isBusy && isLineAuthoring;
        _rayButton.IsEnabled =
            canUsePlanTools && !_is3DView &&
            _canvas.CurrentSession is not null;
        _rayUndoButton.IsEnabled =
            !_isBusy && isRayAuthoring &&
            _canvas.PendingRayCount > 0;
        _rayFinishButton.IsEnabled =
            !_isBusy && isRayAuthoring;
        _xlineModeSelector.IsEnabled =
            canUsePlanTools && !_is3DView && !isXLineAuthoring &&
            _canvas.CurrentSession is not null;
        _xlineButton.IsEnabled =
            canUsePlanTools && !_is3DView &&
            _canvas.CurrentSession is not null;
        _xlineUndoButton.IsEnabled =
            !_isBusy && isXLineAuthoring &&
            _canvas.PendingXLineCount > 0;
        _xlineFinishButton.IsEnabled =
            !_isBusy && isXLineAuthoring;
        _pointButton.IsEnabled =
            canUsePlanTools && !_is3DView &&
            _canvas.CurrentSession is not null;
        _polylineButton.IsEnabled =
            canUsePlanTools && !_is3DView &&
            _canvas.CurrentSession is not null;
        bool isPolylinePointPrompt = isPolylineAuthoring &&
            _canvas.PendingPolylinePrompt == CadPolylineAuthoringPrompt.Point;
        _polylineUndoButton.IsEnabled =
            !_isBusy && _canvas.CanUndoPolylineAuthoring;
        _polylineLineModeButton.IsEnabled =
            !_isBusy && isPolylinePointPrompt &&
            _canvas.PolylineAuthoringMode != CadPolylineAuthoringMode.Line;
        _polylineArcModeButton.IsEnabled =
            !_isBusy && isPolylinePointPrompt &&
            _canvas.PendingPolylineCurrentPoint is not null &&
            _canvas.PolylineAuthoringMode != CadPolylineAuthoringMode.TangentArc;
        _polylineArcConstructionSelector.IsEnabled =
            !_isBusy && _canvas.CanBeginPolylineArcConstruction;
        _polylineArcConstructionButton.IsEnabled =
            !_isBusy &&
            (_polylineArcConstructionSelector.SelectedItem as ComboBoxItem)?.Tag is
                CadPolylineArcConstruction construction &&
            _canvas.CanBeginPolylineArcConstructionOption(construction);
        _polylineWidthButton.IsEnabled =
            !_isBusy && _canvas.CanBeginPolylineWidthInput;
        _polylineHalfwidthButton.IsEnabled =
            !_isBusy && _canvas.CanBeginPolylineWidthInput;
        _polylineLengthButton.IsEnabled =
            !_isBusy && _canvas.CanBeginPolylineLengthInput;
        _polylineCloseButton.IsEnabled =
            !_isBusy && isPolylineAuthoring &&
            _canvas.CanClosePolylineAuthoring;
        _polylineFinishButton.IsEnabled =
            !_isBusy && isPolylinePointPrompt;
        bool canStartCircle = canUsePlanTools && !_is3DView &&
            _canvas.CurrentSession is not null;
        _circleButton.IsEnabled = canStartCircle;
        _circleDiameterButton.IsEnabled = canStartCircle;
        _circleTwoPointButton.IsEnabled = canStartCircle;
        _circleThreePointButton.IsEnabled = canStartCircle;
        bool canStartArc = canUsePlanTools && !_is3DView &&
            _canvas.CurrentSession is not null;
        _arcModeSelector.IsEnabled = canStartArc;
        _arcButton.IsEnabled = canStartArc &&
            (_arcModeSelector.SelectedItem as ComboBoxItem)?.Tag is
                CadArcAuthoringMode;
        bool canStartEllipse = canUsePlanTools && !_is3DView &&
            _canvas.CurrentSession is not null;
        _ellipseModeSelector.IsEnabled = canStartEllipse;
        CadEllipseAuthoringMode? selectedEllipseMode =
            (_ellipseModeSelector.SelectedItem as ComboBoxItem)?.Tag is
                CadEllipseAuthoringMode ellipseMode
                    ? ellipseMode
                    : null;
        bool selectedIsocircle = selectedEllipseMode is
            CadEllipseAuthoringMode.IsocircleRadius or
            CadEllipseAuthoringMode.IsocircleDiameter;
        _ellipseArcInputSelector.IsEnabled =
            canStartEllipse && !selectedIsocircle;
        _ellipseButton.IsEnabled = canStartEllipse &&
            selectedEllipseMode is not null &&
            (!selectedIsocircle ||
             _canvas.PlanGridSnapSettings.Style ==
                CadPlanGridSnapStyle.Isometric) &&
            (selectedIsocircle ||
             (_ellipseArcInputSelector.SelectedItem as ComboBoxItem)?.Tag is
                CadEllipseArcInputMode);
        bool canStartPolygon = canUsePlanTools && !_is3DView &&
            _canvas.CurrentSession is not null;
        _polygonSideCountInput.IsEnabled = canStartPolygon;
        _polygonModeSelector.IsEnabled = canStartPolygon;
        _polygonButton.IsEnabled = canStartPolygon &&
            CadPolygonSideCount.TryParse(
                _polygonSideCountInput.Text,
                out _) &&
            (_polygonModeSelector.SelectedItem as ComboBoxItem)?.Tag is
                CadPolygonAuthoringMode;
        bool canStartRectangle = canUsePlanTools && !_is3DView &&
            _canvas.CurrentSession is not null;
        _rectangleConstructionSelector.IsEnabled = canStartRectangle;
        CadRectangleConstructionMode? rectangleConstructionMode =
            (_rectangleConstructionSelector.SelectedItem as ComboBoxItem)?.Tag
                is CadRectangleConstructionMode selectedConstructionMode
                    ? selectedConstructionMode
                    : null;
        _rectangleAreaDimensionSelector.IsEnabled = canStartRectangle &&
            rectangleConstructionMode == CadRectangleConstructionMode.Area;
        _rectangleValuesInput.IsEnabled = canStartRectangle &&
            rectangleConstructionMode !=
                CadRectangleConstructionMode.DiagonalCorners;
        _rectangleCornerSelector.IsEnabled = canStartRectangle;
        CadRectangleCornerMode? rectangleCornerMode =
            (_rectangleCornerSelector.SelectedItem as ComboBoxItem)?.Tag
                is CadRectangleCornerMode selectedCornerMode
                    ? selectedCornerMode
                    : null;
        _rectangleCornerValuesInput.IsEnabled = canStartRectangle &&
            rectangleCornerMode != CadRectangleCornerMode.Sharp;
        _rectangleRotationInput.IsEnabled = canStartRectangle;
        _rectangleButton.IsEnabled = canStartRectangle &&
            TryCreateRectangleConfiguration(out _, out _, out _, out _);
        _selectionColorInput.IsEnabled = canTransform;
        _selectionLineWeightSelector.IsEnabled = canTransform;
        _selectionLayerSelector.IsEnabled = canTransform;
        _selectionLineTypeSelector.IsEnabled = canTransform;
        _selectionLineTypeScaleInput.IsEnabled = canTransform;
        _selectionTransparencyInput.IsEnabled = canTransform;
        _selectionVisibilitySelector.IsEnabled = canTransform;
        _selectionSolidThicknessInput.IsEnabled =
            canTransform && _isSolidThicknessSelection;
        CadAttributeValueEntry? selectedAttribute =
            (_selectionAttributeSelector.SelectedItem as ComboBoxItem)?.Tag is
                CadAttributeValueEntry entry
                ? entry
                : null;
        bool hasSelectedAttribute = selectedAttribute is not null;
        bool hasSelectedDefinitionAttribute = selectedAttribute is
            { Owner: not CadAttributeValueOwner.Reference };
        _selectionAttributeSelector.IsEnabled =
            canUsePlanTools &&
            _canvas.SelectedHandleCount == 1 &&
            _selectionAttributeSelector.Items.Count > 1;
        _selectionAttributeValueInput.IsEnabled =
            canTransform && hasSelectedAttribute;
        _selectionAttributePromptInput.IsEnabled =
            canTransform && hasSelectedDefinitionAttribute;
        _selectionAttributeTagInput.IsEnabled =
            canTransform && hasSelectedDefinitionAttribute;
        _selectionAttributeInvisibleCheckBox.IsEnabled =
            canTransform && hasSelectedDefinitionAttribute;
        _selectionAttributeVerifyCheckBox.IsEnabled =
            canTransform && hasSelectedDefinitionAttribute;
        _selectionAttributePresetCheckBox.IsEnabled =
            canTransform && hasSelectedDefinitionAttribute;
        _selectionAttributePositionLockedCheckBox.IsEnabled =
            canTransform && hasSelectedDefinitionAttribute;
        _selectionAttributeConstantCheckBox.IsEnabled =
            canTransform && hasSelectedDefinitionAttribute;
        _setSelectionColorButton.IsEnabled =
            canTransform &&
            TryParseSelectionColor(
                _selectionColorInput.Text,
                out _);
        _setSelectionLineWeightButton.IsEnabled =
            canTransform &&
            (_selectionLineWeightSelector.SelectedItem as ComboBoxItem)?.Tag is
                ACadSharp.LineWeightType;
        _setSelectionLayerButton.IsEnabled =
            canTransform &&
            (_selectionLayerSelector.SelectedItem as ComboBoxItem)?.Tag is string;
        _setSelectionLineTypeButton.IsEnabled =
            canTransform &&
            (_selectionLineTypeSelector.SelectedItem as ComboBoxItem)?.Tag is string;
        _setSelectionLineTypeScaleButton.IsEnabled =
            canTransform &&
            TryParsePositiveInvariantDouble(
                _selectionLineTypeScaleInput.Text,
                out _);
        _setSelectionTransparencyButton.IsEnabled =
            canTransform &&
            TryParseTransparency(
                _selectionTransparencyInput.Text,
                out _);
        _setSelectionVisibilityButton.IsEnabled =
            canTransform &&
            (_selectionVisibilitySelector.SelectedItem as ComboBoxItem)?.Tag is bool;
        _setSelectionSolidThicknessButton.IsEnabled =
            canTransform &&
            _isSolidThicknessSelection &&
            TryParseFiniteInvariantDouble(
                _selectionSolidThicknessInput.Text,
                out _);
        _setSelectionAttributeValueButton.IsEnabled =
            canTransform &&
            hasSelectedAttribute &&
            _selectionAttributeValueInput.Text.Length <=
                CadSetAttributeValueCommand.MaximumValueCodeUnits;
        _setSelectionAttributePromptButton.IsEnabled =
            canTransform &&
            hasSelectedDefinitionAttribute &&
            _selectionAttributePromptInput.Text.Length <=
                CadSetAttributeDefinitionPromptCommand.MaximumPromptCodeUnits;
        _setSelectionAttributeTagButton.IsEnabled =
            canTransform &&
            hasSelectedDefinitionAttribute &&
            CadSetAttributeDefinitionTagCommand.IsValidNewTag(
                _selectionAttributeTagInput.Text) &&
            !string.Equals(
                selectedAttribute?.Tag,
                _selectionAttributeTagInput.Text,
                StringComparison.OrdinalIgnoreCase);
        _setSelectionAttributeModesButton.IsEnabled =
            canTransform &&
            hasSelectedDefinitionAttribute &&
            selectedAttribute is CadAttributeValueEntry modesEntry &&
            HaveSelectedAttributeModesChanged(modesEntry);
        _setSelectionAttributeConstantButton.IsEnabled =
            canTransform &&
            hasSelectedDefinitionAttribute &&
            selectedAttribute is CadAttributeValueEntry constantEntry &&
            HasSelectedAttributeConstantModeChanged(constantEntry);
        _synchronizeSelectionAttributePropertiesButton.IsEnabled =
            canTransform &&
            _canvas.CanSynchronizeSelectedBlockAttributeProperties;
        bool canEditLayerState =
            canUsePlanTools &&
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag is string;
        _layerStateSelector.IsEnabled = canUsePlanTools;
        _layerVisibilitySelector.IsEnabled = canEditLayerState;
        _layerPlotSelector.IsEnabled = canEditLayerState;
        _layerFreezeSelector.IsEnabled = canEditLayerState;
        _layerLockSelector.IsEnabled = canEditLayerState;
        _layerColorInput.IsEnabled = canEditLayerState;
        _layerLineWeightSelector.IsEnabled = canEditLayerState;
        _layerLineTypeSelector.IsEnabled = canEditLayerState;
        _layerNameInput.IsEnabled = canEditLayerState;
        _layerMergeTargetSelector.IsEnabled = canEditLayerState;
        _setLayerVisibilityButton.IsEnabled =
            canEditLayerState &&
            (_layerVisibilitySelector.SelectedItem as ComboBoxItem)?.Tag is bool;
        _setLayerPlotButton.IsEnabled =
            canEditLayerState &&
            (_layerPlotSelector.SelectedItem as ComboBoxItem)?.Tag is bool;
        _setLayerFreezeButton.IsEnabled =
            canEditLayerState &&
            (_layerFreezeSelector.SelectedItem as ComboBoxItem)?.Tag is bool;
        _setLayerLockButton.IsEnabled =
            canEditLayerState &&
            (_layerLockSelector.SelectedItem as ComboBoxItem)?.Tag is bool;
        _setLayerColorButton.IsEnabled =
            canEditLayerState &&
            TryParseLayerColor(_layerColorInput.Text, out _);
        _setLayerLineWeightButton.IsEnabled =
            canEditLayerState &&
            (_layerLineWeightSelector.SelectedItem as ComboBoxItem)?.Tag is
                ACadSharp.LineWeightType;
        _setLayerLineTypeButton.IsEnabled =
            canEditLayerState &&
            (_layerLineTypeSelector.SelectedItem as ComboBoxItem)?.Tag is string;
        string? selectedLayerName =
            (_layerStateSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        _createLayerButton.IsEnabled =
            canEditLayerState &&
            _canvas.CanCreateLayer(_layerNameInput.Text);
        _renameLayerButton.IsEnabled =
            canEditLayerState &&
            _selectedLayerCanRename &&
            selectedLayerName is not null &&
            _canvas.CanRenameLayer(
                selectedLayerName,
                _layerNameInput.Text);
        _removeLayerButton.IsEnabled =
            canEditLayerState &&
            _selectedLayerCanRemove;
        _queueLayerMergeSourceButton.IsEnabled =
            canEditLayerState &&
            _selectedLayerCanRemove &&
            selectedLayerName is not null &&
            !_layerMergeSourceNameSet.Contains(selectedLayerName);
        _clearLayerMergeSourcesButton.IsEnabled =
            canUsePlanTools && _layerMergeSourceNames.Count > 0;
        string? mergeTargetLayerName =
            (_layerMergeTargetSelector.SelectedItem as ComboBoxItem)?.Tag as string;
        _mergeLayerButton.IsEnabled =
            canUsePlanTools &&
            _layerMergeSourceNames.Count > 0 &&
            mergeTargetLayerName is not null &&
            _canvas.CanMergeLayers(
                _layerMergeSourceNames,
                mergeTargetLayerName);
        _moveStepInput.IsEnabled = canUsePlanTools;
        _copyArrayItemsInput.IsEnabled = canUsePlanTools;
        _copyArrayModeSelector.IsEnabled = canUsePlanTools;
        _rotationStepInput.IsEnabled = canUsePlanTools;
        _scaleFactorInput.IsEnabled = canUsePlanTools;
        _objectSnapSelector.IsEnabled =
            !_isBusy && !_isPrintPreview && !_is3DView &&
            _canvas.CurrentSnapshot is not null;
        bool canUsePlanSnap =
            !_isBusy && !_isPrintPreview && !_is3DView &&
            _canvas.CurrentSnapshot is not null &&
            _canvas.PlanGridSnapSettings.IsSupported;
        _planGridSnapCheckBox.IsEnabled = canUsePlanSnap;
        bool canEditPlanGridDisplay =
            canUsePlanTools && !_is3DView &&
            _canvas.CurrentSnapshot is not null &&
            _canvas.PlanGridDisplaySettings.IsSupported;
        _planGridDisplayCheckBox.IsEnabled = canEditPlanGridDisplay;
        _planGridIsometricCheckBox.IsEnabled = canEditPlanGridDisplay;
        _planGridDotsCheckBox.IsEnabled =
            canEditPlanGridDisplay && !_planGridIsometricCheckBox.IsChecked;
        _planGridIsoplaneSelector.IsEnabled =
            canEditPlanGridDisplay && _planGridIsometricCheckBox.IsChecked;
        _planSnapUnitXInput.IsEnabled = canEditPlanGridDisplay;
        _planSnapUnitYInput.IsEnabled = canEditPlanGridDisplay;
        _planGridUnitXInput.IsEnabled = canEditPlanGridDisplay;
        _planGridUnitYInput.IsEnabled = canEditPlanGridDisplay;
        _planGridAdaptiveCheckBox.IsEnabled = canEditPlanGridDisplay;
        _planGridSubdivisionCheckBox.IsEnabled =
            canEditPlanGridDisplay && _planGridAdaptiveCheckBox.IsChecked;
        _planGridBeyondLimitsCheckBox.IsEnabled = canEditPlanGridDisplay;
        _planGridMajorInput.IsEnabled = canEditPlanGridDisplay;
        bool hasValidChangedPlanGridDisplay = false;
        if (canEditPlanGridDisplay &&
            TryCreatePlanGridDisplayEditValues(out var planGridValues))
        {
            try
            {
                hasValidChangedPlanGridDisplay =
                    _canvas.GetPlanGridDisplayEditValues() != planGridValues;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or ArgumentException)
            {
                hasValidChangedPlanGridDisplay = false;
            }
        }
        _applyPlanGridDisplayButton.IsEnabled =
            hasValidChangedPlanGridDisplay;
        _planOrthoCheckBox.IsEnabled =
            !_isBusy && !_isPrintPreview && !_is3DView &&
            _canvas.CurrentSnapshot is not null &&
            _canvas.PlanGridSnapSettings.IsSupported;
        _planPolarTrackingCheckBox.IsEnabled =
            !_isBusy && !_isPrintPreview && !_is3DView &&
            _canvas.CurrentSnapshot is not null &&
            _canvas.PlanPolarTrackingSettings.IsSupported;
        _planPolarTrackingIncrementSelector.IsEnabled =
            _planPolarTrackingCheckBox.IsEnabled;
        _planPolarRelativeCheckBox.IsEnabled =
            _planPolarTrackingCheckBox.IsEnabled;
        _planPolarAdditionalAnglesInput.IsEnabled =
            _planPolarTrackingCheckBox.IsEnabled;
        _planPolarAdditionalAnglesCheckBox.IsEnabled =
            _planPolarTrackingCheckBox.IsEnabled &&
            (_planPolarAdditionalAnglesCheckBox.IsChecked ||
             CadPlanPolarAdditionalAngles.TryParseInvariantDegrees(
                 _planPolarAdditionalAnglesInput.Text,
                 out _));
        _planPolarSnapDistanceInput.IsEnabled = canUsePlanSnap;
        _planPolarSnapCheckBox.IsEnabled =
            canUsePlanSnap &&
            (_planPolarSnapCheckBox.IsChecked ||
             TryParseNonNegativeInvariantDouble(
                 _planPolarSnapDistanceInput.Text,
                 out _));
        _pointTransformInput.IsEnabled =
            !_isBusy && isPointInputActive;
        _acceptPointTransformInputButton.IsEnabled =
            !_isBusy &&
            isPointInputActive &&
            (isPointAuthoring
                ? _canvas.CanAcceptPointAuthoringInput(
                    _pointTransformInput.Text)
                : isPolygonAuthoring
                ? _canvas.CanAcceptPolygonAuthoringInput(
                    _pointTransformInput.Text)
                : isEllipseAuthoring
                ? _canvas.CanAcceptEllipseAuthoringInput(
                    _pointTransformInput.Text)
                : isArcAuthoring
                ? _canvas.CanAcceptArcAuthoringInput(
                    _pointTransformInput.Text)
                : isCircleAuthoring
                ? _canvas.CanAcceptCircleAuthoringInput(
                    _pointTransformInput.Text)
                : isPolylineAuthoring
                ? _canvas.CanAcceptPolylineAuthoringInput(
                    _pointTransformInput.Text)
                : isLineAuthoring
                ? _canvas.CanAcceptLineAuthoringInput(
                    _pointTransformInput.Text)
                : isRayAuthoring
                ? _canvas.CanAcceptRayAuthoringInput(
                    _pointTransformInput.Text)
                : isXLineAuthoring
                ? _canvas.CanAcceptXLineAuthoringInput(
                    _pointTransformInput.Text)
                : _canvas.CanAcceptSelectionPointTransformInput(
                    _pointTransformInput.Text));
        foreach (Button drawOrderButton in _drawOrderButtons)
        {
            drawOrderButton.IsEnabled = canTransform;
        }
        bool canMoveMeshSubobjects = canEditMeshSubobjects;
        for (int index = 0; index < _moveButtons.Length; index++)
        {
            _moveButtons[index].IsEnabled = canTransform ||
                (canMoveMeshSubobjects && index < 4);
        }
        foreach (Button copyButton in _copyButtons)
        {
            copyButton.IsEnabled = canTransform;
        }
        foreach (Button rotateButton in _rotateButtons)
        {
            rotateButton.IsEnabled = canTransform || canMoveMeshSubobjects;
        }
        foreach (Button scaleButton in _scaleButtons)
        {
            scaleButton.IsEnabled = canTransform || canMoveMeshSubobjects;
        }
    }

    private bool CanCreateNamedPageSetup(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Length > CadCreateNamedPageSetupCommand.MaximumNameCodeUnits)
        {
            return false;
        }
        return !_pageSetupSelector.Items
            .OfType<ComboBoxItem>()
            .Any(item => item.Tag is PageSetupChoice
            {
                PageSetup:
                {
                    SourceKind: CadPageSetupSourceKind.NamedOverride,
                } pageSetup,
            } && string.Equals(
                pageSetup.Name,
                name,
                StringComparison.OrdinalIgnoreCase));
    }

    private bool CanDeleteSelectedNamedPageSetup()
    {
        if ((_pageSetupSelector.SelectedItem as ComboBoxItem)?.Tag is not
            PageSetupChoice
            {
                PageSetup:
                {
                    SourceKind: CadPageSetupSourceKind.NamedOverride,
                } named,
            })
        {
            return false;
        }

        return !_pageSetupSelector.Items
            .OfType<ComboBoxItem>()
            .Any(item => item.Tag is PageSetupChoice
            {
                PageSetup:
                {
                    SourceKind: CadPageSetupSourceKind.Layout,
                } layout,
            } && string.Equals(
                layout.PageSetupName,
                named.Name,
                StringComparison.OrdinalIgnoreCase));
    }

    private bool CanRenameSelectedNamedPageSetup()
    {
        if ((_pageSetupSelector.SelectedItem as ComboBoxItem)?.Tag is not
            PageSetupChoice
            {
                PageSetup:
                {
                    SourceKind: CadPageSetupSourceKind.NamedOverride,
                } named,
            })
        {
            return false;
        }

        string newName = _pageSetupNameInput.Text;
        if (string.IsNullOrWhiteSpace(newName) ||
            newName.Length > CadRenameNamedPageSetupCommand.MaximumNameCodeUnits ||
            string.Equals(named.Name, newName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !_pageSetupSelector.Items
            .OfType<ComboBoxItem>()
            .Any(item => item.Tag is PageSetupChoice
            {
                PageSetup:
                {
                    SourceKind: CadPageSetupSourceKind.NamedOverride,
                } candidate,
            } && string.Equals(
                candidate.Name,
                newName,
                StringComparison.OrdinalIgnoreCase));
    }

    private string DescribeCurrentDocument(string name, int diagnosticCount = 0)
    {
        CadDocumentSnapshot? snapshot = _canvas.CurrentSnapshot;
        if (snapshot is null)
        {
            return name;
        }

        string dirtyState = _canvas.CurrentSession?.IsDirty == true
            ? "modified"
            : "saved";
        return $"{name} | {dirtyState} | " +
            $"{snapshot.Statistics.VisibleEntityCount:N0} visible | " +
            $"{snapshot.Statistics.ExpandedEntityCount:N0} expanded | " +
            $"{snapshot.Statistics.UnsupportedEntityCount:N0} unsupported | " +
            $"{diagnosticCount + snapshot.Diagnostics.Length:N0} diagnostics" +
            DescribeSelection();
    }

    private string DescribeSelection()
    {
        if (_canvas.SelectedHandleCount == 0)
        {
            string emptySelectionUnsupportedStatus = _canvas.LastUnsupportedPrimitiveCount == 0
                ? string.Empty
                : $" | {_canvas.LastUnsupportedPrimitiveCount:N0} unsupported selection candidates";
            if (_is3DView)
            {
                return emptySelectionUnsupportedStatus +
                    $" | {_meshPickTargetHeight:0.#}-pixel pickbox click selects; " +
                    "Alt-click cycles depth; empty-origin Box/Lasso drag " +
                    "right: Window/left: Crossing; Space cycles lasso modes; " +
                    "object-origin drag orbits; " +
                    "Shift-left or middle/right pans";
            }
            return emptySelectionUnsupportedStatus +
                " | left select/drag (right: Window, left: Crossing); middle/right pan";
        }

        string mode = _canvas.LastSelectionMode?.ToString() ?? "Selection";
        string truncated = _canvas.LastSelectionWasTruncated ? " | truncated" : string.Empty;
        string selectedUnsupportedStatus = _canvas.LastUnsupportedPrimitiveCount == 0
            ? string.Empty
            : $" | {_canvas.LastUnsupportedPrimitiveCount:N0} unsupported selection candidates";
        string lockedStatus = _isSelectionEditable
            ? string.Empty
            : " | locked layer: inspection only";
        return $" | {_canvas.SelectedHandleCount:N0} selected ({mode})" +
            selectedUnsupportedStatus + lockedStatus + truncated;
    }

    private void SetStatus(string value) => _status.Text = value;

    private readonly record struct PageSetupKey(
        bool IsFallback,
        CadPageSetupSourceKind SourceKind,
        string Name);

    private sealed class PageSetupChoice
    {
        public PageSetupKey Key { get; }

        public CadPageSetupSnapshot? PageSetup { get; }

        public CadPageSetupPrintOptionsResult? Lowering { get; }

        public bool IsFallback => PageSetup is null;

        public bool CanApplyToModel =>
            PageSetup is
            {
                SourceKind: CadPageSetupSourceKind.NamedOverride,
                TargetSpace: CadPageTargetSpace.Model,
            };

        public string DisplayName { get; }

        public string StatusName { get; }

        private PageSetupChoice(
            PageSetupKey key,
            CadPageSetupSnapshot? pageSetup,
            CadPageSetupPrintOptionsResult? lowering,
            string displayName,
            string statusName)
        {
            Key = key;
            PageSetup = pageSetup;
            Lowering = lowering;
            DisplayName = displayName;
            StatusName = statusName;
        }

        public static PageSetupChoice CreateFallback() => new(
            new PageSetupKey(true, default, string.Empty),
            pageSetup: null,
            lowering: null,
            "A4 model extents (fallback)",
            "A4 model-extents fallback");

        public static PageSetupChoice Create(
            CadPageSetupSnapshot pageSetup,
            CadPageSetupPrintOptionsResult lowering)
        {
            string source = pageSetup.SourceKind ==
                CadPageSetupSourceKind.Layout
                ? "Layout"
                : "Named";
            string applied = pageSetup.SourceKind ==
                    CadPageSetupSourceKind.Layout &&
                !string.IsNullOrWhiteSpace(pageSetup.PageSetupName)
                ? $" ({pageSetup.PageSetupName})"
                : string.Empty;
            string unsupported = lowering.IsSupported
                ? string.Empty
                : $" [unsupported {lowering.Diagnostics.Span[0].Code}]";
            return new PageSetupChoice(
                new PageSetupKey(false, pageSetup.SourceKind, pageSetup.Name),
                pageSetup,
                lowering,
                $"{source}: {pageSetup.Name}{applied}{unsupported}",
                $"{source} {pageSetup.Name}");
        }
    }

    private static ComboBox CreatePropertySelector(TtfFont font, float width)
    {
        var selector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = width,
            HeightConstraint = 30,
            MaxDropDownHeight = 256,
            Margin = new Thickness(0, 0, 8, 0),
        };
        PopulateNamedPropertyChoices(selector, ReadOnlySpan<string>.Empty);
        return selector;
    }

    private static void PopulateNamedPropertyChoices(
        ComboBox selector,
        ReadOnlySpan<string> names)
    {
        selector.Items.Clear();
        selector.Items.Add(new ComboBoxItem { Text = "—" });
        selector.Items.Add(new ComboBoxItem { Text = "*VARIES*" });
        foreach (string name in names)
        {
            selector.Items.Add(new ComboBoxItem
            {
                Text = name,
                Tag = name,
            });
        }
        selector.SelectedIndex = 0;
    }

    private static void PopulateLayerLineTypeChoices(
        ComboBox selector,
        ReadOnlySpan<string> names)
    {
        selector.Items.Clear();
        selector.Items.Add(new ComboBoxItem { Text = "—" });
        foreach (string name in names)
        {
            if (name.Equals(
                    ACadSharp.Tables.LineType.ByLayerName,
                    StringComparison.OrdinalIgnoreCase) ||
                name.Equals(
                    ACadSharp.Tables.LineType.ByBlockName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            selector.Items.Add(new ComboBoxItem
            {
                Text = name,
                Tag = name,
            });
        }
        selector.SelectedIndex = 0;
    }

    private static void PopulateLayerStateChoices(
        ComboBox selector,
        ReadOnlySpan<string> names,
        string? previousName)
    {
        selector.Items.Clear();
        selector.Items.Add(new ComboBoxItem { Text = "—" });
        ComboBoxItem? selected = null;
        ComboBoxItem? defaultLayer = null;
        foreach (string name in names)
        {
            var item = new ComboBoxItem
            {
                Text = name,
                Tag = name,
            };
            selector.Items.Add(item);
            if (name.Equals(previousName, StringComparison.OrdinalIgnoreCase))
            {
                selected = item;
            }
            if (name.Equals(
                ACadSharp.Tables.Layer.DefaultName,
                StringComparison.OrdinalIgnoreCase))
            {
                defaultLayer = item;
            }
        }
        selector.SelectedItem = selected ?? defaultLayer;
        if (selector.SelectedItem is null)
        {
            selector.SelectedIndex = 0;
        }
    }

    private static ComboBox CreateBooleanPropertySelector(
        TtfFont font,
        string trueText,
        string falseText,
        float width)
    {
        var selector = new ComboBox
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = width,
            HeightConstraint = 30,
            Margin = new Thickness(0, 0, 8, 0),
        };
        selector.Items.Add(new ComboBoxItem { Text = "—" });
        selector.Items.Add(new ComboBoxItem { Text = trueText, Tag = true });
        selector.Items.Add(new ComboBoxItem { Text = falseText, Tag = false });
        selector.SelectedIndex = 0;
        return selector;
    }

    private static void PopulateLineWeightChoices(ComboBox selector)
    {
        selector.Items.Add(new ComboBoxItem { Text = "—" });
        selector.Items.Add(new ComboBoxItem { Text = "*VARIES*" });
        selector.Items.Add(new ComboBoxItem { Text = "ByDIPs (unsupported)" });
        AddLineWeightChoice(selector, ACadSharp.LineWeightType.ByLayer);
        AddLineWeightChoice(selector, ACadSharp.LineWeightType.ByBlock);
        AddLineWeightChoice(selector, ACadSharp.LineWeightType.Default);
        foreach (ACadSharp.LineWeightType value in
            Enum.GetValues<ACadSharp.LineWeightType>())
        {
            if ((short)value >= 0)
            {
                AddLineWeightChoice(selector, value);
            }
        }
        selector.SelectedIndex = 0;
    }

    private static void PopulateLayerLineWeightChoices(ComboBox selector)
    {
        selector.Items.Add(new ComboBoxItem { Text = "—" });
        AddLineWeightChoice(selector, ACadSharp.LineWeightType.Default);
        foreach (ACadSharp.LineWeightType value in
            Enum.GetValues<ACadSharp.LineWeightType>())
        {
            if ((short)value >= 0)
            {
                AddLineWeightChoice(selector, value);
            }
        }
        selector.SelectedIndex = 0;
    }

    private static void PopulateVisibilityChoices(ComboBox selector)
    {
        selector.Items.Add(new ComboBoxItem { Text = "—" });
        selector.Items.Add(new ComboBoxItem { Text = "*VARIES*" });
        selector.Items.Add(new ComboBoxItem { Text = "Visible", Tag = true });
        selector.Items.Add(new ComboBoxItem { Text = "Hidden", Tag = false });
        selector.SelectedIndex = 0;
    }

    private static void AddLineWeightChoice(
        ComboBox selector,
        ACadSharp.LineWeightType value) =>
        selector.Items.Add(new ComboBoxItem
        {
            Text = FormatLineWeight(value),
            Tag = value,
        });

    private static string FormatLineWeight(ACadSharp.LineWeightType value) =>
        value switch
        {
            ACadSharp.LineWeightType.ByLayer => "ByLayer",
            ACadSharp.LineWeightType.ByBlock => "ByBlock",
            ACadSharp.LineWeightType.Default => "Default",
            _ when (short)value >= 0 =>
                $"{((short)value / 100.0).ToString("0.00", CultureInfo.InvariantCulture)} mm",
            _ => value.ToString(),
        };

    private static string FormatSelectionColor(ACadSharp.Color color)
    {
        if (color.IsByLayer)
        {
            return "ByLayer";
        }
        if (color.IsByBlock)
        {
            return "ByBlock";
        }
        return color.IsTrueColor
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"ACI {color.Index.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string FormatTransparency(ACadSharp.Transparency transparency)
    {
        if (transparency.IsByLayer)
        {
            return "ByLayer";
        }
        if (transparency.IsByBlock)
        {
            return "ByBlock";
        }
        return transparency.Value.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryParsePositiveInvariantDouble(
        string source,
        out double value) =>
        double.TryParse(
            source,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) &&
        double.IsFinite(value) &&
        value > 0.0;

    private static bool TryParseNonNegativeInvariantDouble(
        string source,
        out double value) =>
        double.TryParse(
            source,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) &&
        double.IsFinite(value) &&
        value >= 0.0;

    private static bool TryParseFiniteInvariantDouble(
        string source,
        out double value) =>
        double.TryParse(
            source,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) &&
        double.IsFinite(value);

    private static bool TryParseInvariantPair(
        string source,
        bool positive,
        out double first,
        out double second)
    {
        first = 0.0;
        second = 0.0;
        int separator = source.IndexOf(',');
        if (separator <= 0 || separator == source.Length - 1 ||
            source.IndexOf(',', separator + 1) >= 0 ||
            !TryParseFiniteInvariantDouble(source[..separator], out first) ||
            !TryParseFiniteInvariantDouble(source[(separator + 1)..], out second))
        {
            return false;
        }
        return positive
            ? first > 0.0 && second > 0.0 &&
                first <= float.MaxValue && second <= float.MaxValue
            : first >= 0.0 && second >= 0.0;
    }

    private static bool TryParseTransparency(
        string source,
        out ACadSharp.Transparency transparency)
    {
        string value = source.Trim();
        if (string.Equals(value, "ByLayer", StringComparison.OrdinalIgnoreCase))
        {
            transparency = ACadSharp.Transparency.ByLayer;
            return true;
        }
        if (string.Equals(value, "ByBlock", StringComparison.OrdinalIgnoreCase))
        {
            transparency = ACadSharp.Transparency.ByBlock;
            return true;
        }
        if (short.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out short explicitValue) &&
            explicitValue is >= 0 and <= 90)
        {
            transparency = new ACadSharp.Transparency(explicitValue);
            return true;
        }

        transparency = default;
        return false;
    }

    private static bool TryParseSelectionColor(
        string source,
        out ACadSharp.Color color)
    {
        string value = source.Trim();
        if (string.Equals(value, "ByLayer", StringComparison.OrdinalIgnoreCase))
        {
            color = ACadSharp.Color.ByLayer;
            return true;
        }
        if (string.Equals(value, "ByBlock", StringComparison.OrdinalIgnoreCase))
        {
            color = ACadSharp.Color.ByBlock;
            return true;
        }
        if (value.Length == 7 &&
            value[0] == '#' &&
            uint.TryParse(
                value.AsSpan(1),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out uint rgb))
        {
            color = new ACadSharp.Color(
                (byte)((rgb >> 16) & byte.MaxValue),
                (byte)((rgb >> 8) & byte.MaxValue),
                (byte)(rgb & byte.MaxValue));
            return true;
        }

        string indexText = value.StartsWith(
            "ACI",
            StringComparison.OrdinalIgnoreCase)
            ? value[3..].TrimStart(' ', ':', '=')
            : value;
        if (short.TryParse(
                indexText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out short index) &&
            index is >= 1 and <= 255)
        {
            color = new ACadSharp.Color(index);
            return true;
        }

        color = default;
        return false;
    }

    private static bool TryParseLayerColor(
        string source,
        out ACadSharp.Color color)
    {
        if (TryParseSelectionColor(source, out color) &&
            !color.IsByLayer &&
            !color.IsByBlock &&
            (color.IsTrueColor || color.Index is >= 1 and <= 255))
        {
            return true;
        }

        color = default;
        return false;
    }

    private static Button CreateButton(
        string label,
        TtfFont font,
        float width,
        float height = 34) =>
        new()
        {
            WidthConstraint = width,
            HeightConstraint = height,
            Content = new TextBlock
            {
                Text = label,
                Font = font,
                FontSize = 11,
                Foreground = new ThemeResourceBrush("TextPrimary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

    private static CheckBox CreateAttributeModeCheckBox(
        string label,
        TtfFont font) =>
        new()
        {
            HeightConstraint = 30,
            Margin = new Thickness(0, 0, 8, 0),
            Content = new TextBlock
            {
                Text = label,
                Font = font,
                FontSize = 11,
                Foreground = new ThemeResourceBrush("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

    private static TextBox CreatePageSetupFieldInput(
        string placeholder,
        TtfFont font,
        float width) =>
        new()
        {
            PlaceholderText = placeholder,
            Font = font,
            WidthConstraint = width,
            HeightConstraint = 30,
            IsSpellCheckEnabled = false,
            Margin = new Thickness(0, 0, 8, 0),
        };

    private static ComboBox CreatePageSetupFieldSelector(
        TtfFont font,
        float width) =>
        new()
        {
            Font = font,
            FontSize = 11,
            WidthConstraint = width,
            HeightConstraint = 30,
            MaxDropDownHeight = 256,
            Margin = new Thickness(0, 0, 8, 0),
        };

    private static void AddPageSetupFieldChoice<T>(
        ComboBox selector,
        string label,
        T value)
        where T : struct, Enum =>
        selector.Items.Add(new ComboBoxItem
        {
            Text = label,
            Tag = value,
        });

    private static void SelectPageSetupFieldChoice<T>(
        ComboBox selector,
        T? value)
        where T : struct, Enum
    {
        selector.SelectedItem = value.HasValue
            ? selector.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is T candidate &&
                    EqualityComparer<T>.Default.Equals(candidate, value.Value))
            : null;
    }

    private static string SuggestedFileName(CadDocumentSession session)
    {
        string stem = string.IsNullOrWhiteSpace(session.SourceName)
            ? "drawing"
            : Path.GetFileNameWithoutExtension(session.SourceName);
        string extension = session.SourceFormat == CadDocumentFormat.Dwg ? ".dwg" : ".dxf";
        return stem + extension;
    }

    private static string SuggestedOutputFileName(
        CadDocumentSession session,
        string extension)
    {
        string stem = string.IsNullOrWhiteSpace(session.SourceName)
            ? "drawing"
            : Path.GetFileNameWithoutExtension(session.SourceName);
        return stem + extension;
    }
}
