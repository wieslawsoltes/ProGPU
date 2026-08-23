using Avalonia;
using Avalonia.Controls;
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
        titleBar.PointerPressed += OnTitleBarPointerPressed;

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
                content
            }
        };
        Grid.SetRow(content, 1);
    }

    private void OnTitleBarPointerPressed(
        object? sender,
        PointerPressedEventArgs args)
    {
        _ = sender;
        if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(args);
    }
}
