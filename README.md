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
| Space | Jump (fly mode: up; swimming: swim up; at a ledge: climb out) |
| Left Shift | Fly mode: down; dive when swimming |
| Left Ctrl | Sprint |
| F | Toggle fly mode |
| Left mouse | Break block (hold to repeat) |
| Right mouse | Place selected block (hold to repeat) |
| 1-9 / scroll wheel | Choose the hotbar slot |
| B (creative) | Swap the held stack for the next block type (Shift+B: previous) |
| E | Open or close the inventory |
| F3 | Show or hide the debug text |
| F11 | Toggle fullscreen (also in Settings) |
| Shift+B | Cycle block selection backwards |
| F5 | Reload the block atlas, water normal map and UI font from disk (see below) |
| T (hold) | Fast-forward time of day (a full day is 10 minutes) |
| Esc | Pause menu (or close the open menu). Switching to another window also pauses. |

FPS, time of day, position and chunk counts are shown on screen (F3 to hide) and in the window title.

Launch switches (see `src/LaunchOptions.cs`): `dotnet run -- --seed=1234 --time=0.5 --yaw=90 --pitch=-20 --lamp`

More test switches: `--fly --lift=N` (hover N blocks above the ground), `--spill` (a waterfall appears in front of
you), `--rain` (random ripples on nearby water), `--screen=inventory|pause|settings` (open a menu at start),
`--survival` (start in survival mode).

To visit a biome: `dotnet run -- --find=PaleGarden` or `--find=River` (any name from the table below, spaces optional),
or `--tp=X,Z`.
The window title shows the biome you are standing in.

## Performance

How the game stays fast, and how to measure it.

- **Benchmark:** `dotnet run -c Release -- --bench` lifts the frame-rate cap, visits four fixed viewpoints and a flight over
  unexplored land in a fixed world, and prints frame times, triangles, draw calls and start-up phases. `--distance=N` sets the
  render distance and `--size=2560x1440` the window size. Compare runs on the same machine.
- **Build configuration:** `dotnet run` builds Debug by default, which used to be several times slower. Debug is now optimised
  (see `VoxelGame.csproj`); `-c Release` is still the fastest.
- **Greedy meshing** (`ChunkMesher`): neighbouring faces with the same texture and identical lighting are merged into one big
  rectangle, roughly halving the triangles. Following the 0fps ambient-occlusion article, only faces whose four corners are lit
  identically are merged, so a merged quad looks exactly like the faces it replaces; faces on a lighting gradient stay single.
  The vertex's texture coordinate counts tiles and the shader wraps it inside the tile named in the vertex's extra data.
- **Worker threads** (`ChunkWorkers`): terrain generation and meshing run on background threads (your core count minus 2, at most 6).
  The main thread only lights new chunks (that edits neighbouring chunks, so it can't run beside other work), uploads finished
  meshes to the GPU, and meshes chunks you just edited so changes appear on the next frame. A chunk's `Version` number guards
  against races: a mesh built from an old snapshot never marks an edited chunk as up to date. Tests build worlds with 0 workers,
  which does everything on one thread and gives identical results.
- **Smaller wins:** meshing and lighting skip the empty air above the terrain (`Chunk.TopY`); chunk meshes use 16-bit indices when
  they can; the shadow map is only redrawn when the sun has moved noticeably, the shadow camera has re-centred (it follows the
  player in 4-block steps) or a mesh changed; terrain is drawn nearest chunk first so the GPU can skip hidden pixels; chunk loading
  has a time budget per frame so the game never stalls.
- **What to expect:** on a fast GPU the frame rate was already limited by the monitor. The gains show up as faster start-up and
  loading, no hitches while exploring, half the triangles (which is what matters on slower GPUs and at long view distances).

## User interface

A small widget toolkit in `src/UI/`, plus the screens built from it. Everything is laid out in "UI pixels" and scaled up
by a whole number (set automatically from the window size, or in Settings), so it stays crisp at any resolution.

**Screens**
- **HUD** (`Hud.cs`): crosshair, hotbar, the name of the held item, debug text, short messages. Always on.
- **Inventory** (E): 3 rows of storage plus the hotbar. In creative mode a palette of every block sits above it.
  Drag a stack to move it (left button: all, right button: half); dropping on the same item merges, on a different one
  swaps. Shift-click sends a stack between the hotbar and the rest. Click a hotbar slot to hold it. Palette: drag to copy,
  click to add a stack, drop an item onto it to delete it. Hover for a tooltip.
- **Pause menu** (Esc): resume, settings, save, save and quit. The game stands still while it is open.
- **Settings:** render distance, field of view, mouse sensitivity, UI scale, fullscreen (borderless, at the desktop resolution), shadows, debug text, game mode. Saved to
  `%AppData%\VoxelGame\settings.json`.

**Game modes** (Settings): *Creative* (default): nothing is used up, and the palette is available. *Survival*: breaking a
block puts it in your inventory, placing uses one up, and you start with nothing.

**Building your own screen.** Subclass `UiScreen`, add widgets to `Root`, and call `Ui.Open(screen)`:

```csharp
public sealed class HelloScreen : UiScreen
{
    public HelloScreen()
    {
        var window = Root.Add(new StackPanel { Background = true, Padding = 10, Spacing = 5, CenterChildren = true, Anchor = Anchor.Center });
        window.Add(new Label("Hello!") { Scale = 2 });
        window.Add(new Button("Close", () => Ui.CloseTop()));
    }
}
```

| Piece | What it is |
|---|---|
| `Widget` | Base class. Position (or `Anchor` + `Offset`), `Size`, children, mouse events, drag and drop hooks. |
| `StackPanel`, `GridPanel` | Lay children out in a row, column or grid, and size themselves to fit. `Background = true` draws a window behind them. |
| `Label`, `Button`, `Slider`, `CycleButton` | The standard controls. |
| `ItemSlot` | A square holding a stack of items; reads and writes it through two functions, so it works for any container. |
| `UiManager` | Routes the mouse: hover, press, click, scroll, and drag and drop (`BeginDrag` / `CanAcceptDrop` / `OnDrop`). Owns the screen stack. |
| `UiTheme` | Every colour in one place. Use `UiTheme.Alpha` for see-through colours (sprites expect premultiplied alpha). |

**Drag and drop** for your own widgets: override `BeginDrag` to return a `DragPayload` subclass (it draws the thing that
follows the cursor), and override `CanAcceptDrop` and `OnDrop` on whatever should accept it. See `ItemSlot`.

**Font:** `Assets/Textures/font.png`, a 16x6 grid of 8x8 characters (ASCII 32-127, white on transparent). Edit it and
press F5. Character widths are measured from the pixels. **Block icons** in slots are drawn once from the real block textures
(`BlockIconAtlas`) and redrawn when the textures or UI scale change.

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
| River | channels from highlands to the sea | gravel and sand bed, sandy banks | none |
| Lake | flat inland ground: big deep lakes (14-32 blocks across) and small ponds (4-9) | sand and gravel bed, sandy rim | none |

Oceans fill up to sea level (y=46) and cover about a third of the world (median depth 22 blocks, up to 44), with continents and islands in between. Rivers run down from the highlands to the coast, and lakes and ponds are scattered over flat inland ground. New worlds start on dry land within a short walk of water. There are no caves or ores yet.

**Adding a biome:** add it to `Biome` and `BiomeRegistry` (name, ground blocks, tree list), then add a rule for where it
occurs in `BiomeSource`. **Adding a tree:** add a `TreeKind` and a shape in `TreeBuilder`, new log/leaf blocks if needed.
Worlds saved with an older generator will show a warning: their edited chunks won't match the new terrain.

## Water

**Looks** (`Content/Shaders/Water.fx`, `Assets/Textures/water_normal.png`). The world is drawn first into an off-screen
image that stores colour plus the distance from the camera. Water is then drawn on top, and each water pixel looks at
a copy of that image to work out what it is lying over:
- **Waves:** a few travelling sine waves move the surface vertices, and the lighting uses the waves' slopes.
- **Normal maps:** a tiling normal map is read at three scales, scrolling in different directions. Sea water drifts
  with the wind; river water slides along the river's flow direction.
- **Refraction:** the view is bent along the surface normal. Samples that land on something nearer than the water are
  rejected so objects in front don't smear into it.
- **Absorption:** light fades with the distance it travels through water, red first, so shallows are turquoise and
  depths go deep blue. The sea floor stays visible because underwater faces fade by depth rather than going black.
- **Reflection and sun glints:** a sky gradient reflected by Fresnel, plus sharp glitter paths when the sun is low.
- **Foam** at shallow edges, and **streaks** sliding down waterfalls.
- **Caustics:** moving light patterns on anything under water (computed in `Voxel.fx` from the same normal map).
- **Underwater:** murky fog, and the surface seen from below is a bright rippled ceiling.
`water_normal.png` is a plain image like the block atlas: red/green = surface slope, blue = a pattern used for foam.
Edit or replace it and press F5.

**Ripples** (`RippleSimulator`, `Ripples.fx`): a wave simulation running on the GPU on a map that wraps around the
player. Swimming leaves a gentle wake in front of you, diving in makes a small splash, treading water leaves slow rings,
and walking through shallows ripples too. They only bend the reflections (no foam); strengths and sizes are in
`StirWater` (VoxelGameApp) and `RippleStrength` (WorldRenderer). `--rain` shows raindrops. Call
`renderer.Ripples.Disturb(from, to, radius, strength)` to make ripples from anything else.

**Swimming:** Space swims up, Shift dives, you float at the surface by yourself otherwise, and Space against a ledge climbs out.

**Flow** (`FluidSimulator`): water blocks have a level: 0 is a source (never runs dry), 1-7 is flowing water that thins
as it travels. Water falls into air below it, spreads sideways when resting on something solid, and dries up when
nothing feeds it. Dig next to a lake and it pours in; remove a source and what it fed drains. Generated oceans and
rivers are all source blocks, so they stay put until something nearby changes. Water is in the B-cycle: place it
yourself. The simulation advances 5 times a second, and levels are saved with chunks.

**Lakes and ponds** (`LakeField`): the world is cut into a grid and each cell decides from the seed alone whether it
holds a lake, so chunks can be generated in any order. A lake is only placed on fairly flat, inland ground, and its
water level is set a block below the lowest point of its rim so it can never spill. The shoreline wobbles with noise,
and the bed is a smooth bowl, deepest in the middle. Ponds are the same thing, smaller and shallower, and much more common.

**Rivers** (`RiverNetwork`): rivers start on high ground and flow downhill to the sea, forking near the coast like a
delta. They are traced in big regions so they don't depend on chunk order: each region picks sources on its highest
ground (and some in the foothills, so many rivers reach the sea) and follows the land downhill, steered by the slope,
by the direction of the coast and by fractal noise that makes them meander. Rivers widen downstream. Each is carved
into the terrain as a channel with banks, and filled with water that never rises as the river goes on. Rivers that
don't reach the sea end in a pond.

## Block textures

All block textures live in one image: `Assets/Textures/blocks.png` (256x256, a 16x16 grid of 16-pixel tiles,
so 256 slots; 39 are used). Edit it in any image editor that handles PNG transparency.
The water normal map (`Assets/Textures/water_normal.png`) reloads the same way. **While the game is running,
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
Content/Shaders/Voxel.fx    terrain shader (lighting, shadows, caustics)
Content/Shaders/Water.fx    water surface shader
Content/Shaders/Ripples.fx  ripple simulation step
src/
  VoxelGameApp.cs        Game loop; owns and orders the systems
  LaunchOptions.cs       command-line test switches
  Blocks/
    BlockType.cs         enum of every block (1 byte per voxel)
    BlockRegistry.cs     per-block data: solid/opaque flags, which tile each face uses
  Terrain/
    Chunk.cs             16x128x16 block array + its GPU mesh
    World.cs             loaded chunks, Get/SetBlock in world coords, chunk streaming
    ChunkNeighborhood.cs  a chunk plus its 8 neighbours as a snapshot the mesher reads (thread-safe)
    ChunkWorkers.cs      background threads that generate and mesh chunks
    LightingEngine.cs    voxel light: sky + block light flood fill, incremental add/remove
    FluidSimulator.cs    water flow: sources, falling, spreading, drying up
    RiverNetwork.cs      rivers traced downhill from highlands to the sea
    LakeField.cs         lakes and ponds scattered over flat inland ground
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
    AssetFiles.cs        loads editable PNGs from disk
    RippleSimulator.cs   GPU ripple wave simulation
    FullscreenQuad.cs    draws a shader over a whole render target
    ChunkMesher.cs       blocks -> greedy-merged triangles; bakes smooth light + ambient occlusion per vertex
    VoxelVertex.cs       vertex format (position, normal, uv, light)
    WorldRenderer.cs     frame pipeline: shadows, scene, water (Voxel.fx, Water.fx), present
    DayNightCycle.cs     time of day -> sun direction, light/sky/ambient colours
    CelestialRenderer.cs sun, moon and stars sprites
  Entities/
    Camera.cs            first-person view/projection
    Player.cs            AABB movement, gravity, collision
    BlockInteraction.cs  what the crosshair targets; break/place; hotbar selection
  Persistence/
    WorldStorage.cs      level.json + per-chunk files (palette + gzip, written atomically)
    LevelData.cs         what level.json holds
  Items/
    ItemStack.cs, Inventory.cs   stacks, 36-slot inventory, drag/drop transfer rules
  Settings/
    GameSettings.cs      options saved between sessions
  UI/
    Widget.cs, Layouts.cs, Controls.cs, ItemSlot.cs   the widget toolkit
    UiManager.cs         mouse routing, drag and drop, screen stack
    Hud.cs, Screens/     hotbar and crosshair; inventory, pause and settings menus
    PixelFont.cs, UiRenderer.cs, UiTheme.cs
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
- [x] UI toolkit: panels, buttons, sliders, item slots, drag and drop, pause and settings menus
- [x] Inventory and hotbar (creative palette, survival collecting)
- [x] Biomes: climate noise, 15 biomes, trees (see above)
- [ ] Chopping trees down (logs and leaves exist; needs tool/break-time and drops)
- [ ] Caves and ores
- [x] Water: waves, refraction, caustics, ripples, swimming, flowing water, rivers
- [ ] Mobs and pathfinding (A* over the block grid)
- [ ] Audio
- [x] Saving/loading changed chunks, random seed per launch, `--seed` to reload a world
- [ ] World select / new-world screen with a seed field (needs UI)

## Known limitations / future work

- Saving runs on the main thread (a changed chunk is ~1 KB gzipped, so it's quick); move it to a worker if it ever hitches.
- Chunk files store block names, not numbers, so adding or reordering `BlockType` entries won't break old saves.
- Chunk generation and meshing happen on the main thread (budgeted to 2 each per frame).
  Move them to worker threads when it starts to hitch.
- Water reflects the sky and sun but not terrain or trees (no screen-space reflections yet), and has no biome tint.
- Water doesn't make "infinite sources" from two adjacent sources, and doesn't push the player or items along.
- Torches (non-cube, wall-mounted light sources) need a custom block shape; lamps are full cubes for now.
- Light is white/warm only (one value per voxel, not RGB).
- Mesh vertices use world coordinates; very far from the origin, float precision will cause jitter.
