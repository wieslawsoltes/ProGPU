using System.Numerics;

namespace ProGPU.Samples.Suntrail.Game;

public enum GameMode { Title, Playing, Paused, Fallen, LevelComplete, Complete }
public enum PlatformKind { Ground, Ledge, Moving, Crate, Pipe, Stone }
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
public enum MechanismKind { Saw, FlameJet, Crusher }
public readonly record struct Mechanism(Box Bounds, MechanismKind Kind, float Phase = 0, float Travel = 0)
{
    public float Cycle(float time) => (time / 3.2f + Phase) - MathF.Floor(time / 3.2f + Phase);
    public bool IsDangerous(float time) => Kind != MechanismKind.FlameJet || Cycle(time) is > .30f and < .64f;
    public Box At(float time) => Kind switch
    {
        MechanismKind.Saw => Bounds with { X = Bounds.X + MathF.Sin(time * 1.8f + Phase) * Travel },
        // Slow retraction, brief held warning, rapid drop, then a grounded pause.
        MechanismKind.Crusher => Bounds with { Y = Bounds.Y + Travel * Drop(Cycle(time)) },
        _ => Bounds
    };
    private static float Drop(float t) => t < .40f ? 1 - t / .40f : t < .65f ? 0 : t < .75f ? (t - .65f) / .10f : 1;
}
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
    public int Index { get; }
    public bool IsDungeon { get; }
    public int Biome => Index;
    public Box[] Pipes { get; }
    public Mechanism[] Mechanisms { get; }
    public Platform[] Platforms { get; }
    public Pickup[] Pickups { get; }
    public Enemy[] Enemies { get; }
    public Box[] Hazards { get; }
    public Checkpoint[] Checkpoints { get; }
    public Vector2 Spawn { get; } = new(140, 530);
    public Vector2 Exit { get; }
    public float Width => Exit.X + 500;
    public int CoinCount { get; }

    public Level(int index, bool isDungeon = false)
    {
        if ((uint)index >= Names.Length) throw new ArgumentOutOfRangeException(nameof(index));
        Index = index;
        IsDungeon = isDungeon;
        var platforms = new List<Platform>();
        var pickups = new List<Pickup>();
        var enemies = new List<Enemy>();
        var hazards = new List<Box>();
        var checkpoints = new List<Checkpoint>();
        var mechanisms = new List<Mechanism>();
        if (isDungeon)
        {
            // Each vault has its own silhouette: descent, terraces, central trench,
            // ascending gallery, two pits, low tunnel, furnace steps, and sky crypt.
            int[][] heights = [[600, 650, 650, 600], [600, 540, 480, 540, 600],
                [600, 600, 690, 600], [600, 520, 440, 520, 600],
                [600, 660, 580, 660, 600], [600, 600, 600, 600],
                [600, 520, 580, 500, 600], [600, 520, 440, 360, 440, 520, 600]];
            float roomX = 0;
            for (int room = 0; room < heights[index].Length; room++)
            {
                float floorY = heights[index][room];
                float span = room == 0 ? 880 : 380 + ((index + room * 3) % 4) * 60;
                platforms.Add(new(new(roomX, floorY, span, 510), PlatformKind.Ground));
                if (room > 0)
                {
                    platforms.Add(new(new(roomX + 75, floorY - 92, 150, 24), PlatformKind.Ledge));
                    if (index is 2 or 4 or 6)
                        hazards.Add(new(roomX + span - 160, floorY - 22, 58, 22));
                    if (index is 1 or 5)
                        platforms.Add(new(new(roomX + 280, floorY - 54, 54, 54), PlatformKind.Stone));
                    // Optional high galleries have hazards distinct from the lower coin route.
                    if (index is 1 or 3 or 5)
                        mechanisms.Add(new(new(roomX + 138, floorY - 140, 42, 42), MechanismKind.Saw, room * .3f, 40));
                    else if (index is 2 or 6)
                        mechanisms.Add(new(new(roomX + 175, floorY - 185, 28, 70), MechanismKind.FlameJet, room * .2f));
                    else if (index == 7)
                        mechanisms.Add(new(new(roomX + 140, floorY - 330, 52, 70), MechanismKind.Crusher, room * .17f, 150));
                }
                for (int coin = 0; coin < 6; coin++)
                    pickups.Add(new() { Position = new(roomX + 160 + coin * 32, floorY - 58 - MathF.Sin(coin * MathF.PI / 5) * 50) });
                roomX += span + (index == 5 ? 70 : 104);
            }
            float end = roomX - (index == 5 ? 70 : 104) - 200;
            // A two-way entrance allows a player to leave the optional room at once.
            Pipes = [new(65, 504, 100, 96), new(end, 504, 100, 96)];
            foreach (var pipe in Pipes) platforms.Add(new(pipe, PlatformKind.Pipe));
            // Ceiling does not intersect the highest authored jump route.
            platforms.Add(new(new(0, 130, roomX, 44), PlatformKind.Stone));
            Spawn = new(210, 530); Exit = new(end + 100, 600);
            Platforms = platforms.ToArray(); Pickups = pickups.ToArray(); Enemies = [];
            Hazards = hazards.ToArray(); Checkpoints = [];
            Mechanisms = mechanisms.ToArray();
            CoinCount = Pickups.Length;
            return;
        }
        float x = 0;
        float lastY = 600;
        var route = CampaignRoute.ForWorld(index);
        int sections = route.Length;
        for (int section = 0; section < sections; section++)
        {
            bool first = section == 0, last = section == sections - 1;
            int rhythm = (section * 5 + index * 3) % 4;
            var score = route[section];
            float width = score.Width;
            float y = 600 - score.Elevation;
            platforms.Add(new(new(x, y, width, 510), PlatformKind.Ground));
            if (section == 3 || section == sections - 4) checkpoints.Add(new(x + (score.Encounter == EncounterKind.Tunnel ? width * .5f : 95), y));
            if (!first && !last && width >= 520 && score.Encounter != EncounterKind.Tunnel)
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
                    if (index is 1 or 4 or 5)
                        mechanisms.Add(new(new(x + 322, y - 260, 40, 40), MechanismKind.Saw, section * .21f, 55));
                    else if (index is 2 or 6)
                        mechanisms.Add(new(new(x + 440, y - 268, 30, 80), MechanismKind.FlameJet, section * .13f));
                    else if (index == 7)
                        mechanisms.Add(new(new(x + 430, y - 405, 56, 80), MechanismKind.Crusher, section * .17f, 120));
                }
                else if (index is 1 or 2 or 7 || rhythm == 0)
                {
                    // A staircase of optional upper shelves breaks up the ground route.
                    platforms.Add(new(new(x + 292, y - 150, 112, 24), PlatformKind.Ledge));
                    platforms.Add(new(new(x + width - 230, y - 83, 104, 24), PlatformKind.Ledge));
                }
                else platforms.Add(new(new(x + 305, y - 54, 54, 54), PlatformKind.Crate));
                if (score.Encounter == EncounterKind.Open && (rhythm != 2 || index >= 4))
                    enemies.Add(new() { Position = new(x + width - 125, y - 34), Left = x + width - 205, Right = x + width - 45, Speed = -(50 + index * 7) });
                if (score.Encounter == EncounterKind.Open && ((index >= 2 && rhythm == 2) || (index == 6 && section % 2 == 0)))
                    hazards.Add(new(x + 255, y - 22, index == 6 ? 76 : 52, 22));
            }
            if (score.Encounter == EncounterKind.Tunnel)
            {
                // Side steps reach the roof without trapping the lower walking route.
                platforms.Add(new(new(x + 50, y - 75, 70, 24), PlatformKind.Ledge));
                platforms.Add(new(new(x + width - 120, y - 75, 70, 24), PlatformKind.Ledge));
                if (section is 1 or 4 or 7)
                    pickups.Add(new() { Position = new(x + width * .5f, y - 195), IsRelic = true });
            }
            CampaignRoute.AddEncounter(score, x, y, platforms, hazards, mechanisms);
            int coinRow = first ? 5 : Math.Min(4 + rhythm, Math.Max(3, (int)(width - 150) / 35));
            for (int c = 0; c < coinRow; c++)
                pickups.Add(new() { Position = new(x + (first ? 370 : 135) + c * 35,
                    y - (first || score.Encounter == EncounterKind.Tunnel ? 65 : 140) - MathF.Sin(c * MathF.PI / (coinRow - 1)) * 26) });
            if (!last)
            {
                float gap = score.Gap;
                for (int c = 0; c < 3; c++)
                    pickups.Add(new() { Position = new(x + width - 20 + c * (gap + 40) / 2,
                        y - 100 - MathF.Sin(c * MathF.PI / 2) * 35) });
                x += width + gap;
            }
            lastY = y;
        }
        Pipes = [new(580, 504, 100, 96)];
        Mechanisms = mechanisms.ToArray();
        platforms.Add(new(Pipes[0], PlatformKind.Pipe));
        Platforms = platforms.ToArray(); Pickups = pickups.ToArray(); Enemies = enemies.ToArray();
        Hazards = hazards.ToArray(); Checkpoints = checkpoints.ToArray();
        Exit = new(x + 615, lastY);
        CoinCount = Pickups.Count(p => !p.IsRelic);
    }
}
