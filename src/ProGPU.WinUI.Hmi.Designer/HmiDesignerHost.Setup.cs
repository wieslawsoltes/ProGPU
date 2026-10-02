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
        // Visible text and wrapping make the authoring entry points discoverable
        // without requiring a tooltip, hidden data tab or wide desktop window.
        var row = Toolbar();
        row.Name = "HmiSetupNavigation";
        row.Margin = new Thickness(8, 0, 8, 4);
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
