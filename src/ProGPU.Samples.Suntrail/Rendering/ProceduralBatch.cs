using System.Numerics;
using System.Runtime.InteropServices;
using ProGPU.Scene;
using ProGPU.Samples.Suntrail.Game;

namespace ProGPU.Samples.Suntrail.Rendering;

public enum Artwork { Sky, Cliff, Tree, Bush, Flower, Crate, Coin, Courier, Beetle, Lantern, Portal, Ledge, Thorns, Spark, Cloud, Mountain, Ruin, Mushroom, Shadow = 19, SunShaft, Fern, Grass, Crystal, Palm, Pine, Spire, Water, Cavern }

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProceduralSprite(Vector4 Bounds, Vector4 Color, Vector4 Material);

/// <summary>
/// One UI-thread-owned, versioned frame. Build completes before visual invalidation;
/// its contents remain frozen throughout compilation/submission and cached replay.
/// Capacity is fixed: overflow is an explicit failure, never a silent dropped sprite.
/// </summary>
public sealed class ProceduralBatch
{
    public const int Capacity = 2048;
    private readonly ProceduralSprite[] _sprites = new ProceduralSprite[Capacity];
    public ReadOnlySpan<ProceduralSprite> Sprites => _sprites.AsSpan(0, Count);
    public int Count { get; private set; }
    public uint Generation { get; private set; }
    public Vector2 Size { get; private set; }
    public Vector4 Scene { get; private set; }
    public bool EnableBackgroundOcclusion { get; set; } = true;
    private readonly Vector4[] _occluders = new Vector4[8];
    public ReadOnlySpan<Vector4> Occluders => _occluders;
    public int OccluderCount { get; private set; }
    public Vector4 Light0 { get; private set; }
    public Vector4 Light1 { get; private set; }
    public Vector4 Light2 { get; private set; }
    private float _scale, _cameraX, _cameraY;

    public void Build(GameSession game, Vector2 size, float atmosphereTime)
    {
        if (size.X <= 0 || size.Y <= 0) return;
        Count = 0; Size = size; _scale = size.Y / 800;
        float worldWidth = size.X / _scale;
        game.ViewWidth = worldWidth;
        _cameraX = game.CameraX; _cameraY = game.CameraY;
        Scene = new(atmosphereTime, game.Level.Biome, _cameraX, _scale);
        float inset = Math.Max(8, 4 / _scale);
        OccluderCount = 0;
        if (EnableBackgroundOcclusion)
        {
            foreach (var platform in game.Level.Platforms)
            {
                var b = platform.Bounds;
                if (platform.Kind != PlatformKind.Ground || b.Right <= _cameraX || b.X >= _cameraX + worldWidth) continue;
                if (OccluderCount == _occluders.Length) { OccluderCount = 0; break; }
                // Inset past every antialiased/noisy edge. These pixels are fully opaque.
                _occluders[OccluderCount++] = new((b.X + inset - _cameraX) * _scale,
                    (b.Y + inset - _cameraY) * _scale, (b.Right - inset - _cameraX) * _scale,
                    (b.Bottom - inset - _cameraY) * _scale);
            }
        }
        Light0 = Light(game.Level.Checkpoints[0].X + 25, game.Level.Checkpoints[0].Y - 77, 225, .38f);
        Light1 = Light(game.Level.Checkpoints[1].X + 25, game.Level.Checkpoints[1].Y - 77, 225, .38f);
        Light2 = Light(game.Level.Exit.X + 37, game.Level.Exit.Y - 85, 290, .52f);
        int biome = game.Level.Biome;
        Artwork landmark = biome switch { 1 or 3 => Artwork.Palm, 2 => Artwork.Crystal, 5 => Artwork.Pine, 6 or 7 => Artwork.Spire, _ => Artwork.Tree };
        AddScreen(Artwork.Sky, 0, 0, size.X, size.Y);
        // Three depth planes with different camera factors; all generated from integer coordinates.
        for (int layer = 0; layer < 3; layer++)
        {
            float parallax = .12f + layer * .12f;
            int start = (int)MathF.Floor(_cameraX * parallax / 290) - 2;
            for (int i = start; i < start + (int)(worldWidth / 290) + 5; i++)
            {
                float seed = Hash(i + layer * 371);
                float h = 150 + seed * 200 + (2 - layer) * 48;
                AddScreen(Artwork.Mountain, (i * 290 - _cameraX * parallax) * _scale, (625 - h - _cameraY * parallax) * _scale, 470 * _scale, (h + 400) * _scale,
                    new(1, 1, 1, 1), layer, seed);
            }
        }
        for (int i = -1; biome != 2 && i < 7; i++)
        {
            float x = i * 370 - (_cameraX * .09f + atmosphereTime * 3) % 370;
            AddScreen(Artwork.Cloud, x * _scale, (65 + Hash(i + 75) * 160) * _scale, 240 * _scale, 100 * _scale, new(1, 1, 1, .75f));
        }
        int firstTree = (int)MathF.Floor(_cameraX * .52f / 225) - 1;
        for (int i = firstTree; i < firstTree + (int)(worldWidth / 225) + 3; i++)
        {
            float seed = Hash(i + 88); float h = 200 + seed * 170;
            AddScreen(landmark, (i * 225 - _cameraX * .52f) * _scale, (640 - h - _cameraY * .3f) * _scale, 235 * _scale, h * _scale,
                Vector4.One, seed, 1);
        }
        if (biome == 2) AddScreen(Artwork.Cavern, 0, 0, size.X, 265 * _scale);
        if (biome is 3 or 6)
            AddScreen(Artwork.Water, 0, (690 - _cameraY * .2f) * _scale, size.X, 260 * _scale);
        int ruinCell = (int)MathF.Floor(_cameraX * .25f / 1200);
        for (int i = ruinCell; i < ruinCell + 3; i++)
            AddScreen(Artwork.Ruin, (i * 1200 + 900 - _cameraX * .25f) * _scale, 345 * _scale, 230 * _scale, 245 * _scale, new(1, 1, 1, .48f), i);
        if (biome == 1) {
            int archCell = (int)MathF.Floor(_cameraX * .34f / 240);
            for (int i = archCell; i < archCell + (int)(worldWidth / 240) + 2; i++)
                AddScreen(Artwork.Ruin, (i * 240 - _cameraX * .34f) * _scale, 360 * _scale, 330 * _scale, 345 * _scale, new(1, 1, 1, .38f), i);
        }
        // Static world records are culled against the viewport before entering the batch.
        foreach (var platform in game.Level.Platforms)
        {
            var b = platform.At(game.Time);
            if (b.Right < _cameraX - 250 || b.X > _cameraX + worldWidth + 250) continue;
            if (platform.Kind == PlatformKind.Ground)
            {
                float seed = Hash((int)b.X);
                if (b.X > 0 || biome != 0)
                    Add(landmark, b.X + 25, b.Y - 295, 240, 310, Vector4.One, seed);
                Add(Artwork.Cliff, b.X, b.Y - 8, b.Width, b.Height + 8, Vector4.One, seed);
                for (int k = 0; k < b.Width / 72; k++)
                {
                    float variation = Hash(k * 71 + (int)b.X);
                    float px = b.X + 16 + k * 72 + variation * 32;
                    if (biome is 2 or 6) {
                        Add(Artwork.Crystal, px, b.Y - 36 - variation * 30, 45 + variation * 25, 44 + variation * 30, Vector4.One, variation);
                        continue;
                    }
                    if (biome == 1 && variation < .62f) continue;
                    Add(Artwork.Grass, px - 12, b.Y - 31, 65, 38, Vector4.One, variation);
                    if (biome == 5) continue;
                    if (variation < .17f) Add(Artwork.Mushroom, px, b.Y - 32, 34, 38, Vector4.One, variation);
                    else if (variation < .40f) Add(Artwork.Flower, px + 8, b.Y - 32 - variation * 18, 21, 42, Vector4.One, variation);
                    else if (variation > .72f) Add(Artwork.Fern, px, b.Y - 48, 76, 54, Vector4.One, variation);
                }
                if (biome is 0 or 3 or 4 or 7) Add(Artwork.Bush, b.X + b.Width * .55f, b.Y - 67, 145, 76, Vector4.One, seed);
            }
            else Add(platform.Kind == PlatformKind.Crate ? Artwork.Crate : Artwork.Ledge, b.X - 3, b.Y - 5, b.Width + 6, b.Height + 12, Vector4.One, platform.Phase);
        }
        foreach (var hazard in game.Level.Hazards) Add(Artwork.Thorns, hazard.X - 3, hazard.Y - 5, hazard.Width + 6, hazard.Height + 9);
        foreach (var pickup in game.Level.Pickups)
            if (!pickup.Collected) Add(Artwork.Coin, pickup.Position.X - 20, pickup.Position.Y - 24, 40, 48, Vector4.One, pickup.Position.X * .017f, pickup.IsRelic ? 1 : 0);
        for (int i = 0; i < game.Level.Checkpoints.Length; i++)
        {
            var cp = game.Level.Checkpoints[i];
            Add(Artwork.Lantern, cp.X - 22, cp.Y - 118, 75, 125, Vector4.One, i <= game.CheckpointIndex ? 1 : 0);
        }
        Add(Artwork.Portal, game.Level.Exit.X - 40, game.Level.Exit.Y - 188, 155, 205);
        foreach (var enemy in game.Level.Enemies)
        {
            if (enemy.Defeated) continue;
            Add(Artwork.Shadow, enemy.Position.X - 10, enemy.Position.Y + 23, 66, 19, new(0, 0, 0, .28f));
            Add(Artwork.Beetle, enemy.Position.X - 10, enemy.Position.Y - 12, 64, 51, Vector4.One, enemy.Speed < 0 ? -1 : 1);
        }
        if (game.Mode != GameMode.Fallen)
        {
            var p = game.Position;
            Add(Artwork.Shadow, p.X - 16, p.Y + 39, 66, 17, new(0, 0, 0, .3f));
            Add(Artwork.Courier, p.X - 25, p.Y - 24, 82, 80,
                new(1, 1, 1, game.Invulnerability > 0 && game.Tick % 12 < 5 ? .42f : 1),
                game.Facing, game.Grounded ? Math.Clamp(Math.Abs(game.Velocity.X) / 180, 0, 1) : -1);
        }
        foreach (var particle in game.Particles)
            if (particle.Life > 0) Add(Artwork.Spark, particle.Position.X - 14, particle.Position.Y - 14, 28, 28,
                new(1, 1, 1, Math.Clamp(particle.Life / particle.MaxLife, 0, 1)), particle.Kind);
        // Airborne pollen is visual only; deterministic and bounded independently of simulation.
        for (int i = 0; i < 24; i++)
        {
            float x = (Hash(i + 841) * worldWidth + atmosphereTime * (3 + i % 4)) % worldWidth;
            float y = 120 + Hash(i + 615) * 450 + MathF.Sin(atmosphereTime + i) * 12;
            if (biome is 4 or 5) y = (y + atmosphereTime * (17 + i % 7)) % 680;
            if (biome == 6) y = 680 - (y + atmosphereTime * 27) % 610;
            AddScreen(Artwork.Spark, x * _scale, y * _scale, 4 * _scale, 4 * _scale, new(1, 1, 1, .5f), biome == 5 ? 3 : biome == 4 ? 4 : 1);
        }
        AddScreen(Artwork.SunShaft, 0, 0, size.X, size.Y);
        Generation++;
    }

    private Vector4 Light(float x, float y, float radius, float strength)
        => new((x - _cameraX) * _scale, (y - _cameraY) * _scale, radius * _scale, strength);

    private void Add(Artwork kind, float x, float y, float w, float h, Vector4 color = default, float p = 0, float q = 0)
        => AddScreen(kind, (x - _cameraX) * _scale, (y - _cameraY) * _scale, w * _scale, h * _scale, color, p, q);
    private void AddScreen(Artwork kind, float x, float y, float w, float h, Vector4 color = default, float p = 0, float q = 0)
    {
        if (x + w < 0 || y + h < 0 || x > Size.X || y > Size.Y) return;
        if (Count == Capacity) throw new InvalidOperationException("Suntrail procedural sprite capacity exceeded.");
        _sprites[Count++] = new(new(x, y, w, h), color == default ? Vector4.One : color, new((float)kind, p, q, 0));
    }
    public static float Hash(int seed)
    {
        uint x = unchecked((uint)seed * 747796405u + 2891336453u);
        x = ((x >> (int)((x >> 28) + 4)) ^ x) * 277803737u;
        return ((x >> 22) ^ x) / (float)uint.MaxValue;
    }
}

public static class ProceduralDrawingContextExtensions
{
    // Application-reserved extension ID. Registration is per compositor/device.
    public const int ExtensionId = 0x53554e;
    public static void DrawProceduralWorld(this DrawingContext context, ProceduralBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawExtension, ExtensionId = ExtensionId,
            Rect = new(0, 0, batch.Size.X, batch.Size.Y), DataParam = batch
        });
    }
}
