# Silverpine Vertical Layers

`SilverpineVerticalLayers` is a BepInEx plugin for Silverpine 1.7.3 that adds
connected, buildable upper floors to the base Silverpine world tile.

## Requirements

- Silverpine 1.7.3
- BepInEx 5
- ModdingTools (`Saelac.Silverpine.ModdingTools`)

Custom Item Loader is supported in the same installation but is not required.
The stairs, projected floor, and structural support are registered as stable
runtime prefabs because their paired-floor state requires custom serialization.

Structure Handler 1.1.10 is detected optionally. When present, Vertical Layers
adds floor-aware editor and transfer behavior without making it a hard dependency.

## Gameplay

- Open the normal **Construct** radial. Vertical Layers adds **Floor Transition
  Stairs** and **Structural Support** as the first two choices on that menu
  instead of adding a separate player ability.
- Build **Floor Transition Stairs** on a tile tagged `Roofed`.
- The destination floor is activated and every roofed tile below is projected
  as a walkable floor tile above it.
- Use the normal base-game **Wood Floor** or **Rock Floor** construction entries
  to build into open air upstairs. A new tile must remain within four
  orthogonal steps of a projected roof tile or structural support.
- Build a **Structural Support** beneath an existing upper floor to reset the
  four-tile distance and extend farther.
- The default maximum is three total floors: ground, second, and third.

Stairs are paired. Interacting with either end changes floors, and NPC
pathfinding evaluates every connected stair route using local path length plus
transition cost before choosing a staircase. NPCs walk to the best reachable
orthogonally adjacent stair-use tile when the stair's own occupied turf cannot
be selected as a base-game pathfinding destination. When a follower targets the
player on another level, an exact target that is empty or locally unreachable
is resolved to the nearest reachable real floor within four tiles before the
stair route is rejected. Player stair transitions also cancel any follower's
old route before placing that NPC on a valid destination-floor cell.

Upper-floor physical coordinates inherit the corresponding canonical
Silverpine map zone, nearest-zone identity, persistence zone, and faction.
NPC environment prompts retain the base area description and explicitly label
the current vertical level as `Ground Floor`, `2nd Floor`, `3rd Floor`, and so on.
Routine executors ignore intermediate staircase arrivals and suspend the base
game's direct-to-target unstuck teleport while a stair route is active. If a
route segment is temporarily blocked during loading or by another entity, it
remains pending and is recalculated on later turns instead of reporting a false
arrival to the routine system.
When Runtime NPC Editor is installed, Vertical Layers injects a Ground/Floor 2/
Floor 3 selector beside routine X/Y. The editor continues to display canonical
Silverpine coordinates while Vertical Layers encodes the selected floor only
when the override is saved.

## Lower-floor display

The default `Live` view composites all lower active floors behind the current
floor at 67% resolution. Its capture cameras render only the current viewport
for substantially less work than a full-town atlas. During player or camera
motion the viewport is synchronized every frame, including the complete
movement animation. Once movement settles it returns to the low configurable
idle refresh rate. Lower walls, terrain, NPCs, furniture, and other live
objects are all included without floor masks or wall-specific suppression.

Set the view to `Void` to use a plain background like an older tile-based game.
Void mode releases the lower cameras, material, and render texture rather than
leaving hidden render resources allocated.

All levels share Silverpine's current weather. On upper levels, explicit roof
tags, a constructed floor directly above, and wall-enclosed floor regions count
as indoors; exposed floor regions continue to receive rain and other outdoor
effects.

Open **Inventory > Mods > Vertical Layers** to change:

- Live or Void lower-floor view
- 50%, 67%, or 100% lower render resolution
- 0, 1, 2, or 4 FPS idle refresh
- Manual display refresh
- Projected roof-floor rebuild
- Emergency return to ground

## Structure Handler integration

The Structure Editor receives a prominent **Vertical Layers — Editor Floor**
bar with **All**,
**Ground**, **Second**, **Third**, and any additional configured floors.

- A selected floor is displayed and edited using canonical Silverpine X/Y
  coordinates rather than the remote physical X-coordinate band.
- The JSON file continues storing physical coordinates for compatibility.
- New records are assigned to the selected floor. Records added while **All**
  is selected default to ground.
- **All** overlays every floor at canonical coordinates so corresponding rooms
  can be compared; use a specific floor for unambiguous selection and editing.

For a complete multi-floor export:

1. In **Inventory > Mods > Structure Handler**, select top-left and
   bottom-right bounds on one floor. Ground is recommended.
2. Keep both bounds on the same logical floor and enclose the complete building
   footprint.
3. Run **Structures Export** normally.

Vertical Layers applies that canonical footprint to every active floor. It
exports ordinary player-built upper floors as objects, automatically includes
the companion halves of stairs and supports, and omits generated projected
roof floors.

During import it activates and registers the custom prefabs, assigns fresh pair
IDs, recomputes stair destinations from the imported counterparts, activates
the required floors, and regenerates roof projections. Structure Handler's
world-tile controls also treat upper bands as Silverpine tile `(0, 0)`.

The integration is version-checked against Structure Handler 1.1.10. If its
private transfer methods change in a later release, only this optional adapter
is disabled and Vertical Layers continues to operate normally.

The editor adapter preserves Structure Handler's integer supporting-terrain
coordinates and floating-point object coordinates when changing floor views.
If a future record type is incompatible, floor filtering disables itself and
leaves the base editor session open rather than propagating an editor-closing
GUI exception.

## Configuration

The generated BepInEx configuration file is
`BepInEx/config/renegadex.silverpine.verticallayers.cfg`.

Important defaults:

| Setting | Default | Purpose |
| --- | ---: | --- |
| `MaximumLevels` | 3 | Total floors, including ground |
| `MaximumUnsupportedSpan` | 4 | Orthogonal tiles allowed from support |
| `LowerLayerViewMode` | Live | Live composite or unallocated Void mode |
| `LowerResolutionScale` | 0.6667 | Lower-floor render scale |
| `MovementBurstFPS` | 12 | Stationary event-refresh cap; movement is frame-synced |
| `AmbientRefreshFPS` | 2 | Idle refresh; 0 is fully event-driven |
| `StructureEditorFloor` | -1 | Structure Editor floor; -1 shows all |

The coordinate origin and stride settings reserve remote X-coordinate bands
for upper floors. Changing them after saving a game will orphan existing upper
objects, so treat those two settings as save-format values.

## Save and construction behavior

- The persistent layer manager is created on Vertical Layers' own runtime host
  before save loading; it does not depend on a world-manager singleton becoming
  available during a particular frame. It is verified after every save load and
  recovered on demand by vertical construction if Unity clears its instance.
- Placing stairs or a support sets up a normal Silverpine construction site.
  Deposit its listed materials, then finish it with a hammer and the ordinary
  construction interaction. NPC builders can see the same site, unfinished
  sites survive saves, and deconstructing one refunds its deposited inventory.
- Stair and support completion is atomic: the base site completes only when
  Vertical Layers can create both linked halves on their respective floors.
- Stairs, supports, their unfinished sites, projected floors, and ordinary
  serializable construction on upper floors are included in the normal save.
- Removing a floor/support is rejected if it would leave another floor beyond
  the configured support span.
- Non-flying players and NPCs cannot enter an upper-level coordinate unless an
  actual projected, wood, or rock floor exists there. The same rule is included
  in pathfinding, while empty air remains targetable by construction placement.
  Attempting to walk into empty air behaves like an ordinary blocked tile and
  does not emit repeated notifications.
- Objects must be removed before their floor can be deconstructed.
- A projected floor cannot be removed directly while its source tile remains
  roofed. Remove or deroof the source structure below instead.
- Normal vanilla wood and rock floor placement upstairs is filtered through the
  same support rule. Walls and furniture use the normal construction system.

### Salt Extra Debug Free Build

Salt Extra Debug remains optional. When its debug-menu **Free Build** toggle is
enabled, newly placed vertical construction sites require no setup hammer, no
materials, and zero work turns. Interact with the site to complete it using
Salt Extra Debug's normal instant construction-site path. Placement rules still
apply, so Free Build does not permit stairs off Silverpine, exceed the maximum
floor count, or create unsupported/blocked structures.

## Art

The four wooden stair rotations are embedded in the DLL at 32 pixels per unit.
Source and processed art are under `Art/Generated` and `Assets`.

The sprite was generated from extracted Silverpine dungeon-stair, wood-floor,
and wood-railing references, then chroma-keyed, reduced to the game's pixel-art
scale, and rotated into four point-filtered variants.

## Build

```powershell
dotnet build SilverpineVerticalLayers.csproj -c Release
```

The release output is `bin/Release/SilverpineVerticalLayers.dll`.

## Credits

Created by **Saelac and ChatGPT**.
