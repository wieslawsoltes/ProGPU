using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Android.Hardware;
using Android.Opengl;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Java.Nio;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using ProGPU.Media.Editing;
using ProGPU.Media.Effects;
using Silk.NET.WebGPU;
using AndroidSurfaceTexture = Android.Graphics.SurfaceTexture;
using AndroidSurface = Android.Views.Surface;

namespace ProGPU.Android.Media;

/// <summary>
/// Bounded MediaCodec input sink backed by three reusable RGBA
/// AHardwareBuffers shared between Dawn/Vulkan and EGL/OpenGL ES.
/// </summary>
/// <remarks>
/// Acquisition and completion are O(1). WebGPU and EGL exchange binary
/// SyncFDs in both directions; neither side performs a device-wide wait.
/// The only full-frame operation after WebGPU effects is one terminal EGL
/// texture sample/write into the timestamped encoder surface. Solid-color
/// frames replace decoder staging with a WebGPU clear on the same bounded
/// source ring.
/// </remarks>
public sealed unsafe class AndroidMediaCodecGpuEncoderFrameSink :
    IMediaGpuEncoderFrameSink,
    IAndroidEncoderSurfaceRenderer
{
    private const int TargetCount = 3;
    private readonly object _gate = new();
    private readonly Slot[] _slots;
    private readonly SourceSlot[] _sourceSlots;
    private readonly Queue<int> _available = new(TargetCount);
    private readonly AndroidHardwareBufferEglPresenter _presenter;
    private int _outstanding;
    private int _nextSourceSlot;
    private int _disposed;

    public AndroidMediaCodecGpuEncoderFrameSink(
        DawnGpuContext dawn,
        AndroidSurface encoderSurface,
        uint width,
        uint height)
    {
        ArgumentNullException.ThrowIfNull(dawn);
        ArgumentNullException.ThrowIfNull(encoderSurface);
        if (!OperatingSystem.IsAndroid())
        {
            throw new PlatformNotSupportedException(
                "The Android MediaCodec GPU sink is available only on Android.");
        }
        if (width == 0 || height == 0 ||
            width > int.MaxValue || height > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        Context = dawn.Context;
        Width = width;
        Height = height;
        Capabilities = new MediaGpuEncoderFrameSinkCapabilities(
            "progpu.android.mediacodec.webgpu",
            MediaCompositionExportVideoPath.GpuCopy,
            TextureFormat.Rgba8Unorm,
            hardwareEncoderSurface: true,
            supportsExplicitPresentationTime: true,
            supportsGpuEffects: true,
            maximumFramesInFlight: TargetCount);
        _presenter = new AndroidHardwareBufferEglPresenter(
            encoderSurface,
            checked((int)width),
            checked((int)height));
        _slots = new Slot[TargetCount];
        _sourceSlots = new SourceSlot[TargetCount];

        try
        {
            for (int index = 0; index < TargetCount; index++)
            {
                var owner =
                    AndroidAllocatedHardwareBufferOwner.Create(
                        checked((int)width),
                        checked((int)height));
                AndroidEglImageTarget? eglTarget = null;
                try
                {
                    eglTarget = _presenter.CreateTarget(owner.Handle);
                    var descriptor =
                        new ProGpuExternalTextureDescriptor(
                            ProGpuExternalTextureHandleKind
                                .AndroidHardwareBuffer,
                            owner.Handle,
                            width,
                            height,
                            TextureFormat.Rgba8Unorm,
                            TextureUsage.RenderAttachment,
                            GpuTextureAlphaMode.Straight,
                            IsInitialized: false);
                    if (!dawn.TryImportAHardwareBufferRenderTarget(
                            in descriptor,
                            owner,
                            out DawnExplicitSharedTextureAccess access))
                    {
                        throw new NotSupportedException(
                            "The active Dawn Vulkan device cannot import an RGBA AHardwareBuffer render target with SyncFD synchronization.");
                    }

                    owner = null!;
                    _slots[index] =
                        new Slot(
                            index,
                            access,
                            eglTarget,
                            this);
                    eglTarget = null;
                    _available.Enqueue(index);
                }
                finally
                {
                    eglTarget?.Dispose();
                    owner?.Dispose();
                }
            }
            for (int index = 0; index < TargetCount; index++)
            {
                _sourceSlots[index] =
                    CreateSourceSlot(
                        dawn,
                        index,
                        width,
                        height);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public WgpuContext Context { get; }

    public uint Width { get; }

    public uint Height { get; }

    public MediaGpuEncoderFrameSinkCapabilities Capabilities { get; }

    public AndroidSurface DecoderSurface =>
        _presenter.DecoderSurface;

    public bool TryAcquireFrame(
        TimeSpan presentationTime,
        out IMediaGpuEncoderFrame frame)
    {
        if (presentationTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(presentationTime));
        }
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        lock (_gate)
        {
            if (_available.Count == 0)
            {
                frame = null!;
                return false;
            }

            int index = _available.Dequeue();
            Slot slot = _slots[index];
            slot.Lease.Activate(presentationTime);
            _outstanding++;
            frame = slot.Lease;
            return true;
        }
    }

    public void DrawFrame(
        long presentationTimeMicroseconds,
        MediaVideoColorTransform transform,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (presentationTimeMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(presentationTimeMicroseconds));
        }
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        SourceSlot source =
            _sourceSlots[
                _nextSourceSlot++ % _sourceSlots.Length];
        int priorWebGpuFence =
            source.WebGpuFence.HasFence
                ? source.WebGpuFence.DetachHandle()
                : -1;
        int eglWriteFence =
            _presenter.StageDecoderFrame(
                source.EglTarget,
                priorWebGpuFence,
                cancellationToken);
        source.Access.BeginAccessAndConsumeSyncFd(
            eglWriteFence,
            initialized: true);

        IMediaGpuEncoderFrame? encoderFrame = null;
        bool sourceAccessActive = true;
        try
        {
            TimeSpan presentationTime =
                TimeSpan.FromTicks(
                    checked(
                        presentationTimeMicroseconds *
                        TimeSpan.TicksPerMicrosecond));
            if (!TryAcquireFrame(
                    presentationTime,
                    out encoderFrame))
            {
                throw new InvalidOperationException(
                    "The bounded Android encoder-target ring is full.");
            }

            GpuTextureBlitter.Blit(
                source.Access.Texture,
                encoderFrame.Texture.ViewPtr,
                encoderFrame.Texture.Format,
                ToGpuTransform(transform));
            source.Access.EndAccessAndExportSyncFd(
                source.WebGpuFence);
            sourceAccessActive = false;
            encoderFrame.Complete(renderSucceeded: true);
            encoderFrame = null;
        }
        finally
        {
            if (sourceAccessActive)
            {
                try
                {
                    source.Access.EndAccessAndExportSyncFd(
                        source.WebGpuFence);
                }
                catch
                {
                    // Preserve the original export failure.
                }
            }
            encoderFrame?.Dispose();
        }
    }

    public void DrawColorFrame(
        long presentationTimeMicroseconds,
        uint argbColor,
        MediaVideoColorTransform transform,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (presentationTimeMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(presentationTimeMicroseconds));
        }
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        SourceSlot source =
            _sourceSlots[
                _nextSourceSlot++ % _sourceSlots.Length];
        if (source.WebGpuFence.HasFence)
        {
            source.Access.BeginAccessAndConsumeSyncFd(
                source.WebGpuFence.DetachHandle(),
                source.WebGpuFence.Initialized);
        }
        else
        {
            source.Access.BeginAccess(
                initialized: false);
        }

        IMediaGpuEncoderFrame? encoderFrame = null;
        bool sourceAccessActive = true;
        try
        {
            GpuTextureClearer.Clear(
                source.Access.Texture,
                ToWebGpuColor(argbColor));
            TimeSpan presentationTime =
                TimeSpan.FromTicks(
                    checked(
                        presentationTimeMicroseconds *
                        TimeSpan.TicksPerMicrosecond));
            if (!TryAcquireFrame(
                    presentationTime,
                    out encoderFrame))
            {
                throw new InvalidOperationException(
                    "The bounded Android encoder-target ring is full.");
            }

            GpuTextureBlitter.Blit(
                source.Access.Texture,
                encoderFrame.Texture.ViewPtr,
                encoderFrame.Texture.Format,
                ToGpuTransform(transform));
            source.Access.EndAccessAndExportSyncFd(
                source.WebGpuFence);
            sourceAccessActive = false;
            encoderFrame.Complete(renderSucceeded: true);
            encoderFrame = null;
        }
        finally
        {
            if (sourceAccessActive)
            {
                try
                {
                    source.Access.EndAccessAndExportSyncFd(
                        source.WebGpuFence);
                }
                catch
                {
                    // Preserve the original export failure.
                }
            }
            encoderFrame?.Dispose();
        }
    }

    private static Color ToWebGpuColor(
        uint argbColor)
    {
        const double scale = 1d / byte.MaxValue;
        return new Color
        {
            R = ((argbColor >> 16) & 0xff) * scale,
            G = ((argbColor >> 8) & 0xff) * scale,
            B = (argbColor & 0xff) * scale,
            A = ((argbColor >> 24) & 0xff) * scale
        };
    }

    private static GpuTextureColorTransform
        ToGpuTransform(
            MediaVideoColorTransform transform) =>
        new(
            transform.Red,
            transform.Green,
            transform.Blue);

    public ValueTask DrainAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        lock (_gate)
        {
            if (_outstanding != 0)
            {
                throw new InvalidOperationException(
                    "All acquired encoder frames must be completed before draining.");
            }
        }

        // The exporter owns MediaCodec output draining. Every completed frame
        // has already been swapped into its input Surface.
        return ValueTask.CompletedTask;
    }

    private void Complete(
        Slot slot,
        bool renderSucceeded)
    {
        bool reusable = false;
        DawnSyncFdEndAccessResult webGpuFence =
            slot.WebGpuFence;
        slot.Access.EndAccessAndExportSyncFd(webGpuFence);
        if (renderSucceeded)
        {
            if (!webGpuFence.HasFence)
            {
                throw new InvalidOperationException(
                    "Dawn submitted a rendered encoder frame without a completion fence.");
            }

            int webGpuSyncFd = webGpuFence.DetachHandle();
            int eglSyncFd = _presenter.Present(
                slot.EglTarget,
                webGpuSyncFd,
                slot.Lease.PresentationTime);
            slot.Access.BeginAccessAndConsumeSyncFd(
                eglSyncFd,
                initialized: true);
            reusable = true;
        }
        else if (webGpuFence.HasFence)
        {
            // Preserve ordering for partially submitted work without exposing
            // that frame to MediaCodec.
            slot.Access.BeginAccessAndConsumeSyncFd(
                webGpuFence.DetachHandle(),
                webGpuFence.Initialized);
            reusable = true;
        }
        else
        {
            slot.Access.BeginAccess(
                webGpuFence.Initialized);
            reusable = true;
        }

        lock (_gate)
        {
            _outstanding--;
            if (reusable &&
                Volatile.Read(ref _disposed) == 0)
            {
                _available.Enqueue(slot.Index);
            }
        }
    }

    private SourceSlot CreateSourceSlot(
        DawnGpuContext dawn,
        int index,
        uint width,
        uint height)
    {
        var owner =
            AndroidAllocatedHardwareBufferOwner.Create(
                checked((int)width),
                checked((int)height));
        AndroidEglImageTarget? eglTarget = null;
        try
        {
            eglTarget =
                _presenter.CreateRenderTarget(
                    owner.Handle);
            var descriptor =
                new ProGpuExternalTextureDescriptor(
                    ProGpuExternalTextureHandleKind
                        .AndroidHardwareBuffer,
                    owner.Handle,
                    width,
                    height,
                    TextureFormat.Rgba8Unorm,
                    TextureUsage.TextureBinding |
                    TextureUsage.RenderAttachment,
                    GpuTextureAlphaMode.Straight,
                    IsInitialized: false);
            if (!dawn.TryImportAHardwareBufferRenderTarget(
                    in descriptor,
                    owner,
                    out DawnExplicitSharedTextureAccess access))
            {
                throw new NotSupportedException(
                    "The active Dawn Vulkan device cannot import an AHardwareBuffer decoder-staging target.");
            }

            owner = null!;
            var slot =
                new SourceSlot(
                    index,
                    access,
                    eglTarget);
            eglTarget = null;
            // Transfer initial ownership from Dawn to EGL. The texture has
            // not been submitted, so Dawn normally returns no fence.
            access.EndAccessAndExportSyncFd(
                slot.WebGpuFence);
            return slot;
        }
        finally
        {
            eglTarget?.Dispose();
            owner?.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        for (int index = 0; index < _slots.Length; index++)
        {
            Slot? slot = _slots[index];
            if (slot is null)
            {
                continue;
            }
            try
            {
                slot.Lease.AbortIfActive();
            }
            catch
            {
                // Continue releasing the remaining native queue slots.
            }
            finally
            {
                try
                {
                    try
                    {
                        slot.EglTarget.Dispose();
                    }
                    finally
                    {
                        slot.WebGpuFence.Dispose();
                        slot.Access.Dispose();
                    }
                }
                catch
                {
                    // Continue releasing the remaining native queue slots.
                }
            }
        }
        for (int index = 0;
             index < _sourceSlots.Length;
             index++)
        {
            SourceSlot? slot = _sourceSlots[index];
            if (slot is null)
            {
                continue;
            }
            try
            {
                slot.EglTarget.Dispose();
            }
            catch
            {
            }
            slot.WebGpuFence.Dispose();
            slot.Access.Dispose();
        }
        _presenter.Dispose();
        lock (_gate)
        {
            _available.Clear();
            _outstanding = 0;
        }
    }

    private sealed class Slot
    {
        internal Slot(
            int index,
            DawnExplicitSharedTextureAccess access,
            AndroidEglImageTarget eglTarget,
            AndroidMediaCodecGpuEncoderFrameSink sink)
        {
            Index = index;
            Access = access;
            EglTarget = eglTarget;
            WebGpuFence = new DawnSyncFdEndAccessResult();
            Lease = new FrameLease(this, sink);
        }

        internal int Index { get; }
        internal DawnExplicitSharedTextureAccess Access { get; }
        internal AndroidEglImageTarget EglTarget { get; }
        internal DawnSyncFdEndAccessResult WebGpuFence { get; }
        internal FrameLease Lease { get; }
    }

    private sealed class SourceSlot
    {
        internal SourceSlot(
            int index,
            DawnExplicitSharedTextureAccess access,
            AndroidEglImageTarget eglTarget)
        {
            Index = index;
            Access = access;
            EglTarget = eglTarget;
            WebGpuFence =
                new DawnSyncFdEndAccessResult();
        }

        internal int Index { get; }
        internal DawnExplicitSharedTextureAccess Access { get; }
        internal AndroidEglImageTarget EglTarget { get; }
        internal DawnSyncFdEndAccessResult WebGpuFence { get; }
    }

    private sealed class FrameLease :
        IMediaGpuEncoderFrame
    {
        private readonly Slot _slot;
        private readonly AndroidMediaCodecGpuEncoderFrameSink _sink;
        private long _presentationTicks;
        private int _active;

        internal FrameLease(
            Slot slot,
            AndroidMediaCodecGpuEncoderFrameSink sink)
        {
            _slot = slot;
            _sink = sink;
        }

        public GpuTexture Texture => _slot.Access.Texture;

        public TimeSpan PresentationTime =>
            TimeSpan.FromTicks(
                Volatile.Read(ref _presentationTicks));

        public bool IsCompleted =>
            Volatile.Read(ref _active) == 0;

        internal void Activate(TimeSpan presentationTime)
        {
            Volatile.Write(
                ref _presentationTicks,
                presentationTime.Ticks);
            if (Interlocked.Exchange(ref _active, 1) != 0)
            {
                throw new InvalidOperationException(
                    "The pooled Android encoder frame is already active.");
            }
        }

        public void Complete(bool renderSucceeded)
        {
            if (Interlocked.Exchange(ref _active, 0) == 0)
            {
                throw new InvalidOperationException(
                    "The Android encoder frame is not active.");
            }
            _sink.Complete(_slot, renderSucceeded);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) != 0)
            {
                _sink.Complete(
                    _slot,
                    renderSucceeded: false);
            }
        }

        internal void AbortIfActive() => Dispose();
    }
}

internal sealed class AndroidAllocatedHardwareBufferOwner :
    IDisposable
{
    private HardwareBuffer? _buffer;
    private nint _handle;

    private AndroidAllocatedHardwareBufferOwner(
        HardwareBuffer buffer,
        nint handle)
    {
        _buffer = buffer;
        _handle = handle;
    }

    internal nint Handle => Volatile.Read(ref _handle);

    internal static AndroidAllocatedHardwareBufferOwner Create(
        int width,
        int height)
    {
        HardwareBuffer buffer = HardwareBuffer.Create(
            width,
            height,
            HardwareBufferFormat.Rgba8888,
            1,
            HardwareBufferUsage.UsageGpuColorOutput |
            HardwareBufferUsage.UsageGpuSampledImage);
        nint handle =
            AndroidHardwareBufferNative.FromJavaHardwareBuffer(
                JNIEnv.Handle,
                buffer.Handle);
        if (handle == 0)
        {
            buffer.Close();
            buffer.Dispose();
            throw new InvalidOperationException(
                "Android did not expose the allocated AHardwareBuffer.");
        }

        AndroidHardwareBufferNative.Acquire(handle);
        return new AndroidAllocatedHardwareBufferOwner(
            buffer,
            handle);
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            AndroidHardwareBufferNative.Release(handle);
        }
        HardwareBuffer? buffer =
            Interlocked.Exchange(ref _buffer, null);
        buffer?.Close();
        buffer?.Dispose();
    }
}

internal sealed class AndroidEglImageTarget :
    IDisposable
{
    private readonly AndroidHardwareBufferEglPresenter _owner;
    private nint _image;
    private int _texture;
    private int _framebuffer;

    internal AndroidEglImageTarget(
        AndroidHardwareBufferEglPresenter owner,
        nint image,
        int texture,
        int framebuffer = 0)
    {
        _owner = owner;
        _image = image;
        _texture = texture;
        _framebuffer = framebuffer;
    }

    internal int Texture => Volatile.Read(ref _texture);
    internal int Framebuffer => Volatile.Read(ref _framebuffer);

    public void Dispose()
    {
        int texture = Interlocked.Exchange(ref _texture, 0);
        int framebuffer =
            Interlocked.Exchange(ref _framebuffer, 0);
        nint image = Interlocked.Exchange(ref _image, 0);
        _owner.DestroyTarget(
            image,
            texture,
            framebuffer);
    }
}

internal sealed unsafe class AndroidHardwareBufferEglPresenter :
    IDisposable
{
    private const long FrameWaitMilliseconds = 5_000;
    private const int EglNativeBufferAndroid = 0x3140;
    private const int EglImagePreservedKhr = 0x30D2;
    private const int EglSyncNativeFenceAndroid = 0x3144;
    private const int EglSyncNativeFenceFdAndroid = 0x3145;
    private const uint EglWaitSyncFlags = 0;
    private static readonly string s_vertexShader =
        ShaderResource.Load(
            typeof(AndroidHardwareBufferEglPresenter),
            "AndroidHardwareBufferBlitVertex.glsl");
    private static readonly string s_fragmentShader =
        ShaderResource.Load(
            typeof(AndroidHardwareBufferEglPresenter),
            "AndroidHardwareBufferBlitFragment.glsl");
    private static readonly string s_decoderVertexShader =
        ShaderResource.Load(
            typeof(AndroidHardwareBufferEglPresenter),
            "AndroidMediaCompositionVertex.glsl");
    private static readonly string s_decoderFragmentShader =
        ShaderResource.Load(
            typeof(AndroidHardwareBufferEglPresenter),
            "AndroidMediaCompositionFragment.glsl");
    private static readonly float[] s_quad =
    [
        -1f, -1f, 0f, 1f,
         1f, -1f, 1f, 1f,
        -1f,  1f, 0f, 0f,
         1f,  1f, 1f, 0f
    ];
    private static readonly float[] s_decoderQuad =
    [
        -1f, -1f, 0f, 0f,
         1f, -1f, 1f, 0f,
        -1f,  1f, 0f, 1f,
         1f,  1f, 1f, 1f
    ];

    private readonly int _width;
    private readonly int _height;
    private readonly FloatBuffer _quad;
    private readonly FloatBuffer _decoderQuad;
    private readonly AutoResetEvent _frameAvailable = new(false);
    private readonly HandlerThread _callbackThread;
    private readonly Handler _callbackHandler;
    private readonly DecoderFrameListener _frameListener;
    private readonly float[] _textureTransform = new float[16];
    private readonly int[] _decoderTextureIds = new int[1];
    private EGLDisplay? _display;
    private EGLContext? _context;
    private EGLSurface? _surface;
    private int _program;
    private int _vertexShader;
    private int _fragmentShader;
    private int _positionLocation;
    private int _texCoordLocation;
    private AndroidSurfaceTexture? _surfaceTexture;
    private AndroidSurface? _decoderSurface;
    private int _decoderProgram;
    private int _decoderVertexShader;
    private int _decoderFragmentShader;
    private int _decoderPositionLocation;
    private int _decoderTexCoordLocation;
    private int _decoderTransformLocation;
    private int _decoderRedTransformLocation;
    private int _decoderGreenTransformLocation;
    private int _decoderBlueTransformLocation;
    private int _disposed;

    internal AndroidHardwareBufferEglPresenter(
        AndroidSurface encoderSurface,
        int width,
        int height)
    {
        _width = width;
        _height = height;
        _quad = ByteBuffer
            .AllocateDirect(s_quad.Length * sizeof(float))
            .Order(ByteOrder.NativeOrder()!)
            .AsFloatBuffer();
        _quad.Put(s_quad);
        _quad.Position(0);
        _decoderQuad = ByteBuffer
            .AllocateDirect(
                s_decoderQuad.Length *
                sizeof(float))
            .Order(ByteOrder.NativeOrder()!)
            .AsFloatBuffer();
        _decoderQuad.Put(s_decoderQuad);
        _decoderQuad.Position(0);
        _callbackThread =
            new HandlerThread(
                "ProGPU Android WebGPU Export Decoder");
        _callbackThread.Start();
        _callbackHandler =
            new Handler(
                _callbackThread.Looper ??
                throw new InvalidOperationException(
                    "Android could not create the WebGPU export decoder looper."));
        _frameListener =
            new DecoderFrameListener(_frameAvailable);
        try
        {
            InitializeEgl(encoderSurface);
            InitializeGl();
            InitializeDecoderGl();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal AndroidSurface DecoderSurface =>
        _decoderSurface ??
        throw new ObjectDisposedException(
            nameof(AndroidHardwareBufferEglPresenter));

    internal AndroidEglImageTarget CreateTarget(
        nint hardwareBuffer) =>
        CreateTargetCore(
            hardwareBuffer,
            renderTarget: false);

    internal AndroidEglImageTarget CreateRenderTarget(
        nint hardwareBuffer) =>
        CreateTargetCore(
            hardwareBuffer,
            renderTarget: true);

    private AndroidEglImageTarget CreateTargetCore(
        nint hardwareBuffer,
        bool renderTarget)
    {
        MakeCurrent();
        nint clientBuffer =
            AndroidEglNative.GetNativeClientBuffer(hardwareBuffer);
        if (clientBuffer == 0)
        {
            throw new InvalidOperationException(
                "EGL could not wrap the AHardwareBuffer client buffer.");
        }
        Span<int> attributes =
            stackalloc int[]
            {
                EglImagePreservedKhr,
                EGL14.EglTrue,
                EGL14.EglNone
            };
        nint image;
        fixed (int* attributePointer = attributes)
        {
            image = AndroidEglNative.CreateImage(
                (nint)_display!.NativeHandle,
                0,
                EglNativeBufferAndroid,
                clientBuffer,
                attributePointer);
        }
        if (image == 0)
        {
            throw new InvalidOperationException(
                $"EGL could not create an AHardwareBuffer image: 0x{EGL14.EglGetError():X}.");
        }

        var textureIds = new int[1];
        GLES20.GlGenTextures(1, textureIds, 0);
        int texture = textureIds[0];
        int framebuffer = 0;
        try
        {
            GLES20.GlBindTexture(
                GLES20.GlTexture2d,
                texture);
            GLES20.GlTexParameteri(
                GLES20.GlTexture2d,
                GLES20.GlTextureMinFilter,
                GLES20.GlLinear);
            GLES20.GlTexParameteri(
                GLES20.GlTexture2d,
                GLES20.GlTextureMagFilter,
                GLES20.GlLinear);
            GLES20.GlTexParameteri(
                GLES20.GlTexture2d,
                GLES20.GlTextureWrapS,
                GLES20.GlClampToEdge);
            GLES20.GlTexParameteri(
                GLES20.GlTexture2d,
                GLES20.GlTextureWrapT,
                GLES20.GlClampToEdge);
            AndroidEglNative.ImageTargetTexture2D(
                GLES20.GlTexture2d,
                image);
            if (renderTarget)
            {
                var framebufferIds = new int[1];
                GLES20.GlGenFramebuffers(
                    1,
                    framebufferIds,
                    0);
                framebuffer = framebufferIds[0];
                GLES20.GlBindFramebuffer(
                    GLES20.GlFramebuffer,
                    framebuffer);
                GLES20.GlFramebufferTexture2D(
                    GLES20.GlFramebuffer,
                    GLES20.GlColorAttachment0,
                    GLES20.GlTexture2d,
                    texture,
                    0);
                int status =
                    GLES20.GlCheckFramebufferStatus(
                        GLES20.GlFramebuffer);
                GLES20.GlBindFramebuffer(
                    GLES20.GlFramebuffer,
                    0);
                if (status != GLES20.GlFramebufferComplete)
                {
                    throw new InvalidOperationException(
                        $"Android could not attach the AHardwareBuffer framebuffer: 0x{status:X}.");
                }
            }
            return new AndroidEglImageTarget(
                this,
                image,
                texture,
                framebuffer);
        }
        catch
        {
            if (framebuffer != 0)
            {
                GLES20.GlDeleteFramebuffers(
                    1,
                    [framebuffer],
                    0);
            }
            if (texture != 0)
            {
                GLES20.GlDeleteTextures(1, [texture], 0);
            }
            _ = AndroidEglNative.DestroyImage(
                (nint)_display!.NativeHandle,
                image);
            throw;
        }
    }

    internal int StageDecoderFrame(
        AndroidEglImageTarget target,
        int ownedPriorWebGpuSyncFd,
        CancellationToken cancellationToken)
    {
        int pendingSyncFd = ownedPriorWebGpuSyncFd;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long deadline =
                Stopwatch.GetTimestamp() +
                FrameWaitMilliseconds *
                Stopwatch.Frequency /
                1_000;
            while (!_frameAvailable.WaitOne(50))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    throw new TimeoutException(
                        "Android decoder did not deliver its WebGPU staging frame.");
                }
            }

            MakeCurrent();
            AndroidSurfaceTexture texture =
                _surfaceTexture ??
                throw new ObjectDisposedException(
                    nameof(AndroidHardwareBufferEglPresenter));
            texture.UpdateTexImage();
            texture.GetTransformMatrix(
                _textureTransform);
            if (pendingSyncFd >= 0)
            {
                int waitFd = pendingSyncFd;
                pendingSyncFd = -1;
                WaitForSyncFd(waitFd);
            }

            GLES20.GlBindFramebuffer(
                GLES20.GlFramebuffer,
                target.Framebuffer);
            GLES20.GlViewport(
                0,
                0,
                _width,
                _height);
            GLES20.GlUseProgram(_decoderProgram);
            GLES20.GlActiveTexture(GLES20.GlTexture0);
            GLES20.GlBindTexture(
                GLES11Ext.GlTextureExternalOes,
                _decoderTextureIds[0]);
            _decoderQuad.Position(0);
            GLES20.GlEnableVertexAttribArray(
                _decoderPositionLocation);
            GLES20.GlVertexAttribPointer(
                _decoderPositionLocation,
                2,
                GLES20.GlFloat,
                false,
                4 * sizeof(float),
                _decoderQuad);
            _decoderQuad.Position(2);
            GLES20.GlEnableVertexAttribArray(
                _decoderTexCoordLocation);
            GLES20.GlVertexAttribPointer(
                _decoderTexCoordLocation,
                2,
                GLES20.GlFloat,
                false,
                4 * sizeof(float),
                _decoderQuad);
            GLES20.GlUniformMatrix4fv(
                _decoderTransformLocation,
                1,
                false,
                _textureTransform,
                0);
            GLES20.GlUniform4f(
                _decoderRedTransformLocation,
                1f,
                0f,
                0f,
                0f);
            GLES20.GlUniform4f(
                _decoderGreenTransformLocation,
                0f,
                1f,
                0f,
                0f);
            GLES20.GlUniform4f(
                _decoderBlueTransformLocation,
                0f,
                0f,
                1f,
                0f);
            GLES20.GlDrawArrays(
                GLES20.GlTriangleStrip,
                0,
                4);
            GLES20.GlDisableVertexAttribArray(
                _decoderPositionLocation);
            GLES20.GlDisableVertexAttribArray(
                _decoderTexCoordLocation);
            GLES20.GlBindFramebuffer(
                GLES20.GlFramebuffer,
                0);
            return CreateCompletionSyncFd();
        }
        finally
        {
            AndroidEglNative.Close(pendingSyncFd);
        }
    }

    internal int Present(
        AndroidEglImageTarget target,
        int ownedWebGpuSyncFd,
        TimeSpan presentationTime)
    {
        MakeCurrent();
        WaitForSyncFd(ownedWebGpuSyncFd);

        GLES20.GlViewport(0, 0, _width, _height);
        GLES20.GlUseProgram(_program);
        GLES20.GlActiveTexture(GLES20.GlTexture0);
        GLES20.GlBindTexture(
            GLES20.GlTexture2d,
            target.Texture);
        _quad.Position(0);
        GLES20.GlEnableVertexAttribArray(_positionLocation);
        GLES20.GlVertexAttribPointer(
            _positionLocation,
            2,
            GLES20.GlFloat,
            false,
            4 * sizeof(float),
            _quad);
        _quad.Position(2);
        GLES20.GlEnableVertexAttribArray(_texCoordLocation);
        GLES20.GlVertexAttribPointer(
            _texCoordLocation,
            2,
            GLES20.GlFloat,
            false,
            4 * sizeof(float),
            _quad);
        GLES20.GlDrawArrays(
            GLES20.GlTriangleStrip,
            0,
            4);
        GLES20.GlDisableVertexAttribArray(_positionLocation);
        GLES20.GlDisableVertexAttribArray(_texCoordLocation);

        int completionFd = CreateCompletionSyncFd();
        try
        {
            EGLExt.EglPresentationTimeANDROID(
                _display!,
                _surface!,
                checked(
                    presentationTime.Ticks *
                    TimeSpan.NanosecondsPerTick));
            if (!EGL14.EglSwapBuffers(
                    _display!,
                    _surface!))
            {
                throw new InvalidOperationException(
                    $"Android EGL encoder swap failed: 0x{EGL14.EglGetError():X}.");
            }
            return completionFd;
        }
        catch
        {
            AndroidEglNative.Close(completionFd);
            throw;
        }
    }

    private void WaitForSyncFd(int ownedSyncFd)
    {
        Span<int> attributes =
            stackalloc int[]
            {
                EglSyncNativeFenceFdAndroid,
                ownedSyncFd,
                EGL14.EglNone
            };
        nint sync = 0;
        try
        {
            fixed (int* attributePointer = attributes)
            {
                sync = AndroidEglNative.CreateSync(
                    (nint)_display!.NativeHandle,
                    EglSyncNativeFenceAndroid,
                    attributePointer);
            }
            if (sync == 0)
            {
                throw new InvalidOperationException(
                    $"EGL could not import the WebGPU SyncFD: 0x{EGL14.EglGetError():X}.");
            }
            // EGL owns the descriptor after successful native-fence creation.
            ownedSyncFd = -1;
            if (!AndroidEglNative.WaitSync(
                    (nint)_display!.NativeHandle,
                    sync,
                    EglWaitSyncFlags))
            {
                throw new InvalidOperationException(
                    $"EGL could not queue the WebGPU fence wait: 0x{EGL14.EglGetError():X}.");
            }
        }
        finally
        {
            if (sync != 0)
            {
                _ = AndroidEglNative.DestroySync(
                    (nint)_display!.NativeHandle,
                    sync);
            }
            if (ownedSyncFd >= 0)
            {
                AndroidEglNative.Close(ownedSyncFd);
            }
        }
    }

    private int CreateCompletionSyncFd()
    {
        Span<int> attributes =
            stackalloc int[] { EGL14.EglNone };
        nint sync;
        fixed (int* attributePointer = attributes)
        {
            sync = AndroidEglNative.CreateSync(
                (nint)_display!.NativeHandle,
                EglSyncNativeFenceAndroid,
                attributePointer);
        }
        if (sync == 0)
        {
            throw new InvalidOperationException(
                $"EGL could not create an encoder-read completion fence: 0x{EGL14.EglGetError():X}.");
        }
        try
        {
            GLES20.GlFlush();
            int descriptor =
                AndroidEglNative.DuplicateNativeFenceFd(
                    (nint)_display!.NativeHandle,
                    sync);
            if (descriptor < 0)
            {
                throw new InvalidOperationException(
                    $"EGL could not export the encoder-read SyncFD: 0x{EGL14.EglGetError():X}.");
            }
            return descriptor;
        }
        finally
        {
            _ = AndroidEglNative.DestroySync(
                (nint)_display!.NativeHandle,
                sync);
        }
    }

    internal void DestroyTarget(
        nint image,
        int texture,
        int framebuffer)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        MakeCurrent();
        if (framebuffer != 0)
        {
            GLES20.GlDeleteFramebuffers(
                1,
                [framebuffer],
                0);
        }
        if (texture != 0)
        {
            GLES20.GlDeleteTextures(1, [texture], 0);
        }
        if (image != 0)
        {
            _ = AndroidEglNative.DestroyImage(
                (nint)_display!.NativeHandle,
                image);
        }
    }

    private void InitializeEgl(AndroidSurface encoderSurface)
    {
        _display =
            EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
        if (_display is null ||
            !EGL14.EglInitialize(
                _display,
                new int[1],
                0,
                new int[1],
                0))
        {
            throw new InvalidOperationException(
                "Android could not initialize the encoder EGL display.");
        }

        string extensions =
            EGL14.EglQueryString(
                _display,
                EGL14.EglExtensions) ??
            string.Empty;
        RequireExtension(
            extensions,
            "EGL_ANDROID_image_native_buffer");
        RequireExtension(
            extensions,
            "EGL_ANDROID_native_fence_sync");
        RequireExtension(
            extensions,
            "EGL_KHR_image_base");
        RequireExtension(
            extensions,
            "EGL_KHR_wait_sync");

        int[] attributes =
        [
            EGL14.EglRedSize, 8,
            EGL14.EglGreenSize, 8,
            EGL14.EglBlueSize, 8,
            EGL14.EglAlphaSize, 8,
            EGL14.EglRenderableType,
            EGL14.EglOpenglEs2Bit,
            EGL14.EglSurfaceType,
            EGL14.EglWindowBit,
            EGL14.EglNone
        ];
        var configs = new EGLConfig[1];
        var count = new int[1];
        if (!EGL14.EglChooseConfig(
                _display,
                attributes,
                0,
                configs,
                0,
                1,
                count,
                0) ||
            count[0] != 1)
        {
            throw new InvalidOperationException(
                "Android could not choose the encoder EGL configuration.");
        }
        _context = EGL14.EglCreateContext(
            _display,
            configs[0],
            EGL14.EglNoContext,
            [
                EGL14.EglContextClientVersion,
                2,
                EGL14.EglNone
            ],
            0);
        _surface = EGL14.EglCreateWindowSurface(
            _display,
            configs[0],
            encoderSurface,
            [EGL14.EglNone],
            0);
        MakeCurrent();
    }

    private void InitializeGl()
    {
        _vertexShader = CompileShader(
            GLES20.GlVertexShader,
            s_vertexShader);
        _fragmentShader = CompileShader(
            GLES20.GlFragmentShader,
            s_fragmentShader);
        _program = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(_program, _vertexShader);
        GLES20.GlAttachShader(_program, _fragmentShader);
        GLES20.GlLinkProgram(_program);
        var status = new int[1];
        GLES20.GlGetProgramiv(
            _program,
            GLES20.GlLinkStatus,
            status,
            0);
        if (status[0] == 0)
        {
            throw new InvalidOperationException(
                $"Android AHardwareBuffer blit program link failed: {GLES20.GlGetProgramInfoLog(_program)}");
        }
        _positionLocation =
            GLES20.GlGetAttribLocation(
                _program,
                "a_position");
        _texCoordLocation =
            GLES20.GlGetAttribLocation(
                _program,
                "a_tex_coord");
        int sourceLocation =
            GLES20.GlGetUniformLocation(
                _program,
                "u_source");
        GLES20.GlUseProgram(_program);
        GLES20.GlUniform1i(sourceLocation, 0);
    }

    private void InitializeDecoderGl()
    {
        _decoderVertexShader = CompileShader(
            GLES20.GlVertexShader,
            s_decoderVertexShader);
        _decoderFragmentShader = CompileShader(
            GLES20.GlFragmentShader,
            s_decoderFragmentShader);
        _decoderProgram = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(
            _decoderProgram,
            _decoderVertexShader);
        GLES20.GlAttachShader(
            _decoderProgram,
            _decoderFragmentShader);
        GLES20.GlLinkProgram(_decoderProgram);
        var status = new int[1];
        GLES20.GlGetProgramiv(
            _decoderProgram,
            GLES20.GlLinkStatus,
            status,
            0);
        if (status[0] == 0)
        {
            throw new InvalidOperationException(
                $"Android decoder staging program link failed: {GLES20.GlGetProgramInfoLog(_decoderProgram)}");
        }

        _decoderPositionLocation =
            GLES20.GlGetAttribLocation(
                _decoderProgram,
                "a_position");
        _decoderTexCoordLocation =
            GLES20.GlGetAttribLocation(
                _decoderProgram,
                "a_tex_coord");
        _decoderTransformLocation =
            GLES20.GlGetUniformLocation(
                _decoderProgram,
                "u_tex_transform");
        _decoderRedTransformLocation =
            GLES20.GlGetUniformLocation(
                _decoderProgram,
                "u_red_transform");
        _decoderGreenTransformLocation =
            GLES20.GlGetUniformLocation(
                _decoderProgram,
                "u_green_transform");
        _decoderBlueTransformLocation =
            GLES20.GlGetUniformLocation(
                _decoderProgram,
                "u_blue_transform");
        int sourceLocation =
            GLES20.GlGetUniformLocation(
                _decoderProgram,
                "u_source");

        GLES20.GlGenTextures(
            1,
            _decoderTextureIds,
            0);
        GLES20.GlBindTexture(
            GLES11Ext.GlTextureExternalOes,
            _decoderTextureIds[0]);
        GLES20.GlTexParameteri(
            GLES11Ext.GlTextureExternalOes,
            GLES20.GlTextureMinFilter,
            GLES20.GlLinear);
        GLES20.GlTexParameteri(
            GLES11Ext.GlTextureExternalOes,
            GLES20.GlTextureMagFilter,
            GLES20.GlLinear);
        GLES20.GlTexParameteri(
            GLES11Ext.GlTextureExternalOes,
            GLES20.GlTextureWrapS,
            GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(
            GLES11Ext.GlTextureExternalOes,
            GLES20.GlTextureWrapT,
            GLES20.GlClampToEdge);
        GLES20.GlUseProgram(_decoderProgram);
        GLES20.GlUniform1i(sourceLocation, 0);

        _surfaceTexture =
            new AndroidSurfaceTexture(
                _decoderTextureIds[0]);
        _surfaceTexture.SetDefaultBufferSize(
            _width,
            _height);
        _surfaceTexture.SetOnFrameAvailableListener(
            _frameListener,
            _callbackHandler);
        _decoderSurface =
            new AndroidSurface(_surfaceTexture);
    }

    private void MakeCurrent()
    {
        if (_display is null ||
            _context is null ||
            _surface is null ||
            !EGL14.EglMakeCurrent(
                _display,
                _surface,
                _surface,
                _context))
        {
            throw new InvalidOperationException(
                $"Android could not make the encoder EGL context current: 0x{EGL14.EglGetError():X}.");
        }
    }

    private static int CompileShader(
        int type,
        string source)
    {
        int shader = GLES20.GlCreateShader(type);
        GLES20.GlShaderSource(shader, source);
        GLES20.GlCompileShader(shader);
        var status = new int[1];
        GLES20.GlGetShaderiv(
            shader,
            GLES20.GlCompileStatus,
            status,
            0);
        if (status[0] == 0)
        {
            string? log = GLES20.GlGetShaderInfoLog(shader);
            GLES20.GlDeleteShader(shader);
            throw new InvalidOperationException(
                $"Android AHardwareBuffer blit shader compilation failed: {log}");
        }
        return shader;
    }

    private static void RequireExtension(
        string extensions,
        string required)
    {
        if (!extensions.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries)
            .Contains(required, StringComparer.Ordinal))
        {
            throw new NotSupportedException(
                $"Android EGL does not expose {required}.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (_display is not null)
        {
            if (_surface is not null &&
                _context is not null)
            {
                EGL14.EglMakeCurrent(
                    _display,
                    _surface,
                    _surface,
                    _context);
            }
            _surfaceTexture?
                .SetOnFrameAvailableListener(null);
            _decoderSurface?.Release();
            _decoderSurface?.Dispose();
            _decoderSurface = null;
            _surfaceTexture?.Release();
            _surfaceTexture?.Dispose();
            _surfaceTexture = null;
            if (_decoderTextureIds[0] != 0)
            {
                GLES20.GlDeleteTextures(
                    1,
                    _decoderTextureIds,
                    0);
            }
            if (_decoderProgram != 0)
            {
                GLES20.GlDeleteProgram(
                    _decoderProgram);
            }
            if (_decoderVertexShader != 0)
            {
                GLES20.GlDeleteShader(
                    _decoderVertexShader);
            }
            if (_decoderFragmentShader != 0)
            {
                GLES20.GlDeleteShader(
                    _decoderFragmentShader);
            }
            if (_program != 0)
            {
                GLES20.GlDeleteProgram(_program);
            }
            if (_vertexShader != 0)
            {
                GLES20.GlDeleteShader(_vertexShader);
            }
            if (_fragmentShader != 0)
            {
                GLES20.GlDeleteShader(_fragmentShader);
            }
            EGL14.EglMakeCurrent(
                _display,
                EGL14.EglNoSurface,
                EGL14.EglNoSurface,
                EGL14.EglNoContext);
            if (_surface is not null)
            {
                EGL14.EglDestroySurface(
                    _display,
                    _surface);
            }
            if (_context is not null)
            {
                EGL14.EglDestroyContext(
                    _display,
                    _context);
            }
            EGL14.EglReleaseThread();
            EGL14.EglTerminate(_display);
        }
        _quad.Dispose();
        _decoderQuad.Dispose();
        _callbackThread.QuitSafely();
        _callbackThread.Join();
        _callbackHandler.Dispose();
        _callbackThread.Dispose();
        _frameListener.Dispose();
        _frameAvailable.Dispose();
    }

    private sealed class DecoderFrameListener :
        Java.Lang.Object,
        AndroidSurfaceTexture.IOnFrameAvailableListener
    {
        private readonly AutoResetEvent _available;

        internal DecoderFrameListener(
            AutoResetEvent available)
        {
            _available = available;
        }

        public void OnFrameAvailable(
            AndroidSurfaceTexture? surfaceTexture)
        {
            _ = surfaceTexture;
            _available.Set();
        }
    }
}

internal static unsafe partial class AndroidEglNative
{
    [LibraryImport(
        "EGL",
        EntryPoint = "eglGetNativeClientBufferANDROID")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint GetNativeClientBuffer(
        nint hardwareBuffer);

    [LibraryImport(
        "EGL",
        EntryPoint = "eglCreateImageKHR")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CreateImage(
        nint display,
        nint context,
        int target,
        nint clientBuffer,
        int* attributes);

    [LibraryImport(
        "EGL",
        EntryPoint = "eglDestroyImageKHR")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I4)]
    internal static partial bool DestroyImage(
        nint display,
        nint image);

    [LibraryImport(
        "GLESv2",
        EntryPoint = "glEGLImageTargetTexture2DOES")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ImageTargetTexture2D(
        int target,
        nint image);

    [LibraryImport(
        "EGL",
        EntryPoint = "eglCreateSyncKHR")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CreateSync(
        nint display,
        int type,
        int* attributes);

    [LibraryImport(
        "EGL",
        EntryPoint = "eglWaitSyncKHR")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I4)]
    internal static partial bool WaitSync(
        nint display,
        nint sync,
        uint flags);

    [LibraryImport(
        "EGL",
        EntryPoint = "eglDestroySyncKHR")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    [return: MarshalAs(UnmanagedType.I4)]
    internal static partial bool DestroySync(
        nint display,
        nint sync);

    [LibraryImport(
        "EGL",
        EntryPoint = "eglDupNativeFenceFDANDROID")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DuplicateNativeFenceFd(
        nint display,
        nint sync);

    [LibraryImport(
        "libc",
        EntryPoint = "close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int CloseNative(int descriptor);

    internal static void Close(int descriptor)
    {
        if (descriptor >= 0)
        {
            _ = CloseNative(descriptor);
        }
    }
}
