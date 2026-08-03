using System.Numerics;
using ProGPU.Backend;
using ProGPU.Media.Playback;
using ProGPU.Scene;

namespace ProGPU.Media.Rendering;

/// <summary>
/// Framework-neutral controller for Avalonia, LibreWPF, LibreWinForms, and
/// custom hosts. Frame notifications are coalesced onto the captured owner
/// context while retained drawing records the current native texture lease.
/// A frame event is O(1) time/storage and at most one UI dispatch is pending;
/// recording has the same cost and ownership behavior as DrawLatestFrame.
/// </summary>
public sealed class MediaGpuSurfacePresenter : IDisposable
{
    private readonly MediaGpuSurface _surface;
    private readonly Action _invalidate;
    private readonly SynchronizationContext?
        _ownerContext;
    private readonly Action<Action>? _ownerDispatcher;
    private readonly int _ownerThreadId;
    private MediaVideoPresentationOptions
        _presentationOptions;
    private int _dispatchPending;
    private int _disposed;

    public MediaGpuSurfacePresenter(
        MediaGpuSurface surface,
        Action invalidate,
        SynchronizationContext? ownerContext = null,
        Action<Action>? ownerDispatcher = null)
    {
        _surface =
            surface ??
            throw new ArgumentNullException(
                nameof(surface));
        _invalidate =
            invalidate ??
            throw new ArgumentNullException(
                nameof(invalidate));
        _ownerContext =
            ownerContext ??
            SynchronizationContext.Current;
        _ownerDispatcher = ownerDispatcher;
        _ownerThreadId =
            Environment.CurrentManagedThreadId;
        _presentationOptions =
            new MediaVideoPresentationOptions();
        _surface.FrameAvailable += OnFrameAvailable;
    }

    public MediaGpuSurface Surface => _surface;

    public MediaVideoPresentationOptions
        PresentationOptions
    {
        get => _presentationOptions;
        set
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            _presentationOptions = value;
            RequestInvalidation();
        }
    }

    public Vector2 NaturalSize
    {
        get
        {
            MediaGpuFrameDescriptor descriptor =
                _surface.CurrentDescriptor;
            return new Vector2(
                descriptor.Width,
                descriptor.Height);
        }
    }

    public bool Record(
        DrawingContext context,
        WgpuContext requiredContext,
        Rect bounds)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        MediaVideoPresentationOptions options =
            _presentationOptions;
        return context.DrawLatestFrame(
            _surface,
            requiredContext,
            bounds,
            in options);
    }

    public bool Record(
        DrawingContext context,
        WgpuContext requiredContext,
        Rect bounds,
        in MediaVideoPresentationOptions options)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        return context.DrawLatestFrame(
            _surface,
            requiredContext,
            bounds,
            in options);
    }

    /// <summary>
    /// Records into a typed framework-owned ProGPU drawing surface and
    /// composes its current outer transform exactly once. This is the common
    /// retained rendering path used by LibreWPF, ProGPU-backed
    /// LibreWinForms/System.Drawing, Avalonia adapters, and custom hosts.
    /// Recording is O(C) for the bounded number of emitted commands and does
    /// not copy or read back decoded pixels.
    /// </summary>
    public bool Record(
        IProGpuDrawingContextSource contextSource,
        WgpuContext requiredContext,
        Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(contextSource);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (!contextSource.TryGetProGpuDrawingContext(
                out ProGpuDrawingContextState state))
        {
            return false;
        }

        MediaVideoPresentationOptions options =
            _presentationOptions;
        return RecordHostContext(
            in state,
            requiredContext,
            bounds,
            in options);
    }

    /// <summary>
    /// Records explicit presentation state into a typed framework-owned
    /// ProGPU drawing surface.
    /// </summary>
    public bool Record(
        IProGpuDrawingContextSource contextSource,
        WgpuContext requiredContext,
        Rect bounds,
        in MediaVideoPresentationOptions options)
    {
        ArgumentNullException.ThrowIfNull(contextSource);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (!contextSource.TryGetProGpuDrawingContext(
                out ProGpuDrawingContextState state))
        {
            return false;
        }

        return RecordHostContext(
            in state,
            requiredContext,
            bounds,
            in options);
    }

    /// <summary>
    /// Records into explicit typed host state, composing its outer transform
    /// exactly once. Framework integration packages can obtain this state
    /// from a package-neutral native context through
    /// ProGpuDrawingContextState.TryCreate. The operation is allocation-free
    /// and O(C) for the bounded number of commands emitted.
    /// </summary>
    public bool Record(
        in ProGpuDrawingContextState state,
        WgpuContext requiredContext,
        Rect bounds)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ValidateState(in state);
        MediaVideoPresentationOptions options =
            _presentationOptions;
        return RecordHostContext(
            in state,
            requiredContext,
            bounds,
            in options);
    }

    /// <summary>
    /// Records explicit presentation options into typed host state. No
    /// framework adapter is retained and no allocation is performed.
    /// </summary>
    public bool Record(
        in ProGpuDrawingContextState state,
        WgpuContext requiredContext,
        Rect bounds,
        in MediaVideoPresentationOptions options)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ValidateState(in state);
        return RecordHostContext(
            in state,
            requiredContext,
            bounds,
            in options);
    }

    public void RequestInvalidation()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.Exchange(
                ref _dispatchPending,
                1) != 0)
        {
            return;
        }

        SynchronizationContext? context =
            _ownerContext;
        if (context is not null)
        {
            if (ReferenceEquals(
                    SynchronizationContext.Current,
                    context))
            {
                DispatchInvalidation();
            }
            else
            {
                context.Post(
                    static state =>
                        ((MediaGpuSurfacePresenter)state!)
                            .DispatchInvalidation(),
                    this);
            }
            return;
        }

        if (Environment.CurrentManagedThreadId ==
            _ownerThreadId)
        {
            DispatchInvalidation();
            return;
        }

        Action<Action>? dispatcher =
            _ownerDispatcher;
        if (dispatcher is null)
        {
            DispatchInvalidation();
            return;
        }

        dispatcher(DispatchInvalidation);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _surface.FrameAvailable -= OnFrameAvailable;
        Interlocked.Exchange(ref _dispatchPending, 0);
    }

    private void OnFrameAvailable(
        object? sender,
        EventArgs eventArgs) =>
        RequestInvalidation();

    private void DispatchInvalidation()
    {
        Interlocked.Exchange(ref _dispatchPending, 0);
        if (Volatile.Read(ref _disposed) == 0)
        {
            _invalidate();
        }
    }

    private bool RecordHostContext(
        in ProGpuDrawingContextState state,
        WgpuContext requiredContext,
        Rect bounds,
        in MediaVideoPresentationOptions options)
    {
        DrawingContext context = state.DrawingContext;
        int startIndex = context.Commands.Count;
        if (!Record(
                context,
                requiredContext,
                bounds,
                in options))
        {
            return false;
        }

        Matrix4x4 outerTransform = state.OuterTransform;
        if (outerTransform == Matrix4x4.Identity)
        {
            return true;
        }

        RenderCommandList commands = context.Commands;
        for (int index = startIndex;
             index < commands.Count;
             index++)
        {
            RenderCommand command = commands[index];
            command.Transform =
                command.Transform == default ||
                command.Transform == Matrix4x4.Identity
                    ? outerTransform
                    : command.Transform * outerTransform;
            commands[index] = command;
        }
        return true;
    }

    private static void ValidateState(
        in ProGpuDrawingContextState state)
    {
        if (state.DrawingContext is null)
        {
            throw new ArgumentException(
                "Drawing context state must contain a typed ProGPU drawing context.",
                nameof(state));
        }
    }
}
