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

    public Vector3 Position { get; set; }
    public Vector3 Velocity { get; private set; }
    public bool OnGround { get; private set; }
    public bool IsFlying { get; set; }

    public Vector3 EyePosition => Position + new Vector3(0, EyeHeight, 0);

    public Player(Vector3 position)
    {
        Position = position;
    }

    private World _world;

    public void Update(float dt, InputState input, float yaw, World world)
    {
        _world = world;

        if (input.ToggleFlyPressed) { IsFlying = !IsFlying; Velocity = Vector3.Zero; }

        // Movement direction relative to where the camera is facing, flattened onto the ground.
        var forward = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        var wish = forward * input.Move.Y + right * input.Move.X;
        if (wish.LengthSquared() > 1) wish.Normalize();

        var v = Velocity;
        if (IsFlying)
        {
            float speed = input.Sprint ? FlySprintSpeed : FlySpeed;
            float vertical = (input.Jump ? 1 : 0) - (input.Sneak ? 1 : 0);
            v = wish * speed + Vector3.Up * vertical * speed;
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
        MoveAxis(Axis.X, Velocity.X * dt);
        MoveAxis(Axis.Z, Velocity.Z * dt);
        MoveAxis(Axis.Y, Velocity.Y * dt);
    }

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
                p.X = amount > 0 ? MathF.Floor(p.X + hw) - hw - Epsilon : MathF.Floor(p.X - hw) + 1 + hw + Epsilon;
                vel.X = 0;
                break;
            case Axis.Z:
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
