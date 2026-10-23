using Microsoft.Xna.Framework;

namespace VoxelGame.Entities;

/// <summary>First-person camera. Yaw 0 looks toward -Z; positive yaw turns left.</summary>
public sealed class Camera
{
    public Vector3 Position { get; set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }

    public float FieldOfView { get; set; } = MathHelper.ToRadians(70);
    public float AspectRatio { get; set; } = 16f / 9f;
    public float NearPlane { get; set; } = 0.05f;
    public float FarPlane { get; set; } = 500f;

    public Vector3 Forward => new(
        -MathF.Sin(Yaw) * MathF.Cos(Pitch),
        MathF.Sin(Pitch),
        -MathF.Cos(Yaw) * MathF.Cos(Pitch));

    public Matrix View => Matrix.CreateLookAt(Position, Position + Forward, Vector3.Up);
    public Matrix Projection => Matrix.CreatePerspectiveFieldOfView(FieldOfView, AspectRatio, NearPlane, FarPlane);

    /// <summary>Turns the camera by the given angles (radians).</summary>
    public void Rotate(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Pitch = MathHelper.Clamp(Pitch + deltaPitch, -MathHelper.ToRadians(89), MathHelper.ToRadians(89));
    }
}
