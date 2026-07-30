using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using ProGPU.Samples.ActivityMonitor.Monitoring;
using ProGPU.Text;
using ProGPU.Vector;
using Button = Microsoft.UI.Xaml.Controls.Button;
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace ProGPU.Samples.ActivityMonitor.Presentation;

internal enum ActivityCategory
{
    Cpu,
    Memory,
    Energy,
    Disk,
    Network
}

internal enum ActivityProcessScope
{
    AllProcesses,
    AllProcessesHierarchically,
    MyProcesses,
    SystemProcesses,
    OtherUsersProcesses,
    ActiveProcesses,
    InactiveProcesses,
    GpuProcesses,
    WindowedProcesses,
    SelectedProcesses,
    Applications
}

internal sealed class ActivityUpdateFrequencyChangedEventArgs(TimeSpan interval) : EventArgs
{
    public TimeSpan Interval { get; } = interval;
}

internal sealed class ActivityMonitorView : Grid
{
    private readonly TtfFont _font;
    private readonly DataGrid _dataGrid;
    private TextBlock _subtitle = null!;
    private TextBlock _status = null!;
    private TextBox _search = null!;
    private readonly Grid _footer;
    private readonly Dictionary<ActivityCategory, Sparkline> _histories = new()
    {
        [ActivityCategory.Cpu] = CreateSparkline(),
        [ActivityCategory.Memory] = CreateSparkline(),
        [ActivityCategory.Energy] = CreateSparkline(),
        [ActivityCategory.Disk] = CreateSparkline(),
        [ActivityCategory.Network] = CreateSparkline()
    };
    private readonly Sparkline _batteryHistory = CreateSparkline();
    private readonly Dictionary<ActivityCategory, SelectorBarItem> _categoryItems = new();
    private readonly Dictionary<ActivityProcessScope, RadioMenuFlyoutItem> _scopeItems = new();
    private readonly Dictionary<int, int> _hierarchyDepthByProcessId = new();
    private SelectorBar _categorySelector = null!;
    private AppBarButton _quitButton = null!;
    private AppBarButton _inspectButton = null!;
    private AppBarButton _actionsButton = null!;
    private ActivitySnapshot? _snapshot;
    private ActivityCategory _category;
    private ActivityProcessScope _processScope = ActivityProcessScope.AllProcesses;
    private int? _selectedProcessId;
    private SystemSnapshot? _previousSystem;
    private DateTimeOffset? _previousCapturedAt;
    private long _diskReadRate;
    private long _diskWriteRate;
    private long _diskReadOperationsRate;
    private long _diskWriteOperationsRate;
    private long _networkReadRate;
    private long _networkWriteRate;
    private long _networkReceivedPacketsRate;
    private long _networkSentPacketsRate;

    public ActivityMonitorView(TtfFont font)
    {
        _font = font;
        _histories[ActivityCategory.Cpu].PrimaryStroke =
            new ThemeResourceBrush("ActivityGraphBlue");
        _histories[ActivityCategory.Cpu].SecondaryStroke =
            new ThemeResourceBrush("ActivityGraphRed");
        _histories[ActivityCategory.Memory].PrimaryStroke =
            new ThemeResourceBrush("ActivityGraphOrange");
        _histories[ActivityCategory.Memory].SecondaryStroke = null;
        _histories[ActivityCategory.Energy].PrimaryStroke =
            new ThemeResourceBrush("ActivityGraphBlue");
        _histories[ActivityCategory.Energy].SecondaryStroke = null;
        _histories[ActivityCategory.Disk].PrimaryStroke =
            new ThemeResourceBrush("ActivityGraphBlue");
        _histories[ActivityCategory.Disk].SecondaryStroke =
            new ThemeResourceBrush("ActivityGraphRed");
        _histories[ActivityCategory.Network].PrimaryStroke =
            new ThemeResourceBrush("ActivityGraphBlue");
        _histories[ActivityCategory.Network].SecondaryStroke =
            new ThemeResourceBrush("ActivityGraphRed");
        _batteryHistory.PrimaryStroke = new ThemeResourceBrush("ActivityGraphGreen");
        _batteryHistory.SecondaryStroke = null;
        RequestedTheme = ElementTheme.Light;
        RequestedThemeFamily = VisualThemeFamily.macOS;
        Background = new ThemeResourceBrush("PageBackground");
        RowDefinitions.Add(new GridLength(86, GridUnitType.Absolute));
        RowDefinitions.Add(new GridLength(1, GridUnitType.Star));
        RowDefinitions.Add(new GridLength(164, GridUnitType.Absolute));

        FrameworkElement toolbar = BuildToolbar();
        AddChild(toolbar);

        _dataGrid = new DataGrid
        {
            Font = font,
            FontSize = 12.5f,
            Background = new ThemeResourceBrush("ActivityGraphBackground"),
            SelectionBackground = new ThemeResourceBrush("SystemAccentColor"),
            SelectionForeground = new ThemeResourceBrush("TextOnAccent"),
            HeaderHeight = 34,
            RowHeight = 30,
            RowCornerRadius = 7,
            RowHorizontalInset = 10,
            ShowRowGridLines = false,
            ShowSelectionIndicator = false,
            IsReadOnly = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CellValueBinding = FormatCell,
            CellSortValueBinding = SortCell
        };
        _dataGrid.SelectionChanged += (_, _) =>
        {
            _selectedProcessId = SelectedProcess?.ProcessId;
            UpdateSelectionCommands();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        _dataGrid.DoubleTapped += (_, args) =>
        {
            if (SelectedProcess is not null)
            {
                InspectRequested?.Invoke(this, EventArgs.Empty);
                args.Handled = true;
            }
        };
        _dataGrid.KeyDown += (_, args) =>
        {
            if (args.Key == Silk.NET.Input.Key.Enter && SelectedProcess is not null)
            {
                InspectRequested?.Invoke(this, EventArgs.Empty);
                args.Handled = true;
            }
        };
        AddChild(_dataGrid);
        SetRow(_dataGrid, 1);

        _footer = new Grid
        {
            Width = 580,
            MaxWidth = 580,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 14)
        };
        var footerFrame = new Grid
        {
            Background = new ThemeResourceBrush("ActivityFooterBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        footerFrame.AddChild(_footer);
        AddChild(footerFrame);
        SetRow(footerFrame, 2);

        ConfigureCategory(ActivityCategory.Cpu);
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler? RefreshRequested;
    public event EventHandler? InspectRequested;
    public event EventHandler? TerminationRequested;
    public event EventHandler? SampleRequested;
    public event EventHandler? SpindumpRequested;
    public event EventHandler? SystemDiagnosticsRequested;
    public event EventHandler<ActivityUpdateFrequencyChangedEventArgs>? UpdateFrequencyChanged;

    public ProcessSnapshot? SelectedProcess =>
        _dataGrid.SelectedIndex >= 0 &&
        _dataGrid.SelectedIndex < _dataGrid.ItemsSource.Count
            ? _dataGrid.ItemsSource[_dataGrid.SelectedIndex] as ProcessSnapshot
            : null;

    internal ActivityCategory ActiveCategory => _category;
    internal ActivityProcessScope ProcessScope => _processScope;
    internal int VisibleProcessCount => _dataGrid.ItemsSource.Count;
    internal int HistoryPointCount(ActivityCategory category) =>
        _histories[category].ValueCount;
    internal IReadOnlyList<int> VisibleProcessIds =>
        _dataGrid.ItemsSource
            .OfType<ProcessSnapshot>()
            .Select(process => process.ProcessId)
            .ToArray();
    internal int VisibleHierarchyDepth(int processId) =>
        _hierarchyDepthByProcessId.GetValueOrDefault(processId);
    internal IReadOnlyList<string> ColumnHeaders =>
        _dataGrid.Columns.Select(column => column.Header).ToArray();

    internal void SelectCategory(ActivityCategory category) =>
        ConfigureCategory(category);

    internal void SelectProcessScope(ActivityProcessScope scope)
    {
        _processScope = scope;
        foreach ((ActivityProcessScope itemScope, RadioMenuFlyoutItem item) in _scopeItems)
        {
            item.IsChecked = itemScope == scope;
        }
        UpdateSubtitle();
        RefreshVisibleRows();
    }

    internal void SetSearchText(string text)
    {
        _search.Text = text;
    }

    public void ApplySnapshot(ActivitySnapshot snapshot)
    {
        _snapshot = snapshot;
        _status.Text = $"Updated {snapshot.CapturedAt.ToLocalTime():HH:mm:ss}";
        UpdateHistories(snapshot);
        RefreshVisibleRows();
        UpdateFooter(snapshot);
        _previousSystem = snapshot.System;
        _previousCapturedAt = snapshot.CapturedAt;
    }

    public void SetStatus(string text)
    {
        _status.Text = text;
    }

    private FrameworkElement BuildToolbar()
    {
        var toolbar = new Border
        {
            Background = new ThemeResourceBrush("ActivityToolbarBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 10)
        };
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new GridLength(365, GridUnitType.Absolute));
        layout.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        layout.ColumnDefinitions.Add(new GridLength(330, GridUnitType.Absolute));
        toolbar.Child = layout;

        var identity = new Grid();
        identity.ColumnDefinitions.Add(new GridLength(70, GridUnitType.Absolute));
        identity.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        identity.ColumnDefinitions.Add(new GridLength(42, GridUnitType.Absolute));
        identity.ColumnDefinitions.Add(new GridLength(42, GridUnitType.Absolute));
        identity.ColumnDefinitions.Add(new GridLength(42, GridUnitType.Absolute));
        layout.AddChild(identity);

        var lights = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center
        };
        lights.AddChild(CreateTrafficLight("ActivityTrafficRed"));
        lights.AddChild(CreateTrafficLight("ActivityTrafficYellow"));
        lights.AddChild(CreateTrafficLight("ActivityTrafficGreen"));
        identity.AddChild(lights);

        var titles = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        titles.AddChild(new TextBlock
        {
            Text = "Activity Monitor",
            Font = _font,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new ThemeResourceBrush("TextPrimary")
        });
        _subtitle = new TextBlock
        {
            Text = "All Processes",
            Font = _font,
            FontSize = 12,
            Foreground = new ThemeResourceBrush("TextSecondary")
        };
        titles.AddChild(_subtitle);
        identity.AddChild(titles);
        SetColumn(titles, 1);

        var commandBar = new CommandBar
        {
            Font = _font,
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed,
            OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        _quitButton = CreateToolbarCommand("×", "Stop selected process");
        _quitButton.Click += (_, _) => TerminationRequested?.Invoke(this, EventArgs.Empty);
        commandBar.PrimaryCommands.Add(_quitButton);
        _inspectButton = CreateToolbarCommand("ⓘ", "Inspect selected process");
        _inspectButton.Click += (_, _) => InspectRequested?.Invoke(this, EventArgs.Empty);
        commandBar.PrimaryCommands.Add(_inspectButton);
        _actionsButton = CreateToolbarCommand("•••", "Process actions and view options");
        _actionsButton.Flyout = BuildActionsFlyout();
        commandBar.PrimaryCommands.Add(_actionsButton);
        identity.AddChild(commandBar);
        SetColumn(commandBar, 2);
        SetColumnSpan(commandBar, 3);

        _categorySelector = new SelectorBar
        {
            Font = _font,
            FontSize = 13,
            Background = new ThemeResourceBrush("ActivitySegmentBackground"),
            BorderBrush = new ThemeResourceBrush("ActivitySegmentBorder"),
            BorderThickness = new Thickness(1),
            Height = 46,
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _categorySelector.SelectionChanged += (_, _) =>
        {
            if (_categorySelector.SelectedItem?.Tag is ActivityCategory category &&
                category != _category)
            {
                ConfigureCategory(category);
            }
        };
        layout.AddChild(_categorySelector);
        SetColumn(_categorySelector, 1);

        AddCategoryItem(ActivityCategory.Cpu, "CPU");
        AddCategoryItem(ActivityCategory.Memory, "Memory");
        AddCategoryItem(ActivityCategory.Energy, "Energy");
        AddCategoryItem(ActivityCategory.Disk, "Disk");
        AddCategoryItem(ActivityCategory.Network, "Network");

        var searchArea = new Grid();
        searchArea.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        searchArea.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        _search = new TextBox
        {
            Font = _font,
            FontSize = 13,
            PlaceholderText = "Search",
            Height = 40,
            CornerRadius = 20,
            Padding = new Thickness(18, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        _search.TextChanged += (_, _) => RefreshVisibleRows();
        searchArea.AddChild(_search);
        layout.AddChild(searchArea);
        SetColumn(searchArea, 2);

        _status = new TextBlock
        {
            Text = "Loading live process data…",
            Font = _font,
            FontSize = 10,
            Foreground = new ThemeResourceBrush("TextTertiary"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed
        };
        searchArea.AddChild(_status);

        return toolbar;
    }

    private void AddCategoryItem(ActivityCategory category, string label)
    {
        var item = new SelectorBarItem
        {
            Font = _font,
            FontSize = 13,
            Text = label,
            Tag = category,
            MinWidth = 86
        };
        _categoryItems.Add(category, item);
        _categorySelector.Items.Add(item);
    }

    private AppBarButton CreateToolbarCommand(string glyph, string automationName)
    {
        var button = new AppBarButton
        {
            Font = _font,
            Icon = new FontIcon
            {
                Font = _font,
                Glyph = glyph,
                FontSize = glyph == "•••" ? 11 : 17,
                Foreground = new ThemeResourceBrush("TextPrimary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Label = automationName,
            LabelPosition = CommandBarLabelPosition.Collapsed,
            IsCompact = true,
            Width = 34,
            Height = 34,
            CornerRadius = 17,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, automationName);
        return button;
    }

    private MenuFlyout BuildActionsFlyout()
    {
        var flyout = new MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom
        };
        flyout.Items.Add(CreateMenuItem(
            "Sample Process",
            (_, _) => SampleRequested?.Invoke(this, EventArgs.Empty)));
        flyout.Items.Add(CreateMenuItem(
            "Spindump",
            (_, _) => SpindumpRequested?.Invoke(this, EventArgs.Empty)));
        flyout.Items.Add(CreateMenuItem(
            "System Diagnostics…",
            (_, _) => SystemDiagnosticsRequested?.Invoke(this, EventArgs.Empty)));
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(CreateMenuItem(
            "Refresh Now",
            (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty)));

        var frequency = new MenuFlyoutSubItem
        {
            Text = "Update Frequency",
            AreCheckStatesEnabled = true
        };
        frequency.Items.Add(CreateFrequencyItem("Very often (1 sec)", 1));
        frequency.Items.Add(CreateFrequencyItem("Often (2 sec)", 2, true));
        frequency.Items.Add(CreateFrequencyItem("Normally (5 sec)", 5));
        flyout.Items.Add(frequency);

        var scope = new MenuFlyoutSubItem
        {
            Text = "Process Scope",
            AreCheckStatesEnabled = true
        };
        AddScopeItem(scope, ActivityProcessScope.AllProcesses, "All Processes", true);
        AddScopeItem(scope, ActivityProcessScope.AllProcessesHierarchically, "All Processes, Hierarchically");
        AddScopeItem(scope, ActivityProcessScope.MyProcesses, "My Processes");
        AddScopeItem(scope, ActivityProcessScope.SystemProcesses, "System Processes");
        AddScopeItem(scope, ActivityProcessScope.OtherUsersProcesses, "Other Users’ Processes");
        AddScopeItem(scope, ActivityProcessScope.ActiveProcesses, "Active Processes");
        AddScopeItem(scope, ActivityProcessScope.InactiveProcesses, "Inactive Processes");
        AddScopeItem(scope, ActivityProcessScope.GpuProcesses, "GPU Processes");
        AddScopeItem(scope, ActivityProcessScope.WindowedProcesses, "Windowed Processes");
        AddScopeItem(scope, ActivityProcessScope.SelectedProcesses, "Selected Processes");
        AddScopeItem(scope, ActivityProcessScope.Applications, "Applications");
        flyout.Items.Add(scope);
        return flyout;
    }

    private static MenuFlyoutItem CreateMenuItem(
        string text,
        RoutedEventHandler clicked)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += clicked;
        return item;
    }

    private RadioMenuFlyoutItem CreateFrequencyItem(
        string text,
        double seconds,
        bool isChecked = false)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = text,
            GroupName = "ActivityMonitorUpdateFrequency",
            IsChecked = isChecked
        };
        item.Click += (_, _) => UpdateFrequencyChanged?.Invoke(
            this,
            new ActivityUpdateFrequencyChangedEventArgs(TimeSpan.FromSeconds(seconds)));
        return item;
    }

    private void AddScopeItem(
        MenuFlyoutSubItem parent,
        ActivityProcessScope scope,
        string label,
        bool isChecked = false)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = label,
            GroupName = "ActivityMonitorProcessScope",
            IsChecked = isChecked
        };
        item.Click += (_, _) =>
        {
            _processScope = scope;
            UpdateSubtitle();
            RefreshVisibleRows();
        };
        _scopeItems.Add(scope, item);
        parent.Items.Add(item);
    }

    private static Ellipse CreateTrafficLight(string brushKey) => new()
    {
        Width = 13,
        Height = 13,
        Fill = new ThemeResourceBrush(brushKey),
        Stroke = new ThemeResourceBrush("ActivityTrafficBorder"),
        StrokeThickness = 0.5f,
        VerticalAlignment = VerticalAlignment.Center
    };

    private void ConfigureCategory(ActivityCategory category)
    {
        _category = category;
        if (_categoryItems.TryGetValue(category, out SelectorBarItem? selectedItem) &&
            !ReferenceEquals(_categorySelector.SelectedItem, selectedItem))
        {
            _categorySelector.SelectedItem = selectedItem;
        }

        UpdateSubtitle();
        ConfigureColumns();
        BuildFooter();
        RefreshVisibleRows();
        InvalidateMeasure();
        InvalidateArrange();
        Invalidate();
    }

    private void UpdateSubtitle()
    {
        _subtitle.Text = EffectiveProcessScope switch
        {
            ActivityProcessScope.AllProcesses => "All Processes",
            ActivityProcessScope.AllProcessesHierarchically => "All Processes, Hierarchically",
            ActivityProcessScope.MyProcesses => "My Processes",
            ActivityProcessScope.SystemProcesses => "System Processes",
            ActivityProcessScope.OtherUsersProcesses => "Other Users’ Processes",
            ActivityProcessScope.ActiveProcesses => "Active Processes",
            ActivityProcessScope.InactiveProcesses => "Inactive Processes",
            ActivityProcessScope.GpuProcesses => "GPU Processes",
            ActivityProcessScope.WindowedProcesses => "Windowed Processes",
            ActivityProcessScope.SelectedProcesses => "Selected Processes",
            ActivityProcessScope.Applications => "Applications",
            _ => "All Processes"
        };
    }

    private ActivityProcessScope EffectiveProcessScope =>
        _category == ActivityCategory.Energy
            ? ActivityProcessScope.Applications
            : _processScope;

    private void ConfigureColumns()
    {
        _dataGrid.SortingColumn = null;
        _dataGrid.Columns.Clear();
        switch (_category)
        {
            case ActivityCategory.Cpu:
                AddColumn("Process Name", "*", "Name");
                AddColumn("% CPU", 92, "CpuPercent");
                AddColumn("CPU Time", 110, "CpuTime");
                AddColumn("Threads", 82, "ThreadCount");
                AddColumn("Memory", 105, "MemoryBytes");
                AddColumn("Idle Wake-Ups", 115, "IdleWakeUps");
                AddColumn("Kind", 80, "Kind");
                AddColumn("% GPU", 92, "GpuPercent");
                AddColumn("GPU Time", 105, "GpuTime");
                AddColumn("PID", 76, "ProcessId");
                AddColumn("User", 145, "User");
                break;
            case ActivityCategory.Memory:
                AddColumn("Process Name", "*", "Name");
                AddColumn("Memory", 115, "MemoryBytes");
                AddColumn("Threads", 82, "ThreadCount");
                AddColumn("Ports", 82, "PortCount");
                AddColumn("PID", 76, "ProcessId");
                AddColumn("User", 150, "User");
                AddColumn("% CPU", 82, "CpuPercent");
                AddColumn("Kind", 82, "Kind");
                AddColumn("% GPU", 96, "GpuPercent");
                AddColumn("Real Mem", 115, "MemoryBytes");
                break;
            case ActivityCategory.Energy:
                AddColumn("App Name", "*", "Name");
                AddColumn("Energy Impact", 140, "EnergyImpact");
                AddColumn("App Nap", 100, "AppNap");
                AddColumn("Preventing Sleep", 145, "PreventingSleep");
                AddColumn("User", 170, "User");
                break;
            case ActivityCategory.Disk:
                AddColumn("Process Name", "*", "Name");
                AddColumn("Bytes Written", 145, "DiskWrittenBytes");
                AddColumn("Bytes Read", 145, "DiskReadBytes");
                AddColumn("PID", 82, "ProcessId");
                AddColumn("User", 180, "User");
                break;
            case ActivityCategory.Network:
                AddColumn("Process Name", "*", "Name");
                AddColumn("Sent Bytes", 140, "NetworkSentBytes");
                AddColumn("Rcvd Bytes", 140, "NetworkReceivedBytes");
                AddColumn("Sent Packets", 125, "SentPackets");
                AddColumn("Rcvd Packets", 125, "ReceivedPackets");
                AddColumn("PID", 82, "ProcessId");
                AddColumn("User", 180, "User");
                break;
        }
        _dataGrid.InvalidateMeasure();
        _dataGrid.InvalidateArrange();
    }

    private void AddColumn(string header, DataGridLength width, string property)
    {
        _dataGrid.Columns.Add(new DataGridColumn(header, width, property));
    }

    private void RefreshVisibleRows()
    {
        if (_snapshot is null)
        {
            return;
        }

        int? selectedId = SelectedProcess?.ProcessId ?? _selectedProcessId;
        string query = _search.Text.Trim();
        IEnumerable<ProcessSnapshot> filtered = _snapshot.Processes;
        string currentUser = Environment.UserName;
        filtered = EffectiveProcessScope switch
        {
            ActivityProcessScope.MyProcesses => filtered.Where(process =>
                string.Equals(process.User, currentUser, StringComparison.Ordinal)),
            ActivityProcessScope.SystemProcesses => filtered.Where(process =>
                process.User == "root" || process.User.StartsWith('_')),
            ActivityProcessScope.OtherUsersProcesses => filtered.Where(process =>
                !string.Equals(process.User, currentUser, StringComparison.Ordinal) &&
                process.User != "root" &&
                !process.User.StartsWith('_')),
            ActivityProcessScope.ActiveProcesses => filtered.Where(process =>
                process.CpuPercent > 0.01 || process.GpuPercent.GetValueOrDefault() > 0.01),
            ActivityProcessScope.InactiveProcesses => filtered.Where(process =>
                process.CpuPercent <= 0.01 && process.GpuPercent.GetValueOrDefault() <= 0.01),
            ActivityProcessScope.GpuProcesses => filtered.Where(process =>
                process.GpuPercent.GetValueOrDefault() > 0.01 ||
                process.GpuTime.GetValueOrDefault() > TimeSpan.Zero),
            ActivityProcessScope.WindowedProcesses or
                ActivityProcessScope.Applications =>
                filtered.Where(process => process.IsApplication),
            ActivityProcessScope.SelectedProcesses when selectedId.HasValue =>
                filtered.Where(process => process.ProcessId == selectedId.Value),
            _ => filtered
        };
        if (query.Length > 0)
        {
            filtered = filtered.Where(process =>
                process.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                process.User.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                process.ProcessId.ToString().Contains(query, StringComparison.Ordinal));
        }

        filtered = _category switch
        {
            ActivityCategory.Cpu => filtered.OrderByDescending(process => process.CpuPercent),
            ActivityCategory.Memory => filtered.OrderByDescending(process => process.MemoryBytes),
            ActivityCategory.Energy => filtered.OrderByDescending(process => process.EnergyImpact),
            ActivityCategory.Disk => filtered.OrderByDescending(process => process.DiskWrittenBytes),
            ActivityCategory.Network => filtered.OrderByDescending(process => process.NetworkReceivedBytes),
            _ => filtered
        };

        bool isHierarchical =
            EffectiveProcessScope == ActivityProcessScope.AllProcessesHierarchically;
        _hierarchyDepthByProcessId.Clear();
        if (isHierarchical)
        {
            filtered = ArrangeHierarchically(filtered);
        }
        _dataGrid.CanUserSortColumns = !isHierarchical;
        _dataGrid.ClearItems();
        _dataGrid.ItemsSource.AddRange(filtered);
        if (isHierarchical)
        {
            _dataGrid.SortingColumn = null;
        }
        else if (_dataGrid.SortingColumn is not null)
        {
            _dataGrid.SortItems(_dataGrid.SortingColumn);
        }
        else if (_dataGrid.Columns.Count > 1)
        {
            DataGridColumn defaultColumn = _dataGrid.Columns[1];
            defaultColumn.IsAscending = false;
            _dataGrid.SortItems(defaultColumn);
        }

        if (selectedId.HasValue)
        {
            _dataGrid.SelectedIndex = _dataGrid.ItemsSource.FindIndex(
                item => item is ProcessSnapshot process && process.ProcessId == selectedId.Value);
        }
        UpdateSelectionCommands();
        _dataGrid.Invalidate();
    }

    private IReadOnlyList<ProcessSnapshot> ArrangeHierarchically(
        IEnumerable<ProcessSnapshot> processes)
    {
        List<ProcessSnapshot> ordered = processes.ToList();
        HashSet<int> processIds = ordered
            .Select(process => process.ProcessId)
            .ToHashSet();
        var children = new Dictionary<int, List<ProcessSnapshot>>();
        var roots = new List<ProcessSnapshot>();
        foreach (ProcessSnapshot process in ordered)
        {
            if (process.ParentProcessId != process.ProcessId &&
                processIds.Contains(process.ParentProcessId))
            {
                if (!children.TryGetValue(
                        process.ParentProcessId,
                        out List<ProcessSnapshot>? siblings))
                {
                    siblings = new List<ProcessSnapshot>();
                    children.Add(process.ParentProcessId, siblings);
                }
                siblings.Add(process);
            }
            else
            {
                roots.Add(process);
            }
        }

        var result = new List<ProcessSnapshot>(ordered.Count);
        var visited = new HashSet<int>();
        foreach (ProcessSnapshot root in roots)
        {
            AppendHierarchy(root, 0);
        }
        foreach (ProcessSnapshot process in ordered)
        {
            AppendHierarchy(process, 0);
        }
        return result;

        void AppendHierarchy(ProcessSnapshot process, int depth)
        {
            if (!visited.Add(process.ProcessId))
            {
                return;
            }

            _hierarchyDepthByProcessId[process.ProcessId] = depth;
            result.Add(process);
            if (children.TryGetValue(
                    process.ProcessId,
                    out List<ProcessSnapshot>? descendants))
            {
                foreach (ProcessSnapshot child in descendants)
                {
                    AppendHierarchy(child, depth + 1);
                }
            }
        }
    }

    private void UpdateSelectionCommands()
    {
        bool hasSelection = SelectedProcess is not null;
        _quitButton.IsEnabled = hasSelection;
        _inspectButton.IsEnabled = hasSelection;
    }

    private void BuildFooter()
    {
        DetachGraphs();
        _footer.ClearChildren();
        _footer.ColumnDefinitions.Clear();
        _footer.RowDefinitions.Clear();
        _footer.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        _footer.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        _footer.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));

        if (_snapshot is null)
        {
            AddFooterPanel(0, "SUMMARY", ["Waiting for the first sample…"]);
            return;
        }
        UpdateFooter(_snapshot);
    }

    private void UpdateFooter(ActivitySnapshot snapshot)
    {
        DetachGraphs();
        _footer.ClearChildren();
        SystemSnapshot system = snapshot.System;

        switch (_category)
        {
            case ActivityCategory.Cpu:
                AddFooterPanel(0, string.Empty, [
                    $"System:  {system.SystemCpuPercent:N1}%",
                    $"User:      {system.UserCpuPercent:N1}%",
                    $"Idle:        {system.IdleCpuPercent:N1}%"
                ]);
                AddFooterGraph(1, "CPU LOAD");
                AddFooterPanel(2, string.Empty, [
                    $"Threads:   {system.ThreadCount:N0}",
                    $"Processes: {system.ProcessCount:N0}"
                ]);
                break;
            case ActivityCategory.Memory:
                AddFooterGraph(0, "MEMORY PRESSURE");
                AddFooterPanel(1, string.Empty, [
                    $"Physical Memory: {ActivityMetricFormatter.Bytes(system.PhysicalMemoryBytes)}",
                    $"Memory Used:      {ActivityMetricFormatter.Bytes(system.UsedMemoryBytes)}",
                    $"Cached Files:       {ActivityMetricFormatter.Bytes(system.CachedMemoryBytes)}",
                    $"Swap Used:          {ActivityMetricFormatter.Bytes(system.SwapUsedBytes)}"
                ]);
                AddFooterPanel(2, string.Empty, [
                    $"App Memory:   {ActivityMetricFormatter.Bytes(system.AppMemoryBytes)}",
                    $"Wired Memory: {ActivityMetricFormatter.Bytes(system.WiredMemoryBytes)}",
                    $"Compressed:    {ActivityMetricFormatter.Bytes(system.CompressedMemoryBytes)}"
                ]);
                break;
            case ActivityCategory.Energy:
                AddFooterGraph(0, "ENERGY IMPACT");
                AddFooterPanel(1, string.Empty, BuildBatterySummary(system.Battery));
                AddFooterGraph(2, "BATTERY (Session)", _batteryHistory);
                break;
            case ActivityCategory.Disk:
                AddFooterPanel(0, string.Empty, [
                    $"Reads in:             {system.DiskReadOperations:N0}",
                    $"Writes out:           {system.DiskWriteOperations:N0}",
                    $"Reads in/sec:    {_diskReadOperationsRate:N0}",
                    $"Writes out/sec: {_diskWriteOperationsRate:N0}"
                ]);
                AddFooterGraph(1, "IO");
                AddFooterPanel(2, string.Empty, [
                    $"Data read:          {ActivityMetricFormatter.Bytes(system.DiskReadBytes)}",
                    $"Data written:      {ActivityMetricFormatter.Bytes(system.DiskWrittenBytes)}",
                    $"Data read/sec:    {ActivityMetricFormatter.Bytes(_diskReadRate)}",
                    $"Data written/sec: {ActivityMetricFormatter.Bytes(_diskWriteRate)}"
                ]);
                break;
            case ActivityCategory.Network:
                AddFooterPanel(0, string.Empty, [
                    $"Packets in:          {system.NetworkReceivedPackets:N0}",
                    $"Packets out:        {system.NetworkSentPackets:N0}",
                    $"Packets in/sec:  {_networkReceivedPacketsRate:N0}",
                    $"Packets out/sec: {_networkSentPacketsRate:N0}"
                ]);
                AddFooterGraph(1, "DATA");
                AddFooterPanel(2, string.Empty, [
                    $"Data received: {ActivityMetricFormatter.Bytes(system.NetworkReceivedBytes)}",
                    $"Data sent:         {ActivityMetricFormatter.Bytes(system.NetworkSentBytes)}",
                    $"Received/sec:    {ActivityMetricFormatter.Bytes(_networkReadRate)}",
                    $"Sent/sec:            {ActivityMetricFormatter.Bytes(_networkWriteRate)}"
                ]);
                break;
        }
    }

    private void UpdateHistories(ActivitySnapshot snapshot)
    {
        SystemSnapshot system = snapshot.System;
        double elapsed = Math.Max(
            0.001,
            (snapshot.CapturedAt - (_previousCapturedAt ?? snapshot.CapturedAt)).TotalSeconds);
        _diskReadRate = Rate(system.DiskReadBytes, _previousSystem?.DiskReadBytes, elapsed);
        _diskWriteRate = Rate(system.DiskWrittenBytes, _previousSystem?.DiskWrittenBytes, elapsed);
        _diskReadOperationsRate = Rate(
            system.DiskReadOperations,
            _previousSystem?.DiskReadOperations,
            elapsed);
        _diskWriteOperationsRate = Rate(
            system.DiskWriteOperations,
            _previousSystem?.DiskWriteOperations,
            elapsed);
        _networkReadRate = Rate(system.NetworkReceivedBytes, _previousSystem?.NetworkReceivedBytes, elapsed);
        _networkWriteRate = Rate(system.NetworkSentBytes, _previousSystem?.NetworkSentBytes, elapsed);
        _networkReceivedPacketsRate = Rate(
            system.NetworkReceivedPackets,
            _previousSystem?.NetworkReceivedPackets,
            elapsed);
        _networkSentPacketsRate = Rate(
            system.NetworkSentPackets,
            _previousSystem?.NetworkSentPackets,
            elapsed);

        _histories[ActivityCategory.Cpu].Append(
            system.UserCpuPercent,
            system.SystemCpuPercent);
        _histories[ActivityCategory.Memory].Append(
            system.UsedMemoryBytes,
            system.PhysicalMemoryBytes);
        _histories[ActivityCategory.Energy].Append(
            snapshot.Processes
                .Where(process => process.IsApplication)
                .Sum(process => process.EnergyImpact));
        _histories[ActivityCategory.Disk].Append(
            _diskReadRate,
            _diskWriteRate);
        _histories[ActivityCategory.Network].Append(
            _networkReadRate,
            _networkWriteRate);
        _batteryHistory.Append(system.Battery.ChargePercent);
    }

    private void AddFooterGraph(
        int column,
        string title,
        Sparkline? graph = null)
    {
        var panel = new Grid
        {
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(column == 0 ? 1 : 0, 0, 1, 0),
            Padding = new Thickness(12, 0)
        };
        panel.RowDefinitions.Add(new GridLength(25, GridUnitType.Absolute));
        panel.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));
        panel.AddChild(new TextBlock
        {
            Text = title,
            Font = _font,
            FontSize = 10.5f,
            FontWeight = FontWeights.SemiBold,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        Sparkline history = graph ?? ActiveHistory;
        panel.AddChild(history);
        SetRow(history, 1);
        _footer.AddChild(panel);
        SetColumn(panel, column);
    }

    private void AddFooterPanel(int column, string title, IReadOnlyList<string> lines)
    {
        var border = new Border
        {
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(column == 0 ? 1 : 0, 0, 1, 0),
            Padding = new Thickness(18, 0)
        };
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (title.Length > 0)
        {
            stack.AddChild(new TextBlock
            {
                Text = title,
                Font = _font,
                FontSize = 10.5f,
                FontWeight = FontWeights.SemiBold,
                Foreground = new ThemeResourceBrush("TextSecondary"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        foreach (string line in lines)
        {
            stack.AddChild(new TextBlock
            {
                Text = line,
                Font = _font,
                FontSize = 11.5f,
                Foreground = new ThemeResourceBrush("TextPrimary")
            });
        }
        border.Child = stack;
        _footer.AddChild(border);
        SetColumn(border, column);
    }

    private Sparkline ActiveHistory => _histories[_category];

    private void DetachGraphs()
    {
        foreach (Sparkline graph in _histories.Values.Append(_batteryHistory))
        {
            if (graph.Parent is Panel parent)
            {
                parent.Children.Remove(graph);
            }
        }
    }

    private static Sparkline CreateSparkline() => new()
    {
        MinHeight = 96,
        PrimaryStroke = new ThemeResourceBrush("SystemAccentColor"),
        SecondaryStroke = new ThemeResourceBrush("TabViewItemCloseHover"),
        Background = new ThemeResourceBrush("CardBackground"),
        BorderBrush = new ThemeResourceBrush("ControlBorder"),
        BorderThickness = new Thickness(1)
    };

    private static long Rate(long current, long? previous, double elapsedSeconds) =>
        previous.HasValue
            ? Math.Max(0, (long)((current - previous.Value) / elapsedSeconds))
            : 0;

    private string FormatCell(object item, string propertyName)
    {
        if (item is not ProcessSnapshot process)
        {
            return string.Empty;
        }
        return propertyName switch
        {
            "Name" => FormatProcessName(process),
            "CpuPercent" => ActivityMetricFormatter.Percent(process.CpuPercent),
            "CpuTime" => ActivityMetricFormatter.Duration(process.CpuTime),
            "ThreadCount" => ActivityMetricFormatter.Count(process.ThreadCount),
            "MemoryBytes" => ActivityMetricFormatter.Bytes(process.MemoryBytes),
            "IdleWakeUps" => ActivityMetricFormatter.Count(process.IdleWakeUps),
            "Kind" => process.Kind,
            "GpuPercent" => process.GpuPercent.HasValue
                ? ActivityMetricFormatter.Percent(process.GpuPercent.Value)
                : "Unavailable",
            "GpuTime" => process.GpuTime.HasValue
                ? ActivityMetricFormatter.Duration(process.GpuTime.Value)
                : "Unavailable",
            "ProcessId" => process.ProcessId.ToString(),
            "User" => process.User,
            "PortCount" => ActivityMetricFormatter.Count(process.PortCount),
            "EnergyImpact" => ActivityMetricFormatter.Percent(process.EnergyImpact),
            "AppNap" => process.AppNap ? "Yes" : "No",
            "PreventingSleep" => process.PreventingSleep ? "Yes" : "No",
            "DiskWrittenBytes" => ActivityMetricFormatter.Bytes(process.DiskWrittenBytes),
            "DiskReadBytes" => ActivityMetricFormatter.Bytes(process.DiskReadBytes),
            "NetworkSentBytes" => ActivityMetricFormatter.Bytes(process.NetworkSentBytes),
            "NetworkReceivedBytes" => ActivityMetricFormatter.Bytes(process.NetworkReceivedBytes),
            "SentPackets" => ActivityMetricFormatter.Count(process.NetworkSentPackets),
            "ReceivedPackets" => ActivityMetricFormatter.Count(process.NetworkReceivedPackets),
            _ => string.Empty
        };
    }

    private string FormatProcessName(ProcessSnapshot process)
    {
        int depth = _hierarchyDepthByProcessId.GetValueOrDefault(process.ProcessId);
        return depth == 0
            ? process.Name
            : $"{new string('\u00A0', depth * 3)}↳ {process.Name}";
    }

    internal static IReadOnlyList<string> BuildBatterySummary(BatterySnapshot battery)
    {
        if (!battery.IsPresent)
        {
            return [
                "Battery: Not Present",
                $"Power Source: {battery.PowerSource}",
                "Time Remaining: Unavailable"
            ];
        }

        string timing = string.Equals(
                battery.PowerSource,
                "Battery",
                StringComparison.OrdinalIgnoreCase)
            ? $"Time Remaining:      {battery.TimeRemaining}"
            : battery.IsCharging
                ? $"Time Until Full:      {battery.TimeRemaining}"
                : $"Power Source:        {battery.PowerSource}";
        return [
            $"Remaining charge: {battery.ChargePercent:N0}%",
            battery.IsCharging ? "Battery Is Charging" : "Battery Is Not Charging",
            timing
        ];
    }

    private static IComparable? SortCell(object item, string propertyName)
    {
        if (item is not ProcessSnapshot process)
        {
            return null;
        }
        return propertyName switch
        {
            "Name" => process.Name,
            "CpuPercent" => process.CpuPercent,
            "CpuTime" => process.CpuTime,
            "ThreadCount" => process.ThreadCount,
            "MemoryBytes" => process.MemoryBytes,
            "IdleWakeUps" => process.IdleWakeUps,
            "Kind" => process.Kind,
            "GpuPercent" => process.GpuPercent,
            "GpuTime" => process.GpuTime,
            "ProcessId" => process.ProcessId,
            "User" => process.User,
            "PortCount" => process.PortCount,
            "EnergyImpact" => process.EnergyImpact,
            "AppNap" => process.AppNap,
            "PreventingSleep" => process.PreventingSleep,
            "DiskWrittenBytes" => process.DiskWrittenBytes,
            "DiskReadBytes" => process.DiskReadBytes,
            "NetworkSentBytes" => process.NetworkSentBytes,
            "NetworkReceivedBytes" => process.NetworkReceivedBytes,
            "SentPackets" => process.NetworkSentPackets,
            "ReceivedPackets" => process.NetworkReceivedPackets,
            _ => null
        };
    }
}
