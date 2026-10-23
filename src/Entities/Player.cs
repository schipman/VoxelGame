using Microsoft.Xna.Framework;
using VoxelGame.Input;
using VoxelGame.Terrain;

namespace VoxelGame.Entities;

/// <summary>
/// The player: an axis-aligned box that walks, jumps and collides with solid blocks.
/// Position is the point at the player's feet, centred in the box.
/// </summary>
public sealed class Player
{
    private const float Width = 0.6f;
    private const float Height = 1.8f;
    private const float EyeHeight = 1.62f;

    private const float WalkSpeed = 4.3f;
    private const float SprintSpeed = 5.6f;
    private const float FlySpeed = 10f;
    private const float FlySprintSpeed = 24f;
    private const float JumpSpeed = 9f;
    private const float Gravity = 28f;
    private const float MaxFallSpeed = 50f;
    private const float Epsilon = 1e-4f;

    private const float SwimSpeed = 2.6f;
    private const float SwimSprintSpeed = 3.6f;
    private const float SwimVerticalSpeed = 3.6f;
    private const float FloatDepth = 1.2f;     // when floating, the water line is this far above the player's feet (head and shoulders out)

    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; private set; }
    public bool OnGround { get; private set; }
    public bool IsFlying { get; set; }

    /// <summary>Water reaches at least the player's waist: they swim instead of walking.</summary>
    public bool IsInWater { get; private set; }

    /// <summary>The player's eyes are under water.</summary>
    public bool IsSubmerged { get; private set; }

    /// <summary>World height of the water surface around the player (negative infinity if there is none).</summary>
    public float WaterSurfaceY { get; private set; } = float.NegativeInfinity;

    /// <summary>True for the one frame in which the player entered the water; EntrySpeed is how fast they were falling.</summary>
    public bool JustEnteredWater { get; private set; }
    public float EntrySpeed { get; private set; }

    public Vector3 EyePosition => Position + new Vector3(0, EyeHeight, 0);

    public Player(Vector3 position)
    {
        Position = position;
    }

    private World _world;
    private bool _hitWall;   // bumped into something sideways during the last move

    public void Update(float dt, InputState input, float yaw, World world)
    {
        _world = world;

        if (input.ToggleFlyPressed) { IsFlying = !IsFlying; Velocity = Vector3.Zero; }

        // Movement direction relative to where the camera is facing, flattened onto the ground.
        var forward = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        var wish = forward * input.Move.Y + right * input.Move.X;
        if (wish.LengthSquared() > 1) wish.Normalize();

        bool wasInWater = IsInWater;
        UpdateWaterState();
        JustEnteredWater = IsInWater && !wasInWater;
        EntrySpeed = JustEnteredWater ? MathF.Max(-Velocity.Y, 0f) : 0f;

        var v = Velocity;
        if (IsFlying)
        {
            float speed = input.Sprint ? FlySprintSpeed : FlySpeed;
            float vertical = (input.Jump ? 1 : 0) - (input.Sneak ? 1 : 0);
            v = wish * speed + Vector3.Up * vertical * speed;
        }
        else if (IsInWater)
        {
            // Swimming: slow, with little gravity. Space swims up, Shift dives; otherwise you float: a gentle spring
            // eases you toward the height where your head and shoulders are out of the water.
            float speed = input.Sprint ? SwimSprintSpeed : SwimSpeed;
            v.X = wish.X * speed;
            v.Z = wish.Z * speed;

            float buoyancy = Math.Clamp((WaterSurfaceY - FloatDepth - Position.Y) * 3f, -1.5f, 1.8f);
            float target = input.Jump ? SwimVerticalSpeed : input.Sneak ? -SwimVerticalSpeed : buoyancy;
            v.Y = MoveToward(v.Y, target, 16f * dt);

            // At the surface, jumping against a ledge hops you up onto it; otherwise you just tread water.
            if (input.Jump && !IsSubmerged)
                v.Y = _hitWall ? MathF.Max(v.Y, 6.5f) : MoveToward(v.Y, 0.5f, 16f * dt);
        }
        else
        {
            float speed = input.Sprint ? SprintSpeed : WalkSpeed;
            v.X = wish.X * speed;
            v.Z = wish.Z * speed;
            v.Y = MathF.Max(v.Y - Gravity * dt, -MaxFallSpeed);
            if (input.Jump && OnGround) v.Y = JumpSpeed;
        }
        Velocity = v;

        OnGround = false;
        _hitWall = false;
        MoveAxis(Axis.X, Velocity.X * dt);
        MoveAxis(Axis.Z, Velocity.Z * dt);
        MoveAxis(Axis.Y, Velocity.Y * dt);
    }

    /// <summary>Finds out how deep in water the player is, from the water surface above their feet or waist.</summary>
    private void UpdateWaterState()
    {
        int x = (int)MathF.Floor(Position.X), z = (int)MathF.Floor(Position.Z);
        float? surface = _world.WaterSurface(x, (int)MathF.Floor(Position.Y + 0.1f), z)
                      ?? _world.WaterSurface(x, (int)MathF.Floor(Position.Y + 0.6f), z);

        WaterSurfaceY = surface ?? float.NegativeInfinity;
        IsInWater = surface is { } s && s > Position.Y + 0.35f;
        IsSubmerged = surface is { } s2 && s2 > Position.Y + EyeHeight;
    }

    private static float MoveToward(float value, float target, float maxDelta) =>
        MathF.Abs(target - value) <= maxDelta ? target : value + MathF.Sign(target - value) * maxDelta;

    /// <summary>True if the player's box intersects the given block cell.</summary>
    public bool OverlapsBlock(int x, int y, int z)
    {
        const float hw = Width / 2;
        return Position.X + hw > x && Position.X - hw < x + 1
            && Position.Y + Height > y && Position.Y < y + 1
            && Position.Z + hw > z && Position.Z - hw < z + 1;
    }

    private enum Axis { X, Y, Z }

    /// <summary>Moves along one axis; on impact snaps flush against the block and zeroes that velocity.</summary>
    private void MoveAxis(Axis axis, float amount)
    {
        if (amount == 0) return;

        var p = Position;
        const float hw = Width / 2;
        switch (axis)
        {
            case Axis.X: p.X += amount; break;
            case Axis.Y: p.Y += amount; break;
            case Axis.Z: p.Z += amount; break;
        }

        if (!Collides(p))
        {
            Position = p;
            return;
        }

        var vel = Velocity;
        switch (axis)
        {
            case Axis.X:
                _hitWall = true;
                p.X = amount > 0 ? MathF.Floor(p.X + hw) - hw - Epsilon : MathF.Floor(p.X - hw) + 1 + hw + Epsilon;
                vel.X = 0;
                break;
            case Axis.Z:
                _hitWall = true;
                p.Z = amount > 0 ? MathF.Floor(p.Z + hw) - hw - Epsilon : MathF.Floor(p.Z - hw) + 1 + hw + Epsilon;
                vel.Z = 0;
                break;
            case Axis.Y:
                if (amount > 0) p.Y = MathF.Floor(p.Y + Height) - Height - Epsilon;
                else { p.Y = MathF.Floor(p.Y) + 1; OnGround = true; }
                vel.Y = 0;
                break;
        }
        Velocity = vel;
        Position = p;
    }

    private bool Collides(Vector3 feet)
    {
        const float hw = Width / 2;
        int x0 = (int)MathF.Floor(feet.X - hw), x1 = (int)MathF.Floor(feet.X + hw - Epsilon);
        int y0 = (int)MathF.Floor(feet.Y),      y1 = (int)MathF.Floor(feet.Y + Height - Epsilon);
        int z0 = (int)MathF.Floor(feet.Z - hw), z1 = (int)MathF.Floor(feet.Z + hw - Epsilon);

        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
            if (_world.IsSolid(x, y, z)) return true;
        return false;
    }
}
