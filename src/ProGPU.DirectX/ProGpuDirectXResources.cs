using ProGPU.Backend;
using System.Runtime.InteropServices;

namespace ProGPU.DirectX;

public abstract class ProGpuDirectXResource : IDisposable
{
    private bool _isDisposed;

    protected ProGpuDirectXResource(ProGpuDirectXDevice device, string label)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Label = label;
    }

    public ProGpuDirectXDevice Device { get; }

    public string Label { get; }

    public bool IsDisposed => _isDisposed;

    protected void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(
                string.IsNullOrWhiteSpace(Label) ? nameof(ProGpuDirectXResource) : Label);
        }
    }

    protected virtual void DisposeCore()
    {
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        DisposeCore();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}

public sealed class ProGpuDirectXBuffer : ProGpuDirectXResource
{
    private readonly GpuBuffer? _backendBuffer;
    private readonly byte[]? _cpuShadow;
    private readonly byte[] _writeShadow;
    private ProGpuDirectXMappedSubresource? _activeMapping;

    internal ProGpuDirectXBuffer(ProGpuDirectXDevice device, DxBufferDescriptor descriptor)
        : base(device, descriptor.Label)
    {
        ValidateDescriptor(descriptor, device.IsGpuBacked);

        Descriptor = descriptor;
        if ((descriptor.CpuAccess & DxCpuAccessFlags.Read) != 0 ||
            (descriptor.CpuAccess & DxCpuAccessFlags.Write) != 0)
        {
            _cpuShadow = new byte[descriptor.SizeInBytes];
        }

        var shadowSize = checked((int)AlignToWebGpuBufferCopySize(descriptor.SizeInBytes));
        _writeShadow = _cpuShadow is { Length: var cpuShadowLength } && cpuShadowLength == shadowSize
            ? _cpuShadow
            : new byte[shadowSize];

        if (device.Context is { } context && device.IsGpuBacked)
        {
            _backendBuffer = new GpuBuffer(
                context,
                descriptor.SizeInBytes,
                ProGpuDirectXFormatConverter.ToBufferUsage(descriptor.Usage, descriptor.CpuAccess),
                descriptor.Label);
        }
    }

    public DxBufferDescriptor Descriptor { get; }

    public GpuBuffer? BackendBuffer => _backendBuffer;

    public uint LastWriteSizeInBytes { get; private set; }

    public uint LastWriteOffsetInBytes { get; private set; }

    public ulong Generation { get; private set; }

    public bool IsMapped => _activeMapping is not null;

    public unsafe void Write<T>(ReadOnlySpan<T> data, uint offsetBytes = 0) where T : unmanaged
    {
        ThrowIfDisposed();
        var dataSize = checked((uint)(data.Length * sizeof(T)));
        if (offsetBytes + dataSize > Descriptor.SizeInBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "Buffer write exceeds the DirectX buffer bounds.");
        }

        var bytes = MemoryMarshal.AsBytes(data);
        bytes.CopyTo(_writeShadow.AsSpan(checked((int)offsetBytes), checked((int)dataSize)));
        if (_cpuShadow is not null && !ReferenceEquals(_cpuShadow, _writeShadow))
        {
            bytes.CopyTo(_cpuShadow.AsSpan(checked((int)offsetBytes), checked((int)dataSize)));
        }

        UploadWriteShadowRange(offsetBytes, dataSize);
        LastWriteSizeInBytes = dataSize;
        LastWriteOffsetInBytes = offsetBytes;
        Generation++;
    }

    public ProGpuDirectXMappedSubresource Map(
        DxMapMode mode,
        DxMapFlags flags = DxMapFlags.None,
        uint offsetBytes = 0,
        uint? sizeInBytes = null)
    {
        ThrowIfDisposed();
        ValidateMapMode(mode);
        if (_activeMapping is not null)
        {
            throw new InvalidOperationException("DirectX buffer is already mapped.");
        }

        var mapSize = sizeInBytes ?? (Descriptor.SizeInBytes - offsetBytes);
        ValidateReadRange(offsetBytes, mapSize);
        if (mapSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeInBytes), "Mapped DirectX buffer ranges must be non-empty.");
        }

        var requiresRead = RequiresCpuRead(mode);
        var requiresWrite = RequiresCpuWrite(mode);
        if (requiresRead && (Descriptor.CpuAccess & DxCpuAccessFlags.Read) == 0)
        {
            throw new InvalidOperationException("DirectX buffer was not created with CPU read access.");
        }

        if (requiresWrite && (Descriptor.CpuAccess & DxCpuAccessFlags.Write) == 0)
        {
            throw new InvalidOperationException("DirectX buffer was not created with CPU write access.");
        }

        if (requiresRead)
        {
            SynchronizeShadowForRead(offsetBytes, mapSize);
        }
        else if (mode == DxMapMode.WriteDiscard)
        {
            _writeShadow.AsSpan(checked((int)offsetBytes), checked((int)mapSize)).Clear();
        }

        _activeMapping = new ProGpuDirectXMappedSubresource(
            this,
            mode,
            flags,
            offsetBytes,
            mapSize,
            _writeShadow,
            uploadOnUnmap: requiresWrite);

        return _activeMapping;
    }

    public void Unmap(ProGpuDirectXMappedSubresource mapping)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(mapping);
        if (!ReferenceEquals(mapping.Buffer, this))
        {
            throw new ArgumentException("Mapped DirectX subresource belongs to a different buffer.", nameof(mapping));
        }

        mapping.Unmap();
    }

    public byte[] ReadBytes(uint offsetBytes = 0, uint? sizeInBytes = null)
    {
        ThrowIfDisposed();
        if ((Descriptor.CpuAccess & DxCpuAccessFlags.Read) == 0)
        {
            throw new InvalidOperationException("Buffer was not created with CPU read access.");
        }

        var readSize = sizeInBytes ?? (Descriptor.SizeInBytes - offsetBytes);
        ValidateReadRange(offsetBytes, readSize);

        if (readSize == 0)
        {
            return [];
        }

        var bytes = new byte[checked((int)readSize)];
        ReadBytes(bytes, offsetBytes);
        return bytes;
    }

    public void ReadBytes(Span<byte> destination, uint offsetBytes = 0)
    {
        ThrowIfDisposed();
        if ((Descriptor.CpuAccess & DxCpuAccessFlags.Read) == 0)
        {
            throw new InvalidOperationException("Buffer was not created with CPU read access.");
        }

        var readSize = checked((uint)destination.Length);
        ValidateReadRange(offsetBytes, readSize);
        if (readSize == 0)
        {
            return;
        }

        if (_backendBuffer is { BufferPtr: not null })
        {
            _backendBuffer.ReadBytes(destination, offsetBytes);
            return;
        }

        if (_cpuShadow is null)
        {
            throw new InvalidOperationException("Buffer does not have readable CPU storage.");
        }

        _cpuShadow.AsSpan(checked((int)offsetBytes), checked((int)readSize)).CopyTo(destination);
    }

    public unsafe T[] Read<T>(uint elementCount, uint offsetBytes = 0) where T : unmanaged
    {
        var values = new T[checked((int)elementCount)];
        Read(values, offsetBytes);
        return values;
    }

    public unsafe void Read<T>(Span<T> destination, uint offsetBytes = 0) where T : unmanaged
    {
        ReadBytes(MemoryMarshal.AsBytes(destination), offsetBytes);
    }

    internal void CopyCpuShadowFrom(ProGpuDirectXBuffer source)
    {
        var copySize = checked((int)Math.Min(Descriptor.SizeInBytes, source.Descriptor.SizeInBytes));
        var shadowCopySize = Math.Min(_writeShadow.Length, source._writeShadow.Length);
        source._writeShadow.AsSpan(0, shadowCopySize).CopyTo(_writeShadow);
        if (_cpuShadow is not null && !ReferenceEquals(_cpuShadow, _writeShadow))
        {
            _writeShadow.AsSpan(0, copySize).CopyTo(_cpuShadow);
        }

        LastWriteSizeInBytes = Math.Min(source.LastWriteSizeInBytes, Descriptor.SizeInBytes);
        LastWriteOffsetInBytes = 0;
        Generation++;
    }

    internal byte[] ReadWriteShadowBytes(uint offsetBytes, uint sizeInBytes)
    {
        ValidateReadRange(offsetBytes, sizeInBytes);
        if (sizeInBytes == 0)
        {
            return [];
        }

        var bytes = new byte[checked((int)sizeInBytes)];
        ReadWriteShadowBytes(bytes, offsetBytes);
        return bytes;
    }

    internal void ReadWriteShadowBytes(Span<byte> destination, uint offsetBytes)
    {
        var sizeInBytes = checked((uint)destination.Length);
        ValidateReadRange(offsetBytes, sizeInBytes);
        if (sizeInBytes == 0)
        {
            return;
        }

        _writeShadow.AsSpan(checked((int)offsetBytes), checked((int)sizeInBytes)).CopyTo(destination);
    }

    internal void CompleteMappedSubresource(ProGpuDirectXMappedSubresource mapping)
    {
        if (!ReferenceEquals(_activeMapping, mapping))
        {
            throw new InvalidOperationException("DirectX buffer mapping is not active.");
        }

        if (mapping.UploadOnUnmap)
        {
            var mappedBytes = _writeShadow.AsSpan(
                checked((int)mapping.OffsetBytes),
                checked((int)mapping.SizeInBytes));

            UploadWriteShadowRange(mapping.OffsetBytes, mapping.SizeInBytes);
            if (_cpuShadow is not null && !ReferenceEquals(_cpuShadow, _writeShadow))
            {
                mappedBytes.CopyTo(_cpuShadow.AsSpan(
                    checked((int)mapping.OffsetBytes),
                    checked((int)mapping.SizeInBytes)));
            }

            LastWriteOffsetInBytes = mapping.OffsetBytes;
            LastWriteSizeInBytes = mapping.SizeInBytes;
            Generation++;
        }

        _activeMapping = null;
    }

    private void SynchronizeShadowForRead(uint offsetBytes, uint sizeInBytes)
    {
        if (_backendBuffer is not { BufferPtr: not null })
        {
            return;
        }

        var writeShadowSpan = _writeShadow.AsSpan(checked((int)offsetBytes), checked((int)sizeInBytes));
        _backendBuffer.ReadBytes(writeShadowSpan, offsetBytes);
        if (_cpuShadow is not null && !ReferenceEquals(_cpuShadow, _writeShadow))
        {
            writeShadowSpan.CopyTo(_cpuShadow.AsSpan(checked((int)offsetBytes), checked((int)sizeInBytes)));
        }
    }

    private void UploadWriteShadowRange(uint offsetBytes, uint sizeInBytes)
    {
        if (_backendBuffer is not { BufferPtr: not null })
        {
            return;
        }

        var alignedOffset = AlignDown(offsetBytes, 4);
        var leadingBytes = offsetBytes - alignedOffset;
        var alignedSize = AlignToWebGpuBufferCopySize(leadingBytes + sizeInBytes);
        _backendBuffer.WriteAlignedBytes(
            _writeShadow.AsSpan(checked((int)alignedOffset), checked((int)alignedSize)),
            alignedOffset);
    }

    private static uint AlignDown(uint value, uint alignment)
    {
        return value - (value % alignment);
    }

    private static uint AlignToWebGpuBufferCopySize(uint size)
    {
        return (size + 3) & ~3u;
    }

    private static void ValidateDescriptor(DxBufferDescriptor descriptor, bool isGpuBacked)
    {
        if (descriptor.SizeInBytes == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "DirectX buffers must have a non-zero size.");
        }

        if ((descriptor.CpuAccess & DxCpuAccessFlags.Read) != 0 &&
            (descriptor.Usage & ~(DxBufferUsage.CopySource | DxBufferUsage.CopyDestination)) != 0)
        {
            throw new ArgumentException("CPU-readable DirectX buffers must be staging/copy resources without bind flags.", nameof(descriptor));
        }

        if (isGpuBacked &&
            (descriptor.CpuAccess & DxCpuAccessFlags.Read) != 0 &&
            (descriptor.Usage & DxBufferUsage.CopySource) != 0)
        {
            throw new ArgumentException("GPU-backed CPU-readable DirectX buffers cannot also be copy-source buffers because WebGPU map-read buffers cannot include copy-source usage.", nameof(descriptor));
        }
    }

    private static void ValidateMapMode(DxMapMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Unknown DirectX map mode.");
        }
    }

    private static bool RequiresCpuRead(DxMapMode mode)
    {
        return mode is DxMapMode.Read or DxMapMode.ReadWrite;
    }

    private static bool RequiresCpuWrite(DxMapMode mode)
    {
        return mode is DxMapMode.Write or DxMapMode.ReadWrite or DxMapMode.WriteDiscard or DxMapMode.WriteNoOverwrite;
    }

    private void ValidateReadRange(uint offsetBytes, uint sizeInBytes)
    {
        if (offsetBytes > Descriptor.SizeInBytes || sizeInBytes > Descriptor.SizeInBytes - offsetBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeInBytes), "Buffer read exceeds the DirectX buffer bounds.");
        }
    }

    protected override void DisposeCore()
    {
        _activeMapping?.Dispose();
        _activeMapping = null;
        _backendBuffer?.Dispose();
    }
}

public sealed class ProGpuDirectXMappedSubresource : IDisposable
{
    private ProGpuDirectXResource? _resource;
    private readonly Action<ProGpuDirectXMappedSubresource> _completeMapping;
    private readonly byte[] _data;

    internal ProGpuDirectXMappedSubresource(
        ProGpuDirectXBuffer buffer,
        DxMapMode mode,
        DxMapFlags flags,
        uint offsetBytes,
        uint sizeInBytes,
        byte[] data,
        bool uploadOnUnmap)
        : this(buffer, buffer.CompleteMappedSubresource, mode, flags, offsetBytes, sizeInBytes, sizeInBytes, sizeInBytes, data, uploadOnUnmap)
    {
    }

    internal ProGpuDirectXMappedSubresource(
        ProGpuDirectXTexture2D texture,
        DxMapMode mode,
        DxMapFlags flags,
        uint subresource,
        uint offsetBytes,
        uint sizeInBytes,
        uint rowPitch,
        uint depthPitch,
        byte[] data,
        bool uploadOnUnmap)
        : this(texture, texture.CompleteMappedSubresource, mode, flags, subresource, offsetBytes, sizeInBytes, rowPitch, depthPitch, data, uploadOnUnmap)
    {
    }

    internal ProGpuDirectXMappedSubresource(
        ProGpuDirectXTexture3D texture,
        DxMapMode mode,
        DxMapFlags flags,
        uint subresource,
        uint offsetBytes,
        uint sizeInBytes,
        uint rowPitch,
        uint depthPitch,
        byte[] data,
        bool uploadOnUnmap)
        : this(texture, texture.CompleteMappedSubresource, mode, flags, subresource, offsetBytes, sizeInBytes, rowPitch, depthPitch, data, uploadOnUnmap)
    {
    }

    private ProGpuDirectXMappedSubresource(
        ProGpuDirectXResource resource,
        Action<ProGpuDirectXMappedSubresource> completeMapping,
        DxMapMode mode,
        DxMapFlags flags,
        uint offsetBytes,
        uint sizeInBytes,
        uint rowPitch,
        uint depthPitch,
        byte[] data,
        bool uploadOnUnmap)
        : this(resource, completeMapping, mode, flags, subresource: 0, offsetBytes, sizeInBytes, rowPitch, depthPitch, data, uploadOnUnmap)
    {
    }

    private ProGpuDirectXMappedSubresource(
        ProGpuDirectXResource resource,
        Action<ProGpuDirectXMappedSubresource> completeMapping,
        DxMapMode mode,
        DxMapFlags flags,
        uint subresource,
        uint offsetBytes,
        uint sizeInBytes,
        uint rowPitch,
        uint depthPitch,
        byte[] data,
        bool uploadOnUnmap)
    {
        _resource = resource;
        _completeMapping = completeMapping;
        Mode = mode;
        Flags = flags;
        Subresource = subresource;
        OffsetBytes = offsetBytes;
        SizeInBytes = sizeInBytes;
        RowPitch = rowPitch;
        DepthPitch = depthPitch;
        _data = data;
        UploadOnUnmap = uploadOnUnmap;
    }

    public ProGpuDirectXResource? Resource => _resource;

    public ProGpuDirectXBuffer? Buffer => _resource as ProGpuDirectXBuffer;

    public ProGpuDirectXTexture2D? Texture => _resource as ProGpuDirectXTexture2D;

    public ProGpuDirectXTexture3D? Texture3D => _resource as ProGpuDirectXTexture3D;

    public DxMapMode Mode { get; }

    public DxMapFlags Flags { get; }

    public uint Subresource { get; }

    public uint OffsetBytes { get; }

    public uint SizeInBytes { get; }

    public uint RowPitch { get; }

    public uint DepthPitch { get; }

    public bool IsMapped => _resource is not null;

    public Memory<byte> Data
    {
        get
        {
            ThrowIfUnmapped();
            return _data.AsMemory(checked((int)OffsetBytes), checked((int)SizeInBytes));
        }
    }

    public Span<byte> Span
    {
        get
        {
            ThrowIfUnmapped();
            return _data.AsSpan(checked((int)OffsetBytes), checked((int)SizeInBytes));
        }
    }

    internal bool UploadOnUnmap { get; }

    public unsafe void Write<T>(ReadOnlySpan<T> data, uint offsetBytes = 0) where T : unmanaged
    {
        ThrowIfUnmapped();
        var dataSize = checked((uint)(data.Length * sizeof(T)));
        ValidateMappedRange(offsetBytes, dataSize);
        MemoryMarshal.AsBytes(data).CopyTo(Span.Slice(checked((int)offsetBytes), checked((int)dataSize)));
    }

    public unsafe T[] Read<T>(uint elementCount, uint offsetBytes = 0) where T : unmanaged
    {
        ThrowIfUnmapped();
        var dataSize = checked((uint)(elementCount * sizeof(T)));
        ValidateMappedRange(offsetBytes, dataSize);
        var values = new T[checked((int)elementCount)];
        Read(values, offsetBytes);
        return values;
    }

    public unsafe void Read<T>(Span<T> destination, uint offsetBytes = 0) where T : unmanaged
    {
        ThrowIfUnmapped();
        var dataSize = checked((uint)(destination.Length * sizeof(T)));
        ValidateMappedRange(offsetBytes, dataSize);
        Span.Slice(checked((int)offsetBytes), checked((int)dataSize)).CopyTo(MemoryMarshal.AsBytes(destination));
    }

    public void Unmap()
    {
        if (_resource is null)
        {
            return;
        }

        _completeMapping(this);
        _resource = null;
    }

    public void Dispose()
    {
        Unmap();
    }

    private void ValidateMappedRange(uint offsetBytes, uint sizeInBytes)
    {
        if (offsetBytes > SizeInBytes || sizeInBytes > SizeInBytes - offsetBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeInBytes), "Mapped DirectX buffer access exceeds the mapped range.");
        }
    }

    private void ThrowIfUnmapped()
    {
        if (_resource is null)
        {
            throw new ObjectDisposedException(nameof(ProGpuDirectXMappedSubresource));
        }
    }
}

public sealed class ProGpuDirectXTexture2D : ProGpuDirectXResource
{
    private GpuTexture? _backendTexture;
    private GpuTexture[]? _backendArraySliceTextures;
    private byte[]? _cpuShadow;
    private byte[] _writeShadow = [];
    private bool[] _writeShadowSubresourcesCurrent = [];
    private ProGpuDirectXMappedSubresource? _activeMapping;

    internal ProGpuDirectXTexture2D(ProGpuDirectXDevice device, DxTexture2DDescriptor descriptor)
        : base(device, descriptor.Label)
    {
        ValidateDescriptor(descriptor);
        Descriptor = descriptor;
        AllocateCpuStorage(descriptor);
        AllocateBackendTexture();
    }

    public DxTexture2DDescriptor Descriptor { get; private set; }

    public GpuTexture? BackendTexture => _backendTexture;

    internal bool UsesBackendArraySliceTextures => _backendArraySliceTextures is not null;

    public uint Width => Descriptor.Width;

    public uint Height => Descriptor.Height;

    public uint LastWriteSizeInBytes { get; private set; }

    public uint Generation { get; private set; }

    public bool IsMapped => _activeMapping is not null;

    internal void MarkBackendContentsChanged()
    {
        if (_writeShadowSubresourcesCurrent.Length > 0)
        {
            Array.Fill(_writeShadowSubresourcesCurrent, false);
        }

        Generation++;
    }

    internal GpuTexture? GetBackendTexture(uint arraySlice)
    {
        if (_backendArraySliceTextures is { } sliceTextures)
        {
            return arraySlice < sliceTextures.Length
                ? sliceTextures[checked((int)arraySlice)]
                : null;
        }

        return _backendTexture;
    }

    internal GpuTexture? GetBackendTextureForSubresource(uint subresource)
    {
        var subresourceInfo = GetSubresourceInfo(Descriptor, subresource);
        return GetBackendTexture(subresourceInfo.ArraySlice);
    }

    internal uint GetNativeArrayLayer(uint arraySlice)
    {
        return _backendArraySliceTextures is not null ? 0 : arraySlice;
    }

    internal uint GetNativeArrayLayerForSubresource(uint subresource)
    {
        var subresourceInfo = GetSubresourceInfo(Descriptor, subresource);
        return GetNativeArrayLayer(subresourceInfo.ArraySlice);
    }

    public unsafe void WritePixels<T>(ReadOnlySpan<T> pixels) where T : unmanaged
    {
        ThrowIfDisposed();
        var expectedSize = GetTextureSizeInBytes(Descriptor);
        var bytes = MemoryMarshal.AsBytes(pixels);
        if (bytes.Length < expectedSize)
        {
            throw new ArgumentException($"Pixel span is too small ({bytes.Length} bytes, expected {expectedSize} bytes).", nameof(pixels));
        }

        if (_backendTexture is not null || _backendArraySliceTextures is not null)
        {
            UploadAllSubresourcesToBackend(bytes);
        }

        if (_writeShadow.Length > 0)
        {
            bytes.Slice(0, checked((int)expectedSize)).CopyTo(_writeShadow);
            if (_cpuShadow is not null && !ReferenceEquals(_cpuShadow, _writeShadow))
            {
                _writeShadow.CopyTo(_cpuShadow, 0);
            }

            Array.Fill(_writeShadowSubresourcesCurrent, true);
        }

        LastWriteSizeInBytes = expectedSize;
        Generation++;
    }

    public byte[] ReadPixels()
    {
        var pixels = new byte[checked((int)GetTextureSizeInBytes(Descriptor))];
        ReadPixels(pixels);
        return pixels;
    }

    public void ReadPixels(Span<byte> destination)
    {
        ThrowIfDisposed();
        if ((Descriptor.CpuAccess & DxCpuAccessFlags.Read) == 0)
        {
            throw new InvalidOperationException("Texture was not created with CPU read access.");
        }

        var expectedSize = GetTextureSizeInBytes(Descriptor);
        if (destination.Length < expectedSize)
        {
            throw new ArgumentException(
                $"Destination span is too small ({destination.Length} bytes, expected {expectedSize} bytes).",
                nameof(destination));
        }

        if (_backendTexture is { IsDisposed: false } ||
            _backendArraySliceTextures is not null)
        {
            SynchronizeAllShadowForRead(_backendTexture);
            _writeShadow.AsSpan(0, checked((int)expectedSize)).CopyTo(destination);
            return;
        }

        if (_cpuShadow is null)
        {
            throw new InvalidOperationException("Texture readback requires a GPU-backed texture.");
        }

        _cpuShadow.AsSpan(0, checked((int)expectedSize)).CopyTo(destination);
    }

    internal void GenerateMips(DxShaderResourceViewDescriptor shaderResourceView)
    {
        ThrowIfDisposed();
        ValidateGenerateMips(shaderResourceView);
        if (shaderResourceView.MipLevels <= 1)
        {
            return;
        }

        if (_activeMapping is not null)
        {
            throw new InvalidOperationException("DirectX mip generation cannot run while the texture is mapped.");
        }

        var firstMip = shaderResourceView.MostDetailedMip;
        if (_backendTexture is not null)
        {
            _backendTexture.GenerateMipmaps2DLinear(
                firstMip,
                shaderResourceView.MipLevels,
                shaderResourceView.FirstArraySlice,
                shaderResourceView.ArraySize);
            LastWriteSizeInBytes = 0;
            MarkBackendContentsChanged();
            return;
        }

        if (_writeShadow.Length == 0)
        {
            throw new InvalidOperationException("DirectX mip generation requires texture shadow storage or a synchronized native source mip.");
        }

        var lastMipExclusive = checked(firstMip + shaderResourceView.MipLevels);
        var generatedBytes = 0u;

        for (var arraySlice = shaderResourceView.FirstArraySlice;
             arraySlice < checked(shaderResourceView.FirstArraySlice + shaderResourceView.ArraySize);
             arraySlice++)
        {
            EnsureShadowSubresourceCurrent(GetSubresourceIndex(firstMip, arraySlice));

            for (var mipLevel = firstMip + 1; mipLevel < lastMipExclusive; mipLevel++)
            {
                var sourceInfo = GetSubresourceInfo(Descriptor, GetSubresourceIndex(mipLevel - 1, arraySlice));
                var destinationInfo = GetSubresourceInfo(Descriptor, GetSubresourceIndex(mipLevel, arraySlice));
                var source = _writeShadow.AsSpan(
                    checked((int)sourceInfo.OffsetBytes),
                    checked((int)sourceInfo.SizeInBytes));
                var destination = _writeShadow.AsSpan(
                    checked((int)destinationInfo.OffsetBytes),
                    checked((int)destinationInfo.SizeInBytes));

                DownsampleColorMip(source, destination, sourceInfo, destinationInfo);
                var destinationSubresource = GetSubresourceIndex(mipLevel, arraySlice);
                MarkShadowSubresourceCurrent(destinationSubresource);
                generatedBytes = checked(generatedBytes + destinationInfo.SizeInBytes);

                if (_cpuShadow is not null && !ReferenceEquals(_cpuShadow, _writeShadow))
                {
                    destination.CopyTo(_cpuShadow.AsSpan(
                        checked((int)destinationInfo.OffsetBytes),
                        checked((int)destinationInfo.SizeInBytes)));
                }
            }
        }

        LastWriteSizeInBytes = generatedBytes;
        Generation++;
    }

    private void UploadAllSubresourcesToBackend(ReadOnlySpan<byte> bytes)
    {
        if (_backendTexture is null && _backendArraySliceTextures is null)
        {
            return;
        }

        var subresourceCount = checked(Descriptor.ArraySize * Descriptor.MipLevels);
        for (uint subresource = 0; subresource < subresourceCount; subresource++)
        {
            var subresourceInfo = GetSubresourceInfo(Descriptor, subresource);
            var backendTexture = GetBackendTexture(subresourceInfo.ArraySlice);
            if (backendTexture is null)
            {
                continue;
            }

            backendTexture.WritePixelsSubRect(
                bytes.Slice(checked((int)subresourceInfo.OffsetBytes), checked((int)subresourceInfo.SizeInBytes)),
                x: 0,
                y: 0,
                subWidth: subresourceInfo.Width,
                subHeight: subresourceInfo.Height,
                arrayLayer: GetNativeArrayLayer(subresourceInfo.ArraySlice),
                mipLevel: subresourceInfo.MipLevel);
        }
    }

    private void SynchronizeAllShadowForRead(GpuTexture? fallbackTexture)
    {
        var subresourceCount = checked(Descriptor.ArraySize * Descriptor.MipLevels);
        for (uint subresource = 0; subresource < subresourceCount; subresource++)
        {
            var subresourceInfo = GetSubresourceInfo(Descriptor, subresource);
            var backendTexture = GetBackendTexture(subresourceInfo.ArraySlice) ?? fallbackTexture;
            if (backendTexture is not { IsDisposed: false })
            {
                continue;
            }

            ReadBackendSubresourceIntoWriteShadow(backendTexture, subresourceInfo);
            MarkShadowSubresourceCurrent(subresource);
        }

        if (_cpuShadow is not null && !ReferenceEquals(_cpuShadow, _writeShadow))
        {
            _writeShadow.CopyTo(_cpuShadow, 0);
        }
    }

    private void SynchronizeShadowForRead(uint subresource)
    {
        var subresourceInfo = GetSubresourceInfo(Descriptor, subresource);
        var texture = GetBackendTexture(subresourceInfo.ArraySlice);
        if (texture is not { IsDisposed: false })
        {
            return;
        }

        ReadBackendSubresourceIntoWriteShadow(texture, subresourceInfo);
        MarkShadowSubresourceCurrent(subresource);
        if (_cpuShadow is not null && !ReferenceEquals(_cpuShadow, _writeShadow))
        {
            _writeShadow.AsSpan(
                    checked((int)subresourceInfo.OffsetBytes),
                    checked((int)subresourceInfo.SizeInBytes))
                .CopyTo(_cpuShadow.AsSpan(
                    checked((int)subresourceInfo.OffsetBytes),
                    checked((int)subresourceInfo.SizeInBytes)));
        }
    }

    private void ReadBackendSubresourceIntoWriteShadow(GpuTexture texture, SubresourceInfo subresourceInfo)
    {
        texture.ReadPixels(
            _writeShadow.AsSpan(
                checked((int)subresourceInfo.OffsetBytes),
                checked((int)subresourceInfo.SizeInBytes)),
            subresourceInfo.MipLevel,
            GetNativeArrayLayer(subresourceInfo.ArraySlice),
            depthOrArrayLayers: 1);
    }

    public ProGpuDirectXMappedSubresource Map(
        DxMapMode mode,
        DxMapFlags flags = DxMapFlags.None,
        uint subresource = 0)
    {
        ThrowIfDisposed();
        ValidateMapMode(mode);
        ValidateMappableSubresource(subresource);
        if (_activeMapping is not null)
        {
            throw new InvalidOperationException("DirectX texture is already mapped.");
        }

        var requiresRead = RequiresCpuRead(mode);
        var requiresWrite = RequiresCpuWrite(mode);
        if (requiresRead && (Descriptor.CpuAccess & DxCpuAccessFlags.Read) == 0)
        {
            throw new InvalidOperationException("DirectX texture was not created with CPU read access.");
        }

        if (requiresWrite && (Descriptor.CpuAccess & DxCpuAccessFlags.Write) == 0)
        {
            throw new InvalidOperationException("DirectX texture was not created with CPU write access.");
        }

        if (_backendTexture is not null &&
            IsDepthStencilFormat(Descriptor.Format) &&
            (Descriptor.Format != DxResourceFormat.D32Float || requiresWrite))
        {
            throw new NotSupportedException("GPU-backed DirectX depth-stencil texture mapping currently supports D32Float read staging only.");
        }

        var subresourceInfo = GetSubresourceInfo(Descriptor, subresource);
        if (requiresRead)
        {
            SynchronizeShadowForRead(subresource);
        }
        else if (mode == DxMapMode.WriteDiscard)
        {
            _writeShadow.AsSpan(checked((int)subresourceInfo.OffsetBytes), checked((int)subresourceInfo.SizeInBytes)).Clear();
        }

        _activeMapping = new ProGpuDirectXMappedSubresource(
            this,
            mode,
            flags,
            subresource,
            offsetBytes: subresourceInfo.OffsetBytes,
            sizeInBytes: subresourceInfo.SizeInBytes,
            subresourceInfo.RowPitch,
            subresourceInfo.SizeInBytes,
            _writeShadow,
            uploadOnUnmap: requiresWrite);

        return _activeMapping;
    }

    public void Unmap(ProGpuDirectXMappedSubresource mapping)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(mapping);
        if (!ReferenceEquals(mapping.Texture, this))
        {
            throw new ArgumentException("Mapped DirectX subresource belongs to a different texture.", nameof(mapping));
        }

        mapping.Unmap();
    }

    public void Resize(uint width, uint height)
    {
        ThrowIfDisposed();
        if (width == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (Descriptor.Width == width && Descriptor.Height == height)
        {
            return;
        }

        Descriptor = Descriptor with { Width = width, Height = height };
        AllocateCpuStorage(Descriptor);
        if (_backendTexture != null)
        {
            _backendTexture.Resize(width, height);
        }
        else if (_backendArraySliceTextures is not null)
        {
            for (var sliceIndex = 0; sliceIndex < _backendArraySliceTextures.Length; sliceIndex++)
            {
                _backendArraySliceTextures[sliceIndex].Resize(width, height);
            }
        }

        Generation++;
    }

    internal void CompleteMappedSubresource(ProGpuDirectXMappedSubresource mapping)
    {
        if (!ReferenceEquals(_activeMapping, mapping))
        {
            throw new InvalidOperationException("DirectX texture mapping is not active.");
        }

        if (mapping.UploadOnUnmap)
        {
            var mappedBytes = _writeShadow.AsSpan(
                checked((int)mapping.OffsetBytes),
                checked((int)mapping.SizeInBytes));
            if (_backendTexture is not null)
            {
                var subresourceInfo = GetSubresourceInfo(Descriptor, mapping.Subresource);

                _backendTexture.WritePixelsSubRect(
                    mappedBytes,
                    x: 0,
                    y: 0,
                    subWidth: subresourceInfo.Width,
                    subHeight: subresourceInfo.Height,
                    arrayLayer: GetNativeArrayLayer(subresourceInfo.ArraySlice),
                    mipLevel: subresourceInfo.MipLevel);
            }

            if (_cpuShadow is not null && !ReferenceEquals(_cpuShadow, _writeShadow))
            {
                mappedBytes.CopyTo(_cpuShadow.AsSpan(
                    checked((int)mapping.OffsetBytes),
                    checked((int)mapping.SizeInBytes)));
            }

            LastWriteSizeInBytes = mapping.SizeInBytes;
            MarkShadowSubresourceCurrent(mapping.Subresource);
            Generation++;
        }

        _activeMapping = null;
    }

    private void AllocateCpuStorage(DxTexture2DDescriptor descriptor)
    {
        var subresourceCount = checked(descriptor.ArraySize * descriptor.MipLevels);
        _writeShadowSubresourcesCurrent = new bool[checked((int)subresourceCount)];

        if ((descriptor.CpuAccess & (DxCpuAccessFlags.Read | DxCpuAccessFlags.Write)) == 0)
        {
            var needsMipGenerationShadow = descriptor.MipLevels > 1 &&
                (descriptor.Usage & DxTextureUsage.ShaderResource) != 0 &&
                (descriptor.Usage & DxTextureUsage.RenderTarget) != 0;
            _cpuShadow = null;
            _writeShadow = needsMipGenerationShadow
                ? new byte[checked((int)GetTextureSizeInBytes(descriptor))]
                : [];
            LastWriteSizeInBytes = 0;
            return;
        }

        var byteSize = GetTextureSizeInBytes(descriptor);
        _cpuShadow = new byte[byteSize];
        _writeShadow = _cpuShadow ?? new byte[byteSize];
        LastWriteSizeInBytes = 0;
    }

    private void AllocateBackendTexture()
    {
        if (Device.Context is not { } context || !Device.IsGpuBacked)
        {
            return;
        }

        if (NeedsBackendArraySliceTextures(Descriptor))
        {
            _backendArraySliceTextures = new GpuTexture[checked((int)Descriptor.ArraySize)];
            for (uint arraySlice = 0; arraySlice < Descriptor.ArraySize; arraySlice++)
            {
                _backendArraySliceTextures[checked((int)arraySlice)] = new GpuTexture(
                    context,
                    Descriptor.Width,
                    Descriptor.Height,
                    ProGpuDirectXFormatConverter.ToTextureFormat(Descriptor.Format),
                    ProGpuDirectXFormatConverter.ToTextureUsage(Descriptor.Usage, Descriptor.CpuAccess),
                    $"{Descriptor.Label}[{arraySlice}]",
                    Descriptor.SampleCount,
                    ProGpuDirectXFormatConverter.ToTextureAlphaMode(Descriptor.Format),
                    depthOrArrayLayers: 1,
                    mipLevelCount: Descriptor.MipLevels);
            }

            return;
        }

        _backendTexture = new GpuTexture(
            context,
            Descriptor.Width,
            Descriptor.Height,
            ProGpuDirectXFormatConverter.ToTextureFormat(Descriptor.Format),
            ProGpuDirectXFormatConverter.ToTextureUsage(Descriptor.Usage, Descriptor.CpuAccess),
            Descriptor.Label,
            Descriptor.SampleCount,
            ProGpuDirectXFormatConverter.ToTextureAlphaMode(Descriptor.Format),
            depthOrArrayLayers: Descriptor.ArraySize,
            mipLevelCount: Descriptor.MipLevels);
    }

    private void EnsureShadowSubresourceCurrent(uint subresource)
    {
        if (IsShadowSubresourceCurrent(subresource))
        {
            return;
        }

        if (_backendTexture is { IsDisposed: false } &&
            HasEffectiveCopySourceAccess())
        {
            SynchronizeShadowForRead(subresource);
            return;
        }

        throw new InvalidOperationException("DirectX mip generation requires current source mip data. Create the texture with copy-source usage or upload/map the source mip before GenerateMips.");
    }

    private bool IsShadowSubresourceCurrent(uint subresource)
    {
        return subresource < _writeShadowSubresourcesCurrent.Length &&
            _writeShadowSubresourcesCurrent[checked((int)subresource)];
    }

    private bool HasEffectiveCopySourceAccess()
    {
        return (Descriptor.Usage & DxTextureUsage.CopySource) != 0 ||
            (Descriptor.CpuAccess & DxCpuAccessFlags.Read) != 0;
    }

    private void MarkShadowSubresourceCurrent(uint subresource)
    {
        if (subresource < _writeShadowSubresourcesCurrent.Length)
        {
            _writeShadowSubresourcesCurrent[checked((int)subresource)] = true;
        }
    }

    private void ValidateMappableSubresource(uint subresource)
    {
        if (subresource >= checked(Descriptor.ArraySize * Descriptor.MipLevels))
        {
            throw new ArgumentOutOfRangeException(nameof(subresource), "DirectX texture mapping subresource is outside the texture array/mip range.");
        }

        if (Descriptor.SampleCount != 1)
        {
            throw new NotSupportedException("DirectX texture mapping currently supports only single-sample textures.");
        }

        _ = GetBytesPerPixel(Descriptor.Format);
    }

    private static void ValidateDescriptor(DxTexture2DDescriptor descriptor)
    {
        if (descriptor.Width == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "DirectX textures must have a non-zero width.");
        }

        if (descriptor.Height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "DirectX textures must have a non-zero height.");
        }

        if (descriptor.MipLevels == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "DirectX textures must have at least one mip level.");
        }

        if (descriptor.ArraySize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "DirectX textures must have at least one array slice.");
        }

        if (descriptor.SampleCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "DirectX textures must have at least one sample.");
        }
    }

    private static bool NeedsBackendArraySliceTextures(DxTexture2DDescriptor descriptor)
    {
        return descriptor.SampleCount > 1 && descriptor.ArraySize > 1;
    }

    private static void ValidateMapMode(DxMapMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Unknown DirectX map mode.");
        }
    }

    private static bool RequiresCpuRead(DxMapMode mode)
    {
        return mode is DxMapMode.Read or DxMapMode.ReadWrite;
    }

    private static bool RequiresCpuWrite(DxMapMode mode)
    {
        return mode is DxMapMode.Write or DxMapMode.ReadWrite or DxMapMode.WriteDiscard or DxMapMode.WriteNoOverwrite;
    }

    private void ValidateGenerateMips(DxShaderResourceViewDescriptor shaderResourceView)
    {
        if (shaderResourceView.Dimension is not DxResourceViewDimension.Texture2D and not DxResourceViewDimension.Texture2DArray)
        {
            throw new NotSupportedException("DirectX mip generation currently supports Texture2D shader-resource views.");
        }

        if (shaderResourceView.MipLevels == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(shaderResourceView), "Shader-resource views must expose at least one mip level.");
        }

        if (shaderResourceView.MostDetailedMip >= Descriptor.MipLevels ||
            shaderResourceView.MipLevels > Descriptor.MipLevels - shaderResourceView.MostDetailedMip)
        {
            throw new ArgumentOutOfRangeException(nameof(shaderResourceView), "Shader-resource view mip range exceeds the texture.");
        }

        if (shaderResourceView.FirstArraySlice >= Descriptor.ArraySize ||
            shaderResourceView.ArraySize == 0 ||
            shaderResourceView.ArraySize > Descriptor.ArraySize - shaderResourceView.FirstArraySlice)
        {
            throw new ArgumentOutOfRangeException(nameof(shaderResourceView), "Shader-resource view array range exceeds the texture.");
        }

        if (Descriptor.SampleCount != 1)
        {
            throw new NotSupportedException("DirectX mip generation currently supports only single-sample textures.");
        }

        if ((Descriptor.Usage & DxTextureUsage.ShaderResource) == 0)
        {
            throw new InvalidOperationException("DirectX mip generation requires shader-resource texture usage.");
        }

        if ((Descriptor.Usage & DxTextureUsage.RenderTarget) == 0)
        {
            throw new InvalidOperationException("DirectX mip generation requires render-target texture usage.");
        }

        if (!IsMipGenerationFormat(Descriptor.Format))
        {
            throw new NotSupportedException($"DirectX mip generation does not support resource format {Descriptor.Format}.");
        }
    }

    private uint GetSubresourceIndex(uint mipLevel, uint arraySlice)
    {
        return checked(mipLevel + arraySlice * Descriptor.MipLevels);
    }

    private static SubresourceInfo GetSubresourceInfo(DxTexture2DDescriptor descriptor, uint subresource)
    {
        var mipLevels = descriptor.MipLevels;
        var mipLevel = subresource % mipLevels;
        var arraySlice = subresource / mipLevels;
        if (arraySlice >= descriptor.ArraySize)
        {
            throw new ArgumentOutOfRangeException(nameof(subresource), "DirectX subresource is outside the texture array/mip range.");
        }

        var offsetBytes = checked(GetArraySliceSizeInBytes(descriptor) * arraySlice);
        for (uint mip = 0; mip < mipLevel; mip++)
        {
            offsetBytes = checked(offsetBytes + GetMipSizeInBytes(descriptor, mip));
        }

        var width = GetMipDimension(descriptor.Width, mipLevel);
        var height = GetMipDimension(descriptor.Height, mipLevel);
        var rowPitch = GetRowPitchInBytes(descriptor, mipLevel);
        var sizeInBytes = checked(rowPitch * height);
        return new SubresourceInfo(mipLevel, arraySlice, width, height, rowPitch, sizeInBytes, offsetBytes);
    }

    private static uint GetMipSizeInBytes(DxTexture2DDescriptor descriptor, uint mipLevel)
    {
        return checked(GetRowPitchInBytes(descriptor, mipLevel) * GetMipDimension(descriptor.Height, mipLevel));
    }

    private static uint GetTextureSizeInBytes(DxTexture2DDescriptor descriptor)
    {
        return checked(GetArraySliceSizeInBytes(descriptor) * descriptor.ArraySize);
    }

    private static uint GetArraySliceSizeInBytes(DxTexture2DDescriptor descriptor)
    {
        var sizeInBytes = 0u;
        for (uint mip = 0; mip < descriptor.MipLevels; mip++)
        {
            sizeInBytes = checked(sizeInBytes + GetMipSizeInBytes(descriptor, mip));
        }

        return sizeInBytes;
    }

    private static uint GetRowPitchInBytes(DxTexture2DDescriptor descriptor, uint mipLevel)
    {
        return checked(GetMipDimension(descriptor.Width, mipLevel) * GetBytesPerPixel(descriptor.Format));
    }

    private static uint GetMipDimension(uint dimension, uint mipLevel)
    {
        if (mipLevel >= 31)
        {
            return 1;
        }

        var shifted = dimension >> checked((int)mipLevel);
        return Math.Max(1u, shifted);
    }

    private static void DownsampleColorMip(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        SubresourceInfo sourceInfo,
        SubresourceInfo destinationInfo)
    {
        const uint BytesPerPixel = 4;
        for (uint y = 0; y < destinationInfo.Height; y++)
        {
            for (uint x = 0; x < destinationInfo.Width; x++)
            {
                var sourceX = x * 2;
                var sourceY = y * 2;
                var red = 0u;
                var green = 0u;
                var blue = 0u;
                var alpha = 0u;
                var count = 0u;

                for (var offsetY = 0u; offsetY < 2; offsetY++)
                {
                    var sampleY = sourceY + offsetY;
                    if (sampleY >= sourceInfo.Height)
                    {
                        continue;
                    }

                    for (var offsetX = 0u; offsetX < 2; offsetX++)
                    {
                        var sampleX = sourceX + offsetX;
                        if (sampleX >= sourceInfo.Width)
                        {
                            continue;
                        }

                        var sourceOffset = checked((int)(sampleY * sourceInfo.RowPitch + sampleX * BytesPerPixel));
                        red += source[sourceOffset];
                        green += source[sourceOffset + 1];
                        blue += source[sourceOffset + 2];
                        alpha += source[sourceOffset + 3];
                        count++;
                    }
                }

                var destinationOffset = checked((int)(y * destinationInfo.RowPitch + x * BytesPerPixel));
                destination[destinationOffset] = AverageByte(red, count);
                destination[destinationOffset + 1] = AverageByte(green, count);
                destination[destinationOffset + 2] = AverageByte(blue, count);
                destination[destinationOffset + 3] = AverageByte(alpha, count);
            }
        }
    }

    private static byte AverageByte(uint total, uint count)
    {
        return checked((byte)((total + count / 2) / count));
    }

    private static uint GetBytesPerPixel(DxResourceFormat format)
    {
        return format switch
        {
            DxResourceFormat.R8Unorm => 1,
            DxResourceFormat.R16Float => 2,
            DxResourceFormat.R32Float or
            DxResourceFormat.R32UInt or
            DxResourceFormat.R32SInt => 4,
            DxResourceFormat.R8G8B8A8Unorm or
            DxResourceFormat.R8G8B8A8UnormSrgb or
            DxResourceFormat.B8G8R8A8Unorm or
            DxResourceFormat.B8G8R8A8UnormSrgb => 4,
            DxResourceFormat.R32G32Float or
            DxResourceFormat.R32G32UInt or
            DxResourceFormat.R32G32SInt => 8,
            DxResourceFormat.R32G32B32A32Float or
            DxResourceFormat.R32G32B32A32UInt or
            DxResourceFormat.R32G32B32A32SInt => 16,
            DxResourceFormat.D24UnormS8UInt or
            DxResourceFormat.D32Float => 4,
            _ => throw new NotSupportedException($"DirectX texture mapping does not support resource format {format}.")
        };
    }

    private static bool IsDepthStencilFormat(DxResourceFormat format)
    {
        return format is DxResourceFormat.D24UnormS8UInt or DxResourceFormat.D32Float;
    }

    private static bool IsMipGenerationFormat(DxResourceFormat format)
    {
        return format is
            DxResourceFormat.R8G8B8A8Unorm or
            DxResourceFormat.R8G8B8A8UnormSrgb or
            DxResourceFormat.B8G8R8A8Unorm or
            DxResourceFormat.B8G8R8A8UnormSrgb;
    }

    private readonly record struct SubresourceInfo(
        uint MipLevel,
        uint ArraySlice,
        uint Width,
        uint Height,
        uint RowPitch,
        uint SizeInBytes,
        uint OffsetBytes);

    protected override void DisposeCore()
    {
        _activeMapping?.Dispose();
        _activeMapping = null;
        _backendTexture?.Dispose();
        _backendTexture = null;
        if (_backendArraySliceTextures is not null)
        {
            for (var sliceIndex = 0; sliceIndex < _backendArraySliceTextures.Length; sliceIndex++)
            {
                _backendArraySliceTextures[sliceIndex].Dispose();
            }

            _backendArraySliceTextures = null;
        }
    }
}

public sealed class ProGpuDirectXTexture3D : ProGpuDirectXResource
{
    private GpuTexture? _backendTexture;
    private byte[]? _cpuShadow;
    private byte[] _writeShadow = [];
    private ProGpuDirectXMappedSubresource? _activeMapping;

    internal ProGpuDirectXTexture3D(ProGpuDirectXDevice device, DxTexture3DDescriptor descriptor)
        : base(device, descriptor.Label)
    {
        ValidateDescriptor(descriptor);
        Descriptor = descriptor;
        AllocateCpuStorage(descriptor);
        AllocateBackendTexture();
    }

    public DxTexture3DDescriptor Descriptor { get; }

    public GpuTexture? BackendTexture => _backendTexture;

    public uint Width => Descriptor.Width;

    public uint Height => Descriptor.Height;

    public uint Depth => Descriptor.Depth;

    public uint LastWriteSizeInBytes { get; private set; }

    public uint Generation { get; private set; }

    public bool IsMapped => _activeMapping is not null;

    public unsafe void WritePixels<T>(ReadOnlySpan<T> pixels) where T : unmanaged
    {
        ThrowIfDisposed();
        var expectedSize = GetTextureSizeInBytes(Descriptor);
        var bytes = MemoryMarshal.AsBytes(pixels);
        if (bytes.Length < expectedSize)
        {
            throw new ArgumentException($"Pixel span is too small ({bytes.Length} bytes, expected {expectedSize} bytes).", nameof(pixels));
        }

        if (_backendTexture is not null)
        {
            UploadAllSubresourcesToBackend(bytes);
        }

        if (_writeShadow.Length > 0)
        {
            bytes.Slice(0, checked((int)expectedSize)).CopyTo(_writeShadow);
            if (_cpuShadow is not null && !ReferenceEquals(_cpuShadow, _writeShadow))
            {
                _writeShadow.CopyTo(_cpuShadow, 0);
            }
        }

        LastWriteSizeInBytes = expectedSize;
        Generation++;
    }

    public byte[] ReadPixels()
    {
        var pixels = new byte[checked((int)GetTextureSizeInBytes(Descriptor))];
        ReadPixels(pixels);
        return pixels;
    }

    public void ReadPixels(Span<byte> destination)
    {
        ThrowIfDisposed();
        if ((Descriptor.CpuAccess & DxCpuAccessFlags.Read) == 0)
        {
            throw new InvalidOperationException("Texture was not created with CPU read access.");
        }

        var expectedSize = GetTextureSizeInBytes(Descriptor);
        if (destination.Length < expectedSize)
        {
            throw new ArgumentException(
                $"Destination span is too small ({destination.Length} bytes, expected {expectedSize} bytes).",
                nameof(destination));
        }

        if (_backendTexture is { IsDisposed: false } texture)
        {
            SynchronizeAllShadowForRead(texture);
            _writeShadow.AsSpan(0, checked((int)expectedSize)).CopyTo(destination);
            return;
        }

        if (_cpuShadow is null)
        {
            throw new InvalidOperationException("Texture readback requires a GPU-backed texture.");
        }

        _cpuShadow.AsSpan(0, checked((int)expectedSize)).CopyTo(destination);
    }

    public ProGpuDirectXMappedSubresource Map(
        DxMapMode mode,
        DxMapFlags flags = DxMapFlags.None,
        uint subresource = 0)
    {
        ThrowIfDisposed();
        ValidateMapMode(mode);
        ValidateMappableSubresource(subresource);
        if (_activeMapping is not null)
        {
            throw new InvalidOperationException("DirectX texture is already mapped.");
        }

        var requiresRead = RequiresCpuRead(mode);
        var requiresWrite = RequiresCpuWrite(mode);
        if (requiresRead && (Descriptor.CpuAccess & DxCpuAccessFlags.Read) == 0)
        {
            throw new InvalidOperationException("DirectX texture was not created with CPU read access.");
        }

        if (requiresWrite && (Descriptor.CpuAccess & DxCpuAccessFlags.Write) == 0)
        {
            throw new InvalidOperationException("DirectX texture was not created with CPU write access.");
        }

        var subresourceInfo = GetSubresourceInfo(Descriptor, subresource);
        if (requiresRead)
        {
            SynchronizeShadowForRead(subresource);
        }
        else if (mode == DxMapMode.WriteDiscard)
        {
            _writeShadow.AsSpan(checked((int)subresourceInfo.OffsetBytes), checked((int)subresourceInfo.SizeInBytes)).Clear();
        }

        _activeMapping = new ProGpuDirectXMappedSubresource(
            this,
            mode,
            flags,
            subresource,
            offsetBytes: subresourceInfo.OffsetBytes,
            sizeInBytes: subresourceInfo.SizeInBytes,
            subresourceInfo.RowPitch,
            subresourceInfo.DepthPitch,
            _writeShadow,
            uploadOnUnmap: requiresWrite);

        return _activeMapping;
    }

    public void Unmap(ProGpuDirectXMappedSubresource mapping)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(mapping);
        if (!ReferenceEquals(mapping.Texture3D, this))
        {
            throw new ArgumentException("Mapped DirectX subresource belongs to a different texture.", nameof(mapping));
        }

        mapping.Unmap();
    }

    internal void CompleteMappedSubresource(ProGpuDirectXMappedSubresource mapping)
    {
        if (!ReferenceEquals(_activeMapping, mapping))
        {
            throw new InvalidOperationException("DirectX texture mapping is not active.");
        }

        if (mapping.UploadOnUnmap)
        {
            var mappedBytes = _writeShadow.AsSpan(
                checked((int)mapping.OffsetBytes),
                checked((int)mapping.SizeInBytes));
            if (_backendTexture is not null)
            {
                var subresourceInfo = GetSubresourceInfo(Descriptor, mapping.Subresource);
                _backendTexture.WritePixelsVolume(
                    mappedBytes,
                    x: 0,
                    y: 0,
                    z: 0,
                    subWidth: subresourceInfo.Width,
                    subHeight: subresourceInfo.Height,
                    subDepth: subresourceInfo.Depth,
                    mipLevel: subresourceInfo.MipLevel);
            }

            if (_cpuShadow is not null && !ReferenceEquals(_cpuShadow, _writeShadow))
            {
                mappedBytes.CopyTo(_cpuShadow.AsSpan(
                    checked((int)mapping.OffsetBytes),
                    checked((int)mapping.SizeInBytes)));
            }

            LastWriteSizeInBytes = mapping.SizeInBytes;
            Generation++;
        }

        _activeMapping = null;
    }

    private void UploadAllSubresourcesToBackend(ReadOnlySpan<byte> bytes)
    {
        if (_backendTexture is null)
        {
            return;
        }

        for (uint subresource = 0; subresource < Descriptor.MipLevels; subresource++)
        {
            var subresourceInfo = GetSubresourceInfo(Descriptor, subresource);
            _backendTexture.WritePixelsVolume(
                bytes.Slice(checked((int)subresourceInfo.OffsetBytes), checked((int)subresourceInfo.SizeInBytes)),
                x: 0,
                y: 0,
                z: 0,
                subWidth: subresourceInfo.Width,
                subHeight: subresourceInfo.Height,
                subDepth: subresourceInfo.Depth,
                mipLevel: subresourceInfo.MipLevel);
        }
    }

    private void SynchronizeAllShadowForRead(GpuTexture texture)
    {
        for (uint subresource = 0; subresource < Descriptor.MipLevels; subresource++)
        {
            var subresourceInfo = GetSubresourceInfo(Descriptor, subresource);
            texture.ReadPixels(
                _writeShadow.AsSpan(
                    checked((int)subresourceInfo.OffsetBytes),
                    checked((int)subresourceInfo.SizeInBytes)),
                subresourceInfo.MipLevel);
        }

        if (_cpuShadow is not null && !ReferenceEquals(_cpuShadow, _writeShadow))
        {
            _writeShadow.CopyTo(_cpuShadow, 0);
        }
    }

    private void SynchronizeShadowForRead(uint subresource)
    {
        if (_backendTexture is not { IsDisposed: false } texture)
        {
            return;
        }

        var subresourceInfo = GetSubresourceInfo(Descriptor, subresource);
        texture.ReadPixels(
            _writeShadow.AsSpan(
                checked((int)subresourceInfo.OffsetBytes),
                checked((int)subresourceInfo.SizeInBytes)),
            subresourceInfo.MipLevel);
        if (_cpuShadow is not null && !ReferenceEquals(_cpuShadow, _writeShadow))
        {
            _writeShadow.AsSpan(
                    checked((int)subresourceInfo.OffsetBytes),
                    checked((int)subresourceInfo.SizeInBytes))
                .CopyTo(_cpuShadow.AsSpan(
                    checked((int)subresourceInfo.OffsetBytes),
                    checked((int)subresourceInfo.SizeInBytes)));
        }
    }

    private void AllocateCpuStorage(DxTexture3DDescriptor descriptor)
    {
        if ((descriptor.CpuAccess & (DxCpuAccessFlags.Read | DxCpuAccessFlags.Write)) == 0)
        {
            _cpuShadow = null;
            _writeShadow = [];
            LastWriteSizeInBytes = 0;
            return;
        }

        var byteSize = GetTextureSizeInBytes(descriptor);
        _cpuShadow = new byte[byteSize];
        _writeShadow = _cpuShadow ?? new byte[byteSize];
        LastWriteSizeInBytes = 0;
    }

    private void AllocateBackendTexture()
    {
        if (Device.Context is not { } context || !Device.IsGpuBacked)
        {
            return;
        }

        _backendTexture = new GpuTexture(
            context,
            Descriptor.Width,
            Descriptor.Height,
            ProGpuDirectXFormatConverter.ToTextureFormat(Descriptor.Format),
            ProGpuDirectXFormatConverter.ToTextureUsage(Descriptor.Usage, Descriptor.CpuAccess),
            Descriptor.Label,
            sampleCount: 1,
            ProGpuDirectXFormatConverter.ToTextureAlphaMode(Descriptor.Format),
            depthOrArrayLayers: Descriptor.Depth,
            mipLevelCount: Descriptor.MipLevels,
            dimension: GpuTextureDimension.Dimension3D);
    }

    private void ValidateMappableSubresource(uint subresource)
    {
        if (subresource >= Descriptor.MipLevels)
        {
            throw new ArgumentOutOfRangeException(nameof(subresource), "DirectX 3D texture mapping subresource is outside the mip range.");
        }

        _ = GetBytesPerPixel(Descriptor.Format);
    }

    private static void ValidateDescriptor(DxTexture3DDescriptor descriptor)
    {
        if (descriptor.Width == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "DirectX 3D textures must have a non-zero width.");
        }

        if (descriptor.Height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "DirectX 3D textures must have a non-zero height.");
        }

        if (descriptor.Depth == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "DirectX 3D textures must have a non-zero depth.");
        }

        if (descriptor.MipLevels == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "DirectX 3D textures must have at least one mip level.");
        }

        if (IsDepthStencilFormat(descriptor.Format))
        {
            throw new NotSupportedException("DirectX 3D depth/stencil textures are not supported by the ProGPU shim.");
        }
    }

    private static void ValidateMapMode(DxMapMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Unknown DirectX map mode.");
        }
    }

    private static bool RequiresCpuRead(DxMapMode mode)
    {
        return mode is DxMapMode.Read or DxMapMode.ReadWrite;
    }

    private static bool RequiresCpuWrite(DxMapMode mode)
    {
        return mode is DxMapMode.Write or DxMapMode.ReadWrite or DxMapMode.WriteDiscard or DxMapMode.WriteNoOverwrite;
    }

    private static Subresource3DInfo GetSubresourceInfo(DxTexture3DDescriptor descriptor, uint subresource)
    {
        if (subresource >= descriptor.MipLevels)
        {
            throw new ArgumentOutOfRangeException(nameof(subresource), "DirectX 3D subresource is outside the mip range.");
        }

        var offsetBytes = 0u;
        for (uint mip = 0; mip < subresource; mip++)
        {
            offsetBytes = checked(offsetBytes + GetMipSizeInBytes(descriptor, mip));
        }

        var width = GetMipDimension(descriptor.Width, subresource);
        var height = GetMipDimension(descriptor.Height, subresource);
        var depth = GetMipDimension(descriptor.Depth, subresource);
        var rowPitch = GetRowPitchInBytes(descriptor, subresource);
        var depthPitch = checked(rowPitch * height);
        var sizeInBytes = checked(depthPitch * depth);
        return new Subresource3DInfo(subresource, width, height, depth, rowPitch, depthPitch, sizeInBytes, offsetBytes);
    }

    private static uint GetTextureSizeInBytes(DxTexture3DDescriptor descriptor)
    {
        var sizeInBytes = 0u;
        for (uint mip = 0; mip < descriptor.MipLevels; mip++)
        {
            sizeInBytes = checked(sizeInBytes + GetMipSizeInBytes(descriptor, mip));
        }

        return sizeInBytes;
    }

    private static uint GetMipSizeInBytes(DxTexture3DDescriptor descriptor, uint mipLevel)
    {
        return checked(GetRowPitchInBytes(descriptor, mipLevel)
            * GetMipDimension(descriptor.Height, mipLevel)
            * GetMipDimension(descriptor.Depth, mipLevel));
    }

    private static uint GetRowPitchInBytes(DxTexture3DDescriptor descriptor, uint mipLevel)
    {
        return checked(GetMipDimension(descriptor.Width, mipLevel) * GetBytesPerPixel(descriptor.Format));
    }

    private static uint GetMipDimension(uint dimension, uint mipLevel)
    {
        if (mipLevel >= 31)
        {
            return 1;
        }

        var shifted = dimension >> checked((int)mipLevel);
        return Math.Max(1u, shifted);
    }

    private static uint GetBytesPerPixel(DxResourceFormat format)
    {
        return format switch
        {
            DxResourceFormat.R8Unorm => 1,
            DxResourceFormat.R16Float => 2,
            DxResourceFormat.R32Float or
            DxResourceFormat.R32UInt or
            DxResourceFormat.R32SInt => 4,
            DxResourceFormat.R8G8B8A8Unorm or
            DxResourceFormat.R8G8B8A8UnormSrgb or
            DxResourceFormat.B8G8R8A8Unorm or
            DxResourceFormat.B8G8R8A8UnormSrgb => 4,
            DxResourceFormat.R32G32Float or
            DxResourceFormat.R32G32UInt or
            DxResourceFormat.R32G32SInt => 8,
            DxResourceFormat.R32G32B32A32Float or
            DxResourceFormat.R32G32B32A32UInt or
            DxResourceFormat.R32G32B32A32SInt => 16,
            _ => throw new NotSupportedException($"DirectX 3D texture mapping does not support resource format {format}.")
        };
    }

    private static bool IsDepthStencilFormat(DxResourceFormat format)
    {
        return format is DxResourceFormat.D24UnormS8UInt or DxResourceFormat.D32Float;
    }

    private readonly record struct Subresource3DInfo(
        uint MipLevel,
        uint Width,
        uint Height,
        uint Depth,
        uint RowPitch,
        uint DepthPitch,
        uint SizeInBytes,
        uint OffsetBytes);

    protected override void DisposeCore()
    {
        _activeMapping?.Dispose();
        _activeMapping = null;
        _backendTexture?.Dispose();
        _backendTexture = null;
    }
}

public sealed class ProGpuDirectXSwapChain : IDisposable
{
    private readonly ProGpuDirectXDevice _device;
    private ProGpuDirectXTexture2D _backBuffer;
    private bool _isDisposed;

    internal ProGpuDirectXSwapChain(ProGpuDirectXDevice device, DxSwapChainDescriptor descriptor)
    {
        _device = device;
        Descriptor = descriptor;
        _backBuffer = CreateBackBuffer(device, descriptor);
    }

    public DxSwapChainDescriptor Descriptor { get; private set; }

    public ProGpuDirectXTexture2D BackBuffer => _backBuffer;

    public ulong PresentCount { get; private set; }

    public ProGpuDirectXTexture2D AcquireBackBuffer()
    {
        ThrowIfDisposed();
        return _backBuffer;
    }

    public void Resize(uint width, uint height)
    {
        ThrowIfDisposed();
        if (width == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Descriptor = Descriptor with { Width = width, Height = height };
        _backBuffer.Resize(width, height);
    }

    public void Present()
    {
        ThrowIfDisposed();
        PresentCount++;
    }

    private static ProGpuDirectXTexture2D CreateBackBuffer(ProGpuDirectXDevice device, DxSwapChainDescriptor descriptor)
    {
        return device.CreateTexture2D(new DxTexture2DDescriptor
        {
            Width = descriptor.Width,
            Height = descriptor.Height,
            Format = descriptor.Format,
            Usage = DxTextureUsage.RenderTarget | DxTextureUsage.Present | DxTextureUsage.CopyDestination,
            Label = descriptor.Label + ".BackBuffer"
        });
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(ProGpuDirectXSwapChain));
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _backBuffer.Dispose();
        _isDisposed = true;
    }
}
