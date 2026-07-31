using Microsoft.UI.Content;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using ProGPU.WinUI.Platform;
using System.Runtime.CompilerServices;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.System;
using Windows.UI.Core;

namespace Microsoft.UI.Input;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public class InputObject
{
    protected internal InputObject(
        WinRT.IObjectReference objRef)
        : this(RequireDispatcherQueue())
    {
        ArgumentNullException.ThrowIfNull(objRef);
    }

    protected InputObject(
        WinRT.DerivedComposed _)
        : this(RequireDispatcherQueue())
    {
        ArgumentNullException.ThrowIfNull(_);
    }

    internal InputObject(
        DispatcherQueue dispatcherQueue)
    {
        DispatcherQueue = dispatcherQueue ??
            throw new ArgumentNullException(
                nameof(dispatcherQueue));
    }

    public DispatcherQueue DispatcherQueue { get; }

    internal void VerifyAccess()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException(
                "InputObject must be accessed from its dispatcher thread.");
        }
    }

    private static DispatcherQueue RequireDispatcherQueue() =>
        DispatcherQueue.GetForCurrentThread() ??
        throw new InvalidOperationException(
            "InputObject requires a DispatcherQueue on the current thread.");
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class CharacterReceivedEventArgs
{
    internal CharacterReceivedEventArgs(
        uint keyCode,
        PhysicalKeyStatus keyStatus)
    {
        KeyCode = keyCode;
        KeyStatus = keyStatus;
    }

    public bool Handled { get; set; }

    public uint KeyCode { get; }

    public PhysicalKeyStatus KeyStatus { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class ContextMenuKeyEventArgs
{
    internal ContextMenuKeyEventArgs()
    {
    }

    public bool Handled { get; set; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class FocusChangedEventArgs
{
    internal FocusChangedEventArgs()
    {
    }

    public bool Handled { get; set; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010005)]
public sealed class FocusNavigationRequest
{
    private FocusNavigationRequest(
        FocusNavigationReason reason,
        Rect? hintRect,
        Guid correlationId)
    {
        Reason = reason;
        HintRect = hintRect;
        CorrelationId = correlationId;
    }

    public Guid CorrelationId { get; }

    public Rect? HintRect { get; }

    public FocusNavigationReason Reason { get; }

    public static FocusNavigationRequest Create(
        FocusNavigationReason reason) =>
        new(reason, null, Guid.Empty);

    public static FocusNavigationRequest Create(
        FocusNavigationReason reason,
        Rect hintRect) =>
        new(reason, hintRect, Guid.Empty);

    public static FocusNavigationRequest Create(
        FocusNavigationReason reason,
        Rect hintRect,
        Guid correlationId) =>
        new(reason, hintRect, correlationId);
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010005)]
public sealed class FocusNavigationRequestEventArgs
{
    internal FocusNavigationRequestEventArgs(
        FocusNavigationRequest request)
    {
        Request = request;
    }

    public FocusNavigationRequest Request { get; }

    public FocusNavigationResult Result { get; set; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class KeyEventArgs
{
    internal KeyEventArgs(
        VirtualKey virtualKey,
        PhysicalKeyStatus keyStatus,
        ulong timestamp)
    {
        VirtualKey = virtualKey;
        KeyStatus = keyStatus;
        Timestamp = timestamp;
    }

    public bool Handled { get; set; }

    public PhysicalKeyStatus KeyStatus { get; }

    public ulong Timestamp { get; }

    public VirtualKey VirtualKey { get; }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public sealed class InputFocusController :
    InputObject
{
    private readonly ContentIsland _island;
    private WindowInputState? _state;
    private bool _hasFocus;

    private InputFocusController(
        ContentIsland island)
        : base(island.DispatcherQueue)
    {
        _island = island;
        Attach(island.InputState);
    }

    public bool HasFocus
    {
        get
        {
            VerifyAccess();
            return _hasFocus;
        }
    }

    public bool ShouldShowKeyboardCues
    {
        get
        {
            VerifyAccess();
            return _state?.IsKeyboardFocusActive == true;
        }
    }

    public event TypedEventHandler<
        InputFocusController,
        FocusChangedEventArgs>? GotFocus;

    public event TypedEventHandler<
        InputFocusController,
        FocusChangedEventArgs>? LostFocus;

    public event TypedEventHandler<
        InputFocusController,
        FocusNavigationRequestEventArgs>?
        NavigateFocusRequested;

    public static InputFocusController GetForIsland(
        ContentIsland island)
    {
        ArgumentNullException.ThrowIfNull(island);
        island.VerifyAccess();
        ObjectDisposedException.ThrowIf(
            island.IsClosed,
            island);
        return island.FocusController ??=
            new InputFocusController(island);
    }

    public FocusNavigationResult DepartFocus(
        FocusNavigationRequest request)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(request);
        return _island.FocusNavigationHost?
            .RaiseDepartFocusRequested(request) ??
            FocusNavigationResult.NotMoved;
    }

    public bool TrySetFocus()
    {
        VerifyAccess();
        if (_state is null)
            return false;

        bool accepted =
            _state.ContentIslandFocusProvider?
                .TrySetFocus(_island, _state) ??
            true;
        if (accepted)
            InputSystem.SetHostFocus(_state, true);
        return accepted;
    }

    internal FocusNavigationResult RaiseNavigateFocusRequested(
        FocusNavigationRequest request)
    {
        VerifyAccess();
        TypedEventHandler<
            InputFocusController,
            FocusNavigationRequestEventArgs>? handler =
            NavigateFocusRequested;
        if (handler is null)
            return FocusNavigationResult.NotMoved;

        var args = new FocusNavigationRequestEventArgs(request);
        handler(this, args);
        return args.Result;
    }

    internal void Attach(
        WindowInputState? state)
    {
        if (ReferenceEquals(_state, state))
            return;
        if (_state is not null)
            _state.HostFocusChanged -= OnHostFocusChanged;
        _state = state;
        if (_state is not null)
        {
            _hasFocus = _state.HasHostFocus;
            _state.HostFocusChanged += OnHostFocusChanged;
        }
        else
        {
            _hasFocus = false;
        }
    }

    internal void Detach()
    {
        Attach(null);
        GotFocus = null;
        LostFocus = null;
        NavigateFocusRequested = null;
    }

    private void OnHostFocusChanged(
        bool hasFocus)
    {
        if (_hasFocus == hasFocus)
            return;
        _hasFocus = hasFocus;
        TypedEventHandler<
            InputFocusController,
            FocusChangedEventArgs>? handler =
            hasFocus ? GotFocus : LostFocus;
        if (handler is not null)
            handler(this, new FocusChangedEventArgs());
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010005)]
public sealed class InputFocusNavigationHost :
    InputObject
{
    private static readonly ConditionalWeakTable<
        IContentSiteBridge,
        InputFocusNavigationHost> s_bridgeHosts = new();
    private static readonly ConditionalWeakTable<
        IContentSiteLink,
        InputFocusNavigationHost> s_linkHosts = new();

    private ContentIsland? _island;

    private InputFocusNavigationHost(
        ContentIsland island)
        : base(island.DispatcherQueue)
    {
        _island = island;
        island.FocusNavigationHost = this;
    }

    public bool ContainsFocus
    {
        get
        {
            VerifyAccess();
            return _island is not null &&
                InputFocusController
                    .GetForIsland(_island)
                    .HasFocus;
        }
    }

    public event TypedEventHandler<
        InputFocusNavigationHost,
        FocusNavigationRequestEventArgs>?
        DepartFocusRequested;

    public static InputFocusNavigationHost GetForSiteBridge(
        IContentSiteBridge site)
    {
        ArgumentNullException.ThrowIfNull(site);
        ContentIsland island =
            (site as IContentIslandSiteProvider)?
                .ContentIsland ??
            throw new InvalidOperationException(
                "The site bridge is not connected to a ContentIsland.");
        island.VerifyAccess();
        return s_bridgeHosts.GetValue(
            site,
            _ => new InputFocusNavigationHost(island));
    }

    public static InputFocusNavigationHost GetForSiteLink(
        IContentSiteLink contentSiteLink)
    {
        ArgumentNullException.ThrowIfNull(contentSiteLink);
        ContentIsland island = contentSiteLink.Parent ??
            throw new InvalidOperationException(
                "The site link has no parent ContentIsland.");
        island.VerifyAccess();
        return s_linkHosts.GetValue(
            contentSiteLink,
            _ => new InputFocusNavigationHost(island));
    }

    public FocusNavigationResult NavigateFocus(
        FocusNavigationRequest request)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(request);
        return _island is null
            ? FocusNavigationResult.NotMoved
            : InputFocusController
                .GetForIsland(_island)
                .RaiseNavigateFocusRequested(request);
    }

    internal FocusNavigationResult RaiseDepartFocusRequested(
        FocusNavigationRequest request)
    {
        VerifyAccess();
        TypedEventHandler<
            InputFocusNavigationHost,
            FocusNavigationRequestEventArgs>? handler =
            DepartFocusRequested;
        if (handler is null)
            return FocusNavigationResult.NotMoved;
        var args = new FocusNavigationRequestEventArgs(request);
        handler(this, args);
        return args.Result;
    }

    internal void Detach()
    {
        _island = null;
        DepartFocusRequested = null;
    }
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010000)]
public sealed class InputKeyboardSource :
    InputObject
{
    private readonly ContentIsland _island;
    private WindowInputState? _state;

    private InputKeyboardSource(
        ContentIsland island)
        : base(island.DispatcherQueue)
    {
        _island = island;
        Attach(island.InputState);
    }

    public event TypedEventHandler<
        InputKeyboardSource,
        CharacterReceivedEventArgs>?
        CharacterReceived;

    public event TypedEventHandler<
        InputKeyboardSource,
        ContextMenuKeyEventArgs>?
        ContextMenuKey;

    public event TypedEventHandler<
        InputKeyboardSource,
        KeyEventArgs>? KeyDown;

    public event TypedEventHandler<
        InputKeyboardSource,
        KeyEventArgs>? KeyUp;

    public event TypedEventHandler<
        InputKeyboardSource,
        KeyEventArgs>? SystemKeyDown;

    public event TypedEventHandler<
        InputKeyboardSource,
        KeyEventArgs>? SystemKeyUp;

    public static InputKeyboardSource GetForIsland(
        ContentIsland island)
    {
        ArgumentNullException.ThrowIfNull(island);
        island.VerifyAccess();
        ObjectDisposedException.ThrowIf(
            island.IsClosed,
            island);
        return island.KeyboardSource ??=
            new InputKeyboardSource(island);
    }

    public VirtualKeyStates GetCurrentKeyState(
        VirtualKey virtualKey)
    {
        VerifyAccess();
        return _state?.KeyboardState.Get(virtualKey) ??
            VirtualKeyStates.None;
    }

    public VirtualKeyStates GetKeyState(
        VirtualKey virtualKey)
    {
        VerifyAccess();
        return _state?.MessageKeyboardState.Get(virtualKey) ??
            VirtualKeyStates.None;
    }

    public static CoreVirtualKeyStates
        GetKeyStateForCurrentThread(
            VirtualKey virtualKey) =>
        (CoreVirtualKeyStates)
            InputSystem.Current.KeyboardState.Get(virtualKey);

    internal void Attach(
        WindowInputState? state)
    {
        if (_state?.KeyboardSource == this)
            _state.KeyboardSource = null;
        _state = state;
        if (_state is not null)
            _state.KeyboardSource = this;
    }

    internal void Detach()
    {
        if (_state?.KeyboardSource == this)
            _state.KeyboardSource = null;
        _state = null;
        CharacterReceived = null;
        ContextMenuKey = null;
        KeyDown = null;
        KeyUp = null;
        SystemKeyDown = null;
        SystemKeyUp = null;
    }

    internal bool RaiseKey(
        in ProGPU.WinUI.Input.KeyboardInputEvent input)
    {
        VerifyAccess();
        TypedEventHandler<
            InputKeyboardSource,
            KeyEventArgs>? handler =
            input.IsSystemKey
                ? input.IsReleased
                    ? SystemKeyUp
                    : SystemKeyDown
                : input.IsReleased
                    ? KeyUp
                    : KeyDown;
        if (handler is null)
            return false;
        var args = new KeyEventArgs(
            input.VirtualKey,
            input.KeyStatus,
            input.Timestamp);
        handler(this, args);
        return args.Handled;
    }

    internal bool RaiseCharacter(
        uint keyCode,
        PhysicalKeyStatus status)
    {
        VerifyAccess();
        TypedEventHandler<
            InputKeyboardSource,
            CharacterReceivedEventArgs>? handler =
            CharacterReceived;
        if (handler is null)
            return false;
        var args =
            new CharacterReceivedEventArgs(
                keyCode,
                status);
        handler(this, args);
        return args.Handled;
    }

    internal bool RaiseContextMenuKey()
    {
        VerifyAccess();
        TypedEventHandler<
            InputKeyboardSource,
            ContextMenuKeyEventArgs>? handler =
            ContextMenuKey;
        if (handler is null)
            return false;
        var args = new ContextMenuKeyEventArgs();
        handler(this, args);
        return args.Handled;
    }
}
