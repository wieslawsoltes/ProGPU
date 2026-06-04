using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
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
    public class ShaderToyPlaygroundPageGrid : Grid, IAnimatedElement
    {
        private readonly ShaderToyControl _toyControl;
        private readonly RichEditBox _editor;
        private readonly RichTextBlock _consoleText;
        private readonly TextBlock _statsText;
        private readonly Button _playBtn;
        private readonly Button _pauseBtn;

        private bool _isCodeDirty;
        private DateTime _lastCodeChangeTime;

        private const string Preset1_CosmicWaves = @"// Rainbow Plasma / Cosmic Waves
fn mainImage(fragCoord: vec2<f32>) -> vec4<f32> {
    let uv = fragCoord / inputs.iResolution.xy;
    let t = inputs.iTime * 1.5;
    
    var col = vec3<f32>(0.0);
    col.r = 0.5 + 0.5 * sin(uv.x * 10.0 + t + sin(uv.y * 5.0 + t));
    col.g = 0.5 + 0.5 * sin(uv.y * 10.0 - t + cos(uv.x * 5.0 + t));
    col.b = 0.5 + 0.5 * sin((uv.x + uv.y) * 5.0 + t + sin(t));
    
    // Pulse circle at mouse position if left-clicked
    let mouse = inputs.iMouse;
    if (mouse.z > 0.0) {
        let dist = distance(fragCoord, mouse.xy);
        let circle = smoothstep(60.0, 58.0, dist);
        col = mix(col, vec3<f32>(1.0, 1.0, 1.0), circle * 0.8);
    }
    
    return vec4<f32>(col, 1.0);
}";

        private const string Preset2_StarNest = @"// Star Nest (Cosmic Space Folding)
fn mainImage(fragCoord: vec2<f32>) -> vec4<f32> {
    let uv = (fragCoord - 0.5 * inputs.iResolution.xy) / inputs.iResolution.y;
    var dir = vec3<f32>(uv * 0.8, 1.0);
    let time = inputs.iTime * 0.05;
    
    // Rotate camera based on mouse or time
    var s = sin(time * 0.3);
    var c = cos(time * 0.3);
    if (inputs.iMouse.z > 0.0) {
        let mouseNorm = inputs.iMouse.xy / inputs.iResolution.xy;
        s = sin(mouseNorm.x * 3.14);
        c = cos(mouseNorm.x * 3.14);
    }
    
    dir = vec3<f32>(dir.x * c - dir.z * s, dir.y, dir.x * s + dir.z * c);
    
    var from = vec3<f32>(1.0, 0.5, 0.5);
    from += vec3<f32>(time * 2.0, time, -2.0);
    
    // Volumetric rendering loop
    var s_val = 0.1;
    var fade = 0.5;
    var v = vec3<f32>(0.0);
    
    for (var r: i32 = 0; r < 12; r = r + 1) {
        var p = from + f32(r) * dir * s_val;
        // Float floor-modulo replacement for WebGPU portability
        p = abs(vec3<f32>(0.85) - (p - floor(p / 1.7) * 1.7));
        
        var pa = 0.0;
        var a = 0.0;
        for (var i: i32 = 0; i < 10; i = i + 1) {
            p = abs(p) / dot(p, p) - vec3<f32>(0.53);
            let len = length(p);
            a = a + abs(len - pa);
            pa = len;
        }
        
        let dm = max(0.0, 0.85 - a * a * 0.001);
        var a_val = a * a * a;
        v = v + vec3<f32>(dm, dm, dm) * fade;
        v = v + vec3<f32>(s_val, s_val * s_val, s_val * s_val * s_val) * a_val * fade * 0.0003;
        fade = fade * 0.86;
    }
    
    let intensity = length(v);
    var col = mix(vec3<f32>(intensity * 0.01), v * 0.1, 0.5);
    col = col * 0.35;
    
    return vec4<f32>(col, 1.0);
}";

        private const string Preset3_RaymarchedTorus = @"// Spinning Raymarched Torus SDF
fn sdTorus(p: vec3<f32>, t: vec2<f32>) -> f32 {
    let q = vec2<f32>(length(p.xz) - t.x, p.y);
    return length(q) - t.y;
}

fn map(p: vec3<f32>) -> f32 {
    let t = inputs.iTime * 1.0;
    let c = cos(t);
    let s = sin(t);
    var rp = p;
    rp = vec3<f32>(rp.x * c - rp.y * s, rp.x * s + rp.y * c, rp.z);
    rp = vec3<f32>(rp.x, rp.y * c - rp.z * s, rp.y * s + rp.z * c);
    return sdTorus(rp, vec2<f32>(1.5, 0.5));
}

fn getNormal(p: vec3<f32>) -> vec3<f32> {
    let eps = 0.001;
    let h = vec2<f32>(eps, 0.0);
    return normalize(vec3<f32>(
        map(p + h.xyy) - map(p - h.xyy),
        map(p + h.yxy) - map(p - h.yxy),
        map(p + h.yyx) - map(p - h.yyx)
    ));
}

fn mainImage(fragCoord: vec2<f32>) -> vec4<f32> {
    let uv = (fragCoord - 0.5 * inputs.iResolution.xy) / inputs.iResolution.y;
    
    let ro = vec3<f32>(0.0, 0.0, -4.0);
    let rd = normalize(vec3<f32>(uv, 1.0));
    
    var t = 0.0;
    var d = 0.0;
    var hit = false;
    for (var i: i32 = 0; i < 80; i = i + 1) {
        let p = ro + rd * t;
        d = map(p);
        if (d < 0.001) {
            hit = true;
            break;
        }
        t = t + d;
        if (t > 10.0) {
            break;
        }
    }
    
    var col = vec3<f32>(0.1, 0.12, 0.15);
    if (hit) {
        let p = ro + rd * t;
        let n = getNormal(p);
        let lightDir = normalize(vec3<f32>(1.0, 2.0, -3.0));
        
        let diff = max(0.0, dot(n, lightDir));
        let viewDir = normalize(ro - p);
        let reflectDir = reflect(-lightDir, n);
        let spec = pow(max(0.0, dot(viewDir, reflectDir)), 32.0);
        
        let baseColor = 0.5 + 0.5 * cos(inputs.iTime + p.xyx + vec3<f32>(0.0, 2.0, 4.0));
        col = baseColor * (diff + 0.1) + vec3<f32>(0.5) * spec;
    }
    
    return vec4<f32>(col, 1.0);
}";

        public ShaderToyPlaygroundPageGrid()
        {
            Margin = new Thickness(12);

            // Columns: Left (Code & Controls) / Right (Canvas & Console)
            ColumnDefinitions.Add(new GridLength(460, GridUnitType.Absolute));
            ColumnDefinitions.Add(new GridLength(1f, GridUnitType.Star));

            // ----------------------------------------------------
            // LEFT COLUMN: Controls & Code Editor
            // ----------------------------------------------------
            var leftGrid = new Grid();
            leftGrid.RowDefinitions.Add(new GridLength(45, GridUnitType.Absolute)); // Header toolbar
            leftGrid.RowDefinitions.Add(new GridLength(40, GridUnitType.Absolute)); // Actions row
            leftGrid.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));    // Editor

            // Row 0: Presets dropdown ComboBox
            var toolbarStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
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
            presetCombo.Items.Add(new ComboBoxItem("Cosmic Waves"));
            presetCombo.Items.Add(new ComboBoxItem("Star Nest Journey"));
            presetCombo.Items.Add(new ComboBoxItem("Raymarched Torus"));
            presetCombo.SelectedIndex = 0;
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
                Text = "▶ Run (Ctrl+↵)",
                Font = AppState._font,
                FontSize = 11f,
                Foreground = new SolidColorBrush(Vector4.One),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            runBtn.Click += (s, e) => CompileNow();
            toolbarStack.AddChild(runBtn);

            leftGrid.AddChild(toolbarStack);
            Grid.SetRow(toolbarStack, 0);

            // Row 1: Playing / Timeline Actions
            var actionStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            
            _playBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 6, 0) };
            _playBtn.Content = new TextBlock { Text = "Play", Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _playBtn.Click += (s, e) => SetPlaying(true);
            actionStack.AddChild(_playBtn);

            _pauseBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 6, 0) };
            _pauseBtn.Content = new TextBlock { Text = "Pause", Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _pauseBtn.Click += (s, e) => SetPlaying(false);
            actionStack.AddChild(_pauseBtn);

            var resetBtn = new Button { Width = 60f, Height = 28f, CornerRadius = 4f, Margin = new Thickness(0, 0, 12, 0) };
            resetBtn.Content = new TextBlock { Text = "Reset", Font = AppState._font, FontSize = 11f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            resetBtn.Click += (s, e) => _toyControl.Reset();
            actionStack.AddChild(resetBtn);

            // Stats Text block
            _statsText = new TextBlock
            {
                Text = "Time: 0.0s | Frame: 0",
                Font = AppState._fontCourier,
                FontSize = 11f,
                Foreground = new ThemeResourceBrush("TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center
            };
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

            _editor = new RichEditBox
            {
                Font = AppState._fontCourier,
                FontSize = 12f,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            
            // Listen to edits and debounce compilation
            _editor.TextChanged += (s, e) =>
            {
                _isCodeDirty = true;
                _lastCodeChangeTime = DateTime.UtcNow;
            };

            _editor.KeyDown += (s, e) =>
            {
                if (e.Key == Silk.NET.Input.Key.Enter && InputSystem.Current.IsControlPressed)
                {
                    e.Handled = true;
                    CompileNow();
                }
            };

            editorBorder.Child = _editor;
            leftGrid.AddChild(editorBorder);
            Grid.SetRow(editorBorder, 2);

            AddChild(leftGrid);
            Grid.SetColumn(leftGrid, 0);

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
                Padding = new Thickness(0),
                ClipToBounds = true
            };

            _toyControl = new ShaderToyControl();
            _toyControl.CompilationFailed += HandleCompilationFailed;
            _toyControl.CompilationSucceeded += HandleCompilationSucceeded;
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
            _consoleText = new RichTextBlock
            {
                Font = AppState._fontCourier,
                FontSize = 11f,
                Foreground = new SolidColorBrush(new Vector4(0.7f, 0.7f, 0.75f, 1f))
            };
            
            _consoleText.Inlines.Add(new Run("[System] ShaderToy Playground ready. Welcome!\n"));
            _consoleText.Inlines.Add(new Run("[System] Type WGSL code and click Run (or press Ctrl+Enter)."));

            consoleScroll.Content = _consoleText;
            consoleCard.Child = consoleScroll;
            rightGrid.AddChild(consoleCard);
            Grid.SetRow(consoleCard, 1);

            AddChild(rightGrid);
            Grid.SetColumn(rightGrid, 1);

            // Handle dropdown Preset changes
            presetCombo.SelectionChanged += (s, e) =>
            {
                if (presetCombo.SelectedItem != null)
                {
                    string code = presetCombo.SelectedItem.Text switch
                    {
                        "Cosmic Waves" => Preset1_CosmicWaves,
                        "Star Nest Journey" => Preset2_StarNest,
                        "Raymarched Torus" => Preset3_RaymarchedTorus,
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

            // 3. Propagate animation ticks to children
            this.UpdateSampleAnimations(delta);
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
