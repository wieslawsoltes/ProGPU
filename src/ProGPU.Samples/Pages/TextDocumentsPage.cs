using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Windowing;
using Silk.NET.Input;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Compute;
using ProGPU.Virtualization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Button = Microsoft.UI.Xaml.Controls.Button;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples;

public static class TextDocumentsPage
{
        private static RichEditBox? _benchmarkEditor;
        private static int _benchmarkPosition;
        private static int _benchmarkDirection = 1;

        internal static void AdvanceBenchmarkScroll(float step)
        {
            if (_benchmarkEditor is not { } editor || editor.Text.Length == 0) return;
            int distance = Math.Max(1, (int)MathF.Round(step * 8f));
            int next = _benchmarkPosition + _benchmarkDirection * distance;
            if (next >= editor.Text.Length)
            {
                next = editor.Text.Length;
                _benchmarkDirection = -1;
            }
            else if (next <= 0)
            {
                next = 0;
                _benchmarkDirection = 1;
            }
            _benchmarkPosition = next;
            editor.TextDocument.GetRange(next, next).ScrollIntoView(Microsoft.UI.Text.PointOptions.None);
        }

        internal static bool TryGetBenchmarkState(out int realizedParagraphs, out int visibleCharacters)
        {
            realizedParagraphs = _benchmarkEditor?.LayoutSession.RealizedBlockCount ?? 0;
            visibleCharacters = 0;
            if (_benchmarkEditor is not { } editor) return false;
            var provider = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer
                .CreatePeerForElement(editor) as Microsoft.UI.Xaml.Automation.Provider.ITextProvider;
            visibleCharacters = provider?.GetVisibleRanges().Sum(static range => range.GetText().Length) ?? 0;
            return realizedParagraphs > 0 && visibleCharacters > 0;
        }

        public static FrameworkElement Create()
        {
            // Column 0: Interactive text typing editors
            var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };
            
            var editorTitle = new RichTextBlock { Font = AppState._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 5) };
            editorTitle.Inlines.Add(new Bold(new Run("Caret-Interactive Input Arenas")));
            leftStack.AddChild(editorTitle);
    
            var editorDesc = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 15) };
            editorDesc.Inlines.Add(new Run("Input focus is obtained on clicking, enabling caret positioning, arrow-key navigation, backspace deletions, and live character typing."));
            leftStack.AddChild(editorDesc);
    
            // TextBox (Single line)
            var textboxLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
            textboxLabel.Inlines.Add(new Bold(new Run("Standard TextBox (Single Line)")));
            leftStack.AddChild(textboxLabel);
    
            var textEntry = new TextBox 
            { 
                Font = AppState._font, 
                Text = "ProGPU typing", 
                Width = 300f, 
                Height = 32f, 
                Margin = new Thickness(0, 0, 0, 20) 
            };
            leftStack.AddChild(textEntry);
    
            // RichEditBox (Multi line)
            var richeditLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 4) };
            richeditLabel.Inlines.Add(new Bold(new Run("Interactive RichEditBox (Formatted Runs)")));
            leftStack.AddChild(richeditLabel);
    
            var richEntry = new RichEditBox 
            { 
                Font = AppState._font, 
                Width = 320f, 
                Height = 180f 
            };
            richEntry.Inlines.Add(new Run("Drag mouse to select text range!\nUse "));
            richEntry.Inlines.Add(new Bold(new Run("Ctrl+B (Bold)")));
            richEntry.Inlines.Add(new Run(", "));
            richEntry.Inlines.Add(new Italic(new Run("Ctrl+I (Italic)")));
            richEntry.Inlines.Add(new Run(", or "));
            richEntry.Inlines.Add(new Underline(new Run("Ctrl+U (Underline)")));
            richEntry.Inlines.Add(new Run(" to toggle style, or type over selection."));
            _benchmarkEditor = richEntry;
            _benchmarkPosition = 0;
            _benchmarkDirection = 1;
            if (string.Equals(
                    SamplePerformanceBenchmark.RequestedPage,
                    "Text & Documents",
                    StringComparison.OrdinalIgnoreCase))
            {
                richEntry.Text = string.Join('\n', Enumerable.Range(0, 20_000).Select(static index =>
                    $"Paragraph {index:D5}: Latin office affinity — العربية مرحبا — עברית שלום — हिन्दी नमस्ते — 日本語かなカナ"));
            }
            leftStack.AddChild(richEntry);
    
            // Formatting & Actions Buttons row (Undo, Redo, Bold, Italic, Underline, Copy, Paste)
            var actionBtns1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
            
            var undoBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 4, 0) };
            undoBtn.Content = new TextVisual { Text = "Undo", FontSize = 11f, Brush = new ThemeResourceBrush("ButtonForeground"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            undoBtn.Click += (s, e) => richEntry.Undo();
    
            var redoBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 4, 0) };
            redoBtn.Content = new TextVisual { Text = "Redo", FontSize = 11f, Brush = new ThemeResourceBrush("ButtonForeground"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            redoBtn.Click += (s, e) => richEntry.Redo();
    
            var boldBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 4, 0) };
            boldBtn.Content = new TextVisual { Text = "Bold", FontSize = 11f, Brush = new ThemeResourceBrush("ButtonForeground"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            boldBtn.Click += (s, e) => richEntry.ToggleStyle("bold");
    
            var italicBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 4, 0) };
            italicBtn.Content = new TextVisual { Text = "Italic", FontSize = 11f, Brush = new ThemeResourceBrush("ButtonForeground"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            italicBtn.Click += (s, e) => richEntry.ToggleStyle("italic");
    
            var underlineBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f };
            underlineBtn.Content = new TextVisual { Text = "Underline", FontSize = 11f, Brush = new ThemeResourceBrush("ButtonForeground"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            underlineBtn.Click += (s, e) => richEntry.ToggleStyle("underline");
    
            actionBtns1.AddChild(undoBtn);
            actionBtns1.AddChild(redoBtn);
            actionBtns1.AddChild(boldBtn);
            actionBtns1.AddChild(italicBtn);
            actionBtns1.AddChild(underlineBtn);
            leftStack.AddChild(actionBtns1);
    
            var actionBtns2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            
            var copyBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 4, 0) };
            copyBtn.Content = new TextVisual { Text = "Copy", FontSize = 11f, Brush = new ThemeResourceBrush("ButtonForeground"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            copyBtn.Click += (s, e) => richEntry.Copy();
    
            var cutBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 4, 0) };
            cutBtn.Content = new TextVisual { Text = "Cut", FontSize = 11f, Brush = new ThemeResourceBrush("ButtonForeground"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            cutBtn.Click += (s, e) => richEntry.Cut();
    
            var pasteBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f };
            pasteBtn.Content = new TextVisual { Text = "Paste", FontSize = 11f, Brush = new ThemeResourceBrush("ButtonForeground"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            pasteBtn.Click += (s, e) => richEntry.PasteFromClipboard();
    
            actionBtns2.AddChild(copyBtn);
            actionBtns2.AddChild(cutBtn);
            actionBtns2.AddChild(pasteBtn);
            leftStack.AddChild(actionBtns2);
    
            // Column 1: Multi-column FlowDocument
            var rightStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };
            var docTitle = new RichTextBlock { Font = AppState._font, FontSize = 14f, Margin = new Thickness(8, 0, 0, 5) };
            docTitle.Inlines.Add(new Bold(new Run("Multi-Column Structured FlowDocument")));
            rightStack.AddChild(docTitle);
    
            var flowDoc = new FlowDocument 
            { 
                Font = AppState._font, 
                FontSize = 11.5f, 
                ColumnCount = 2, 
                ColumnGap = 22f,
                Height = 330f
            };
            
            flowDoc.Blocks.Add(new Paragraph(
                new Bold(new Run("GPU Substrate Typography\n")),
                new Run("The new text layout is powered by real-time SDF atlas packing, producing extremely sharp vector paths with synthetic "),
                new Bold(new Run("bold")),
                new Run(", "),
                new Italic(new Run("italic")),
                new Run(", and "),
                new Bold(new Italic(new Run("bold-italic obliques"))),
                new Run(" rendering seamlessly.")
            ));
    
            // Add a clean bullet list block
            var bulletList = new ListBlock { IsOrdered = false, Indentation = 18f };
            bulletList.Items.Add(new ListItem(new Run("GPU-accelerated text layout")));
            bulletList.Items.Add(new ListItem(new Run("Flow balance across column paths")));
            bulletList.Items.Add(new ListItem(new Run("Crisp vector borders and tables")));
            flowDoc.Blocks.Add(bulletList);
    
            flowDoc.Blocks.Add(new Paragraph(
                new Italic(new Run("Flow-Balanced Columns:\n")),
                new Run("Text flows between columns automatically, managing margins, alignment bounds, and paragraphs dynamically.")
            ));
    
            flowDoc.Blocks.Add(new Paragraph(
                new Bold(new Run("Real Font-Driven Unicode Vector Symbols:\n")),
                new Run("ProGPU decodes full 32-bit Unicode characters from standard font files and renders their outlines natively on the GPU: "),
                new Bold(new Run("★ ")),
                new Run("Star, "),
                new Bold(new Run("♠ ")),
                new Run("Spade, "),
                new Bold(new Run("♦ ")),
                new Run("Diamond, "),
                new Bold(new Run("♣ ")),
                new Run("Club, "),
                new Bold(new Run("♥ ")),
                new Run("Heart, "),
                new Bold(new Run("▲ ")),
                new Run("Up, and "),
                new Bold(new Run("▼ ")),
                new Run("Down render with zero performance loss as crisp, premium vector paths!")
            ));
    
            // Add a beautiful structured vector table
            var table = new Table
            {
                CellPadding = 5f,
                BorderThickness = 1f,
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                ColumnWidths = new List<float> { 70f, 100f }
            };
    
            // Table Header
            var headerRow = new TableRow(
                new TableCell(new Bold(new Run("Metric"))) { Background = new ThemeResourceBrush("ControlBackgroundHover") },
                new TableCell(new Bold(new Run("Compositor Value"))) { Background = new ThemeResourceBrush("ControlBackgroundHover") }
            );
            table.Rows.Add(headerRow);
    
            // Table Rows
            table.Rows.Add(new TableRow(
                new TableCell(new Run("FPS")),
                new TableCell(new Bold(new Run("60.0 fps")))
            ));
            table.Rows.Add(new TableRow(
                new TableCell(new Run("Shaders")),
                new TableCell(new Italic(new Run("SDF WebGPU")))
            ));
    
            flowDoc.Blocks.Add(table);
    
            var docBorder = new Border
            {
                Background = new ThemeResourceBrush("ControlBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Margin = new Thickness(4)
            };
            docBorder.Child = flowDoc;
            rightStack.AddChild(docBorder);
    
            return new ResponsiveSplitView
            {
                OpenPaneLength = 420f,
                PaneContent = leftStack,
                MainContent = rightStack
            };
        }
}
