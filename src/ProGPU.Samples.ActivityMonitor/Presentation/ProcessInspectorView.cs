using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Samples.ActivityMonitor.Monitoring;
using ProGPU.Text;
using ProGPU.Vector;

namespace ProGPU.Samples.ActivityMonitor.Presentation;

internal sealed class ProcessInspectorView : Grid
{
    private readonly TtfFont _font;
    private readonly ProcessDetails _details;
    private readonly SelectorBar _selector;
    private readonly Border _contentHost;

    public ProcessInspectorView(TtfFont font, ProcessDetails details)
    {
        _font = font;
        _details = details;
        Width = 760;
        Height = 410;
        Background = new ThemeResourceBrush("CardBackground");
        RowDefinitions.Add(new GridLength(92, GridUnitType.Absolute));
        RowDefinitions.Add(new GridLength(42, GridUnitType.Absolute));
        RowDefinitions.Add(new GridLength(1, GridUnitType.Star));

        AddChild(BuildSummary());

        _selector = new SelectorBar
        {
            Font = font,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new ThemeResourceBrush("ControlBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1),
            Height = 34
        };
        _selector.SelectionChanged += (_, _) => UpdateContent();
        _selector.Items.Add(CreateItem("Memory", InspectorSection.Memory));
        _selector.Items.Add(CreateItem("Statistics", InspectorSection.Statistics));
        _selector.Items.Add(CreateItem("Open Files and Ports", InspectorSection.OpenFilesAndPorts));
        _selector.SelectedItem = _selector.Items[0];
        AddChild(_selector);
        SetRow(_selector, 1);

        _contentHost = new Border
        {
            Background = new ThemeResourceBrush("ActivityInspectorBackground"),
            BorderBrush = new ThemeResourceBrush("ControlBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = 6,
            Padding = new Thickness(18),
            Margin = new Thickness(0, 8, 0, 0)
        };
        AddChild(_contentHost);
        SetRow(_contentHost, 2);
        UpdateContent();
    }

    private FrameworkElement BuildSummary()
    {
        var summary = new Grid();
        summary.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        summary.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
        var left = CreateSummaryColumn(
        [
            $"Executable Path:  {_details.ExecutablePath}",
            $"Parent Process:   {_details.ParentProcessId}",
            $"Process Group:    {_details.Snapshot.ProcessGroupId}",
            $"% CPU:                  {ActivityMetricFormatter.Percent(_details.Snapshot.CpuPercent)}"
        ]);
        var right = CreateSummaryColumn(
        [
            $"User:                 {_details.User}",
            $"PID:                    {_details.ProcessId}",
            $"Recent hangs:   Unavailable",
            $"Started:              {FormatStartTime(_details.StartTime)}"
        ]);
        summary.AddChild(left);
        summary.AddChild(right);
        SetColumn(right, 1);
        return summary;
    }

    private StackPanel CreateSummaryColumn(IReadOnlyList<string> lines)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 6
        };
        foreach (string line in lines)
        {
            stack.AddChild(new TextBlock
            {
                Font = _font,
                FontSize = 12,
                Text = line,
                Foreground = new ThemeResourceBrush("TextPrimary")
            });
        }
        return stack;
    }

    private SelectorBarItem CreateItem(string text, InspectorSection section) => new()
    {
        Font = _font,
        FontSize = 13,
        Text = text,
        Tag = section,
        MinWidth = section == InspectorSection.OpenFilesAndPorts ? 190 : 105
    };

    private void UpdateContent()
    {
        if (_contentHost is null)
        {
            return;
        }
        InspectorSection section = _selector.SelectedItem?.Tag is InspectorSection value
            ? value
            : InspectorSection.Memory;
        string content = section switch
        {
            InspectorSection.Memory => BuildMemoryText(),
            InspectorSection.Statistics => BuildStatisticsText(),
            InspectorSection.OpenFilesAndPorts => BuildOpenFilesText(),
            _ => string.Empty
        };
        _contentHost.Child = new ScrollViewer
        {
            Content = new TextBlock
            {
                Font = _font,
                FontSize = 12,
                Text = content,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new ThemeResourceBrush("TextPrimary")
            },
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Enabled
        };
    }

    private string BuildMemoryText() =>
        $"Real Memory Size:      {ActivityMetricFormatter.Bytes(_details.Snapshot.MemoryBytes)}\n" +
        $"Virtual Memory Size:  {ActivityMetricFormatter.Bytes(_details.Snapshot.VirtualMemoryBytes)}\n" +
        "Shared Memory Size:  Unavailable\n" +
        "Private Memory Size:  Unavailable";

    private string BuildStatisticsText() =>
        $"Process ID:           {_details.ProcessId}\n" +
        $"Parent Process ID: {_details.ParentProcessId}\n" +
        $"Threads:              {_details.Snapshot.ThreadCount:N0}\n" +
        $"Ports:                  {_details.Snapshot.PortCount:N0}\n" +
        $"CPU Time:            {ActivityMetricFormatter.Duration(_details.Snapshot.CpuTime)}\n" +
        $"Idle Wake-Ups:    {_details.Snapshot.IdleWakeUps:N0}\n" +
        $"Command:            {_details.CommandLine}";

    private string BuildOpenFilesText() =>
        _details.OpenFilesAndPorts.Count == 0
            ? "No open files or ports were reported."
            : string.Join('\n', _details.OpenFilesAndPorts);

    private static string FormatStartTime(DateTimeOffset? startTime) =>
        startTime?.ToLocalTime().ToString("g") ?? "Unavailable";

    private enum InspectorSection
    {
        Memory,
        Statistics,
        OpenFilesAndPorts
    }
}
