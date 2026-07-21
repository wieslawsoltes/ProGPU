using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.Samples;

/// <summary>
/// End-to-end editor for every import format registered with the shared rich-document engine.
/// File selection and I/O stay asynchronous; parsing happens once when a document is opened,
/// while subsequent edits reuse RichEditBox's retained, virtualized layout.
/// </summary>
public sealed class RichTextEditorPage : Grid
{
    private readonly RichDocumentFormatRegistry _formats;
    private readonly RichEditBox _editor;
    private readonly Run _documentRun;
    private readonly Run _statusRun;
    private readonly Button _openButton;
    private readonly Button _saveButton;
    private StorageFile? _currentFile;
    private IRichDocumentFormatCodec? _currentCodec;

    public RichTextEditorPage()
        : this(RichDocumentFormatRegistry.CreateDefault())
    {
    }

    public RichTextEditorPage(RichDocumentFormatRegistry formats)
    {
        _formats = formats ?? throw new ArgumentNullException(nameof(formats));
        SupportedExtensions = _formats.Formats
            .Where(static codec => codec.CanImport)
            .SelectMany(static codec => codec.FileExtensions)
            .Select(static extension => extension.StartsWith('.') ? extension : $".{extension}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static extension => extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        SupportedExportExtensions = _formats.Formats
            .Where(static codec => codec.CanExport)
            .SelectMany(static codec => codec.FileExtensions)
            .Select(static extension => extension.StartsWith('.') ? extension : $".{extension}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static extension => extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        TtfFont font = AppState.GetFont() ??
            throw new InvalidOperationException("The sample font must be initialized before creating the rich-text editor page.");

        Padding = new Thickness(18f);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        RowDefinitions.Add(GridLength.Auto);
        RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        var headerCard = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(16f),
            Margin = new Thickness(0f, 0f, 0f, 12f),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var header = new StackPanel { Orientation = Orientation.Vertical };
        var title = new RichTextBlock { Font = font, FontSize = 22f };
        title.Inlines.Add(new Bold(new Run("Rich Document Editor")));
        header.AddChild(title);

        var description = new RichTextBlock
        {
            Font = font,
            FontSize = 12f,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0f, 4f, 0f, 10f)
        };
        description.Inlines.Add(new Run(
            "Open and save plain text, Markdown, RTF, HTML, or Microsoft Word DOCX through the shared format registry, then edit with the same virtualized layout and rendering engine."));
        header.AddChild(description);

        var supported = new RichTextBlock
        {
            Font = font,
            FontSize = 11f,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0f, 0f, 0f, 10f)
        };
        supported.Inlines.Add(new Bold(new Run("Supported: ")));
        supported.Inlines.Add(new Run(string.Join(", ", SupportedExtensions)));
        header.AddChild(supported);

        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalSpacing = 6f,
            VerticalSpacing = 6f,
            Margin = new Thickness(0f, 0f, 0f, 10f)
        };

        _openButton = CreateButton("Open document…", 132f);
        _saveButton = CreateButton("Save as…", 88f);
        Button newButton = CreateButton("New", 64f);
        Button undoButton = CreateButton("Undo", 64f);
        Button redoButton = CreateButton("Redo", 64f);
        Button boldButton = CreateButton("Bold", 64f);
        Button italicButton = CreateButton("Italic", 64f);
        Button underlineButton = CreateButton("Underline", 82f);

        actions.AddChild(_openButton);
        actions.AddChild(_saveButton);
        actions.AddChild(newButton);
        actions.AddChild(undoButton);
        actions.AddChild(redoButton);
        actions.AddChild(boldButton);
        actions.AddChild(italicButton);
        actions.AddChild(underlineButton);
        header.AddChild(actions);

        var documentStatus = new RichTextBlock { Font = font, FontSize = 11f };
        documentStatus.Inlines.Add(new Bold(new Run("Document: ")));
        _documentRun = new Run("Untitled — editable plain text");
        documentStatus.Inlines.Add(_documentRun);
        documentStatus.Inlines.Add(new Run("\n"));
        documentStatus.Inlines.Add(new Bold(new Run("Status: ")));
        _statusRun = new Run("Ready. Choose any registered document format.")
        {
            Foreground = new ThemeResourceBrush("TextSecondary")
        };
        documentStatus.Inlines.Add(_statusRun);
        header.AddChild(documentStatus);

        headerCard.Child = header;
        AddChild(headerCard);
        SetRow(headerCard, 0);

        _editor = new RichEditBox
        {
            Font = font,
            FontSize = 15f,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Header = "Editable document",
            Description = "Imported content remains fully editable after conversion to the shared rich-document model."
        };
        _editor.Text = "Open a supported document, or start typing here to create a new one.";

        var editorCard = new Border
        {
            Background = new ThemeResourceBrush("ControlBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(8f),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = _editor
        };
        AddChild(editorCard);
        SetRow(editorCard, 1);

        _openButton.Click += async (_, _) => await OpenWithPickerAsync();
        _saveButton.Click += async (_, _) => await SaveWithPickerAsync();
        newButton.Click += (_, _) => NewDocument();
        undoButton.Click += (_, _) => _editor.Undo();
        redoButton.Click += (_, _) => _editor.Redo();
        boldButton.Click += (_, _) => _editor.ToggleStyle("bold");
        italicButton.Click += (_, _) => _editor.ToggleStyle("italic");
        underlineButton.Click += (_, _) => _editor.ToggleStyle("underline");
    }

    public static FrameworkElement Create() => new RichTextEditorPage();

    public RichEditBox Editor => _editor;

    public IReadOnlyList<string> SupportedExtensions { get; }

    public IReadOnlyList<string> SupportedExportExtensions { get; }

    public StorageFile? CurrentFile => _currentFile;

    public string? CurrentFormatId => _currentCodec?.FormatId;

    public string Status => _statusRun.Text;

    public async Task OpenDocumentAsync(StorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!_formats.TryGetFileExtension(file.FileType, out IRichDocumentFormatCodec? codec) || codec is null || !codec.CanImport)
        {
            throw new NotSupportedException(
                $"No import codec is registered for '{file.FileType}'. Supported extensions: {string.Join(", ", SupportedExtensions)}.");
        }

        byte[] source = await file.ReadBytesAsync();
        TtfFont font = _editor.Font ?? AppState.GetFont() ??
            throw new InvalidOperationException("A default font is required to import a rich document.");
        TtfFont codeFont = AppState.GetFontCourier() ?? font;
        var context = new RichDocumentImportContext(
            font,
            codeFont,
            _editor.FontSize,
            new ThemeResourceBrush("TextPrimary"),
            _editor.ActualTheme);

        RichDocument imported = await Task.Run(() => codec.Import(source, context));
        _editor.SetRichDocument(imported);
        _currentFile = file;
        _currentCodec = codec;
        _documentRun.Text = $"{file.Name} — {codec.FormatId}";
        _statusRun.Text = $"Loaded {source.Length:N0} bytes. The converted document is ready for editing.";
    }

    public async Task SaveDocumentAsync(StorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!_formats.TryGetFileExtension(file.FileType, out IRichDocumentFormatCodec? codec) || codec is null || !codec.CanExport)
        {
            throw new NotSupportedException(
                $"No export codec is registered for '{file.FileType}'. Supported extensions: {string.Join(", ", SupportedExportExtensions)}.");
        }

        RichDocument snapshot = _editor.CreateRichDocumentSnapshot();
        byte[] output = await Task.Run(() => codec.Export(snapshot));
        await file.WriteBytesAsync(output);
        _currentFile = file;
        _currentCodec = codec;
        _documentRun.Text = $"{file.Name} — {codec.FormatId}";
        _statusRun.Text = $"Saved {output.Length:N0} bytes.";
    }

    private async Task OpenWithPickerAsync()
    {
        _openButton.IsEnabled = false;
        _statusRun.Text = "Opening the system document picker…";
        try
        {
            var picker = new FileOpenPicker();
            foreach (string extension in SupportedExtensions)
            {
                picker.FileTypeFilter.Add(extension);
            }

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                _statusRun.Text = "Open cancelled; the current document was not changed.";
                return;
            }

            await OpenDocumentAsync(file);
        }
        catch (Exception exception)
        {
            _statusRun.Text = $"Open failed: {exception.Message}";
        }
        finally
        {
            _openButton.IsEnabled = true;
        }
    }

    private async Task SaveWithPickerAsync()
    {
        _saveButton.IsEnabled = false;
        _statusRun.Text = "Opening the system save picker…";
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = _currentFile?.Name ?? "Untitled.docx"
            };
            picker.FileTypeChoices["Rich documents"] = SupportedExportExtensions.ToList();
            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                _statusRun.Text = "Save cancelled; the current document was not changed.";
                return;
            }
            await SaveDocumentAsync(file);
        }
        catch (Exception exception)
        {
            _statusRun.Text = $"Save failed: {exception.Message}";
        }
        finally
        {
            _saveButton.IsEnabled = true;
        }
    }

    private void NewDocument()
    {
        _editor.TextDocument.SetText(TextSetOptions.None, string.Empty);
        _currentFile = null;
        _currentCodec = null;
        _documentRun.Text = "Untitled — editable plain text";
        _statusRun.Text = "Created a new empty document.";
    }

    private static Button CreateButton(string text, float width) => new()
    {
        Width = width,
        Height = 30f,
        CornerRadius = 4f,
        Content = new TextVisual
        {
            Text = text,
            FontSize = 11f,
            Brush = new ThemeResourceBrush("ButtonForeground"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };
}
