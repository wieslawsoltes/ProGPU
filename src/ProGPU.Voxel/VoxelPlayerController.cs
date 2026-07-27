using System.Numerics;

namespace ProGPU.Voxel;

public readonly record struct VoxelPlayerInput(
    float Forward,
    float Strafe,
    float Vertical,
    bool Jump,
    bool Sprint);

/// <summary>
/// First-person kinematic controller with axis-separated voxel collision.
/// Update is O(B), where B is the small number of blocks overlapping the player AABB.
/// </summary>
public sealed class VoxelPlayerController
{
    private const float Radius = 0.3f;
    private const float Height = 1.8f;
    private const float EyeHeight = 1.62f;
    private const float Gravity = 24f;
    private const float JumpSpeed = 8.2f;

    public Vector3 Position { get; private set; }

    public Vector3 Velocity { get; private set; }

    public float Yaw { get; private set; }

    public float Pitch { get; private set; } = -0.15f;

    public bool IsGrounded { get; private set; }

    public bool IsFlying { get; private set; }

    public Vector3 EyePosition => Position + new Vector3(0, EyeHeight, 0);

    public Vector3 LookDirection
    {
        get
        {
            var cosPitch = MathF.Cos(Pitch);
            return Vector3.Normalize(new Vector3(
                MathF.Sin(Yaw) * cosPitch,
                MathF.Sin(Pitch),
                MathF.Cos(Yaw) * cosPitch));
        }
    }

    public void Teleport(Vector3 position, float yaw = 0f, float pitch = -0.15f)
    {
        Position = position;
        Velocity = Vector3.Zero;
        Yaw = yaw;
        Pitch = Math.Clamp(pitch, -1.52f, 1.52f);
        IsGrounded = false;
    }

    public void AddLook(float yawDelta, float pitchDelta)
    {
        Yaw += yawDelta;
        Pitch = Math.Clamp(Pitch + pitchDelta, -1.52f, 1.52f);
    }

    public void ToggleFlying()
    {
        IsFlying = !IsFlying;
        Velocity = Vector3.Zero;
    }

    public void Update(VoxelWorld world, in VoxelPlayerInput input, float elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(world);
        var delta = Math.Clamp(elapsedSeconds, 0f, 0.05f);
        if (delta <= 0f)
        {
            return;
        }

        var forward = new Vector3(MathF.Sin(Yaw), 0, MathF.Cos(Yaw));
        // Matrix4x4.CreateLookAt uses a right-handed view. With this controller's
        // +Z forward convention, screen-right is the negative horizontal
        // perpendicular of forward.
        var right = new Vector3(-forward.Z, 0, forward.X);
        var movement = forward * input.Forward + right * input.Strafe;
        if (movement.LengthSquared() > 1f)
        {
            movement = Vector3.Normalize(movement);
        }

        var speed = input.Sprint ? 9.5f : 5.5f;
        Velocity = new Vector3(movement.X * speed, Velocity.Y, movement.Z * speed);

        if (IsFlying)
        {
            Velocity = new Vector3(Velocity.X, input.Vertical * speed, Velocity.Z);
            Position += Velocity * delta;
            IsGrounded = false;
            return;
        }

        if (input.Jump && IsGrounded)
        {
            Velocity = new Vector3(Velocity.X, JumpSpeed, Velocity.Z);
            IsGrounded = false;
        }

        Velocity = new Vector3(Velocity.X, Velocity.Y - Gravity * delta, Velocity.Z);
        MoveAxis(world, 0, Velocity.X * delta);
        MoveAxis(world, 2, Velocity.Z * delta);
        IsGrounded = false;
        MoveAxis(world, 1, Velocity.Y * delta);
    }

    public bool IntersectsBlock(int x, int y, int z)
    {
        var playerMin = new Vector3(Position.X - Radius, Position.Y, Position.Z - Radius);
        var playerMax = new Vector3(Position.X + Radius, Position.Y + Height, Position.Z + Radius);
        return playerMin.X < x + 1 && playerMax.X > x &&
               playerMin.Y < y + 1 && playerMax.Y > y &&
               playerMin.Z < z + 1 && playerMax.Z > z;
    }

    private void MoveAxis(VoxelWorld world, int axis, float amount)
    {
        if (MathF.Abs(amount) < 1e-7f)
        {
            return;
        }

        var candidate = Position;
        if (axis == 0) candidate.X += amount;
        else if (axis == 1) candidate.Y += amount;
        else candidate.Z += amount;

        if (!Collides(world, candidate))
        {
            Position = candidate;
            return;
        }

        if (axis == 0)
        {
            Velocity = new Vector3(0, Velocity.Y, Velocity.Z);
        }
        else if (axis == 1)
        {
            if (amount < 0f)
            {
                IsGrounded = true;
            }
            Velocity = new Vector3(Velocity.X, 0, Velocity.Z);
        }
        else
        {
            Velocity = new Vector3(Velocity.X, Velocity.Y, 0);
        }
    }

    private static bool Collides(VoxelWorld world, Vector3 position)
    {
        const float epsilon = 0.0001f;
        var minX = (int)MathF.Floor(position.X - Radius);
        var maxX = (int)MathF.Floor(position.X + Radius - epsilon);
        var minY = (int)MathF.Floor(position.Y);
        var maxY = (int)MathF.Floor(position.Y + Height - epsilon);
        var minZ = (int)MathF.Floor(position.Z - Radius);
        var maxZ = (int)MathF.Floor(position.Z + Radius - epsilon);

        for (var y = minY; y <= maxY; y++)
        {
            for (var z = minZ; z <= maxZ; z++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    if (VoxelBlockCatalog.IsSolid(world.GetBlock(x, y, z)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
