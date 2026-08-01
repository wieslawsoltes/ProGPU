using System;
using ProGPU.Backend;

namespace SkiaSharp;

#nullable disable

public class GRContextOptions
{
    public bool AvoidStencilBuffers { get; set; }
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

public struct GRVkAlloc
{
    public ulong Memory { get; set; }
    public ulong Size { get; set; }
    public uint Flags { get; set; }
}

public struct GRVkImageInfo
{
    public uint CurrentQueueFamily { get; set; }
    public uint Format { get; set; }
    public ulong Image { get; set; }
    public uint ImageLayout { get; set; }
    public uint ImageTiling { get; set; }
    public uint ImageUsageFlags { get; set; }
    public uint LevelCount { get; set; }
    public uint SampleCount { get; set; }
    public bool Protected { get; set; }
    public GRVkAlloc Alloc { get; set; }
}

public delegate IntPtr GRVkGetProcDelegate(string name, IntPtr instance, IntPtr device);

public class GRVkBackendContext
{
    public IntPtr VkInstance { get; set; }
    public IntPtr VkPhysicalDevice { get; set; }
    public IntPtr VkDevice { get; set; }
    public IntPtr VkQueue { get; set; }
    public uint GraphicsQueueIndex { get; set; }
    public GRVkGetProcDelegate? GetProcedureAddress { get; set; }
}

public class GRMtlBackendContext
{
    public IntPtr DeviceHandle { get; set; }
    public IntPtr QueueHandle { get; set; }
}

public class GRGlInterface : IDisposable
{
    public static GRGlInterface Create() => new();
    public static GRGlInterface CreateOpenGl(Func<string, IntPtr> getProcAddress) => new();
    public static GRGlInterface CreateGles(Func<string, IntPtr> getProcAddress) => new();
    public void Dispose() { }
}

public class GRBackendRenderTarget : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public int SampleCount { get; }
    public int StencilBits { get; }
    public GpuTexture? BackendTexture { get; }
    
    public GRGlFramebufferInfo GlFramebufferInfo { get; }
    public GRMtlTextureInfo MtlTextureInfo { get; }
    public GRVkImageInfo VkImageInfo { get; }

    public GRBackendRenderTarget(int width, int height, GpuTexture texture)
        : this(width, height, (int)texture.SampleCount, texture)
    {
    }

    public GRBackendRenderTarget(int width, int height, int sampleCount, GpuTexture texture)
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
    }

    public GRBackendRenderTarget(int width, int height, int sampleCount, int stencilBits, GRGlFramebufferInfo glInfo)
    {
        Width = width;
        Height = height;
        SampleCount = sampleCount;
        StencilBits = stencilBits;
        GlFramebufferInfo = glInfo;
    }

    public GRBackendRenderTarget(int width, int height, int sampleCount, GRVkImageInfo vkImageInfo)
    {
        Width = width;
        Height = height;
        SampleCount = sampleCount;
        VkImageInfo = vkImageInfo;
    }

    public GRBackendRenderTarget(int width, int height, GRVkImageInfo vkImageInfo)
        : this(width, height, (int)Math.Max(1u, vkImageInfo.SampleCount), vkImageInfo)
    {
    }

    public GRBackendRenderTarget(int width, int height, GRMtlTextureInfo mtlTextureInfo)
    {
        Width = width;
        Height = height;
        SampleCount = 1;
        MtlTextureInfo = mtlTextureInfo;
    }

    public GRBackendRenderTarget(int width, int height, int sampleCount, GRMtlTextureInfo mtlTextureInfo)
    {
        Width = width;
        Height = height;
        SampleCount = sampleCount;
        MtlTextureInfo = mtlTextureInfo;
    }

    public void Dispose() { }
}

public sealed class GRBackendTexture : IDisposable
{
    public GRBackendTexture(GpuTexture texture, bool mipmapped = false)
    {
        ArgumentNullException.ThrowIfNull(texture);
        Width = checked((int)texture.Width);
        Height = checked((int)texture.Height);
        Mipmapped = mipmapped || texture.MipLevelCount > 1;
        BackendTexture = texture;
    }

    public GRBackendTexture(int width, int height, bool mipmapped, GRGlTextureInfo glTextureInfo)
    {
        Width = width;
        Height = height;
        Mipmapped = mipmapped;
        GlTextureInfo = glTextureInfo;
    }

    public int Width { get; }
    public int Height { get; }
    public bool Mipmapped { get; }
    public GRGlTextureInfo GlTextureInfo { get; }
    public GpuTexture? BackendTexture { get; }

    public void Dispose()
    {
    }
}

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

public class GRContext : GRRecordingContext
{
    public GRContext(WgpuContext context)
        : base(context)
    {
    }

    public WgpuContext Context => BackendContext;

    public override GRBackend Backend => base.Backend;

    public override bool IsAbandoned => base.IsAbandoned;

    public static GRContext CreateGl(object? interfaceObj = null, GRContextOptions? options = null)
    {
        return new GRContext(SKContextHelper.GetContext());
    }

    public static GRContext CreateMetal(object? backendContext, GRContextOptions? options = null)
    {
        return new GRContext(SKContextHelper.GetContext());
    }

    public static GRContext CreateVulkan(object? backendContext, GRContextOptions? options = null)
    {
        return new GRContext(SKContextHelper.GetContext());
    }

    public void Flush(bool submit = true, bool finish = false)
    {
        Context.WaitIdle();
    }

    public void ResetContext(uint flags = 0)
    {
        // No-op
    }

    public void AbandonContext()
    {
        // No-op
    }

    public void AbandonContext(bool releaseResources)
    {
        AbandonContext();
    }

    public void SetResourceCacheLimit(long maxResourceBytes)
    {
        // No-op
    }

    public new int GetMaxSurfaceSampleCount(SKColorType colorType)
    {
        return base.GetMaxSurfaceSampleCount(colorType);
    }
}
