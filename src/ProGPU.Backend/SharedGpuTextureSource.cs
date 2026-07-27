using System;
using System.Collections.Generic;
using System.Threading;

namespace ProGPU.Backend;

/// <summary>
/// Typed, same-device texture-sharing contract used by framework compositors.
/// Acquired leases keep the source texture alive without copying pixels or
/// exposing a native pointer as managed ownership.
/// </summary>
public interface ISharedGpuTextureSource
{
    GpuTextureLease AcquireTexture();
}

/// <summary>
/// Owns one GPU texture and releases it after the owner and every acquired
/// lease have been disposed.
/// </summary>
public sealed class SharedGpuTextureSource : ISharedGpuTextureSource, IDisposable
{
    private static readonly object s_registryLock = new();
    private static readonly Dictionary<nint, SharedGpuTextureSource>
        s_registry = new();
    private static long s_nextHandle;
    private static int s_compositionImporterRegistered;

    /// <summary>
    /// Capability identifier used by ProGPU composition interop.
    /// </summary>
    public const string CompositionHandleType =
        "ProGPU.SameDevice.WebGPUTexture";

    public static bool IsCompositionImporterRegistered =>
        Volatile.Read(ref s_compositionImporterRegistered) != 0;

    public static void RegisterCompositionImporter()
    {
        Volatile.Write(ref s_compositionImporterRegistered, 1);
    }

    private readonly SharedState _state;
    private int _disposed;

    public SharedGpuTextureSource(GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.IsDisposed)
        {
            throw new ObjectDisposedException(nameof(texture));
        }

        _state = new SharedState(texture);
        Handle = (nint)Interlocked.Increment(ref s_nextHandle);
        lock (s_registryLock)
        {
            s_registry.Add(Handle, this);
        }
    }

    /// <summary>
    /// Opaque process-local token for the composition platform-handle seam.
    /// It is never a native texture pointer.
    /// </summary>
    public nint Handle { get; }

    public GpuTextureLease AcquireTexture()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(
                nameof(SharedGpuTextureSource));
        }

        return _state.Acquire();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            lock (s_registryLock)
            {
                if (s_registry.TryGetValue(
                        Handle,
                        out SharedGpuTextureSource? registered) &&
                    ReferenceEquals(registered, this))
                {
                    s_registry.Remove(Handle);
                }
            }
            _state.Release();
        }
    }

    public static bool TryAcquire(
        nint handle,
        out SharedGpuTextureSource? source,
        out GpuTextureLease? lease)
    {
        lock (s_registryLock)
        {
            if (!s_registry.TryGetValue(handle, out source))
            {
                lease = null;
                return false;
            }

            try
            {
                lease = source.AcquireTexture();
                return true;
            }
            catch (ObjectDisposedException)
            {
                source = null;
                lease = null;
                return false;
            }
        }
    }

    internal sealed class SharedState
    {
        private GpuTexture? _texture;
        private int _references = 1;

        public SharedState(GpuTexture texture)
        {
            _texture = texture;
        }

        public GpuTextureLease Acquire()
        {
            while (true)
            {
                int references = Volatile.Read(ref _references);
                if (references == 0)
                {
                    throw new ObjectDisposedException(
                        nameof(SharedGpuTextureSource));
                }

                if (Interlocked.CompareExchange(
                        ref _references,
                        references + 1,
                        references) == references)
                {
                    GpuTexture? texture = Volatile.Read(ref _texture);
                    if (texture is null || texture.IsDisposed)
                    {
                        Release();
                        throw new ObjectDisposedException(
                            nameof(SharedGpuTextureSource));
                    }

                    return new GpuTextureLease(this, texture);
                }
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
            {
                Interlocked.Exchange(ref _texture, null)?.Dispose();
            }
        }
    }
}

/// <summary>
/// A borrowed reference to a shared GPU texture.
/// </summary>
public sealed class GpuTextureLease : IDisposable
{
    private SharedGpuTextureSource.SharedState? _state;

    internal GpuTextureLease(
        SharedGpuTextureSource.SharedState state,
        GpuTexture texture)
    {
        _state = state;
        Texture = texture;
    }

    public GpuTexture Texture { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _state, null)?.Release();
    }
}
