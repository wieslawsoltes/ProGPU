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
        root.RowDefinitions.Add(new GridLength(82f, GridUnitType.Absolute));
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
        title.Inlines.Add(new Run("  •  pure WGSL chunk rendering"));
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
        header.Child = headerLayout;
        root.AddChild(header);
        Grid.SetRow(header, 0);

        var game = new VoxelGameView
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            RenderDistanceInChunks = 6
        };

        void UpdateStatus()
        {
            var capture = game.IsMouseLookActive
                ? "Mouse captured — Esc release"
                : "Click game to capture mouse";
            var name = VoxelBlockCatalog.Get(game.SelectedBlock).Name;
            statusRun.Text =
                $"{capture} • WASD move, Shift sprint, Space jump, F fly • " +
                $"Left mine, right place, wheel/1–7 select • Selected: {name}";
        }

        game.WorldReady += (_, _) =>
        {
            UpdateStatus();
        };
        game.SelectedBlockChanged += (_, _) => UpdateStatus();
        game.MouseLookActiveChanged += (_, _) => UpdateStatus();

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
}
