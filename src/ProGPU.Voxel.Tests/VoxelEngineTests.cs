using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene.Extensions;
using ProGPU.Voxel;
using Xunit;

namespace ProGPU.Tests;

public sealed class VoxelEngineTests
{
    [Fact]
    public void WorldCoordinatesRoundTripAcrossNegativeChunkBoundaries()
    {
        var world = new VoxelWorld();

        Assert.True(world.SetBlock(-1, -1, -1, VoxelBlock.Stone));
        Assert.True(world.SetBlock(-16, 0, -17, VoxelBlock.Dirt));

        Assert.Equal(VoxelBlock.Stone, world.GetBlock(-1, -1, -1));
        Assert.Equal(VoxelBlock.Dirt, world.GetBlock(-16, 0, -17));
        Assert.Equal(new VoxelChunkPosition(-1, -1, -1), VoxelWorld.ToChunkPosition(-1, -1, -1));
        Assert.Equal(new VoxelChunkPosition(-1, 0, -2), VoxelWorld.ToChunkPosition(-16, 0, -17));
        Assert.Equal(15, VoxelWorld.ToLocal(-1));
        Assert.Equal(0, VoxelWorld.ToLocal(-16));
    }

    [Fact]
    public void SingleBlockProducesSixIndexedQuads()
    {
        var world = new VoxelWorld();
        world.SetBlock(2, 3, 4, VoxelBlock.Grass);
        var chunk = Assert.Single(world.Chunks);

        var mesh = world.GetOrBuildMesh(chunk);

        Assert.Equal(6, mesh.VisibleFaceCount);
        Assert.Equal(6, mesh.MergedQuadCount);
        Assert.Equal(24, mesh.Vertices.Length);
        Assert.Equal(36, mesh.Indices.Length);
        Assert.Equal(12, mesh.TriangleCount);
    }

    [Fact]
    public void GreedyMeshingCollapsesAFullChunkToSixQuads()
    {
        var world = new VoxelWorld();
        var chunk = world.GetOrCreateChunk(new VoxelChunkPosition(0, 0, 0));
        for (var y = 0; y < VoxelChunk.Size; y++)
        {
            for (var z = 0; z < VoxelChunk.Size; z++)
            {
                for (var x = 0; x < VoxelChunk.Size; x++)
                {
                    chunk.SetLocal(x, y, z, VoxelBlock.Stone);
                }
            }
        }

        var mesh = world.GetOrBuildMesh(chunk);

        Assert.Equal(6 * VoxelChunk.Size * VoxelChunk.Size, mesh.VisibleFaceCount);
        Assert.Equal(6, mesh.MergedQuadCount);
        Assert.Equal(24, mesh.Vertices.Length);
        Assert.Equal(36, mesh.Indices.Length);
    }

    [Fact]
    public void DifferentMaterialsDoNotMergeAcrossAVisiblePlane()
    {
        var world = new VoxelWorld();
        world.SetBlock(0, 0, 0, VoxelBlock.Grass);
        world.SetBlock(1, 0, 0, VoxelBlock.Stone);
        var mesh = world.GetOrBuildMesh(Assert.Single(world.Chunks));

        Assert.Equal(10, mesh.VisibleFaceCount);
        Assert.True(mesh.MergedQuadCount > 6);
    }

    [Fact]
    public void BoundaryMutationInvalidatesBothParticipatingChunks()
    {
        var world = new VoxelWorld();
        world.SetBlock(15, 1, 1, VoxelBlock.Stone);
        world.SetBlock(16, 1, 1, VoxelBlock.Stone);
        Assert.True(world.TryGetChunk(new VoxelChunkPosition(0, 0, 0), out var left));
        Assert.True(world.TryGetChunk(new VoxelChunkPosition(1, 0, 0), out var right));
        world.GetOrBuildMesh(left);
        world.GetOrBuildMesh(right);
        Assert.False(left.IsMeshDirty);
        Assert.False(right.IsMeshDirty);

        world.SetBlock(15, 1, 1, VoxelBlock.Air);

        Assert.True(left.IsMeshDirty);
        Assert.True(right.IsMeshDirty);
        Assert.True(left.MeshVersion > 0);
        Assert.True(right.MeshVersion > 0);
    }

    [Fact]
    public void GridDdaReturnsHitAndPlacementCell()
    {
        var world = new VoxelWorld();
        world.SetBlock(3, 1, 0, VoxelBlock.Wood);

        var found = VoxelRaycaster.TryCast(
            world,
            new Vector3(0.5f, 1.5f, 0.5f),
            Vector3.UnitX,
            8f,
            out var hit);

        Assert.True(found);
        Assert.Equal((3, 1, 0), hit.Block);
        Assert.Equal((2, 1, 0), hit.Previous);
        Assert.Equal((-1, 0, 0), hit.Normal);
        Assert.Equal(VoxelBlock.Wood, hit.BlockType);
        Assert.InRange(hit.Distance, 2.49f, 2.51f);
    }

    [Fact]
    public void PlayerFallsOntoSolidTerrain()
    {
        var world = new VoxelWorld();
        for (var z = -2; z <= 2; z++)
        {
            for (var x = -2; x <= 2; x++)
            {
                world.SetBlock(x, 0, z, VoxelBlock.Stone);
            }
        }
        var player = new VoxelPlayerController();
        player.Teleport(new Vector3(0.5f, 4f, 0.5f));
        var input = default(VoxelPlayerInput);

        for (var frame = 0; frame < 180; frame++)
        {
            player.Update(world, input, 1f / 60f);
        }

        Assert.True(player.IsGrounded);
        Assert.InRange(player.Position.Y, 0.999f, 1.01f);
    }

    [Fact]
    public void PositiveStrafeMovesTowardCameraRight()
    {
        var world = new VoxelWorld();
        var player = new VoxelPlayerController();
        player.Teleport(Vector3.Zero, yaw: 0f, pitch: 0f);
        player.ToggleFlying();

        player.Update(
            world,
            new VoxelPlayerInput(
                Forward: 0f,
                Strafe: 1f,
                Vertical: 0f,
                Jump: false,
                Sprint: false),
            0.05f);

        Assert.True(player.Position.X < 0f);
        Assert.Equal(0f, player.Position.Z, 5);
    }

    [Fact]
    public void HomogeneousFrustumTestRejectsBoxOutsideIdentityClipVolume()
    {
        Assert.True(VoxelFrustum.Intersects(
            Matrix4x4.Identity,
            new Vector3(-0.5f, -0.5f, 0.1f),
            new Vector3(0.5f, 0.5f, 0.9f)));
        Assert.False(VoxelFrustum.Intersects(
            Matrix4x4.Identity,
            new Vector3(2f, -0.5f, 0.1f),
            new Vector3(3f, 0.5f, 0.9f)));
    }

    [Fact]
    public void TerrainGenerationIsDeterministic()
    {
        var settings = new VoxelTerrainSettings(42, ChunkRadius: 1, BuildMeshes: false);
        var first = VoxelTerrainGenerator.Generate(settings);
        var second = VoxelTerrainGenerator.Generate(settings);

        Assert.Equal(first.ChunkCount, second.ChunkCount);
        for (var z = -12; z <= 12; z += 3)
        {
            for (var x = -12; x <= 12; x += 3)
            {
                Assert.Equal(first.FindSurfaceY(x, z), second.FindSurfaceY(x, z));
            }
        }
    }

    [Fact]
    public void VoxelShaderIsEmbeddedAndDocumentsItsCostModel()
    {
        var source = ShaderResource.Load(
            typeof(VoxelTerrainCompilationPayload),
            "VoxelTerrain.wgsl");

        Assert.Contains("// Algorithm:", source, StringComparison.Ordinal);
        Assert.Contains("// Time complexity:", source, StringComparison.Ordinal);
        Assert.Contains("// Space complexity:", source, StringComparison.Ordinal);
        Assert.Contains("@" + "vertex", source, StringComparison.Ordinal);
        Assert.Contains("@" + "fragment", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var<storage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RayTracingVolumeUsesXThenZThenYLayoutAndVersionsMutations()
    {
        var volume = new VoxelRayTracingVolume
        {
            Blocks = new uint[2 * 3 * 4],
            OriginX = -1,
            OriginY = 5,
            OriginZ = 9,
            Width = 2,
            Height = 4,
            Depth = 3,
            ContentVersion = 7
        };

        Assert.True(volume.TrySetBlock(0, 7, 11, (uint)VoxelBlock.Wood));
        Assert.Equal((uint)VoxelBlock.Wood, volume.Blocks[1 + 2 * (2 + 3 * 2)]);
        Assert.Equal(8, volume.ContentVersion);
        Assert.False(volume.TrySetBlock(1, 7, 11, (uint)VoxelBlock.Stone));
        Assert.Equal(8, volume.ContentVersion);
    }

    [Fact]
    public void RayTracingAndMaterialShadersExposeBoundedPublicContracts()
    {
        var rayTracing = ShaderResource.Load(
            typeof(VoxelTerrainCompilationPayload),
            "VoxelRayTracing.wgsl");
        var material = ShaderResource.Load(
            typeof(VoxelTerrainCompilationPayload),
            "VoxelMaterialDynamicEnvironment.wgsl");

        Assert.Contains("stepIndex < 512u", rayTracing, StringComparison.Ordinal);
        Assert.Contains("var<storage, read> blocks", rayTracing, StringComparison.Ordinal);
        Assert.Contains("fn progpu_voxel_deform", material, StringComparison.Ordinal);
        Assert.Contains("fn progpu_voxel_shade", material, StringComparison.Ordinal);
    }

    [Fact]
    public void WorldContentVersionChangesOnlyForEffectiveMutations()
    {
        var world = new VoxelWorld();

        Assert.True(world.SetBlock(1, 2, 3, VoxelBlock.Stone));
        var version = world.ContentVersion;
        Assert.False(world.SetBlock(1, 2, 3, VoxelBlock.Stone));
        Assert.Equal(version, world.ContentVersion);
        Assert.True(world.SetBlock(1, 2, 3, VoxelBlock.Air));
        Assert.Equal(version + 1, world.ContentVersion);
    }
}
