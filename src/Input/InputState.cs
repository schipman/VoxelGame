using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace VoxelGame.Input;

/// <summary>
/// One place that turns raw keyboard/mouse state into game-level input. Systems read this
/// instead of polling MonoGame directly, so rebinding keys later only touches this file.
/// </summary>
public sealed class InputState
{
    private KeyboardState _keys, _prevKeys;
    private MouseState _mouse, _prevMouse;
    private bool _wasCaptured;
    private int _settleFrames;

    /// <summary>X = strafe (right is +), Y = forward (+).</summary>
    public Vector2 Move { get; private set; }
    public Vector2 MouseDelta { get; private set; }
    public bool Jump { get; private set; }
    public bool Sprint { get; private set; }
    public bool Sneak { get; private set; }

    public bool ToggleFlyPressed => WasPressed(Keys.F);

    /// <summary>Mouse position in window pixels (only meaningful while the mouse is free, i.e. when a menu is open).</summary>
    public Point MousePosition => new(_mouse.X, _mouse.Y);

    public bool ShiftHeld => _keys.IsKeyDown(Keys.LeftShift) || _keys.IsKeyDown(Keys.RightShift);

    public bool LeftHeld => _mouse.LeftButton == ButtonState.Pressed;
    public bool RightHeld => _mouse.RightButton == ButtonState.Pressed;

    /// <summary>Scroll wheel movement this frame (positive = scrolled up).</summary>
    public int ScrollDelta => _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;

    public bool LeftClicked => _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
    public bool RightClicked => _mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released;

    public bool IsKeyDown(Keys key) => _keys.IsKeyDown(key);

    public bool WasPressed(Keys key) => _keys.IsKeyDown(key) && _prevKeys.IsKeyUp(key);

    /// <param name="captured">When true the mouse is locked to the window centre and MouseDelta is reported.</param>
    /// <param name="windowCenter">Window-relative pixel position to lock the mouse to.</param>
    public void Update(bool captured, Point windowCenter)
    {
        _prevKeys = _keys;
        _prevMouse = _mouse;
        _keys = Keyboard.GetState();
        _mouse = Mouse.GetState();

        float x = (_keys.IsKeyDown(Keys.D) ? 1 : 0) - (_keys.IsKeyDown(Keys.A) ? 1 : 0);
        float y = (_keys.IsKeyDown(Keys.W) ? 1 : 0) - (_keys.IsKeyDown(Keys.S) ? 1 : 0);
        Move = new Vector2(x, y);
        Jump = _keys.IsKeyDown(Keys.Space);
        Sprint = _keys.IsKeyDown(Keys.LeftControl);
        Sneak = _keys.IsKeyDown(Keys.LeftShift);

        // Right after capture the OS can keep reporting the old cursor position for a few frames while it
        // jumps to the centre; counting that as movement would snap the camera, so ignore it.
        if (captured && !_wasCaptured) _settleFrames = 5;

        if (captured && _settleFrames == 0)
            MouseDelta = new Vector2(_mouse.X - windowCenter.X, _mouse.Y - windowCenter.Y);
        else
            MouseDelta = Vector2.Zero;
        if (_settleFrames > 0) _settleFrames--;

        if (captured) Mouse.SetPosition(windowCenter.X, windowCenter.Y);
        _wasCaptured = captured;
    }
}
