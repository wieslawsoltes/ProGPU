namespace ProGPU.Wpf.Interop;

public readonly record struct PortableWpfServiceKey(string Name)
{
    public static PortableWpfServiceKey PresentationCore { get; } = new(nameof(PresentationCore));

    public static PortableWpfServiceKey PresentationFramework { get; } = new(nameof(PresentationFramework));
}

public interface IPortableClipboardServiceRegistrar
{
    PortableWpfServiceKey ServiceKey { get; }

    IDisposable Register(Func<string?> getText, Action<string?> setText);

    void Clear();
}

public interface IPortableLauncherServiceRegistrar
{
    PortableWpfServiceKey ServiceKey { get; }

    IDisposable Register(Func<PortableLaunchRequest, bool> launch);

    void Clear();
}

public interface IPortableMessageBoxServiceRegistrar
{
    PortableWpfServiceKey ServiceKey { get; }

    IDisposable Register(Func<PortableMessageBoxRequest, string?> show);

    void Clear();
}

public interface IPortableFileDialogServiceRegistrar
{
    PortableWpfServiceKey ServiceKey { get; }

    IDisposable Register(Func<PortableFileDialogRequest, string?> showDialog);

    void Clear();
}

public sealed class PortableLaunchRequest
{
    public PortableLaunchRequest(Uri uri, string targetFrame, bool isTopLevel)
    {
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        TargetFrame = targetFrame;
        IsTopLevel = isTopLevel;
    }

    public Uri Uri { get; }

    public string TargetFrame { get; }

    public bool IsTopLevel { get; }
}

public sealed class PortableMessageBoxRequest
{
    public PortableMessageBoxRequest(
        string? messageBoxText,
        string? caption,
        string? button,
        string? icon,
        string? defaultResult,
        string? options,
        string? fallbackResult)
    {
        MessageBoxText = messageBoxText ?? string.Empty;
        Caption = caption ?? string.Empty;
        Button = button ?? "OK";
        Icon = icon ?? "None";
        DefaultResult = defaultResult ?? "None";
        Options = options ?? "None";
        FallbackResult = fallbackResult ?? "OK";
    }

    public string MessageBoxText { get; }

    public string Caption { get; }

    public string Button { get; }

    public string Icon { get; }

    public string DefaultResult { get; }

    public string Options { get; }

    public string FallbackResult { get; }
}

public sealed class PortableFileDialogRequest
{
    public PortableFileDialogRequest(
        string? kind,
        string? title,
        string? initialDirectory,
        string? defaultDirectory,
        string? suggestedItemName,
        string? defaultExtension,
        string? filter,
        int filterIndex)
    {
        Kind = kind ?? "OpenFile";
        Title = title ?? string.Empty;
        InitialDirectory = initialDirectory ?? string.Empty;
        DefaultDirectory = defaultDirectory ?? string.Empty;
        SuggestedItemName = suggestedItemName ?? string.Empty;
        DefaultExtension = defaultExtension ?? string.Empty;
        Filter = filter ?? string.Empty;
        FilterIndex = filterIndex;
    }

    public string Kind { get; }

    public string Title { get; }

    public string InitialDirectory { get; }

    public string DefaultDirectory { get; }

    public string SuggestedItemName { get; }

    public string DefaultExtension { get; }

    public string Filter { get; }

    public int FilterIndex { get; }
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
    PortableWpfServiceKey ServiceKey { get; }

    void Register(PortableWindowActivationCallbacks callbacks);

    bool TryRegisterMediaContextRenderService(
        object window,
        Action<object?, TimeSpan> requestRender,
        out IDisposable? registration)
    {
        registration = null;
        return false;
    }

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
    private static readonly Dictionary<PortableWpfServiceKey, IPortableWindowActivationServiceRegistrar> WindowActivationServices = new();
    private static readonly Dictionary<PortableWpfServiceKey, IPortableClipboardServiceRegistrar> ClipboardServices = new();
    private static readonly Dictionary<PortableWpfServiceKey, IPortableLauncherServiceRegistrar> LauncherServices = new();
    private static readonly Dictionary<PortableWpfServiceKey, IPortableMessageBoxServiceRegistrar> MessageBoxServices = new();
    private static readonly Dictionary<PortableWpfServiceKey, IPortableFileDialogServiceRegistrar> FileDialogServices = new();

    public static IDisposable RegisterWindowActivationService(IPortableWindowActivationServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(service);
        ValidateServiceKey(service.ServiceKey, nameof(service));

        lock (SyncRoot)
        {
            WindowActivationServices[service.ServiceKey] = service;
        }

        return new Registration<IPortableWindowActivationServiceRegistrar>(service, WindowActivationServices);
    }

    public static bool TryGetWindowActivationService(
        PortableWpfServiceKey serviceKey,
        out IPortableWindowActivationServiceRegistrar service)
    {
        ValidateServiceKey(serviceKey, nameof(serviceKey));

        lock (SyncRoot)
        {
            return WindowActivationServices.TryGetValue(serviceKey, out service!);
        }
    }

    public static IDisposable RegisterClipboardService(IPortableClipboardServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(service);
        ValidateServiceKey(service.ServiceKey, nameof(service));

        lock (SyncRoot)
        {
            ClipboardServices[service.ServiceKey] = service;
        }

        return new Registration<IPortableClipboardServiceRegistrar>(service, ClipboardServices);
    }

    public static bool TryGetClipboardService(
        PortableWpfServiceKey serviceKey,
        out IPortableClipboardServiceRegistrar service)
    {
        ValidateServiceKey(serviceKey, nameof(serviceKey));

        lock (SyncRoot)
        {
            return ClipboardServices.TryGetValue(serviceKey, out service!);
        }
    }

    public static IDisposable RegisterLauncherService(IPortableLauncherServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(service);
        ValidateServiceKey(service.ServiceKey, nameof(service));

        lock (SyncRoot)
        {
            LauncherServices[service.ServiceKey] = service;
        }

        return new Registration<IPortableLauncherServiceRegistrar>(service, LauncherServices);
    }

    public static bool TryGetLauncherService(
        PortableWpfServiceKey serviceKey,
        out IPortableLauncherServiceRegistrar service)
    {
        ValidateServiceKey(serviceKey, nameof(serviceKey));

        lock (SyncRoot)
        {
            return LauncherServices.TryGetValue(serviceKey, out service!);
        }
    }

    public static IDisposable RegisterMessageBoxService(IPortableMessageBoxServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(service);
        ValidateServiceKey(service.ServiceKey, nameof(service));

        lock (SyncRoot)
        {
            MessageBoxServices[service.ServiceKey] = service;
        }

        return new Registration<IPortableMessageBoxServiceRegistrar>(service, MessageBoxServices);
    }

    public static bool TryGetMessageBoxService(
        PortableWpfServiceKey serviceKey,
        out IPortableMessageBoxServiceRegistrar service)
    {
        ValidateServiceKey(serviceKey, nameof(serviceKey));

        lock (SyncRoot)
        {
            return MessageBoxServices.TryGetValue(serviceKey, out service!);
        }
    }

    public static IDisposable RegisterFileDialogService(IPortableFileDialogServiceRegistrar service)
    {
        ArgumentNullException.ThrowIfNull(service);
        ValidateServiceKey(service.ServiceKey, nameof(service));

        lock (SyncRoot)
        {
            FileDialogServices[service.ServiceKey] = service;
        }

        return new Registration<IPortableFileDialogServiceRegistrar>(service, FileDialogServices);
    }

    public static bool TryGetFileDialogService(
        PortableWpfServiceKey serviceKey,
        out IPortableFileDialogServiceRegistrar service)
    {
        ValidateServiceKey(serviceKey, nameof(serviceKey));

        lock (SyncRoot)
        {
            return FileDialogServices.TryGetValue(serviceKey, out service!);
        }
    }

    private static void ValidateServiceKey(PortableWpfServiceKey serviceKey, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(serviceKey.Name))
        {
            throw new ArgumentException("Portable WPF service keys must have a non-empty name.", parameterName);
        }
    }

    private sealed class Registration<TService> : IDisposable
        where TService : class
    {
        private readonly Dictionary<PortableWpfServiceKey, TService> _services;
        private TService? _service;

        public Registration(TService service, Dictionary<PortableWpfServiceKey, TService> services)
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
                var serviceKey = GetServiceKey(service);
                if (_services.TryGetValue(serviceKey, out var current) &&
                    ReferenceEquals(current, service))
                {
                    _services.Remove(serviceKey);
                }
            }
        }

        private static PortableWpfServiceKey GetServiceKey(TService service)
        {
            return service switch
            {
                IPortableWindowActivationServiceRegistrar windowActivationService => windowActivationService.ServiceKey,
                IPortableClipboardServiceRegistrar clipboardService => clipboardService.ServiceKey,
                IPortableLauncherServiceRegistrar launcherService => launcherService.ServiceKey,
                IPortableMessageBoxServiceRegistrar messageBoxService => messageBoxService.ServiceKey,
                IPortableFileDialogServiceRegistrar fileDialogService => fileDialogService.ServiceKey,
                _ => throw new InvalidOperationException("Unsupported portable WPF service registrar.")
            };
        }
    }
}
