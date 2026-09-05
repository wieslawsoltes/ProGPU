using System.Buffers.Binary;
using System.Numerics;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Tables;
using CSMath;
using ProGPU.Backend;
using ProGPU.Backend.Native;
using ProGPU.CAD.Native;
using Xunit;

namespace ProGPU.CAD.Tests;

public sealed class CadMesh3DMaterialTests
{
    [Fact]
    public void ExplicitMaterialRetainsScalarsTextureIdentityAndAuthoredUvTransform()
    {
        var document = new CadDocument();
        Material material = CreateMaterial("Brass", "maps/brass.png");
        var transform = Matrix4.Identity;
        transform.M00 = 2.0;
        transform.M11 = 3.0;
        transform.M30 = 0.25;
        transform.M31 = 0.5;
        material.DiffuseMatrix = transform;
        material.DiffuseTilingMethod = TilingMethod.Crop;
        document.Materials.Add(material);
        Mesh mesh = CreateTexturedQuad();
        mesh.Material = material;
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));

        CadMesh3DMaterial retained = Assert.Single(
            snapshot.Mesh3DMaterials.ToArray());
        Assert.Equal("Brass", retained.Name);
        Assert.Equal(new CadColor32(180, 90, 45, 255), retained.DiffuseColor);
        Assert.Equal(new CadColor32(20, 40, 60, 255), retained.AmbientColor);
        Assert.Equal(new CadColor32(220, 230, 240, 255), retained.SpecularColor);
        Assert.Equal(0.6f, retained.Opacity, 5);
        Assert.Equal(64.0f, retained.Shininess, 5);
        Assert.Equal(0.3f, retained.SelfIllumination, 5);
        Assert.Equal(0.75f, retained.DiffuseMapBlend, 5);
        Assert.Equal(CadMaterialTextureTiling.Crop, retained.TextureTiling);
        CadMaterialTextureResource resource = Assert.Single(
            snapshot.MaterialTextureResources.ToArray());
        Assert.Equal("maps/brass.png", resource.FileName);

        var resolver = new RetainedResolver(resource);
        CadRecordedMesh3DScene scene = new CadMesh3DSceneCompiler().Compile(
            snapshot,
            new CadMesh3DSceneOptions { MaterialTextureResolver = resolver });
        CadMesh3DDrawBatch batch = Assert.Single(scene.DrawBatches.ToArray());
        Assert.True(batch.MaterialBinding.HasResolvedTexture);
        Assert.Contains(new Vector2(0.25f, 0.5f),
            batch.TextureCoordinates.ToArray());
        Assert.Contains(new Vector2(2.25f, 3.5f),
            batch.TextureCoordinates.ToArray());
    }

    [Fact]
    public void ByLayerAndNestedByBlockMaterialsResolveBeforeInterning()
    {
        var document = new CadDocument();
        Material layerMaterial = CreateMaterial("LayerMaterial", null);
        Material insertMaterial = CreateMaterial("InsertMaterial", null);
        document.Materials.Add(layerMaterial);
        document.Materials.Add(insertMaterial);
        var layer = new Layer("MATERIAL_LAYER") { Material = layerMaterial };
        document.Layers.Add(layer);

        Mesh layerMesh = CreateTexturedQuad();
        layerMesh.Layer = layer;
        document.Entities.Add(layerMesh);

        var block = new BlockRecord("MATERIAL_BLOCK");
        Mesh blockMesh = CreateTexturedQuad();
        blockMesh.Material = document.Materials[Material.ByBlockName];
        block.Entities.Add(blockMesh);
        var insert = new Insert(block) { Material = insertMaterial };
        document.Entities.Add(insert);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadRecordedMesh3DScene scene = new CadMesh3DSceneCompiler().Compile(snapshot);

        string[] names = scene.DrawBatches.ToArray()
            .Select(batch => batch.MaterialBinding.Material.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["InsertMaterial", "LayerMaterial"], names);
        Assert.Equal(2, snapshot.Mesh3DMaterials.Length);
    }

    [Fact]
    public void UnauthoredFaceGetsBoundedPlanarCoordinatesAndMissingTextureFailsSoft()
    {
        var document = new CadDocument();
        Material material = CreateMaterial("FaceMaterial", "missing.png");
        document.Materials.Add(material);
        document.Entities.Add(new Face3D
        {
            Material = material,
            FirstCorner = new XYZ(10, 20, 4),
            SecondCorner = new XYZ(14, 20, 4),
            ThirdCorner = new XYZ(14, 26, 4),
            FourthCorner = new XYZ(10, 26, 4),
        });

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadRecordedMesh3DScene scene = new CadMesh3DSceneCompiler().Compile(
            snapshot,
            new CadMesh3DSceneOptions
            {
                MaterialTextureResolver = new RejectingResolver(),
            });
        CadMesh3DDrawBatch batch = Assert.Single(scene.DrawBatches.ToArray());
        Assert.False(batch.MaterialBinding.HasResolvedTexture);
        Assert.All(batch.TextureCoordinates.ToArray(), coordinate =>
        {
            Assert.InRange(coordinate.X, 0.0f, 1.0f);
            Assert.InRange(coordinate.Y, 0.0f, 1.0f);
        });
        Assert.Contains(Vector2.Zero, batch.TextureCoordinates.ToArray());
        Assert.Contains(Vector2.One, batch.TextureCoordinates.ToArray());
    }

    [Theory]
    [InlineData(ProjectionMethod.Planar)]
    [InlineData(ProjectionMethod.Box)]
    [InlineData(ProjectionMethod.Cylinder)]
    [InlineData(ProjectionMethod.Sphere)]
    public void UnauthoredMeshProjectionIsFiniteAndBoundedWhenMapperScalesExtents(
        ProjectionMethod projection)
    {
        var document = new CadDocument();
        Material material = CreateMaterial("Projected", "projected.png");
        material.DiffuseProjectionMethod = projection;
        document.Materials.Add(material);
        Mesh mesh = CreateQuadWithoutUv();
        mesh.Material = material;
        document.Entities.Add(mesh);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadRecordedMesh3DScene scene = new CadMesh3DSceneCompiler().Compile(snapshot);
        CadMesh3DDrawBatch batch = Assert.Single(scene.DrawBatches.ToArray());
        Assert.All(batch.TextureCoordinates.ToArray(), coordinate =>
        {
            Assert.True(float.IsFinite(coordinate.X));
            Assert.True(float.IsFinite(coordinate.Y));
            Assert.InRange(coordinate.X, 0.0f, 1.0f);
            Assert.InRange(coordinate.Y, 0.0f, 1.0f);
        });
    }

    [Fact]
    public void NativeSceneReferencesOneInternedExternalMaterialImage()
    {
        var document = new CadDocument();
        Material material = CreateMaterial("Shared", "shared.png");
        material.DiffuseTilingMethod = TilingMethod.Crop;
        document.Materials.Add(material);
        Mesh first = CreateTexturedQuad();
        first.Material = material;
        document.Entities.Add(first);
        Mesh second = CreateTexturedQuad();
        second.Material = material;
        document.Entities.Add(second);

        CadDocumentSnapshot snapshot = new CadSnapshotCompiler().Compile(
            new CadDocumentSession(document));
        CadMaterialTextureResource resource = Assert.Single(
            snapshot.MaterialTextureResources.ToArray());
        CadRecordedMesh3DScene scene = new CadMesh3DSceneCompiler().Compile(
            snapshot,
            new CadMesh3DSceneOptions
            {
                MaterialTextureResolver = new RetainedResolver(resource),
            });
        CadNativeMesh3DScene native = new CadNativeMesh3DSceneCompiler().Compile(
            scene,
            new CadNativeMesh3DCamera(
                System.Numerics.Matrix4x4.Identity,
                System.Numerics.Matrix4x4.Identity,
                new Vector3(0, 0, 5),
                new NativeImageRect(0, 0, 640, 480)),
            701U);

        CadNativeMesh3DTextureBinding binding = Assert.Single(
            native.TextureBindings.ToArray());
        Assert.Equal(1U, binding.ResourceId);
        Assert.Equal(native.NativeGeneration, binding.Generation);

        ReadOnlySpan<byte> stream = native.Stream;
        uint resourceOffset = BinaryPrimitives.ReadUInt32LittleEndian(stream[52..]);
        uint resourceCount = BinaryPrimitives.ReadUInt32LittleEndian(stream[56..]);
        uint resourceStride = BinaryPrimitives.ReadUInt32LittleEndian(stream[60..]);
        Assert.Equal(2U, resourceCount);
        Assert.Equal((uint)NativeSceneResourceKind.Image,
            BinaryPrimitives.ReadUInt32LittleEndian(
                stream[((int)resourceOffset + 4)..]));
        Assert.NotEqual(0U,
            BinaryPrimitives.ReadUInt32LittleEndian(
                stream[((int)resourceOffset + 8)..]) &
            (uint)NativeSceneRecordFlags.ExternalImage);

        int meshResource = checked((int)(resourceOffset + resourceStride));
        uint meshPayload = BinaryPrimitives.ReadUInt32LittleEndian(
            stream[(meshResource + 32)..]);
        Assert.Equal(1U,
            BinaryPrimitives.ReadUInt32LittleEndian(
                stream[((int)meshPayload + 4)..]) & 1U);
        Assert.Equal(0U,
            BinaryPrimitives.ReadUInt32LittleEndian(
                stream[((int)meshPayload + 248)..]));
        uint factors = BinaryPrimitives.ReadUInt32LittleEndian(
            stream[((int)meshPayload + 252)..]);
        Assert.InRange(factors & 0xffffU, 49150U, 49152U);
        Assert.InRange(factors >> 16, 19660U, 19662U);
    }

    private static Material CreateMaterial(string name, string? texture)
    {
        return new Material(name)
        {
            AmbientColorMethod = ColorMethod.Override,
            AmbientColor = new ACadSharp.Color(20, 40, 60),
            AmbientColorFactor = 1.0,
            DiffuseColorMethod = ColorMethod.Override,
            DiffuseColor = new ACadSharp.Color(180, 90, 45),
            DiffuseColorFactor = 1.0,
            SpecularColorMethod = ColorMethod.Override,
            SpecularColor = new ACadSharp.Color(220, 230, 240),
            SpecularColorFactor = 1.0,
            SpecularGlossFactor = 0.5,
            Opacity = 0.8,
            Translucence = 0.25,
            SelfIllumination = 0.3,
            ChannelFlags = MaterialChannelFlags.UseDiffuse,
            DiffuseMapSource = MapSource.UseImageFile,
            DiffuseMapFileName = texture!,
            DiffuseMapBlendFactor = 0.75,
            DiffuseAutoTransform = AutoTransformMethodFlags.ScaleMapper,
            DiffuseTilingMethod = TilingMethod.Tile,
        };
    }

    private static Mesh CreateTexturedQuad()
    {
        Mesh mesh = CreateQuadWithoutUv();
        mesh.AddTextureCoordinate(new XYZ(0, 0, 0));
        mesh.AddTextureCoordinate(new XYZ(1, 0, 0));
        mesh.AddTextureCoordinate(new XYZ(1, 1, 0));
        mesh.AddTextureCoordinate(new XYZ(0, 1, 0));
        return mesh;
    }

    private static Mesh CreateQuadWithoutUv()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(new XYZ(0, 0, 0));
        mesh.Vertices.Add(new XYZ(1, 0, 0));
        mesh.Vertices.Add(new XYZ(1, 1, 0));
        mesh.Vertices.Add(new XYZ(0, 1, 0));
        mesh.Faces.Add([0, 1, 2, 3]);
        return mesh;
    }

    private sealed class RetainedResolver(
        CadMaterialTextureResource expected) :
        ICadMaterialTextureSourceResolver,
        IProGpuTextureLeaseSource
    {
        public bool TryResolve(
            in CadMaterialTextureRequest request,
            out IProGpuTextureLeaseSource source)
        {
            Assert.Equal(expected, request.Resource);
            source = this;
            return true;
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

    private sealed class RejectingResolver : ICadMaterialTextureSourceResolver
    {
        public bool TryResolve(
            in CadMaterialTextureRequest request,
            out IProGpuTextureLeaseSource source)
        {
            source = null!;
            return false;
        }
    }
}
