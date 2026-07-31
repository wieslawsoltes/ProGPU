using System.Numerics;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics;

namespace Microsoft.UI.Content;

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public class ContentSite :
    Microsoft.UI.IClosableNotifier,
    IDisposable
{
    private readonly ContentSiteCore _core;
    private int _isClosed;

    protected internal ContentSite(
        WinRT.IObjectReference objRef)
        : this(RequireDispatcherQueue())
    {
        ArgumentNullException.ThrowIfNull(objRef);
    }

    protected ContentSite(
        WinRT.DerivedComposed _)
        : this(RequireDispatcherQueue())
    {
        ArgumentNullException.ThrowIfNull(_);
    }

    internal ContentSite(
        DispatcherQueue dispatcherQueue)
    {
        _core = new ContentSiteCore(
            dispatcherQueue ??
            throw new ArgumentNullException(
                nameof(dispatcherQueue)));
        var view = new ContentSiteView(_core);
        _core.AttachView(view);
    }

    public Vector2 ActualSize
    {
        get => _core.Read().ActualSize;
        set => _core.SetActualSize(value);
    }

    public SizeInt32 ClientSize
    {
        get => _core.Read().ClientSize;
        set => _core.SetClientSize(value);
    }

    public ContentCoordinateConverter CoordinateConverter =>
        _core.CoordinateConverter;

    public DispatcherQueue DispatcherQueue =>
        _core.DispatcherQueue;

    public ContentSiteEnvironment Environment =>
        _core.Environment;

    public bool IsClosed =>
        Volatile.Read(ref _isClosed) != 0;

    public bool IsConnected =>
        _core.Read().IsConnected;

    public bool IsSiteEnabled
    {
        get => _core.Read().IsSiteEnabled;
        set => _core.SetSiteEnabled(value);
    }

    public bool IsSiteVisible
    {
        get => _core.Read().IsSiteVisible;
        set => _core.SetSiteVisible(value);
    }

    public ContentLayoutDirection LayoutDirection
    {
        get => _core.Read().LayoutDirection;
        set => _core.SetLayoutDirection(value);
    }

    public Matrix4x4 LocalToClientTransformMatrix =>
        _core.Read().LocalToClientTransformMatrix;

    public Matrix4x4 LocalToParentTransformMatrix
    {
        get =>
            _core.Read()
                .LocalToParentTransformMatrix;
        set =>
            _core.SetLocalToParentTransformMatrix(
                value);
    }

    public float OverrideScale
    {
        get => _core.Read().OverrideScale;
        set => _core.SetOverrideScale(value);
    }

    public float ParentScale
    {
        get => _core.Read().ParentScale;
        set => _core.SetParentScale(value);
    }

    public bool ProcessesKeyboardInput
    {
        get =>
            _core.Read()
                .ProcessesKeyboardInput;
        set =>
            _core.SetProcessesKeyboardInput(
                value);
    }

    public bool ProcessesPointerInput
    {
        get =>
            _core.Read()
                .ProcessesPointerInput;
        set =>
            _core.SetProcessesPointerInput(
                value);
    }

    public float RasterizationScale =>
        _core.Read().RasterizationScale;

    public Vector2 RequestedSize =>
        _core.Read().RequestedSize;

    public bool ShouldApplyRasterizationScale
    {
        get =>
            _core.Read()
                .ShouldApplyRasterizationScale;
        set =>
            _core.SetShouldApplyRasterizationScale(
                value);
    }

    public ContentSiteView View =>
        _core.View;

    public event Microsoft.UI.ClosableNotifierHandler?
        Closed;

    public event Microsoft.UI.ClosableNotifierHandler?
        FrameworkClosed;

    public event TypedEventHandler<
        ContentSite,
        ContentSiteRequestedStateChangedEventArgs>?
        RequestedStateChanged;

    internal event Action<
        ContentSiteSnapshot,
        ContentSiteChangeFlags> StateChanged
    {
        add => _core.StateChanged += value;
        remove => _core.StateChanged -= value;
    }

    public ContentDeferral
        GetIslandStateChangeDeferral() =>
        _core.GetIslandStateChangeDeferral();

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _isClosed,
                1) != 0)
        {
            return;
        }

        _core.Close();
        Microsoft.UI.ClosableNotifierHandler?
            frameworkClosed = FrameworkClosed;
        Microsoft.UI.ClosableNotifierHandler?
            closed = Closed;
        FrameworkClosed = null;
        Closed = null;
        RequestedStateChanged = null;
        try
        {
            frameworkClosed?.Invoke();
        }
        finally
        {
            closed?.Invoke();
        }
    }

    internal void SetAutomationOption(
        ContentAutomationOptions value) =>
        _core.SetAutomationOption(value);

    internal void SetConnected(bool value) =>
        _core.SetConnected(value);

    internal void SetLocalToClientTransformMatrix(
        Matrix4x4 value) =>
        _core.SetLocalToClientTransformMatrix(
            value);

    internal void SetRequestedSize(
        Vector2 value)
    {
        if (_core.SetRequestedSize(value))
        {
            RequestedStateChanged?.Invoke(
                this,
                new
                    ContentSiteRequestedStateChangedEventArgs(
                        didRequestedSizeChange:
                            true));
        }
    }

    private static DispatcherQueue
        RequireDispatcherQueue() =>
        DispatcherQueue.GetForCurrentThread() ??
        throw new InvalidOperationException(
            "ContentSite requires a DispatcherQueue on the current thread.");
}

[ContractVersion(
    "Microsoft.Foundation.WindowsAppSDKContract",
    0x00010004)]
public class ContentSiteView
{
    private readonly ContentSiteCore _core;

    protected internal ContentSiteView(
        WinRT.IObjectReference objRef)
        : this(CreateStandaloneCore())
    {
        ArgumentNullException.ThrowIfNull(objRef);
        _core.AttachView(this);
    }

    protected ContentSiteView(
        WinRT.DerivedComposed _)
        : this(CreateStandaloneCore())
    {
        ArgumentNullException.ThrowIfNull(_);
        _core.AttachView(this);
    }

    internal ContentSiteView(
        ContentSiteCore core)
    {
        _core = core ??
            throw new ArgumentNullException(
                nameof(core));
    }

    public Vector2 ActualSize =>
        _core.Read().ActualSize;

    public ContentAutomationOptions AutomationOption =>
        _core.Read().AutomationOption;

    public SizeInt32 ClientSize =>
        _core.Read().ClientSize;

    public ContentCoordinateConverter CoordinateConverter =>
        _core.CoordinateConverter;

    public DispatcherQueue DispatcherQueue =>
        _core.DispatcherQueue;

    public ContentSiteEnvironmentView EnvironmentView =>
        _core.Environment.View;

    public bool IsConnected =>
        _core.Read().IsConnected;

    public bool IsSiteEnabled =>
        _core.Read().IsSiteEnabled;

    public bool IsSiteVisible =>
        _core.Read().IsSiteVisible;

    public ContentLayoutDirection LayoutDirection =>
        _core.Read().LayoutDirection;

    public Matrix4x4 LocalToClientTransformMatrix =>
        _core.Read().LocalToClientTransformMatrix;

    public Matrix4x4 LocalToParentTransformMatrix =>
        _core.Read().LocalToParentTransformMatrix;

    public float OverrideScale =>
        _core.Read().OverrideScale;

    public float ParentScale =>
        _core.Read().ParentScale;

    public bool ProcessesKeyboardInput =>
        _core.Read().ProcessesKeyboardInput;

    public bool ProcessesPointerInput =>
        _core.Read().ProcessesPointerInput;

    public float RasterizationScale =>
        _core.Read().RasterizationScale;

    public Vector2 RequestedSize =>
        _core.Read().RequestedSize;

    public bool ShouldApplyRasterizationScale =>
        _core.Read().ShouldApplyRasterizationScale;

    private static ContentSiteCore
        CreateStandaloneCore()
    {
        DispatcherQueue queue =
            DispatcherQueue.GetForCurrentThread() ??
            throw new InvalidOperationException(
                "ContentSiteView requires a DispatcherQueue on the current thread.");
        return new ContentSiteCore(queue);
    }
}

[Flags]
internal enum ContentSiteChangeFlags : ushort
{
    None = 0,
    ActualSize = 1 << 0,
    ClientSize = 1 << 1,
    IsConnected = 1 << 2,
    IsSiteEnabled = 1 << 3,
    IsSiteVisible = 1 << 4,
    LayoutDirection = 1 << 5,
    LocalToClientTransform = 1 << 6,
    LocalToParentTransform = 1 << 7,
    OverrideScale = 1 << 8,
    ParentScale = 1 << 9,
    ProcessesKeyboardInput = 1 << 10,
    ProcessesPointerInput = 1 << 11,
    ShouldApplyRasterizationScale = 1 << 12,
    AutomationOption = 1 << 13
}

internal sealed class ContentSiteCore :
    IContentCoordinateTransformSource
{
    private readonly object _deferralSync = new();
    private ContentSiteSnapshot _snapshot =
        ContentSiteSnapshot.Default;
    private int _deferralCount;
    private int _deferralGeneration;
    private int _isClosed;
    private ContentSiteChangeFlags _deferredChanges;

    internal ContentSiteCore(
        DispatcherQueue dispatcherQueue)
    {
        DispatcherQueue = dispatcherQueue;
        Environment = new ContentSiteEnvironment();
        CoordinateConverter =
            new ContentCoordinateConverter(this);
    }

    internal DispatcherQueue DispatcherQueue { get; }

    internal ContentSiteEnvironment Environment { get; }

    internal ContentCoordinateConverter CoordinateConverter { get; }

    internal ContentSiteView View { get; private set; } = null!;

    internal event Action<
        ContentSiteSnapshot,
        ContentSiteChangeFlags>? StateChanged;

    internal void AttachView(
        ContentSiteView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (View is not null &&
            !ReferenceEquals(View, view))
        {
            throw new InvalidOperationException(
                "A ContentSiteCore can own only one ContentSiteView.");
        }

        View = view;
    }

    internal ContentSiteSnapshot Read() =>
        Volatile.Read(ref _snapshot);

    internal void SetActualSize(Vector2 value)
    {
        ValidateSize(value, nameof(value));
        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot.ActualSize == nextValue
                    ? snapshot
                    : snapshot with
                    {
                        ActualSize = nextValue
                    },
            ContentSiteChangeFlags.ActualSize);
    }

    internal void SetClientSize(SizeInt32 value)
    {
        if (value.Width < 0 ||
            value.Height < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value));
        }

        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot.ClientSize == nextValue
                    ? snapshot
                    : snapshot with
                    {
                        ClientSize = nextValue
                    },
            ContentSiteChangeFlags.ClientSize);
    }

    internal void SetConnected(bool value)
    {
        if (!value)
            CancelDeferrals();

        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot.IsConnected == nextValue
                    ? snapshot
                    : snapshot with
                    {
                        IsConnected = nextValue
                    },
            ContentSiteChangeFlags.IsConnected);
    }

    internal void SetSiteEnabled(bool value) =>
        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot.IsSiteEnabled == nextValue
                    ? snapshot
                    : snapshot with
                    {
                        IsSiteEnabled = nextValue
                    },
            ContentSiteChangeFlags.IsSiteEnabled);

    internal void SetSiteVisible(bool value) =>
        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot.IsSiteVisible == nextValue
                    ? snapshot
                    : snapshot with
                    {
                        IsSiteVisible = nextValue
                    },
            ContentSiteChangeFlags.IsSiteVisible);

    internal void SetLayoutDirection(
        ContentLayoutDirection value)
    {
        if (value < ContentLayoutDirection.LeftToRight ||
            value > ContentLayoutDirection.RightToLeft)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value));
        }

        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot.LayoutDirection ==
                    nextValue
                    ? snapshot
                    : snapshot with
                    {
                        LayoutDirection =
                            nextValue
                    },
            ContentSiteChangeFlags.LayoutDirection);
    }

    internal void SetLocalToClientTransformMatrix(
        Matrix4x4 value)
    {
        ValidateMatrix(value, nameof(value));
        ValidateTwoDimensionalAffine(value);
        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot
                        .LocalToClientTransformMatrix ==
                    nextValue
                    ? snapshot
                    : snapshot with
                    {
                        LocalToClientTransformMatrix =
                            nextValue
                    },
            ContentSiteChangeFlags
                .LocalToClientTransform);
    }

    internal void SetLocalToParentTransformMatrix(
        Matrix4x4 value)
    {
        ValidateMatrix(value, nameof(value));
        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot
                        .LocalToParentTransformMatrix ==
                    nextValue
                    ? snapshot
                    : snapshot with
                    {
                        LocalToParentTransformMatrix =
                            nextValue
                    },
            ContentSiteChangeFlags
                .LocalToParentTransform);
    }

    internal void SetOverrideScale(float value)
    {
        if (!float.IsFinite(value) ||
            value < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value));
        }

        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot.OverrideScale == nextValue
                    ? snapshot
                    : snapshot with
                    {
                        OverrideScale = nextValue
                    },
            ContentSiteChangeFlags.OverrideScale);
    }

    internal void SetParentScale(float value)
    {
        if (!float.IsFinite(value) ||
            value <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value));
        }

        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot.ParentScale == nextValue
                    ? snapshot
                    : snapshot with
                    {
                        ParentScale = nextValue
                    },
            ContentSiteChangeFlags.ParentScale);
    }

    internal void SetProcessesKeyboardInput(
        bool value) =>
        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot.ProcessesKeyboardInput ==
                    nextValue
                    ? snapshot
                    : snapshot with
                    {
                        ProcessesKeyboardInput =
                            nextValue
                    },
            ContentSiteChangeFlags
                .ProcessesKeyboardInput);

    internal void SetProcessesPointerInput(
        bool value) =>
        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot.ProcessesPointerInput ==
                    nextValue
                    ? snapshot
                    : snapshot with
                    {
                        ProcessesPointerInput =
                            nextValue
                    },
            ContentSiteChangeFlags
                .ProcessesPointerInput);

    internal void SetShouldApplyRasterizationScale(
        bool value) =>
        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot
                        .ShouldApplyRasterizationScale ==
                    nextValue
                    ? snapshot
                    : snapshot with
                    {
                        ShouldApplyRasterizationScale =
                            nextValue
                    },
            ContentSiteChangeFlags
                .ShouldApplyRasterizationScale);

    internal void SetAutomationOption(
        ContentAutomationOptions value)
    {
        if (value < ContentAutomationOptions.None ||
            value >
                ContentAutomationOptions.FragmentBased)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value));
        }

        Update(
            value,
            static (snapshot, nextValue) =>
                snapshot.AutomationOption ==
                    nextValue
                    ? snapshot
                    : snapshot with
                    {
                        AutomationOption = nextValue
                    },
            ContentSiteChangeFlags.AutomationOption);
    }

    internal bool SetRequestedSize(Vector2 value)
    {
        ThrowIfClosed();
        ValidateSize(value, nameof(value));
        while (true)
        {
            ContentSiteSnapshot current = Read();
            if (current.RequestedSize == value)
                return false;

            ContentSiteSnapshot next =
                current with
                {
                    RequestedSize = value
                };
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _snapshot,
                        next,
                        current),
                    current))
            {
                return true;
            }
        }
    }

    internal ContentDeferral
        GetIslandStateChangeDeferral()
    {
        if (Volatile.Read(ref _isClosed) != 0)
            return null!;

        ContentSiteSnapshot snapshot = Read();
        if (!snapshot.IsConnected)
            return null!;

        int generation;
        lock (_deferralSync)
        {
            if (!Read().IsConnected)
                return null!;
            generation = _deferralGeneration;
            _deferralCount++;
        }

        return new ContentDeferral(
            DispatcherQueue,
            () => CompleteDeferral(generation));
    }

    internal void Close()
    {
        if (Volatile.Read(ref _isClosed) != 0)
            return;

        SetConnected(false);
        Interlocked.Exchange(ref _isClosed, 1);
        StateChanged = null;
        CancelDeferrals();
    }

    Matrix3x2
        IContentCoordinateTransformSource
            .GetLocalToScreenTransform()
    {
        ContentSiteSnapshot snapshot = Read();
        Matrix4x4 localToClient =
            snapshot.LocalToClientTransformMatrix;
        var localToClient2D =
            new Matrix3x2(
                localToClient.M11,
                localToClient.M12,
                localToClient.M21,
                localToClient.M22,
                localToClient.M41,
                localToClient.M42);
        Matrix3x2 clientToScreen =
            ContentCoordinateConverter
                .GetWindowLocalToScreenTransform(
                    Environment.AppWindowId);
        return localToClient2D *
            clientToScreen;
    }

    private bool Update<T>(
        T value,
        Func<
            ContentSiteSnapshot,
            T,
            ContentSiteSnapshot>
            update,
        ContentSiteChangeFlags changes)
    {
        ThrowIfClosed();
        while (true)
        {
            ContentSiteSnapshot current = Read();
            ContentSiteSnapshot next =
                update(current, value);
            if (next == current)
                return false;

            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _snapshot,
                        next,
                        current),
                    current))
            {
                PublishChange(next, changes);
                return true;
            }
        }
    }

    private void PublishChange(
        ContentSiteSnapshot snapshot,
        ContentSiteChangeFlags changes)
    {
        lock (_deferralSync)
        {
            if (_deferralCount > 0)
            {
                _deferredChanges |= changes;
                return;
            }
        }

        StateChanged?.Invoke(snapshot, changes);
    }

    private void CompleteDeferral(int generation)
    {
        ContentSiteChangeFlags changes;
        lock (_deferralSync)
        {
            if (generation != _deferralGeneration ||
                _deferralCount == 0)
            {
                return;
            }

            _deferralCount--;
            if (_deferralCount != 0)
                return;
            changes = _deferredChanges;
            _deferredChanges =
                ContentSiteChangeFlags.None;
        }

        if (changes != ContentSiteChangeFlags.None)
            StateChanged?.Invoke(Read(), changes);
    }

    private void CancelDeferrals()
    {
        lock (_deferralSync)
        {
            _deferralGeneration++;
            _deferralCount = 0;
            _deferredChanges =
                ContentSiteChangeFlags.None;
        }
    }

    private void ThrowIfClosed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _isClosed) != 0,
            this);

    private static void ValidateSize(
        Vector2 value,
        string parameterName)
    {
        if (!float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            value.X < 0f ||
            value.Y < 0f)
        {
            throw new ArgumentOutOfRangeException(
                parameterName);
        }
    }

    private static void ValidateMatrix(
        in Matrix4x4 value,
        string parameterName)
    {
        if (!float.IsFinite(value.M11) ||
            !float.IsFinite(value.M12) ||
            !float.IsFinite(value.M13) ||
            !float.IsFinite(value.M14) ||
            !float.IsFinite(value.M21) ||
            !float.IsFinite(value.M22) ||
            !float.IsFinite(value.M23) ||
            !float.IsFinite(value.M24) ||
            !float.IsFinite(value.M31) ||
            !float.IsFinite(value.M32) ||
            !float.IsFinite(value.M33) ||
            !float.IsFinite(value.M34) ||
            !float.IsFinite(value.M41) ||
            !float.IsFinite(value.M42) ||
            !float.IsFinite(value.M43) ||
            !float.IsFinite(value.M44))
        {
            throw new ArgumentOutOfRangeException(
                parameterName);
        }
    }

    private static void
        ValidateTwoDimensionalAffine(
            in Matrix4x4 value)
    {
        if (value.M13 != 0f ||
            value.M14 != 0f ||
            value.M23 != 0f ||
            value.M24 != 0f ||
            value.M31 != 0f ||
            value.M32 != 0f ||
            value.M34 != 0f ||
            value.M43 != 0f ||
            value.M33 != 1f ||
            value.M44 != 1f)
        {
            throw new ArgumentException(
                "The local-to-client coordinate transform must be a two-dimensional affine matrix.",
                nameof(value));
        }
    }
}

internal sealed record ContentSiteSnapshot(
    Vector2 ActualSize,
    ContentAutomationOptions AutomationOption,
    SizeInt32 ClientSize,
    bool IsConnected,
    bool IsSiteEnabled,
    bool IsSiteVisible,
    ContentLayoutDirection LayoutDirection,
    Matrix4x4 LocalToClientTransformMatrix,
    Matrix4x4 LocalToParentTransformMatrix,
    float OverrideScale,
    float ParentScale,
    bool ProcessesKeyboardInput,
    bool ProcessesPointerInput,
    Vector2 RequestedSize,
    bool ShouldApplyRasterizationScale)
{
    internal static ContentSiteSnapshot Default { get; } =
        new(
            Vector2.Zero,
            ContentAutomationOptions.None,
            default,
            IsConnected: false,
            IsSiteEnabled: true,
            IsSiteVisible: true,
            ContentLayoutDirection.LeftToRight,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            OverrideScale: 0f,
            ParentScale: 1f,
            ProcessesKeyboardInput: true,
            ProcessesPointerInput: true,
            RequestedSize: Vector2.Zero,
            ShouldApplyRasterizationScale: true);

    internal float RasterizationScale =>
        OverrideScale > 0f
            ? OverrideScale
            : ParentScale;
}
