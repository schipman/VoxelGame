using System.Diagnostics;
using Microsoft.Xna.Framework;
using VoxelGame.Terrain;

namespace VoxelGame.Diagnostics;

/// <summary>
/// A repeatable performance test: <c>dotnet run -c Release -- --bench</c>. The frame-rate cap is lifted, then the camera
/// visits a few fixed viewpoints in a fixed world. At each it waits for the world to finish loading, measures the average
/// frame time for a few seconds, and prints it with how much geometry was drawn. Numbers from different builds are
/// directly comparable (same machine, same seed). The game exits when it is done.
/// </summary>
public sealed class Benchmark
{
    public const int Seed = 424242;

    private const double WarmupSeconds = 4;
    private const double MeasureSeconds = 5;

    /// <param name="Speed">Blocks per second the camera flies forward during measurement (0 = stay put).</param>
    private readonly record struct Scene(string Name, Biome Biome, float Lift, float Yaw, float Pitch, float Speed = 0f, double Seconds = MeasureSeconds);

    private static readonly Scene[] Scenes =
    {
        new("forest, ground level", Biome.Forest, 3f, 0.6f, -0.05f),
        new("ocean surface",        Biome.Ocean,  8f, 1.2f, -0.12f),
        new("mountains, high up",   Biome.Mountains, 30f, 2.4f, -0.35f),
        new("plains, looking down", Biome.Plains, 40f, 0.0f, -0.9f),
        new("flying over new land", Biome.Plains, 20f, 0.7f, -0.15f, Speed: 40f, Seconds: 14),
    };

    private int _scene = -1;
    private double _phaseTime;
    private bool _measuring;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _frames;
    private double _updateMs, _drawMs;
    private long _triangles, _drawCalls;
    private readonly List<string> _report = new();
    private double _maxFrameMs;
    private readonly List<double> _frameTimes = new();
    private double _lastTick;
    private long _backlog;
    public string StartupLine { get; set; } = "";

    public bool Finished { get; private set; }

    /// <summary>How fast the camera should be moving right now (only while measuring a flight scene).</summary>
    public float CurrentSpeed => _measuring && _scene >= 0 && !Finished ? Scenes[_scene].Speed : 0f;

    /// <summary>Yaw of the current scene, so the game can fly in the direction it points.</summary>
    public float CurrentYaw => _scene >= 0 ? Scenes[_scene].Yaw : 0f;

    /// <summary>Where the game should put the player and camera for the current scene; null when no move is due.</summary>
    public (Biome Biome, float Lift, float Yaw, float Pitch)? TakeTeleport()
    {
        if (_scene >= 0 || Finished) return null;
        return Begin(0);
    }

    private (Biome, float, float, float)? Begin(int index)
    {
        _scene = index;
        _phaseTime = 0;
        _measuring = false;
        _frames = 0;
        _updateMs = _drawMs = 0;
        _triangles = _drawCalls = 0;
        _maxFrameMs = 0;
        _frameTimes.Clear();
        _backlog = 0;
        _lastTick = _clock.Elapsed.TotalMilliseconds;
        var s = Scenes[index];
        return (s.Biome, s.Lift, s.Yaw, s.Pitch);
    }

    /// <summary>Call once per frame after Draw. Returns the next scene to teleport to, if the current one is done.</summary>
    public (Biome Biome, float Lift, float Yaw, float Pitch)? EndFrame(double updateMs, double drawMs, int triangles, int drawCalls, int pendingWork)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double frameMs = now - _lastTick;
        _lastTick = now;
        _phaseTime += frameMs / 1000.0;

        if (!_measuring)
        {
            // Warm up until the world has settled (nothing left to load) and a minimum time has passed.
            if (_phaseTime > WarmupSeconds && pendingWork == 0) { _measuring = true; _phaseTime = 0; }
            else if (_phaseTime > 60) { _measuring = true; _phaseTime = 0; _report.Add("  (world was still loading after 60 s)"); }
            return null;
        }

        _frames++;
        _updateMs += updateMs;
        _drawMs += drawMs;
        _triangles += triangles;
        _drawCalls += drawCalls;
        _maxFrameMs = Math.Max(_maxFrameMs, frameMs);
        _frameTimes.Add(frameMs);
        _backlog += pendingWork;

        if (_phaseTime < Scenes[_scene].Seconds) return null;

        double avg = _phaseTime * 1000.0 / _frames;
        _frameTimes.Sort();
        double p99 = _frameTimes[(int)(_frameTimes.Count * 0.99)];
        int slow = _frameTimes.Count(t => t > 16.7);
        _report.Add($"{Scenes[_scene].Name,-24} {1000.0 / avg,7:F0} fps  {avg,6:F2} ms/frame  (update {_updateMs / _frames:F2}, draw {_drawMs / _frames:F2})  " +
                    $"p99 {p99:F1} ms  worst {_maxFrameMs:F1} ms  backlog {_backlog / (double)_frames,5:F0} chunks  >16.7ms: {100.0 * slow / _frameTimes.Count:F1}%  {_triangles / _frames / 1000.0,5:F0}k tris  {_drawCalls / _frames,4} draws");

        if (_scene + 1 < Scenes.Length) return Begin(_scene + 1);

        Finished = true;
        Console.WriteLine();
        Console.WriteLine("=== benchmark results (frame rate uncapped) ===");
        if (StartupLine.Length > 0) Console.WriteLine(StartupLine);
        foreach (var line in _report) Console.WriteLine(line);
        return null;
    }
}
