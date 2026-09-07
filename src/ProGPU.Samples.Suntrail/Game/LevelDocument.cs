using System.Numerics;

namespace ProGPU.Samples.Suntrail.Game;

public enum LevelObjectKind { Ground, Ledge, Moving, Crate, Pipe, Stone, Coin, Relic, Enemy, Hazard, Checkpoint, Spawn, Exit, Saw, Flame, Crusher }
public readonly record struct LevelObject(LevelObjectKind Kind, Box Bounds, float Travel = 0, float Phase = 0, float VerticalTravel = 0);

/// <summary>
/// Immutable, validated authoring snapshot, independent of mutable play state.
/// Creation is O(N); gameplay receives its own arrays. Limits bound simulation and
/// worst-case procedural decoration without changing the renderer's quality.
/// </summary>
public sealed class LevelDocument
{
    public const int MaximumObjects = 256;
    public const int MaximumBytes = 1_048_576;
    private readonly LevelObject[] _objects;
    public string Name { get; }
    public int Biome { get; }
    public ReadOnlySpan<LevelObject> Objects => _objects;

    public LevelDocument(string name, int biome, ReadOnlySpan<LevelObject> objects)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new FormatException("A level name must contain 1–80 printable characters.");
        if ((uint)biome >= Level.Names.Length) throw new FormatException("Biome must be from 0 through 7.");
        if (objects.Length > MaximumObjects) throw new FormatException($"A level supports at most {MaximumObjects} objects.");
        int spawns = 0, exits = 0, budget = 0;
        foreach (var item in objects)
        {
            var b = item.Bounds;
            if (!Enum.IsDefined(item.Kind) || !Finite(b.X, b.Y, b.Width, b.Height, item.Travel, item.Phase, item.VerticalTravel))
                throw new FormatException("Object kinds and coordinates must be valid and finite.");
            if (b.X < 0 || b.Right > 32_000 || b.Y < 0 || b.Y > 950 || b.Width < 0 || b.Width > 2_000 || b.Height < 0 || b.Height > 600 || b.Bottom > 1550)
                throw new FormatException("Objects must fit within x 0–32000 and y 0–950, with width ≤2000 and height ≤600.");
            bool point = item.Kind is LevelObjectKind.Coin or LevelObjectKind.Relic or LevelObjectKind.Checkpoint or LevelObjectKind.Spawn or LevelObjectKind.Exit;
            if (!point && (b.Width < 8 || b.Height < 8)) throw new FormatException("Solid objects must be at least 8 × 8 units.");
            if (item.Kind == LevelObjectKind.Enemy && (b.Width != 42 || b.Height != 34))
                throw new FormatException("The current enemy uses a 42 × 34 collision box.");
            if (Math.Abs(item.Travel) > 500 || Math.Abs(item.VerticalTravel) > 300 || Math.Abs(item.Phase) > 100)
                throw new FormatException("Object motion exceeds the supported range.");
            if (item.Kind == LevelObjectKind.Spawn) spawns++;
            if (item.Kind == LevelObjectKind.Exit) exits++;
            // Ground emits up to three plants per 72 units plus cliff/landmarks;
            // reserve 400 sprites for the background, actor, particles and HUD.
            budget += item.Kind == LevelObjectKind.Ground ? 12 + 3 * (int)(b.Width / 72) : 4;
        }
        if (spawns != 1 || exits != 1) throw new FormatException("A playable level needs exactly one spawn and one exit.");
        if (budget > 1600) throw new FormatException("This map exceeds the procedural artwork budget. Split it into smaller rooms.");
        Name = name; Biome = biome; _objects = objects.ToArray();
    }

    private static bool Finite(float a, float b, float c, float d, float e, float f, float g) =>
        float.IsFinite(a) && float.IsFinite(b) && float.IsFinite(c) && float.IsFinite(d) && float.IsFinite(e) && float.IsFinite(f) && float.IsFinite(g);

    public Level CreateLevel() => new(this);

    public static LevelDocument CreateStarter() => new("My first trail", 0,
    [
        new(LevelObjectKind.Spawn, new(140, 552, 0, 0)),
        new(LevelObjectKind.Ground, new(0, 600, 900, 500)),
        new(LevelObjectKind.Ground, new(1020, 600, 900, 500)),
        new(LevelObjectKind.Ledge, new(420, 504, 150, 24)),
        new(LevelObjectKind.Coin, new(490, 460, 0, 0)),
        new(LevelObjectKind.Checkpoint, new(1150, 600, 0, 0)),
        new(LevelObjectKind.Exit, new(1700, 600, 0, 0))
    ]);
}
