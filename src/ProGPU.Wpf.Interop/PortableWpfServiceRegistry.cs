using System.Reflection;

namespace ProGPU.Wpf.Interop;

public interface IPortableClipboardServiceRegistrar
{
    Assembly SourceAssembly { get; }

    IDisposable Register(Func<string?> getText, Action<string?> setText);

    void Clear();
}

public interface IPortableLauncherServiceRegistrar
{
    Assembly SourceAssembly { get; }

    IDisposable Register(Func<object, bool> launch);

    void Clear();
}

public interface IPortableMessageBoxServiceRegistrar
{
    Assembly SourceAssembly { get; }

    IDisposable Register(Func<object, object?> show);

    void Clear();
}

public interface IPortableFileDialogServiceRegistrar
{
    Assembly SourceAssembly { get; }

    IDisposable Register(Func<object, string?> showDialog);

    void Clear();
}

public interface IPortableMediaContextRenderServiceRegistrar
{
    Assembly SourceAssembly { get; }

    IDisposable Register(Action<object?, TimeSpan> requestRender);

    void Clear();
}

public sealed class PortableWindowActivationCallbacks
{
    public PortableWindowActivationCallbacks(
        Func<object, object?> activate,
        Action<object>? show = null,
        Action<object>? hide = null,
        Action<object, object>? setWindowState = null,
        Action<object, string>? setTitle = null,
        Action<object, double, double>? setClientSize = null,
        Action<object, double, double>? setPosition = null,
        Action<object, bool>? setTopmost = null,
        Action<object, object, object>? setWindowBorder = null,
        Action<object>? close = null,
        Action<object>? run = null,
        Action<object>? dispose = null,
        Func<object, bool>? dragMove = null,
        Func<object, IntPtr>? getHandle = null)
    {
        Activate = activate ?? throw new ArgumentNullException(nameof(activate));
        Show = show;
        Hide = hide;
        SetWindowState = setWindowState;
        SetTitle = setTitle;
        SetClientSize = setClientSize;
        SetPosition = setPosition;
        SetTopmost = setTopmost;
        SetWindowBorder = setWindowBorder;
        Close = close;
        Run = run;
        Dispose = dispose;
        DragMove = dragMove;
        GetHandle = getHandle;
    }

    public Func<object, object?> Activate { get; }

    public Action<object>? Show { get; }

    public Action<object>? Hide { get; }

    public Action<object, object>? SetWindowState { get; }

    public Action<object, string>? SetTitle { get; }

    public Action<object, double, double>? SetClientSize { get; }

    public Action<object, double, double>? SetPosition { get; }

    public Action<object, bool>? SetTopmost { get; }

    public Action<object, object, object>? SetWindowBorder { get; }

    public Action<object>? Close { get; }

    public Action<object>? Run { get; }

    public Action<object>? Dispose { get; }

    public Func<object, bool>? DragMove { get; }

    public Func<object, IntPtr>? GetHandle { get; }
}

public sealed class PortableWindowInputEvent
{
    public PortableWindowInputEvent(
        int kind,
        string? key = null,
        int scanCode = 0,
        char? character = null,
        double x = 0,
        double y = 0,
        double deltaX = 0,
        double deltaY = 0,
        int button = 0,
        int modifiers = 0)
    {
        Kind = kind;
        Key = key;
        ScanCode = scanCode;
        Character = character;
        X = x;
        Y = y;
        DeltaX = deltaX;
        DeltaY = deltaY;
        Button = button;
        Modifiers = modifiers;
    }

    public int Kind { get; }

    public string? Key { get; }

    public int ScanCode { get; }

    public char? Character { get; }

    public double X { get; }

    public double Y { get; }

    public double DeltaX { get; }

    public double DeltaY { get; }

    public int Button { get; }

    public int Modifiers { get; }

    public bool Handled { get; set; }
}

public enum PortableWindowCloseResult
{
    NotInvoked = 0,
    Closed = 1,
    Canceled = 2
}

public interface IPortableWindowActivationServiceRegistrar
{
    Assembly SourceAssembly { get; }

    void Register(PortableWindowActivationCallbacks callbacks);

    bool TryIsCurrentApplicationMainWindow(object window, out bool isMainWindow);

    bool TryCloseWindow(object window, out PortableWindowCloseResult result);

    bool TrySetActivationState(object window, bool isActive);

    bool TryBeginInvokeInput(object window, Action callback);

    bool TryProcessInputEvent(object window, PortableWindowInputEvent input);

    bool TryFlushDispatcherOperations(object window, string markerPriorityName, TimeSpan? timeout);

    bool TryProcessDragDropEvent(
        object window,
        int dragDropEventKind,
        string[] files,
        string? text,
        double x,
        double y,
        int allowedEffects,
        int acceptedEffect,
        out int result);

    void Clear();
}

public static class PortableWpfServiceRegistry
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<Assembly, IPortableWindowActivationServiceRegistrar> WindowActivationServices = new();
    private static readonly Dictionary<Assembly, IPortableClipboardServiceRegistrar> ClipboardServices = new();
    private static readonly Dictionary<Assembly, IPortableLauncherServiceRegistrar> LauncherServices = new();
    private static readonly Dictionary<Assembly, IPortableMessageBoxServiceRegistrar> MessageBoxServices = new();
    private static readonly Dictionary<Assembly, IPortableFileDialogServiceRegistrar> FileDialogServices = new();
    private static readonly Dictionary<Assembly, IPortableMediaContextRenderServiceRegistrar> MediaContextRenderServices = new();

    public static IDisposable RegisterWindowActivationService(IPortableWindowActivationServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (SyncRoot)
        {
            WindowActivationServices[service.SourceAssembly] = service;
        }

        return new Registration<IPortableWindowActivationServiceRegistrar>(service, WindowActivationServices);
    }

    public static bool TryGetWindowActivationService(
        Assembly sourceAssembly,
        out IPortableWindowActivationServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(sourceAssembly);

        lock (SyncRoot)
        {
            return WindowActivationServices.TryGetValue(sourceAssembly, out service!);
        }
    }

    public static IDisposable RegisterClipboardService(IPortableClipboardServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (SyncRoot)
        {
            ClipboardServices[service.SourceAssembly] = service;
        }

        return new Registration<IPortableClipboardServiceRegistrar>(service, ClipboardServices);
    }

    public static bool TryGetClipboardService(
        Assembly sourceAssembly,
        out IPortableClipboardServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(sourceAssembly);

        lock (SyncRoot)
        {
            return ClipboardServices.TryGetValue(sourceAssembly, out service!);
        }
    }

    public static IDisposable RegisterLauncherService(IPortableLauncherServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (SyncRoot)
        {
            LauncherServices[service.SourceAssembly] = service;
        }

        return new Registration<IPortableLauncherServiceRegistrar>(service, LauncherServices);
    }

    public static bool TryGetLauncherService(
        Assembly sourceAssembly,
        out IPortableLauncherServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(sourceAssembly);

        lock (SyncRoot)
        {
            return LauncherServices.TryGetValue(sourceAssembly, out service!);
        }
    }

    public static IDisposable RegisterMessageBoxService(IPortableMessageBoxServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (SyncRoot)
        {
            MessageBoxServices[service.SourceAssembly] = service;
        }

        return new Registration<IPortableMessageBoxServiceRegistrar>(service, MessageBoxServices);
    }

    public static bool TryGetMessageBoxService(
        Assembly sourceAssembly,
        out IPortableMessageBoxServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(sourceAssembly);

        lock (SyncRoot)
        {
            return MessageBoxServices.TryGetValue(sourceAssembly, out service!);
        }
    }

    public static IDisposable RegisterFileDialogService(IPortableFileDialogServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (SyncRoot)
        {
            FileDialogServices[service.SourceAssembly] = service;
        }

        return new Registration<IPortableFileDialogServiceRegistrar>(service, FileDialogServices);
    }

    public static bool TryGetFileDialogService(
        Assembly sourceAssembly,
        out IPortableFileDialogServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(sourceAssembly);

        lock (SyncRoot)
        {
            return FileDialogServices.TryGetValue(sourceAssembly, out service!);
        }
    }

    public static IDisposable RegisterMediaContextRenderService(IPortableMediaContextRenderServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(service);

        lock (SyncRoot)
        {
            MediaContextRenderServices[service.SourceAssembly] = service;
        }

        return new Registration<IPortableMediaContextRenderServiceRegistrar>(service, MediaContextRenderServices);
    }

    public static bool TryGetMediaContextRenderService(
        Assembly sourceAssembly,
        out IPortableMediaContextRenderServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(sourceAssembly);

        lock (SyncRoot)
        {
            return MediaContextRenderServices.TryGetValue(sourceAssembly, out service!);
        }
    }

    private sealed class Registration<TService> : IDisposable
        where TService : class
    {
        private readonly Dictionary<Assembly, TService> _services;
        private TService? _service;

        public Registration(TService service, Dictionary<Assembly, TService> services)
        {
            _service = service;
            _services = services;
        }

        public void Dispose()
        {
            var service = _service;
            if (service == null)
            {
                return;
            }

            _service = null;

            lock (SyncRoot)
            {
                var sourceAssembly = GetSourceAssembly(service);
                if (_services.TryGetValue(sourceAssembly, out var current) &&
                    ReferenceEquals(current, service))
                {
                    _services.Remove(sourceAssembly);
                }
            }
        }

        private static Assembly GetSourceAssembly(TService service)
        {
            return service switch
            {
                IPortableWindowActivationServiceRegistrar windowActivationService => windowActivationService.SourceAssembly,
                IPortableClipboardServiceRegistrar clipboardService => clipboardService.SourceAssembly,
                IPortableLauncherServiceRegistrar launcherService => launcherService.SourceAssembly,
                IPortableMessageBoxServiceRegistrar messageBoxService => messageBoxService.SourceAssembly,
                IPortableFileDialogServiceRegistrar fileDialogService => fileDialogService.SourceAssembly,
                IPortableMediaContextRenderServiceRegistrar renderService => renderService.SourceAssembly,
                _ => throw new InvalidOperationException("Unsupported portable WPF service registrar.")
            };
        }
    }
}
