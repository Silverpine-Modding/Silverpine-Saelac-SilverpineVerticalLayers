using SilverpineVerticalLayers;
using UnityEngine;

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition)
        throw new Exception(message);
}
GameObject Floor(int x, bool projected = false, int level = 1)
{
    GameObject obj = new(projected ? "svl_projected_floor(Clone)" : "prefab_tile_wood(Clone)");
    if (projected)
        obj.Add(new ProjectedUpperFloor { SourceLevel = level - 1 });
    Turfs.Add(obj, LayerCoordinates.ToPhysical(new(x, 0), level));
    return obj;
}

foreach (string name in new[]
{
    "prefab_tile_wood", "prefab_tile_rock(Clone)", "prefab_tile_wood water label",
    "prefab_tile_wood(water)", "PREFAB_TILE_WOOD", "prefab_tile_water(Clone)",
    "prefab_tile_waterfall", "prefab_tile_TELEPORTER custom", "prefab_tile_",
    "prefab_tile_teleporter", "prefab_tile_water (Clone)", "prefab_furniture_table",
    "prefab_tile_wood water teleporter", "prefab_tile_wood(teleporter) water", ""
})
{
    string baseline = name.Split(' ', '(')[0];
    bool expected = baseline.StartsWith("prefab_tile_", StringComparison.OrdinalIgnoreCase) &&
                    !baseline.Contains("water", StringComparison.OrdinalIgnoreCase) &&
                    !baseline.Contains("teleporter", StringComparison.OrdinalIgnoreCase);
    Check(FloorNetwork.IsFloorPrefabName(name) == expected, "Prefab classification changed: " + name);
}

Turfs.Clear();
FloorNetwork network = new();
const int projectedCount = 1000;
for (int index = 0; index < projectedCount; index++)
{
    Vector2Int canonical = new(index % 100 - 50, index / 100 - 50);
    Check(!network.HasFloor(1, canonical), "Expected empty projection tile");
    GameObject projection = new("svl_projected_floor(Clone)");
    projection.Add(new ProjectedUpperFloor());
    Turfs.Add(projection, LayerCoordinates.ToPhysical(canonical, 1));
    network.Invalidate(1);
}
Check(Turfs.Probes == projectedCount, "Presence checks rebuilt a town-wide snapshot");
Check(Turfs.EmptyListAllocations == 0, "Presence checks allocated empty turf lists");
Console.WriteLine($"Projection presence checks: {Turfs.Probes:N0} tile probes for {projectedCount:N0} floors (previously 10,000,000 snapshot probes).");

Turfs.Clear();
network = new();
Floor(0, projected: true);
Floor(1);
Floor(2);
Floor(3);
Check(network.CanPlaceFloor(1, new(4, 0), out _), "Fourth tile must remain buildable");
int firstSnapshotProbes = Turfs.Probes;
Check(firstSnapshotProbes == 10000, "Unexpected initial snapshot scan size");
Check(network.GetDistance(1, new(3, 0)) == 3, "Incorrect support distance");
Check(Turfs.Probes == firstSnapshotProbes, "Cached support distance rescanned town");
network.Invalidate(2);
Check(network.GetDistance(1, new(3, 0)) == 3 && Turfs.Probes == firstSnapshotProbes,
    "Invalidating a different level discarded this snapshot");
Floor(4);
network.Invalidate(1);
Check(!network.CanPlaceFloor(1, new(5, 0), out _), "Fifth unsupported tile must be rejected");
Check(Turfs.EmptyListAllocations == 0, "Support snapshots allocated lists for empty cells");

GameObject pillar = new("svl_support(Clone)");
pillar.Add(new VerticalSupport { IsAnchor = false, PairId = "lower", AnchorLevel = 1 });
pillar.Add(new VerticalSupport { IsAnchor = true, PairId = "support-a", AnchorLevel = 1 });
Turfs.Add(pillar, LayerCoordinates.ToPhysical(new(4, 0), 1));
network.Invalidate(1);
Check(network.CanPlaceFloor(1, new(5, 0), out _), "Support did not extend build range");
Check(network.GetDistance(1, new(4, 0)) == 0, "Multiple support components lost the anchor");
GameObject fifth = Floor(5);
network.Invalidate(1);
Check(network.GetDistance(1, new(5, 0)) == 1, "Expected one tile from added support");
// Separate normal pillar for the pair-exclusion/deconstruction test.
Turfs.Remove(pillar);
pillar = new("svl_support(Clone)");
pillar.Add(new VerticalSupport { IsAnchor = true, PairId = "support-a", AnchorLevel = 1 });
Turfs.Add(pillar, LayerCoordinates.ToPhysical(new(4, 0), 1));
network.Invalidate(1);
Check(!network.CanDeconstruct(pillar, out _), "Removing support incorrectly allowed unsupported floor");
Turfs.Remove(fifth);
Check(!network.HasFloor(1, new(5, 0)), "Presence lookup used stale support snapshot");
network.Invalidate(1);
Check(network.CanDeconstruct(pillar, out _), "Support should be removable once dependent floor is gone");

Vector2Int air = LayerCoordinates.ToPhysical(new(10, 0), 1);
Check(!UpperFloorOccupancy.CanOccupy(air, false), "Walking into upper-floor air became possible");
Check(UpperFloorOccupancy.CanOccupy(air, true), "Flying in upper-floor air changed");
Check(UpperFloorOccupancy.CanOccupy(new(10, 0), false), "Ground occupancy behavior changed");
Check(UpperFloorOccupancy.CanOccupy(LayerCoordinates.ToPhysical(new(0, 0), 1), false),
    "Projected floor stopped being walkable");
GameObject doorFloor = new("prefab_tile_wood(Clone)");
doorFloor.Add(new Door());
Turfs.Add(doorFloor, air);
Check(!UpperFloorOccupancy.HasActualFloor(air), "Door must not count as supporting terrain");
Turfs.Remove(doorFloor);
Floor(0, projected: true, level: 2);
Check(network.HasFloor(2, new(0, 0)), "Third-floor coordinate lookup failed");
Check(!network.HasFloor(1, new(50, 0)), "Out-of-bounds support lookup changed");

// Warm the linked production occupancy path before measuring managed allocation.
for (int index = 0; index < 1000; index++)
    UpperFloorOccupancy.HasActualFloor(air);
long startBytes = GC.GetAllocatedBytesForCurrentThread();
for (int index = 0; index < 10000; index++)
    UpperFloorOccupancy.HasActualFloor(air);
long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
Check(allocatedBytes == 0, "Empty-air checks unexpectedly allocate managed memory");
Console.WriteLine($"Empty-air occupancy: {allocatedBytes} managed bytes across 10,000 checks in the test world.");
Console.WriteLine($"PASS: {assertions} assertions. Unity rendering/lifecycle still requires an in-game check.");
