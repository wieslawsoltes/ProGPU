using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Compute;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Button = Microsoft.UI.Xaml.Controls.Button;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples
{
    public static class MarkdownPage
    {
        private static MarkdownTextBlock? _previewControl;
        private static RichEditBox? _editorControl;
        private static RichTextBlock? _statusText;
        private static ScrollViewer? _previewScroll;
        private static float _benchmarkScrollDirection = 1f;

        internal static void AdvanceBenchmarkScroll()
        {
            if (_previewScroll == null)
            {
                return;
            }

            float maxOffset = Math.Max(0f, _previewScroll.ContentHeight - _previewScroll.Size.Y);
            if (maxOffset <= 0f)
            {
                return;
            }

            float nextOffset = _previewScroll.VerticalOffset + _benchmarkScrollDirection * 30f;
            if (nextOffset >= maxOffset)
            {
                nextOffset = maxOffset;
                _benchmarkScrollDirection = -1f;
            }
            else if (nextOffset <= 0f)
            {
                nextOffset = 0f;
                _benchmarkScrollDirection = 1f;
            }

            _previewScroll.VerticalOffset = nextOffset;
        }

        internal static bool TryGetBenchmarkRenderState(
            out int positionedCharacters,
            out float scrollOffset,
            out float contentHeight)
        {
            positionedCharacters = _previewControl?.PositionedChars.Count ?? 0;
            scrollOffset = _previewScroll?.VerticalOffset ?? 0f;
            contentHeight = _previewScroll?.ContentHeight ?? 0f;
            return positionedCharacters > 0 && contentHeight > 0f;
        }

        public static FrameworkElement Create()
        {
            _benchmarkScrollDirection = 1f;
            // Code-block editors request the shared TextMate resources only when the preview
            // actually contains code. They render an immediate plain-text fallback and recolor
            // in place when the active theme grammar becomes ready.
            MarkdownParser.CodeBlockFactory = (code, language) =>
            {
                var editor = new ProGPU.WinUI.Designer.VirtualizedCodeEditor(
                    useLightweightSyntaxHighlighting: true)
                {
                    Font = AppState.GetFontCourier() ?? AppState.GetFont(),
                    Margin = new Thickness(0, 4, 0, 12),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Top
                };
                editor.SetCode(code);

                var lineCount = code.AsSpan().Count('\n') + 1;
                editor.HeightConstraint = Math.Clamp(lineCount * 22f + 10f, 60f, 260f);
                return editor;
            };

            var rootGrid = new Microsoft.UI.Xaml.Controls.Grid();
            rootGrid.RowDefinitions.Add(new GridLength(48f, GridUnitType.Absolute));  // Row 0: File Operations & Presets
            rootGrid.RowDefinitions.Add(new GridLength(48f, GridUnitType.Absolute));  // Row 1: Configurations Toolbar
            rootGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Row 2: Dual-Pane Editor & Previewer

            var activeFont = AppState.GetFont();

            // ================= ROW 0: FILE OPERATIONS & PRESETS =================
            var fileBar = new Border
            {
                Background = new ThemeResourceBrush("HeaderBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(0, 0, 0, 1f),
                Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var fileStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            fileBar.Child = fileStack;

            // Load File Button
            var loadBtn = new Button { WidthConstraint = 130f, HeightConstraint = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 8, 0) };
            var loadBtnText = new RichTextBlock { Font = activeFont, FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            loadBtnText.Inlines.Add(new Run("📁 Load MD File..."));
            loadBtn.Content = loadBtnText;
            fileStack.AddChild(loadBtn);

            // Save File Button
            var saveBtn = new Button { WidthConstraint = 130f, HeightConstraint = 32f, CornerRadius = 4f, Margin = new Thickness(0, 0, 8, 0) };
            var saveBtnText = new RichTextBlock { Font = activeFont, FontSize = 11.5f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            saveBtnText.Inlines.Add(new Run("💾 Save MD File..."));
            saveBtn.Content = saveBtnText;
            fileStack.AddChild(saveBtn);




            // Preset Label
            var presetLabel = new RichTextBlock { Font = activeFont, FontSize = 12f, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            presetLabel.Inlines.Add(new Bold(new Run("Load Template:")));
            fileStack.AddChild(presetLabel);

            // Presets ComboBox
            var presetCombo = new ComboBox { Font = activeFont, WidthConstraint = 200f, HeightConstraint = 32f, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            var presetSyntax = new ComboBoxItem("Syntax & Feature Guide");
            var presetReadMe = new ComboBoxItem("Compositor ReadMe Spec");
            var presetCad = new ComboBoxItem("SDF Vector CAD Checklist");
            presetCombo.Items.Add(presetSyntax);
            presetCombo.Items.Add(presetReadMe);
            presetCombo.Items.Add(presetCad);
            presetCombo.SelectedItem = presetSyntax;
            fileStack.AddChild(presetCombo);

            // Status Bar Text
            _statusText = new RichTextBlock { Font = activeFont, FontSize = 11f, Foreground = new ThemeResourceBrush("TextSecondary"), VerticalAlignment = VerticalAlignment.Center };
            _statusText.Inlines.Add(new Run("Idle. Live preview active."));
            fileStack.AddChild(_statusText);

            rootGrid.AddChild(fileBar);
            Microsoft.UI.Xaml.Controls.Grid.SetRow(fileBar, 0);

            // ================= ROW 1: CONFIGURATIONS TOOLBAR =================
            var configBar = new Border
            {
                Background = new ThemeResourceBrush("ControlBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(0, 0, 0, 1f),
                Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Stretch
            };
            var configStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            configBar.Child = configStack;

            // Font Combo
            var fontLbl = new RichTextBlock { Font = activeFont, FontSize = 11.5f, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            fontLbl.Inlines.Add(new Bold(new Run("Font:")));
            configStack.AddChild(fontLbl);

            var fontCombo = new ComboBox { Font = activeFont, WidthConstraint = 140f, HeightConstraint = 32f, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            var fArial = new ComboBoxItem("Arial");
            var fTimes = new ComboBoxItem("Times New Roman");
            var fGeorgia = new ComboBoxItem("Georgia");
            var fCourier = new ComboBoxItem("Courier New");
            fontCombo.Items.Add(fArial);
            fontCombo.Items.Add(fTimes);
            fontCombo.Items.Add(fGeorgia);
            fontCombo.Items.Add(fCourier);
            fontCombo.SelectedItem = fArial;
            configStack.AddChild(fontCombo);

            // Font Size Slider
            var sizeLbl = new RichTextBlock { Font = activeFont, FontSize = 11.5f, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            sizeLbl.Inlines.Add(new Bold(new Run("Size (14):")));
            configStack.AddChild(sizeLbl);

            var sizeSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 10f, Maximum = 24f, Value = 14f, WidthConstraint = 100f, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            configStack.AddChild(sizeSlider);

            // Column Count Slider
            var colLbl = new RichTextBlock { Font = activeFont, FontSize = 11.5f, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            colLbl.Inlines.Add(new Bold(new Run("Columns (1):")));
            configStack.AddChild(colLbl);

            var colSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 1f, Maximum = 3f, Value = 1f, WidthConstraint = 80f, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            configStack.AddChild(colSlider);

            // Column Gap Slider
            var gapLbl = new RichTextBlock { Font = activeFont, FontSize = 11.5f, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            gapLbl.Inlines.Add(new Bold(new Run("Gap (24):")));
            configStack.AddChild(gapLbl);

            var gapSlider = new Microsoft.UI.Xaml.Controls.Slider { Minimum = 12f, Maximum = 48f, Value = 24f, WidthConstraint = 80f, VerticalAlignment = VerticalAlignment.Center };
            configStack.AddChild(gapSlider);

            rootGrid.AddChild(configBar);
            Microsoft.UI.Xaml.Controls.Grid.SetRow(configBar, 1);

            // ================= ROW 2: DUAL-PANE EDITOR & PREVIEW =================
            var workspaceGrid = new Microsoft.UI.Xaml.Controls.Grid { Margin = new Thickness(12) };
            workspaceGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star)); // Left Pane: Editor
            workspaceGrid.ColumnDefinitions.Add(new GridLength(1.1f, GridUnitType.Star)); // Right Pane: Previewer

            // LEFT PANE: EDITOR CARD WITH TOOLBAR (using Grid to fill panel space dynamically)
            var editorContainerGrid = new Microsoft.UI.Xaml.Controls.Grid { Margin = new Thickness(0, 0, 10, 0) };
            editorContainerGrid.RowDefinitions.Add(new GridLength(34f, GridUnitType.Absolute)); // Row 0: Formatting Toolbar
            editorContainerGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Row 1: RichEditBox

            // Editor Header & Formatting Toolbar
            var toolbarBorder = new Border
            {
                Background = new ThemeResourceBrush("ControlBackgroundHover"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f, 1f, 1f, 0),
                CornerRadius = 6f,
                Padding = new Thickness(8, 4, 8, 4),
                HeightConstraint = 34f
            };
            var toolbarStack = new StackPanel { Orientation = Orientation.Horizontal };
            toolbarBorder.Child = toolbarStack;

            void CreateToolbarBtn(string text, string markdownSyntax)
            {
                var btn = new Button { WidthConstraint = 42f, HeightConstraint = 26f, CornerRadius = 3f, Margin = new Thickness(0, 0, 4, 0) };
                var btnText = new RichTextBlock { Font = activeFont, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                btnText.Inlines.Add(new Run(text));
                btn.Content = btnText;
                btn.Click += (s, e) => InsertMarkdownSyntax(markdownSyntax);
                toolbarStack.AddChild(btn);
            }

            CreateToolbarBtn("B", "**bold**");
            CreateToolbarBtn("I", "*italic*");
            CreateToolbarBtn("H1", "\n# Header 1\n");
            CreateToolbarBtn("H2", "\n## Header 2\n");
            CreateToolbarBtn("LI", "\n- Bullet item\n");
            CreateToolbarBtn("{}", "\n```csharp\n// code run\n```\n");
            CreateToolbarBtn("Tb", "\nHeader | Value\n---|---\nData 1 | Data 2\n");
            CreateToolbarBtn("Lnk", "[Link Label](https://)");
            CreateToolbarBtn("Img", "![Caption](path.bmp)");
            CreateToolbarBtn("---", "\n---\n");

            editorContainerGrid.AddChild(toolbarBorder);
            Microsoft.UI.Xaml.Controls.Grid.SetRow(toolbarBorder, 0);

            // Editor Box
            _editorControl = new RichEditBox
            {
                Font = AppState.GetFontCourier() ?? activeFont,
                FontSize = 13f,
                CornerRadius = 6f,
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _editorControl.Inlines.Clear();
            _editorControl.Inlines.Add(new Run(OperatingSystem.IsBrowser()
                ? GetDefaultTemplateText()
                : string.Empty));
            editorContainerGrid.AddChild(_editorControl);
            Microsoft.UI.Xaml.Controls.Grid.SetRow(_editorControl, 1);
            workspaceGrid.AddChild(editorContainerGrid);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(editorContainerGrid, 0);

            // RIGHT PANE: PREVIEW CARD
            var previewBorder = new Border
            {
                Background = new ThemeResourceBrush("ControlBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(16),
                Margin = new Thickness(10, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            _previewScroll = new ScrollViewer { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            previewBorder.Child = _previewScroll;

            _previewControl = new MarkdownTextBlock
            {
                Font = activeFont,
                FontSize = 14f,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _previewControl.Markdown = OperatingSystem.IsBrowser()
                ? GetDefaultTemplateText()
                : string.Empty;
            _previewScroll.Content = _previewControl;

            workspaceGrid.AddChild(previewBorder);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(previewBorder, 1);

            rootGrid.AddChild(workspaceGrid);
            Microsoft.UI.Xaml.Controls.Grid.SetRow(workspaceGrid, 2);

            // ================= ASYNC ACTIONS & PRESETS SYNC =================

            // Helper to get text representation from RichEditBox
            string GetEditorText()
            {
                if (_editorControl == null) return string.Empty;
                var flatChars = new List<RichChar>();
                var defaultFg = ThemeManager.GetBrush("TextPrimary");
                foreach (var inline in _editorControl.Inlines)
                {
                    TextLayoutEngine.AccumulateInlines(inline, flatChars, defaultFg, _editorControl.FontSize, false, false, false, _editorControl.ActualTheme);
                }
                var sb = new System.Text.StringBuilder();
                foreach (var rc in flatChars)
                {
                    sb.Append(rc.Character);
                }
                return sb.ToString();
            }

            // Sync typing from editor to preview in real-time
            _editorControl.TextChanged += (s, e) =>
            {
                if (_previewControl != null)
                {
                    _previewControl.Markdown = GetEditorText();
                }
            };

            // Preset Switching
            presetCombo.SelectionChanged += (s, e) =>
            {
                if (presetCombo.SelectedItem != null && _editorControl != null && _previewControl != null)
                {
                    string template = presetCombo.SelectedItem.Text switch
                    {
                        "Syntax & Feature Guide" => GetDefaultTemplateText(),
                        "Compositor ReadMe Spec" => GetCompositorSpecText(),
                        "SDF Vector CAD Checklist" => GetCadChecklistText(),
                        _ => GetDefaultTemplateText()
                    };

                    _editorControl.Inlines.Clear();
                    _editorControl.Inlines.Add(new Run(template));
                    _previewControl.Markdown = template;

                    _statusText?.Inlines.Clear();
                    _statusText?.Inlines.Add(new Run($"Successfully loaded preset: {presetCombo.SelectedItem.Text}"));
                    _statusText?.Invalidate();
                }
            };

            // Font & Size Config Hookups
            Action updateConfigs = () =>
            {
                if (_previewControl != null)
                {
                    _previewControl.Font = fontCombo.SelectedItem?.Text switch
                    {
                        "Arial" => AppState.GetFont()!,
                        "Times New Roman" => AppState.GetFontTimes() ?? AppState.GetFont()!,
                        "Georgia" => AppState.GetFontGeorgia() ?? AppState.GetFont()!,
                        "Courier New" => AppState.GetFontCourier() ?? AppState.GetFont()!,
                        _ => AppState.GetFont()!
                    };

                    _previewControl.FontSize = sizeSlider.Value;
                    _previewControl.ColumnCount = (int)colSlider.Value;
                    _previewControl.ColumnGap = gapSlider.Value;
                    _previewControl.Invalidate();

                    sizeLbl.Inlines.Clear();
                    sizeLbl.Inlines.Add(new Bold(new Run($"Size ({sizeSlider.Value:F0}):")));
                    sizeLbl.Invalidate();

                    colLbl.Inlines.Clear();
                    colLbl.Inlines.Add(new Bold(new Run($"Columns ({(int)colSlider.Value}):")));
                    colLbl.Invalidate();

                    gapLbl.Inlines.Clear();
                    gapLbl.Inlines.Add(new Bold(new Run($"Gap ({gapSlider.Value:F0}):")));
                    gapLbl.Invalidate();
                }
            };

            fontCombo.SelectionChanged += (s, e) => updateConfigs();
            sizeSlider.ValueChanged += (s, e) => updateConfigs();
            colSlider.ValueChanged += (s, e) => updateConfigs();
            gapSlider.ValueChanged += (s, e) => updateConfigs();



            // Load External MD File Action
            loadBtn.Click += async (s, e) =>
            {
                if (_statusText == null || _editorControl == null || _previewControl == null) return;

                _statusText.Inlines.Clear();
                _statusText.Inlines.Add(new Run("Launching system file picker..."));
                _statusText.Invalidate();

                try
                {
                    var picker = new FileOpenPicker();
                    picker.FileTypeFilter.Add(".md");
                    picker.FileTypeFilter.Add(".txt");
                    picker.FileTypeFilter.Add(".markdown");

                    var file = await picker.PickSingleFileAsync();
                    if (file != null)
                    {
                        string content = await file.ReadTextAsync();
                        _editorControl.Inlines.Clear();
                        _editorControl.Inlines.Add(new Run(content));
                        _previewControl.Markdown = content;

                        _statusText.Inlines.Clear();
                        _statusText.Inlines.Add(new Run($"Loaded external file: {file.Name}"));
                        _statusText.Invalidate();
                    }
                    else
                    {
                        _statusText.Inlines.Clear();
                        _statusText.Inlines.Add(new Run("File load cancelled by user."));
                        _statusText.Invalidate();
                    }
                }
                catch (Exception ex)
                {
                    _statusText.Inlines.Clear();
                    _statusText.Inlines.Add(new Run($"Load failed: {ex.Message}"));
                    _statusText.Invalidate();
                }
            };

            // Save MD File Action
            saveBtn.Click += async (s, e) =>
            {
                if (_statusText == null) return;

                _statusText.Inlines.Clear();
                _statusText.Inlines.Add(new Run("Launching save file dialog..."));
                _statusText.Invalidate();

                try
                {
                    var picker = new FileSavePicker();
                    picker.FileTypeChoices.Add("Markdown Document", new List<string> { ".md" });
                    picker.SuggestedFileName = "document.md";

                    var file = await picker.PickSaveFileAsync();
                    if (file != null)
                    {
                        string mdText = GetEditorText();
                        await file.WriteTextAsync(mdText);

                        _statusText.Inlines.Clear();
                        _statusText.Inlines.Add(new Run($"Saved successfully to: {file.Name}"));
                        _statusText.Invalidate();
                    }
                    else
                    {
                        _statusText.Inlines.Clear();
                        _statusText.Inlines.Add(new Run("Save operation cancelled by user."));
                        _statusText.Invalidate();
                    }
                }
                catch (Exception ex)
                {
                    _statusText.Inlines.Clear();
                    _statusText.Inlines.Add(new Run($"Save failed: {ex.Message}"));
                    _statusText.Invalidate();
                }
            };

            if (!OperatingSystem.IsBrowser())
            {
                // Present the lightweight shell first, then populate the preview and editor on
                // separate frames. The immediate main-style frame allocates roughly 350 MB for
                // both text-heavy panes and can stall in GC; staging keeps each frame bounded.
                var initialMarkdown = GetDefaultTemplateText();
                var initialEditor = _editorControl;
                var initialPreview = _previewControl;
                var initialStatus = _statusText;
                UIThread.Post(() => UIThread.Post(() =>
                {
                    if (string.IsNullOrEmpty(initialPreview.Markdown))
                    {
                        initialPreview.Markdown = initialMarkdown;

                        UIThread.Post(() =>
                        {
                            if (initialEditor.Inlines.Count == 1 &&
                                initialEditor.Inlines[0] is Run { Text.Length: 0 })
                            {
                                initialEditor.Inlines.Clear();
                                initialEditor.Inlines.Add(new Run(initialMarkdown));
                                initialEditor.Invalidate();
                            }

                            initialStatus.Inlines.Clear();
                            initialStatus.Inlines.Add(new Run("Idle. Live preview active."));
                            initialStatus.Invalidate();
                        });
                    }
                }));
            }

            return rootGrid;
        }

        private static void InsertMarkdownSyntax(string syntax)
        {
            if (_editorControl == null) return;

            // Simply insert at current caret position
            int caretIdx = _editorControl.CaretIndex;
            var flatChars = new List<RichChar>();
            var defaultFg = ThemeManager.GetBrush("TextPrimary");
            foreach (var inline in _editorControl.Inlines)
            {
                TextLayoutEngine.AccumulateInlines(inline, flatChars, defaultFg, _editorControl.FontSize, false, false, false, _editorControl.ActualTheme);
            }

            var before = new System.Text.StringBuilder();
            var after = new System.Text.StringBuilder();

            int insertPos = Math.Clamp(caretIdx, 0, flatChars.Count);
            for (int k = 0; k < flatChars.Count; k++)
            {
                if (k < insertPos) before.Append(flatChars[k].Character);
                else after.Append(flatChars[k].Character);
            }

            string resultText = before.ToString() + syntax + after.ToString();
            _editorControl.Inlines.Clear();
            _editorControl.Inlines.Add(new Run(resultText));
            
            if (_previewControl != null)
            {
                _previewControl.Markdown = resultText;
            }
            
            _editorControl.CaretIndex = insertPos + syntax.Length;
            _editorControl.Invalidate();
        }

        private static string GetDefaultTemplateText()
        {
            return @"# ProGPU Unified Markdown Substrate

Welcome to the **ProGPU** real-time GPU-accelerated Markdown rendering engine! This substrate decodes Markdown syntax in real-time using `Markdig` and maps it directly onto our high-performance vector graphics and SDF text compositors.

## Key Features

- **Direct GPU Path Rendering**: Outlines of large symbols (like ★, ✔, ♠) are rendered with subpixel-snapping, Retina quality, and zero performance loss.
- **Unified Text Layout Engine**: Both `RichTextBlock`, `FlowDocument`, and `MarkdownTextBlock` reuse a single, highly-optimized text wrapping and alignment engine.
- **Multi-Column Balance**: Seamlessly partition and balance your flow document across multiple responsive columns. Try toggling the ""Column Count"" slider!

---

## Typography Styles

We support standard Markdown formatting:
- **Bold Text** for extreme emphasis.
- *Italicized Runs* for clean oblique slants.
- **_Combined Bold & Italic Obliques_** for maximum text premium.
- `Inline Code Highlights` with a customized monospaced theme coloring.

---

## Blockquotes & Indentations

> ""This is a premium quote block. Notice the vertical accent line on the left, the subtle backdrop shading, and the clean oblique formatting of the quote text.""

---

## Real-Time Code blocks

```csharp
// High-Performance WebGPU Vector Rendering
public void RenderFrame(DrawingContext context)
{
    var brush = ThemeManager.GetBrush(""SystemAccentColor"");
    context.DrawRoundedRectangle(brush, null, new Rect(0, 0, 100, 100), 8f);
}
```

---

## Vector Border Tables

Metric | Compositor Value | Status
--- | --- | ---
**FPS Benchmark** | `60.0 fps (Ultra)` | Stable
**Render Pipeline** | `SDF WebGPU WGSL` | Active
**Vertices Count** | `12,854 paths` | Cached
";
        }

        private static string GetCompositorSpecText()
        {
            return @"# ProGPU Compositor ReadMe Spec

This spec document outlines the decoupled compositor engine that powers high-fidelity vector rendering in ProGPU.

## WebGPU Swapchain Integration

The swapchain is backed by physical framebuffer pixels rather than logical coordinates to guarantee **High-DPI sharp rendering** on macOS Retina screens.

### 4-Way Subpixel Snapping Rules
1. Snapping occurs in *physical screen coordinates* to a grid of `1/4` pixel boundaries:
   `snapped = Math.Round(pos * DpiScale * 4.0) / (DpiScale * 4.0)`
2. Projection matrices then transform physical coordinates to clip space.

---

## Precise Winding Intersection Rules

To eliminate jagged horizontal lines and intersection seams at transition nodes:
- **Upward crossings** are measured on the interval `[0.0, 1.0)`
- **Downward crossings** are measured on the interval `(0.0, 1.0]`

---

## Performance Metrics

Metric | Target Threshold | Status
---|---|---
**SDF Atlas Resolution** | `2048 x 2048` | OK
**Frame Compilation** | `< 1.20 ms` | Fast
**Renderpass Submission** | `< 0.85 ms` | Fast
";
        }

        private static string GetCadChecklistText()
        {
            return @"# SDF Vector CAD Viewer Checklist

Use this pipeline checklist to verify DXF vector path rendering correctness under GPU acceleration.

## DXF Entities Checked

- [x] **LWPOLYLINE**: Decodes 2D polyline vertices and parses bulges.
- [x] **HATCH**: Extracts polyline boundaries and applies dense filling.
- [ ] **MTEXT**: Decodes multi-line text blocks with character runs.

---

## Visual Diagnostic Checklist

> ""Check glyph atlas allocations whenever coordinate boundaries exhibit jagged lines or flat joints. Use the outlined `TtfDiag` outline extractor tool to dump coordinate segment structures.""

---

## Pipeline Performance Stats

```text
=========================================
ProGPU DXF CAD Parser Diagnostic Log
=========================================
[DXF] Entities Loaded: 14,856
[DXF] Boundary Loop Extraction: 0.12 ms
[DXF] Bezier Control Points: 32,450
[GPU] Draw Submissions: 12
=========================================
```
";
        }
    }
}
