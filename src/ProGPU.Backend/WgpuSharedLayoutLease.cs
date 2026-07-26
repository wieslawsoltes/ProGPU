using System;
using System.Threading;
using Silk.NET.WebGPU;

namespace ProGPU.Backend;

/// <summary>
/// Stable, source-declared identity for an immutable device resource ABI.
/// </summary>
public readonly record struct WgpuDeviceResourceKey(
    string Scope,
    string Name,
    uint Version = 1)
{
    public override string ToString() => $"{Scope}/{Name}@{Version}";
}

/// <summary>
/// A reference-counted bind-group-layout lease from one WebGPU device domain.
/// </summary>
public unsafe sealed class WgpuBindGroupLayoutLease : IDisposable
{
    private WgpuContext? _context;
    private WgpuDeviceResourceDomain? _domain;
    private nint _handle;

    internal WgpuBindGroupLayoutLease(
        WgpuContext context,
        WgpuDeviceResourceDomain domain,
        WgpuDeviceResourceKey key,
        BindGroupLayout* handle)
    {
        _context = context;
        _domain = domain;
        Key = key;
        _handle = (nint)handle;
    }

    public WgpuDeviceResourceKey Key { get; }

    public BindGroupLayout* Handle
    {
        get
        {
            nint handle = Volatile.Read(ref _handle);
            ObjectDisposedException.ThrowIf(
                handle == 0,
                this);
            return (BindGroupLayout*)handle;
        }
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        WgpuContext? context = Interlocked.Exchange(ref _context, null);
        WgpuDeviceResourceDomain? domain =
            Interlocked.Exchange(ref _domain, null);
        if (handle != 0 && context is not null && domain is not null)
        {
            domain.ReleaseBindGroupLayout(
                Key,
                (BindGroupLayout*)handle,
                context);
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A reference-counted pipeline-layout lease from one WebGPU device domain.
/// </summary>
public unsafe sealed class WgpuPipelineLayoutLease : IDisposable
{
    private WgpuContext? _context;
    private WgpuDeviceResourceDomain? _domain;
    private nint _handle;

    internal WgpuPipelineLayoutLease(
        WgpuContext context,
        WgpuDeviceResourceDomain domain,
        WgpuDeviceResourceKey key,
        PipelineLayout* handle)
    {
        _context = context;
        _domain = domain;
        Key = key;
        _handle = (nint)handle;
    }

    public WgpuDeviceResourceKey Key { get; }

    public PipelineLayout* Handle
    {
        get
        {
            nint handle = Volatile.Read(ref _handle);
            ObjectDisposedException.ThrowIf(
                handle == 0,
                this);
            return (PipelineLayout*)handle;
        }
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        WgpuContext? context = Interlocked.Exchange(ref _context, null);
        WgpuDeviceResourceDomain? domain =
            Interlocked.Exchange(ref _domain, null);
        if (handle != 0 && context is not null && domain is not null)
        {
            domain.ReleasePipelineLayout(
                Key,
                (PipelineLayout*)handle,
                context);
        }
        GC.SuppressFinalize(this);
    }
}
