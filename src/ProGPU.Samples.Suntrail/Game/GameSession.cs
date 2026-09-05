using System.Numerics;

namespace ProGPU.Samples.Suntrail.Game;

public readonly record struct GameInput(float Move, bool JumpHeld, bool JumpPressed, bool Run, bool InteractPressed = false);

/// <summary>Allocation-free fixed 120 Hz simulation. Rendering never advances game state.</summary>
public sealed class GameSession
{
    public const float StepSeconds = 1f / 120f;
    public const float PlayerWidth = 30, PlayerHeight = 48;
    public Level Level { get; private set; } = new(0);
    public GameMode Mode { get; private set; } = GameMode.Title;
    public Vector2 Position { get; private set; } = new(140, 552);
    public Vector2 PreviousPosition { get; private set; } = new(140, 552);
    public Vector2 Velocity { get; private set; }
    public Particle[] Particles { get; } = new Particle[128];
    public int Hearts { get; private set; } = 3;
    public int Coins { get; private set; }
    public int Relics { get; private set; }
    public int Deaths { get; private set; }
    public int UnlockedLevel { get; private set; }
    public int CheckpointIndex { get; private set; } = -1;
    public int Facing { get; private set; } = 1;
    public bool Grounded { get; private set; }
    public float Time { get; private set; }
    public float Invulnerability { get; private set; }
    public float CameraX { get; private set; }
    public float CameraY { get; private set; }
    public long Tick { get; private set; }
    public uint Revision { get; private set; }
    public float ViewWidth { get; set; } = 1280;
    public float Interpolation => (float)(_accumulator / StepSeconds);
    private double _accumulator;
    private float _coyote, _jumpBuffer;
    private int _standingPlatform = -1, _particleCursor;
    private bool _jumpQueued;
    private bool _interactQueued;
    private Level? _overworld, _dungeon;
    private Vector2 _pipeReturn;
    private Vector2 _respawn = new(140, 552);
    private uint _random = 0x53554e31;
    public Box PlayerBounds => new(Position.X, Position.Y, PlayerWidth, PlayerHeight);

    public void StartLevel(int index)
    {
        Level = new(index); Position = PreviousPosition = _respawn = Level.Spawn;
        _overworld = Level; _dungeon = new(index, true); _interactQueued = false;
        Velocity = Vector2.Zero; Hearts = 3; Coins = Relics = 0; Time = 0; Tick = 0;
        CheckpointIndex = -1; CameraX = CameraY = 0; Invulnerability = 0;
        Grounded = false; _standingPlatform = -1; _accumulator = 0; _coyote = _jumpBuffer = 0; _jumpQueued = false;
        Array.Clear(Particles); _random = 0x53554e31u + (uint)index;
        Mode = GameMode.Playing; Revision++;
    }

    public void SetUnlockedLevel(int value) => UnlockedLevel = Math.Clamp(value, 0, Level.Names.Length - 1);
    public void TogglePause()
    {
        if (Mode == GameMode.Playing) Mode = GameMode.Paused;
        else if (Mode == GameMode.Paused) Mode = GameMode.Playing;
        _jumpQueued = _interactQueued = false; _jumpBuffer = 0; _accumulator = 0; Revision++;
    }
    public void ShowTitle() { Mode = GameMode.Title; Revision++; }
    public void Continue()
    {
        switch (Mode)
        {
            case GameMode.Title: StartLevel(UnlockedLevel); break;
            case GameMode.Paused: TogglePause(); break;
            case GameMode.Fallen: Respawn(); break;
            case GameMode.LevelComplete: StartLevel(Level.Index + 1); break;
            case GameMode.Complete: StartLevel(0); break;
        }
    }
    public void Respawn()
    {
        if (Level.IsDungeon && _overworld is not null) Level = _overworld;
        Position = PreviousPosition = _respawn; Velocity = Vector2.Zero; Hearts = 3;
        Invulnerability = 1.5f; Grounded = false; _standingPlatform = -1;
        _jumpBuffer = _coyote = 0; _jumpQueued = _interactQueued = false; _accumulator = 0;
        CameraX = Math.Clamp(Position.X - ViewWidth * .32f, 0, Math.Max(0, Level.Width - ViewWidth));
        Mode = GameMode.Playing; Revision++;
    }

    public void Advance(float elapsed, GameInput input)
    {
        if (Mode != GameMode.Playing || !float.IsFinite(elapsed) || elapsed <= 0) return;
        _jumpQueued |= input.JumpPressed;
        _interactQueued |= input.InteractPressed;
        _accumulator += Math.Min(elapsed, .1f);
        int steps = 0;
        while (_accumulator >= StepSeconds && steps++ < 12 && Mode == GameMode.Playing)
        {
            Step(input with { JumpPressed = _jumpQueued, InteractPressed = _interactQueued });
            _jumpQueued = _interactQueued = false;
            _accumulator -= StepSeconds;
        }
    }

    public void Step(GameInput input)
    {
        if (Mode != GameMode.Playing) return;
        const float dt = StepSeconds;
        PreviousPosition = Position; float oldTime = Time;
        Time = ++Tick * dt; Revision++;
        if (input.InteractPressed && TryUsePipe()) return;
        Invulnerability = Math.Max(0, Invulnerability - dt);
        _jumpBuffer = input.JumpPressed ? .13f : Math.Max(0, _jumpBuffer - dt);
        _coyote = Grounded ? .11f : Math.Max(0, _coyote - dt);
        if (_standingPlatform >= 0)
        {
            var platform = Level.Platforms[_standingPlatform];
            var current = platform.At(Time); var previous = platform.At(oldTime);
            Position += new Vector2(current.X - previous.X, current.Y - previous.Y);
        }
        float move = float.IsFinite(input.Move) ? Math.Clamp(input.Move, -1, 1) : 0;
        float speed = input.Run ? 390 : 300;
        float target = move * speed;
        Velocity = new(Approach(Velocity.X, target, (Grounded ? 2300 : 1400) * dt), Velocity.Y);
        if (Math.Abs(move) > .1f) Facing = move > 0 ? 1 : -1;
        if (_jumpBuffer > 0 && _coyote > 0)
        {
            Velocity = new(Velocity.X, -720); Grounded = false;
            _jumpBuffer = _coyote = 0; _standingPlatform = -1;
            Burst(Position + new Vector2(15, 48), 8, 0);
        }
        if (!input.JumpHeld && Velocity.Y < -270) Velocity = new(Velocity.X, Velocity.Y + 2600 * dt);
        Velocity = new(Velocity.X, Math.Min(980, Velocity.Y + 1760 * dt));
        MoveAndCollide(dt);
        for (int i = 0; i < Level.Pickups.Length; i++)
        {
            ref var coin = ref Level.Pickups[i];
            if (coin.Collected || !PlayerBounds.Intersects(new(coin.Position.X - 15, coin.Position.Y - 15, 30, 30))) continue;
            coin.Collected = true; if (coin.IsRelic) Relics++; else Coins++;
            Burst(coin.Position, coin.IsRelic ? 20 : 6, 1);
        }
        for (int i = 0; i < Level.Enemies.Length; i++)
        {
            ref var enemy = ref Level.Enemies[i];
            if (enemy.Defeated) continue;
            enemy.Position.X += enemy.Speed * dt;
            if (enemy.Position.X < enemy.Left) { enemy.Position.X = enemy.Left; enemy.Speed = Math.Abs(enemy.Speed); }
            if (enemy.Position.X > enemy.Right) { enemy.Position.X = enemy.Right; enemy.Speed = -Math.Abs(enemy.Speed); }
            if (!PlayerBounds.Intersects(new(enemy.Position.X, enemy.Position.Y, 42, 34))) continue;
            if (Velocity.Y > 0 && PreviousPosition.Y + PlayerHeight <= enemy.Position.Y + 12)
            {
                enemy.Defeated = true; Velocity = new(Velocity.X, input.JumpHeld ? -560 : -410);
                Burst(enemy.Position + new Vector2(20, 15), 12, 2);
            }
            else Damage(enemy.Position.X + 21);
        }
        foreach (var hazard in Level.Hazards) if (PlayerBounds.Intersects(hazard)) Damage(hazard.X + hazard.Width / 2);
        foreach (var mechanism in Level.Mechanisms)
        {
            if (!mechanism.IsDangerous(Time)) continue;
            var bounds = mechanism.At(Time);
            if (PlayerBounds.Intersects(bounds)) Damage(bounds.X + bounds.Width / 2);
        }
        for (int i = CheckpointIndex + 1; i < Level.Checkpoints.Length; i++)
        {
            var checkpoint = Level.Checkpoints[i];
            // A lantern also serves the room's upper route. Always retain its
            // authored safe floor as the respawn point, even when crossing above it.
            float checkpointHeight = Position.Y + PlayerHeight - checkpoint.Y;
            if (Math.Abs(Position.X - checkpoint.X) > 38 || checkpointHeight < -220 || checkpointHeight > 70) continue;
            CheckpointIndex = i; _respawn = new(checkpoint.X, checkpoint.Y - PlayerHeight);
            Hearts = 3; Burst(_respawn, 20, 1);
        }
        if (Position.Y > 1080) Die();
        if (!Level.IsDungeon && PlayerBounds.Intersects(new(Level.Exit.X - 15, Level.Exit.Y - 150, 100, 150)))
        {
            Mode = Level.Index == Level.Names.Length - 1 ? GameMode.Complete : GameMode.LevelComplete;
            UnlockedLevel = Math.Max(UnlockedLevel, Math.Min(Level.Index + 1, Level.Names.Length - 1));
            Burst(Position, 80, 1);
        }
        float cameraTarget = Math.Clamp(Position.X - ViewWidth * .34f + Velocity.X * .15f, 0, Math.Max(0, Level.Width - ViewWidth));
        CameraX += (cameraTarget - CameraX) * (1 - MathF.Exp(-5.5f * dt));
        CameraY += (Math.Clamp(Position.Y - 460, Level.IsDungeon ? -320 : -155, Level.IsDungeon ? 140 : 30) - CameraY) * (1 - MathF.Exp(-3 * dt));
        for (int i = 0; i < Particles.Length; i++)
        {
            ref var p = ref Particles[i]; if (p.Life <= 0) continue;
            p.Life -= dt; p.Position += p.Velocity * dt; p.Velocity.Y += 460 * dt;
        }
        if (Grounded && Math.Abs(Velocity.X) > 180 && Tick % 9 == 0) Burst(Position + new Vector2(15, 47), 1, 0);
    }

    private void MoveAndCollide(float dt)
    {
        Position += new Vector2(Velocity.X * dt, 0);
        foreach (var platform in Level.Platforms)
        {
            if (platform.Kind is PlatformKind.Ledge or PlatformKind.Moving) continue;
            var b = platform.At(Time); if (!PlayerBounds.Intersects(b)) continue;
            Position = new(Velocity.X > 0 ? b.X - PlayerWidth : b.Right, Position.Y);
            Velocity = new(0, Velocity.Y);
        }
        Position = new(Math.Max(0, Position.X), Position.Y);
        float oldBottom = Position.Y + PlayerHeight;
        Position += new Vector2(0, Velocity.Y * dt); Grounded = false; _standingPlatform = -1;
        for (int i = 0; i < Level.Platforms.Length; i++)
        {
            var platform = Level.Platforms[i]; var b = platform.At(Time);
            if (!PlayerBounds.Intersects(b)) continue;
            if (Velocity.Y >= 0 && oldBottom <= b.Y + 1)
            {
                Position = new(Position.X, b.Y - PlayerHeight); Velocity = new(Velocity.X, 0);
                Grounded = true; _standingPlatform = i;
            }
            else if (Velocity.Y < 0 && platform.Kind is not (PlatformKind.Ledge or PlatformKind.Moving))
            {
                Position = new(Position.X, b.Bottom); Velocity = new(Velocity.X, 0);
            }
        }
    }
    public bool CanUsePipe
    {
        get
        {
            if (!Grounded || Mode != GameMode.Playing) return false;
            foreach (var pipe in Level.Pipes)
                if (Position.X >= pipe.X && Position.X + PlayerWidth <= pipe.Right && Math.Abs(Position.Y + PlayerHeight - pipe.Y) < 2) return true;
            return false;
        }
    }
    private bool TryUsePipe()
    {
        if (!CanUsePipe || _overworld is null || _dungeon is null) return false;
        if (Level.IsDungeon) { Level = _overworld; Position = _pipeReturn; }
        else { _pipeReturn = Position; Level = _dungeon; Position = Level.Spawn; }
        PreviousPosition = Position; Velocity = Vector2.Zero; Grounded = false;
        _standingPlatform = -1; _coyote = _jumpBuffer = 0;
        _jumpQueued = _interactQueued = false;
        CameraX = Math.Clamp(Position.X - ViewWidth * .34f, 0, Math.Max(0, Level.Width - ViewWidth));
        CameraY = Math.Clamp(Position.Y - 460, -320, 140);
        Invulnerability = Math.Max(Invulnerability, .5f);
        Array.Clear(Particles); Revision++;
        return true;
    }
    private void Damage(float fromX)
    {
        if (Invulnerability > 0 || Mode != GameMode.Playing) return;
        Hearts--; Invulnerability = 1.6f; Velocity = new(Position.X + 15 < fromX ? -250 : 250, -320);
        Burst(Position, 12, 2); if (Hearts <= 0) Die();
    }
    private void Die() { if (Mode == GameMode.Playing) { Mode = GameMode.Fallen; Deaths++; Revision++; } }
    private static float Approach(float value, float target, float step) => value < target ? Math.Min(value + step, target) : Math.Max(value - step, target);
    private float RandomUnit() { _random ^= _random << 13; _random ^= _random >> 17; _random ^= _random << 5; return (_random & 65535) / 65535f; }
    private void Burst(Vector2 position, int count, int kind)
    {
        for (int i = 0; i < count; i++)
        {
            ref var p = ref Particles[_particleCursor++ % Particles.Length];
            p = new() { Position = position, Velocity = new((RandomUnit() - .5f) * 230, -80 - RandomUnit() * 220), Life = .45f + RandomUnit() * .4f, Kind = kind };
            p.MaxLife = p.Life;
        }
    }
}
