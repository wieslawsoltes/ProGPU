using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ProGPU.Backend;

namespace SkiaSharp;

#nullable disable

public class GRContextOptions
{
    public bool AllowPathMaskCaching { get; set; }

    public bool AvoidStencilBuffers { get; set; }

    public int BufferMapThreshold { get; set; }

    public bool DoManualMipmapping { get; set; }

    public int GlyphCacheTextureMaximumBytes { get; set; }

    public int RuntimeProgramCacheSize { get; set; }
}

public enum GRSurfaceOrigin
{
    TopLeft = 0,
    BottomLeft = 1,
}

public enum GRBackend
{
    Metal,
    OpenGL,
    Vulkan,
    Dawn,
    Direct3D,
    Unsupported,
}

[Flags]
public enum GRBackendState : uint
{
    None = 0,
    All = uint.MaxValue,
}

[Flags]
public enum GRGlBackendState : uint
{
    None = 0,
    RenderTarget = 1u << 0,
    TextureBinding = 1u << 1,
    View = 1u << 2,
    Blend = 1u << 3,
    MSAAEnable = 1u << 4,
    Vertex = 1u << 5,
    Stencil = 1u << 6,
    PixelStore = 1u << 7,
    Program = 1u << 8,
    FixedFunction = 1u << 9,
    Misc = 1u << 10,
    PathRendering = 1u << 11,
    All = 0xffffu,
}

public struct GRGlFramebufferInfo : IEquatable<GRGlFramebufferInfo>
{
    private uint _framebufferObjectId;
    private uint _format;
    private byte _protected;

    public GRGlFramebufferInfo(uint fboId)
        : this(fboId, 0)
    {
    }

    public GRGlFramebufferInfo(uint fboId, uint format)
    {
        _framebufferObjectId = fboId;
        _format = format;
        _protected = 0;
    }

    public uint FramebufferObjectId
    {
        readonly get => _framebufferObjectId;
        set => _framebufferObjectId = value;
    }

    public uint Format
    {
        readonly get => _format;
        set => _format = value;
    }

    public bool Protected
    {
        readonly get => _protected != 0;
        set => _protected = value ? (byte)1 : (byte)0;
    }

    public readonly bool Equals(GRGlFramebufferInfo obj) =>
        _framebufferObjectId == obj._framebufferObjectId &&
        _format == obj._format &&
        _protected == obj._protected;

    public override readonly bool Equals(object obj) =>
        obj is GRGlFramebufferInfo info && Equals(info);

    public override readonly int GetHashCode() =>
        HashCode.Combine(_framebufferObjectId, _format, _protected);

    public static bool operator ==(GRGlFramebufferInfo left, GRGlFramebufferInfo right) =>
        left.Equals(right);

    public static bool operator !=(GRGlFramebufferInfo left, GRGlFramebufferInfo right) =>
        !left.Equals(right);
}

public struct GRGlTextureInfo : IEquatable<GRGlTextureInfo>
{
    private uint _target;
    private uint _id;
    private uint _format;
    private byte _protected;

    public GRGlTextureInfo(uint target, uint id)
        : this(target, id, 0)
    {
    }

    public GRGlTextureInfo(uint target, uint id, uint format)
    {
        _target = target;
        _id = id;
        _format = format;
        _protected = 0;
    }

    public uint Target
    {
        readonly get => _target;
        set => _target = value;
    }

    public uint Id
    {
        readonly get => _id;
        set => _id = value;
    }

    public uint Format
    {
        readonly get => _format;
        set => _format = value;
    }

    public bool Protected
    {
        readonly get => _protected != 0;
        set => _protected = value ? (byte)1 : (byte)0;
    }

    public readonly bool Equals(GRGlTextureInfo obj) =>
        _target == obj._target &&
        _id == obj._id &&
        _format == obj._format &&
        _protected == obj._protected;

    public override readonly bool Equals(object obj) =>
        obj is GRGlTextureInfo info && Equals(info);

    public override readonly int GetHashCode() =>
        HashCode.Combine(_target, _id, _format, _protected);

    public static bool operator ==(GRGlTextureInfo left, GRGlTextureInfo right) =>
        left.Equals(right);

    public static bool operator !=(GRGlTextureInfo left, GRGlTextureInfo right) =>
        !left.Equals(right);
}

public struct GRMtlTextureInfo
{
    private IntPtr _textureHandle;

    public GRMtlTextureInfo(IntPtr textureHandle)
    {
        _textureHandle = textureHandle;
    }

    public IntPtr TextureHandle
    {
        readonly get => _textureHandle;
        set => _textureHandle = value;
    }

    public readonly bool Equals(GRMtlTextureInfo obj) =>
        _textureHandle == obj._textureHandle;

    public override readonly bool Equals(object obj) =>
        obj is GRMtlTextureInfo info && Equals(info);

    public override readonly int GetHashCode() => _textureHandle.GetHashCode();

    public static bool operator ==(GRMtlTextureInfo left, GRMtlTextureInfo right) =>
        left.Equals(right);

    public static bool operator !=(GRMtlTextureInfo left, GRMtlTextureInfo right) =>
        !left.Equals(right);
}

#nullable restore

#nullable disable

public struct GRVkAlloc : IEquatable<GRVkAlloc>
{
    private ulong _memory;
    private ulong _size;
    private ulong _offset;
    private uint _flags;
    private IntPtr _backendMemory;
#pragma warning disable CS0649
    private byte _usesSystemHeap;
#pragma warning restore CS0649

    public ulong Memory
    {
        readonly get => _memory;
        set => _memory = value;
    }

    public ulong Size
    {
        readonly get => _size;
        set => _size = value;
    }

    public ulong Offset
    {
        readonly get => _offset;
        set => _offset = value;
    }

    public uint Flags
    {
        readonly get => _flags;
        set => _flags = value;
    }

    public IntPtr BackendMemory
    {
        readonly get => _backendMemory;
        set => _backendMemory = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(GRVkAlloc obj) =>
        _memory == obj._memory &&
        _size == obj._size &&
        _offset == obj._offset &&
        _flags == obj._flags &&
        _backendMemory == obj._backendMemory &&
        _usesSystemHeap == obj._usesSystemHeap;

    public override readonly bool Equals(object obj) =>
        obj is GRVkAlloc alloc && Equals(alloc);

    public override readonly int GetHashCode() =>
        HashCode.Combine(_memory, _size, _offset, _flags, _backendMemory, _usesSystemHeap);

    public static bool operator ==(GRVkAlloc left, GRVkAlloc right) => left.Equals(right);

    public static bool operator !=(GRVkAlloc left, GRVkAlloc right) => !left.Equals(right);
}

public struct GRVkYcbcrComponents : IEquatable<GRVkYcbcrComponents>
{
    private uint _r;
    private uint _g;
    private uint _b;
    private uint _a;

    public uint R
    {
        readonly get => _r;
        set => _r = value;
    }

    public uint G
    {
        readonly get => _g;
        set => _g = value;
    }

    public uint B
    {
        readonly get => _b;
        set => _b = value;
    }

    public uint A
    {
        readonly get => _a;
        set => _a = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(GRVkYcbcrComponents obj) =>
        _r == obj._r && _g == obj._g && _b == obj._b && _a == obj._a;

    public override readonly bool Equals(object obj) =>
        obj is GRVkYcbcrComponents components && Equals(components);

    public override readonly int GetHashCode() => HashCode.Combine(_r, _g, _b, _a);

    public static bool operator ==(GRVkYcbcrComponents left, GRVkYcbcrComponents right) =>
        left.Equals(right);

    public static bool operator !=(GRVkYcbcrComponents left, GRVkYcbcrComponents right) =>
        !left.Equals(right);
}

public struct GRVkYcbcrConversionInfo : IEquatable<GRVkYcbcrConversionInfo>
{
    private uint _format;
    private ulong _externalFormat;
    private uint _ycbcrModel;
    private uint _ycbcrRange;
    private uint _xChromaOffset;
    private uint _yChromaOffset;
    private uint _chromaFilter;
    private uint _forceExplicitReconstruction;
    private GRVkYcbcrComponents _components;
    private byte _supportsLinearFilter;
    private byte _samplerFilterMustMatchChromaFilter;

    public uint Format
    {
        readonly get => _format;
        set => _format = value;
    }

    public ulong ExternalFormat
    {
        readonly get => _externalFormat;
        set => _externalFormat = value;
    }

    public uint YcbcrModel
    {
        readonly get => _ycbcrModel;
        set => _ycbcrModel = value;
    }

    public uint YcbcrRange
    {
        readonly get => _ycbcrRange;
        set => _ycbcrRange = value;
    }

    public uint XChromaOffset
    {
        readonly get => _xChromaOffset;
        set => _xChromaOffset = value;
    }

    public uint YChromaOffset
    {
        readonly get => _yChromaOffset;
        set => _yChromaOffset = value;
    }

    public uint ChromaFilter
    {
        readonly get => _chromaFilter;
        set => _chromaFilter = value;
    }

    public uint ForceExplicitReconstruction
    {
        readonly get => _forceExplicitReconstruction;
        set => _forceExplicitReconstruction = value;
    }

    public GRVkYcbcrComponents Components
    {
        readonly get => _components;
        set => _components = value;
    }

    public bool SupportsLinearFilter
    {
        readonly get => _supportsLinearFilter != 0;
        set => _supportsLinearFilter = value ? (byte)1 : (byte)0;
    }

    public bool SamplerFilterMustMatchChromaFilter
    {
        readonly get => _samplerFilterMustMatchChromaFilter != 0;
        set => _samplerFilterMustMatchChromaFilter = value ? (byte)1 : (byte)0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(GRVkYcbcrConversionInfo obj) =>
        _format == obj._format &&
        _externalFormat == obj._externalFormat &&
        _ycbcrModel == obj._ycbcrModel &&
        _ycbcrRange == obj._ycbcrRange &&
        _xChromaOffset == obj._xChromaOffset &&
        _yChromaOffset == obj._yChromaOffset &&
        _chromaFilter == obj._chromaFilter &&
        _forceExplicitReconstruction == obj._forceExplicitReconstruction &&
        _components.Equals(obj._components) &&
        _supportsLinearFilter == obj._supportsLinearFilter &&
        _samplerFilterMustMatchChromaFilter == obj._samplerFilterMustMatchChromaFilter;

    public override readonly bool Equals(object obj) =>
        obj is GRVkYcbcrConversionInfo info && Equals(info);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_format);
        hash.Add(_externalFormat);
        hash.Add(_ycbcrModel);
        hash.Add(_ycbcrRange);
        hash.Add(_xChromaOffset);
        hash.Add(_yChromaOffset);
        hash.Add(_chromaFilter);
        hash.Add(_forceExplicitReconstruction);
        hash.Add(_components);
        hash.Add(_supportsLinearFilter);
        hash.Add(_samplerFilterMustMatchChromaFilter);
        return hash.ToHashCode();
    }

    public static bool operator ==(GRVkYcbcrConversionInfo left, GRVkYcbcrConversionInfo right) =>
        left.Equals(right);

    public static bool operator !=(GRVkYcbcrConversionInfo left, GRVkYcbcrConversionInfo right) =>
        !left.Equals(right);
}

[Obsolete("Use GRVkYcbcrConversionInfo instead.")]
public struct GrVkYcbcrConversionInfo
{
    private GRVkYcbcrConversionInfo _value;

    public uint Format
    {
        readonly get => _value.Format;
        set => _value.Format = value;
    }

    public ulong ExternalFormat
    {
        readonly get => _value.ExternalFormat;
        set => _value.ExternalFormat = value;
    }

    public uint YcbcrModel
    {
        readonly get => _value.YcbcrModel;
        set => _value.YcbcrModel = value;
    }

    public uint YcbcrRange
    {
        readonly get => _value.YcbcrRange;
        set => _value.YcbcrRange = value;
    }

    public uint XChromaOffset
    {
        readonly get => _value.XChromaOffset;
        set => _value.XChromaOffset = value;
    }

    public uint YChromaOffset
    {
        readonly get => _value.YChromaOffset;
        set => _value.YChromaOffset = value;
    }

    public uint ChromaFilter
    {
        readonly get => _value.ChromaFilter;
        set => _value.ChromaFilter = value;
    }

    public uint ForceExplicitReconstruction
    {
        readonly get => _value.ForceExplicitReconstruction;
        set => _value.ForceExplicitReconstruction = value;
    }

    public GRVkYcbcrComponents Components
    {
        readonly get => _value.Components;
        set => _value.Components = value;
    }

    public bool SupportsLinearFilter
    {
        readonly get => _value.SupportsLinearFilter;
        set => _value.SupportsLinearFilter = value;
    }

    public bool SamplerFilterMustMatchChromaFilter
    {
        readonly get => _value.SamplerFilterMustMatchChromaFilter;
        set => _value.SamplerFilterMustMatchChromaFilter = value;
    }

    [Obsolete("FormatFeatures is no longer supported in the native API.")]
    public uint FormatFeatures
    {
        readonly get => 0;
        set { }
    }

    public static implicit operator GRVkYcbcrConversionInfo(GrVkYcbcrConversionInfo value) =>
        value._value;

    public static implicit operator GrVkYcbcrConversionInfo(GRVkYcbcrConversionInfo value) =>
        new() { _value = value };
}

public struct GRVkImageInfo : IEquatable<GRVkImageInfo>
{
    private ulong _image;
    private GRVkAlloc _alloc;
    private uint _imageTiling;
    private uint _imageLayout;
    private uint _format;
    private uint _imageUsageFlags;
    private uint _sampleCount;
    private uint _levelCount;
    private uint _currentQueueFamily;
    private byte _protected;
    private GRVkYcbcrConversionInfo _ycbcrConversionInfo;
    private uint _sharingMode;

    public ulong Image
    {
        readonly get => _image;
        set => _image = value;
    }

    public GRVkAlloc Alloc
    {
        readonly get => _alloc;
        set => _alloc = value;
    }

    public uint ImageTiling
    {
        readonly get => _imageTiling;
        set => _imageTiling = value;
    }

    public uint ImageLayout
    {
        readonly get => _imageLayout;
        set => _imageLayout = value;
    }

    public uint Format
    {
        readonly get => _format;
        set => _format = value;
    }

    public uint ImageUsageFlags
    {
        readonly get => _imageUsageFlags;
        set => _imageUsageFlags = value;
    }

    public uint SampleCount
    {
        readonly get => _sampleCount;
        set => _sampleCount = value;
    }

    public uint LevelCount
    {
        readonly get => _levelCount;
        set => _levelCount = value;
    }

    public uint CurrentQueueFamily
    {
        readonly get => _currentQueueFamily;
        set => _currentQueueFamily = value;
    }

    public bool Protected
    {
        readonly get => _protected != 0;
        set => _protected = value ? (byte)1 : (byte)0;
    }

    public GRVkYcbcrConversionInfo YcbcrConversionInfo
    {
        readonly get => _ycbcrConversionInfo;
        set => _ycbcrConversionInfo = value;
    }

    public uint SharingMode
    {
        readonly get => _sharingMode;
        set => _sharingMode = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Equals(GRVkImageInfo obj) =>
        _image == obj._image &&
        _alloc.Equals(obj._alloc) &&
        _imageTiling == obj._imageTiling &&
        _imageLayout == obj._imageLayout &&
        _format == obj._format &&
        _imageUsageFlags == obj._imageUsageFlags &&
        _sampleCount == obj._sampleCount &&
        _levelCount == obj._levelCount &&
        _currentQueueFamily == obj._currentQueueFamily &&
        _protected == obj._protected &&
        _ycbcrConversionInfo.Equals(obj._ycbcrConversionInfo) &&
        _sharingMode == obj._sharingMode;

    public override readonly bool Equals(object obj) =>
        obj is GRVkImageInfo info && Equals(info);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_image);
        hash.Add(_alloc);
        hash.Add(_imageTiling);
        hash.Add(_imageLayout);
        hash.Add(_format);
        hash.Add(_imageUsageFlags);
        hash.Add(_sampleCount);
        hash.Add(_levelCount);
        hash.Add(_currentQueueFamily);
        hash.Add(_protected);
        hash.Add(_ycbcrConversionInfo);
        hash.Add(_sharingMode);
        return hash.ToHashCode();
    }

    public static bool operator ==(GRVkImageInfo left, GRVkImageInfo right) => left.Equals(right);

    public static bool operator !=(GRVkImageInfo left, GRVkImageInfo right) => !left.Equals(right);
}

#nullable restore

public class GRD3DTextureResourceInfo : IDisposable
{
    public IntPtr Resource { get; set; }

    public uint ResourceState { get; set; }

    public uint Format { get; set; }

    public uint LevelCount { get; set; }

    public uint SampleCount { get; set; }

    public uint SampleQualityPattern { get; set; }

    public bool Protected { get; set; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

#nullable disable

public delegate IntPtr GRVkGetProcedureAddressDelegate(string name, IntPtr instance, IntPtr device);

public class GRVkExtensions : SKObject
{
    private readonly HashSet<string> _extensions = new(StringComparer.Ordinal);

    private GRVkExtensions()
        : base(SKObjectHandle.Create(), owns: true)
    {
    }

    public static GRVkExtensions Create(
        GRVkGetProcedureAddressDelegate getProc,
        IntPtr vkInstance,
        IntPtr vkPhysicalDevice,
        string[] instanceExtensions,
        string[] deviceExtensions)
    {
        var extensions = new GRVkExtensions();
        extensions.Initialize(
            getProc,
            vkInstance,
            vkPhysicalDevice,
            instanceExtensions,
            deviceExtensions);
        return extensions;
    }

    public void Initialize(
        GRVkGetProcedureAddressDelegate getProc,
        IntPtr vkInstance,
        IntPtr vkPhysicalDevice)
    {
        Initialize(getProc, vkInstance, vkPhysicalDevice, [], []);
    }

    public void Initialize(
        GRVkGetProcedureAddressDelegate getProc,
        IntPtr vkInstance,
        IntPtr vkPhysicalDevice,
        string[] instanceExtensions,
        string[] deviceExtensions)
    {
        ArgumentNullException.ThrowIfNull(getProc);
        ArgumentNullException.ThrowIfNull(instanceExtensions);
        ArgumentNullException.ThrowIfNull(deviceExtensions);
        _extensions.Clear();
        AddExtensions(instanceExtensions);
        AddExtensions(deviceExtensions);
    }

    public void HasExtension(string extension, int minVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentOutOfRangeException.ThrowIfNegative(minVersion);
        _ = _extensions.Contains(extension);
    }

    protected override void DisposeNative() => _extensions.Clear();

    private void AddExtensions(IEnumerable<string> extensions)
    {
        foreach (var extension in extensions)
        {
            if (!string.IsNullOrWhiteSpace(extension))
            {
                _extensions.Add(extension);
            }
        }
    }
}

public class GRD3DBackendContext : IDisposable
{
    public IntPtr Adapter { get; set; }

    public IntPtr Device { get; set; }

    public bool ProtectedContext { get; set; }

    public IntPtr Queue { get; set; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

public class GRVkBackendContext : IDisposable
{
    public IntPtr VkInstance { get; set; }
    public IntPtr VkPhysicalDevice { get; set; }
    public IntPtr VkDevice { get; set; }
    public IntPtr VkQueue { get; set; }
    public uint GraphicsQueueIndex { get; set; }
    public GRVkGetProcedureAddressDelegate GetProcedureAddress { get; set; }
    public GRVkExtensions Extensions { get; set; }
    public uint MaxAPIVersion { get; set; }
    public bool ProtectedContext { get; set; }
    public IntPtr VkPhysicalDeviceFeatures { get; set; }
    public IntPtr VkPhysicalDeviceFeatures2 { get; set; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

public class GRMtlBackendContext : IDisposable
{
    public IntPtr DeviceHandle { get; set; }
    public IntPtr QueueHandle { get; set; }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}

public delegate IntPtr GRGlGetProcedureAddressDelegate(string name);

public class GRGlInterface : SKObject
{
    private readonly GRGlGetProcedureAddressDelegate _getProcedureAddress;

    private GRGlInterface(GRGlGetProcedureAddressDelegate getProcedureAddress)
        : base(SKObjectHandle.Create(), owns: true)
    {
        _getProcedureAddress = getProcedureAddress;
    }

    public static GRGlInterface Create() => new(null);

    public static GRGlInterface Create(GRGlGetProcedureAddressDelegate get)
    {
        ArgumentNullException.ThrowIfNull(get);
        return new GRGlInterface(get);
    }

    public static GRGlInterface CreateAngle() => new(null);

    public static GRGlInterface CreateAngle(GRGlGetProcedureAddressDelegate get) => Create(get);

    public static GRGlInterface CreateEvas(IntPtr evas) => new(null);

    public static GRGlInterface CreateOpenGl(GRGlGetProcedureAddressDelegate get) => Create(get);

    public static GRGlInterface CreateGles(GRGlGetProcedureAddressDelegate get) => Create(get);

    public static GRGlInterface CreateWebGl(GRGlGetProcedureAddressDelegate get) => Create(get);

    public bool HasExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        return false;
    }

    public bool Validate() => !IsDisposed && (_getProcedureAddress is not null || Handle != IntPtr.Zero);

    protected override void Dispose(bool disposing) => base.Dispose(disposing);

}

#nullable disable

public class GRBackendRenderTarget : SKObject
{
    private readonly GRGlFramebufferInfo _glInfo;
    private readonly bool _valid;

    public GRBackendRenderTarget(int width, int height, GpuTexture texture)
        : this(width, height, (int)texture.SampleCount, texture)
    {
    }

    public GRBackendRenderTarget(int width, int height, int sampleCount, GpuTexture texture)
        : base(SKObjectHandle.Create(), owns: true)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Render target width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Render target height must be positive.");
        }

        if (texture.Width != (uint)width || texture.Height != (uint)height)
        {
            throw new ArgumentException("Backend texture dimensions must match the render target dimensions.", nameof(texture));
        }

        Width = width;
        Height = height;
        SampleCount = sampleCount;
        BackendTexture = texture;
        Backend = GRBackend.Dawn;
        _valid = true;
    }

    public GRBackendRenderTarget(int width, int height, int sampleCount, int stencilBits, GRGlFramebufferInfo glInfo)
        : base(SKObjectHandle.Create(), owns: true)
    {
        Width = width;
        Height = height;
        SampleCount = sampleCount;
        StencilBits = stencilBits;
        Backend = GRBackend.OpenGL;
        _glInfo = glInfo;
        _valid = width > 0 && height > 0;
    }

    public GRBackendRenderTarget(int width, int height, int sampleCount, GRVkImageInfo vkImageInfo)
        : base(SKObjectHandle.Create(), owns: true)
    {
        Width = width;
        Height = height;
        SampleCount = sampleCount;
        Backend = GRBackend.Vulkan;
        _valid = width > 0 && height > 0 && vkImageInfo.Image != 0;
    }

    public GRBackendRenderTarget(int width, int height, GRVkImageInfo vkImageInfo)
        : this(width, height, (int)Math.Max(1u, vkImageInfo.SampleCount), vkImageInfo)
    {
    }

    public GRBackendRenderTarget(int width, int height, GRMtlTextureInfo mtlInfo)
        : base(SKObjectHandle.Create(), owns: true)
    {
        Width = width;
        Height = height;
        SampleCount = 1;
        Backend = GRBackend.Metal;
        _valid = width > 0 && height > 0 && mtlInfo.TextureHandle != IntPtr.Zero;
    }

    public GRBackendRenderTarget(int width, int height, int sampleCount, GRMtlTextureInfo mtlTextureInfo)
        : base(SKObjectHandle.Create(), owns: true)
    {
        Width = width;
        Height = height;
        SampleCount = sampleCount;
        Backend = GRBackend.Metal;
        _valid = width > 0 && height > 0 && mtlTextureInfo.TextureHandle != IntPtr.Zero;
    }

    public GRBackendRenderTarget(int width, int height, GRD3DTextureResourceInfo d3dTextureInfo)
        : base(SKObjectHandle.Create(), owns: true)
    {
        ArgumentNullException.ThrowIfNull(d3dTextureInfo);
        Width = width;
        Height = height;
        SampleCount = (int)Math.Max(1u, d3dTextureInfo.SampleCount);
        Backend = GRBackend.Direct3D;
        _valid = width > 0 && height > 0 && d3dTextureInfo.Resource != IntPtr.Zero;
    }

    public int Width { get; }

    public int Height { get; }

    public int SampleCount { get; }

    public int StencilBits { get; }

    public GRBackend Backend { get; }

    public SKSizeI Size => new(Width, Height);

    public SKRectI Rect => new(0, 0, Width, Height);

    public bool IsValid => !IsDisposed && _valid;

    /// <summary>
    /// Gets the ProGPU Dawn texture when this wrapper was created through the
    /// typed WebGPU extension constructor. Native descriptor constructors
    /// return <see langword="null"/>.
    /// </summary>
    public GpuTexture BackendTexture { get; }

    public GRGlFramebufferInfo GetGlFramebufferInfo() =>
        Backend == GRBackend.OpenGL ? _glInfo : default;

    public bool GetGlFramebufferInfo(out GRGlFramebufferInfo glInfo)
    {
        if (Backend == GRBackend.OpenGL)
        {
            glInfo = _glInfo;
            return true;
        }

        glInfo = default;
        return false;
    }

    protected override void Dispose(bool disposing) => base.Dispose(disposing);

    protected override void DisposeNative()
    {
        // Backend handles are borrowed. Disposing the wrapper never destroys
        // the caller-owned GL/Vulkan/Metal/D3D/WebGPU resource.
    }
}

public class GRBackendTexture : SKObject
{
    private readonly GRGlTextureInfo _glInfo;
    private readonly bool _valid;

    public GRBackendTexture(GpuTexture texture, bool mipmapped = false)
        : base(SKObjectHandle.Create(), owns: true)
    {
        ArgumentNullException.ThrowIfNull(texture);
        Width = checked((int)texture.Width);
        Height = checked((int)texture.Height);
        HasMipMaps = mipmapped || texture.MipLevelCount > 1;
        BackendTexture = texture;
        Backend = GRBackend.Dawn;
        _valid = Width > 0 && Height > 0;
    }

    public GRBackendTexture(int width, int height, bool mipmapped, GRGlTextureInfo glInfo)
        : base(SKObjectHandle.Create(), owns: true)
    {
        Width = width;
        Height = height;
        HasMipMaps = mipmapped;
        Backend = GRBackend.OpenGL;
        _glInfo = glInfo;
        _valid = width > 0 && height > 0 && glInfo.Id != 0;
    }

    public GRBackendTexture(int width, int height, GRVkImageInfo vkInfo)
        : base(SKObjectHandle.Create(), owns: true)
    {
        Width = width;
        Height = height;
        HasMipMaps = vkInfo.LevelCount > 1;
        Backend = GRBackend.Vulkan;
        _valid = width > 0 && height > 0 && vkInfo.Image != 0;
    }

    public GRBackendTexture(int width, int height, bool mipmapped, GRMtlTextureInfo mtlInfo)
        : base(SKObjectHandle.Create(), owns: true)
    {
        Width = width;
        Height = height;
        HasMipMaps = mipmapped;
        Backend = GRBackend.Metal;
        _valid = width > 0 && height > 0 && mtlInfo.TextureHandle != IntPtr.Zero;
    }

    public GRBackendTexture(int width, int height, GRD3DTextureResourceInfo d3dTextureInfo)
        : base(SKObjectHandle.Create(), owns: true)
    {
        ArgumentNullException.ThrowIfNull(d3dTextureInfo);
        Width = width;
        Height = height;
        HasMipMaps = d3dTextureInfo.LevelCount > 1;
        Backend = GRBackend.Direct3D;
        _valid = width > 0 && height > 0 && d3dTextureInfo.Resource != IntPtr.Zero;
    }

    public int Width { get; }

    public int Height { get; }

    public GRBackend Backend { get; }

    public SKSizeI Size => new(Width, Height);

    public SKRectI Rect => new(0, 0, Width, Height);

    public bool HasMipMaps { get; }

    public bool IsValid => !IsDisposed && _valid;

    /// <summary>
    /// Gets the ProGPU Dawn texture when this wrapper was created through the
    /// typed WebGPU extension constructor. Native descriptor constructors
    /// return <see langword="null"/>.
    /// </summary>
    public GpuTexture BackendTexture { get; }

    public GRGlTextureInfo GetGlTextureInfo() =>
        Backend == GRBackend.OpenGL ? _glInfo : default;

    public bool GetGlTextureInfo(out GRGlTextureInfo glInfo)
    {
        if (Backend == GRBackend.OpenGL)
        {
            glInfo = _glInfo;
            return true;
        }

        glInfo = default;
        return false;
    }

    protected override void Dispose(bool disposing) => base.Dispose(disposing);

    protected override void DisposeNative()
    {
        // Backend handles are borrowed. Disposing the wrapper never destroys
        // the caller-owned GL/Vulkan/Metal/D3D/WebGPU resource.
    }
}

#nullable restore

public class GRRecordingContext : SKObject
{
    internal GRRecordingContext(WgpuContext context)
        : base(SKObjectHandle.Create(), owns: true)
    {
        BackendContext = context ?? throw new ArgumentNullException(nameof(context));
    }

    internal WgpuContext BackendContext { get; }

    public virtual GRBackend Backend => GRBackend.Dawn;

    public virtual bool IsAbandoned => IsDisposed || BackendContext.IsDisposed;

    public int MaxTextureSize => 16384;

    public int MaxRenderTargetSize => 16384;

    public int GetMaxSurfaceSampleCount(SKColorType colorType) => 1;

    protected override void DisposeNative()
    {
        // The wrapper never owns the application WgpuContext.
    }
}

#nullable disable

public class GRContext : GRRecordingContext
{
    private const long DefaultResourceCacheLimit = 256L * 1024 * 1024;
    private bool _abandoned;
    private long _resourceCacheLimit = DefaultResourceCacheLimit;

    public GRContext(WgpuContext context)
        : base(context)
    {
    }

    public WgpuContext Context => BackendContext;

    public override GRBackend Backend => base.Backend;

    public override bool IsAbandoned => _abandoned || base.IsAbandoned;

    public static GRContext CreateGl() => CreateContext();

    public static GRContext CreateGl(GRContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CreateContext();
    }

    public static GRContext CreateGl(GRGlInterface backendContext)
    {
        ArgumentNullException.ThrowIfNull(backendContext);
        return CreateContext();
    }

    public static GRContext CreateGl(GRGlInterface backendContext, GRContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(backendContext);
        ArgumentNullException.ThrowIfNull(options);
        return CreateContext();
    }

    public static GRContext CreateMetal(GRMtlBackendContext backendContext) =>
        CreateMetal(backendContext, new GRContextOptions());

    public static GRContext CreateMetal(
        GRMtlBackendContext backendContext,
        GRContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(backendContext);
        ArgumentNullException.ThrowIfNull(options);
        return CreateContext();
    }

    public static GRContext CreateVulkan(GRVkBackendContext backendContext) =>
        CreateVulkan(backendContext, new GRContextOptions());

    public static GRContext CreateVulkan(
        GRVkBackendContext backendContext,
        GRContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(backendContext);
        ArgumentNullException.ThrowIfNull(options);
        return CreateContext();
    }

    public static GRContext CreateDirect3D(GRD3DBackendContext backendContext) =>
        CreateDirect3D(backendContext, new GRContextOptions());

    public static GRContext CreateDirect3D(
        GRD3DBackendContext backendContext,
        GRContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(backendContext);
        ArgumentNullException.ThrowIfNull(options);
        return CreateContext();
    }

    public void Flush()
    {
        Flush(submit: true, synchronous: false);
    }

    public void Flush(SKImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Flush();
    }

    public void Flush(SKSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        Flush();
    }

    public void Flush(bool submit, bool synchronous = false)
    {
        ThrowIfUnavailable();
        if (submit)
        {
            Submit(synchronous);
        }
    }

    public void Submit(bool synchronous = false)
    {
        ThrowIfUnavailable();
        Context.PollDevice(wait: synchronous);
    }

    public void ResetContext(GRBackendState state = GRBackendState.All)
    {
        ThrowIfUnavailable();
    }

    public void ResetContext(GRGlBackendState state) =>
        ResetContext((GRBackendState)(uint)state);

    public void ResetContext(uint state) =>
        ResetContext((GRBackendState)state);

    public void AbandonContext(bool releaseResources = false)
    {
        if (_abandoned)
        {
            return;
        }

        if (releaseResources && !Context.IsDisposed)
        {
            Context.CleanupPendingResources();
        }

        _abandoned = true;
    }

    public long GetResourceCacheLimit() =>
        Interlocked.Read(ref _resourceCacheLimit);

    public void SetResourceCacheLimit(long maxResourceBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxResourceBytes);
        Interlocked.Exchange(ref _resourceCacheLimit, maxResourceBytes);
    }

    public void GetResourceCacheUsage(out int maxResources, out long maxResourceBytes)
    {
        if (Context.IsDisposed)
        {
            maxResources = 0;
            maxResourceBytes = 0;
            return;
        }

        maxResources = Context.CachedDeviceShaderModuleCount +
            Context.CachedDeviceBindGroupLayoutCount +
            Context.CachedDevicePipelineLayoutCount +
            Context.CachedDeviceRenderPipelineCount +
            Context.CachedDeviceComputePipelineCount;
        maxResourceBytes = 0;
    }

    public void PurgeResources()
    {
        if (!Context.IsDisposed)
        {
            Context.CleanupPendingResources();
        }
    }

    public void PurgeUnlockedResources(bool scratchResourcesOnly) =>
        PurgeResources();

    public void PurgeUnlockedResources(long bytesToPurge, bool preferScratchResources)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesToPurge);
        PurgeResources();
    }

    public void PurgeUnusedResources(long milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        PurgeResources();
    }

    public void DumpMemoryStatistics(SKTraceMemoryDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        GetResourceCacheUsage(out var resourceCount, out var resourceBytes);
        dump.OnDumpNumericValue("skia/gr_context", "resource_count", "objects", (ulong)resourceCount);
        dump.OnDumpNumericValue("skia/gr_context", "resource_bytes", "bytes", (ulong)resourceBytes);
        dump.OnDumpNumericValue("skia/gr_context", "resource_limit", "bytes", (ulong)GetResourceCacheLimit());
        dump.OnDumpStringValue("skia/gr_context", "backend", "ProGPU WebGPU");
    }

    public new int GetMaxSurfaceSampleCount(SKColorType colorType)
    {
        return base.GetMaxSurfaceSampleCount(colorType);
    }

    protected override void Dispose(bool disposing) => base.Dispose(disposing);

    protected override void DisposeNative()
    {
        _abandoned = true;
        base.DisposeNative();
    }

    private static GRContext CreateContext() =>
        new(SKContextHelper.GetContext());

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_abandoned)
        {
            throw new InvalidOperationException("The GPU context has been abandoned.");
        }
    }
}

#nullable restore
