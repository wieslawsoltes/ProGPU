using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Media3D;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Scene.Extensions;
using ProGPU.Tests.Headless;
using Silk.NET.WebGPU;
using Xunit;

namespace ProGPU.Tests;

public sealed class Mesh3DTextureMaterialTests
{
    [Fact]
    public void CropUsesScalarMaterialOutsideTheDiffuseImageDomain()
    {
        using var window = new HeadlessWindow(160, 90);
        var texture = new GpuTexture(
            window.Context,
            2,
            2,
            TextureFormat.Rgba8Unorm,
            TextureUsage.TextureBinding | TextureUsage.CopyDst |
                TextureUsage.CopySrc,
            "Mesh3D crop material test");
        texture.WritePixels(
        new byte[]
        {
            255, 0, 0, 255,
            255, 0, 0, 255,
            255, 0, 0, 255,
            255, 0, 0, 255,
        });
        Assert.All(texture.ReadPixels().Chunk(4), pixel =>
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, pixel));
        using var owner = new SharedGpuTextureSource(texture);
        var source = new CountingTextureSource(owner);
        using var material = new ProGpuTextureMaterial
        {
            TextureSource = source,
            SamplingMode = TextureSamplingMode.Nearest,
            TilingMode = MeshTextureTilingMode.Crop,
            Color = Vector4.One,
            AmbientColor = Vector3.One,
            SelfIllumination = 1.0f,
        };
        var mesh = new MeshGeometry3D
        {
            Positions =
            [
                new Vector3(-1.5f, -0.8f, 0.0f),
                new Vector3(1.5f, -0.8f, 0.0f),
                new Vector3(1.5f, 0.8f, 0.0f),
                new Vector3(-1.5f, 0.8f, 0.0f),
            ],
            Normals =
            [
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                -Vector3.UnitZ,
                -Vector3.UnitZ,
            ],
            TextureCoordinates =
            [
                new Vector2(-0.5f, 1.5f),
                new Vector2(1.5f, 1.5f),
                new Vector2(1.5f, -0.5f),
                new Vector2(-0.5f, -0.5f),
            ],
            TriangleIndices = [0, 1, 2, 0, 2, 3],
        };
        var viewport = new Viewport3D
        {
            Camera = new OrthographicCamera { Width = 4.0f },
            ShadingMode = ShadingMode3D.Flat,
        };
        viewport.Children.Add(new ModelVisual3D
        {
            Content = new GeometryModel3D
            {
                Geometry = mesh,
                Material = material,
                BackMaterial = material,
            },
        });
        window.Content = viewport;

        try
        {
            window.Render();
            byte[] pixels = window.ReadPixels();
            int red = 0;
            int white = 0;
            int nonBlack = 0;
            byte maximumRed = 0;
            byte maximumGreen = 0;
            byte maximumBlue = 0;
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                byte r = pixels[offset];
                byte g = pixels[offset + 1];
                byte b = pixels[offset + 2];
                byte a = pixels[offset + 3];
                maximumRed = Math.Max(maximumRed, r);
                maximumGreen = Math.Max(maximumGreen, g);
                maximumBlue = Math.Max(maximumBlue, b);
                if (r != 0 || g != 0 || b != 0)
                {
                    nonBlack++;
                }
                if (r >= 220 && g <= 30 && b <= 30 && a == 255)
                {
                    red++;
                }
                if (r >= 220 && g >= 220 && b >= 220 && a == 255)
                {
                    white++;
                }
            }

            int centerOffset = ((45 * 160) + 80) * 4;
            string center =
                $"({pixels[centerOffset]}, {pixels[centerOffset + 1]}, " +
                $"{pixels[centerOffset + 2]}, {pixels[centerOffset + 3]})";

            Assert.True(red >= 500,
                $"Expected a red mapped center, found {red} pixels; " +
                $"non-black={nonBlack}, max=({maximumRed}, {maximumGreen}, {maximumBlue}), " +
                $"center={center}, acquisitions={source.AcquireCount}.");
            Assert.True(white >= 500, $"Expected scalar crop margins, found {white} pixels.");
        }
        finally
        {
            window.Content = null;
        }
    }

    [Fact]
    public void DisposeUnsubscribesFromInvalidatingTextureSource()
    {
        var source = new CountingInvalidatingSource();
        var material = new ProGpuTextureMaterial { TextureSource = source };

        Assert.Equal(1, source.SubscriberCount);

        material.Dispose();
        material.Dispose();

        Assert.Equal(0, source.SubscriberCount);
        Assert.Throws<ObjectDisposedException>(() => material.TextureSource = source);
    }

    private sealed class CountingInvalidatingSource :
        IProGpuInvalidatingTextureSource
    {
        private EventHandler? _textureChanged;

        public int SubscriberCount { get; private set; }

        public event EventHandler? TextureChanged
        {
            add
            {
                _textureChanged += value;
                SubscriberCount++;
            }
            remove
            {
                _textureChanged -= value;
                SubscriberCount--;
            }
        }

        public bool TryGetGpuTexture(out GpuTexture texture)
        {
            texture = null!;
            return false;
        }

        public bool TryAcquireGpuTextureLease(out IProGpuTextureLease lease)
        {
            lease = null!;
            return false;
        }
    }

    private sealed class CountingTextureSource(
        SharedGpuTextureSource source) : IProGpuTextureLeaseSource
    {
        public int AcquireCount { get; private set; }

        public bool TryGetGpuTexture(out GpuTexture texture) =>
            source.TryGetGpuTexture(out texture);

        public bool TryAcquireGpuTextureLease(out IProGpuTextureLease lease)
        {
            AcquireCount++;
            return source.TryAcquireGpuTextureLease(out lease);
        }
    }
}
