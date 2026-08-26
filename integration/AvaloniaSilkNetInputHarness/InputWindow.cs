using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaloniaSilkNetInputHarness;

internal sealed class InputWindow : Window
{
    internal InputWindow()
    {
        Title = "ProGPU Avalonia Silk.NET input validation";
        Width = 720;
        Height = 480;
        MinWidth = 420;
        MinHeight = 280;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 44;
        ApplyWindowPlacement();

        var titleBar = new Border
        {
            Height = 44,
            Background = new SolidColorBrush(Color.FromRgb(38, 42, 50)),
            Padding = new Thickness(16, 0),
            Child = new TextBlock
            {
                Text = "Silk.NET input validation",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        WindowDecorationProperties.SetElementRole(
            titleBar,
            WindowDecorationsElementRole.TitleBar);

        var resizeGrip = new Border
        {
            Width = 48,
            Height = 48,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            ZIndex = 100
        };
        WindowDecorationProperties.SetElementRole(
            resizeGrip,
            WindowDecorationsElementRole.ResizeSE);

        var editor = new TextBox
        {
            Name = "InputEditor",
            PlaceholderText = "Type here",
            Width = 360,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var content = new StackPanel
        {
            Margin = new Thickness(28),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "Mouse, touch, keyboard, shortcuts, and gestures",
                    FontSize = 22
                },
                editor,
                new Border
                {
                    Height = 220,
                    Background = new SolidColorBrush(
                        Color.FromRgb(224, 232, 244)),
                    CornerRadius = new CornerRadius(12),
                    Child = new TextBlock
                    {
                        Text = "Pointer target",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            }
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("44,*"),
            Children =
            {
                titleBar,
                content,
                resizeGrip
            }
        };
        Grid.SetRow(content, 1);
        Grid.SetRowSpan(resizeGrip, 2);
    }

    private void ApplyWindowPlacement()
    {
        string? position = Environment.GetEnvironmentVariable(
            "PROGPU_AVALONIA_WINDOW_POSITION");
        if (!string.IsNullOrWhiteSpace(position))
        {
            string[] parts = position.Split(
                ',',
                StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !int.TryParse(
                    parts[0],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int x) ||
                !int.TryParse(
                    parts[1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int y))
            {
                throw new InvalidOperationException(
                    "PROGPU_AVALONIA_WINDOW_POSITION must be X,Y.");
            }

            Position = new PixelPoint(x, y);
        }

        string? startupLocation = Environment.GetEnvironmentVariable(
            "PROGPU_AVALONIA_WINDOW_STARTUP_LOCATION");
        if (!string.IsNullOrWhiteSpace(startupLocation) &&
            Enum.TryParse(
                startupLocation,
                ignoreCase: true,
                out WindowStartupLocation parsed))
        {
            WindowStartupLocation = parsed;
        }
    }
}
