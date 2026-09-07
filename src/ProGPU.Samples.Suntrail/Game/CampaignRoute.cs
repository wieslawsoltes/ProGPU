namespace ProGPU.Samples.Suntrail.Game;

internal enum EncounterKind { Open, Steps, Tunnel, Brambles, SawCrossing, FlameGate, CrusherHall }
internal readonly record struct RouteSection(float Width, float Elevation, float Gap, EncounterKind Encounter = EncounterKind.Open);

/// <summary>Original authored route scores. Distances are world units; generation is O(S) at level load.</summary>
internal static class CampaignRoute
{
    // Each score has its own spacing, rests, narrow crossings and encounter order.
    // Short islands have no generic gallery, while long rooms can carry upper routes.
    private static readonly RouteSection[][] Routes =
    [
        // Orchard: first steps, creek stones, a low passage, then a bramble meadow.
        [new(930,0,100), new(660,0,84,EncounterKind.Steps), new(280,48,76),
         new(720,48,96,EncounterKind.Tunnel), new(650,0,120), new(300,24,100),
         new(740,0,80,EncounterKind.Brambles), new(640,48,100), new(270,72,84),
         new(560,24,120,EncounterKind.Steps), new(850,0,0)],
        // Aqueduct: broad chambers separated by closely spaced broken arch piers.
        [new(930,0,90), new(720,32,100,EncounterKind.SawCrossing), new(240,64,80),
         new(280,96,90), new(740,48,120,EncounterKind.Tunnel), new(260,0,80),
         new(680,32,96,EncounterKind.Steps), new(760,80,105,EncounterKind.SawCrossing),
         new(280,112,80), new(320,64,100), new(600,24,120), new(850,0,0)],
        // Cathedral: climb into the galleries, descend under stone, climb back out.
        [new(930,0,100), new(660,48,90,EncounterKind.Steps), new(300,96,90),
         new(620,144,110,EncounterKind.FlameGate), new(700,96,80), new(320,48,100),
         new(760,0,90,EncounterKind.Tunnel), new(680,48,100,EncounterKind.CrusherHall),
         new(280,96,85), new(580,128,115,EncounterKind.FlameGate), new(850,64,0)],
        // Coast: low causeways and small, irregular stepping islands between ruins.
        [new(930,0,120), new(640,0,135), new(260,24,100), new(240,48,110),
         new(720,0,140,EncounterKind.SawCrossing), new(290,24,105), new(250,0,130),
         new(700,48,100,EncounterKind.Steps), new(300,80,115),
         new(660,32,130,EncounterKind.Tunnel), new(280,0,110), new(850,0,0)],
        // Highlands: long climbing terraces, bramble ridges and a sheltered descent.
        [new(930,0,90), new(680,48,80,EncounterKind.Brambles), new(320,96,80),
         new(650,144,100,EncounterKind.Steps), new(720,96,115), new(280,48,95),
         new(680,96,80,EncounterKind.Brambles), new(720,144,100,EncounterKind.SawCrossing),
         new(300,96,80), new(680,48,100,EncounterKind.Tunnel), new(850,0,0)],
        // Glacier: compact ascending snow pillars alternating with sheltered tunnels.
        [new(930,0,105), new(700,32,100,EncounterKind.Tunnel), new(250,80,85),
         new(280,128,90), new(700,80,115,EncounterKind.SawCrossing), new(300,24,90),
         new(650,64,95,EncounterKind.Steps), new(720,112,105,EncounterKind.Tunnel),
         new(260,64,100), new(600,24,120,EncounterKind.SawCrossing), new(850,0,0)],
        // Forge: alternation of flame timing, short rests and crusher chambers.
        [new(930,0,100), new(700,32,110,EncounterKind.FlameGate), new(300,80,90),
         new(680,48,100,EncounterKind.CrusherHall), new(720,0,120,EncounterKind.FlameGate),
         new(320,48,90), new(700,96,100,EncounterKind.CrusherHall), new(680,48,115),
         new(300,0,100), new(740,56,100,EncounterKind.FlameGate), new(850,0,0)],
        // Sky gardens: rising island runs and a final mixed sequence with landing rests.
        [new(930,0,105), new(700,48,110,EncounterKind.SawCrossing), new(280,96,90),
         new(320,144,105), new(760,96,120,EncounterKind.CrusherHall), new(280,48,100),
         new(650,96,90,EncounterKind.Steps), new(740,144,110,EncounterKind.FlameGate),
         new(280,96,100), new(680,48,110,EncounterKind.SawCrossing),
         new(320,0,90), new(680,48,115,EncounterKind.CrusherHall), new(850,0,0)]
    ];

    public static ReadOnlySpan<RouteSection> ForWorld(int world) => Routes[world];

    public static void AddEncounter(RouteSection section, float x, float y,
        List<Platform> platforms, List<Box> hazards, List<Mechanism> mechanisms)
    {
        float center = x + section.Width * .5f;
        switch (section.Encounter)
        {
            case EncounterKind.Steps:
                platforms.Add(new(new(center - 100, y - 48, 64, 48), PlatformKind.Stone));
                platforms.Add(new(new(center - 36, y - 88, 90, 88), PlatformKind.Stone));
                break;
            case EncounterKind.Tunnel:
                // A solid roof changes the jump envelope, with clear entrances and exits.
                platforms.Add(new(new(x + 145, y - 150, section.Width - 290, 36), PlatformKind.Stone));
                break;
            case EncounterKind.Brambles:
                hazards.Add(new(center - 100, y - 22, 48, 22));
                hazards.Add(new(center + 130, y - 22, 48, 22));
                break;
            case EncounterKind.SawCrossing:
                mechanisms.Add(new(new(center - 22, y - 42, 42, 42), MechanismKind.Saw, .4f, 48));
                break;
            case EncounterKind.FlameGate:
                mechanisms.Add(new(new(center, y - 90, 30, 90), MechanismKind.FlameJet, .23f));
                break;
            case EncounterKind.CrusherHall:
                mechanisms.Add(new(new(center, y - 275, 64, 80), MechanismKind.Crusher, .4f, 195));
                break;
        }
    }
}
