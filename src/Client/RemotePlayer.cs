using Microsoft.Xna.Framework;

namespace VoxelGame.Client;

/// <summary>Another player in the world, as this client sees them: where the server last said they were, smoothed for drawing.</summary>
public sealed class RemotePlayer
{
    public int Id { get; }
    public string Name { get; }

    /// <summary>Where to draw them (feet), eased toward the last position the server reported.</summary>
    public Vector3 Position { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public bool Flying { get; private set; }

    /// <summary>True once the server has told us where they are, so they can be drawn.</summary>
    public bool Visible { get; private set; }

    private Vector3 _target;
    private float _targetYaw, _targetPitch;

    public RemotePlayer(int id, string name)
    {
        Id = id;
        Name = name;
    }

    public void SetTarget(Vector3 position, float yaw, float pitch, bool flying)
    {
        _target = position;
        _targetYaw = yaw;
        _targetPitch = pitch;
        Flying = flying;
        if (!Visible)
        {
            Visible = true;
            Position = position; Yaw = yaw; Pitch = pitch;
        }
    }

    public void Update(float dt)
    {
        float k = 1 - MathF.Exp(-16 * dt);   // updates arrive 20 times a second; this fills the gaps without lagging far behind
        Position = Vector3.Lerp(Position, _target, k);
        Pitch += (_targetPitch - Pitch) * k;
        Yaw += MathHelper.WrapAngle(_targetYaw - Yaw) * k;
    }
}
