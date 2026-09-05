using System.Numerics;

namespace ProGPU.Samples.Suntrail.Game;

public enum GameMode { Title, Playing, Paused, Fallen, LevelComplete, Complete }
public enum PlatformKind { Ground, Ledge, Moving, Crate }
public readonly record struct Box(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public bool Intersects(Box b) => X < b.Right && Right > b.X && Y < b.Bottom && Bottom > b.Y;
}
public readonly record struct Platform(Box Bounds, PlatformKind Kind, float Travel = 0, float Phase = 0, float VerticalTravel = 0)
{
    public Box At(float time) => Bounds with { X = Bounds.X + MathF.Sin(time * 1.4f + Phase) * Travel, Y = Bounds.Y + MathF.Sin(time * 1.4f + Phase) * VerticalTravel };
}
public readonly record struct Checkpoint(float X, float Y);
public struct Pickup { public Vector2 Position; public bool Collected; public bool IsRelic; }
public struct Enemy { public Vector2 Position; public float Left, Right, Speed; public bool Defeated; }
public struct Particle { public Vector2 Position, Velocity; public float Life, MaxLife; public int Kind; }

/// <summary>Original authored platform grammar. Generation is bounded and seeded; no runtime asset discovery.</summary>
public sealed class Level
{
    public static readonly string[] Names = ["The waking orchard", "The amber aqueduct", "Crystal cathedral", "The drowned kingdom", "Copperleaf ascent", "The silent glacier", "Furnace of stars", "The last sunrise"];
    public static readonly string[] Regions = ["VERDANT ISLES", "SANDSTONE REACH", "LUMEN CAVERNS", "TIDAL KINGDOM", "AUTUMN HIGHLANDS", "FROSTBOUND PEAKS", "OBSIDIAN FORGE", "CELESTIAL GARDENS"];
    public static readonly string[] Descriptions = [
        "Meadow paths, orchard boughs, and gentle first leaps.",
        "Follow the ancient arches across a sunlit sandstone ravine.",
        "Climb crystal shelves beneath a luminous cavern ceiling.",
        "Cross broken causeways above the mist of a forgotten coast.",
        "Ascend copper forests through rising terraces and falling leaves.",
        "Find the high route through snow pines and blue ice.",
        "Thread basalt ledges and thorn fields above an ember sea.",
        "A final climb through cloud islands and marble sky gardens."];
    // Elevation scores are authored per world, rather than repeating one section cycle.
    // Adjacent rises stay within a full ordinary jump; high routes are optional.
    private static readonly int[][] Elevations = [
        [0, 0, 24, 0, 40, 24, 0, 48, 24, 0],
        [0, 32, 64, 32, 0, 32, 64, 80, 48, 24, 0],
        [0, 48, 96, 64, 32, 80, 112, 64, 16, 48, 0],
        [0, 0, 24, 0, 48, 24, 0, 32, 0, 24, 0, 0],
        [0, 48, 96, 144, 96, 48, 96, 144, 112, 64, 24, 0],
        [0, 56, 24, 80, 128, 80, 24, 64, 112, 64, 24, 0],
        [0, 32, 80, 48, 0, 48, 96, 48, 0, 56, 24, 0],
        [0, 48, 96, 144, 96, 48, 96, 144, 96, 48, 0, 48, 0]];
    public int Index { get; }
    public int Biome => Index;
    public Platform[] Platforms { get; }
    public Pickup[] Pickups { get; }
    public Enemy[] Enemies { get; }
    public Box[] Hazards { get; }
    public Checkpoint[] Checkpoints { get; }
    public Vector2 Spawn { get; } = new(140, 530);
    public Vector2 Exit { get; }
    public float Width => Exit.X + 500;
    public int CoinCount { get; }

    public Level(int index)
    {
        if ((uint)index >= Names.Length) throw new ArgumentOutOfRangeException(nameof(index));
        Index = index;
        var platforms = new List<Platform>();
        var pickups = new List<Pickup>();
        var enemies = new List<Enemy>();
        var hazards = new List<Box>();
        var checkpoints = new List<Checkpoint>();
        float x = 0;
        float lastY = 600;
        int sections = Elevations[index].Length;
        for (int section = 0; section < sections; section++)
        {
            bool first = section == 0, last = section == sections - 1;
            int rhythm = (section * 5 + index * 3) % 4;
            float width = first ? 930 : last ? 850 :
                index switch { 1 => 620 + rhythm * 70, 3 => 720 + rhythm * 55,
                    4 => 620 + rhythm * 40, 5 => 590 + rhythm * 45,
                    7 => 610 + rhythm * 65, _ => 560 + rhythm * 50 };
            float y = 600 - Elevations[index][section];
            platforms.Add(new(new(x, y, width, 510), PlatformKind.Ground));
            if (section == 3 || section == sections - 4) checkpoints.Add(new(x + 95, y));
            if (!first && !last)
            {
                bool relicRoute = section is 1 or 4 or 7;
                float shelfX = x + 110 + rhythm * 12;
                float shelfY = y - (index is 2 or 7 ? 112 : 96);
                platforms.Add(new(new(shelfX, shelfY, index == 1 ? 205 : 142, 24), PlatformKind.Ledge));
                if (relicRoute)
                {
                    // Crystal/snow worlds use vertical lifts; others use horizontal ferries.
                    bool lift = index is 2 or 5;
                    platforms.Add(new(new(x + 325, y - 188, 124, 24), PlatformKind.Moving,
                        lift ? 0 : 38, section, lift ? 24 : 0));
                    pickups.Add(new() { Position = new(x + 384, y - 232), IsRelic = true });
                }
                else if (index is 1 or 2 or 7 || rhythm == 0)
                {
                    // A staircase of optional upper shelves breaks up the ground route.
                    platforms.Add(new(new(x + 292, y - 150, 112, 24), PlatformKind.Ledge));
                    platforms.Add(new(new(x + width - 230, y - 83, 104, 24), PlatformKind.Ledge));
                }
                else platforms.Add(new(new(x + 305, y - 54, 54, 54), PlatformKind.Crate));
                if (rhythm != 2 || index >= 4)
                    enemies.Add(new() { Position = new(x + width - 125, y - 34), Left = x + width - 205, Right = x + width - 45, Speed = -(50 + index * 7) });
                if ((index >= 2 && rhythm == 2) || (index == 6 && section % 2 == 0))
                    hazards.Add(new(x + 255, y - 22, index == 6 ? 76 : 52, 22));
            }
            int coinRow = first ? 5 : 4 + rhythm;
            for (int c = 0; c < coinRow; c++)
                pickups.Add(new() { Position = new(x + (first ? 370 : 135) + c * 35,
                    y - (first ? 65 : 140) - MathF.Sin(c * MathF.PI / (coinRow - 1)) * 26) });
            if (!last)
            {
                float gap = 96 + ((section * 3 + index) % 5) * 10;
                for (int c = 0; c < 3; c++)
                    pickups.Add(new() { Position = new(x + width - 20 + c * (gap + 40) / 2,
                        y - 100 - MathF.Sin(c * MathF.PI / 2) * 35) });
                x += width + gap;
            }
            lastY = y;
        }
        Platforms = platforms.ToArray(); Pickups = pickups.ToArray(); Enemies = enemies.ToArray();
        Hazards = hazards.ToArray(); Checkpoints = checkpoints.ToArray();
        Exit = new(x + 615, lastY);
        CoinCount = Pickups.Count(p => !p.IsRelic);
    }
}
