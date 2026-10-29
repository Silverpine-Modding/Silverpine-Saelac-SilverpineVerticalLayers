# Silverpine Vertical Layers

`SilverpineVerticalLayers` is a BepInEx plugin for Silverpine 1.7.3 that adds
connected, buildable upper floors to the base Silverpine world tile.

## Requirements

- Silverpine 1.7.3
- BepInEx 5
- ModdingTools 1.11.0 or newer (`Saelac.Silverpine.ModdingTools`)

Custom Item Loader is supported in the same installation but is not required.
The stairs, projected floor, and structural support are registered as stable
runtime prefabs because their paired-floor state requires custom serialization.

Structure Handler 1.1.10 or its compatible editor API 2 (including 1.2.11) is
detected optionally. When present, Vertical Layers adds floor-aware editor and
transfer behavior without making it a hard dependency.

## Gameplay

- Open **Actions > Construct**, or press Modding Tools' **B** shortcut. The shared
  construction window includes **Floor Transition Stairs** and **Structural
  Support** under **Vertical Layers** and **All**, with searchable names. Their
  order keeps them ahead of native entries in **All**.
- Build **Floor Transition Stairs** on a tile tagged `Roofed`.
- The destination floor is activated and every roofed tile below is projected
  as a walkable floor tile above it.
- Use the normal base-game **Wood Floor** or **Rock Floor** construction entries
  to build into open air upstairs. A new tile must remain within four
  orthogonal steps of a projected roof tile or structural support.
- Build a **Structural Support** beneath an existing upper floor to reset the
  four-tile distance and extend farther.
- The default maximum is three total floors: ground, second, and third.

Version 0.4.9 registers these entries through Modding Tools' construction API
instead of patching the old radial menu. This removes the patch-order conflict
with the framework's replacement menu. Custom placement callbacks still create
the same paired stairs/supports and construction sites, with the same materials,
setup/work turns, placement rules, and Salt Extra Debug Free Build behavior.
No save-format or prefab-ID changes are required.

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

Version 0.4.7 reduces CPU work and temporary allocations without lowering the
movement refresh rate or the default render resolution:

- Visibility uses the game's existing `CullInvisible` registry and cached
  sprite references instead of a Unity-wide object search each moving frame.
- A padded viewport candidate list is reused during movement. It refreshes on
  world changes, floor changes, camera movement beyond its margin, zoom changes,
  and at least every 0.25 seconds while capturing. Actual visibility is checked
  against each capture camera's frustum on every rendered frame.
- Only hidden sprites intersecting the lower view are temporarily enabled;
  their original hidden state is restored even if a capture fails.
- Floor-presence queries inspect one turf rather than building the entire
  support network. Full support snapshots avoid empty-tile lists and per-cell
  LINQ/component-array allocations. The four-tile support rule is unchanged.
- Already active levels skip redundant full-town activation rebuilds. Newly
  generated floors register immediately so the stair landing cannot duplicate
  a floor generated earlier in the same frame.

These changes reduce known CPU/garbage-collection costs; an actual FPS gain must
be measured in-game. Live mode still performs one camera render per lower floor
during movement, so its GPU cost remains dependent on resolution and scene size.

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

The integration checks for Structure Handler 1.1.10 or editor API 2. If its
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

### Regression checks

With the .NET 9 SDK installed:

```powershell
dotnet run --project Tests/PerformanceRegressionTests.csproj -c Release
dotnet run --project Tests/Compatibility/CompatibilityChecks.csproj -c Release -- ../..
```

The dependency-free test harness links the production floor-network, occupancy,
and coordinate code against a small in-memory world. It checks prefab-name
classification, walkability, support distances/removal, cache invalidation,
third-floor coordinates, tile-probe counts, and empty-air allocations. It does
not simulate Unity rendering or object lifecycle callbacks.

The compatibility harness compares the release DLL's framework references with
the installed Modding Tools DLL and runs construction registration against a
byte-identical copy of it in the test output. It checks category/entry ownership,
repeat-registration safety, material and time metadata, preserved placement
callbacks, the minimum framework version, and removal of the radial patches.
It does not invoke Unity menu drawing or world placement.

Before enabling a new build, check in-game: walk continuously on the 2nd and
3rd floors, zoom while walking, watch NPCs enter/leave the lower viewport, switch
Live/Void, load a save, and build/rebuild stairs. Confirm movement stays synced,
lower sprites return to normal when descending, and no duplicate landing floor
or new rendering/registration errors appear in the BepInEx log.

## Credits

Created by **Saelac and ChatGPT**.
