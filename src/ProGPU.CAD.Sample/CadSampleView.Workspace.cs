using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Scene;
using ProGPU.Text;

namespace ProGPU.CAD.Sample;

public sealed partial class CadSampleView
{
    private const float CompactToolbarHeight = 104;
    private const float ExpandedToolbarHeight = 320;

    public bool AreAdvancedToolsVisible { get; private set; }

    private Grid CreateWorkspaceToolbar(StackPanel advancedRows, TtfFont font)
    {
        var workspace = new Grid();
        workspace.RowDefinitions.Add(new GridLength(46, GridUnitType.Absolute));
        workspace.RowDefinitions.Add(new GridLength(46, GridUnitType.Absolute));
        workspace.RowDefinitions.Add(GridLength.Star());

        var header = new Grid();
        header.ColumnDefinitions.Add(GridLength.Star());
        header.ColumnDefinitions.Add(new GridLength(112, GridUnitType.Absolute));
        var fileActions = CreateCompactStrip(
            _openButton, _saveButton, _fitButton, _undoButton, _redoButton,
            _deleteButton, _viewModeButton);
        var basicActions = CreateCompactStrip(
            _lineButton, _circleButton, _polylineButton, _rectangleButton,
            _moveByPointsButton, _copyByPointsButton, _clearSelectionButton);
        var moreTools = CreateButton("More tools", font, 104);
        moreTools.VerticalAlignment = VerticalAlignment.Top;
        var advanced = new ScrollViewer
        {
            Content = advancedRows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed,
        };
        moreTools.Click += (_, _) =>
        {
            AreAdvancedToolsVisible = !AreAdvancedToolsVisible;
            advanced.Visibility = AreAdvancedToolsVisible
                ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)moreTools.Content!).Text = AreAdvancedToolsVisible
                ? "Fewer tools" : "More tools";
            RowDefinitions[0].Height = new GridLength(
                AreAdvancedToolsVisible ? ExpandedToolbarHeight : CompactToolbarHeight,
                GridUnitType.Absolute);
            // Grid definitions do not notify their owning grid here. Invalidate
            // layout and pixels explicitly when its reserved height changes.
            InvalidateMeasure();
            Invalidate();
        };
        header.AddChild(fileActions);
        header.AddChild(moreTools);
        SetColumn(moreTools, 1);
        workspace.AddChild(header);
        workspace.AddChild(basicActions);
        workspace.AddChild(advanced);
        SetRow(basicActions, 1);
        SetRow(advanced, 2);
        return workspace;
    }

    private static ScrollViewer CreateCompactStrip(params Button[] buttons)
    {
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach (Button button in buttons)
        {
            // Move the already-configured control once during construction;
            // expanding/collapsing tools never reparents or recreates controls.
            (button.Parent as ContainerVisual)?.RemoveChild(button);
            strip.AddChild(button);
        }
        return new ScrollViewer
        {
            Content = strip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }
}
