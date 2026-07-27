using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace ProGPU.Avalonia.HeadlessPixelTests;

internal sealed class PixelContractView : Grid
{
    private readonly TextBlock _status;

    public PixelContractView()
    {
        Margin = new Thickness(24);
        RowDefinitions =
        [
            new RowDefinition(GridLength.Auto),
            new RowDefinition(GridLength.Auto),
            new RowDefinition(GridLength.Star)
        ];

        var heading = new TextBlock
        {
            Text = "ProGPU rendering contract",
            FontSize = 24,
            FontWeight = global::Avalonia.Media.FontWeight.SemiBold
        };
        Children.Add(heading);

        _status = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 12),
            Text = "Frame 1 · retained scene",
            FontSize = 15
        };
        SetRow(_status, 1);
        Children.Add(_status);

        var content = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(new GridLength(2, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star))
            ],
            ColumnSpacing = 18
        };
        SetRow(content, 2);
        Children.Add(content);

        var items = new ListBox
        {
            ItemsSource = new[]
            {
                "Vector geometry",
                "OpenType shaping",
                "Texture lifetime",
                "Compositor reuse"
            },
            SelectedIndex = 1
        };
        content.Children.Add(items);

        var controls = new StackPanel
        {
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetColumn(controls, 1);
        content.Children.Add(controls);

        controls.Children.Add(new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 72,
            Height = 10
        });
        controls.Children.Add(new ToggleSwitch
        {
            Content = "GPU composition",
            IsChecked = true
        });
        controls.Children.Add(new Button
        {
            Content = "Capture frame",
            HorizontalAlignment = HorizontalAlignment.Stretch
        });
    }

    public void AdvanceFrame()
    {
        _status.Text = "Frame 2 · invalidated text";
    }
}
