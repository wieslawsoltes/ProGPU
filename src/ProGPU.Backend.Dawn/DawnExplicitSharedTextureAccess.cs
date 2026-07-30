using ProGPU.Backend;
using SW = Silk.NET.WebGPU;
using W = WebGpuSharp;
using WebGpuSharp.FFI;

namespace ProGPU.Backend.Dawn;

/// <summary>
/// Owns one explicit Dawn access scope over a native encoder allocation.
/// </summary>
/// <remarks>
/// Unlike the ordinary decoded-frame import seam, end access is controlled by
/// the caller so a native encoder can consume the exported GPU completion
/// fence immediately. Import and completion are O(1); no pixel storage is
/// allocated or copied.
/// </remarks>
public sealed class DawnExplicitSharedTextureAccess :
    IDisposable
{
    private ExplicitSharedTextureOwner? _owner;

    internal DawnExplicitSharedTextureAccess(
        GpuTexture texture,
        ExplicitSharedTextureOwner owner)
    {
        Texture = texture;
        _owner = owner;
    }

    public GpuTexture Texture { get; }

    public bool IsAccessActive =>
        Volatile.Read(ref _owner)?.IsAccessActive == true;

    /// <summary>
    /// Ends the active WebGPU access scope without exporting a fence.
    /// </summary>
    /// <remarks>
    /// This is the keyed-mutex hand-off used by DXGI shared textures. Dawn
    /// releases its keyed-mutex ownership as part of EndAccess; the native
    /// D3D11 consumer can then acquire the same allocation. The operation is
    /// O(1) and does not wait for the device or copy pixel storage.
    /// </remarks>
    public void EndAccess()
    {
        ExplicitSharedTextureOwner owner =
            Volatile.Read(ref _owner) ??
            throw new ObjectDisposedException(
                nameof(DawnExplicitSharedTextureAccess));
        lock (Texture.Context.RenderLock)
        {
            owner.EndAccess();
        }
    }

    public void EndAccessAndExportSyncFd(
        DawnSyncFdEndAccessResult destination)
    {
        ExplicitSharedTextureOwner owner =
            Volatile.Read(ref _owner) ??
            throw new ObjectDisposedException(
                nameof(DawnExplicitSharedTextureAccess));
        lock (Texture.Context.RenderLock)
        {
            owner.EndAccessAndExportSyncFd(destination);
        }
    }

    /// <summary>
    /// Begins the next WebGPU write access after consuming an EGL-produced
    /// sync-file fence. Dawn duplicates the descriptor before ProGPU closes
    /// the caller-owned original.
    /// </summary>
    public void BeginAccessAndConsumeSyncFd(
        int ownedSyncFd,
        bool initialized = true)
    {
        ExplicitSharedTextureOwner? owner =
            Volatile.Read(ref _owner);
        if (owner is null)
        {
            if (ownedSyncFd >= 0)
            {
                PosixFileDescriptor.Close(ownedSyncFd);
            }
            throw new ObjectDisposedException(
                nameof(DawnExplicitSharedTextureAccess));
        }
        lock (Texture.Context.RenderLock)
        {
            owner.BeginAccessAndConsumeSyncFd(
                ownedSyncFd,
                initialized);
        }
    }

    /// <summary>
    /// Begins a new access scope when the allocation has no outstanding
    /// external GPU work.
    /// </summary>
    public void BeginAccess(bool initialized = true)
    {
        ExplicitSharedTextureOwner owner =
            Volatile.Read(ref _owner) ??
            throw new ObjectDisposedException(
                nameof(DawnExplicitSharedTextureAccess));
        lock (Texture.Context.RenderLock)
        {
            owner.BeginAccess(initialized);
        }
    }

    public void Dispose()
    {
        ExplicitSharedTextureOwner? owner =
            Interlocked.Exchange(ref _owner, null);
        if (owner is null)
        {
            return;
        }

        Texture.Dispose();
    }
}

internal sealed class ExplicitSharedTextureOwner :
    IDisposable
{
    private DawnSharedTextureMemory? _sharedMemory;
    private readonly DawnSharedTextureMemoryFeature _feature;
    private IDisposable? _nativeOwner;
    private readonly TextureHandle _texture;
    private int _accessActive = 1;

    internal ExplicitSharedTextureOwner(
        DawnSharedTextureMemory sharedMemory,
        TextureHandle texture,
        IDisposable nativeOwner,
        DawnSharedTextureMemoryFeature feature)
    {
        _sharedMemory = sharedMemory;
        _texture = texture;
        _nativeOwner = nativeOwner;
        _feature = feature;
    }

    internal bool IsAccessActive =>
        Volatile.Read(ref _accessActive) == 1;

    internal void EndAccessAndExportSyncFd(
        DawnSyncFdEndAccessResult destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (Interlocked.CompareExchange(
                ref _accessActive,
                2,
                1) != 1)
        {
            throw new InvalidOperationException(
                "The shared texture access scope is no longer active.");
        }

        DawnSharedTextureMemory sharedMemory =
            Volatile.Read(ref _sharedMemory) ??
            throw new ObjectDisposedException(
                nameof(DawnExplicitSharedTextureAccess));
        try
        {
            sharedMemory.EndAccessAndExportSyncFd(
                _texture,
                destination);
        }
        finally
        {
            // EndAccess consumes the access scope even when post-call fence
            // validation fails. Keep the state terminal.
            Volatile.Write(ref _accessActive, 0);
        }
    }

    internal void EndAccess()
    {
        if (Interlocked.CompareExchange(
                ref _accessActive,
                2,
                1) != 1)
        {
            throw new InvalidOperationException(
                "The shared texture access scope is no longer active.");
        }

        DawnSharedTextureMemory sharedMemory =
            Volatile.Read(ref _sharedMemory) ??
            throw new ObjectDisposedException(
                nameof(DawnExplicitSharedTextureAccess));
        try
        {
            sharedMemory.EndAccess(_texture);
        }
        finally
        {
            // Dawn consumes the access scope even when validation of the
            // native hand-off fails.
            Volatile.Write(ref _accessActive, 0);
        }
    }

    internal void BeginAccessAndConsumeSyncFd(
        int ownedSyncFd,
        bool initialized)
    {
        if (ownedSyncFd < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ownedSyncFd));
        }
        if (Interlocked.CompareExchange(
                ref _accessActive,
                2,
                0) != 0)
        {
            PosixFileDescriptor.Close(ownedSyncFd);
            throw new InvalidOperationException(
                "The shared texture access scope is already active.");
        }

        try
        {
            using DawnSharedFence waitFence =
                _feature.ImportSyncFd(ownedSyncFd);
            DawnSharedTextureMemory sharedMemory =
                Volatile.Read(ref _sharedMemory) ??
                throw new ObjectDisposedException(
                    nameof(DawnExplicitSharedTextureAccess));
            sharedMemory.BeginAccess(
                _texture,
                initialized,
                waitFence,
                waitValue: 1);
            Volatile.Write(ref _accessActive, 1);
        }
        catch
        {
            Volatile.Write(ref _accessActive, 0);
            throw;
        }
        finally
        {
            PosixFileDescriptor.Close(ownedSyncFd);
        }
    }

    internal void BeginAccess(bool initialized)
    {
        if (Interlocked.CompareExchange(
                ref _accessActive,
                2,
                0) != 0)
        {
            throw new InvalidOperationException(
                "The shared texture access scope is already active.");
        }

        try
        {
            DawnSharedTextureMemory sharedMemory =
                Volatile.Read(ref _sharedMemory) ??
                throw new ObjectDisposedException(
                    nameof(DawnExplicitSharedTextureAccess));
            sharedMemory.BeginAccess(
                _texture,
                initialized);
            Volatile.Write(ref _accessActive, 1);
        }
        catch
        {
            Volatile.Write(ref _accessActive, 0);
            throw;
        }
    }

    public void Dispose()
    {
        DawnSharedTextureMemory? sharedMemory =
            Interlocked.Exchange(ref _sharedMemory, null);
        IDisposable? nativeOwner =
            Interlocked.Exchange(ref _nativeOwner, null);
        try
        {
            if (sharedMemory is not null &&
                Interlocked.Exchange(
                    ref _accessActive,
                    0) == 1)
            {
                sharedMemory.EndAccess(_texture);
            }
        }
        finally
        {
            sharedMemory?.Dispose();
            nativeOwner?.Dispose();
        }
    }
}

public sealed unsafe partial class DawnGpuContext
{
    /// <summary>
    /// Imports a keyed-mutex DXGI shared texture as an explicit WebGPU render
    /// attachment for Media Foundation encoder hand-off.
    /// </summary>
    /// <remarks>
    /// A successful call transfers <paramref name="nativeOwner"/> to the
    /// returned access. The caller ends WebGPU access before acquiring the
    /// keyed mutex from D3D11 and begins it again after releasing the mutex.
    /// Import and ownership transitions are O(1); no pixel storage is mapped
    /// or copied.
    /// </remarks>
    public bool TryImportDxgiRenderTarget(
        in ProGpuExternalTextureDescriptor descriptor,
        IDisposable nativeOwner,
        out DawnExplicitSharedTextureAccess access)
    {
        ArgumentNullException.ThrowIfNull(nativeOwner);
        access = null!;
        bool supportedFormat =
            descriptor.Format is
                SW.TextureFormat.Bgra8Unorm or
                SW.TextureFormat.Rgba8Unorm;
        if (Context.IsDisposed ||
            Context.AdapterBackendType != SW.BackendType.D3D12 ||
            descriptor.HandleKind !=
                ProGpuExternalTextureHandleKind.DxgiSharedHandle ||
            descriptor.Handle == 0 ||
            descriptor.Width == 0 ||
            descriptor.Height == 0 ||
            !descriptor.UsesKeyedMutex ||
            !supportedFormat ||
            (descriptor.Usage & SW.TextureUsage.RenderAttachment) == 0 ||
            !Adapter.HasFeature(
                DawnSharedTextureMemoryFeatures
                    .SharedTextureMemoryDXGISharedHandle))
        {
            return false;
        }

        W.TextureFormat expectedFormat =
            descriptor.Format ==
                SW.TextureFormat.Bgra8Unorm
                ? W.TextureFormat.BGRA8Unorm
                : W.TextureFormat.RGBA8Unorm;
        DawnSharedTextureMemory? sharedMemory = null;
        TextureHandle importedTexture = TextureHandle.Null;
        bool accessBegan = false;
        try
        {
            sharedMemory =
                SharedTextureMemory.ImportDXGISharedHandle(
                    descriptor.Handle,
                    useKeyedMutex: true);
            DawnSharedTextureMemoryProperties properties =
                sharedMemory.GetProperties();
            W.TextureUsage requestedUsage =
                (W.TextureUsage)descriptor.Usage;
            if (properties.Size.Width != descriptor.Width ||
                properties.Size.Height != descriptor.Height ||
                properties.Format != expectedFormat ||
                (properties.Usage & requestedUsage) !=
                    requestedUsage)
            {
                sharedMemory.Dispose();
                return false;
            }

            importedTexture = sharedMemory.CreateTexture(
                requestedUsage,
                "ProGPU Windows encoder DXGI render target"u8);
            sharedMemory.BeginAccess(
                importedTexture,
                descriptor.IsInitialized);
            accessBegan = true;
            var owner = new ExplicitSharedTextureOwner(
                sharedMemory,
                importedTexture,
                nativeOwner,
                SharedTextureMemory);
            sharedMemory = null;
            TextureHandle ownedTexture = importedTexture;
            importedTexture = TextureHandle.Null;
            GpuTexture texture = GpuTexture.WrapOwnedExternal(
                Context,
                (SW.Texture*)ownedTexture.GetAddress(),
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.Usage,
                "Imported Windows encoder DXGI render target",
                descriptor.AlphaMode,
                owner);
            access =
                new DawnExplicitSharedTextureAccess(
                    texture,
                    owner);
            return true;
        }
        catch
        {
            if (accessBegan &&
                sharedMemory is not null &&
                importedTexture != TextureHandle.Null)
            {
                try
                {
                    sharedMemory.EndAccess(importedTexture);
                }
                catch
                {
                }
            }
            if (importedTexture != TextureHandle.Null)
            {
                importedTexture.Release();
            }
            sharedMemory?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Imports an RGBA AHardwareBuffer as a WebGPU render attachment whose
    /// end-access fence is exported explicitly for EGL/MediaCodec.
    /// </summary>
    /// <remarks>
    /// A successful call transfers <paramref name="nativeOwner"/> to the
    /// returned access. A false result leaves it caller-owned.
    /// </remarks>
    public bool TryImportAHardwareBufferRenderTarget(
        in ProGpuExternalTextureDescriptor descriptor,
        IDisposable nativeOwner,
        out DawnExplicitSharedTextureAccess access)
    {
        ArgumentNullException.ThrowIfNull(nativeOwner);
        access = null!;
        if (Context.IsDisposed ||
            Context.AdapterBackendType != SW.BackendType.Vulkan ||
            descriptor.HandleKind !=
                ProGpuExternalTextureHandleKind.AndroidHardwareBuffer ||
            descriptor.Handle == 0 ||
            descriptor.Width == 0 ||
            descriptor.Height == 0 ||
            descriptor.Format != SW.TextureFormat.Rgba8Unorm ||
            (descriptor.Usage & SW.TextureUsage.RenderAttachment) == 0 ||
            !Adapter.HasFeature(
                DawnSharedTextureMemoryFeatures
                    .SharedTextureMemoryAHardwareBuffer) ||
            !Adapter.HasFeature(
                DawnSharedTextureMemoryFeatures
                    .SharedFenceSyncFD))
        {
            return false;
        }

        DawnSharedTextureMemory? sharedMemory = null;
        TextureHandle importedTexture = TextureHandle.Null;
        bool accessBegan = false;
        try
        {
            sharedMemory =
                SharedTextureMemory.ImportAHardwareBuffer(
                    descriptor.Handle);
            DawnSharedTextureMemoryProperties properties =
                sharedMemory.GetProperties();
            W.TextureUsage requestedUsage =
                (W.TextureUsage)descriptor.Usage;
            if (properties.Size.Width != descriptor.Width ||
                properties.Size.Height != descriptor.Height ||
                properties.Format != W.TextureFormat.RGBA8Unorm ||
                (properties.Usage & requestedUsage) !=
                    requestedUsage)
            {
                sharedMemory.Dispose();
                return false;
            }

            importedTexture = sharedMemory.CreateTexture(
                requestedUsage,
                "ProGPU Android encoder render target"u8);
            sharedMemory.BeginAccess(
                importedTexture,
                descriptor.IsInitialized);
            accessBegan = true;
            var owner = new ExplicitSharedTextureOwner(
                sharedMemory,
                importedTexture,
                nativeOwner,
                SharedTextureMemory);
            sharedMemory = null;
            TextureHandle ownedTexture = importedTexture;
            importedTexture = TextureHandle.Null;
            GpuTexture texture = GpuTexture.WrapOwnedExternal(
                Context,
                (SW.Texture*)ownedTexture.GetAddress(),
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.Usage,
                "Imported Android encoder render target",
                descriptor.AlphaMode,
                owner);
            access =
                new DawnExplicitSharedTextureAccess(
                    texture,
                    owner);
            return true;
        }
        catch
        {
            if (accessBegan &&
                sharedMemory is not null &&
                importedTexture != TextureHandle.Null)
            {
                try
                {
                    sharedMemory.EndAccess(importedTexture);
                }
                catch
                {
                }
            }
            if (importedTexture != TextureHandle.Null)
            {
                importedTexture.Release();
            }
            sharedMemory?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Imports one single-plane Linux DMA-BUF view as an explicit WebGPU
    /// render attachment and exposes the queue-completion SyncFD at end
    /// access.
    /// </summary>
    /// <remarks>
    /// Multi-plane encoder allocations are represented by one R8 luma view
    /// and one RG8 chroma view. A successful call transfers
    /// <paramref name="nativeOwner"/>; a false result leaves it caller-owned.
    /// Import is O(1) and does not map or copy the allocation.
    /// </remarks>
    public bool TryImportDmaBufRenderTarget(
        in ProGpuExternalTextureDescriptor descriptor,
        IDisposable nativeOwner,
        out DawnExplicitSharedTextureAccess access)
    {
        ArgumentNullException.ThrowIfNull(nativeOwner);
        access = null!;
        bool supportedFormat =
            descriptor.Format is
                SW.TextureFormat.R8Unorm or
                SW.TextureFormat.RG8Unorm;
        if (Context.IsDisposed ||
            Context.AdapterBackendType != SW.BackendType.Vulkan ||
            descriptor.HandleKind !=
                ProGpuExternalTextureHandleKind.DmaBuf ||
            descriptor.Handle < 0 ||
            descriptor.Width == 0 ||
            descriptor.Height == 0 ||
            descriptor.DmaBuf.PlaneCount != 1 ||
            !supportedFormat ||
            (descriptor.Usage &
             SW.TextureUsage.RenderAttachment) == 0 ||
            !Adapter.HasFeature(
                DawnSharedTextureMemoryFeatures
                    .SharedTextureMemoryDmaBuf) ||
            !Adapter.HasFeature(
                DawnSharedTextureMemoryFeatures
                    .SharedFenceSyncFD))
        {
            return false;
        }

        W.TextureFormat expectedFormat =
            descriptor.Format ==
                SW.TextureFormat.R8Unorm
                ? W.TextureFormat.R8Unorm
                : W.TextureFormat.RG8Unorm;
        DawnSharedTextureMemory? sharedMemory = null;
        TextureHandle importedTexture = TextureHandle.Null;
        bool accessBegan = false;
        try
        {
            sharedMemory =
                SharedTextureMemory.ImportDmaBuf(
                    descriptor.Width,
                    descriptor.Height,
                    descriptor.DmaBuf);
            DawnSharedTextureMemoryProperties properties =
                sharedMemory.GetProperties();
            W.TextureUsage requestedUsage =
                (W.TextureUsage)descriptor.Usage;
            if (properties.Size.Width != descriptor.Width ||
                properties.Size.Height != descriptor.Height ||
                properties.Format != expectedFormat ||
                (properties.Usage & requestedUsage) !=
                    requestedUsage)
            {
                sharedMemory.Dispose();
                return false;
            }

            importedTexture = sharedMemory.CreateTexture(
                requestedUsage,
                "ProGPU Linux encoder DMA-BUF plane"u8);
            sharedMemory.BeginAccess(
                importedTexture,
                descriptor.IsInitialized);
            accessBegan = true;
            var owner = new ExplicitSharedTextureOwner(
                sharedMemory,
                importedTexture,
                nativeOwner,
                SharedTextureMemory);
            sharedMemory = null;
            TextureHandle ownedTexture = importedTexture;
            importedTexture = TextureHandle.Null;
            GpuTexture texture = GpuTexture.WrapOwnedExternal(
                Context,
                (SW.Texture*)ownedTexture.GetAddress(),
                descriptor.Width,
                descriptor.Height,
                descriptor.Format,
                descriptor.Usage,
                "Imported Linux encoder DMA-BUF plane",
                descriptor.AlphaMode,
                owner);
            access =
                new DawnExplicitSharedTextureAccess(
                    texture,
                    owner);
            return true;
        }
        catch
        {
            if (accessBegan &&
                sharedMemory is not null &&
                importedTexture != TextureHandle.Null)
            {
                try
                {
                    sharedMemory.EndAccess(importedTexture);
                }
                catch
                {
                }
            }
            if (importedTexture != TextureHandle.Null)
            {
                importedTexture.Release();
            }
            sharedMemory?.Dispose();
            throw;
        }
    }
}
