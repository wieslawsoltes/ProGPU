using System;
using System.Threading;
using Avalonia.Platform;
#if AVALONIA11
using Avalonia.Controls.Platform.Surfaces;
#else
using Avalonia.Platform.Surfaces;
#endif

namespace Avalonia.SilkNet
{
    public class SilkNetFramebufferManager : IFramebufferPlatformSurface, IDisposable
    {
        private readonly Silk.NET.Windowing.IWindow _window;
        private readonly SilkNetFramebufferAddressProvider _addressProvider = new();
        private readonly object _sync = new();
        private readonly Action _unlock;
        private int _disposed;

        public SilkNetFramebufferManager(Silk.NET.Windowing.IWindow window)
        {
            _window = window;
            _unlock = Unlock;
        }

        public bool IsReady =>
            Volatile.Read(ref _disposed) == 0 &&
            TryGetRenderableFramebufferSize(out _);

        public ILockedFramebuffer Lock()
        {
            Monitor.Enter(_sync);
            SilkNetLockedFramebuffer? framebuffer = null;
            try
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    !TryGetRenderableFramebufferSize(out PixelSize size))
                {
                    throw new RenderTargetNotReadyException();
                }

                var stride = checked(size.Width * 4);
                var totalBytes = checked(stride * size.Height);

                return framebuffer = new SilkNetLockedFramebuffer(
                    _addressProvider,
                    totalBytes,
                    size,
                    stride,
                    GetFramebufferDpi(size),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul,
                    _unlock,
                    _window);
            }
            finally
            {
                if (framebuffer is null)
                    Monitor.Exit(_sync);
            }
        }

        public IFramebufferRenderTarget CreateFramebufferRenderTarget() => new RenderTarget(this);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            lock (_sync)
                _addressProvider.Dispose();
        }

        private void Unlock() => Monitor.Exit(_sync);

#if !AVALONIA11
        private PlatformRenderTargetState State => Volatile.Read(ref _disposed) != 0
            ? PlatformRenderTargetState.Disposed
            : TryGetRenderableFramebufferSize(out _)
                ? PlatformRenderTargetState.Ready
                : PlatformRenderTargetState.NotReadyTryLater;
#endif

        private bool TryGetRenderableFramebufferSize(out PixelSize size)
        {
            size = default;
            if (!_window.IsInitialized)
                return false;

            var framebufferSize = _window.FramebufferSize;
            if (framebufferSize.X <= 0 || framebufferSize.Y <= 0)
                return false;

            size = new PixelSize(framebufferSize.X, framebufferSize.Y);
            return true;
        }

        private Vector GetFramebufferDpi(PixelSize framebufferSize)
        {
            var logicalSize = _window.Size;
            double scaleX = logicalSize.X > 0
                ? (double)framebufferSize.Width / logicalSize.X
                : 1.0;
            double scaleY = logicalSize.Y > 0
                ? (double)framebufferSize.Height / logicalSize.Y
                : 1.0;
            if (!double.IsFinite(scaleX) || scaleX <= 0)
                scaleX = 1.0;
            if (!double.IsFinite(scaleY) || scaleY <= 0)
                scaleY = 1.0;

            return new Vector(96.0 * scaleX, 96.0 * scaleY);
        }

        private sealed class RenderTarget :
#if AVALONIA11
            IFramebufferRenderTargetWithProperties
#else
            IFramebufferRenderTarget
#endif
        {
            private SilkNetFramebufferManager? _manager;

            public RenderTarget(SilkNetFramebufferManager manager)
            {
                _manager = manager;
            }

#if AVALONIA11
            public bool RetainsFrameContents => true;

            public ILockedFramebuffer Lock() =>
                (_manager ?? throw new RenderTargetNotReadyException()).Lock();

            public ILockedFramebuffer Lock(out FramebufferLockProperties properties)
            {
                properties = new FramebufferLockProperties(true);
                return Lock();
            }
#else
            public PlatformRenderTargetState State =>
                _manager?.State ?? PlatformRenderTargetState.Disposed;

            public ILockedFramebuffer Lock(
                IRenderTarget.RenderTargetSceneInfo sceneInfo,
                out FramebufferLockProperties properties)
            {
                properties = default;
                return (_manager ?? throw new RenderTargetNotReadyException()).Lock();
            }
#endif

            public void Dispose() => _manager = null;
        }
    }
}
