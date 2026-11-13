# Silverpine Vertical Layers 0.4.10

Created by **Saelac and ChatGPT**.

## Changes

- Cache the Structure Handler editor's floor-filtered view between GUI events,
  avoiding full-document filtering, coordinate conversion and merging on every
  repaint. Idle history flushes no longer trigger those operations.
- Restore the complete physical document for save, load, undo, redo, history
  initialization and close. Nested operations preserve hidden floors.
- Commit new records to the floor being edited before switching floors, and
  invalidate placement-conflict caches when changing between filtered/full views.
- Support Structure Handler's in-memory history API without warnings about its
  removed snapshot method. Pair with Structure Handler 1.2.26 for its latest
  preview, input, placement and import improvements.

No ModdingTools API, gameplay, prefab identifier or save-format changes.

## Validation

- Release build: zero warnings and errors.
- 1,038 floor/support regression assertions and 37 compatibility checks pass.
- Compatibility checks cover the installed framework and floor-view/history
  integration. Live Unity menu rendering and save/load still need in-game testing.

## Requirements and installation

Requires Silverpine 1.7.3, BepInEx 5 and ModdingTools 1.11.0 or newer.
Structure Handler is optional and distributed separately; its own minimum
ModdingTools version is 1.12.0.

Close Silverpine and extract the ZIP into `BepInEx/plugins/`; it contains a
`SilverpineVerticalLayers` folder. Alternatively, replace the existing
`SilverpineVerticalLayers.dll` with the standalone DLL. Keep only one enabled
copy and preserve configuration and saves.

The download includes only this plugin and its documentation. Dependencies,
game assemblies, saves and player configuration are not bundled. SHA-256
checksums are provided as a separate release asset.
