# VoxelGame tests

A console project that checks the parts of the game that can be checked without a window: lighting, water, saving, threads, networking and the server. It references the game project and runs headless (no graphics device).

```
cd tests/VoxelGame.Tests
dotnet run -c Release -- all          # runs every check below, each in its own process, ~15 s
dotnet run -c Release -- <name>       # runs just one
```

Each check prints `... ALL OK` / `PASSED` and exits with 0, or lists what went wrong and exits with 1.

| Name | What it checks |
|---|---|
| `99` (or any seed) | Lighting: every cell's sky and block light against a brute-force recomputation, after generation and after random edits (leaves, lamps, water, the top of the world) |
| `fluids` | Water rules hold after many random edits (nothing floating, unfed or un-spread) |
| `persist` | Edited chunks, flowing water and per-world folders survive a save and reload (uses a temp folder) |
| `borders` | Trees that cross chunk borders look the same whichever chunk is generated first |
| `items` | Inventory save data round-trips and ignores unknown blocks and bad slots |
| `threads 3` | Worker-thread generation and meshing give the same world as the single-threaded path |
| `net` | Message encoding, the in-memory connection, and real UDP connections on localhost (fragmentation, ordering, disconnects, port in use, dead address) |
| `server` | A `GameServer` with fake clients: join, chunk streaming, edits, rejected edits, position relay, name and version checks, leaving |

Tools that print numbers for a person to read, not pass/fail: `cost [seed]` (what it costs to generate a chunk), `stats [n]` and `rivers [n]` (biome and river statistics), `timing` (river generation time), `fall` (a column of water settling).

**Adding a check:** write a `static class XTest { public static int Run() }` that returns the number of failures, dispatch it from `Program.cs`, and list it in `RunAll.cs` if it should run with `all`.
