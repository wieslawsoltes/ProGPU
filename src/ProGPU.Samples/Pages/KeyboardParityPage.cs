using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Input;
using ProGPU.Layout;
using ProGPU.Scene;
using ProGPU.Vector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Button = Microsoft.UI.Xaml.Controls.Button;
using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;

namespace ProGPU.Samples;

public class SpatialButton : Button
{
    public int Row { get; }
    public int Col { get; }
    public SpatialButton[,] GridRef { get; }
    private readonly Action<FrameworkElement> _onFocusedAction;

    public SpatialButton(int row, int col, SpatialButton[,] gridRef, string text, Action<FrameworkElement> onFocusedAction)
    {
        Row = row;
        Col = col;
        GridRef = gridRef;
        _onFocusedAction = onFocusedAction;
        IsTabStop = true;

        Content = new RichTextBlock
        {
            Font = AppState._font,
            FontSize = 12f,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ((RichTextBlock)Content).Inlines.Add(new Run(text));
    }

    public override void OnVisualStateChanged()
    {
        base.OnVisualStateChanged();
        if (IsFocused)
        {
            _onFocusedAction?.Invoke(this);
        }
    }

    protected override string GetThemePrefix() => "Button";

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        int nextRow = Row;
        int nextCol = Col;
        bool handled = false;

        if (e.Key == Key.Up)
        {
            nextRow = Math.Max(0, Row - 1);
            handled = true;
        }
        else if (e.Key == Key.Down)
        {
            nextRow = Math.Min(2, Row + 1);
            handled = true;
        }
        else if (e.Key == Key.Left)
        {
            nextCol = Math.Max(0, Col - 1);
            handled = true;
        }
        else if (e.Key == Key.Right)
        {
            nextCol = Math.Min(2, Col + 1);
            handled = true;
        }

        if (handled)
        {
            var nextBtn = GridRef[nextRow, nextCol];
            if (nextBtn != null)
            {
                InputSystem.SetFocus(nextBtn);
                InputSystem.IsKeyboardFocusActive = true;
            }
            e.Handled = true;
        }
        else
        {
            base.OnKeyDown(e);
        }
    }
}

public class InteractiveTextBox : RichEditBox
{
    private readonly Action<string, string> _logAction;
    private readonly Action<FrameworkElement> _onFocusedAction;
    private readonly HashSet<Key> _pressedKeys = new();

    public InteractiveTextBox(Action<string, string> logAction, Action<FrameworkElement> onFocusedAction)
    {
        _logAction = logAction;
        _onFocusedAction = onFocusedAction;
        IsTabStop = true;
    }

    public override void OnVisualStateChanged()
    {
        base.OnVisualStateChanged();
        if (IsFocused)
        {
            _onFocusedAction?.Invoke(this);
        }
        else
        {
            _pressedKeys.Clear();
        }
    }

    public override void OnKeyUp(KeyRoutedEventArgs e)
    {
        _pressedKeys.Remove(e.Key);
        base.OnKeyUp(e);
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        _pressedKeys.Add(e.Key);

        bool isCtrl = _pressedKeys.Contains(Key.ControlLeft) || 
                      _pressedKeys.Contains(Key.ControlRight) || 
                      _pressedKeys.Contains(Key.SuperLeft) || 
                      _pressedKeys.Contains(Key.SuperRight);

        if (isCtrl)
        {
            if (e.Key == Key.C)
            {
                _logAction?.Invoke("Ctrl+C", "Copied selected text to clipboard");
            }
            else if (e.Key == Key.X)
            {
                _logAction?.Invoke("Ctrl+X", "Cut selected text to clipboard");
            }
            else if (e.Key == Key.V)
            {
                _logAction?.Invoke("Ctrl+V", "Pasted text from clipboard");
            }
            else if (e.Key == Key.Z)
            {
                _logAction?.Invoke("Ctrl+Z", "Triggered Undo");
            }
            else if (e.Key == Key.Y)
            {
                _logAction?.Invoke("Ctrl+Y", "Triggered Redo");
            }
            else if (e.Key == Key.A)
            {
                _logAction?.Invoke("Ctrl+A", "Selected all text");
            }
        }

        base.OnKeyDown(e);
    }
}

public static class KeyboardParityPage
{
    private static readonly List<string> _logs = new();
    private static RichTextBlock? _loggerBlock;
    private static RichTextBlock? _focusLabel;

    public static FrameworkElement Create()
    {
        // Root layout Grid
        var grid = new Microsoft.UI.Xaml.Controls.Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new GridLength(90f, GridUnitType.Absolute));   // Header description
        grid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));       // Columns

        // 1. Title & Description
        var descStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 10) };
        var titleText = new RichTextBlock { Font = AppState._font, FontSize = 16f, Margin = new Thickness(0, 0, 0, 4) };
        titleText.Inlines.Add(new Bold(new Run("Keyboard & Navigation Parity Showcase")));
        descStack.AddChild(titleText);

        var descText = new RichTextBlock { Font = AppState._font, FontSize = 11.5f };
        descText.Inlines.Add(new Run("Demonstrates premium Fluent dark input systems:\n• "));
        descText.Inlines.Add(new Bold(new Run("Keyboard Accelerators")));
        descText.Inlines.Add(new Run(" mapping controls to commands; • "));
        descText.Inlines.Add(new Bold(new Run("FocusManager Spatial Navigation")));
        descText.Inlines.Add(new Run(" shifting 2D buttons via Arrow keys; • "));
        descText.Inlines.Add(new Bold(new Run("TextBox Selection Gestures")));
        descText.Inlines.Add(new Run(" with keyboard clipboard & history undo/redo; • "));
        descText.Inlines.Add(new Bold(new Run("TabView Key Navigation")));
        descText.Inlines.Add(new Run(" driving browser-style tabs via Ctrl+Tab, Ctrl+T, Ctrl+W."));
        descStack.AddChild(descText);

        grid.AddChild(descStack);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(descStack, 0);

        // 2. Main Columns layout
        var colsGrid = new Microsoft.UI.Xaml.Controls.Grid();
        colsGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        colsGrid.ColumnDefinitions.Add(new GridLength(1.1f, GridUnitType.Star));

        // Column 0 Stack: Spatial Focus Grid + TextBox Selection Area
        var leftStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };

        // CARD 1: Spatial Focus Grid
        var focusCardStack = new StackPanel { Orientation = Orientation.Vertical };
        var focusCard = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(16f),
            Margin = new Thickness(0, 0, 0, 16f),
            Child = focusCardStack
        };

        var focusHeader = new RichTextBlock { Font = AppState._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 8) };
        focusHeader.Inlines.Add(new Bold(new Run("2D Spatial Focus Grid (Arrow Keys)")));
        focusCardStack.AddChild(focusHeader);

        var focusSub = new RichTextBlock { Font = AppState._font, FontSize = 11f, Foreground = new ThemeResourceBrush("TextSecondary"), Margin = new Thickness(0, 0, 0, 10) };
        focusSub.Inlines.Add(new Run("Use Tab to focus the grid, then move in 2D using Up/Down/Left/Right arrow keys."));
        focusCardStack.AddChild(focusSub);

        _focusLabel = new RichTextBlock { Font = AppState._font, FontSize = 12f, Margin = new Thickness(0, 0, 0, 12) };
        _focusLabel.Inlines.Add(new Run("Last Focused Control: "));
        _focusLabel.Inlines.Add(new Bold(new Run("None")) { Foreground = new ThemeResourceBrush("SystemAccentColor") });
        focusCardStack.AddChild(_focusLabel);

        Action<FrameworkElement> updateFocused = (fe) =>
        {
            if (_focusLabel != null)
            {
                _focusLabel.Inlines.Clear();
                _focusLabel.Inlines.Add(new Run("Last Focused Control: "));
                string name = fe.GetType().Name;
                if (fe is SpatialButton sb) name = $"SpatialButton ({sb.Row + 1}, {sb.Col + 1})";
                _focusLabel.Inlines.Add(new Bold(new Run(name)) { Foreground = new ThemeResourceBrush("SystemAccentColor") });
                _focusLabel.Invalidate();
            }
        };

        // Create the 3x3 Button Grid
        var spatialGrid = new Microsoft.UI.Xaml.Controls.Grid
        {
            WidthConstraint = 280f,
            HeightConstraint = 130f,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        spatialGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        spatialGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        spatialGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        spatialGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        spatialGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));
        spatialGrid.ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        var gridButtons = new SpatialButton[3, 3];
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                var btn = new SpatialButton(r, c, gridButtons, $"Btn {r + 1},{c + 1}", updateFocused)
                {
                    Width = 80f,
                    Height = 32f,
                    Margin = new Thickness(2f)
                };
                gridButtons[r, c] = btn;
                spatialGrid.AddChild(btn);
                Microsoft.UI.Xaml.Controls.Grid.SetRow(btn, r);
                Microsoft.UI.Xaml.Controls.Grid.SetColumn(btn, c);
            }
        }
        focusCardStack.AddChild(spatialGrid);
        leftStack.AddChild(focusCard);

        // CARD 2: TextBox Selection Gestures
        var textCardStack = new StackPanel { Orientation = Orientation.Vertical };
        var textCard = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(16f),
            Child = textCardStack
        };

        var textHeader = new RichTextBlock { Font = AppState._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 8) };
        textHeader.Inlines.Add(new Bold(new Run("TextBox Selection & Clipboard Area")));
        textCardStack.AddChild(textHeader);

        var textSub = new RichTextBlock { Font = AppState._font, FontSize = 11f, Foreground = new ThemeResourceBrush("TextSecondary"), Margin = new Thickness(0, 0, 0, 12) };
        textSub.Inlines.Add(new Run("Type inside, drag mouse to select. Supports keyboard accelerators: Ctrl+C, Ctrl+X, Ctrl+V, Ctrl+Z, Ctrl+Y."));
        textCardStack.AddChild(textSub);

        Action<string, string> logAccelerator = (combo, desc) =>
        {
            LogAccelerator(combo, desc);
        };

        var richEntry = new InteractiveTextBox(logAccelerator, updateFocused)
        {
            Font = AppState._font,
            Width = 320f,
            Height = 110f,
            Margin = new Thickness(0, 0, 0, 10f)
        };
        richEntry.Inlines.Clear();
        richEntry.Inlines.Add(new Run("Interactive typing... Drag selection here.\nTry bold/italic modifiers!"));
        textCardStack.AddChild(richEntry);

        // Toolbar Buttons
        var toolbarRow1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var undoBtn = new Button { Width = 60f, Height = 26f, Margin = new Thickness(0, 0, 4, 0) };
        undoBtn.Content = new TextVisual { Text = "Undo", FontSize = 11f, Brush = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        undoBtn.Click += (s, e) => { richEntry.Undo(); logAccelerator("Button Click", "Triggered Undo"); };

        var redoBtn = new Button { Width = 60f, Height = 26f, Margin = new Thickness(0, 0, 4, 0) };
        redoBtn.Content = new TextVisual { Text = "Redo", FontSize = 11f, Brush = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        redoBtn.Click += (s, e) => { richEntry.Redo(); logAccelerator("Button Click", "Triggered Redo"); };

        var copyBtn = new Button { Width = 60f, Height = 26f, Margin = new Thickness(0, 0, 4, 0) };
        copyBtn.Content = new TextVisual { Text = "Copy", FontSize = 11f, Brush = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        copyBtn.Click += (s, e) => { richEntry.Copy(); logAccelerator("Button Click", "Copied selected text"); };

        var cutBtn = new Button { Width = 60f, Height = 26f, Margin = new Thickness(0, 0, 4, 0) };
        cutBtn.Content = new TextVisual { Text = "Cut", FontSize = 11f, Brush = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        cutBtn.Click += (s, e) => { richEntry.Cut(); logAccelerator("Button Click", "Cut selected text"); };

        var pasteBtn = new Button { Width = 60f, Height = 26f };
        pasteBtn.Content = new TextVisual { Text = "Paste", FontSize = 11f, Brush = new ThemeResourceBrush("TextPrimary"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        pasteBtn.Click += (s, e) => { richEntry.PasteFromClipboard(); logAccelerator("Button Click", "Pasted clipboard text"); };

        toolbarRow1.AddChild(undoBtn);
        toolbarRow1.AddChild(redoBtn);
        toolbarRow1.AddChild(copyBtn);
        toolbarRow1.AddChild(cutBtn);
        toolbarRow1.AddChild(pasteBtn);
        textCardStack.AddChild(toolbarRow1);

        leftStack.AddChild(textCard);
        colsGrid.AddChild(leftStack);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(leftStack, 0);

        // Column 1 Stack: TabView Controller + Logger Panel
        var rightStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };

        // CARD 3: TabView Controller
        var tabCardStack = new StackPanel { Orientation = Orientation.Vertical };
        var tabCard = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(16f),
            Margin = new Thickness(0, 0, 0, 16f),
            Child = tabCardStack
        };

        var tabHeader = new RichTextBlock { Font = AppState._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 6) };
        tabHeader.Inlines.Add(new Bold(new Run("Keyboard-Driven TabView")));
        tabCardStack.AddChild(tabHeader);

        var tabSub = new RichTextBlock { Font = AppState._font, FontSize = 11f, Foreground = new ThemeResourceBrush("TextSecondary"), Margin = new Thickness(0, 0, 0, 12) };
        tabSub.Inlines.Add(new Run("Supports browser accelerators when focused: Ctrl+Tab (next), Ctrl+Shift+Tab (prev), Ctrl+T (add), Ctrl+W (close)."));
        tabCardStack.AddChild(tabSub);

        var tabView = new TabView
        {
            Font = AppState._font,
            Width = 340f,
            Height = 150f
        };

        int tabCount = 0;
        Action addTabAction = () =>
        {
            tabCount++;
            var tabItem = new TabViewItem($"Document {tabCount}");
            var contentStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12) };
            
            var textContent = new RichTextBlock { Font = AppState._font, FontSize = 11.5f };
            textContent.Inlines.Add(new Bold(new Run($"Active Editor Workspace {tabCount}\n")));
            textContent.Inlines.Add(new Run("Press "));
            textContent.Inlines.Add(new Bold(new Run("Ctrl+Tab")));
            textContent.Inlines.Add(new Run(" to switch tabs or "));
            textContent.Inlines.Add(new Bold(new Run("Ctrl+W")));
            textContent.Inlines.Add(new Run(" to close this workspace."));
            contentStack.AddChild(textContent);
            
            tabItem.Content = contentStack;
            tabView.TabItems.Add(tabItem);
            tabView.SelectedItem = tabItem;
        };

        // Add default tabs
        addTabAction();
        addTabAction();

        tabView.TabAddRequested += (s, e) =>
        {
            addTabAction();
        };

        tabView.TabAcceleratorTriggered += (s, e) =>
        {
            logAccelerator(e.CommandName, e.Message);
        };

        tabCardStack.AddChild(tabView);
        rightStack.AddChild(tabCard);

        // CARD 4: Logger Panel
        var logCardStack = new StackPanel { Orientation = Orientation.Vertical };
        var logCard = new Border
        {
            Background = new ThemeResourceBrush("ControlBackground"), // Console backdrop
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(16f),
            Child = logCardStack
        };

        var logHeader = new RichTextBlock { Font = AppState._font, FontSize = 14f, Margin = new Thickness(0, 0, 0, 8) };
        logHeader.Inlines.Add(new Bold(new Run("Keyboard Accelerators Logger")));
        logCardStack.AddChild(logHeader);

        _loggerBlock = new RichTextBlock
        {
            Font = AppState._font,
            FontSize = 11.5f,
            Height = 110f,
            Margin = new Thickness(4)
        };
        _loggerBlock.Inlines.Add(new Run("Console initialized. Waiting for accelerators...") { Foreground = new ThemeResourceBrush("TextSecondary") });
        logCardStack.AddChild(_loggerBlock);

        rightStack.AddChild(logCard);
        colsGrid.AddChild(rightStack);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(rightStack, 1);

        grid.AddChild(colsGrid);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(colsGrid, 1);

        // Reset logs
        _logs.Clear();

        return grid;
    }

    private static void LogAccelerator(string keyCombo, string actionDescription)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string logText = $"[{timestamp}] Triggered {keyCombo}: {actionDescription}";

        _logs.Insert(0, logText);
        if (_logs.Count > 5)
        {
            _logs.RemoveAt(5);
        }

        if (_loggerBlock != null)
        {
            _loggerBlock.Inlines.Clear();
            for (int i = 0; i < _logs.Count; i++)
            {
                var run = new Run(_logs[i] + "\n");
                if (i == 0)
                {
                    run.Foreground = new ThemeResourceBrush("SystemAccentColor"); // Highlight the newest log in Segoe Blue
                }
                else
                {
                    run.Foreground = new ThemeResourceBrush("TextSecondary"); // Dim previous logs
                }
                _loggerBlock.Inlines.Add(run);
            }
            _loggerBlock.PerformRichLayout(_loggerBlock.Size.X > 0 ? _loggerBlock.Size.X : 400f);
            _loggerBlock.Invalidate();
        }
    }
}
