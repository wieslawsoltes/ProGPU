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

internal sealed class ActivityMonitorView : Grid
{
    private readonly TtfFont _font;
    private readonly DataGrid _dataGrid;
    private TextBlock _subtitle = null!;
    private TextBlock _status = null!;
    private TextBox _search = null!;
    private readonly Grid _footer;
    private readonly Dictionary<ActivityCategory, HistoryGraph> _histories = new()
    {
        [ActivityCategory.Cpu] = new HistoryGraph(),
        [ActivityCategory.Memory] = new HistoryGraph(),
        [ActivityCategory.Energy] = new HistoryGraph(),
        [ActivityCategory.Disk] = new HistoryGraph(),
        [ActivityCategory.Network] = new HistoryGraph()
    };
    private Grid? _historyHost;
    private readonly Dictionary<ActivityCategory, Button> _categoryButtons = new();
    private ActivitySnapshot? _snapshot;
    private ActivityCategory _category;
    private int? _selectedProcessId;
    private SystemSnapshot? _previousSystem;
    private DateTimeOffset? _previousCapturedAt;

    public ActivityMonitorView(TtfFont font)
    {
        _font = font;
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
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CellValueBinding = FormatCell,
            CellSortValueBinding = SortCell
        };
        _dataGrid.SelectionChanged += (_, _) =>
        {
            _selectedProcessId = SelectedProcess?.ProcessId;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        AddChild(_dataGrid);
        SetRow(_dataGrid, 1);

        _footer = new Grid
        {
            Background = new ThemeResourceBrush("ActivityFooterBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 14, 0, 14)
        };
        AddChild(_footer);
        SetRow(_footer, 2);

        ConfigureCategory(ActivityCategory.Cpu);
    }

    public event EventHandler? SelectionChanged;
    public event EventHandler? RefreshRequested;
    public event EventHandler? InspectRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler? ForceQuitRequested;

    public ProcessSnapshot? SelectedProcess =>
        _dataGrid.SelectedIndex >= 0 &&
        _dataGrid.SelectedIndex < _dataGrid.ItemsSource.Count
            ? _dataGrid.ItemsSource[_dataGrid.SelectedIndex] as ProcessSnapshot
            : null;

    internal ActivityCategory ActiveCategory => _category;
    internal int VisibleProcessCount => _dataGrid.ItemsSource.Count;
    internal IReadOnlyList<string> ColumnHeaders =>
        _dataGrid.Columns.Select(column => column.Header).ToArray();

    internal void SelectCategory(ActivityCategory category) =>
        ConfigureCategory(category);

    internal void SetSearchText(string text)
    {
        _search.Text = text;
    }

    public void ApplySnapshot(ActivitySnapshot snapshot)
    {
        _snapshot = snapshot;
        _status.Text = $"Updated {snapshot.CapturedAt.ToLocalTime():HH:mm:ss}";
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

        Button quit = CreateToolbarButton("×", "Quit selected process");
        quit.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
        identity.AddChild(quit);
        SetColumn(quit, 2);

        Button inspect = CreateToolbarButton("i", "Inspect selected process");
        inspect.Click += (_, _) => InspectRequested?.Invoke(this, EventArgs.Empty);
        identity.AddChild(inspect);
        SetColumn(inspect, 3);

        Button more = CreateToolbarButton("•••", "Force quit selected process");
        more.Click += (_, _) => ForceQuitRequested?.Invoke(this, EventArgs.Empty);
        identity.AddChild(more);
        SetColumn(more, 4);

        var segmentBorder = new Border
        {
            Background = new ThemeResourceBrush("ActivitySegmentBackground"),
            BorderBrush = new ThemeResourceBrush("ActivitySegmentBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = 20,
            Height = 46,
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4)
        };
        var segments = new Grid();
        for (int index = 0; index < 5; index++)
        {
            segments.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        }
        segmentBorder.Child = segments;
        layout.AddChild(segmentBorder);
        SetColumn(segmentBorder, 1);

        AddCategoryButton(segments, ActivityCategory.Cpu, "CPU", 0);
        AddCategoryButton(segments, ActivityCategory.Memory, "Memory", 1);
        AddCategoryButton(segments, ActivityCategory.Energy, "Energy", 2);
        AddCategoryButton(segments, ActivityCategory.Disk, "Disk", 3);
        AddCategoryButton(segments, ActivityCategory.Network, "Network", 4);

        var searchArea = new Grid();
        searchArea.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        searchArea.ColumnDefinitions.Add(new GridLength(46, GridUnitType.Absolute));
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
        Button refresh = CreateToolbarButton("↻", "Refresh now");
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        searchArea.AddChild(refresh);
        SetColumn(refresh, 1);
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
            Margin = new Thickness(0, 0, 52, 0)
        };
        searchArea.AddChild(_status);
        SetColumnSpan(_status, 2);

        return toolbar;
    }

    private void AddCategoryButton(
        Grid parent,
        ActivityCategory category,
        string label,
        int column)
    {
        var button = new Button
        {
            Font = _font,
            FontSize = 13,
            Content = new TextBlock
            {
                Text = label,
                Font = _font,
                FontSize = 13,
                Foreground = new ThemeResourceBrush("TextPrimary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
            Background = new ThemeResourceBrush("ActivityTransparent"),
            BorderBrush = new ThemeResourceBrush("ActivityTransparent"),
            CornerRadius = 17,
            Padding = new Thickness(12, 5),
            Margin = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        button.Click += (_, _) => ConfigureCategory(category);
        _categoryButtons.Add(category, button);
        parent.AddChild(button);
        SetColumn(button, column);
    }

    private Button CreateToolbarButton(string glyph, string automationName)
    {
        var button = new Button
        {
            Font = _font,
            Content = new TextBlock
            {
                Text = glyph,
                Font = _font,
                FontSize = glyph == "•••" ? 11 : 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = new ThemeResourceBrush("TextPrimary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            },
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
        foreach ((ActivityCategory key, Button button) in _categoryButtons)
        {
            button.Background = new ThemeResourceBrush(
                key == category ? "ActivitySegmentSelected" : "ActivityTransparent");
        }

        _subtitle.Text = category == ActivityCategory.Energy
            ? "Applications"
            : "All Processes";
        ConfigureColumns();
        BuildFooter();
        RefreshVisibleRows();
        InvalidateMeasure();
        InvalidateArrange();
        Invalidate();
    }

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
                AddColumn("% GPU", 78, "GpuPercent");
                AddColumn("GPU Time", 90, "GpuTime");
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
                AddColumn("% GPU", 82, "GpuPercent");
                AddColumn("Real Mem", 115, "MemoryBytes");
                break;
            case ActivityCategory.Energy:
                AddColumn("App Name", "*", "Name");
                AddColumn("Energy Impact", 140, "EnergyImpact");
                AddColumn("12 hr Power", 120, "TwelveHourPower");
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
        if (_category == ActivityCategory.Energy)
        {
            filtered = filtered.Where(process => process.IsApplication);
        }
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

        _dataGrid.ClearItems();
        _dataGrid.ItemsSource.AddRange(filtered);
        if (_dataGrid.SortingColumn is not null)
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
        _dataGrid.Invalidate();
    }

    private void BuildFooter()
    {
        _historyHost?.ClearChildren();
        _historyHost = null;
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
        _historyHost?.ClearChildren();
        _historyHost = null;
        _footer.ClearChildren();
        SystemSnapshot system = snapshot.System;
        double elapsed = Math.Max(
            0.001,
            (snapshot.CapturedAt - (_previousCapturedAt ?? snapshot.CapturedAt)).TotalSeconds);
        long diskReadRate = Rate(system.DiskReadBytes, _previousSystem?.DiskReadBytes, elapsed);
        long diskWriteRate = Rate(system.DiskWrittenBytes, _previousSystem?.DiskWrittenBytes, elapsed);
        long networkReadRate = Rate(system.NetworkReceivedBytes, _previousSystem?.NetworkReceivedBytes, elapsed);
        long networkWriteRate = Rate(system.NetworkSentBytes, _previousSystem?.NetworkSentBytes, elapsed);

        switch (_category)
        {
            case ActivityCategory.Cpu:
                ActiveHistory.Append(system.UserCpuPercent, system.SystemCpuPercent);
                AddFooterPanel(0, "CPU", [
                    $"System:  {system.SystemCpuPercent:N1}%",
                    $"User:      {system.UserCpuPercent:N1}%",
                    $"Idle:        {system.IdleCpuPercent:N1}%"
                ]);
                AddFooterGraph(1, "CPU LOAD");
                AddFooterPanel(2, "TOTAL", [
                    $"Threads:   {system.ThreadCount:N0}",
                    $"Processes: {system.ProcessCount:N0}"
                ]);
                break;
            case ActivityCategory.Memory:
                ActiveHistory.Append(system.UsedMemoryBytes, system.PhysicalMemoryBytes);
                AddFooterGraph(0, "MEMORY PRESSURE");
                AddFooterPanel(1, "MEMORY", [
                    $"Physical Memory: {ActivityMetricFormatter.Bytes(system.PhysicalMemoryBytes)}",
                    $"Memory Used:      {ActivityMetricFormatter.Bytes(system.UsedMemoryBytes)}",
                    $"Cached Files:       {ActivityMetricFormatter.Bytes(system.CachedMemoryBytes)}",
                    $"Swap Used:          {ActivityMetricFormatter.Bytes(system.SwapUsedBytes)}"
                ]);
                AddFooterPanel(2, "DETAIL", [
                    $"App Memory:   {ActivityMetricFormatter.Bytes(system.AppMemoryBytes)}",
                    $"Wired Memory: {ActivityMetricFormatter.Bytes(system.WiredMemoryBytes)}",
                    $"Compressed:    {ActivityMetricFormatter.Bytes(system.CompressedMemoryBytes)}"
                ]);
                break;
            case ActivityCategory.Energy:
                double energy = snapshot.Processes.Where(item => item.IsApplication).Sum(item => item.EnergyImpact);
                ActiveHistory.Append(energy);
                AddFooterGraph(0, "ENERGY IMPACT");
                AddFooterPanel(1, "POWER", [
                    $"Remaining charge: {system.Battery.ChargePercent:N0}%",
                    system.Battery.IsCharging ? "Battery Is Charging" : "Battery Is Not Charging",
                    $"Power source:       {system.Battery.PowerSource}",
                    $"Time remaining:     {system.Battery.TimeRemaining}"
                ]);
                AddFooterPanel(2, "BATTERY", [
                    system.Battery.IsPresent ? "Internal battery detected" : "No battery detected",
                    $"Applications: {snapshot.Processes.Count(item => item.IsApplication):N0}"
                ]);
                break;
            case ActivityCategory.Disk:
                ActiveHistory.Append(diskReadRate, diskWriteRate);
                AddFooterPanel(0, "OPERATIONS", [
                    $"Reads in/sec:    {ActivityMetricFormatter.Bytes(diskReadRate)}",
                    $"Writes out/sec: {ActivityMetricFormatter.Bytes(diskWriteRate)}"
                ]);
                AddFooterGraph(1, "IO");
                AddFooterPanel(2, "DATA", [
                    $"Data read:          {ActivityMetricFormatter.Bytes(system.DiskReadBytes)}",
                    $"Data written:      {ActivityMetricFormatter.Bytes(system.DiskWrittenBytes)}",
                    $"Data read/sec:    {ActivityMetricFormatter.Bytes(diskReadRate)}",
                    $"Data written/sec: {ActivityMetricFormatter.Bytes(diskWriteRate)}"
                ]);
                break;
            case ActivityCategory.Network:
                ActiveHistory.Append(networkReadRate, networkWriteRate);
                AddFooterPanel(0, "PACKETS", [
                    $"Data received/sec: {ActivityMetricFormatter.Bytes(networkReadRate)}",
                    $"Data sent/sec:         {ActivityMetricFormatter.Bytes(networkWriteRate)}"
                ]);
                AddFooterGraph(1, "DATA");
                AddFooterPanel(2, "TOTAL", [
                    $"Data received: {ActivityMetricFormatter.Bytes(system.NetworkReceivedBytes)}",
                    $"Data sent:         {ActivityMetricFormatter.Bytes(system.NetworkSentBytes)}",
                    $"Received/sec:    {ActivityMetricFormatter.Bytes(networkReadRate)}",
                    $"Sent/sec:            {ActivityMetricFormatter.Bytes(networkWriteRate)}"
                ]);
                break;
        }
    }

    private void AddFooterGraph(int column, string title)
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
        HistoryGraph history = ActiveHistory;
        panel.AddChild(history);
        _historyHost = panel;
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
        stack.AddChild(new TextBlock
        {
            Text = title,
            Font = _font,
            FontSize = 10.5f,
            FontWeight = FontWeights.SemiBold,
            Foreground = new ThemeResourceBrush("TextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Center
        });
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

    private HistoryGraph ActiveHistory => _histories[_category];

    private static long Rate(long current, long? previous, double elapsedSeconds) =>
        previous.HasValue
            ? Math.Max(0, (long)((current - previous.Value) / elapsedSeconds))
            : 0;

    private static string FormatCell(object item, string propertyName)
    {
        if (item is not ProcessSnapshot process)
        {
            return string.Empty;
        }
        return propertyName switch
        {
            "Name" => process.Name,
            "CpuPercent" => ActivityMetricFormatter.Percent(process.CpuPercent),
            "CpuTime" => ActivityMetricFormatter.Duration(process.CpuTime),
            "ThreadCount" => ActivityMetricFormatter.Count(process.ThreadCount),
            "MemoryBytes" => ActivityMetricFormatter.Bytes(process.MemoryBytes),
            "IdleWakeUps" => ActivityMetricFormatter.Count(process.IdleWakeUps),
            "Kind" => process.Kind,
            "GpuPercent" => ActivityMetricFormatter.Percent(process.GpuPercent),
            "GpuTime" => ActivityMetricFormatter.Duration(process.GpuTime),
            "ProcessId" => process.ProcessId.ToString(),
            "User" => process.User,
            "PortCount" => ActivityMetricFormatter.Count(process.PortCount),
            "EnergyImpact" => ActivityMetricFormatter.Percent(process.EnergyImpact),
            "TwelveHourPower" => ActivityMetricFormatter.Percent(process.TwelveHourPower),
            "AppNap" => process.AppNap ? "Yes" : "No",
            "PreventingSleep" => process.PreventingSleep ? "Yes" : "No",
            "DiskWrittenBytes" => ActivityMetricFormatter.Bytes(process.DiskWrittenBytes),
            "DiskReadBytes" => ActivityMetricFormatter.Bytes(process.DiskReadBytes),
            "NetworkSentBytes" => ActivityMetricFormatter.Bytes(process.NetworkSentBytes),
            "NetworkReceivedBytes" => ActivityMetricFormatter.Bytes(process.NetworkReceivedBytes),
            "SentPackets" => ActivityMetricFormatter.Count(process.NetworkSentBytes / 1200),
            "ReceivedPackets" => ActivityMetricFormatter.Count(process.NetworkReceivedBytes / 1200),
            _ => string.Empty
        };
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
            "TwelveHourPower" => process.TwelveHourPower,
            "AppNap" => process.AppNap,
            "PreventingSleep" => process.PreventingSleep,
            "DiskWrittenBytes" => process.DiskWrittenBytes,
            "DiskReadBytes" => process.DiskReadBytes,
            "NetworkSentBytes" => process.NetworkSentBytes,
            "NetworkReceivedBytes" => process.NetworkReceivedBytes,
            "SentPackets" => process.NetworkSentBytes,
            "ReceivedPackets" => process.NetworkReceivedBytes,
            _ => null
        };
    }
}
