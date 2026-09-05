using System.Numerics;
using ProGPU.Samples.Suntrail.Game;
using ProGPU.Samples.Suntrail.Rendering;
using Xunit;

namespace ProGPU.Samples.Suntrail.Tests;

public sealed class SimulationTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void EveryIslandCanBeCompletedWithOnlyOrdinaryInputs(int level)
    {
        var game = new GameSession(); game.StartLevel(level);
        var trace = new System.Text.StringBuilder();
        for (int tick = 0; tick < 24000 && game.Mode == GameMode.Playing; tick++)
        {
            var input = RoutePilot.GetInput(game);
            if(input.JumpPressed) trace.AppendLine($"jump {game.Position} v={game.Velocity}");
            bool grounded = game.Grounded;
            game.Step(input);
            if(game.Grounded && !grounded) trace.AppendLine($"land {game.Position}");
        }
        Assert.True(game.Mode is GameMode.LevelComplete or GameMode.Complete,
            $"Level {level + 1}: mode={game.Mode}, position={game.Position}, hearts={game.Hearts}, time={game.Time}\n{trace}");
        Assert.Equal(0, game.Deaths);
        Assert.Equal(1, game.CheckpointIndex);
        Assert.True(game.Coins > 0);
    }

    [Fact]
    public void ReplayAndArtworkAreDeterministic()
    {
        var a = new GameSession(); var b = new GameSession(); a.StartLevel(3); b.StartLevel(3);
        for (int i = 0; i < 4000; i++) { var input = RoutePilot.GetInput(a); a.Step(input); b.Step(input); }
        Assert.Equal(a.Position, b.Position); Assert.Equal(a.Velocity, b.Velocity); Assert.Equal(a.Coins, b.Coins);
        var first = new ProceduralBatch(); var second = new ProceduralBatch();
        first.Build(a,new(1280,800),a.Time); second.Build(b,new(1280,800),b.Time);
        Assert.True(first.Sprites.SequenceEqual(second.Sprites));
    }
    [Fact]
    public void FixedStepIsIndependentOfPresentationRate()
    {
        var a=new GameSession(); var b=new GameSession(); a.StartLevel(0); b.StartLevel(0);
        var input = new GameInput(1,false,false,false);
        for(int i=0;i<120;i++) a.Advance(GameSession.StepSeconds,input);
        for(int i=0;i<30;i++) b.Advance(GameSession.StepSeconds*4,input);
        Assert.Equal(a.Tick,b.Tick); Assert.Equal(a.Position,b.Position);
    }
    [Fact]
    public void HoldingJumpProducesAHigherArc()
    {
        float FullJump(bool held)
        {
            var g=new GameSession();g.StartLevel(0);
            for(int i=0;i<100;i++) g.Step(default);
            float start=g.Position.Y, highest=start;
            for(int i=0;i<110;i++) {g.Step(new(0,held,i==0,false));highest=Math.Min(highest,g.Position.Y);}
            return start-highest;
        }
        Assert.True(FullJump(true)>FullJump(false)+45);
    }
    [Fact]
    public void PausedStateAndResumeDoNotAccumulateTime()
    {
        var g=new GameSession();g.StartLevel(0);g.Step(default);g.TogglePause();
        var position=g.Position;var tick=g.Tick;
        g.Advance(100,new(1,true,true,true));
        Assert.Equal(position,g.Position);Assert.Equal(tick,g.Tick);
        g.TogglePause();g.Advance(GameSession.StepSeconds,default);Assert.Equal(tick+1,g.Tick);
    }
    [Fact]
    public void CheckpointRespawnKeepsCollectiblesAndRestoresHealth()
    {
        var g=new GameSession();g.StartLevel(0);
        for(int i=0;i<8000 && g.CheckpointIndex<0;i++)g.Step(RoutePilot.GetInput(g));
        Assert.Equal(0,g.CheckpointIndex);int coins=g.Coins;
        g.Respawn();
        Assert.Equal(g.Level.Checkpoints[0].X,g.Position.X);
        Assert.Equal(coins,g.Coins);Assert.Equal(3,g.Hearts);Assert.Equal(GameMode.Playing,g.Mode);
    }
    [Fact]
    public void WarmSimulationAndBatchConstructionAllocateNoManagedMemory()
    {
        var g=new GameSession();g.StartLevel(0);var batch=new ProceduralBatch();
        for(int i=0;i<300;i++){g.Step(default);batch.Build(g,new(1440,900),g.Time);}
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++){g.Step(default);batch.Build(g,new(1440,900),g.Time);}
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
        Assert.InRange(batch.Count,30,ProceduralBatch.Capacity);
    }
    [Fact]
    public void WorldsHaveDistinctRoutesAndDifferentMovingPlatformAxes()
    {
        var signatures = new HashSet<string>();
        for (int i = 0; i < Level.Names.Length; i++)
        {
            var level = new Level(i);
            Assert.Equal(i, level.Biome);
            signatures.Add(string.Join(";", level.Platforms.Where(p => p.Kind == PlatformKind.Ground).Select(p => p.Bounds)));
            Assert.Contains(level.Platforms, p => p.Kind == PlatformKind.Moving);
            Assert.Equal(i is 2 or 5, level.Platforms.Any(p => p.VerticalTravel != 0));
        }
        Assert.Equal(8, signatures.Count);
        Assert.Equal(8, Level.Regions.Distinct().Count());
    }

    [Fact]
    public void VerticalLiftCarriesAStandingPlayerWithoutDroppingContact()
    {
        var game = new GameSession(); game.StartLevel(2);
        // Move an existing original lift under the spawn; exercise real collision and carry.
        game.Level.Platforms[0] = new(new(0, 600, 930, 24), PlatformKind.Moving, 0, 0, 24);
        for (int i = 0; i < 120; i++) game.Step(default);
        Assert.True(game.Grounded);
        for (int i = 0; i < 700; i++)
        {
            game.Step(default);
            Assert.True(game.Grounded);
            Assert.InRange(Math.Abs(game.Position.Y + GameSession.PlayerHeight - game.Level.Platforms[0].At(game.Time).Y), 0, .001f);
        }
    }

    [Fact]
    public void LevelGenerationStaysBoundedAndHasThreeOptionalRelics()
    {
        for(int i=0;i<8;i++)
        {
            var l=new Level(i);Assert.Equal(3,l.Pickups.Count(p=>p.IsRelic));
            Assert.Equal(2,l.Checkpoints.Length);Assert.InRange(l.Platforms.Length,20,48);
        }
    }
}
