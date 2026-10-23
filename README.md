# VoxelGame

A Minecraft-style voxel game in C# using MonoGame (DesktopGL, .NET 9).

```
dotnet run
```

## Controls

| Key | Action |
|---|---|
| W A S D | Move |
| Mouse | Look |
| Space | Jump (fly mode: up) |
| Left Shift | Fly mode: down |
| Left Ctrl | Sprint |
| F | Toggle fly mode |
| Left mouse | Break block (hold to repeat) |
| Right mouse | Place selected block (hold to repeat) |
| B / scroll wheel | Cycle through every block type to place (1-9 jump to the first nine) |
| Shift+B | Cycle block selection backwards |
| F5 | Reload the block texture atlas from disk (see below) |
| T (hold) | Fast-forward time of day (a full day is 10 minutes) |
| Esc | Release mouse (press again to quit); click the window to grab it again |

FPS, time of day, position and chunk counts are shown in the window title (no font asset yet).

Launch switches (see `src/LaunchOptions.cs`): `dotnet run -- --seed=1234 --time=0.5 --yaw=90 --pitch=-20 --lamp`

To visit a biome: `dotnet run -- --find=PaleGarden` (any name from the table below, spaces optional), or `--tp=X,Z`.
The window title shows the biome you are standing in.

## World generation and biomes

Terrain follows the shape of Minecraft's generator (`src/Terrain/`):

1. **Climate** (`Climate.cs`): five noise fields per spot: *continentalness* (ocean to deep inland), *erosion*
   (rugged to flat), *weirdness* (folded into *peaks-and-valleys*), *temperature* and *humidity*.
2. **Biome** (`BiomeSource.cs`): low continentalness is ocean, then beach. Inland, rugged high ground (low erosion,
   high peaks) becomes mountains and peaks. Everything else comes from a 5x5 temperature x humidity table using
   Minecraft's band edges. Pale Garden is a rare variant of the wettest temperate forest (picked by weirdness).
3. **Height** (`WorldGenerator.ShapeHeight`): continentalness sets the base level, erosion sets how big the relief is,
   peaks-and-valleys pushes ridges up and valleys down, plus rolling hills. Sea level is y=46.
4. **Surface** (`BiomeRegistry` + `WorldGenerator.SurfaceAt`): each biome has its own ground blocks. Mountains show bare
   rock on steep slopes and snow above y=100.
5. **Trees** (`TreeBuilder.cs`): one candidate per 4x4 cell, with a per-biome chance and species mix. Trees are
   deterministic and stamped chunk by chunk, so they cross chunk borders cleanly.

| Biome | Where | Ground | Grows |
|---|---|---|---|
| Plains | temperate, dry | grass | a few oaks |
| Forest | temperate/warm, moderate-wet | grass | oak, some birch |
| Birch Forest | temperate, wet | grass | birch |
| Dark Forest | temperate, very wet | grass | huge dark oaks (dim, dense canopy) |
| Pale Garden | rare variant of dark forest | pale moss | pale oaks (sparse, pale leaves) |
| Taiga | cool, wet | grass and podzol | spruce |
| Snowy Taiga / Snowy Plains | cold | snowy grass | spruce |
| Savanna | warm, dry | dry grass | acacia |
| Desert | hot, dry | sand over sandstone | cactus |
| Mountains | inland, rugged | grass, bare rock, snow cap | spruce, oak |
| Stony Peaks / Snowy Peaks | inland, tallest ridges (warm / cold) | stone, gravel / snow | none |
| Beach, Ocean | low continentalness | sand, gravel | none |

There is no water yet, so oceans are dry sand basins for now (about 13% of the world). Once water exists they fill up
to sea level automatically. There are also no caves or ores yet.

**Adding a biome:** add it to `Biome` and `BiomeRegistry` (name, ground blocks, tree list), then add a rule for where it
occurs in `BiomeSource`. **Adding a tree:** add a `TreeKind` and a shape in `TreeBuilder`, new log/leaf blocks if needed.
Worlds saved before this generator existed will show a warning: their edited chunks won't match the new terrain.

## Block textures

All block textures live in one image: `Assets/Textures/blocks.png` (256x256, a 16x16 grid of 16-pixel tiles,
so 256 slots; 39 are used). Edit it in any image editor that handles PNG transparency. **While the game is running,
save the file and press F5** to see the change immediately (run the game from the project folder, e.g. `dotnet run`,
so it reads this working copy; otherwise it reads the copy in `bin/`, refreshed on every build).

- A tile's slot number counts left to right, then down: `column = slot % 16`, `row = slot / 16`.
  Slot 0 is the top-left tile, slot 16 is the first tile of the second row.
- Slots are named by the `Tile` enum in `src/Blocks/BlockRegistry.cs`. Slots 0-38 are in use; see the `Tile`
  enum for the list (8 gravel, 9 snow, 10-20 biome ground and cactus, 21-32 logs, 33-38 leaves).
- Want higher resolution? Make the image 512x512 (32px tiles) or 1024x1024 (64px). It must stay square and be
  divisible by 16; nothing else needs to change.

## Worlds and saving

- Every launch picks a **new random seed** (shown in the window title and printed in the console). Use
  `--seed=N` to start a specific seed, or to reload a world you saved earlier.
- Worlds save to `%AppData%\VoxelGame\saves\seed_<N>\`: `level.json` (seed, time of day, player position) and
  `chunks\c.<x>.<z>.bin` for each chunk you changed. Untouched terrain is regenerated from the seed, so saves stay tiny.
- Saves happen every 30 seconds, when a changed chunk unloads, and on exit. A world you never edited is not saved.
- Delete a world by deleting its `seed_<N>` folder.

## Code map

```
Assets/Textures/blocks.png   the block texture atlas (edit me)
Content/Shaders/Voxel.fx    terrain shader
src/
  VoxelGameApp.cs        Game loop; owns and orders the systems
  LaunchOptions.cs       command-line test switches
  Blocks/
    BlockType.cs         enum of every block (1 byte per voxel)
    BlockRegistry.cs     per-block data: solid/opaque flags, which tile each face uses
  Terrain/
    Chunk.cs             16x128x16 block array + its GPU mesh
    World.cs             loaded chunks, Get/SetBlock in world coords, chunk streaming
    LightingEngine.cs    voxel light: sky + block light flood fill, incremental add/remove
    WorldGenerator.cs    seed -> terrain: height shaping, surface rules, tree placement, biome/spawn search
    Climate.cs           the five climate noise fields (ClimateSampler)
    BiomeSource.cs       climate -> biome (Minecraft-style table)
    Biome.cs             biome list and per-biome ground/trees (BiomeRegistry)
    TreeBuilder.cs       tree and cactus shapes
    PerlinNoise.cs
    VoxelRaycast.cs      find the block a ray hits (and which face)
  Rendering/
    BlockOutlineRenderer.cs  wireframe around the targeted block
    TextureAtlas.cs      loads Assets/Textures/blocks.png; tile slot -> UV math
    ChunkMesher.cs       blocks -> triangles; bakes smooth light + ambient occlusion per vertex
    VoxelVertex.cs       vertex format (position, normal, uv, light)
    WorldRenderer.cs     shadow pass + main pass using Content/Shaders/Voxel.fx
    DayNightCycle.cs     time of day -> sun direction, light/sky/ambient colours
    CelestialRenderer.cs sun, moon and stars sprites
  Entities/
    Camera.cs            first-person view/projection
    Player.cs            AABB movement, gravity, collision
    BlockInteraction.cs  what the crosshair targets; break/place; hotbar selection
  Persistence/
    WorldStorage.cs      level.json + per-chunk files (palette + gzip, written atomically)
    LevelData.cs         what level.json holds
  Input/
    InputState.cs        keyboard/mouse -> game-level input
```

## How lighting works

- **Voxel light** (`LightingEngine`): each voxel stores sky light and block light, 0-15. Light spreads outward one
  level dimmer per block through non-opaque blocks; sky light going straight down doesn't dim. Mining or placing a
  block updates only the affected area. This is what makes caves dark and lamps light their surroundings.
- **Per-vertex bake** (`ChunkMesher`): each corner of a face averages the light of the cells around it (smooth
  lighting) and gets an ambient-occlusion term from its neighbouring blocks. Light changes therefore re-mesh chunks.
- **Sun and shadows** (`Voxel.fx`, `WorldRenderer`): the scene is first drawn from the sun's view into a depth
  texture (the shadow map); the main pass compares each pixel's depth against it. Final colour is
  `(ambient sky light + lamp light + sun * shadow) * ambient occlusion`, then fog.
- **Time of day** (`DayNightCycle`): sun direction, sky/fog colour and light colours all derive from one 0..1 value.
- To change the look, tweak the colours in `DayNightCycle` and the constants at the top of `WorldRenderer`
  (shadow map size and range).

## How to add things

- **A light-emitting block:** same as a new block, plus `LightEmission: 0-15` in its `BlockRegistry` line.
- **A new block:** (1) draw its texture(s) in free slots of `blocks.png`; (2) add `Tile` entries for them with those
  slot numbers; (3) add it to `BlockType`; (4) add a line in `BlockRegistry` saying which tiles its top, sides and
  bottom use. It then shows up in the B-cycle automatically.
- **Changing terrain:** edit `WorldGenerator.GetHeight` / `Generate`.
- **Changing blocks at runtime:** call `World.SetBlock`. It marks the chunk (and edge neighbours) for re-meshing.

## Roadmap

- [x] Block rendering (face culling, texture atlas, per-face shading)
- [x] Chunks and world generation (streamed around the player)
- [x] Player, camera, walking, jumping, collision
- [x] Mining and placing blocks (DDA raycast, block outline, 5-block reach)
- [x] Lighting: voxel sky/block light, ambient occlusion, moving sun with shadow map, day/night, lamps
- [ ] UI: needs a SpriteFont (the content pipeline is now set up: add it to `Content/Content.mgcb`)
- [ ] Inventory and hotbar
- [x] Biomes: climate noise, 15 biomes, trees (see above)
- [ ] Chopping trees down (logs and leaves exist; needs tool/break-time and drops)
- [ ] Caves, ores, rivers and lakes (rivers/lakes need water)
- [ ] Water (separate transparent mesh pass in `WorldRenderer`)
- [ ] Mobs and pathfinding (A* over the block grid)
- [ ] Audio
- [x] Saving/loading changed chunks, random seed per launch, `--seed` to reload a world
- [ ] World select / new-world screen with a seed field (needs UI)

## Known limitations / future work

- Saving runs on the main thread (a changed chunk is ~1 KB gzipped, so it's quick); move it to a worker if it ever hitches.
- Chunk files store block names, not numbers, so adding or reordering `BlockType` entries won't break old saves.
- Chunk generation and meshing happen on the main thread (budgeted to 2 each per frame).
  Move them to worker threads when it starts to hitch.
- Torches (non-cube, wall-mounted light sources) need a custom block shape; lamps are full cubes for now.
- Light is white/warm only (one value per voxel, not RGB).
- Mesh vertices use world coordinates; very far from the origin, float precision will cause jitter.
