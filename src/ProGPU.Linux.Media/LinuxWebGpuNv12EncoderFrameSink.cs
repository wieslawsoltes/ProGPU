using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProGPU.Backend;
using ProGPU.Backend.Dawn;
using Silk.NET.WebGPU;

namespace ProGPU.Linux.Media;

/// <summary>
/// Bounded Linux WebGPU effect/generated-frame lane backed by GBM NV12
/// DMA-BUF allocations that transfer directly into a V4L2 encoder OUTPUT
/// queue.
/// </summary>
/// <remarks>
/// Three reusable targets bound memory independently of frame count. Each
/// frame performs two WebGPU plane writes in one command buffer, imports the
/// resulting SyncFD into the DMA-BUF reservation object, and queues the same
/// allocation to V4L2. No decoded or rendered pixel is CPU mapped or copied.
/// </remarks>
internal sealed class LinuxWebGpuNv12EncoderFrameSink :
    IDisposable
{
    private const int TargetCount =
        GpuNv12Processor.MaxInFlightSlots;
    private readonly object _gate = new();
    private readonly LinuxGbmDevice _device;
    private readonly TargetSlot[] _slots;
    private readonly Queue<int> _available =
        new(TargetCount);
    private int _disposed;

    private LinuxWebGpuNv12EncoderFrameSink(
        LinuxGbmDevice device,
        TargetSlot[] slots)
    {
        _device = device;
        _slots = slots;
        for (int index = 0;
             index < slots.Length;
             index++)
        {
            _available.Enqueue(index);
        }
    }

    internal static bool TryCreate(
        DawnGpuContext dawn,
        uint width,
        uint height,
        out LinuxWebGpuNv12EncoderFrameSink sink)
    {
        ArgumentNullException.ThrowIfNull(dawn);
        sink = null!;
        if (!OperatingSystem.IsLinux() ||
            width == 0 ||
            height == 0 ||
            dawn.Context.AdapterBackendType !=
                BackendType.Vulkan ||
            !LinuxGbmNative.IsAvailable())
        {
            return false;
        }

        string[] renderNodes;
        try
        {
            renderNodes = Directory.GetFiles(
                "/dev/dri",
                "renderD*",
                SearchOption.TopDirectoryOnly);
            Array.Sort(
                renderNodes,
                StringComparer.Ordinal);
        }
        catch
        {
            return false;
        }

        foreach (string renderNode in renderNodes)
        {
            LinuxGbmDevice? device = null;
            TargetSlot[] slots = [];
            try
            {
                device = LinuxGbmDevice.Open(renderNode);
                slots = new TargetSlot[TargetCount];
                for (int index = 0;
                     index < TargetCount;
                     index++)
                {
                    slots[index] = CreateSlot(
                        dawn,
                        device,
                        index,
                        width,
                        height);
                }
                sink =
                    new LinuxWebGpuNv12EncoderFrameSink(
                        device,
                        slots);
                return true;
            }
            catch (NotSupportedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }

            for (int index = 0;
                 index < slots.Length;
                 index++)
            {
                slots[index]?.Dispose();
            }
            device?.Dispose();
        }

        return false;
    }

    internal bool TryProcessFrame(
        in V4l2DecodedFrame frame,
        TimeSpan presentationTime,
        float saturation,
        float grayscale,
        V4l2StatefulVideoEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (frame.PixelFormat !=
                V4l2DecodedPixelFormat.Nv12 ||
            !frame.TryCreatePlanarExternalDescriptors(
                out ProGpuExternalTextureDescriptor
                    sourceLumaDescriptor,
                out ProGpuExternalTextureDescriptor
                    sourceChromaDescriptor) ||
            !encoder.CanQueueFrame)
        {
            return false;
        }

        TargetSlot slot;
        lock (_gate)
        {
            if (_available.Count == 0)
            {
                return false;
            }
            slot = _slots[_available.Dequeue()];
        }

        GpuTexture? sourceLuma = null;
        GpuTexture? sourceChroma = null;
        SharedOwnerRoot? sourceOwner =
            new(frame.Owner);
        SharedOwnerLease? lumaOwner =
            sourceOwner.CreateLease();
        SharedOwnerLease? chromaOwner =
            sourceOwner.CreateLease();
        bool submissionStarted = false;
        try
        {
            if (!slot.Context.TryImportExternalTexture(
                    in sourceLumaDescriptor,
                    lumaOwner,
                    out sourceLuma))
            {
                throw new NotSupportedException(
                    "Dawn could not import the decoded NV12 luma DMA-BUF.");
            }
            lumaOwner = null;
            if (!slot.Context.TryImportExternalTexture(
                    in sourceChromaDescriptor,
                    chromaOwner,
                    out sourceChroma))
            {
                throw new NotSupportedException(
                    "Dawn could not import the decoded NV12 chroma DMA-BUF.");
            }
            chromaOwner = null;

            GpuNv12Processor.Process(
                sourceLuma,
                sourceChroma,
                slot.LumaAccess.Texture,
                slot.ChromaAccess.Texture,
                saturation,
                grayscale,
                slot.Index);
            submissionStarted = true;
            SubmitRenderedSlot(
                slot,
                presentationTime,
                encoder);
            return true;
        }
        finally
        {
            sourceLuma?.Dispose();
            sourceChroma?.Dispose();
            lumaOwner?.Dispose();
            chromaOwner?.Dispose();
            sourceOwner.Dispose();
            if (!submissionStarted)
            {
                ReturnAvailable(slot);
            }
        }
    }

    internal bool TryProcessColorFrame(
        uint argbColor,
        TimeSpan presentationTime,
        float saturation,
        float grayscale,
        V4l2StatefulVideoEncoder encoder)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (!encoder.CanQueueFrame)
        {
            return false;
        }

        TargetSlot slot;
        lock (_gate)
        {
            if (_available.Count == 0)
            {
                return false;
            }
            slot = _slots[_available.Dequeue()];
        }

        bool submissionStarted = false;
        try
        {
            GpuNv12Processor.RenderSolidColor(
                slot.LumaAccess.Texture,
                slot.ChromaAccess.Texture,
                argbColor,
                saturation,
                grayscale);
            submissionStarted = true;
            SubmitRenderedSlot(
                slot,
                presentationTime,
                encoder);
            return true;
        }
        finally
        {
            if (!submissionStarted)
            {
                ReturnAvailable(slot);
            }
        }
    }

    private void SubmitRenderedSlot(
        TargetSlot slot,
        TimeSpan presentationTime,
        V4l2StatefulVideoEncoder encoder)
    {
        bool lumaAccessEnded = false;
        bool chromaAccessEnded = false;
        bool queuedToEncoder = false;
        int recoveryLumaFence = -1;
        int recoveryChromaFence = -1;
        EncoderSlotLease? encoderLease = null;
        try
        {
            slot.LumaAccess.EndAccessAndExportSyncFd(
                slot.LumaFence);
            lumaAccessEnded = true;
            slot.ChromaAccess.EndAccessAndExportSyncFd(
                slot.ChromaFence);
            chromaAccessEnded = true;

            // One serial WebGPU queue submitted both plane passes. The later
            // chroma EndAccess fence therefore covers luma and chroma writes.
            slot.LumaFence.Dispose();
            int completionFence =
                slot.ChromaFence.DetachHandle();
            try
            {
                recoveryLumaFence =
                    LinuxMediaNative.Duplicate(
                        completionFence);
                recoveryChromaFence =
                    LinuxMediaNative.Duplicate(
                        completionFence);
                slot.Buffer.ImportWriteFence(
                    completionFence);
            }
            finally
            {
                LinuxMediaNative.Close(
                    completionFence);
            }

            encoderLease =
                new EncoderSlotLease(this, slot);
            ProGpuDmaBufDescriptor encoderBuffer =
                slot.Buffer.EncoderDescriptor;
            if (!encoder.TryQueueFrame(
                    in encoderBuffer,
                    presentationTime,
                    encoderLease))
            {
                throw new InvalidOperationException(
                    "A V4L2 encoder slot disappeared after its bounded availability check.");
            }
            queuedToEncoder = true;
            encoderLease = null;
            CloseIfValid(
                Interlocked.Exchange(
                    ref recoveryLumaFence,
                    -1));
            CloseIfValid(
                Interlocked.Exchange(
                    ref recoveryChromaFence,
                    -1));
        }
        finally
        {
            if (!queuedToEncoder)
            {
                encoderLease?.Abandon();
                lumaAccessEnded |=
                    !slot.LumaAccess.IsAccessActive;
                chromaAccessEnded |=
                    !slot.ChromaAccess.IsAccessActive;
                if (lumaAccessEnded &&
                    recoveryLumaFence < 0 &&
                    slot.LumaFence.HasFence)
                {
                    recoveryLumaFence =
                        slot.LumaFence.DetachHandle();
                }
                if (chromaAccessEnded &&
                    recoveryChromaFence < 0 &&
                    slot.ChromaFence.HasFence)
                {
                    recoveryChromaFence =
                        slot.ChromaFence.DetachHandle();
                }
                if (lumaAccessEnded ||
                    chromaAccessEnded)
                {
                    RearmAfterFailure(
                        slot,
                        lumaAccessEnded,
                        chromaAccessEnded,
                        ref recoveryLumaFence,
                        ref recoveryChromaFence);
                }
                else
                {
                    ReturnAvailable(slot);
                }
            }
            CloseIfValid(
                Interlocked.Exchange(
                    ref recoveryLumaFence,
                    -1));
            CloseIfValid(
                Interlocked.Exchange(
                    ref recoveryChromaFence,
                    -1));
        }
    }

    private void RearmAfterFailure(
        TargetSlot slot,
        bool lumaAccessEnded,
        bool chromaAccessEnded,
        ref int lumaFence,
        ref int chromaFence)
    {
        try
        {
            if (lumaAccessEnded)
            {
                if (lumaFence < 0)
                {
                    throw new InvalidOperationException(
                        "The ended luma access has no recovery fence.");
                }
                int owned =
                    Interlocked.Exchange(
                        ref lumaFence,
                        -1);
                slot.LumaAccess
                    .BeginAccessAndConsumeSyncFd(
                        owned);
            }
            if (chromaAccessEnded)
            {
                if (chromaFence < 0)
                {
                    throw new InvalidOperationException(
                        "The ended chroma access has no recovery fence.");
                }
                int owned =
                    Interlocked.Exchange(
                        ref chromaFence,
                        -1);
                slot.ChromaAccess
                    .BeginAccessAndConsumeSyncFd(
                        owned);
            }
            ReturnAvailable(slot);
        }
        catch
        {
            // The original render/queue failure remains authoritative. A
            // terminal target is not returned to the reusable ring.
        }
    }

    private void ReturnFromEncoder(TargetSlot slot)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        slot.LumaAccess.BeginAccess(initialized: true);
        slot.ChromaAccess.BeginAccess(initialized: true);
        ReturnAvailable(slot);
    }

    private void ReturnAvailable(TargetSlot slot)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _available.Enqueue(slot.Index);
            }
        }
    }

    private static void CloseIfValid(int fileDescriptor)
    {
        if (fileDescriptor >= 0)
        {
            LinuxMediaNative.Close(fileDescriptor);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
        {
            return;
        }
        for (int index = 0;
             index < _slots.Length;
             index++)
        {
            _slots[index].Dispose();
        }
        _device.Dispose();
    }

    private static TargetSlot CreateSlot(
        DawnGpuContext dawn,
        LinuxGbmDevice device,
        int index,
        uint width,
        uint height)
    {
        LinuxGbmNv12Buffer buffer =
            device.CreateNv12(width, height);
        DawnExplicitSharedTextureAccess? luma = null;
        DawnExplicitSharedTextureAccess? chroma = null;
        BorrowedGbmLifetime? lumaOwner =
            new(buffer);
        BorrowedGbmLifetime? chromaOwner =
            new(buffer);
        try
        {
            ProGpuExternalTextureDescriptor
                lumaDescriptor =
                    buffer.CreateLumaDescriptor();
            if (!dawn.TryImportDmaBufRenderTarget(
                    in lumaDescriptor,
                    lumaOwner,
                    out luma))
            {
                throw new NotSupportedException(
                    "The Dawn/Vulkan device cannot render to the GBM NV12 luma plane.");
            }
            lumaOwner = null;
            ProGpuExternalTextureDescriptor
                chromaDescriptor =
                    buffer.CreateChromaDescriptor();
            if (!dawn.TryImportDmaBufRenderTarget(
                    in chromaDescriptor,
                    chromaOwner,
                    out chroma))
            {
                throw new NotSupportedException(
                    "The Dawn/Vulkan device cannot render to the GBM NV12 chroma plane.");
            }
            chromaOwner = null;
            var slot = new TargetSlot(
                index,
                dawn.Context,
                buffer,
                luma,
                chroma);
            buffer = null!;
            luma = null;
            chroma = null;
            return slot;
        }
        finally
        {
            lumaOwner?.Dispose();
            chromaOwner?.Dispose();
            luma?.Dispose();
            chroma?.Dispose();
            buffer?.Dispose();
        }
    }

    private sealed class TargetSlot : IDisposable
    {
        internal TargetSlot(
            int index,
            WgpuContext context,
            LinuxGbmNv12Buffer buffer,
            DawnExplicitSharedTextureAccess lumaAccess,
            DawnExplicitSharedTextureAccess chromaAccess)
        {
            Index = index;
            Context = context;
            Buffer = buffer;
            LumaAccess = lumaAccess;
            ChromaAccess = chromaAccess;
        }

        internal int Index { get; }
        internal WgpuContext Context { get; }
        internal LinuxGbmNv12Buffer Buffer { get; }
        internal DawnExplicitSharedTextureAccess
            LumaAccess { get; }
        internal DawnExplicitSharedTextureAccess
            ChromaAccess { get; }
        internal DawnSyncFdEndAccessResult LumaFence { get; } =
            new();
        internal DawnSyncFdEndAccessResult ChromaFence { get; } =
            new();

        public void Dispose()
        {
            LumaFence.Dispose();
            ChromaFence.Dispose();
            LumaAccess.Dispose();
            ChromaAccess.Dispose();
            Buffer.Dispose();
        }
    }

    private sealed class EncoderSlotLease : IDisposable
    {
        private LinuxWebGpuNv12EncoderFrameSink? _owner;
        private TargetSlot? _slot;

        internal EncoderSlotLease(
            LinuxWebGpuNv12EncoderFrameSink owner,
            TargetSlot slot)
        {
            _owner = owner;
            _slot = slot;
        }

        public void Dispose()
        {
            LinuxWebGpuNv12EncoderFrameSink? owner =
                Interlocked.Exchange(
                    ref _owner,
                    null);
            TargetSlot? slot =
                Interlocked.Exchange(
                    ref _slot,
                    null);
            if (owner is not null &&
                slot is not null)
            {
                owner.ReturnFromEncoder(slot);
            }
        }

        internal void Abandon()
        {
            _ = Interlocked.Exchange(
                ref _owner,
                null);
            _ = Interlocked.Exchange(
                ref _slot,
                null);
        }
    }
}

internal sealed class LinuxGbmDevice : IDisposable
{
    private int _fileDescriptor;
    private nint _device;

    private LinuxGbmDevice(
        int fileDescriptor,
        nint device)
    {
        _fileDescriptor = fileDescriptor;
        _device = device;
    }

    internal static LinuxGbmDevice Open(string path)
    {
        int fileDescriptor = LinuxMediaNative.Open(
            path,
            V4l2Constants.OpenReadWrite |
            V4l2Constants.OpenCloseOnExec,
            0);
        if (fileDescriptor < 0)
        {
            throw new InvalidOperationException(
                $"Could not open DRM render node '{path}'.");
        }
        nint device =
            LinuxGbmNative.CreateDevice(
                fileDescriptor);
        if (device == 0)
        {
            LinuxMediaNative.Close(fileDescriptor);
            throw new NotSupportedException(
                $"GBM rejected DRM render node '{path}'.");
        }
        return new LinuxGbmDevice(
            fileDescriptor,
            device);
    }

    internal LinuxGbmNv12Buffer CreateNv12(
        uint width,
        uint height)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _device) == 0,
            this);
        LinuxGbmPlaneAllocation luma = default;
        LinuxGbmPlaneAllocation chroma = default;
        try
        {
            luma = CreatePlane(
                width,
                height,
                V4l2Constants.DrmR8);
            chroma = CreatePlane(
                (width + 1) / 2,
                (height + 1) / 2,
                V4l2Constants.DrmGr88);
            var result =
                new LinuxGbmNv12Buffer(
                    luma.BufferObject,
                    chroma.BufferObject,
                    width,
                    height,
                    luma.Modifier,
                    chroma.Modifier,
                    luma.Plane,
                    chroma.Plane);
            luma = default;
            chroma = default;
            return result;
        }
        finally
        {
            luma.Dispose();
            chroma.Dispose();
        }
    }

    private LinuxGbmPlaneAllocation CreatePlane(
        uint width,
        uint height,
        uint format)
    {
        nint bufferObject =
            LinuxGbmNative.CreateBufferObject(
                _device,
                width,
                height,
                format,
                LinuxGbmNative.UseRendering |
                LinuxGbmNative.UseLinear |
                LinuxGbmNative.UseTexturing);
        if (bufferObject == 0)
        {
            throw new NotSupportedException(
                $"GBM could not allocate linear renderable DRM format 0x{format:x8}.");
        }
        int fileDescriptor = -1;
        try
        {
            if (LinuxGbmNative.GetPlaneCount(
                    bufferObject) != 1)
            {
                throw new NotSupportedException(
                    "A GBM encoder plane did not expose one linear DMA-BUF plane.");
            }
            fileDescriptor =
                LinuxGbmNative.GetFileDescriptor(
                    bufferObject);
            uint stride =
                LinuxGbmNative.GetStrideForPlane(
                    bufferObject,
                    0);
            uint offset =
                LinuxGbmNative.GetOffset(
                    bufferObject,
                    0);
            if (fileDescriptor < 0 ||
                stride == 0)
            {
                throw new NotSupportedException(
                    "GBM did not export a valid encoder-plane DMA-BUF.");
            }
            var allocation =
                new LinuxGbmPlaneAllocation(
                    bufferObject,
                    LinuxGbmNative.GetModifier(
                        bufferObject),
                    new ProGpuDmaBufPlane(
                        fileDescriptor,
                        offset,
                        stride));
            bufferObject = 0;
            fileDescriptor = -1;
            return allocation;
        }
        finally
        {
            if (fileDescriptor >= 0)
            {
                LinuxMediaNative.Close(
                    fileDescriptor);
            }
            if (bufferObject != 0)
            {
                LinuxGbmNative.DestroyBufferObject(
                    bufferObject);
            }
        }
    }

    public void Dispose()
    {
        nint device =
            Interlocked.Exchange(ref _device, 0);
        int fileDescriptor =
            Interlocked.Exchange(
                ref _fileDescriptor,
                -1);
        if (device != 0)
        {
            LinuxGbmNative.DestroyDevice(device);
        }
        LinuxMediaNative.Close(fileDescriptor);
    }
}

internal sealed class LinuxGbmNv12Buffer : IDisposable
{
    private nint _lumaBufferObject;
    private nint _chromaBufferObject;

    internal LinuxGbmNv12Buffer(
        nint lumaBufferObject,
        nint chromaBufferObject,
        uint width,
        uint height,
        ulong lumaModifier,
        ulong chromaModifier,
        ProGpuDmaBufPlane luma,
        ProGpuDmaBufPlane chroma)
    {
        _lumaBufferObject = lumaBufferObject;
        _chromaBufferObject = chromaBufferObject;
        Width = width;
        Height = height;
        LumaModifier = lumaModifier;
        ChromaModifier = chromaModifier;
        Luma = luma;
        Chroma = chroma;
        EncoderDescriptor =
            new ProGpuDmaBufDescriptor(
                V4l2Constants.DrmNv12,
                0,
                2,
                luma,
                chroma);
    }

    internal uint Width { get; }
    internal uint Height { get; }
    internal ulong LumaModifier { get; }
    internal ulong ChromaModifier { get; }
    internal ProGpuDmaBufPlane Luma { get; }
    internal ProGpuDmaBufPlane Chroma { get; }
    internal ProGpuDmaBufDescriptor
        EncoderDescriptor { get; }

    internal ProGpuExternalTextureDescriptor
        CreateLumaDescriptor()
    {
        var dmaBuf =
            new ProGpuDmaBufDescriptor(
                V4l2Constants.DrmR8,
                LumaModifier,
                1,
                Luma);
        return new ProGpuExternalTextureDescriptor(
            ProGpuExternalTextureHandleKind.DmaBuf,
            Luma.FileDescriptor,
            Width,
            Height,
            TextureFormat.R8Unorm,
            TextureUsage.RenderAttachment,
            GpuTextureAlphaMode.Straight,
            IsInitialized: false)
        {
            DmaBuf = dmaBuf
        };
    }

    internal ProGpuExternalTextureDescriptor
        CreateChromaDescriptor()
    {
        var dmaBuf =
            new ProGpuDmaBufDescriptor(
                V4l2Constants.DrmGr88,
                ChromaModifier,
                1,
                Chroma);
        return new ProGpuExternalTextureDescriptor(
            ProGpuExternalTextureHandleKind.DmaBuf,
            Chroma.FileDescriptor,
            (Width + 1) / 2,
            (Height + 1) / 2,
            TextureFormat.RG8Unorm,
            TextureUsage.RenderAttachment,
            GpuTextureAlphaMode.Straight,
            IsInitialized: false)
        {
            DmaBuf = dmaBuf
        };
    }

    internal void ImportWriteFence(int syncFile)
    {
        LinuxDmaBufSynchronization.ImportWriteFence(
            Luma.FileDescriptor,
            syncFile);
        LinuxDmaBufSynchronization.ImportWriteFence(
            Chroma.FileDescriptor,
            syncFile);
    }

    public void Dispose()
    {
        nint lumaBufferObject =
            Interlocked.Exchange(
                ref _lumaBufferObject,
                0);
        nint chromaBufferObject =
            Interlocked.Exchange(
                ref _chromaBufferObject,
                0);
        if (lumaBufferObject == 0 &&
            chromaBufferObject == 0)
        {
            return;
        }
        LinuxMediaNative.Close(Luma.FileDescriptor);
        LinuxMediaNative.Close(Chroma.FileDescriptor);
        if (lumaBufferObject != 0)
        {
            LinuxGbmNative.DestroyBufferObject(
                lumaBufferObject);
        }
        if (chromaBufferObject != 0)
        {
            LinuxGbmNative.DestroyBufferObject(
                chromaBufferObject);
        }
    }
}

internal struct LinuxGbmPlaneAllocation :
    IDisposable
{
    internal LinuxGbmPlaneAllocation(
        nint bufferObject,
        ulong modifier,
        ProGpuDmaBufPlane plane)
    {
        BufferObject = bufferObject;
        Modifier = modifier;
        Plane = plane;
    }

    internal nint BufferObject;
    internal ulong Modifier;
    internal ProGpuDmaBufPlane Plane;

    public void Dispose()
    {
        int fileDescriptor = Plane.FileDescriptor;
        nint bufferObject = BufferObject;
        this = default;
        if (bufferObject == 0)
        {
            return;
        }
        if (fileDescriptor >= 0)
        {
            LinuxMediaNative.Close(fileDescriptor);
        }
        LinuxGbmNative.DestroyBufferObject(
            bufferObject);
    }
}

internal sealed class BorrowedGbmLifetime :
    IDisposable
{
    private LinuxGbmNv12Buffer? _buffer;

    internal BorrowedGbmLifetime(
        LinuxGbmNv12Buffer buffer)
    {
        _buffer = buffer;
    }

    public void Dispose()
    {
        _ = Interlocked.Exchange(
            ref _buffer,
            null);
    }
}

internal sealed class SharedOwnerRoot : IDisposable
{
    private IDisposable? _owner;
    private int _references = 1;

    internal SharedOwnerRoot(IDisposable owner)
    {
        _owner = owner;
    }

    internal SharedOwnerLease CreateLease()
    {
        if (Interlocked.Increment(
                ref _references) <= 1)
        {
            throw new ObjectDisposedException(
                nameof(SharedOwnerRoot));
        }
        return new SharedOwnerLease(this);
    }

    internal void Release()
    {
        if (Interlocked.Decrement(
                ref _references) == 0)
        {
            IDisposable? owner =
                Interlocked.Exchange(
                    ref _owner,
                    null);
            owner?.Dispose();
        }
    }

    public void Dispose() => Release();
}

internal sealed class SharedOwnerLease : IDisposable
{
    private SharedOwnerRoot? _root;

    internal SharedOwnerLease(
        SharedOwnerRoot root)
    {
        _root = root;
    }

    public void Dispose()
    {
        SharedOwnerRoot? root =
            Interlocked.Exchange(
                ref _root,
                null);
        root?.Release();
    }
}

internal static unsafe class LinuxDmaBufSynchronization
{
    private const nuint ImportSyncFile =
        0x4008_6203;
    private const uint SyncWrite = 2;

    internal static void ImportWriteFence(
        int dmaBuf,
        int syncFile)
    {
        var import =
            new DmaBufImportSyncFile
            {
                Flags = SyncWrite,
                FileDescriptor = syncFile
            };
        int result;
        do
        {
            result = LinuxMediaNative.Ioctl(
                dmaBuf,
                ImportSyncFile,
                &import);
        }
        while (result < 0 &&
               Marshal.GetLastPInvokeError() ==
                   V4l2Constants.ErrorInterrupted);
        if (result < 0)
        {
            throw new NotSupportedException(
                $"DMA_BUF_IOCTL_IMPORT_SYNC_FILE failed with errno {Marshal.GetLastPInvokeError()}.");
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct DmaBufImportSyncFile
{
    internal uint Flags;
    internal int FileDescriptor;
}

internal static partial class LinuxGbmNative
{
    internal const uint UseRendering = 1u << 2;
    internal const uint UseLinear = 1u << 4;
    internal const uint UseTexturing = 1u << 5;

    internal static bool IsAvailable()
    {
        if (!NativeLibrary.TryLoad(
                "libgbm.so.1",
                out nint library))
        {
            return false;
        }
        NativeLibrary.Free(library);
        return true;
    }

    [LibraryImport(
        "libgbm.so.1",
        EntryPoint = "gbm_create_device")]
    [UnmanagedCallConv(
        CallConvs =
            [typeof(CallConvCdecl)])]
    internal static partial nint CreateDevice(
        int fileDescriptor);

    [LibraryImport(
        "libgbm.so.1",
        EntryPoint = "gbm_device_destroy")]
    [UnmanagedCallConv(
        CallConvs =
            [typeof(CallConvCdecl)])]
    internal static partial void DestroyDevice(
        nint device);

    [LibraryImport(
        "libgbm.so.1",
        EntryPoint = "gbm_bo_create")]
    [UnmanagedCallConv(
        CallConvs =
            [typeof(CallConvCdecl)])]
    internal static partial nint CreateBufferObject(
        nint device,
        uint width,
        uint height,
        uint format,
        uint flags);

    [LibraryImport(
        "libgbm.so.1",
        EntryPoint = "gbm_bo_destroy")]
    [UnmanagedCallConv(
        CallConvs =
            [typeof(CallConvCdecl)])]
    internal static partial void DestroyBufferObject(
        nint bufferObject);

    [LibraryImport(
        "libgbm.so.1",
        EntryPoint = "gbm_bo_get_plane_count")]
    [UnmanagedCallConv(
        CallConvs =
            [typeof(CallConvCdecl)])]
    internal static partial uint GetPlaneCount(
        nint bufferObject);

    [LibraryImport(
        "libgbm.so.1",
        EntryPoint = "gbm_bo_get_fd")]
    [UnmanagedCallConv(
        CallConvs =
            [typeof(CallConvCdecl)])]
    internal static partial int GetFileDescriptor(
        nint bufferObject);

    [LibraryImport(
        "libgbm.so.1",
        EntryPoint =
            "gbm_bo_get_stride_for_plane")]
    [UnmanagedCallConv(
        CallConvs =
            [typeof(CallConvCdecl)])]
    internal static partial uint GetStrideForPlane(
        nint bufferObject,
        int plane);

    [LibraryImport(
        "libgbm.so.1",
        EntryPoint = "gbm_bo_get_offset")]
    [UnmanagedCallConv(
        CallConvs =
            [typeof(CallConvCdecl)])]
    internal static partial uint GetOffset(
        nint bufferObject,
        int plane);

    [LibraryImport(
        "libgbm.so.1",
        EntryPoint = "gbm_bo_get_modifier")]
    [UnmanagedCallConv(
        CallConvs =
            [typeof(CallConvCdecl)])]
    internal static partial ulong GetModifier(
        nint bufferObject);
}
