using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProGPU.WinUI.Hmi.Designer;

public sealed partial class HmiDesignerHost
{
    private Pivot _dataTabs = null!, _inspectorTabs = null!;
    private PivotItem _tagPage = null!, _connectionPage = null!, _helpPage = null!;
    private PivotItem _componentsPage = null!, _propertiesPage = null!;
    private ResponsiveSplitView _libraryPane = null!, _inspectorPane = null!;

    private FrameworkElement BuildSetupNavigation()
    {
        // The horizontal parent measures this group at its natural width. Real
        // scroll buttons reveal overflow without growing the fixed studio header.
        var row = Toolbar();
        row.Name = "HmiSetupNavigation";
        row.AddChild(StudioText("PROJECT SETUP", 10));
        Add("HmiSetupTags", "1. Tags", () => ShowSetupData(_tagPage,
            "Add tag, then edit its name, type, initial value and unit. Bindings use the exact tag name."));
        Add("HmiSetupConnections", "2. PLC / connections", () => ShowSetupData(_connectionPage,
            "Add a protocol profile, configure the host, then map an existing tag to its address/topic/node. Connect read-only is a separate action."));
        Add("HmiSetupComponents", "3. Components", () =>
        {
            if (_inspectorPane.DisplayMode == SplitViewDisplayMode.Overlay)
                _inspectorPane.IsPaneOpen = false;
            _libraryTabs!.SelectedIndex = _libraryTabs.Items.IndexOf(_componentsPage);
            _libraryPane.IsPaneOpen = true;
            Status("Search the component library, then drag, draw or insert a symbol. Format changes its graphic convention.");
        });
        Add("HmiSetupBindings", "4. Bind selected", () =>
        {
            if (_libraryPane.DisplayMode == SplitViewDisplayMode.Overlay)
                _libraryPane.IsPaneOpen = false;
            _inspectorTabs.SelectedIndex = _inspectorTabs.Items.IndexOf(_propertiesPage);
            _inspectorPane.IsPaneOpen = true;
            Status(IsPreviewing
                ? "Stop simulation before editing bindings. The current runtime has not been changed."
                : "Select a component, then set Value tag in its HMI properties. Visibility and Enabled tags require Boolean values.");
        });
        Add("HmiSetupHelp", "Setup guide", () => ShowSetupData(_helpPage,
            "Project setup help is open. Authoring is offline; simulation and equipment acquisition are separate explicit actions."));
        return row;

        void Add(string name, string title, Action action)
        {
            var button = Command(title, action);
            button.Name = name;
            row.AddChild(button);
        }
    }

    private FrameworkElement BuildStudioToolStrip(FrameworkElement tools)
    {
        var scroll = new ScrollViewer
        {
            Name = "HmiStudioTools", Content = tools,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var previous = Command("‹", () => Move(-1));
        var next = Command("›", () => Move(1));
        previous.Name = "HmiStudioScrollLeft";
        next.Name = "HmiStudioScrollRight";
        previous.Width = next.Width = 32;
        ToolTipService.SetToolTip(previous, "Earlier setup and studio commands");
        ToolTipService.SetToolTip(next, "More setup and studio commands");
        var strip = new Grid();
        strip.ColumnDefinitions.Add(GridLength.Auto);
        strip.ColumnDefinitions.Add(GridLength.Star(1));
        strip.ColumnDefinitions.Add(GridLength.Auto);
        strip.AddChild(previous);
        strip.AddChild(scroll); SetColumn(scroll, 1);
        strip.AddChild(next); SetColumn(next, 2);
        // Observe the clamped offset after both buttons exist. Move must capture
        // only the viewer, since its delegates are created in button initializers.
        scroll.RegisterPropertyChangedCallback(ScrollViewer.ScrollableWidthProperty, (_, _) => UpdateButtons());
        scroll.RegisterPropertyChangedCallback(ScrollViewer.HorizontalOffsetProperty, (_, _) => UpdateButtons());
        UpdateButtons();
        return strip;

        void Move(int direction)
        {
            scroll.ChangeView(scroll.HorizontalOffset + direction * Math.Max(120f, scroll.ViewportWidth * 0.75f),
                null, null);
        }
        void UpdateButtons()
        {
            previous.IsEnabled = scroll.HorizontalOffset > 0;
            next.IsEnabled = scroll.HorizontalOffset < scroll.ScrollableWidth;
        }
    }

    private void ShowSetupData(PivotItem page, string instruction)
    {
        // Navigation reuses the actual owned page. It never mutates the project,
        // starts/stops a preview, creates a connection or authorizes a write.
        _dataTabs.SelectedIndex = _dataTabs.Items.IndexOf(page);
        SetDataPanelsVisible(true);
        SetDataPanelHeight(Math.Max(360, _dataArea.Height));
        Status(IsPreviewing ? "Stop simulation before editing. " + instruction : instruction);
    }
}
