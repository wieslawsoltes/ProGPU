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
using Key = Silk.NET.Input.Key;

namespace ProGPU.CAD.Sample;

/// <summary>Shared desktop/browser CAD shell with real stream open and save workflows.</summary>
public sealed class CadSampleView : Grid
{
    private readonly CadDocumentStore _store = new();
    private readonly CadSampleCanvas _canvas;
    private readonly Viewport3D _viewport3D;
    private readonly Button _viewModeButton;
    private readonly TextBlock _viewModeText;
    private readonly TextBlock _status;
    private readonly Button _openButton;
    private readonly Button _saveButton;
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private readonly Button _deleteButton;
    private readonly Button[] _drawOrderButtons;
    private readonly Button[] _moveButtons;
    private readonly Button[] _rotateButtons;
    private readonly Button[] _scaleButtons;
    private readonly TextBox _moveStepInput;
    private readonly TextBox _rotationStepInput;
    private readonly TextBox _scaleFactorInput;
    private readonly List<string> _shxSupportDirectories = new();
    private bool _isBusy;
    private string _currentDocumentName = "Representative analytic scene";
    private int _currentDiagnosticCount;
    private bool _is3DView;

    public CadShxFontCatalog ShxFonts => _canvas.ShxFonts;

    public CadSampleCanvas Canvas => _canvas;

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
        TtfFont font = InterFontFamily.Regular;
        RowDefinitions.Add(new GridLength(124, GridUnitType.Absolute));
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
        toolbarRows.AddChild(actions);
        toolbarRows.AddChild(editActions);
        toolbarRows.AddChild(transformActions);
        toolbar.Child = toolbarRows;

        _openButton = CreateButton("Open DXF/DWG", font, 132);
        _saveButton = CreateButton("Save As", font, 92);
        Button fitButton = CreateButton("Fit", font, 68);
        _viewModeButton = CreateButton("3D surfaces", font, 104);
        _viewModeText = (TextBlock)_viewModeButton.Content!;
        Button clearSelectionButton = CreateButton("Clear selection", font, 112);
        _openButton.Margin = new Thickness(0, 0, 8, 0);
        _saveButton.Margin = new Thickness(0, 0, 8, 0);
        fitButton.Margin = new Thickness(0, 0, 8, 0);
        _viewModeButton.Margin = new Thickness(0, 0, 8, 0);
        actions.AddChild(_openButton);
        actions.AddChild(_saveButton);
        actions.AddChild(fitButton);
        actions.AddChild(_viewModeButton);
        actions.AddChild(clearSelectionButton);

        _undoButton = CreateButton("Undo", font, 68, 30);
        _redoButton = CreateButton("Redo", font, 68, 30);
        _deleteButton = CreateButton("Delete", font, 76, 30);
        Button sendToBack = CreateButton("To back", font, 76, 30);
        Button bringToFront = CreateButton("To front", font, 76, 30);
        _undoButton.Margin = new Thickness(0, 0, 8, 0);
        _redoButton.Margin = new Thickness(0, 0, 8, 0);
        _deleteButton.Margin = new Thickness(0, 0, 8, 0);
        sendToBack.Margin = new Thickness(0, 0, 4, 0);
        bringToFront.Margin = new Thickness(0, 0, 12, 0);
        _drawOrderButtons = [sendToBack, bringToFront];
        editActions.AddChild(_undoButton);
        editActions.AddChild(_redoButton);
        editActions.AddChild(_deleteButton);
        editActions.AddChild(sendToBack);
        editActions.AddChild(bringToFront);
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

        var contentHost = new Grid();
        contentHost.AddChild(_canvas);
        contentHost.AddChild(_viewport3D);
        AddChild(toolbar);
        AddChild(contentHost);
        AddChild(statusBorder);
        SetRow(toolbar, 0);
        SetRow(contentHost, 1);
        SetRow(statusBorder, 2);

        _openButton.Click += async (_, _) => await OpenAsync();
        _saveButton.Click += async (_, _) => await SaveAsAsync();
        fitButton.Click += (_, _) => _canvas.FitToView();
        _viewModeButton.Click += (_, _) => ToggleViewMode();
        clearSelectionButton.Click += (_, _) => _canvas.ClearSelection();
        _undoButton.Click += (_, _) => PerformUndo();
        _redoButton.Click += (_, _) => PerformRedo();
        _deleteButton.Click += (_, _) => PerformDelete();
        sendToBack.Click += (_, _) =>
            SetSelectionDrawOrder(CadDrawOrderPlacement.SendToBack);
        bringToFront.Click += (_, _) =>
            SetSelectionDrawOrder(CadDrawOrderPlacement.BringToFront);
        moveNegativeX.Click += (_, _) => MoveSelection(-1, 0);
        movePositiveX.Click += (_, _) => MoveSelection(1, 0);
        moveNegativeY.Click += (_, _) => MoveSelection(0, -1);
        movePositiveY.Click += (_, _) => MoveSelection(0, 1);
        rotateCounterclockwise.Click += (_, _) => RotateSelection(1);
        rotateClockwise.Click += (_, _) => RotateSelection(-1);
        scaleUp.Click += (_, _) => ScaleSelection(useReciprocal: false);
        scaleDown.Click += (_, _) => ScaleSelection(useReciprocal: true);
        _canvas.SelectionChanged += (_, _) =>
        {
            if (!_isBusy)
            {
                SetStatus(DescribeCurrentDocument(
                    _currentDocumentName,
                    _currentDiagnosticCount));
            }
            UpdateEditControls();
        };
        _canvas.EditStateChanged += (_, _) => UpdateEditControls();
        _canvas.SnapshotChanged += (_, _) => RebuildMesh3DView();
        RebuildMesh3DView();
        UpdateEditControls();
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (!e.Handled &&
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
        if (!_viewModeButton.IsEnabled)
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
        _viewModeButton.IsEnabled = !_isBusy && isAvailable;
        if (isAvailable || !_is3DView)
        {
            return;
        }
        _is3DView = false;
        _canvas.Visibility = Visibility.Visible;
        _viewport3D.Visibility = Visibility.Collapsed;
        _viewModeText.Text = "3D surfaces";
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
        _saveButton.IsEnabled = false;
        UpdateEditControls();
        SetStatus(status);
        return true;
    }

    private void EndOperation()
    {
        _isBusy = false;
        _openButton.IsEnabled = true;
        _saveButton.IsEnabled = true;
        UpdateEditControls();
    }

    private void UpdateEditControls()
    {
        _undoButton.IsEnabled = !_isBusy && _canvas.UndoCount > 0;
        _redoButton.IsEnabled = !_isBusy && _canvas.RedoCount > 0;
        _viewModeButton.IsEnabled = !_isBusy && _viewport3D.Children.Count > 0;
        bool canTransform = !_isBusy && _canvas.SelectedHandleCount > 0;
        _deleteButton.IsEnabled = canTransform;
        _moveStepInput.IsEnabled = !_isBusy;
        _rotationStepInput.IsEnabled = !_isBusy;
        _scaleFactorInput.IsEnabled = !_isBusy;
        foreach (Button drawOrderButton in _drawOrderButtons)
        {
            drawOrderButton.IsEnabled = canTransform;
        }
        foreach (Button moveButton in _moveButtons)
        {
            moveButton.IsEnabled = canTransform;
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
        return $" | {_canvas.SelectedHandleCount:N0} selected ({mode})" +
            selectedUnsupportedStatus + truncated;
    }

    private void SetStatus(string value) => _status.Text = value;

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

    private static string SuggestedFileName(CadDocumentSession session)
    {
        string stem = string.IsNullOrWhiteSpace(session.SourceName)
            ? "drawing"
            : Path.GetFileNameWithoutExtension(session.SourceName);
        string extension = session.SourceFormat == CadDocumentFormat.Dwg ? ".dwg" : ".dxf";
        return stem + extension;
    }
}
