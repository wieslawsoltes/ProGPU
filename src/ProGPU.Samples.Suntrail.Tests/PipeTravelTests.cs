using ProGPU.Samples.Suntrail.Game;
using Xunit;

namespace ProGPU.Samples.Suntrail.Tests;

public sealed class PipeTravelTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void VaultCanBeEnteredTraversedAndLeftUsingNormalControls(int world)
    {
        var game = new GameSession(); game.StartLevel(world);
        var outside = game.Level;
        ReachPipe(game, outside.Pipes[0]);
        var returnPosition = game.Position;
        game.Step(new(0, false, false, false, true));
        Assert.True(game.Level.IsDungeon);
        var vault = game.Level;
        var farPipe = vault.Pipes[^1];
        for (int tick = 0; tick < 16000 && game.Position.X < farPipe.X - 185 && game.Mode == GameMode.Playing; tick++)
            game.Step(RoutePilot.GetInput(game));
        Assert.True(game.Mode == GameMode.Playing, $"World {world}, {game.Position}, hearts={game.Hearts}, time={game.Time}");
        ReachPipe(game, farPipe);
        Assert.True(game.Coins > 0);
        int coins = game.Coins;
        game.Step(new(0, false, false, false, true));
        Assert.Same(outside, game.Level); Assert.Equal(returnPosition, game.Position);
        Assert.Equal(coins, game.Coins); Assert.Equal(0, game.Deaths);
        // The same vault instance retains collected items across visits.
        ReachPipe(game, outside.Pipes[0]);
        game.Step(new(0, false, false, false, true));
        Assert.Same(vault, game.Level);
        Assert.Contains(vault.Pickups, pickup => pickup.Collected);
        game.Respawn();
        Assert.Same(outside, game.Level); Assert.Equal(coins, game.Coins);
    }

    [Fact]
    public void TimedMechanismsRepeatAndFlamesWarnBeforeBecomingDangerous()
    {
        var flame = new Mechanism(new(100, 400, 30, 80), MechanismKind.FlameJet);
        Assert.False(flame.IsDangerous(.20f * 3.2f));
        Assert.True(flame.IsDangerous(.40f * 3.2f));
        Assert.False(flame.IsDangerous(.80f * 3.2f));
        var crusher = new Mechanism(new(100, 200, 60, 80), MechanismKind.Crusher, 0, 120);
        Assert.Equal(200, crusher.At(.50f * 3.2f).Y);
        Assert.Equal(320, crusher.At(.80f * 3.2f).Y);
        Assert.InRange(Math.Abs(crusher.At(.71f * 3.2f).Y - crusher.At(1.71f * 3.2f).Y), 0, .001f);
        var saw = new Mechanism(new(100, 200, 40, 40), MechanismKind.Saw, 0, 55);
        for (int tick = 0; tick < 1000; tick++) Assert.InRange(saw.At(tick / 120f).X, 45, 155);
    }

    [Fact]
    public void InteractAwayFromPipeDoesNotTeleportOrAllocate()
    {
        var game = new GameSession(); game.StartLevel(0);
        for (int i = 0; i < 120; i++) game.Step(default);
        var position = game.Position; var level = game.Level;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) game.Step(new(0, false, false, false, true));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated); Assert.Equal(position, game.Position); Assert.Same(level, game.Level);
    }

    private static void ReachPipe(GameSession game, Box pipe)
    {
        float target = pipe.X + (pipe.Width - GameSession.PlayerWidth) / 2;
        for (int tick = 0; tick < 2000 && !game.CanUsePipe && game.Mode == GameMode.Playing; tick++)
        {
            float distance = target - game.Position.X;
            game.Step(new(Math.Clamp(distance * .07f, -1, 1), true,
                game.Grounded && Math.Abs(distance) < 190, false));
        }
        Assert.True(game.CanUsePipe, $"Could not reach {pipe}: {game.Position}, {game.Mode}");
    }
}
