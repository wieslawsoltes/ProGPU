using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Transpiler;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;

namespace ProGPU.Samples
{
    public class ShaderToyPlaygroundPageGrid : ResponsiveSplitView, IAnimatedElement
    {
        private readonly ShaderToyControl _toyControl;
        private readonly RichEditBox _editor;
        private readonly RichTextBlock _consoleText;
        private readonly TextBlock _statsText;
        private readonly Button _playBtn;
        private readonly Button _pauseBtn;

        private bool _isCodeDirty;
        private DateTime _lastCodeChangeTime;

        public static readonly string Preset1_CosmicWaves = ShaderResource.Load(typeof(ShaderToyPlaygroundPageGrid), "CosmicWaves.wgsl");

        public static readonly string Preset2_StarNest = ShaderResource.Load(typeof(ShaderToyPlaygroundPageGrid), "StarNest.wgsl");

        public static readonly string Preset3_RaymarchedTorus = ShaderResource.Load(typeof(ShaderToyPlaygroundPageGrid), "RaymarchedTorus.wgsl");

        public static readonly string Preset4_RaymarchingPrimitives = ShaderResource.Load(typeof(ShaderToyPlaygroundPageGrid), "RaymarchingPrimitives.glsl");

        public static readonly string Preset5_StarNestGlsl = ShaderResource.Load(typeof(ShaderToyPlaygroundPageGrid), "StarNest.glsl");

        public ShaderToyPlaygroundPageGrid()
        {
            _toyControl = new ShaderToyControl();
            _toyControl.CompilationFailed += HandleCompilationFailed;
            _toyControl.CompilationSucceeded += HandleCompilationSucceeded;

            _editor = new RichEditBox
            {
                Font = AppState._fontCourier,
                FontSize = 12f,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            _consoleText = new RichTextBlock
            {
                Font = AppState._fontCourier,
                FontSize = 11f,
                Foreground = new SolidColorBrush(new Vector4(0.7f, 0.7f, 0.75f, 1f))
            };

            _statsText = new TextBlock
            {
                Text = "Time: 0.0s | Frame: 0",
                Font = AppState._fontCourier,
                FontSize = 11f,
                Foreground = new ThemeResourceBrush("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center
            };

            _playBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 6, 0) };
            _playBtn.Content = new TextBlock { Text = "Play", Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            _pauseBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 6, 0) };
            _pauseBtn.Content = new TextBlock { Text = "Pause", Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            Margin = new Thickness(12);
            OpenPaneLength = 460f;
            CompactModeThreshold = 900f;
            IsPaneScrollEnabled = false;

            // ----------------------------------------------------
            // LEFT COLUMN: Controls & Code Editor
            // ----------------------------------------------------
            var leftGrid = new Grid();
            leftGrid.RowDefinitions.Add(new GridLength(45, GridUnitType.Absolute)); // Header toolbar
            leftGrid.RowDefinitions.Add(new GridLength(40, GridUnitType.Absolute)); // Actions row
            leftGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));    // Editor

            // Row 0: Presets dropdown ComboBox
            var toolbarStack = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            var presetLabel = new TextBlock
            {
                Text = "Preset: ",
                Font = AppState._font,
                FontSize = 12f,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            toolbarStack.AddChild(presetLabel);

            var presetCombo = new ComboBox { Font = AppState._font, Width = 180f };
            var wavesItem = new ComboBoxItem("Cosmic Waves");
            var starNestItem = new ComboBoxItem("Star Nest Journey");
            var torusItem = new ComboBoxItem("Raymarched Torus");
            var primitivesItem = new ComboBoxItem("Raymarching Primitives (GLSL)");
            var starNestGlslItem = new ComboBoxItem("Star Nest (Original GLSL)");
            presetCombo.Items.Add(wavesItem);
            presetCombo.Items.Add(starNestItem);
            presetCombo.Items.Add(torusItem);
            presetCombo.Items.Add(primitivesItem);
            presetCombo.Items.Add(starNestGlslItem);
            presetCombo.SelectedItem = wavesItem;
            toolbarStack.AddChild(presetCombo);

            // Run Button
            var runBtn = new Button
            {
                Width = 100f,
                Height = 28f,
                CornerRadius = 4f,
                Margin = new Thickness(12, 0, 0, 0),
                Background = new ThemeResourceBrush("SystemAccentColor")
            };
            runBtn.Content = new TextBlock
            {
                Text = "Run",
                Font = AppState._font,
                FontSize = 11f,
                Foreground = new SolidColorBrush(Vector4.One),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            runBtn.Click += (s, e) => CompileNow();
            toolbarStack.AddChild(runBtn);

            // Scale Selector
            var scaleLabel = new TextBlock
            {
                Text = "Scale:",
                Font = AppState._font,
                FontSize = 12f,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 4, 0)
            };
            toolbarStack.AddChild(scaleLabel);

            var scaleCombo = new ComboBox { Font = AppState._font, Width = 85f };
            var scale05 = new ComboBoxItem("0.5x");
            var scale10 = new ComboBoxItem("1.0x");
            var scale15 = new ComboBoxItem("1.5x");
            var scale20 = new ComboBoxItem("2.0x");
            var scaleNative = new ComboBoxItem("Retina");
            scaleCombo.Items.Add(scale05);
            scaleCombo.Items.Add(scale10);
            scaleCombo.Items.Add(scale15);
            scaleCombo.Items.Add(scale20);
            scaleCombo.Items.Add(scaleNative);
            scaleCombo.SelectedItem = scale10; // Default to 1.0x (Normal)
            scaleCombo.SelectionChanged += (s, e) =>
            {
                if (_toyControl == null) return;
                if (scaleCombo.SelectedItem == scale05) _toyControl.RenderScale = 0.5f;
                else if (scaleCombo.SelectedItem == scale10) _toyControl.RenderScale = 1.0f;
                else if (scaleCombo.SelectedItem == scale15) _toyControl.RenderScale = 1.5f;
                else if (scaleCombo.SelectedItem == scale20) _toyControl.RenderScale = 2.0f;
                else if (scaleCombo.SelectedItem == scaleNative) _toyControl.RenderScale = 0.0f; // 0.0f = Native DPI
            };
            toolbarStack.AddChild(scaleCombo);

            leftGrid.AddChild(toolbarStack);
            Grid.SetRow(toolbarStack, 0);

            // Row 1: Playing / Timeline Actions
            var actionStack = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            
            _playBtn.Click += (s, e) => SetPlaying(true);
            actionStack.AddChild(_playBtn);

            _pauseBtn.Click += (s, e) => SetPlaying(false);
            actionStack.AddChild(_pauseBtn);

            var resetBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 12, 0) };
            resetBtn.Content = new TextBlock { Text = "Reset", Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            resetBtn.Click += (s, e) => _toyControl.Reset();
            actionStack.AddChild(resetBtn);

            var transpileBtn = new Button { Width = 110f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 12, 0) };
            transpileBtn.Content = new TextBlock { Text = "Transpile GLSL", Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            transpileBtn.Click += (s, e) =>
            {
                try
                {
                    string input = _editor.Text;
                    string translated = ShaderToyTranspiler.Translate(input);
                    _editor.Text = translated;
                    
                    _consoleText.Inlines.Clear();
                    _consoleText.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = new SolidColorBrush(new Vector4(0.2f, 0.8f, 0.2f, 1.0f)) });
                    _consoleText.Inlines.Add(new Bold(new Run("Transpilation succeeded!\n")) { Foreground = new SolidColorBrush(new Vector4(0.2f, 0.8f, 0.2f, 1.0f)) });
                    _consoleText.Inlines.Add(new Run("GLSL code successfully translated to WGSL in editor."));
                    _consoleText.Invalidate();
                    
                    CompileNow();
                }
                catch (Exception ex)
                {
                    _consoleText.Inlines.Clear();
                    _consoleText.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = new SolidColorBrush(new Vector4(0.9f, 0.2f, 0.2f, 1.0f)) });
                    _consoleText.Inlines.Add(new Bold(new Run("Transpilation failed!\n")) { Foreground = new SolidColorBrush(new Vector4(0.9f, 0.2f, 0.2f, 1.0f)) });
                    _consoleText.Inlines.Add(new Run(ex.Message) { Foreground = new SolidColorBrush(new Vector4(0.85f, 0.7f, 0.7f, 1.0f)) });
                    _consoleText.Invalidate();
                }
            };
            actionStack.AddChild(transpileBtn);

            // Stats Text block
            actionStack.AddChild(_statsText);

            leftGrid.AddChild(actionStack);
            Grid.SetRow(actionStack, 1);

            // Row 2: Code Editor (RichEditBox)
            var editorBorder = new Border
            {
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                CornerRadius = 6f,
                Padding = new Thickness(4)
            };

            // Listen to edits and debounce compilation
            _editor.TextChanged += (s, e) =>
            {
                _isCodeDirty = true;
                _lastCodeChangeTime = DateTime.UtcNow;
            };



            editorBorder.Child = _editor;
            leftGrid.AddChild(editorBorder);
            Grid.SetRow(editorBorder, 2);

            PaneContent = leftGrid;

            // ----------------------------------------------------
            // RIGHT COLUMN: Live Canvas & Console Log
            // ----------------------------------------------------
            var rightGrid = new Grid { Margin = new Thickness(12, 0, 0, 0) };
            rightGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));    // ShaderToy control
            rightGrid.RowDefinitions.Add(new GridLength(130, GridUnitType.Absolute)); // Compile output console

            // Row 0: ShaderToy render canvas Card
            var canvasCard = new Border
            {
                Background = new ThemeResourceBrush("CardBackground"),
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                CornerRadius = 8f,
                Padding = new Thickness(0)
            };

            canvasCard.Child = _toyControl;
            rightGrid.AddChild(canvasCard);
            Grid.SetRow(canvasCard, 0);

            // Row 1: Monospaced Console Log Card
            var consoleCard = new Border
            {
                Background = new SolidColorBrush(new Vector4(0.08f, 0.08f, 0.09f, 1f)), // Dark terminal color
                BorderBrush = new ThemeResourceBrush("ControlBorder"),
                BorderThickness = new Thickness(1f),
                CornerRadius = 6f,
                Padding = new Thickness(10),
                Margin = new Thickness(0, 10, 0, 0)
            };

            var consoleScroll = new ScrollViewer { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            
            _consoleText.Inlines.Add(new Run("[System] ShaderToy Playground ready. Welcome!\n"));
            _consoleText.Inlines.Add(new Run("[System] Type WGSL code and click Run (or press Ctrl+Enter)."));

            consoleScroll.Content = _consoleText;
            consoleCard.Child = consoleScroll;
            rightGrid.AddChild(consoleCard);
            Grid.SetRow(consoleCard, 1);

            MainContent = rightGrid;

            // Handle dropdown Preset changes
            presetCombo.SelectionChanged += (s, e) =>
            {
                if (presetCombo.SelectedItem is ComboBoxItem selectedItem)
                {
                    string code = selectedItem.Text switch
                    {
                        "Cosmic Waves" => Preset1_CosmicWaves,
                        "Star Nest Journey" => Preset2_StarNest,
                        "Raymarched Torus" => Preset3_RaymarchedTorus,
                        "Raymarching Primitives (GLSL)" => Preset4_RaymarchingPrimitives,
                        "Star Nest (Original GLSL)" => Preset5_StarNestGlsl,
                        _ => Preset1_CosmicWaves
                    };

                    _editor.Text = code;
                    _toyControl.Reset();
                    CompileNow();
                }
            };

            // Set initial state
            _editor.Text = Preset1_CosmicWaves;
            _toyControl.ShaderSource = Preset1_CosmicWaves;
            SetPlaying(true);
        }

        private void SetPlaying(bool play)
        {
            _toyControl.IsPlaying = play;
            _playBtn.IsEnabled = !play;
            _pauseBtn.IsEnabled = play;
        }

        private void CompileNow()
        {
            _isCodeDirty = false;
            
            _consoleText.Inlines.Clear();
            _consoleText.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] Compiling shader module...\n"));
            _consoleText.Invalidate();

            _toyControl.ShaderSource = _editor.Text;
        }

        private void HandleCompilationSucceeded()
        {
            _consoleText.Inlines.Clear();
            _consoleText.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = new SolidColorBrush(new Vector4(0.2f, 0.8f, 0.2f, 1.0f)) });
            _consoleText.Inlines.Add(new Bold(new Run("Compilation succeeded!\n")) { Foreground = new SolidColorBrush(new Vector4(0.2f, 0.8f, 0.2f, 1.0f)) });
            _consoleText.Inlines.Add(new Run("GPU Pipeline recompiled and hot-swapped smoothly. Zero-copy render target active."));
            _consoleText.Invalidate();
        }

        private void HandleCompilationFailed(string error)
        {
            _consoleText.Inlines.Clear();
            _consoleText.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] ") { Foreground = new SolidColorBrush(new Vector4(0.9f, 0.2f, 0.2f, 1.0f)) });
            _consoleText.Inlines.Add(new Bold(new Run("Compilation failed!\n")) { Foreground = new SolidColorBrush(new Vector4(0.9f, 0.2f, 0.2f, 1.0f)) });
            
            // Output compiler diagnostic error
            _consoleText.Inlines.Add(new Run(error) { Foreground = new SolidColorBrush(new Vector4(0.85f, 0.7f, 0.7f, 1.0f)) });
            _consoleText.Invalidate();
        }

        public void Update(float delta)
        {
            // 1. Accumulate and update visual statistics
            _statsText.Text = $"Time: {_toyControl.Time:F1}s | Frame: {_toyControl.Frame:F0} | FPS: {(1.0f / delta):F0}";

            // 2. Debounced auto-compilation
            if (_isCodeDirty && (DateTime.UtcNow - _lastCodeChangeTime).TotalMilliseconds > 800)
            {
                CompileNow();
            }
        }
    }

    public static class ShaderToyPlaygroundPage
    {
        public static FrameworkElement Create()
        {
            return new ShaderToyPlaygroundPageGrid();
        }
    }
}
