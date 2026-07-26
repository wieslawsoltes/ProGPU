using System;
using System.Collections.Generic;
using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>
/// Owns immutable WebGPU resources whose compatibility boundary is the native
/// <see cref="Device"/>, rather than a presentation surface.
/// </summary>
/// <remarks>
/// A domain is shared by every <see cref="WgpuContext"/> that uses the same
/// device lifetime. Mutable render targets, command state, atlases, and retained
/// scenes remain context-local. Shader lookup is O(1) average and O(S) only when
/// hashing or comparing a previously unseen source of length S. Residency is
/// O(U) for U unique live shader sources.
/// </remarks>
internal unsafe sealed class WgpuDeviceResourceDomain : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<ShaderModuleKey, ShaderModuleEntry> _shaderModules = new();
    private readonly Dictionary<WgpuDeviceResourceKey, BindGroupLayoutEntry> _bindGroupLayouts = new();
    private readonly Dictionary<WgpuDeviceResourceKey, PipelineLayoutEntry> _pipelineLayouts = new();
    private readonly Dictionary<WgpuRenderPipelineResourceKey, PipelineEntry> _renderPipelines = new();
    private readonly Dictionary<WgpuComputePipelineResourceKey, PipelineEntry> _computePipelines = new();
    private IWebGpuApi? _api;
    private Device* _device;
    private bool _isDisposed;

    public WgpuDeviceResourceDomain(IWebGpuApi api, Device* device)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (device == null)
        {
            throw new ArgumentException(
                "A valid WebGPU device is required.",
                nameof(device));
        }

        _api = api;
        _device = device;
    }

    public int ShaderModuleCount
    {
        get
        {
            lock (_sync)
            {
                return _shaderModules.Count;
            }
        }
    }

    public int BindGroupLayoutCount
    {
        get
        {
            lock (_sync)
            {
                return _bindGroupLayouts.Count;
            }
        }
    }

    public int PipelineLayoutCount
    {
        get
        {
            lock (_sync)
            {
                return _pipelineLayouts.Count;
            }
        }
    }

    public int RenderPipelineCount
    {
        get
        {
            lock (_sync)
            {
                return _renderPipelines.Count;
            }
        }
    }

    public int ComputePipelineCount
    {
        get
        {
            lock (_sync)
            {
                return _computePipelines.Count;
            }
        }
    }

    public bool TryAcquireRenderPipeline(
        WgpuRenderPipelineResourceKey key,
        out RenderPipeline* pipeline)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_renderPipelines.TryGetValue(
                    key,
                    out PipelineEntry? cached))
            {
                checked
                {
                    cached.ReferenceCount++;
                }
                pipeline = (RenderPipeline*)cached.Handle;
                return true;
            }
        }

        pipeline = null;
        return false;
    }

    public RenderPipeline* PublishRenderPipeline(
        WgpuRenderPipelineResourceKey key,
        RenderPipeline* createdPipeline)
    {
        if (createdPipeline == null)
        {
            throw new ArgumentNullException(nameof(createdPipeline));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_renderPipelines.TryGetValue(
                    key,
                    out PipelineEntry? cached))
            {
                checked
                {
                    cached.ReferenceCount++;
                }
                _api?.RenderPipelineRelease(createdPipeline);
                return (RenderPipeline*)cached.Handle;
            }

            _renderPipelines.Add(
                key,
                new PipelineEntry((nint)createdPipeline));
            return createdPipeline;
        }
    }

    public bool TryAcquireComputePipeline(
        WgpuComputePipelineResourceKey key,
        out ComputePipeline* pipeline)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_computePipelines.TryGetValue(
                    key,
                    out PipelineEntry? cached))
            {
                checked
                {
                    cached.ReferenceCount++;
                }
                pipeline = (ComputePipeline*)cached.Handle;
                return true;
            }
        }

        pipeline = null;
        return false;
    }

    public ComputePipeline* PublishComputePipeline(
        WgpuComputePipelineResourceKey key,
        ComputePipeline* createdPipeline)
    {
        if (createdPipeline == null)
        {
            throw new ArgumentNullException(nameof(createdPipeline));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_computePipelines.TryGetValue(
                    key,
                    out PipelineEntry? cached))
            {
                checked
                {
                    cached.ReferenceCount++;
                }
                _api?.ComputePipelineRelease(createdPipeline);
                return (ComputePipeline*)cached.Handle;
            }

            _computePipelines.Add(
                key,
                new PipelineEntry((nint)createdPipeline));
            return createdPipeline;
        }
    }

    public BindGroupLayout* AcquireBindGroupLayout(
        WgpuDeviceResourceKey key,
        BindGroupLayoutDescriptor* descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Name);
        if (descriptor == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        BindGroupLayoutSignature signature =
            BindGroupLayoutSignature.Create(descriptor);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_bindGroupLayouts.TryGetValue(
                    key,
                    out BindGroupLayoutEntry? cached))
            {
                if (!cached.Signature.Equals(signature))
                {
                    throw new InvalidOperationException(
                        $"Device bind-group-layout key '{key}' was reused with a different ABI.");
                }

                checked
                {
                    cached.ReferenceCount++;
                }
                return (BindGroupLayout*)cached.Handle;
            }

            IWebGpuApi api = _api ??
                throw new ObjectDisposedException(
                    nameof(WgpuDeviceResourceDomain));
            BindGroupLayout* layout = api.DeviceCreateBindGroupLayout(
                _device,
                descriptor);
            if (layout == null)
            {
                throw new InvalidOperationException(
                    $"Failed to create shared bind-group layout '{key}'.");
            }

            _bindGroupLayouts.Add(
                key,
                new BindGroupLayoutEntry(
                    (nint)layout,
                    signature));
            return layout;
        }
    }

    public PipelineLayout* AcquirePipelineLayout(
        WgpuDeviceResourceKey key,
        PipelineLayoutDescriptor* descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.Name);
        if (descriptor == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        PipelineLayoutSignature signature =
            PipelineLayoutSignature.Create(descriptor);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_pipelineLayouts.TryGetValue(
                    key,
                    out PipelineLayoutEntry? cached))
            {
                if (!cached.Signature.Equals(signature))
                {
                    throw new InvalidOperationException(
                        $"Device pipeline-layout key '{key}' was reused with a different ABI.");
                }

                checked
                {
                    cached.ReferenceCount++;
                }
                return (PipelineLayout*)cached.Handle;
            }

            IWebGpuApi api = _api ??
                throw new ObjectDisposedException(
                    nameof(WgpuDeviceResourceDomain));
            PipelineLayout* layout = api.DeviceCreatePipelineLayout(
                _device,
                descriptor);
            if (layout == null)
            {
                throw new InvalidOperationException(
                    $"Failed to create shared pipeline layout '{key}'.");
            }

            _pipelineLayouts.Add(
                key,
                new PipelineLayoutEntry(
                    (nint)layout,
                    signature));
            return layout;
        }
    }

    public ShaderModule* AcquireShaderModule(
        string logicalKey,
        string wgslCode,
        string label,
        out ShaderModuleKey cacheKey)
    {
        ArgumentNullException.ThrowIfNull(logicalKey);
        ArgumentNullException.ThrowIfNull(wgslCode);
        ArgumentNullException.ThrowIfNull(label);

        cacheKey = new ShaderModuleKey(logicalKey, wgslCode);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_shaderModules.TryGetValue(cacheKey, out ShaderModuleEntry? cached))
            {
                checked
                {
                    cached.ReferenceCount++;
                }
                return (ShaderModule*)cached.Handle;
            }

            ShaderModule* module = CreateShaderModule(wgslCode, label);
            _shaderModules.Add(
                cacheKey,
                new ShaderModuleEntry((nint)module));
            return module;
        }
    }

    public void ReleaseShaderModule(
        ShaderModuleKey cacheKey,
        ShaderModule* expectedModule,
        WgpuContext releaseContext)
    {
        ArgumentNullException.ThrowIfNull(releaseContext);

        lock (_sync)
        {
            if (_isDisposed ||
                !_shaderModules.TryGetValue(
                    cacheKey,
                    out ShaderModuleEntry? cached))
            {
                return;
            }

            if (cached.Handle != (nint)expectedModule)
            {
                throw new InvalidOperationException(
                    "The shader module does not belong to this device cache entry.");
            }

            if (--cached.ReferenceCount != 0)
            {
                return;
            }

            _shaderModules.Remove(cacheKey);
            if (!releaseContext.IsDisposed)
            {
                // The context drains render/compute pipelines before shader
                // modules, preserving deterministic native release order.
                releaseContext.QueueShaderModuleDisposal(cached.Handle);
            }
            else
            {
                _api?.ShaderModuleRelease((ShaderModule*)cached.Handle);
            }
        }
    }

    public void ReleaseBindGroupLayout(
        WgpuDeviceResourceKey key,
        BindGroupLayout* expectedLayout,
        WgpuContext releaseContext)
    {
        ArgumentNullException.ThrowIfNull(releaseContext);
        lock (_sync)
        {
            if (_isDisposed ||
                !_bindGroupLayouts.TryGetValue(
                    key,
                    out BindGroupLayoutEntry? cached))
            {
                return;
            }

            if (cached.Handle != (nint)expectedLayout)
            {
                throw new InvalidOperationException(
                    "The bind-group layout does not belong to this device cache entry.");
            }

            if (--cached.ReferenceCount != 0)
            {
                return;
            }

            _bindGroupLayouts.Remove(key);
            if (!releaseContext.IsDisposed)
            {
                releaseContext.QueueBindGroupLayoutDisposal(cached.Handle);
            }
            else
            {
                _api?.BindGroupLayoutRelease(
                    (BindGroupLayout*)cached.Handle);
            }
        }
    }

    public void ReleasePipelineLayout(
        WgpuDeviceResourceKey key,
        PipelineLayout* expectedLayout,
        WgpuContext releaseContext)
    {
        ArgumentNullException.ThrowIfNull(releaseContext);
        lock (_sync)
        {
            if (_isDisposed ||
                !_pipelineLayouts.TryGetValue(
                    key,
                    out PipelineLayoutEntry? cached))
            {
                return;
            }

            if (cached.Handle != (nint)expectedLayout)
            {
                throw new InvalidOperationException(
                    "The pipeline layout does not belong to this device cache entry.");
            }

            if (--cached.ReferenceCount != 0)
            {
                return;
            }

            _pipelineLayouts.Remove(key);
            if (!releaseContext.IsDisposed)
            {
                releaseContext.QueuePipelineLayoutDisposal(cached.Handle);
            }
            else
            {
                _api?.PipelineLayoutRelease(
                    (PipelineLayout*)cached.Handle);
            }
        }
    }

    public void ReleaseRenderPipeline(
        WgpuRenderPipelineResourceKey key,
        RenderPipeline* expectedPipeline,
        WgpuContext releaseContext)
    {
        ArgumentNullException.ThrowIfNull(releaseContext);
        lock (_sync)
        {
            if (_isDisposed ||
                !_renderPipelines.TryGetValue(
                    key,
                    out PipelineEntry? cached))
            {
                return;
            }

            if (cached.Handle != (nint)expectedPipeline)
            {
                throw new InvalidOperationException(
                    "The render pipeline does not belong to this device cache entry.");
            }
            if (--cached.ReferenceCount != 0)
            {
                return;
            }

            _renderPipelines.Remove(key);
            if (!releaseContext.IsDisposed)
            {
                releaseContext.QueueRenderPipelineDisposal(
                    cached.Handle);
            }
            else
            {
                _api?.RenderPipelineRelease(
                    (RenderPipeline*)cached.Handle);
            }
        }
    }

    public void ReleaseComputePipeline(
        WgpuComputePipelineResourceKey key,
        ComputePipeline* expectedPipeline,
        WgpuContext releaseContext)
    {
        ArgumentNullException.ThrowIfNull(releaseContext);
        lock (_sync)
        {
            if (_isDisposed ||
                !_computePipelines.TryGetValue(
                    key,
                    out PipelineEntry? cached))
            {
                return;
            }

            if (cached.Handle != (nint)expectedPipeline)
            {
                throw new InvalidOperationException(
                    "The compute pipeline does not belong to this device cache entry.");
            }
            if (--cached.ReferenceCount != 0)
            {
                return;
            }

            _computePipelines.Remove(key);
            if (!releaseContext.IsDisposed)
            {
                releaseContext.QueueComputePipelineDisposal(
                    cached.Handle);
            }
            else
            {
                _api?.ComputePipelineRelease(
                    (ComputePipeline*)cached.Handle);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_isDisposed)
            {
                return;
            }

            IWebGpuApi? api = _api;
            if (api is not null)
            {
                foreach (PipelineEntry renderPipeline in _renderPipelines.Values)
                {
                    api.RenderPipelineRelease(
                        (RenderPipeline*)renderPipeline.Handle);
                }
                foreach (PipelineEntry computePipeline in _computePipelines.Values)
                {
                    api.ComputePipelineRelease(
                        (ComputePipeline*)computePipeline.Handle);
                }
                foreach (PipelineLayoutEntry pipelineLayout in _pipelineLayouts.Values)
                {
                    api.PipelineLayoutRelease(
                        (PipelineLayout*)pipelineLayout.Handle);
                }
                foreach (BindGroupLayoutEntry bindGroupLayout in _bindGroupLayouts.Values)
                {
                    api.BindGroupLayoutRelease(
                        (BindGroupLayout*)bindGroupLayout.Handle);
                }
                foreach (ShaderModuleEntry shaderModule in _shaderModules.Values)
                {
                    api.ShaderModuleRelease((ShaderModule*)shaderModule.Handle);
                }
            }

            _renderPipelines.Clear();
            _computePipelines.Clear();
            _pipelineLayouts.Clear();
            _bindGroupLayouts.Clear();
            _shaderModules.Clear();
            _api = null;
            _device = null;
            _isDisposed = true;
        }
    }

    private ShaderModule* CreateShaderModule(string wgslCode, string label)
    {
        IWebGpuApi api = _api ??
            throw new ObjectDisposedException(nameof(WgpuDeviceResourceDomain));

        nint codePtr = Silk.NET.Core.Native.SilkMarshal.StringToPtr(wgslCode);
        nint labelPtr = Silk.NET.Core.Native.SilkMarshal.StringToPtr(label);
        try
        {
            var wgslDescriptor = new ShaderModuleWGSLDescriptor
            {
                Chain = new ChainedStruct
                {
                    Next = null,
                    SType = SType.ShaderModuleWgslDescriptor
                },
                Code = (byte*)codePtr
            };
            var descriptor = new ShaderModuleDescriptor
            {
                NextInChain = (ChainedStruct*)&wgslDescriptor,
                Label = (byte*)labelPtr
            };

            ShaderModule* module = api.DeviceCreateShaderModule(
                _device,
                &descriptor);
            if (module == null)
            {
                throw new InvalidOperationException(
                    $"Failed to compile WGSL shader '{label}'.");
            }

            return module;
        }
        finally
        {
            Silk.NET.Core.Native.SilkMarshal.Free(codePtr);
            Silk.NET.Core.Native.SilkMarshal.Free(labelPtr);
        }
    }

    internal readonly record struct ShaderModuleKey(
        string LogicalKey,
        string WgslCode);

    private sealed class ShaderModuleEntry(nint handle)
    {
        public nint Handle { get; } = handle;
        public int ReferenceCount { get; set; } = 1;
    }

    private sealed class BindGroupLayoutEntry(
        nint handle,
        BindGroupLayoutSignature signature)
    {
        public nint Handle { get; } = handle;
        public BindGroupLayoutSignature Signature { get; } = signature;
        public int ReferenceCount { get; set; } = 1;
    }

    private sealed class PipelineLayoutEntry(
        nint handle,
        PipelineLayoutSignature signature)
    {
        public nint Handle { get; } = handle;
        public PipelineLayoutSignature Signature { get; } = signature;
        public int ReferenceCount { get; set; } = 1;
    }

    private sealed class PipelineEntry(nint handle)
    {
        public nint Handle { get; } = handle;
        public int ReferenceCount { get; set; } = 1;
    }

    private sealed class BindGroupLayoutSignature(
        BindGroupLayoutEntrySignature[] entries) : IEquatable<BindGroupLayoutSignature>
    {
        private readonly BindGroupLayoutEntrySignature[] _entries = entries;

        public static BindGroupLayoutSignature Create(
            BindGroupLayoutDescriptor* descriptor)
        {
            if (descriptor->NextInChain != null)
            {
                throw new NotSupportedException(
                    "Chained bind-group-layout descriptors cannot be shared.");
            }

            int count = checked((int)descriptor->EntryCount);
            var entries = new BindGroupLayoutEntrySignature[count];
            for (var index = 0; index < count; index++)
            {
                entries[index] = BindGroupLayoutEntrySignature.Create(
                    descriptor->Entries[index]);
            }
            return new BindGroupLayoutSignature(entries);
        }

        public bool Equals(BindGroupLayoutSignature? other)
            => other is not null &&
               _entries.AsSpan().SequenceEqual(other._entries);

        public override bool Equals(object? obj)
            => obj is BindGroupLayoutSignature other &&
               Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (BindGroupLayoutEntrySignature entry in _entries)
            {
                hash.Add(entry);
            }
            return hash.ToHashCode();
        }
    }

    private readonly record struct BindGroupLayoutEntrySignature(
        uint Binding,
        ShaderStage Visibility,
        BufferBindingType BufferType,
        bool BufferHasDynamicOffset,
        ulong BufferMinBindingSize,
        SamplerBindingType SamplerType,
        TextureSampleType TextureSampleType,
        TextureViewDimension TextureViewDimension,
        bool TextureMultisampled,
        StorageTextureAccess StorageTextureAccess,
        TextureFormat StorageTextureFormat,
        TextureViewDimension StorageTextureViewDimension)
    {
        public static BindGroupLayoutEntrySignature Create(
            Silk.NET.WebGPU.BindGroupLayoutEntry entry)
        {
            if (entry.NextInChain != null)
            {
                throw new NotSupportedException(
                    "Chained bind-group-layout entries cannot be shared.");
            }

            return new BindGroupLayoutEntrySignature(
                entry.Binding,
                entry.Visibility,
                entry.Buffer.Type,
                entry.Buffer.HasDynamicOffset,
                entry.Buffer.MinBindingSize,
                entry.Sampler.Type,
                entry.Texture.SampleType,
                entry.Texture.ViewDimension,
                entry.Texture.Multisampled,
                entry.StorageTexture.Access,
                entry.StorageTexture.Format,
                entry.StorageTexture.ViewDimension);
        }
    }

    private sealed class PipelineLayoutSignature(
        nint[] bindGroupLayouts) : IEquatable<PipelineLayoutSignature>
    {
        private readonly nint[] _bindGroupLayouts = bindGroupLayouts;

        public static PipelineLayoutSignature Create(
            PipelineLayoutDescriptor* descriptor)
        {
            if (descriptor->NextInChain != null)
            {
                throw new NotSupportedException(
                    "Chained pipeline-layout descriptors cannot be shared.");
            }

            int count = checked((int)descriptor->BindGroupLayoutCount);
            var layouts = new nint[count];
            for (var index = 0; index < count; index++)
            {
                layouts[index] =
                    (nint)descriptor->BindGroupLayouts[index];
            }
            return new PipelineLayoutSignature(layouts);
        }

        public bool Equals(PipelineLayoutSignature? other)
            => other is not null &&
               _bindGroupLayouts.AsSpan().SequenceEqual(
                   other._bindGroupLayouts);

        public override bool Equals(object? obj)
            => obj is PipelineLayoutSignature other &&
               Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (nint layout in _bindGroupLayouts)
            {
                hash.Add(layout);
            }
            return hash.ToHashCode();
        }
    }
}
