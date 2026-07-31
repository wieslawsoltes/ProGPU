using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation.Metadata;

namespace Microsoft.UI.Content;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public enum ContentLayoutDirection
{
    LeftToRight = 0,
    RightToLeft = 1
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public interface IContentSiteBridge : IDisposable
{
    DispatcherQueue DispatcherQueue { get; }

    ContentLayoutDirection? LayoutDirectionOverride { get; set; }

    float OverrideScale { get; set; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010007)]
public interface IContentSiteLink
{
    ContentIsland Parent { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public class ContentIsland :
    Microsoft.UI.IClosableNotifier,
    IDisposable
{
    private static long s_nextId;
    private bool _isClosed;

    protected internal ContentIsland(
        WinRT.IObjectReference objRef)
        : this(RequireDispatcherQueue())
    {
        ArgumentNullException.ThrowIfNull(objRef);
    }

    protected ContentIsland(
        WinRT.DerivedComposed _)
        : this(RequireDispatcherQueue())
    {
        ArgumentNullException.ThrowIfNull(_);
    }

    internal ContentIsland(
        DispatcherQueue dispatcherQueue)
    {
        DispatcherQueue = dispatcherQueue ??
            throw new ArgumentNullException(
                nameof(dispatcherQueue));
        Id = unchecked((ulong)Interlocked.Increment(
            ref s_nextId));
    }

    public DispatcherQueue DispatcherQueue { get; }

    public ulong Id { get; }

    public bool IsClosed => _isClosed;

    public event Microsoft.UI.ClosableNotifierHandler? Closed;

    public event Microsoft.UI.ClosableNotifierHandler? FrameworkClosed;

    internal WindowInputState? InputState { get; private set; }

    internal InputFocusController? FocusController { get; set; }

    internal InputKeyboardSource? KeyboardSource { get; set; }

    internal InputFocusNavigationHost? FocusNavigationHost { get; set; }

    internal InputActivationListener? ActivationListener { get; set; }

    internal InputPreTranslateKeyboardSource?
        PreTranslateKeyboardSource { get; set; }

    internal void VerifyAccess()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException(
                "ContentIsland must be accessed from its dispatcher thread.");
        }
    }

    internal void AttachInputState(
        WindowInputState state)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_isClosed, this);
        ArgumentNullException.ThrowIfNull(state);
        if (state.ContentIsland is not null &&
            !ReferenceEquals(state.ContentIsland, this))
        {
            throw new InvalidOperationException(
                "The input state is already attached to another ContentIsland.");
        }
        if (InputState is not null &&
            !ReferenceEquals(InputState, state) &&
            ReferenceEquals(InputState.ContentIsland, this))
        {
            InputState.ContentIsland = null;
        }
        InputState = state;
        state.ContentIsland = this;
        FocusController?.Attach(state);
        KeyboardSource?.Attach(state);
        ActivationListener?.Attach(state);
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_isClosed)
            return;

        _isClosed = true;
        FocusController?.Detach();
        KeyboardSource?.Detach();
        FocusNavigationHost?.Detach();
        ActivationListener?.Detach();
        PreTranslateKeyboardSource?.Detach();
        FocusController = null;
        KeyboardSource = null;
        FocusNavigationHost = null;
        ActivationListener = null;
        PreTranslateKeyboardSource = null;
        if (InputState is not null &&
            ReferenceEquals(InputState.ContentIsland, this))
        {
            InputState.ContentIsland = null;
            InputState.ContentIslandFocusProvider = null;
        }
        InputState = null;
        Microsoft.UI.ClosableNotifierHandler?
            frameworkClosed = FrameworkClosed;
        Microsoft.UI.ClosableNotifierHandler?
            closed = Closed;
        Closed = null;
        FrameworkClosed = null;
        try
        {
            frameworkClosed?.Invoke();
        }
        finally
        {
            closed?.Invoke();
        }
    }

    private static DispatcherQueue RequireDispatcherQueue() =>
        DispatcherQueue.GetForCurrentThread() ??
        throw new InvalidOperationException(
            "ContentIsland requires a DispatcherQueue on the current thread.");
}
