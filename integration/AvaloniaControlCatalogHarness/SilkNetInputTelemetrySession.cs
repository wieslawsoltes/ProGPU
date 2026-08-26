using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ProGpu.Avalonia.Integration;

/// <summary>
/// Records real routed input delivered by the Silk.NET windowing backend.
/// The harness is intentionally driven by external OS/VM automation rather
/// than by raising Avalonia events in-process.
/// </summary>
internal sealed class SilkNetInputTelemetrySession : IDisposable
{
    private const string OutputVariable =
        "PROGPU_AVALONIA_INPUT_OUTPUT";
    private const string ExpectedVariable =
        "PROGPU_AVALONIA_INPUT_EXPECT";
    private const string TimeoutVariable =
        "PROGPU_AVALONIA_INPUT_TIMEOUT_SECONDS";

    private readonly string _outputPath;
    private readonly HashSet<string> _expected;
    private readonly Dictionary<string, int> _observed =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable _windowOpenedSubscription;
    private readonly DispatcherTimer _timer;
    private readonly DateTime _deadline;
    private Window? _window;
    private PixelPoint _initialPosition;
    private Size _lastLayoutSize;
    private bool _hasInitialPosition;
    private bool _hasLayoutSize;
    private bool _attached;
    private bool _completed;

    private SilkNetInputTelemetrySession(
        string outputPath,
        HashSet<string> expected,
        int timeoutSeconds)
    {
        _outputPath = outputPath;
        _expected = expected;
        _deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        _windowOpenedSubscription =
            Window.WindowOpenedEvent.AddClassHandler<Window>(
                OnWindowOpened);
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            OnTimerTick);
    }

    internal static SilkNetInputTelemetrySession? TryStart(
        bool nativeWindowing)
    {
        string? outputPath =
            Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(outputPath))
            return null;
        if (nativeWindowing)
        {
            throw new InvalidOperationException(
                "Silk.NET input telemetry requires Silk.NET windowing.");
        }

        string expectedText =
            Environment.GetEnvironmentVariable(ExpectedVariable) ??
            "keyboard,text,pointer,wheel,shortcut";
        HashSet<string> expected = expectedText
            .Split(',', StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int timeout = int.TryParse(
                Environment.GetEnvironmentVariable(TimeoutVariable),
                out int value) && value > 0
            ? value
            : 30;
        return new SilkNetInputTelemetrySession(
            Path.GetFullPath(outputPath),
            expected,
            timeout);
    }

    internal void Attach()
    {
        if (_attached)
        {
            throw new InvalidOperationException(
                "Silk.NET input telemetry was attached twice.");
        }

        _attached = true;
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _windowOpenedSubscription.Dispose();
    }

    private void OnWindowOpened(
        Window window,
        RoutedEventArgs args)
    {
        _ = args;
        if (!_attached || _window is not null)
            return;

        _window = window;
        _initialPosition = window.Position;
        _hasInitialPosition = true;
        window.PositionChanged += OnPositionChanged;
        window.PropertyChanged += OnWindowPropertyChanged;
        window.LayoutUpdated += OnLayoutUpdated;
        window.AddHandler(
            InputElement.KeyDownEvent,
            OnKeyDown,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        window.AddHandler(
            InputElement.KeyUpEvent,
            OnKeyUp,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        window.AddHandler(
            InputElement.TextInputEvent,
            OnTextInput,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        window.AddHandler(
            InputElement.PointerMovedEvent,
            OnPointerMoved,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        window.AddHandler(
            InputElement.PointerPressedEvent,
            OnPointerPressed,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        window.AddHandler(
            InputElement.PointerReleasedEvent,
            OnPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        window.AddHandler(
            InputElement.PointerWheelChangedEvent,
            OnPointerWheel,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        window.AddHandler(
            InputElement.PointerTouchPadGestureMagnifyEvent,
            OnMagnify,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        window.AddHandler(
            InputElement.PointerTouchPadGestureRotateEvent,
            OnRotate,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        window.AddHandler(
            InputElement.PointerTouchPadGestureSwipeEvent,
            OnSwipe,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);

        KeyModifiers commandModifier = OperatingSystem.IsMacOS()
            ? KeyModifiers.Meta
            : KeyModifiers.Control;
        window.KeyBindings.Add(
            new KeyBinding
            {
                Gesture = new KeyGesture(
                    Key.K,
                    commandModifier | KeyModifiers.Shift),
                Command = new CallbackCommand(
                    () => Record("shortcut"))
            });
        Dispatcher.UIThread.Post(
            () => window.GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault()?
                .Focus(),
            DispatcherPriority.Input);
    }

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        _ = sender;
        _ = args;
        Record("key-down");
        RecordKeyboardIfComplete();
    }

    private void OnKeyUp(object? sender, KeyEventArgs args)
    {
        _ = sender;
        _ = args;
        Record("key-up");
        RecordKeyboardIfComplete();
    }

    private void OnTextInput(object? sender, TextInputEventArgs args)
    {
        _ = sender;
        if (!string.IsNullOrEmpty(args.Text))
            Record("text");
    }

    private void OnPointerMoved(object? sender, PointerEventArgs args)
    {
        _ = sender;
        _ = args;
        Record("pointer-move");
        RecordPointerIfComplete();
    }

    private void OnPointerPressed(
        object? sender,
        PointerPressedEventArgs args)
    {
        _ = sender;
        Record("pointer-press");
        RecordPointerButton(
            args.GetCurrentPoint(_window)
                .Properties.PointerUpdateKind);
        if (args.Pointer.Type == PointerType.Touch)
            Record("touch-press");
        RecordPointerIfComplete();
    }

    private void OnPointerReleased(
        object? sender,
        PointerReleasedEventArgs args)
    {
        _ = sender;
        Record("pointer-release");
        RecordPointerButton(
            args.GetCurrentPoint(_window)
                .Properties.PointerUpdateKind);
        if (args.Pointer.Type == PointerType.Touch)
        {
            Record("touch-release");
            if (Has("touch-press"))
                Record("touch");
        }
        RecordPointerIfComplete();
    }

    private void OnPointerWheel(
        object? sender,
        PointerWheelEventArgs args)
    {
        _ = sender;
        if (args.Delta.X != 0 || args.Delta.Y != 0)
            Record("wheel");
    }

    private void OnMagnify(object? sender, PointerDeltaEventArgs args)
    {
        _ = sender;
        _ = args;
        Record("magnify");
    }

    private void OnRotate(object? sender, PointerDeltaEventArgs args)
    {
        _ = sender;
        _ = args;
        Record("rotate");
    }

    private void OnSwipe(object? sender, PointerDeltaEventArgs args)
    {
        _ = sender;
        _ = args;
        Record("swipe");
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs args)
    {
        _ = sender;
        if (_hasInitialPosition &&
            (Math.Abs(args.Point.X - _initialPosition.X) >= 100 ||
             Math.Abs(args.Point.Y - _initialPosition.Y) >= 100))
        {
            Record("move");
        }
    }

    private void OnWindowPropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs args)
    {
        _ = sender;
        if (args.Property == TopLevel.ClientSizeProperty)
        {
            Record("resize-sample");
            RecordResizeIfComplete();
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        if (_window is null)
            return;

        Size size = _window.ClientSize;
        if (!_hasLayoutSize)
        {
            _lastLayoutSize = size;
            _hasLayoutSize = true;
            return;
        }

        if (size == _lastLayoutSize)
            return;

        _lastLayoutSize = size;
        Record("layout-resize");
        RecordResizeIfComplete();
    }

    private void RecordResizeIfComplete()
    {
        // A final-only resize notification is insufficient here. Requiring
        // multiple matching client-size and layout observations proves that
        // Avalonia continued arranging while the native size loop was active.
        if (Count("resize-sample") >= 3 && Count("layout-resize") >= 3)
            Record("resize");
    }

    private void RecordPointerButton(PointerUpdateKind kind)
    {
        string? name = kind switch
        {
            PointerUpdateKind.LeftButtonPressed or
            PointerUpdateKind.LeftButtonReleased => "mouse-left",
            PointerUpdateKind.RightButtonPressed or
            PointerUpdateKind.RightButtonReleased => "mouse-right",
            PointerUpdateKind.MiddleButtonPressed or
            PointerUpdateKind.MiddleButtonReleased => "mouse-middle",
            PointerUpdateKind.XButton1Pressed or
            PointerUpdateKind.XButton1Released => "mouse-x1",
            PointerUpdateKind.XButton2Pressed or
            PointerUpdateKind.XButton2Released => "mouse-x2",
            _ => null
        };
        if (name is not null)
            Record(name);
    }

    private void RecordKeyboardIfComplete()
    {
        if (Has("key-down") && Has("key-up"))
            Record("keyboard");
    }

    private void RecordPointerIfComplete()
    {
        if (Has("pointer-move") &&
            Has("pointer-press") &&
            Has("pointer-release"))
        {
            Record("pointer");
        }
    }

    private void Record(string name)
    {
        _observed.TryGetValue(name, out int count);
        _observed[name] = count + 1;
        if (!_completed && _expected.All(Has))
            Complete(passed: true, error: null);
    }

    private bool Has(string name) =>
        _observed.TryGetValue(name, out int count) && count > 0;

    private int Count(string name) =>
        _observed.TryGetValue(name, out int count) ? count : 0;

    private void OnTimerTick(object? sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        if (!_completed && DateTime.UtcNow >= _deadline)
            Complete(passed: false, error: "Input telemetry timed out.");
    }

    private void Complete(bool passed, string? error)
    {
        if (_completed)
            return;
        _completed = true;
        _timer.Stop();
        string? directory = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        using FileStream output = File.Create(_outputPath);
        using var writer = new Utf8JsonWriter(
            output,
            new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteBoolean("Passed", passed);
        writer.WriteStartArray("Expected");
        foreach (string name in _expected.Order())
            writer.WriteStringValue(name);
        writer.WriteEndArray();
        writer.WriteStartObject("Observed");
        foreach ((string name, int count) in _observed.OrderBy(x => x.Key))
            writer.WriteNumber(name, count);
        writer.WriteEndObject();
        if (_window is not null)
        {
            writer.WriteStartObject("Window");
            writer.WriteNumber("PositionX", _window.Position.X);
            writer.WriteNumber("PositionY", _window.Position.Y);
            writer.WriteNumber("ClientWidth", _window.ClientSize.Width);
            writer.WriteNumber("ClientHeight", _window.ClientSize.Height);
            writer.WriteNumber("RenderScaling", _window.RenderScaling);
            writer.WriteEndObject();
        }
        if (error is not null)
            writer.WriteString("Error", error);
        writer.WriteEndObject();
        writer.Flush();
        Environment.Exit(passed ? 0 : 12);
    }

    private sealed class CallbackCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter)
        {
            _ = parameter;
            return true;
        }

        public void Execute(object? parameter)
        {
            _ = parameter;
            action();
        }
    }
}
