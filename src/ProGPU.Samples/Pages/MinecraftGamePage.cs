using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ProGPU.Vector;
using ProGPU.Voxel;
using ProGPU.Voxel.WinUI;

namespace ProGPU.Samples;

public static class MinecraftGamePage
{
    public static FrameworkElement Create()
    {
        var root = new Grid { Margin = new Thickness(12f) };
        root.RowDefinitions.Add(new GridLength(118f, GridUnitType.Absolute));
        root.RowDefinitions.Add(new GridLength(1f, GridUnitType.Star));

        var header = new Border
        {
            Background = new ThemeResourceBrush("CardBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Padding = new Thickness(14f, 10f),
            Margin = new Thickness(0, 0, 0, 10f)
        };
        var headerLayout = new StackPanel { Orientation = Orientation.Vertical };
        var title = new RichTextBlock
        {
            Font = AppState.GetFont(),
            FontSize = 16f
        };
        title.Inlines.Add(new Bold(new Run("ProGPU Voxel Game")));
        title.Inlines.Add(new Run("  •  pure WGSL raster, ray traversal, materials, and VFX"));
        headerLayout.AddChild(title);

        var statusRun = new Run("Generating deterministic terrain and greedy chunk meshes…");
        var status = new RichTextBlock
        {
            Font = AppState.GetFont(),
            FontSize = 11.5f,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            Margin = new Thickness(0, 4f, 0, 0)
        };
        status.Inlines.Add(statusRun);
        headerLayout.AddChild(status);

        var game = new VoxelGameView
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            RenderDistanceInChunks = 6
        };

        var toggles = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6f, 0, 0)
        };
        var rayTracingToggle = CreateToggle("Ray tracing (R)", game.EnableRayTracing);
        var rainToggle = CreateToggle("Rain (T)", game.EnableRain);
        var motionBlurToggle = CreateToggle("Motion blur (M)", game.EnableMotionBlur);
        var voxelEffectsToggle = CreateToggle("Voxel deformation (V)", game.EnableVoxelEffects);
        toggles.AddChild(rayTracingToggle);
        toggles.AddChild(rainToggle);
        toggles.AddChild(motionBlurToggle);
        toggles.AddChild(voxelEffectsToggle);
        headerLayout.AddChild(toggles);
        header.Child = headerLayout;
        root.AddChild(header);
        Grid.SetRow(header, 0);

        rayTracingToggle.Toggled += (_, _) => game.EnableRayTracing = rayTracingToggle.IsOn;
        rainToggle.Toggled += (_, _) => game.EnableRain = rainToggle.IsOn;
        motionBlurToggle.Toggled += (_, _) => game.EnableMotionBlur = motionBlurToggle.IsOn;
        voxelEffectsToggle.Toggled += (_, _) => game.EnableVoxelEffects = voxelEffectsToggle.IsOn;

        void UpdateStatus()
        {
            var capture = game.IsMouseLookActive
                ? "Mouse captured — Esc release"
                : "Click game to capture mouse";
            var name = VoxelBlockCatalog.Get(game.SelectedBlock).Name;
            var renderer = game.EnableRayTracing ? "WGSL DDA ray tracing" : "greedy-mesh raster";
            statusRun.Text =
                $"{capture} • {renderer} • WASD move, Shift sprint, Space jump, F fly • " +
                $"Left mine, right place, wheel/1–7 select • Selected: {name}";
        }

        game.WorldReady += (_, _) =>
        {
            UpdateStatus();
        };
        game.SelectedBlockChanged += (_, _) => UpdateStatus();
        game.MouseLookActiveChanged += (_, _) => UpdateStatus();
        game.RenderOptionsChanged += (_, _) =>
        {
            rayTracingToggle.IsOn = game.EnableRayTracing;
            rainToggle.IsOn = game.EnableRain;
            motionBlurToggle.IsOn = game.EnableMotionBlur;
            voxelEffectsToggle.IsOn = game.EnableVoxelEffects;
            UpdateStatus();
        };

        var gameFrame = new Border
        {
            Background = new ThemeResourceBrush("ControlBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1f),
            CornerRadius = 8f,
            Child = game
        };
        root.AddChild(gameFrame);
        Grid.SetRow(gameFrame, 1);
        game.StartNewWorld(seed: 1337, chunkRadius: 3);
        return root;
    }

    private static ToggleSwitch CreateToggle(string text, bool isOn)
    {
        var label = new RichTextBlock
        {
            Font = AppState.GetFont(),
            FontSize = 11f
        };
        label.Inlines.Add(new Run(text));
        return new ToggleSwitch
        {
            IsOn = isOn,
            Content = label,
            Margin = new Thickness(0, 0, 14f, 0)
        };
    }
}
