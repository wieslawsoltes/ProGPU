using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Skia;
using Xunit;

namespace Avalonia.Skia.RenderTests;

public sealed class AvaloniaSkiaCompatibilityTests
{
    private const string ExpectedSourceHash =
        "b449fb8ed977fcafa9ebc006f0a38f9229d7f78ce4a1986ceccc8fd1cbaf2d2f";

    [Fact]
    public void AvaloniaSkiaSourcesMatchTheUnmodified1205Release()
    {
        var sourceRoot = FindRepositoryPath("src", "ProGPU.Avalonia.SkiaShim");
        var files = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"))
            .OrderBy(path => Path.GetRelativePath(sourceRoot, path), StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(54, files.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            var relativePath = Path
                .GetRelativePath(sourceRoot, file)
                .Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData(new byte[] { 0 });
            hash.AppendData(File.ReadAllBytes(file));
        }

        Assert.Equal(
            ExpectedSourceHash,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        Assert.False(File.Exists(Path.Combine(sourceRoot, "OutOfTreeAdapters.cs")));
        Assert.False(File.Exists(Path.Combine(sourceRoot, "WebGpuFramebufferTarget.cs")));
    }

    [Fact]
    public void CpuFramebufferRejectsZeroSizedSurface()
    {
        var framebuffer = new TestLockedFramebuffer(new PixelSize(0, 0));
        var platformSurface = new TestFramebufferSurface(framebuffer);
        using var target = new FramebufferRenderTarget(platformSurface);

        Assert.Throws<ArgumentException>(() => target.CreateDrawingContext(default, out _));
    }

    private static bool HasPathSegment(string path, string segment)
    {
        return path.Split(Path.DirectorySeparatorChar).Contains(segment, StringComparer.Ordinal);
    }

    private static string FindRepositoryPath(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(segments).ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository directory '{Path.Combine(segments)}'.");
    }

    private sealed class TestFramebufferSurface : IFramebufferPlatformSurface
    {
        private readonly ILockedFramebuffer _framebuffer;

        public TestFramebufferSurface(ILockedFramebuffer framebuffer)
        {
            _framebuffer = framebuffer;
        }

        public IFramebufferRenderTarget CreateFramebufferRenderTarget()
        {
            return new FuncFramebufferRenderTarget(() => _framebuffer);
        }
    }

    private sealed class TestLockedFramebuffer : ILockedFramebuffer
    {
        public TestLockedFramebuffer(PixelSize size)
        {
            Size = size;
        }

        public IntPtr Address => IntPtr.Zero;
        public PixelSize Size { get; }
        public int RowBytes => Math.Max(0, Size.Width * 4);
        public Vector Dpi => new(96, 96);
        public PixelFormat Format => PixelFormat.Bgra8888;
        public AlphaFormat AlphaFormat => AlphaFormat.Premul;
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
