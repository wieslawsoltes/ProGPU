using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Fonts.Inter;
using ProGPU.Text;
using ProGPU.Vector;
using Windows.Storage;

namespace ProGPU.CAD.Sample;

/// <summary>Shared desktop/browser CAD shell with real stream open and save workflows.</summary>
public sealed class CadSampleView : Grid
{
    private readonly CadDocumentStore _store = new();
    private readonly CadSampleCanvas _canvas;
    private readonly TextBlock _status;
    private readonly Button _openButton;
    private readonly Button _saveButton;
    private bool _isBusy;

    public CadShxFontCatalog ShxFonts => _canvas.ShxFonts;

    public CadSampleView()
        : this(null)
    {
    }

    public CadSampleView(CadShxFontCatalog? shxFonts)
    {
        _canvas = new CadSampleCanvas(shxFonts);
        TtfFont font = InterFontFamily.Regular;
        RowDefinitions.Add(new GridLength(52, GridUnitType.Absolute));
        RowDefinitions.Add(GridLength.Star(1));
        RowDefinitions.Add(new GridLength(30, GridUnitType.Absolute));

        var toolbar = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 8),
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        toolbar.Child = actions;

        _openButton = CreateButton("Open DXF/DWG", font, 132);
        _saveButton = CreateButton("Save As", font, 92);
        Button fitButton = CreateButton("Fit", font, 68);
        _openButton.Margin = new Thickness(0, 0, 8, 0);
        _saveButton.Margin = new Thickness(0, 0, 8, 0);
        actions.AddChild(_openButton);
        actions.AddChild(_saveButton);
        actions.AddChild(fitButton);

        _status = new TextBlock
        {
            Font = font,
            FontSize = 11,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Padding = new Thickness(10, 6),
            Text = DescribeCurrentDocument("Representative analytic scene"),
        };
        var statusBorder = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _status,
        };

        AddChild(toolbar);
        AddChild(_canvas);
        AddChild(statusBorder);
        SetRow(toolbar, 0);
        SetRow(_canvas, 1);
        SetRow(statusBorder, 2);

        _openButton.Click += async (_, _) => await OpenAsync();
        _saveButton.Click += async (_, _) => await SaveAsAsync();
        fitButton.Click += (_, _) => _canvas.FitToView();
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
            _canvas.Load(result.Session);
            SetStatus(DescribeCurrentDocument(file.Name, result.Diagnostics.Count));
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
        SetStatus(status);
        return true;
    }

    private void EndOperation()
    {
        _isBusy = false;
        _openButton.IsEnabled = true;
        _saveButton.IsEnabled = true;
    }

    private string DescribeCurrentDocument(string name, int diagnosticCount = 0)
    {
        CadDocumentSnapshot? snapshot = _canvas.CurrentSnapshot;
        if (snapshot is null)
        {
            return name;
        }

        return $"{name} | {snapshot.Statistics.VisibleEntityCount:N0} visible | " +
            $"{snapshot.Statistics.ExpandedEntityCount:N0} expanded | " +
            $"{snapshot.Statistics.UnsupportedEntityCount:N0} unsupported | " +
            $"{diagnosticCount + snapshot.Diagnostics.Length:N0} diagnostics";
    }

    private void SetStatus(string value) => _status.Text = value;

    private static Button CreateButton(string label, TtfFont font, float width) =>
        new()
        {
            WidthConstraint = width,
            HeightConstraint = 34,
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
