using System.Globalization;
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
    private readonly CadDocumentStore _store = new();
    private readonly CadSampleCanvas _canvas;
    private readonly Grid _contentHost;
    private readonly Viewport3D _viewport3D;
    private readonly CadPrintPreviewCanvas _printPreview;
    private readonly Button _viewModeButton;
    private readonly TextBlock _viewModeText;
    private readonly Button _printPreviewButton;
    private readonly TextBlock _printPreviewText;
    private readonly ComboBox _pageSetupSelector;
    private readonly Button _applyPageSetupButton;
    private readonly TextBox _pageSetupNameInput;
    private readonly Button _createPageSetupButton;
    private readonly Button _updatePageSetupButton;
    private readonly Button _renamePageSetupButton;
    private readonly Button _deletePageSetupButton;
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
    private readonly Button[] _drawOrderButtons;
    private readonly Button[] _moveButtons;
    private readonly Button[] _copyButtons;
    private readonly Button[] _rotateButtons;
    private readonly Button[] _scaleButtons;
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
    private bool _isRefreshingSelectionProperties;
    private bool _isSelectionEditable;
    private bool _isSolidThicknessSelection;
    private bool _selectedLayerCanRename;
    private bool _selectedLayerCanRemove;
    private CadDocumentSession? _selectionPropertyCatalogSession;
    private ulong _selectionPropertyCatalogGeneration = ulong.MaxValue;
    private CadDocumentSession? _layerMergeSourceSession;
    private ulong _layerMergeSourceGeneration = ulong.MaxValue;

    public CadShxFontCatalog ShxFonts => _canvas.ShxFonts;

    public CadSampleCanvas Canvas => _canvas;

    public CadPrintPreviewCanvas PrintPreview => _printPreview;

    public ComboBox PageSetupSelector => _pageSetupSelector;

    public TextBox PageSetupNameInput => _pageSetupNameInput;

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
            Visibility = Visibility.Collapsed,
            RenderMode = RenderMode3D.Solid,
            ShadingMode = ShadingMode3D.Flat,
            LightDirection = new System.Numerics.Vector3(0.25f, -0.5f, -1.0f),
            AmbientIntensity = 0.25f,
        };
        _printPreview = new CadPrintPreviewCanvas
        {
            Visibility = Visibility.Collapsed,
        };
        TtfFont font = InterFontFamily.Regular;
        RowDefinitions.Add(new GridLength(464, GridUnitType.Absolute));
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
        toolbarRows.AddChild(actions);
        toolbarRows.AddChild(editActions);
        toolbarRows.AddChild(transformActions);
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
        _printPreviewButton = CreateButton("Print preview", font, 112);
        _printPreviewText = (TextBlock)_printPreviewButton.Content!;
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
        actions.AddChild(_clearSelectionButton);

        _undoButton = CreateButton("Undo", font, 68, 30);
        _redoButton = CreateButton("Redo", font, 68, 30);
        _deleteButton = CreateButton("Delete", font, 76, 30);
        Button sendToBack = CreateButton("To back", font, 76, 30);
        Button bringToFront = CreateButton("To front", font, 76, 30);
        Button bringAbove = CreateButton("Above…", font, 82, 30);
        Button sendUnder = CreateButton("Under…", font, 82, 30);
        _undoButton.Margin = new Thickness(0, 0, 8, 0);
        _redoButton.Margin = new Thickness(0, 0, 8, 0);
        _deleteButton.Margin = new Thickness(0, 0, 8, 0);
        sendToBack.Margin = new Thickness(0, 0, 4, 0);
        bringToFront.Margin = new Thickness(0, 0, 4, 0);
        bringAbove.Margin = new Thickness(0, 0, 4, 0);
        sendUnder.Margin = new Thickness(0, 0, 12, 0);
        _drawOrderButtons = [sendToBack, bringToFront, bringAbove, sendUnder];
        editActions.AddChild(_undoButton);
        editActions.AddChild(_redoButton);
        editActions.AddChild(_deleteButton);
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
        moveNegativeX.Margin = new Thickness(0, 0, 4, 0);
        movePositiveX.Margin = new Thickness(0, 0, 8, 0);
        moveNegativeY.Margin = new Thickness(0, 0, 4, 0);
        _moveButtons = [
            moveNegativeX,
            movePositiveX,
            moveNegativeY,
            movePositiveY,
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
            Text = "Copy by move step",
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(12, 6, 8, 0),
        });
        Button copyNegativeX = CreateButton("Copy −X", font, 72, 30);
        Button copyPositiveX = CreateButton("Copy +X", font, 72, 30);
        Button copyNegativeY = CreateButton("Copy −Y", font, 72, 30);
        Button copyPositiveY = CreateButton("Copy +Y", font, 72, 30);
        copyNegativeX.Margin = new Thickness(0, 0, 4, 0);
        copyPositiveX.Margin = new Thickness(0, 0, 8, 0);
        copyNegativeY.Margin = new Thickness(0, 0, 4, 0);
        _copyButtons = [
            copyNegativeX,
            copyPositiveX,
            copyNegativeY,
            copyPositiveY,
        ];
        foreach (Button copyButton in _copyButtons)
        {
            transformActions.AddChild(copyButton);
        }

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
        _setSelectionAttributeModesButton = CreateButton(
            "Set modes",
            font,
            92,
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
        printActions.AddChild(_pageSetupSelector);
        printActions.AddChild(_applyPageSetupButton);
        printActions.AddChild(_printPreviewButton);

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
        _fitButton.Click += (_, _) => _canvas.FitToView();
        _viewModeButton.Click += (_, _) => ToggleViewMode();
        _printPreviewButton.Click += (_, _) => TogglePrintPreview();
        _pageSetupSelector.SelectionChanged += (_, _) =>
            OnPageSetupSelectionChanged();
        _applyPageSetupButton.Click += (_, _) =>
            ApplySelectedPageSetupToModel();
        _pageSetupNameInput.TextChanged += (_, _) => UpdateEditControls();
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
        _clearSelectionButton.Click += (_, _) => _canvas.ClearSelection();
        _undoButton.Click += (_, _) => PerformUndo();
        _redoButton.Click += (_, _) => PerformRedo();
        _deleteButton.Click += (_, _) => PerformDelete();
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
        copyNegativeX.Click += (_, _) => CopySelection(-1, 0);
        copyPositiveX.Click += (_, _) => CopySelection(1, 0);
        copyNegativeY.Click += (_, _) => CopySelection(0, -1);
        copyPositiveY.Click += (_, _) => CopySelection(0, 1);
        rotateCounterclockwise.Click += (_, _) => RotateSelection(1);
        rotateClockwise.Click += (_, _) => RotateSelection(-1);
        scaleUp.Click += (_, _) => ScaleSelection(useReciprocal: false);
        scaleDown.Click += (_, _) => ScaleSelection(useReciprocal: true);
        _canvas.SelectionChanged += (_, _) =>
        {
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
        _canvas.SnapshotChanged += (_, _) =>
        {
            EnsureLayerMergeSourcesAreCurrent();
            RebuildMesh3DView();
            if (_isPrintPreview)
            {
                ShowPlanView(clearPreview: true);
            }
            RefreshPageSetups(preserveSelection: true);
            RefreshSelectionPropertyControls();
        };
        RebuildMesh3DView();
        RefreshPageSetups(preserveSelection: false);
        RefreshSelectionPropertyControls();
        UpdateEditControls();
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
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

    private void ToggleViewMode()
    {
        if (!_viewModeButton.IsEnabled || _isPrintPreview)
        {
            return;
        }
        _is3DView = !_is3DView;
        _canvas.Visibility = _is3DView ? Visibility.Collapsed : Visibility.Visible;
        _viewport3D.Visibility = _is3DView ? Visibility.Visible : Visibility.Collapsed;
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
        if (_isPrintPreview)
        {
            ShowSelectedPrintPreview();
        }
        else
        {
            UpdateEditControls();
        }
    }

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
            foreach (CadPageSetupSnapshot pageSetup in catalog.Setups.Span)
            {
                CadPageSetupPrintOptionsResult lowering =
                    loweringCompiler.Compile(pageSetup);
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
        _printPreview.Visibility = Visibility.Collapsed;
        _viewModeText.Text = "3D surfaces";
        _printPreviewText.Text = "Print preview";
        if (clearPreview)
        {
            _printPreview.Clear();
        }
    }

    private void RebuildMesh3DView()
    {
        _viewport3D.Children.Clear();
        CadDocumentSnapshot? snapshot = _canvas.CurrentSnapshot;
        if (snapshot is null)
        {
            SetMeshViewAvailability(false);
            return;
        }

        CadRecordedMesh3DScene scene = new CadMesh3DSceneCompiler().Compile(snapshot);
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

        bool hasMeshes = scene.DrawBatches.Length != 0;
        SetMeshViewAvailability(hasMeshes);
        if (!hasMeshes)
        {
            return;
        }
        CadBounds3D bounds = scene.Bounds;
        CadPoint3D center = bounds.Center;
        var target = new System.Numerics.Vector3(
            checked((float)(center.X - scene.RebaseOrigin.X)),
            checked((float)(center.Y - scene.RebaseOrigin.Y)),
            checked((float)(center.Z - scene.RebaseOrigin.Z)));
        float extent = checked((float)Math.Max(
            Math.Max(bounds.Max.X - bounds.Min.X, bounds.Max.Y - bounds.Min.Y),
            bounds.Max.Z - bounds.Min.Z));
        float radius = Math.Max(extent * 1.8f, 10.0f);
        var offset = new System.Numerics.Vector3(radius, -radius, radius * 0.8f);
        _viewport3D.Camera = new PerspectiveCamera
        {
            Position = target + offset,
            LookDirection = -offset,
            UpDirection = System.Numerics.Vector3.UnitZ,
            NearPlaneDistance = Math.Max(radius / 10_000.0f, 0.01f),
            FarPlaneDistance = radius * 20.0f,
            FieldOfView = 42.0f,
        };
        _viewport3D.Invalidate();
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
        _viewModeText.Text = "3D surfaces";
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
                prior.Owner == candidate.Owner &&
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
    }

    private bool HaveSelectedAttributeModesChanged(
        CadAttributeValueEntry entry) =>
        _selectionAttributeInvisibleCheckBox.IsChecked != entry.IsInvisible ||
        _selectionAttributeVerifyCheckBox.IsChecked != entry.IsVerifiable ||
        _selectionAttributePresetCheckBox.IsChecked != entry.IsPreset ||
        _selectionAttributePositionLockedCheckBox.IsChecked !=
            entry.IsPositionLocked;

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
            if (!_canvas.TranslateSelection(new CadPoint3D(
                    xDirection * step,
                    yDirection * step,
                    0)))
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

        int selectedCount = _canvas.SelectedHandleCount;
        try
        {
            if (!_canvas.DuplicateSelection(new CadPoint3D(
                    xDirection * step,
                    yDirection * step,
                    0)))
            {
                SetStatus("Copy requires at least one selected entity.");
                return;
            }
            SetStatus(
                $"Copied {selectedCount:N0} selected entity(s) by " +
                $"({xDirection * step:G}, {yDirection * step:G}, 0) WCS.");
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
            if (!_canvas.RotateSelection(radians))
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
            if (!_canvas.ScaleSelection(appliedFactor))
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
        UpdateEditControls();
    }

    private void UpdateEditControls()
    {
        EnsureLayerMergeSourcesAreCurrent();
        bool isReferencePicking =
            _canvas.PendingDrawOrderPlacement is not null;
        bool canUsePlanTools =
            !_isBusy && !_isPrintPreview && !isReferencePicking;
        _openButton.IsEnabled = canUsePlanTools;
        bool canImportLineTypes = canUsePlanTools &&
            _canvas.CurrentSession is not null;
        _loadLineTypesButton.IsEnabled = canImportLineTypes;
        _reloadLineTypesButton.IsEnabled = canImportLineTypes;
        bool canImportPageSetups = canUsePlanTools &&
            _canvas.CurrentSession is not null;
        _importPageSetupsButton.IsEnabled = canImportPageSetups;
        _importReplacePageSetupsButton.IsEnabled = canImportPageSetups;
        _saveButton.IsEnabled = !_isBusy;
        _fitButton.IsEnabled = canUsePlanTools && !_is3DView;
        _clearSelectionButton.IsEnabled = canUsePlanTools;
        _printPreviewButton.IsEnabled =
            !_isBusy && !isReferencePicking &&
            (_isPrintPreview || _canvas.CurrentSnapshot is not null);
        _pageSetupSelector.IsEnabled =
            !_isBusy && !isReferencePicking &&
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
        _undoButton.IsEnabled =
            canUsePlanTools && _canvas.UndoCount > 0;
        _redoButton.IsEnabled =
            canUsePlanTools && _canvas.RedoCount > 0;
        _viewModeButton.IsEnabled =
            canUsePlanTools && _viewport3D.Children.Count > 0;
        bool canTransform = canUsePlanTools &&
            _canvas.SelectedHandleCount > 0 &&
            _isSelectionEditable;
        _deleteButton.IsEnabled = canTransform;
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
        _rotationStepInput.IsEnabled = canUsePlanTools;
        _scaleFactorInput.IsEnabled = canUsePlanTools;
        foreach (Button drawOrderButton in _drawOrderButtons)
        {
            drawOrderButton.IsEnabled = canTransform;
        }
        foreach (Button moveButton in _moveButtons)
        {
            moveButton.IsEnabled = canTransform;
        }
        foreach (Button copyButton in _copyButtons)
        {
            copyButton.IsEnabled = canTransform;
        }
        foreach (Button rotateButton in _rotateButtons)
        {
            rotateButton.IsEnabled = canTransform;
        }
        foreach (Button scaleButton in _scaleButtons)
        {
            scaleButton.IsEnabled = canTransform;
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

    private static bool TryParseFiniteInvariantDouble(
        string source,
        out double value) =>
        double.TryParse(
            source,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) &&
        double.IsFinite(value);

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

    private static string SuggestedFileName(CadDocumentSession session)
    {
        string stem = string.IsNullOrWhiteSpace(session.SourceName)
            ? "drawing"
            : Path.GetFileNameWithoutExtension(session.SourceName);
        string extension = session.SourceFormat == CadDocumentFormat.Dwg ? ".dwg" : ".dxf";
        return stem + extension;
    }
}
