using ProGPU.Backend;
using Silk.NET.WebGPU;
using StbImageSharp;

namespace ProGPU.CAD;

/// <summary>Immutable resolution request for one snapshot IMAGEDEF.</summary>
public readonly record struct CadRasterImageRequest(
    string? DocumentSourceName,
    CadRasterImageResource Resource);

/// <summary>
/// Resolves immutable CAD image identity to a typed ProGPU texture lease source.
/// Implementations must not perform file I/O or network I/O during retained replay.
/// </summary>
public interface ICadRasterImageSourceResolver
{
    bool TryResolve(
        in CadRasterImageRequest request,
        out IProGpuTextureLeaseSource source);
}

public sealed class CadEncodedRasterImageOptions
{
    public const int DefaultMaxEncodedBytes = 64 * 1024 * 1024;
    public const long DefaultMaxDecodedPixels = 64L * 1024 * 1024;
    public const int DefaultMaxDeviceTextures = 4;

    public int MaxEncodedBytes { get; init; } = DefaultMaxEncodedBytes;
    public long MaxDecodedPixels { get; init; } = DefaultMaxDecodedPixels;
    public int MaxDeviceTextures { get; init; } = DefaultMaxDeviceTextures;
}

/// <summary>
/// Immutable encoded raster payload with bounded one-time CPU decode and lazy,
/// per-device GPU upload. Stable retained replay only borrows an existing lease.
/// </summary>
/// <remarks>
/// Decode is O(B + P), upload is O(P), and retained lookup is O(1) average for B
/// encoded bytes and P decoded pixels. CPU storage is bounded by configured B/P;
/// GPU storage is bounded by P times the configured device-domain count.
/// </remarks>
public sealed class CadEncodedRasterImageSource :
    IProGpuContextTextureLeaseSource,
    IDisposable
{
    private readonly object _gate = new();
    private readonly byte[] _encoded;
    private readonly long _maxDecodedPixels;
    private readonly int _maxDeviceTextures;
    private readonly Dictionary<WgpuContext, SharedGpuTextureSource> _textures = new();
    private byte[]? _rgba;
    private int _width;
    private int _height;
    private bool _decodeAttempted;
    private bool _disposed;

    public CadEncodedRasterImageSource(
        ReadOnlySpan<byte> encoded,
        CadEncodedRasterImageOptions? options = null)
    {
        options ??= new CadEncodedRasterImageOptions();
        ValidateOptions(options);
        if (encoded.IsEmpty || encoded.Length > options.MaxEncodedBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encoded),
                $"Encoded image bytes must be between 1 and {options.MaxEncodedBytes} bytes.");
        }
        _encoded = encoded.ToArray();
        _maxDecodedPixels = options.MaxDecodedPixels;
        _maxDeviceTextures = options.MaxDeviceTextures;
    }

    public bool TryGetGpuTexture(out GpuTexture texture)
    {
        lock (_gate)
        {
            if (_disposed || _textures.Count != 1)
            {
                texture = null!;
                return false;
            }
            return _textures.Values.First().TryGetGpuTexture(out texture);
        }
    }

    public bool TryAcquireGpuTextureLease(out IProGpuTextureLease lease)
    {
        lock (_gate)
        {
            if (_disposed || _textures.Count != 1)
            {
                lease = null!;
                return false;
            }
            return _textures.Values.First().TryAcquireGpuTextureLease(out lease);
        }
    }

    public bool TryGetGpuTexture(
        WgpuContext requiredContext,
        out GpuTexture texture)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);
        lock (_gate)
        {
            return TryGetOrCreateTexture(requiredContext, out _, out texture);
        }
    }

    public bool TryAcquireGpuTextureLease(
        WgpuContext requiredContext,
        out IProGpuTextureLease lease)
    {
        ArgumentNullException.ThrowIfNull(requiredContext);
        lock (_gate)
        {
            if (!TryGetOrCreateTexture(requiredContext, out var source, out _))
            {
                lease = null!;
                return false;
            }
            return source.TryAcquireGpuTextureLease(out lease);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (SharedGpuTextureSource source in _textures.Values)
            {
                source.Dispose();
            }
            _textures.Clear();
            _rgba = null;
        }
    }

    private bool TryGetOrCreateTexture(
        WgpuContext context,
        out SharedGpuTextureSource source,
        out GpuTexture texture)
    {
        if (_disposed)
        {
            source = null!;
            texture = null!;
            return false;
        }
        if (_textures.TryGetValue(context, out source!))
        {
            if (source.TryGetGpuTexture(out texture) && !texture.IsDisposed)
            {
                return true;
            }
            source.Dispose();
            _textures.Remove(context);
        }
        if (_textures.Count >= _maxDeviceTextures || !TryDecode())
        {
            source = null!;
            texture = null!;
            return false;
        }

        texture = new GpuTexture(
            context,
            checked((uint)_width),
            checked((uint)_height),
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst,
            "CAD raster IMAGE",
            alphaMode: GpuTextureAlphaMode.Straight);
        try
        {
            texture.WritePixels<byte>(_rgba!);
            source = new SharedGpuTextureSource(texture);
            _textures.Add(context, source);
            return true;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    private bool TryDecode()
    {
        if (_decodeAttempted)
        {
            return _rgba is not null;
        }
        _decodeAttempted = true;
        try
        {
            using var metadataStream = new MemoryStream(_encoded, writable: false);
            ImageInfo? info = ImageInfo.FromStream(metadataStream);
            if (info is not ImageInfo metadata || metadata.Width <= 0 || metadata.Height <= 0)
            {
                return false;
            }
            long pixels = checked((long)metadata.Width * metadata.Height);
            if (pixels > _maxDecodedPixels)
            {
                return false;
            }
            ImageResult decoded = ImageResult.FromMemory(
                _encoded,
                ColorComponents.RedGreenBlueAlpha);
            if (decoded.Width != metadata.Width || decoded.Height != metadata.Height ||
                decoded.Data.LongLength != checked(pixels * 4L))
            {
                return false;
            }
            _rgba = decoded.Data;
            _width = decoded.Width;
            _height = decoded.Height;
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or ArgumentException or
                OverflowException or OutOfMemoryException)
        {
            _rgba = null;
            return false;
        }
    }

    private static void ValidateOptions(CadEncodedRasterImageOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxEncodedBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDecodedPixels, 1L);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDeviceTextures, 1);
    }
}

/// <summary>
/// Bounded host-owned IMAGEDEF-to-source registry shared by desktop and browser
/// hosts. Registration may perform I/O elsewhere; resolution itself is O(1)
/// average and never reads a path.
/// </summary>
public sealed class CadRasterImageCatalog :
    ICadRasterImageSourceResolver,
    IDisposable
{
    public const int DefaultMaxSources = 65_536;
    private readonly object _gate = new();
    private readonly int _maxSources;
    private readonly Dictionary<ulong, IProGpuTextureLeaseSource> _byHandle = new();
    private readonly Dictionary<string, IProGpuTextureLeaseSource> _byPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<IDisposable> _owned = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;

    public CadRasterImageCatalog(int maxSources = DefaultMaxSources)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSources, 1);
        _maxSources = maxSources;
    }

    public void RegisterSource(
        string fileName,
        IProGpuTextureLeaseSource source,
        ulong definitionHandle = 0,
        bool takeOwnership = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var uniqueSources = new HashSet<IProGpuTextureLeaseSource>(
                _byPath.Values,
                ReferenceEqualityComparer.Instance)
            {
                source,
            };
            if (uniqueSources.Count > _maxSources)
            {
                throw new InvalidOperationException(
                    $"Raster image catalog exceeds its {_maxSources}-source limit.");
            }
            _byPath[fileName] = source;
            if (definitionHandle != 0)
            {
                _byHandle[definitionHandle] = source;
            }
            if (takeOwnership && source is IDisposable disposable)
            {
                _owned.Add(disposable);
            }
        }
    }

    public CadEncodedRasterImageSource RegisterEncoded(
        string fileName,
        ReadOnlySpan<byte> encoded,
        ulong definitionHandle = 0,
        CadEncodedRasterImageOptions? options = null)
    {
        var source = new CadEncodedRasterImageSource(encoded, options);
        try
        {
            RegisterSource(fileName, source, definitionHandle, takeOwnership: true);
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public bool TryResolve(
        in CadRasterImageRequest request,
        out IProGpuTextureLeaseSource source)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                source = null!;
                return false;
            }
            if (request.Resource.DefinitionHandle != 0 &&
                _byHandle.TryGetValue(request.Resource.DefinitionHandle, out source!))
            {
                return true;
            }
            if (_byPath.TryGetValue(request.Resource.FileName, out source!))
            {
                return true;
            }
            if (TryResolveDocumentRelativePath(request, out string? resolved) &&
                resolved is not null &&
                _byPath.TryGetValue(resolved, out source!))
            {
                return true;
            }
            source = null!;
            return false;
        }
    }

    public ICadRasterImageSourceResolver CreateResolverSnapshot()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new ResolverSnapshot(
                new Dictionary<ulong, IProGpuTextureLeaseSource>(_byHandle),
                new Dictionary<string, IProGpuTextureLeaseSource>(
                    _byPath,
                    StringComparer.OrdinalIgnoreCase));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (IDisposable owned in _owned)
            {
                owned.Dispose();
            }
            _owned.Clear();
            _byHandle.Clear();
            _byPath.Clear();
        }
    }

    private static bool TryResolveDocumentRelativePath(
        in CadRasterImageRequest request,
        out string? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(request.DocumentSourceName) ||
            string.IsNullOrWhiteSpace(request.Resource.FileName))
        {
            return false;
        }
        try
        {
            char separator = Path.DirectorySeparatorChar;
            string documentPath = request.DocumentSourceName
                .Replace('\\', separator)
                .Replace('/', separator);
            string resourcePath = request.Resource.FileName
                .Replace('\\', separator)
                .Replace('/', separator);
            string? directory = Path.GetDirectoryName(documentPath);
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }
            resolved = Path.GetFullPath(resourcePath, directory);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private sealed class ResolverSnapshot(
        Dictionary<ulong, IProGpuTextureLeaseSource> byHandle,
        Dictionary<string, IProGpuTextureLeaseSource> byPath) :
        ICadRasterImageSourceResolver
    {
        public bool TryResolve(
            in CadRasterImageRequest request,
            out IProGpuTextureLeaseSource source)
        {
            if (request.Resource.DefinitionHandle != 0 &&
                byHandle.TryGetValue(request.Resource.DefinitionHandle, out source!))
            {
                return true;
            }
            if (byPath.TryGetValue(request.Resource.FileName, out source!))
            {
                return true;
            }
            if (TryResolveDocumentRelativePath(request, out string? resolved) &&
                resolved is not null &&
                byPath.TryGetValue(resolved, out source!))
            {
                return true;
            }
            source = null!;
            return false;
        }
    }
}
