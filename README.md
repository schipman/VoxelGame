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

