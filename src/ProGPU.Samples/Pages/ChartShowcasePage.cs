using Thickness = Microsoft.UI.Xaml.Thickness;
using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Layout;
using ProGPU.Vector;
using ProGPU.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;

using StackPanel = Microsoft.UI.Xaml.Controls.StackPanel;
using Grid = Microsoft.UI.Xaml.Controls.Grid;

namespace ProGPU.Samples
{
    using ProGPU.WinUI.Charts;

    public static class ChartShowcasePage
    {
        private static System.Timers.Timer? _streamingTimer;
        private static List<OHLCDataPoint> _streamingCandles = new();
        private static ChartControl? _candlestickChart;
        private static double _lastStreamingTime = 10.0;
        private static double _lastClose = 100.0;
        private static Random _random = new Random();

        // 1 Million points data cache
        private static List<DataPoint?>? _millionPointsData;
        private static ChartControl? _millionPointsChart;
        private static TextBlock? _millionPointsRenderedReadout;
        private static CheckBox? _millionPointsLttbCheckbox;
        private static CheckBox? _millionPointsBoundsCheckbox;

        // Streaming Dashboard cache
        private static System.Timers.Timer? _dashboardTimer;
        private static List<DataPoint?> _liveSeriesA = new();
        private static List<DataPoint?> _liveSeriesB = new();
        private static double _liveLastTime = 0.0;
        private static ChartControl? _liveDashboardChart;

        // Density Heatmap cache
        private static List<DataPoint?>? _densityScatterData;

        // ==========================================
        // Ultimate Benchmark Cache (Tab 16)
        // ==========================================
        private static ChartControl? _benchmarkChart;
        private static System.Timers.Timer? _benchmarkTimer;
        private static System.Timers.Timer? _benchmarkMetricsTimer;
        private static string _benchmarkDataType = "line";
        private static int _benchmarkPointsCount = 1000000;
        private static int _benchmarkSeriesCount = 1;
        private static int _benchmarkTotalPoints = 0;
        private static int _benchmarkStreamRate = 10000;
        private static int _benchmarkStreamDuration = 100;
        private static int _benchmarkStreamFrames = 0;
        private static int _benchmarkTotalFramesRendered = 0;
        private static double _benchmarkElapsedMs = 0;
        private static bool _benchmarkIsStreaming = false;
        private static int _totalFrameDrops = 0;
        private static int _consecutiveFrameDrops = 0;

        // UI Telemetry Readout text blocks
        private static TextBlock? _benchFpsText;
        private static TextBlock? _benchTimeText;
        private static TextBlock? _benchMemoryText;
        private static TextBlock? _benchDropsText;
        private static TextBlock? _benchFramesText;
        private static TextBlock? _benchElapsedText;
        private static TextBlock? _totalPointsDisplay;
        private static TextBlock? _totalPointsHint;

        // ==========================================
        // Animations & Transitions Cache (Tab 17)
        // ==========================================
        private static ChartControl? _animCartesianChart;
        private static ChartControl? _animPieChart;
        private static int _animStep = 0;
        private static TextBlock? _animStatusText;

        public static FrameworkElement Create()
        {
            var mainGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            mainGrid.RowDefinitions.Add(new GridLength(80, GridUnitType.Absolute));  // Row 0: Header & Info
            mainGrid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));      // Row 1: Pivot Tabbed Workspace

            // 1. PAGE HEADER
            var headerStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(20, 16, 20, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var title = new RichTextBlock { Font = AppState._font, FontSize = 22f, Margin = new Thickness(0, 0, 0, 4) };
            title.Inlines.Add(new Bold(new Run("GPU Charting Dashboard")));
            headerStack.AddChild(title);

            var description = new RichTextBlock { Font = AppState._font, FontSize = 11f, Margin = new Thickness(0, 0, 0, 0) };
            description.Inlines.Add(new Run("Full C# port of the high-performance ChartGPU library. Uses Retina-grade 4-way subpixel snapping, auto-bounds scaling, active crosshairs, glassmorphic tooltips, and interactive slider zooming."));
            headerStack.AddChild(description);

            mainGrid.AddChild(headerStack);
            Grid.SetRow(headerStack, 0);

            // 2. PIVOT TAB CONTAINER
            var pivot = new Pivot
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(20, 4, 20, 20),
                Font = AppState._font
            };

            // Add all 10 ported samples as PivotItems
            pivot.Items.Add(new PivotItem("Basic Line & Area", CreateLineAreaTab()));
            pivot.Items.Add(new PivotItem("Grouped Bar", CreateBarTab()));
            pivot.Items.Add(new PivotItem("Pie & Donut", CreatePieTab()));
            pivot.Items.Add(new PivotItem("Scatter Clusters", CreateScatterTab()));
            pivot.Items.Add(new PivotItem("Candlestick Stream", CreateCandlestickTab()));
            pivot.Items.Add(new PivotItem("1 Million Points", CreateMillionPointsTab()));
            pivot.Items.Add(new PivotItem("Downsampling split", CreateSamplingSplitTab()));
            pivot.Items.Add(new PivotItem("Interactive Zoom", CreateInteractiveZoomTab()));
            pivot.Items.Add(new PivotItem("Custom Annotations", CreateAnnotationsTab()));
            pivot.Items.Add(new PivotItem("Tick Formatter", CreateFormatterTab()));

            // Add the 5 advanced remaining ported samples
            pivot.Items.Add(new PivotItem("Dual Y-Axes", CreateDualYAxesTab()));
            pivot.Items.Add(new PivotItem("Chart Sync", CreateChartSyncTab()));
            pivot.Items.Add(new PivotItem("Live Streaming", CreateLiveStreamTab()));
            pivot.Items.Add(new PivotItem("1M Density Heatmap", CreateScatterDensityHeatmapTab()));
            pivot.Items.Add(new PivotItem("Exchange Gaps", CreateExchangeGapsTab()));

            // Ported missing samples (Phase 5 Completion)
            pivot.Items.Add(new PivotItem("Ultimate Benchmark", CreateUltimateBenchmarkTab()));
            pivot.Items.Add(new PivotItem("Chart Transitions", CreateTransitionsTab()));
            pivot.Items.Add(new PivotItem("Cartesian Formats", CreateCartesianFormatsTab()));

            mainGrid.AddChild(pivot);
            Grid.SetRow(pivot, 1);

            // Handle clean stop of background timers when leaving the page (handled reflectively)
            pivot.SelectionChanged += (s, e) =>
            {
                if (pivot.SelectedIndex != 4)
                {
                    StopStreamingTimer();
                }
                else
                {
                    StartStreamingTimer();
                }

                if (pivot.SelectedIndex != 12)
                {
                    StopLiveDashboardTimer();
                }
                else
                {
                    StartLiveDashboardTimer();
                }

                if (pivot.SelectedIndex != 15)
                {
                    StopBenchmarkTimer();
                    StopBenchmarkMetricsTimer();
                }
            };

            return mainGrid;
        }

        // ==========================================
        // SAMPLE 1: BASIC LINE & AREA (Sine Waves)
        // ==========================================
        private static FrameworkElement CreateLineAreaTab()
        {
            var grid = CreateLayoutGrid("Sine wave phase transitions with subtle area fills & line options.");

            var chart = new ChartControl
            {
                Height = 420f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var dataA = CreateSineWave(300, 0.0, 1.0);
            var dataB = CreateSineWave(300, Math.PI / 3.0, 1.0);
            var dataC = CreateSineWave(300, (2.0 * Math.PI) / 3.0, 1.0);

            chart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 50.0, Right = 20.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig { Type = AxisType.Value, Min = 0.0, Max = Math.PI * 2.0, Name = "Angle (rad)" },
                YAxis = new AxisConfig { Type = AxisType.Value, Min = -1.1, Max = 1.1, Name = "Amplitude" },
                Series = new List<SeriesConfig>
                {
                    new LineSeriesConfig
                    {
                        Name = "sin(x) (Filled)",
                        Data = new CartesianSeriesData(dataA),
                        Color = "#0078D4",
                        AreaStyle = new AreaStyleConfig { Opacity = 0.2 },
                        LineStyle = new LineStyleConfig { Width = 3.0 }
                    },
                    new LineSeriesConfig
                    {
                        Name = "sin(x + π/3)",
                        Data = new CartesianSeriesData(dataB),
                        Color = "#FF4AB0",
                        LineStyle = new LineStyleConfig { Width = 2.0 }
                    },
                    new LineSeriesConfig
                    {
                        Name = "sin(x + 2π/3)",
                        Data = new CartesianSeriesData(dataC),
                        Color = "#40D17C",
                        LineStyle = new LineStyleConfig { Width = 2.0 }
                    }
                },
                Tooltip = new TooltipConfig { Show = true, Trigger = "axis" },
                Legend = new LegendConfig { Show = true }
            };

            grid.AddChild(chart);
            Grid.SetRow(chart, 1);
            return grid;
        }

        // ==========================================
        // SAMPLE 2: GROUPED BAR (Categorical Values)
        // ==========================================
        private static FrameworkElement CreateBarTab()
        {
            var grid = CreateLayoutGrid("Clustered column bars mapped cleanly on Category Scales.");

            var chart = new ChartControl
            {
                Height = 420f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var bar1Points = new List<DataPoint?>
            {
                new DataPoint(0, 42), new DataPoint(1, 68), new DataPoint(2, 53),
                new DataPoint(3, 85), new DataPoint(4, 71), new DataPoint(5, 94)
            };

            var bar2Points = new List<DataPoint?>
            {
                new DataPoint(0, 56), new DataPoint(1, 48), new DataPoint(2, 79),
                new DataPoint(3, 62), new DataPoint(4, 88), new DataPoint(5, 75)
            };

            chart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 50.0, Right = 20.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig
                {
                    Type = AxisType.Category,
                    TickFormatter = (v) =>
                    {
                        string[] cats = { "Jan", "Feb", "Mar", "Apr", "May", "Jun" };
                        int idx = (int)v;
                        return (idx >= 0 && idx < cats.Length) ? cats[idx] : string.Empty;
                    }
                },
                YAxis = new AxisConfig { Type = AxisType.Value, Name = "Usage (Units)" },
                Series = new List<SeriesConfig>
                {
                    new BarSeriesConfig
                    {
                        Name = "Mica Core",
                        Data = new CartesianSeriesData(bar1Points),
                        Color = "#D83B01",
                        BarWidth = "50%"
                    },
                    new BarSeriesConfig
                    {
                        Name = "Acrylic Core",
                        Data = new CartesianSeriesData(bar2Points),
                        Color = "#0078D4",
                        BarWidth = "50%"
                    }
                },
                Tooltip = new TooltipConfig { Show = true, Trigger = "item" },
                Legend = new LegendConfig { Show = true }
            };

            grid.AddChild(chart);
            Grid.SetRow(chart, 1);
            return grid;
        }

        // ==========================================
        // SAMPLE 3: PIE & DONUT (Interactive)
        // ==========================================
        private static FrameworkElement CreatePieTab()
        {
            var grid = CreateLayoutGrid("Beautiful radial slices with donut formatting and hovering tooltip trackers.");

            var chart = new ChartControl
            {
                Height = 420f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var slices = new List<PieDataItem>
            {
                new PieDataItem { Value = 38.0, Name = "Vapor GPU", Color = "#0078D4" },
                new PieDataItem { Value = 27.0, Name = "Substrate Layer", Color = "#107C41" },
                new PieDataItem { Value = 20.0, Name = "Text Layout", Color = "#D83B01" },
                new PieDataItem { Value = 15.0, Name = "GPGPU FX", Color = "#5C2D91" }
            };

            chart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Series = new List<SeriesConfig>
                {
                    new PieSeriesConfig
                    {
                        Name = "Resource Allocation",
                        Radius = ("35%", "70%"),
                        Center = ("50%", "50%"),
                        Data = slices
                    }
                },
                Tooltip = new TooltipConfig { Show = true, Trigger = "item" },
                Legend = new LegendConfig { Show = true }
            };

            grid.AddChild(chart);
            Grid.SetRow(chart, 1);
            return grid;
        }

        // ==========================================
        // SAMPLE 4: SCATTER CLUSTERS
        // ==========================================
        private static FrameworkElement CreateScatterTab()
        {
            var grid = CreateLayoutGrid("Scatter clusters demonstrating fixed sizing, size fields, and dynamic mathematical size formulas.");

            var chart = new ChartControl
            {
                Height = 420f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // Generate deterministic scatter coordinates
            var fixedPoints = GenerateRandomScatter(150, 10, 45, 10, 80, null);
            var perPointPoints = GenerateRandomScatter(150, 55, 90, 10, 80, true);
            var mathPoints = GenerateRandomScatter(80, 20, 80, 50, 90, null);

            chart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 50.0, Right = 20.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig { Type = AxisType.Value, Min = 0.0, Max = 100.0, Name = "X Coordinate" },
                YAxis = new AxisConfig { Type = AxisType.Value, Min = 0.0, Max = 100.0, Name = "Y Coordinate" },
                Series = new List<SeriesConfig>
                {
                    new ScatterSeriesConfig
                    {
                        Name = "Fixed size (4px)",
                        Data = new CartesianSeriesData(fixedPoints),
                        Color = "#0078D4",
                        Symbol = "circle",
                        SymbolSizeConstant = 4.0
                    },
                    new ScatterSeriesConfig
                    {
                        Name = "Per-point size (size field)",
                        Data = new CartesianSeriesData(perPointPoints),
                        Color = "#FF4AB0",
                        Symbol = "rect",
                        SymbolSizeConstant = 3.0 // fallback
                    },
                    new ScatterSeriesConfig
                    {
                        Name = "Math Function size",
                        Data = new CartesianSeriesData(mathPoints),
                        Color = "#40D17C",
                        Symbol = "triangle",
                        SymbolSizeFunction = (pt) => 2.0 + 5.0 * Math.Abs(Math.Sin(pt.X * 0.15))
                    }
                },
                Tooltip = new TooltipConfig { Show = true, Trigger = "item" },
                Legend = new LegendConfig { Show = true }
            };

            grid.AddChild(chart);
            Grid.SetRow(chart, 1);
            return grid;
        }

        // ==========================================
        // SAMPLE 5: REAL-TIME CANDLESTICK STREAM
        // ==========================================
        private static FrameworkElement CreateCandlestickTab()
        {
            var grid = CreateLayoutGrid("Real-time financial stream updating stocks index and auto-fitting vertical bounds cleanly.");

            _streamingCandles.Clear();
            _lastStreamingTime = 10.0;
            _lastClose = 100.0;

            for (int i = 0; i < 15; i++)
            {
                GenerateNextCandle();
            }

            _candlestickChart = new ChartControl
            {
                Height = 400f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            _candlestickChart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 50.0, Right = 20.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig { Type = AxisType.Value, Name = "Timestamp (s)" },
                YAxis = new AxisConfig { Type = AxisType.Value, AutoBounds = "visible", Name = "Index Price ($)" },
                DataZoom = new List<DataZoomConfig>
                {
                    new DataZoomConfig { Type = "slider", Start = 0.0, End = 100.0 }
                },
                Series = new List<SeriesConfig>
                {
                    new CandlestickSeriesConfig
                    {
                        Name = "PRO_GPU Stock Index",
                        Data = _streamingCandles,
                        Style = "classic",
                        BarWidth = 6.0
                    }
                },
                Tooltip = new TooltipConfig { Show = true, Trigger = "item" }
            };

            grid.AddChild(_candlestickChart);
            Grid.SetRow(_candlestickChart, 1);

            StartStreamingTimer();

            return grid;
        }

        // ==========================================
        // SAMPLE 6: 1 MILLION POINTS PERFORMANCE
        // ==========================================
        private static FrameworkElement CreateMillionPointsTab()
        {
            var layout = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            layout.RowDefinitions.Add(new GridLength(60, GridUnitType.Absolute));  // Row 0: Description & controls
            layout.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));      // Row 1: The Chart

            // Setup cache of 1M points
            if (_millionPointsData == null)
            {
                _millionPointsData = GenerateMillionPoints();
            }

            // DESCRIPTION & CONTROLS HEADER BAR
            var controlStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8),
                VerticalAlignment = VerticalAlignment.Center
            };

            var descText = new TextBlock
            {
                Text = "Benchmark: 1,000,000 Cartesian points. Click checkmarks to toggle LTTB downsampling speed.",
                Font = AppState._font,
                FontSize = 11f,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 20, 0)
            };
            controlStack.AddChild(descText);

            _millionPointsLttbCheckbox = new CheckBox
            {
                Content = new TextBlock { Text = "Enable LTTB (8,192 pts)", Font = AppState._font, FontSize = 10f, VerticalAlignment = VerticalAlignment.Center },
                IsChecked = true,
                Margin = new Thickness(0, 0, 16, 0)
            };
            controlStack.AddChild(_millionPointsLttbCheckbox);

            _millionPointsBoundsCheckbox = new CheckBox
            {
                Content = new TextBlock { Text = "Visible Auto-Bounds", Font = AppState._font, FontSize = 10f, VerticalAlignment = VerticalAlignment.Center },
                IsChecked = true,
                Margin = new Thickness(0, 0, 16, 0)
            };
            controlStack.AddChild(_millionPointsBoundsCheckbox);

            var resetBtn = new Button
            {
                Content = new TextBlock { Text = "Reset Zoom", Font = AppState._font, FontSize = 10f, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                WidthConstraint = 80f,
                HeightConstraint = 24f,
                Background = ThemeManager.GetBrush("ControlBackground")
            };
            controlStack.AddChild(resetBtn);

            _millionPointsRenderedReadout = new TextBlock
            {
                Text = "Points: 8,192 rendered",
                Font = AppState._font,
                FontSize = 10f,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 0, 0, 0)
            };
            controlStack.AddChild(_millionPointsRenderedReadout);

            layout.AddChild(controlStack);
            Grid.SetRow(controlStack, 0);

            // CHART CONFIG
            _millionPointsChart = new ChartControl
            {
                Height = 380f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 50.0, Right = 20.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig { Type = AxisType.Value, Min = 0.0, Max = 999999.0, Name = "Index" },
                YAxis = new AxisConfig { Type = AxisType.Value, AutoBounds = "visible", Name = "Value" },
                DataZoom = new List<DataZoomConfig>
                {
                    new DataZoomConfig { Type = "inside" },
                    new DataZoomConfig { Type = "slider", Start = 0.0, End = 100.0 }
                },
                Series = new List<SeriesConfig>
                {
                    new LineSeriesConfig
                    {
                        Name = "1M Wave",
                        Data = new CartesianSeriesData(_millionPointsData),
                        Color = "#0078D4",
                        LineStyle = new LineStyleConfig { Width = 1.0 },
                        Sampling = "lttb",
                        SamplingThreshold = 8192
                    }
                },
                Tooltip = new TooltipConfig { Show = false }
            };

            _millionPointsChart.Options = options;

            // Wire UI Event updates
            _millionPointsLttbCheckbox.CheckedChanged += (s, e) => UpdateMillionPointsSettings();
            _millionPointsBoundsCheckbox.CheckedChanged += (s, e) => UpdateMillionPointsSettings();
            resetBtn.Click += (s, e) =>
            {
                if (_millionPointsChart != null)
                {
                    _millionPointsChart.ResetZoom();
                }
            };

            layout.AddChild(_millionPointsChart);
            Grid.SetRow(_millionPointsChart, 1);

            return layout;
        }

        private static void UpdateMillionPointsSettings()
        {
            if (_millionPointsChart == null || _millionPointsLttbCheckbox == null || _millionPointsBoundsCheckbox == null || _millionPointsRenderedReadout == null) return;

            var opt = _millionPointsChart.Options;
            if (opt != null && opt.Series != null && opt.Series.Count > 0 && opt.Series[0] is LineSeriesConfig ls)
            {
                bool lttb = _millionPointsLttbCheckbox.IsChecked;
                ls.Sampling = lttb ? "lttb" : "none";
                if (opt.YAxis != null)
                {
                    opt.YAxis.AutoBounds = _millionPointsBoundsCheckbox.IsChecked ? "visible" : "global";
                }
                
                _millionPointsRenderedReadout.Text = lttb ? "Points: ~8,192 rendered" : "Points: 1,000,000 rendered";
                
                _millionPointsChart.Invalidate();
            }
        }

        // ==========================================
        // SAMPLE 7: DOWNSAMPLING SIDE-BY-SIDE SPLIT
        // ==========================================
        private static FrameworkElement CreateSamplingSplitTab()
        {
            var layout = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            layout.RowDefinitions.Add(new GridLength(50, GridUnitType.Absolute));  // Row 0: Description
            layout.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));      // Row 1: Split Screen charts

            var descText = new TextBlock
            {
                Text = "Visualizing LTTB Downsampling side-by-side: Left renders 100,000 raw points; Right renders downsampled to 2,000 points.",
                Font = AppState._font,
                FontSize = 11f,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            layout.AddChild(descText);
            Grid.SetRow(descText, 0);

            // Side-by-side grid
            var splitGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            splitGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
            splitGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));

            var data = GenerateHighFreqSpikesData(100000);

            // Chart A: RAW (None)
            var chartA = new ChartControl { Height = 360f, HorizontalAlignment = HorizontalAlignment.Stretch };
            chartA.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 40.0, Right = 10.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig { Type = AxisType.Value, Name = "Raw Index" },
                YAxis = new AxisConfig { Type = AxisType.Value },
                DataZoom = new List<DataZoomConfig> { new DataZoomConfig { Type = "inside" } },
                Series = new List<SeriesConfig>
                {
                    new LineSeriesConfig
                    {
                        Name = "Raw 100k",
                        Data = new CartesianSeriesData(data),
                        Color = "#FF4AB0",
                        LineStyle = new LineStyleConfig { Width = 1.0 },
                        Sampling = "none"
                    }
                },
                Tooltip = new TooltipConfig { Show = false }
            };
            splitGrid.AddChild(chartA);
            Grid.SetColumn(chartA, 0);

            // Chart B: LTTB
            var chartB = new ChartControl { Height = 360f, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(10, 0, 0, 0) };
            chartB.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 40.0, Right = 10.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig { Type = AxisType.Value, Name = "LTTB Index" },
                YAxis = new AxisConfig { Type = AxisType.Value },
                DataZoom = new List<DataZoomConfig> { new DataZoomConfig { Type = "inside" } },
                Series = new List<SeriesConfig>
                {
                    new LineSeriesConfig
                    {
                        Name = "LTTB 2k",
                        Data = new CartesianSeriesData(data),
                        Color = "#0078D4",
                        LineStyle = new LineStyleConfig { Width = 1.0 },
                        Sampling = "lttb",
                        SamplingThreshold = 2000
                    }
                },
                Tooltip = new TooltipConfig { Show = false }
            };
            splitGrid.AddChild(chartB);
            Grid.SetColumn(chartB, 1);

            layout.AddChild(splitGrid);
            Grid.SetRow(splitGrid, 1);

            return layout;
        }

        // ==========================================
        // SAMPLE 8: INTERACTIVE ZOOM & PAN
        // ==========================================
        private static FrameworkElement CreateInteractiveZoomTab()
        {
            var grid = CreateLayoutGrid("Hover to view active crosshairs and tooltips, scroll wheel to zoom, and drag with pointer to pan.");

            var chart = new ChartControl
            {
                Height = 420f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var data = CreateSineWave(400, 0.0, 10.0);

            chart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 50.0, Right = 20.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig { Type = AxisType.Value, Min = 0.0, Max = Math.PI * 2.0, Name = "X Value" },
                YAxis = new AxisConfig { Type = AxisType.Value, Min = -11.0, Max = 11.0, Name = "Y Value" },
                DataZoom = new List<DataZoomConfig>
                {
                    new DataZoomConfig { Type = "inside" },
                    new DataZoomConfig { Type = "slider", Start = 10.0, End = 90.0 }
                },
                Series = new List<SeriesConfig>
                {
                    new LineSeriesConfig
                    {
                        Name = "Wave",
                        Data = new CartesianSeriesData(data),
                        Color = "#40D17C",
                        AreaStyle = new AreaStyleConfig { Opacity = 0.15 }
                    }
                },
                Tooltip = new TooltipConfig { Show = true, Trigger = "axis" },
                Legend = new LegendConfig { Show = true }
            };

            grid.AddChild(chart);
            Grid.SetRow(chart, 1);
            return grid;
        }

        // ==========================================
        // SAMPLE 9: CUSTOM ANNOTATIONS
        // ==========================================
        private static FrameworkElement CreateAnnotationsTab()
        {
            var grid = CreateLayoutGrid("Highlight critical limits with custom horizontal, vertical, and point target annotations.");

            var chart = new ChartControl
            {
                Height = 420f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var points = new List<DataPoint?>
            {
                new DataPoint(0, 10), new DataPoint(1, 25), new DataPoint(2, 18),
                new DataPoint(3, 45), new DataPoint(4, 30), new DataPoint(5, 55),
                new DataPoint(6, 40), new DataPoint(7, 72), new DataPoint(8, 60)
            };

            chart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 50.0, Right = 20.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig { Type = AxisType.Value, Name = "Timeline (Days)" },
                YAxis = new AxisConfig { Type = AxisType.Value, Name = "Load (%)" },
                Series = new List<SeriesConfig>
                {
                    new LineSeriesConfig
                    {
                        Name = "Performance",
                        Data = new CartesianSeriesData(points),
                        Color = "#0078D4",
                        LineStyle = new LineStyleConfig { Width = 2.0 }
                    }
                },
                Annotations = new List<AnnotationConfig>
                {
                    new AnnotationLineX
                    {
                        X = 4.0,
                        Style = new AnnotationStyle { Color = "#FF4AB0", LineDash = new double[] { 4.0, 4.0 } },
                        Label = new AnnotationLabel { Text = "Maintenance Window", Anchor = "end" }
                    },
                    new AnnotationLineY
                    {
                        Y = 50.0,
                        Style = new AnnotationStyle { Color = "#E54B4B", LineWidth = 2.0 },
                        Label = new AnnotationLabel { Text = "Critical Threshold (50%)", Anchor = "end" }
                    },
                    new AnnotationPoint
                    {
                        X = 7.0,
                        Y = 72.0,
                        Marker = new AnnotationPointMarker { Symbol = "circle", Size = 8.0, Style = new AnnotationStyle { Color = "#40D17C" } },
                        Label = new AnnotationLabel { Text = "Max Peak", Anchor = "center" }
                    }
                },
                Tooltip = new TooltipConfig { Show = true, Trigger = "axis" },
                Legend = new LegendConfig { Show = true }
            };

            grid.AddChild(chart);
            Grid.SetRow(chart, 1);
            return grid;
        }

        // ==========================================
        // SAMPLE 10: CUSTOM TICK FORMATTER
        // ==========================================
        private static FrameworkElement CreateFormatterTab()
        {
            var grid = CreateLayoutGrid("Demonstrates customized axis formatting rules for dates and currencies.");

            var chart = new ChartControl
            {
                Height = 420f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // Setup linear indexes which we format as dates
            var points = new List<DataPoint?>
            {
                new DataPoint(0, 1200), new DataPoint(1, 1500), new DataPoint(2, 1350),
                new DataPoint(3, 1900), new DataPoint(4, 1850), new DataPoint(5, 2300)
            };

            chart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 70.0, Right = 20.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig
                {
                    Type = AxisType.Value,
                    TickFormatter = (v) =>
                    {
                        string[] dates = { "05/01", "05/02", "05/03", "05/04", "05/05", "05/06" };
                        int idx = (int)v;
                        return (idx >= 0 && idx < dates.Length) ? dates[idx] : string.Empty;
                    },
                    Name = "Date"
                },
                YAxis = new AxisConfig
                {
                    Type = AxisType.Value,
                    TickFormatter = (v) => $"${v:N0}",
                    Name = "Revenue"
                },
                Series = new List<SeriesConfig>
                {
                    new AreaSeriesConfig
                    {
                        Name = "Daily Sales",
                        Data = new CartesianSeriesData(points),
                        Color = "#D83B01",
                        AreaStyle = new AreaStyleConfig { Opacity = 0.2 }
                    }
                },
                Tooltip = new TooltipConfig { Show = true, Trigger = "axis" },
                Legend = new LegendConfig { Show = true }
            };

            grid.AddChild(chart);
            Grid.SetRow(chart, 1);
            return grid;
        }

        // ==========================================
        // DATA & MATH GENERATOR HELPERS
        // ==========================================
        private static Grid CreateLayoutGrid(string subtitle)
        {
            var grid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            grid.RowDefinitions.Add(new GridLength(35, GridUnitType.Absolute)); // Row 0: Subtitle
            grid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));     // Row 1: Content

            var subtitleText = new TextBlock
            {
                Text = subtitle,
                Font = AppState._font,
                FontSize = 11f,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(0xFFFFFF90)
            };
            grid.AddChild(subtitleText);
            Grid.SetRow(subtitleText, 0);

            return grid;
        }

        private static List<DataPoint?> CreateSineWave(int count, double phase, double amplitude)
        {
            var list = new List<DataPoint?>(count);
            for (int i = 0; i < count; i++)
            {
                double t = (double)i / (count - 1);
                double x = t * Math.PI * 2.0;
                double y = Math.Sin(x + phase) * amplitude;
                list.Add(new DataPoint(x, y));
            }
            return list;
        }

        private static List<DataPoint?> GenerateRandomScatter(int count, double xMin, double xMax, double yMin, double yMax, bool? includeSize)
        {
            var list = new List<DataPoint?>(count);
            double xSpan = xMax - xMin;
            double ySpan = yMax - yMin;

            for (int i = 0; i < count; i++)
            {
                double x = xMin + _random.NextDouble() * xSpan;
                double y = yMin + _random.NextDouble() * ySpan;
                double? size = includeSize.HasValue && includeSize.Value ? (double?)(2.0 + _random.NextDouble() * 6.5) : null;
                list.Add(new DataPoint(x, y, size));
            }

            // Sort scatter list by x coordinate for optimal nearest index lookup
            list.Sort((a, b) =>
            {
                if (!a.HasValue && !b.HasValue) return 0;
                if (!a.HasValue) return -1;
                if (!b.HasValue) return 1;
                return a.Value.X.CompareTo(b.Value.X);
            });
            return list;
        }

        private static List<DataPoint?> GenerateMillionPoints()
        {
            int count = 1000000;
            var list = new List<DataPoint?>(count);

            double freq = 0.012;
            double lowFreq = 0.0017;
            double noiseAmp = 0.35;

            // Highly optimized deterministic PRNG to keep execution fast during creation
            uint seed = 0x12345678;
            double Rand01()
            {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                return (double)seed / uint.MaxValue;
            }

            for (int i = 0; i < count; i++)
            {
                double y = Math.Sin(i * freq) * 0.95 +
                           Math.Sin(i * lowFreq + 1.1) * 0.6 +
                           (Rand01() - 0.5) * noiseAmp;
                list.Add(new DataPoint(i, y));
            }

            return list;
        }

        private static List<DataPoint?> GenerateHighFreqSpikesData(int count)
        {
            var list = new List<DataPoint?>(count);
            int[] spikes = { 12500, 31000, 48000, 66500, 84250 };

            for (int i = 0; i < count; i++)
            {
                double slow = Math.Sin(i * 0.0014) * 1.2 + Math.Sin(i * 0.00017 + 1.1) * 0.6;
                double hf = Math.Sin(i * 0.085) * 0.25 + Math.Sin(i * 0.17 + 0.4) * 0.12;
                double spike = 0.0;
                foreach (int sp in spikes)
                {
                    double d = i - sp;
                    spike += 6.5 * Math.Exp(-(d * d) / (2.0 * 34.0 * 34.0));
                }
                list.Add(new DataPoint(i, slow + hf + spike));
            }
            return list;
        }

        private static void StartStreamingTimer()
        {
            if (_streamingTimer != null) return;

            _streamingTimer = new System.Timers.Timer(1000.0);
            _streamingTimer.Elapsed += (s, e) =>
            {
                UIThread.Post(() =>
                {
                    if (_candlestickChart == null) return;

                    GenerateNextCandle();

                    if (_streamingCandles.Count > 30)
                    {
                        _streamingCandles.RemoveAt(0);
                    }

                    _candlestickChart.Invalidate();
                });
            };
            _streamingTimer.AutoReset = true;
            _streamingTimer.Start();
        }

        private static void StopStreamingTimer()
        {
            if (_streamingTimer != null)
            {
                _streamingTimer.Stop();
                _streamingTimer.Dispose();
                _streamingTimer = null;
            }
        }

        private static void GenerateNextCandle()
        {
            double open = _lastClose;
            double change = (_random.NextDouble() - 0.48) * 8.0;
            double close = open + change;
            double low = Math.Min(open, close) - _random.NextDouble() * 3.0;
            double high = Math.Max(open, close) + _random.NextDouble() * 3.0;

            _streamingCandles.Add(new OHLCDataPoint(_lastStreamingTime, open, close, low, high));
            _lastClose = close;
            _lastStreamingTime += 1.0;
        }

        // ==========================================
        // SAMPLE 11: DUAL Y-AXES
        // ==========================================
        private static FrameworkElement CreateDualYAxesTab()
        {
            var grid = CreateLayoutGrid("Temperature (°C, Left Y-Axis) vs Humidity & Wind (%, Right Y-Axis)");

            var chart = new ChartControl
            {
                Height = 420f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var tempData = new List<DataPoint?>
            {
                new DataPoint(0, 15), new DataPoint(1, 17), new DataPoint(2, 22),
                new DataPoint(3, 26), new DataPoint(4, 24), new DataPoint(5, 19), new DataPoint(6, 16)
            };

            var humidityData = new List<DataPoint?>
            {
                new DataPoint(0, 60), new DataPoint(1, 55), new DataPoint(2, 45),
                new DataPoint(3, 40), new DataPoint(4, 50), new DataPoint(5, 65), new DataPoint(6, 70)
            };

            chart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 60.0, Right = 60.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig
                {
                    Type = AxisType.Category,
                    TickFormatter = (v) =>
                    {
                        string[] days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
                        int idx = (int)v;
                        return (idx >= 0 && idx < days.Length) ? days[idx] : string.Empty;
                    }
                },
                YAxes = new List<AxisConfig>
                {
                    new AxisConfig { Type = AxisType.Value, Name = "Temp (°C)", AutoBounds = "visible" },
                    new AxisConfig { Type = AxisType.Value, Name = "Humidity (%)", AutoBounds = "visible" }
                },
                Series = new List<SeriesConfig>
                {
                    new LineSeriesConfig
                    {
                        Name = "Temperature",
                        Data = new CartesianSeriesData(tempData),
                        Color = "#FF4AB0",
                        YAxis = "y1",
                        LineStyle = new LineStyleConfig { Width = 3.0 }
                    },
                    new LineSeriesConfig
                    {
                        Name = "Humidity",
                        Data = new CartesianSeriesData(humidityData),
                        Color = "#0078D4",
                        YAxis = "y2",
                        LineStyle = new LineStyleConfig { Width = 3.0 }
                    }
                },
                Tooltip = new TooltipConfig { Show = true, Trigger = "axis" },
                Legend = new LegendConfig { Show = true }
            };

            grid.AddChild(chart);
            Grid.SetRow(chart, 1);
            return grid;
        }

        // ==========================================
        // SAMPLE 12: CHART SYNC
        // ==========================================
        private static FrameworkElement CreateChartSyncTab()
        {
            var mainGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            mainGrid.RowDefinitions.Add(new GridLength(35, GridUnitType.Absolute)); // Row 0: Subtitle
            mainGrid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));     // Row 1: Chart 1
            mainGrid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));     // Row 2: Chart 2

            var subtitleText = new TextBlock
            {
                Text = "Linked zoom, pan, and crosshair sync across multiple charts.",
                Font = AppState._font,
                FontSize = 11f,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(0xFFFFFF90)
            };
            mainGrid.AddChild(subtitleText);
            Grid.SetRow(subtitleText, 0);

            var chart1 = new ChartControl { Height = 200f, HorizontalAlignment = HorizontalAlignment.Stretch };
            var chart2 = new ChartControl { Height = 200f, HorizontalAlignment = HorizontalAlignment.Stretch };

            var data1 = CreateSineWave(200, 0.0, 1.0);
            var data2 = CreateSineWave(200, Math.PI / 2.0, 1.5);

            var commonZoom1 = new DataZoomConfig { Type = "slider", Start = 0.0, End = 100.0 };
            var commonZoom2 = new DataZoomConfig { Type = "slider", Start = 0.0, End = 100.0 };

            chart1.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 50.0, Right = 20.0, Top = 10.0, Bottom = 35.0 },
                XAxis = new AxisConfig { Type = AxisType.Value, Min = 0.0, Max = Math.PI * 2.0 },
                YAxis = new AxisConfig { Type = AxisType.Value, Min = -1.1, Max = 1.1, Name = "Signal A" },
                Series = new List<SeriesConfig>
                {
                    new LineSeriesConfig
                    {
                        Name = "Signal A",
                        Data = new CartesianSeriesData(data1),
                        Color = "#0078D4",
                        LineStyle = new LineStyleConfig { Width = 2.0 }
                    }
                },
                DataZoom = new List<DataZoomConfig> { commonZoom1 },
                Tooltip = new TooltipConfig { Show = true, Trigger = "axis" },
                Legend = new LegendConfig { Show = false }
            };

            chart2.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 50.0, Right = 20.0, Top = 10.0, Bottom = 35.0 },
                XAxis = new AxisConfig { Type = AxisType.Value, Min = 0.0, Max = Math.PI * 2.0 },
                YAxis = new AxisConfig { Type = AxisType.Value, Min = -1.6, Max = 1.6, Name = "Signal B" },
                Series = new List<SeriesConfig>
                {
                    new LineSeriesConfig
                    {
                        Name = "Signal B",
                        Data = new CartesianSeriesData(data2),
                        Color = "#FF4AB0",
                        LineStyle = new LineStyleConfig { Width = 2.0 }
                    }
                },
                DataZoom = new List<DataZoomConfig> { commonZoom2 },
                Tooltip = new TooltipConfig { Show = true, Trigger = "axis" },
                Legend = new LegendConfig { Show = false }
            };

            // Set up real-time cross-sync events
            bool isSyncing = false;
            var token1 = new object();
            var token2 = new object();

            chart1.ZoomRangeChanged += (s, e) =>
            {
                if (isSyncing || e.SourceToken == token2) return;
                isSyncing = true;
                chart2.SetZoomRange(e.Start, e.End, token1);
                isSyncing = false;
            };

            chart2.ZoomRangeChanged += (s, e) =>
            {
                if (isSyncing || e.SourceToken == token1) return;
                isSyncing = true;
                chart1.SetZoomRange(e.Start, e.End, token2);
                isSyncing = false;
            };

            chart1.CrosshairMoved += (s, e) =>
            {
                if (isSyncing || e.SourceToken == token2) return;
                isSyncing = true;
                chart2.SetCrosshairX(e.X, token1);
                isSyncing = false;
            };

            chart2.CrosshairMoved += (s, e) =>
            {
                if (isSyncing || e.SourceToken == token1) return;
                isSyncing = true;
                chart1.SetCrosshairX(e.X, token2);
                isSyncing = false;
            };

            mainGrid.AddChild(chart1);
            Grid.SetRow(chart1, 1);

            mainGrid.AddChild(chart2);
            Grid.SetRow(chart2, 2);

            return mainGrid;
        }

        // ==========================================
        // SAMPLE 13: LIVE STREAMING DASHBOARD
        // ==========================================
        private static FrameworkElement CreateLiveStreamTab()
        {
            var grid = CreateLayoutGrid("Real-time live streaming of multi-series metrics with a 500ms update rate.");

            _liveDashboardChart = new ChartControl
            {
                Height = 420f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            _liveSeriesA.Clear();
            _liveSeriesB.Clear();
            for (int i = 0; i < 30; i++)
            {
                double t = i;
                _liveSeriesA.Add(new DataPoint(t, Math.Sin(t * 0.2) * 50.0 + 100.0));
                _liveSeriesB.Add(new DataPoint(t, Math.Cos(t * 0.35) * 30.0 + 80.0));
            }
            _liveLastTime = 29.0;

            _liveDashboardChart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 50.0, Right = 20.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig { Type = AxisType.Value, Name = "Time (seconds)" },
                YAxis = new AxisConfig { Type = AxisType.Value, AutoBounds = "visible", Name = "CPU / Memory Load" },
                Series = new List<SeriesConfig>
                {
                    new LineSeriesConfig
                    {
                        Name = "Core A Usage",
                        Data = new CartesianSeriesData(_liveSeriesA),
                        Color = "#0078D4",
                        LineStyle = new LineStyleConfig { Width = 3.0 }
                    },
                    new LineSeriesConfig
                    {
                        Name = "Core B Usage",
                        Data = new CartesianSeriesData(_liveSeriesB),
                        Color = "#FF4AB0",
                        LineStyle = new LineStyleConfig { Width = 2.0 }
                    }
                },
                Tooltip = new TooltipConfig { Show = true, Trigger = "axis" },
                Legend = new LegendConfig { Show = true }
            };

            grid.AddChild(_liveDashboardChart);
            Grid.SetRow(_liveDashboardChart, 1);

            return grid;
        }

        private static void StartLiveDashboardTimer()
        {
            if (_dashboardTimer != null) return;

            _dashboardTimer = new System.Timers.Timer(500.0);
            _dashboardTimer.Elapsed += (s, e) =>
            {
                UIThread.Post(() =>
                {
                    if (_liveDashboardChart == null) return;

                    _liveLastTime += 1.0;
                    double valA = Math.Sin(_liveLastTime * 0.2) * 50.0 + 100.0 + (_random.NextDouble() - 0.5) * 15.0;
                    double valB = Math.Cos(_liveLastTime * 0.35) * 30.0 + 80.0 + (_random.NextDouble() - 0.5) * 10.0;

                    _liveSeriesA.Add(new DataPoint(_liveLastTime, valA));
                    _liveSeriesB.Add(new DataPoint(_liveLastTime, valB));

                    if (_liveSeriesA.Count > 50)
                    {
                        _liveSeriesA.RemoveAt(0);
                        _liveSeriesB.RemoveAt(0);
                    }

                    _liveDashboardChart.Invalidate();
                });
            };
            _dashboardTimer.AutoReset = true;
            _dashboardTimer.Start();
        }

        private static void StopLiveDashboardTimer()
        {
            if (_dashboardTimer != null)
            {
                _dashboardTimer.Stop();
                _dashboardTimer.Dispose();
                _dashboardTimer = null;
            }
        }

        // ==========================================
        // SAMPLE 14: 1M SCATTER DENSITY HEATMAP
        // ==========================================
        private static FrameworkElement CreateScatterDensityHeatmapTab()
        {
            var grid = CreateLayoutGrid("Gaussian distribution density heatmap showing 50,000 binned points.");

            var chart = new ChartControl
            {
                Height = 420f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            if (_densityScatterData == null)
            {
                _densityScatterData = GenerateDensityScatter(50000);
            }

            chart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 50.0, Right = 20.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig { Type = AxisType.Value, Min = 0.0, Max = 100.0, Name = "X Coordinate" },
                YAxis = new AxisConfig { Type = AxisType.Value, Min = 0.0, Max = 100.0, Name = "Y Coordinate" },
                Series = new List<SeriesConfig>
                {
                    new ScatterSeriesConfig
                    {
                        Name = "Density Map",
                        Data = new CartesianSeriesData(_densityScatterData),
                        Color = "#40D17C",
                        Mode = "density",
                        BinSize = 5.0,
                        DensityNormalization = "log"
                    }
                },
                Tooltip = new TooltipConfig { Show = true, Trigger = "axis" },
                Legend = new LegendConfig { Show = true }
            };

            grid.AddChild(chart);
            Grid.SetRow(chart, 1);
            return grid;
        }

        private static List<DataPoint?> GenerateDensityScatter(int count)
        {
            var list = new List<DataPoint?>(count);
            for (int i = 0; i < count; i++)
            {
                double u1 = 1.0 - _random.NextDouble();
                double u2 = 1.0 - _random.NextDouble();
                double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);

                if (_random.NextDouble() < 0.455)
                {
                    double x = 40.0 + randStdNormal * 10.0;
                    double y = 40.0 + Math.Sqrt(-2.0 * Math.Log(1.0 - _random.NextDouble())) * Math.Sin(2.0 * Math.PI * (1.0 - _random.NextDouble())) * 10.0;
                    list.Add(new DataPoint(x, y));
                }
                else
                {
                    double x = 65.0 + randStdNormal * 15.0;
                    double y = 65.0 + Math.Sqrt(-2.0 * Math.Log(1.0 - _random.NextDouble())) * Math.Sin(2.0 * Math.PI * (1.0 - _random.NextDouble())) * 15.0;
                    list.Add(new DataPoint(x, y));
                }
            }
            return list;
        }

        // ==========================================
        // SAMPLE 15: EXCHANGE GAPS
        // ==========================================
        private static FrameworkElement CreateExchangeGapsTab()
        {
            var grid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            grid.RowDefinitions.Add(new GridLength(35, GridUnitType.Absolute)); // Row 0: Subtitle
            grid.RowDefinitions.Add(new GridLength(45, GridUnitType.Absolute)); // Row 1: Controls
            grid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));     // Row 2: Chart

            var subtitleText = new TextBlock
            {
                Text = "Demonstrating how line/area series handle null data values (gaps).",
                Font = AppState._font,
                FontSize = 11f,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8),
                Foreground = new SolidColorBrush(0xFFFFFF90)
            };
            grid.AddChild(subtitleText);
            Grid.SetRow(subtitleText, 0);

            var controlsStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var connectCheckbox = new CheckBox
            {
                Content = "Connect Nulls (Bridge Gaps)",
                IsChecked = false,
                Font = AppState._font,
                Margin = new Thickness(0, 0, 16, 0)
            };
            controlsStack.AddChild(connectCheckbox);

            grid.AddChild(controlsStack);
            Grid.SetRow(controlsStack, 1);

            var chart = new ChartControl
            {
                Height = 360f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var gapPoints = new List<DataPoint?>
            {
                new DataPoint(0, 100), new DataPoint(1, 105), new DataPoint(2, 98),
                new DataPoint(3, 102), new DataPoint(4, 110),
                null,
                null,
                new DataPoint(7, 115), new DataPoint(8, 120), new DataPoint(9, 118),
                new DataPoint(10, 125),
                null,
                new DataPoint(12, 130), new DataPoint(13, 135), new DataPoint(14, 132)
            };

            var lineSeries = new LineSeriesConfig
            {
                Name = "Asset Price",
                Data = new CartesianSeriesData(gapPoints),
                Color = "#0078D4",
                ConnectNulls = false,
                AreaStyle = new AreaStyleConfig { Opacity = 0.25 },
                LineStyle = new LineStyleConfig { Width = 3.0 }
            };

            chart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 50.0, Right = 20.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig
                {
                    Type = AxisType.Value,
                    Min = 0,
                    Max = 14,
                    TickFormatter = (v) => $"Day {v:F0}"
                },
                YAxis = new AxisConfig
                {
                    Type = AxisType.Value,
                    Min = 80,
                    Max = 150,
                    TickFormatter = (v) => $"${v:F0}"
                },
                Series = new List<SeriesConfig> { lineSeries },
                Tooltip = new TooltipConfig { Show = true, Trigger = "axis" },
                Legend = new LegendConfig { Show = true }
            };

            connectCheckbox.Checked += (s, e) =>
            {
                lineSeries.ConnectNulls = true;
                chart.Invalidate();
            };

            connectCheckbox.Unchecked += (s, e) =>
            {
                lineSeries.ConnectNulls = false;
                chart.Invalidate();
            };

            grid.AddChild(chart);
            Grid.SetRow(chart, 2);
            return grid;
        }

        // ==========================================
        // SAMPLE 16: ULTIMATE BENCHMARK (Stress Cockpit)
        // ==========================================
        private static string FormatAbbreviatedNumber(double n)
        {
            if (n < 1000) return ((int)n).ToString();
            if (n < 1000000) return $"{(n / 1000.0):F1}K";
            if (n < 1000000000) return $"{(n / 1000000.0):F1}M";
            return $"{(n / 1000000000.0):F2}B";
        }

        private static void UpdateTotalPointsDisplay(TextBox ptCountInput, TextBox serCountInput)
        {
            if (_totalPointsDisplay == null || _totalPointsHint == null) return;
            int.TryParse(ptCountInput.Text, out int ptCount);
            int.TryParse(serCountInput.Text, out int serCount);
            long total = (long)ptCount * serCount;
            if (total < 0) total = 0;
            
            _totalPointsDisplay.Text = total.ToString("N0");
            _totalPointsHint.Text = $"{FormatAbbreviatedNumber(total)} points";

            if (total > 1000000000) _totalPointsDisplay.Foreground = new SolidColorBrush(0xFF3333FF); // red/warning
            else if (total > 100000000) _totalPointsDisplay.Foreground = new SolidColorBrush(0xFF8822FF); // orange/warning
            else _totalPointsDisplay.Foreground = new SolidColorBrush(0x00E5FFFF); // cyan/smooth
        }

        private static FrameworkElement CreateUltimateBenchmarkTab()
        {
            var mainGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            mainGrid.ColumnDefinitions.Add(new GridLength(280, GridUnitType.Absolute)); // Sidebar
            mainGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));     // Showcase Area

            // -------------------------------------------------------------
            // SIDEBAR (COLUMN 0)
            // -------------------------------------------------------------
            var sidebarBorder = new Border
            {
                Background = new SolidColorBrush(0x1F1F2F80),
                BorderBrush = new SolidColorBrush(0x3F3F4F80),
                BorderThickness = new Thickness(1),
                CornerRadius = 6f,
                Padding = new Thickness(16),
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var sidebarStack = new StackPanel { Orientation = Orientation.Vertical };

            var configHeader = new TextBlock
            {
                Text = "Configuration",
                Font = AppState._font,
                FontSize = 13f,
                Foreground = new SolidColorBrush(0xFFFFFFFF),
                Margin = new Thickness(0, 0, 0, 16)
            };
            sidebarStack.AddChild(configHeader);

            // Point Count
            var ptLabel = new TextBlock { Text = "Point Count (per series)", Font = AppState._font, FontSize = 10f, Foreground = new SolidColorBrush(0x999999FF), Margin = new Thickness(0, 0, 0, 4) };
            var pointCountInput = new TextBox { Text = "1000000", Font = AppState._font, Width = 230f, Height = 32f, Margin = new Thickness(0, 0, 0, 12) };
            sidebarStack.AddChild(ptLabel);
            sidebarStack.AddChild(pointCountInput);

            // Series Count
            var serLabel = new TextBlock { Text = "Series Count", Font = AppState._font, FontSize = 10f, Foreground = new SolidColorBrush(0x999999FF), Margin = new Thickness(0, 0, 0, 4) };
            var seriesCountInput = new TextBox { Text = "1", Font = AppState._font, Width = 230f, Height = 32f, Margin = new Thickness(0, 0, 0, 12) };
            sidebarStack.AddChild(serLabel);
            sidebarStack.AddChild(seriesCountInput);

            // Total Planned Points Preview Card
            var previewBorder = new Border
            {
                Background = new SolidColorBrush(0x25253560),
                BorderBrush = new SolidColorBrush(0x40405060),
                BorderThickness = new Thickness(1),
                CornerRadius = 4f,
                Padding = new Thickness(12),
                Margin = new Thickness(0, 4, 0, 16)
            };
            var previewStack = new StackPanel { Orientation = Orientation.Vertical };
            var previewLabel = new TextBlock { Text = "Total Rendered Points", Font = AppState._font, FontSize = 9f, Foreground = new SolidColorBrush(0xAAAAAAFF) };
            _totalPointsDisplay = new TextBlock { Text = "1,000,000", Font = AppState._font, FontSize = 18f, Foreground = new SolidColorBrush(0x00E5FFFF), Margin = new Thickness(0, 2, 0, 2) };
            _totalPointsHint = new TextBlock { Text = "1.0M points", Font = AppState._font, FontSize = 9f, Foreground = new SolidColorBrush(0x888888FF) };
            previewStack.AddChild(previewLabel);
            previewStack.AddChild(_totalPointsDisplay);
            previewStack.AddChild(_totalPointsHint);
            previewBorder.Child = previewStack;
            sidebarStack.AddChild(previewBorder);

            // Data Type
            var typeLabel = new TextBlock { Text = "Data Type", Font = AppState._font, FontSize = 10f, Foreground = new SolidColorBrush(0x999999FF), Margin = new Thickness(0, 0, 0, 4) };
            var typeCombo = new ComboBox { Font = AppState._font, Width = 230f, Margin = new Thickness(0, 0, 0, 20) };
            var itemLine = new ComboBoxItem("Line");
            var itemScatter = new ComboBoxItem("Scatter");
            var itemBar = new ComboBoxItem("Bar");
            var itemCandle = new ComboBoxItem("Candlestick");
            typeCombo.Items.Add(itemLine);
            typeCombo.Items.Add(itemScatter);
            typeCombo.Items.Add(itemBar);
            typeCombo.Items.Add(itemCandle);
            typeCombo.SelectedItem = itemLine;
            sidebarStack.AddChild(typeLabel);
            sidebarStack.AddChild(typeCombo);

            // Streaming Settings Header
            var streamHeader = new TextBlock
            {
                Text = "Streaming Settings",
                Font = AppState._font,
                FontSize = 12f,
                Foreground = new SolidColorBrush(0xFFFFFFFF),
                Margin = new Thickness(0, 0, 0, 12)
            };
            sidebarStack.AddChild(streamHeader);

            // Points per Frame
            var rateLabel = new TextBlock { Text = "Points per Frame (series 0)", Font = AppState._font, FontSize = 10f, Foreground = new SolidColorBrush(0x999999FF), Margin = new Thickness(0, 0, 0, 4) };
            var streamRateInput = new TextBox { Text = "10000", Font = AppState._font, Width = 230f, Height = 32f, Margin = new Thickness(0, 0, 0, 12) };
            sidebarStack.AddChild(rateLabel);
            sidebarStack.AddChild(streamRateInput);

            // Stream Duration
            var durLabel = new TextBlock { Text = "Duration (frames)", Font = AppState._font, FontSize = 10f, Foreground = new SolidColorBrush(0x999999FF), Margin = new Thickness(0, 0, 0, 4) };
            var streamDurationInput = new TextBox { Text = "100", Font = AppState._font, Width = 230f, Height = 32f, Margin = new Thickness(0, 0, 0, 20) };
            sidebarStack.AddChild(durLabel);
            sidebarStack.AddChild(streamDurationInput);

            // Action Buttons
            var btnGenerate = new Button { Content = "Generate Data", Font = AppState._font, Height = 28f, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
            var btnStream = new ToggleButton { Content = "Start Streaming", Font = AppState._font, Height = 28f, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
            var btnClear = new Button { Content = "Clear", Font = AppState._font, Height = 28f, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
            var btnEmergency = new Button
            {
                Content = "Emergency Stop",
                Font = AppState._font,
                Height = 28f,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Foreground = new SolidColorBrush(0xFFFFFFFF),
                Background = new SolidColorBrush(0xAA1111FF),
                BorderBrush = new SolidColorBrush(0xFF3333FF)
            };
            sidebarStack.AddChild(btnGenerate);
            sidebarStack.AddChild(btnStream);
            sidebarStack.AddChild(btnClear);
            sidebarStack.AddChild(btnEmergency);

            sidebarBorder.Child = sidebarStack;
            mainGrid.AddChild(sidebarBorder);
            Grid.SetColumn(sidebarBorder, 0);

            // Text change preview events
            pointCountInput.TextChanged += (s, e) => UpdateTotalPointsDisplay(pointCountInput, seriesCountInput);
            seriesCountInput.TextChanged += (s, e) => UpdateTotalPointsDisplay(pointCountInput, seriesCountInput);

            // -------------------------------------------------------------
            // SHOWCASE / MAIN (COLUMN 1)
            // -------------------------------------------------------------
            var showcaseGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            showcaseGrid.RowDefinitions.Add(new GridLength(45, GridUnitType.Absolute)); // Subtitle
            showcaseGrid.RowDefinitions.Add(new GridLength(75, GridUnitType.Absolute)); // Telemetry Readout Cards
            showcaseGrid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));     // Chart canvas

            var titleStack = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 8) };
            var showcaseTitle = new TextBlock
            {
                Text = "GPU Charting Benchmark Cockpit",
                Font = AppState._font,
                FontSize = 15f,
                Foreground = new SolidColorBrush(0xFFFFFFFF)
            };
            var showcaseSubtitle = new TextBlock
            {
                Text = "Hardcore stress testing rendered in real-time on the main thread via raw WebGPU shaders.",
                Font = AppState._font,
                FontSize = 10f,
                Foreground = new SolidColorBrush(0x999999FF)
            };
            titleStack.AddChild(showcaseTitle);
            titleStack.AddChild(showcaseSubtitle);
            showcaseGrid.AddChild(titleStack);
            Grid.SetRow(titleStack, 0);

            // Telemetry Cards (6 columns)
            var metricsGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            for (int i = 0; i < 6; i++)
            {
                metricsGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
            }

            _benchFpsText = CreateMetricCard(metricsGrid, "FPS", "--", "60+ = smooth", 0);
            _benchTimeText = CreateMetricCard(metricsGrid, "Frame Time", "-- ms", "Min/Avg/Max/P95/P99", 1);
            _benchMemoryText = CreateMetricCard(metricsGrid, "Memory", "-- MB", "Used / Peak", 2);
            _benchDropsText = CreateMetricCard(metricsGrid, "Frame Drops", "0 / 0", "Total / Consecutive", 3);
            _benchFramesText = CreateMetricCard(metricsGrid, "Total Frames", "0", "Since start", 4);
            _benchElapsedText = CreateMetricCard(metricsGrid, "Elapsed", "0s", "Running time", 5);

            showcaseGrid.AddChild(metricsGrid);
            Grid.SetRow(metricsGrid, 1);

            _benchmarkChart = new ChartControl
            {
                Height = 300f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            showcaseGrid.AddChild(_benchmarkChart);
            Grid.SetRow(_benchmarkChart, 2);

            mainGrid.AddChild(showcaseGrid);
            Grid.SetColumn(showcaseGrid, 1);

            // Hook actions
            btnGenerate.Click += (s, e) =>
            {
                int.TryParse(pointCountInput.Text, out _benchmarkPointsCount);
                int.TryParse(seriesCountInput.Text, out _benchmarkSeriesCount);
                _benchmarkDataType =
                    (typeCombo.SelectedItem as ComboBoxItem)?.Text.ToLowerInvariant() ?? "line";
                _totalFrameDrops = 0;
                _consecutiveFrameDrops = 0;

                RunBenchmarkGeneration();
            };

            btnStream.Checked += (s, e) =>
            {
                btnStream.Content = "Stop Streaming";
                int.TryParse(streamRateInput.Text, out _benchmarkStreamRate);
                int.TryParse(streamDurationInput.Text, out _benchmarkStreamDuration);
                _benchmarkDataType =
                    (typeCombo.SelectedItem as ComboBoxItem)?.Text.ToLowerInvariant() ?? "line";
                StartBenchmarkStreaming();
            };

            btnStream.Unchecked += (s, e) =>
            {
                btnStream.Content = "Start Streaming";
                StopBenchmarkTimer();
            };

            var clearHandler = new Action(() =>
            {
                btnStream.IsChecked = false;
                StopBenchmarkTimer();
                StopBenchmarkMetricsTimer();
                _benchmarkTotalPoints = 0;
                _benchmarkTotalFramesRendered = 0;
                _benchmarkElapsedMs = 0;
                _benchmarkStreamFrames = 0;
                _totalFrameDrops = 0;
                _consecutiveFrameDrops = 0;
                if (_benchmarkChart != null)
                {
                    _benchmarkChart.Options = null;
                    _benchmarkChart.Invalidate();
                }
                ClearTelemetryDisplay();
                UpdateTotalPointsDisplay(pointCountInput, seriesCountInput);
            });

            btnClear.Click += (s, e) => clearHandler();
            btnEmergency.Click += (s, e) => clearHandler();

            return mainGrid;
        }

        private static void ClearTelemetryDisplay()
        {
            if (_benchFpsText != null) _benchFpsText.Text = "--";
            if (_benchTimeText != null) _benchTimeText.Text = "-- ms";
            if (_benchMemoryText != null) _benchMemoryText.Text = "-- MB";
            if (_benchDropsText != null) _benchDropsText.Text = "0 / 0";
            if (_benchFramesText != null) _benchFramesText.Text = "0";
            if (_benchElapsedText != null) _benchElapsedText.Text = "0s";
        }

        private static TextBlock CreateMetricCard(Grid grid, string header, string placeholder, string hint, int colIdx)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(0x1F1F2F80),
                BorderBrush = new SolidColorBrush(0x3F3F4F80),
                BorderThickness = new Thickness(1),
                CornerRadius = 4f,
                Padding = new Thickness(8),
                Margin = new Thickness(4, 0, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical };
            var headerBlock = new TextBlock
            {
                Text = header,
                Font = AppState._font,
                FontSize = 9f,
                Foreground = new SolidColorBrush(0xCCCCCCFF),
                Margin = new Thickness(0, 0, 0, 2)
            };
            var valBlock = new TextBlock
            {
                Text = placeholder,
                Font = AppState._font,
                FontSize = 13f,
                Foreground = new SolidColorBrush(0xFFFFFFFF),
                Margin = new Thickness(0, 0, 0, 2)
            };
            var hintBlock = new TextBlock
            {
                Text = hint,
                Font = AppState._font,
                FontSize = 8f,
                Foreground = new SolidColorBrush(0x888888FF)
            };
            stack.AddChild(headerBlock);
            stack.AddChild(valBlock);
            stack.AddChild(hintBlock);
            border.Child = stack;

            grid.AddChild(border);
            Grid.SetColumn(border, colIdx);
            return valBlock;
        }

        private static Func<double> CreateLcg(uint seed)
        {
            uint s = seed == 0 ? 1 : seed;
            return () =>
            {
                s = 1664525 * s + 1013904223;
                return (double)s / 4294967296.0;
            };
        }

        private static List<DataPoint?> GenerateBenchmarkCartesian(int count, int seriesIdx)
        {
            var list = new List<DataPoint?>(count);
            var rng = CreateLcg((uint)(0x12345678 ^ (seriesIdx * 0x9e3779b9)));

            double freq = 0.012 + seriesIdx * 0.00035;
            double lowFreq = 0.0017 + seriesIdx * 0.00007;
            double noiseAmp = 0.35;
            double seriesBias = (seriesIdx - 1) * 0.1;

            for (int i = 0; i < count; i++)
            {
                double x = i;
                double y = Math.Sin(i * freq) * 0.95 +
                           Math.Sin(i * lowFreq + 1.1) * 0.6 +
                           (rng() - 0.5) * noiseAmp +
                           seriesBias;
                list.Add(new DataPoint(x, y));
            }
            return list;
        }

        private static List<OHLCDataPoint> GenerateBenchmarkCandlestick(int count, int seriesIdx)
        {
            var list = new List<OHLCDataPoint>(count);
            var rng = CreateLcg((uint)(0x31415926 ^ (seriesIdx * 0x9e3779b9)));

            double lastClose = 100.0 + seriesIdx * 10.0;
            for (int i = 0; i < count; i++)
            {
                double t = i;
                double drift = (rng() - 0.5) * 0.8;
                double open = lastClose;
                double close = open + drift;
                double wick = 0.2 + rng() * 0.8;
                double high = Math.Max(open, close) + wick;
                double low = Math.Min(open, close) - wick;
                list.Add(new OHLCDataPoint(t, open, close, low, high));
                lastClose = close;
            }
            return list;
        }

        private static void RunBenchmarkGeneration()
        {
            if (_benchmarkChart == null) return;

            StopBenchmarkTimer();
            StopBenchmarkMetricsTimer();

            _benchmarkTotalPoints = _benchmarkPointsCount * _benchmarkSeriesCount;
            _benchmarkTotalFramesRendered = 0;
            _benchmarkElapsedMs = 0;
            _benchmarkStreamFrames = 0;

            var seriesList = new List<SeriesConfig>();
            var colors = new string[] { "#00E5FF", "#FF2D95", "#B026FF", "#00F5A0", "#FFD300", "#FF6B00" };

            for (int s = 0; s < _benchmarkSeriesCount; s++)
            {
                var color = colors[s % colors.Length];
                if (_benchmarkDataType == "candlestick")
                {
                    var data = GenerateBenchmarkCandlestick(_benchmarkPointsCount, s);
                    var series = new CandlestickSeriesConfig
                    {
                        Name = $"Series {s}",
                        Data = data,
                        ItemStyle = new CandlestickItemStyleConfig
                        {
                            UpColor = "#107C41",
                            DownColor = "#D83B01"
                        }
                    };
                    seriesList.Add(series);
                }
                else if (_benchmarkDataType == "scatter")
                {
                    var data = GenerateBenchmarkCartesian(_benchmarkPointsCount, s);
                    var series = new ScatterSeriesConfig
                    {
                        Name = $"Series {s}",
                        Data = new CartesianSeriesData(data),
                        Color = color,
                        Symbol = "circle",
                        SymbolSizeConstant = 4.0
                    };
                    seriesList.Add(series);
                }
                else if (_benchmarkDataType == "bar")
                {
                    var data = GenerateBenchmarkCartesian(_benchmarkPointsCount, s);
                    var series = new BarSeriesConfig
                    {
                        Name = $"Series {s}",
                        Data = new CartesianSeriesData(data),
                        Color = color,
                        BarWidth = 1.5
                    };
                    seriesList.Add(series);
                }
                else // line
                {
                    var data = GenerateBenchmarkCartesian(_benchmarkPointsCount, s);
                    var series = new LineSeriesConfig
                    {
                        Name = $"Series {s}",
                        Data = new CartesianSeriesData(data),
                        Color = color,
                        LineStyle = new LineStyleConfig { Width = 1.0 }
                    };
                    seriesList.Add(series);
                }
            }

            _benchmarkChart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 60.0, Right = 20.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig { Type = AxisType.Value },
                YAxis = new AxisConfig { Type = AxisType.Value },
                Series = seriesList,
                Tooltip = new TooltipConfig { Show = true, Trigger = "axis" }
            };

            if (_totalPointsDisplay != null)
            {
                _totalPointsDisplay.Text = _benchmarkTotalPoints.ToString("N0");
                _totalPointsDisplay.Foreground = new SolidColorBrush(0x00E5FFFF);
            }
            if (_totalPointsHint != null) _totalPointsHint.Text = $"{FormatAbbreviatedNumber(_benchmarkTotalPoints)} points";

            _benchmarkChart.Invalidate();
            StartBenchmarkMetricsTimer();
        }

        private static void StartBenchmarkStreaming()
        {
            if (_benchmarkTimer != null || _benchmarkChart == null || _benchmarkChart.Options == null) return;

            _benchmarkIsStreaming = true;
            _benchmarkStreamFrames = 0;

            _benchmarkTimer = new System.Timers.Timer(16.0); // Renders ~60 FPS pressure
            _benchmarkTimer.Elapsed += (s, e) =>
            {
                UIThread.Post(() =>
                {
                    if (!_benchmarkIsStreaming || _benchmarkChart == null || _benchmarkChart.Options == null)
                    {
                        StopBenchmarkTimer();
                        return;
                    }

                    _benchmarkStreamFrames++;
                    if (_benchmarkStreamFrames >= _benchmarkStreamDuration)
                    {
                        StopBenchmarkTimer();
                        return;
                    }

                    var options = _benchmarkChart.Options;
                    if (options == null || options.Series == null) return;

                    // Append data per series
                    var stepIdx = _benchmarkPointsCount + _benchmarkStreamFrames * _benchmarkStreamRate;
                    for (int s = 0; s < options.Series.Count; s++)
                    {
                        var series = options.Series[s];
                        if (series is LineSeriesConfig ls && ls.Data != null)
                        {
                            var batch = GenerateBenchmarkCartesian(_benchmarkStreamRate, s);
                            // Shift X index
                            for (int i = 0; i < batch.Count; i++)
                            {
                                if (batch[i] != null) batch[i] = new DataPoint(stepIdx + i, batch[i]!.Value.Y);
                            }
                            ls.Data.AppendRange(batch);
                        }
                        else if (series is ScatterSeriesConfig scs && scs.Data != null)
                        {
                            var batch = GenerateBenchmarkCartesian(_benchmarkStreamRate, s);
                            for (int i = 0; i < batch.Count; i++)
                            {
                                if (batch[i] != null) batch[i] = new DataPoint(stepIdx + i, batch[i]!.Value.Y);
                            }
                            scs.Data.AppendRange(batch);
                        }
                    }

                    _benchmarkTotalPoints += _benchmarkStreamRate * options.Series.Count;
                    if (_totalPointsDisplay != null)
                    {
                        _totalPointsDisplay.Text = _benchmarkTotalPoints.ToString("N0");
                        if (_benchmarkTotalPoints > 1000000000) _totalPointsDisplay.Foreground = new SolidColorBrush(0xFF3333FF);
                        else if (_benchmarkTotalPoints > 100000000) _totalPointsDisplay.Foreground = new SolidColorBrush(0xFF8822FF);
                        else _totalPointsDisplay.Foreground = new SolidColorBrush(0x00E5FFFF);
                    }
                    if (_totalPointsHint != null) _totalPointsHint.Text = $"{FormatAbbreviatedNumber(_benchmarkTotalPoints)} points";

                    _benchmarkChart.Invalidate();
                });
            };
            _benchmarkTimer.AutoReset = true;
            _benchmarkTimer.Start();
        }

        private static void StartBenchmarkMetricsTimer()
        {
            if (_benchmarkMetricsTimer != null) return;

            System.Diagnostics.Process? process = OperatingSystem.IsBrowser()
                ? null
                : System.Diagnostics.Process.GetCurrentProcess();
            _benchmarkMetricsTimer = new System.Timers.Timer(100.0); // Poll metrics every 100ms
            _benchmarkMetricsTimer.Elapsed += (s, e) =>
            {
                UIThread.Post(() =>
                {
                    _benchmarkTotalFramesRendered++;
                    _benchmarkElapsedMs += 100.0;

                    // Private memory in MB
                    double memoryMb;
                    if (process != null)
                    {
                        process.Refresh();
                        memoryMb = process.PrivateMemorySize64 / (1024.0 * 1024.0);
                    }
                    else
                    {
                        memoryMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
                    }

                    // Compositor metrics
                    double frameTimeMs = 1.0;
                    var compositor = AppState._screenCompositor;
                    if (compositor != null)
                    {
                        frameTimeMs = compositor.Metrics.FrameTimeMs;
                    }

                    double fps = 1000.0 / Math.Max(1.0, frameTimeMs);
                    if (fps > 60.0) fps = 60.0;

                    if (_benchFpsText != null)
                    {
                        _benchFpsText.Text = $"{fps:F1}";
                        if (fps > 55.0) _benchFpsText.Foreground = new SolidColorBrush(0x10B981FF); // smooth green
                        else if (fps > 30.0) _benchFpsText.Foreground = new SolidColorBrush(0xF59E0BFF); // medium orange
                        else _benchFpsText.Foreground = new SolidColorBrush(0xEF4444FF); // choppy red
                    }

                    if (frameTimeMs > 16.67)
                    {
                        _totalFrameDrops++;
                        _consecutiveFrameDrops++;
                    }
                    else
                    {
                        _consecutiveFrameDrops = 0;
                    }

                    if (_benchTimeText != null) _benchTimeText.Text = $"{frameTimeMs:F2} ms (Avg)";
                    if (_benchMemoryText != null) _benchMemoryText.Text = $"{memoryMb:F1} MB";
                    if (_benchDropsText != null) _benchDropsText.Text = $"{_totalFrameDrops} / {_consecutiveFrameDrops}";
                    if (_benchFramesText != null) _benchFramesText.Text = _benchmarkTotalFramesRendered.ToString();
                    if (_benchElapsedText != null) _benchElapsedText.Text = $"{(_benchmarkElapsedMs / 1000.0):F1}s";
                });
            };
            _benchmarkMetricsTimer.AutoReset = true;
            _benchmarkMetricsTimer.Start();
        }

        private static void StopBenchmarkTimer()
        {
            _benchmarkIsStreaming = false;
            if (_benchmarkTimer != null)
            {
                _benchmarkTimer.Stop();
                _benchmarkTimer.Dispose();
                _benchmarkTimer = null;
            }
        }

        private static void StopBenchmarkMetricsTimer()
        {
            if (_benchmarkMetricsTimer != null)
            {
                _benchmarkMetricsTimer.Stop();
                _benchmarkMetricsTimer.Dispose();
                _benchmarkMetricsTimer = null;
            }
        }

        // ==========================================
        // SAMPLE 17: ANIMATIONS & TRANSITIONS
        // ==========================================
        private static FrameworkElement CreateTransitionsTab()
        {
            var grid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            grid.RowDefinitions.Add(new GridLength(35, GridUnitType.Absolute)); // Subtitle
            grid.RowDefinitions.Add(new GridLength(45, GridUnitType.Absolute)); // Controls
            grid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));     // Charts split

            var subtitleText = new TextBlock
            {
                Text = "Data update transitions and animations on Cartesian combined series and Pie breakdown donuts.",
                Font = AppState._font,
                FontSize = 11f,
                Foreground = new SolidColorBrush(0xFFFFFF90),
                Margin = new Thickness(0, 0, 0, 8)
            };
            grid.AddChild(subtitleText);
            Grid.SetRow(subtitleText, 0);

            var controlsStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var btnTrigger = new Button { Content = "Trigger Transition Update", Font = AppState._font, Margin = new Thickness(0, 0, 16, 0) };
            _animStatusText = new TextBlock { Text = "Status: Initial Render.", Font = AppState._font, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(0xCCCCCCFF) };
            controlsStack.AddChild(btnTrigger);
            controlsStack.AddChild(_animStatusText);

            grid.AddChild(controlsStack);
            Grid.SetRow(controlsStack, 1);

            var chartsGrid = new Grid();
            chartsGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));
            chartsGrid.ColumnDefinitions.Add(new GridLength(1, GridUnitType.Star));

            _animCartesianChart = new ChartControl { Height = 300f, Margin = new Thickness(0, 0, 12, 0) };
            _animPieChart = new ChartControl { Height = 300f, Margin = new Thickness(12, 0, 0, 0) };

            chartsGrid.AddChild(_animCartesianChart);
            Grid.SetColumn(_animCartesianChart, 0);
            chartsGrid.AddChild(_animPieChart);
            Grid.SetColumn(_animPieChart, 1);

            grid.AddChild(chartsGrid);
            Grid.SetRow(chartsGrid, 2);

            RunTransitionsUpdate();

            btnTrigger.Click += (s, e) =>
            {
                _animStep++;
                RunTransitionsUpdate();
            };

            return grid;
        }

        private static void RunTransitionsUpdate()
        {
            if (_animCartesianChart == null || _animPieChart == null) return;

            var rng = CreateLcg((uint)(1000 + _animStep * 97));
            double phase = _animStep * 0.7;
            double amplitude = 0.9 + (_animStep % 4) * 0.65;
            double offset = (_animStep % 2 == 0 ? -0.35 : 0.55) + (rng() - 0.5) * 0.15;

            int n = 15;
            var lineData = new List<DataPoint?>(n);
            var barData = new List<DataPoint?>(n);
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)(n - 1);
                double noise = (rng() - 0.5) * 0.18;
                double yLine = offset + Math.Sin(t * Math.PI * 2 + phase) * amplitude + noise;
                double yBar = offset * 0.4 + Math.Cos(t * Math.PI * 2 * 0.75 + phase * 0.6) * (amplitude * 0.9) + (rng() - 0.5) * 0.35;

                lineData.Add(new DataPoint(i, yLine));
                barData.Add(new DataPoint(i, yBar));
            }

            var lineSeries = new LineSeriesConfig
            {
                Name = "Trend Line",
                Data = new CartesianSeriesData(lineData),
                Color = "#FF2D95",
                LineStyle = new LineStyleConfig { Width = 3.0 }
            };

            var barSeries = new BarSeriesConfig
            {
                Name = "Breakout Bars",
                Data = new CartesianSeriesData(barData),
                Color = "#00E5FF",
                BarWidth = 0.6
            };

            _animCartesianChart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 50.0, Right = 20.0, Top = 20.0, Bottom = 45.0 },
                XAxis = new AxisConfig { Type = AxisType.Value },
                YAxis = new AxisConfig { Type = AxisType.Value },
                Series = new List<SeriesConfig> { barSeries, lineSeries },
                Tooltip = new TooltipConfig { Show = true, Trigger = "axis" },
                Legend = new LegendConfig { Show = true }
            };

            var slices = new List<PieDataItem>
            {
                new PieDataItem { Name = "Compute", Value = Math.Max(1.0, 42 * (0.35 + rng() * 1.65)), Color = "#00E5FF" },
                new PieDataItem { Name = "Memory", Value = Math.Max(1.0, 30 * (0.35 + rng() * 1.65)), Color = "#FF2D95" },
                new PieDataItem { Name = "Raster", Value = Math.Max(1.0, 18 * (0.35 + rng() * 1.65)), Color = "#B026FF" },
                new PieDataItem { Name = "Upload", Value = Math.Max(1.0, 12 * (0.35 + rng() * 1.65)), Color = "#00F5A0" },
                new PieDataItem { Name = "Sync", Value = Math.Max(1.0, 9 * (0.35 + rng() * 1.65)), Color = "#FFD300" }
            };

            var pieSeries = new PieSeriesConfig
            {
                Name = "Breakdown",
                Data = slices,
                Radius = ("35%", "75%"),
                Center = ("50%", "50%")
            };

            _animPieChart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 20.0, Right = 20.0, Top = 20.0, Bottom = 20.0 },
                Series = new List<SeriesConfig> { pieSeries },
                Tooltip = new TooltipConfig { Show = true, Trigger = "item" }
            };

            if (_animStatusText != null)
            {
                _animStatusText.Text = $"Step {_animStep} · Amp {amplitude:F2} · Offset {offset:F2} · Transitions Rendered.";
            }

            _animCartesianChart.Invalidate();
            _animPieChart.Invalidate();
        }

        // ==========================================
        // SAMPLE 18: CARTESIAN FORMATS & GRIDS
        // ==========================================
        private static FrameworkElement CreateCartesianFormatsTab()
        {
            var grid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            grid.RowDefinitions.Add(new GridLength(35, GridUnitType.Absolute)); // Subtitle
            grid.RowDefinitions.Add(new GridLength(1, GridUnitType.Star));     // Chart canvas

            var subtitleText = new TextBlock
            {
                Text = "Demonstrates multi-series columnar list imports, custom gridlines, axes tick formats, and padded layout borders.",
                Font = AppState._font,
                FontSize = 11f,
                Foreground = new SolidColorBrush(0xFFFFFF90),
                Margin = new Thickness(0, 0, 0, 8)
            };
            grid.AddChild(subtitleText);
            Grid.SetRow(subtitleText, 0);

            var chart = new ChartControl
            {
                Height = 340f,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var pointsA = new List<DataPoint?>
            {
                new DataPoint(0, 20), new DataPoint(1, 24), new DataPoint(2, 35),
                new DataPoint(3, 45), new DataPoint(4, 52), new DataPoint(5, 60),
                new DataPoint(6, 68), new DataPoint(7, 75)
            };
            var pointsB = new List<DataPoint?>
            {
                new DataPoint(0, 40), new DataPoint(1, 48), new DataPoint(2, 42),
                new DataPoint(3, 38), new DataPoint(4, 30), new DataPoint(5, 25),
                new DataPoint(6, 18), new DataPoint(7, 12)
            };

            var lineA = new LineSeriesConfig
            {
                Name = "Core Load",
                Data = new CartesianSeriesData(pointsA),
                Color = "#00F5A0",
                LineStyle = new LineStyleConfig { Width = 3.0 }
            };

            var lineB = new LineSeriesConfig
            {
                Name = "IO Thread",
                Data = new CartesianSeriesData(pointsB),
                Color = "#FFD300",
                LineStyle = new LineStyleConfig { Width = 2.0 }
            };

            chart.Options = new ChartGPUOptions
            {
                Theme = "dark",
                Grid = new GridConfig { Left = 60.0, Right = 40.0, Top = 30.0, Bottom = 50.0 },
                XAxis = new AxisConfig
                {
                    Type = AxisType.Value,
                    Min = 0,
                    Max = 7,
                    TickFormatter = (v) => $"Cycle {v:F0}"
                },
                YAxis = new AxisConfig
                {
                    Type = AxisType.Value,
                    Min = 0,
                    Max = 80,
                    TickFormatter = (v) => $"{v:F0}%"
                },
                Series = new List<SeriesConfig> { lineA, lineB },
                Tooltip = new TooltipConfig { Show = true, Trigger = "axis" },
                Legend = new LegendConfig { Show = true }
            };

            grid.AddChild(chart);
            Grid.SetRow(chart, 1);
            return grid;
        }
    }
}
