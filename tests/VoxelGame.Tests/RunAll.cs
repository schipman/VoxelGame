using System.Diagnostics;

/// <summary>Runs every check in turn, each in its own process (some of them use global state), and prints a summary.</summary>
static class RunAll
{
    // Things that check something and return a failure count. The others (cost, stats, rivers, timing, fall) print
    // numbers for a person to read.
    private static readonly string[][] Checks =
    {
        new[] { "99" },            // lighting: every cell's light against a brute-force oracle, then after random edits
        new[] { "fluids" },        // water simulation rules hold after random edits
        new[] { "persist" },       // chunks and water survive a save and reload
        new[] { "borders" },       // trees across chunk borders are order independent
        new[] { "items" },         // inventory save data
        new[] { "threads", "3" },  // worker threads give the same world and meshes as the single-threaded path
        new[] { "net" },           // messages, in-memory and real UDP connections
        new[] { "server" },        // a server with fake clients: join, chunks, edits, rejection, leaving
    };

    public static int Run()
    {
        var failed = new List<string>();
        var clock = Stopwatch.StartNew();

        foreach (var check in Checks)
        {
            string name = string.Join(" ", check);
            Console.WriteLine($"\n===== {name} =====");
            using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, check) { UseShellExecute = false })!;
            process.WaitForExit();
            if (process.ExitCode != 0) failed.Add(name);
        }

        Console.WriteLine($"\n===== summary ({clock.Elapsed.TotalSeconds:F0} s) =====");
        Console.WriteLine(failed.Count == 0 ? $"ALL {Checks.Length} CHECKS PASSED" : $"FAILED: {string.Join(", ", failed)}");
        return failed.Count == 0 ? 0 : 1;
    }
}
