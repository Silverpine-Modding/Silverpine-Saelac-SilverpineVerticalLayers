#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SilverpineVerticalLayers;

internal sealed class FloorNetwork
{
    private sealed class Snapshot
    {
        internal readonly HashSet<Vector2Int> Floors = new();
        internal readonly HashSet<Vector2Int> Anchors = new();
        internal readonly Dictionary<Vector2Int, int> Distances = new();
    }

    private readonly Dictionary<int, Snapshot> cache = new();
    private static readonly Vector2Int[] Directions =
    {
        Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left
    };

    internal void Invalidate(int level = -1)
    {
        if (level < 0)
            cache.Clear();
        else
            cache.Remove(level);
    }

    internal bool HasFloor(int level, Vector2Int canonical) =>
        LayerCoordinates.IsCanonicalSilverpine(canonical) &&
        UpperFloorOccupancy.HasActualFloor(LayerCoordinates.ToPhysical(canonical, level));

    internal IEnumerable<Vector2Int> GetFloors(int level) =>
        Get(level).Floors;

    internal bool HasFloorPhysical(Vector2Int physical)
    {
        if (!LayerCoordinates.TryDecode(physical, out int level, out Vector2Int canonical))
            return false;
        return HasFloor(level, canonical);
    }

    internal bool CanPlaceFloor(
        int level,
        Vector2Int canonical,
        out string reason)
    {
        if (level <= 0)
        {
            reason = "Upper floors can only be extended while standing above ground level.";
            return false;
        }
        if (!LayerCoordinates.IsCanonicalSilverpine(canonical))
        {
            reason = "That position is outside the Silverpine layer bounds.";
            return false;
        }
        Snapshot snapshot = Get(level);
        if (snapshot.Floors.Contains(canonical))
        {
            reason = "There is already a floor tile here.";
            return false;
        }

        int best = int.MaxValue;
        foreach (Vector2Int direction in Directions)
        {
            Vector2Int neighbor = canonical + direction;
            if (snapshot.Distances.TryGetValue(neighbor, out int distance))
                best = Math.Min(best, distance + 1);
        }
        if (best == int.MaxValue)
        {
            reason = "The new floor must connect to an existing supported floor.";
            return false;
        }
        if (best > Plugin.MaximumUnsupportedSpan.Value)
        {
            reason = $"A floor can extend only {Plugin.MaximumUnsupportedSpan.Value} tiles from support.";
            return false;
        }
        reason = "";
        return true;
    }

    internal bool CanPlaceSupport(
        int level,
        Vector2Int canonical,
        out string reason)
    {
        if (level <= 0 || !HasFloor(level, canonical))
        {
            reason = "Supports must be placed beneath an existing upper floor.";
            return false;
        }
        Vector2Int upperPhysical = LayerCoordinates.ToPhysical(canonical, level);
        if (Turfs.GetTurfComponents<VerticalSupport>(upperPhysical)
            .Any(support => support.IsAnchor))
        {
            reason = "This floor already has a support.";
            return false;
        }

        Vector2Int lowerPhysical = LayerCoordinates.ToPhysical(canonical, level - 1);
        if (level - 1 == 0)
        {
            if (!Turfs.IsValidTurf(lowerPhysical) ||
                TileTags.HasTileTag(lowerPhysical, TileTag.Submerge))
            {
                reason = "The pillar needs solid ground below it.";
                return false;
            }
        }
        else if (!HasFloor(level - 1, canonical))
        {
            reason = "The pillar needs a supported floor on the level below.";
            return false;
        }

        if (TurfCollider.HasImpassableCollider(lowerPhysical))
        {
            reason = "Something blocks the pillar on the level below.";
            return false;
        }
        reason = "";
        return true;
    }

    internal bool CanDeconstruct(GameObject target, out string reason)
    {
        Vector2Int physical = target.transform.GetVector2IntPosition();
        if (!LayerCoordinates.TryDecode(
                physical,
                out int level,
                out Vector2Int canonical))
        {
            reason = "";
            return true;
        }

        bool isFloor = IsFloorObject(target);
        VerticalSupport? support = target.GetComponent<VerticalSupport>();
        if (support != null && !support.IsAnchor)
            level = support.AnchorLevel;

        if (level == 0 && support == null)
        {
            TileTags? tags = target.GetComponent<TileTags>();
            if (tags != null && tags.tags.Contains(TileTag.Roofed) &&
                VerticalLayerManager.Instance?.IsLevelActive(1) == true)
            {
                Vector2Int upper = LayerCoordinates.ToPhysical(canonical, 1);
                ProjectedUpperFloor? projection = Turfs
                    .GetTurfComponents<ProjectedUpperFloor>(upper)
                    .FirstOrDefault(projected => projected.SourceLevel == 0);
                if (projection != null)
                {
                    if (HasDependentObjects(projection.gameObject, upper))
                    {
                        reason = "Remove objects standing on the upper floor first.";
                        return false;
                    }
                    Snapshot withoutProjection =
                        BuildSnapshot(1, canonical, null);
                    return ValidateSupportDistances(withoutProjection, out reason);
                }
            }
            reason = "";
            return true;
        }

        ProjectedUpperFloor? projectedFloor =
            target.GetComponent<ProjectedUpperFloor>();
        if (projectedFloor != null)
        {
            Vector2Int source = LayerCoordinates.ToPhysical(
                canonical, projectedFloor.SourceLevel);
            if (TileTags.HasTileTag(source, TileTag.Roofed))
            {
                reason = "This floor is generated by the roofed tile below it.";
                return false;
            }
        }

        if (!isFloor && support == null)
        {
            reason = "";
            return true;
        }

        if (isFloor && HasDependentObjects(target, physical))
        {
            reason = "Remove objects standing on this floor before deconstructing it.";
            return false;
        }

        Snapshot candidate = BuildSnapshot(
            level,
            isFloor ? canonical : null,
            support?.PairId);
        return ValidateSupportDistances(candidate, out reason);
    }

    internal int GetDistance(int level, Vector2Int canonical) =>
        Get(level).Distances.TryGetValue(canonical, out int distance)
            ? distance
            : int.MaxValue;

    internal static bool IsFloorObject(GameObject gameObject)
    {
        if (gameObject == null)
            return false;
        if (gameObject.GetComponent<ProjectedUpperFloor>() != null)
            return true;
        return IsFloorPrefabName(gameObject.name) &&
               gameObject.GetComponent<Door>() == null;
    }

    internal static bool IsFloorPrefabName(string objectName)
    {
        // Match GetPrefabName's first space/'(' delimiter without Split's arrays
        // and substrings on every movement/pathfinding occupancy check.
        if (!objectName.StartsWith("prefab_tile_", StringComparison.OrdinalIgnoreCase))
            return false;
        int end = objectName.Length;
        int space = objectName.IndexOf(' ');
        int parenthesis = objectName.IndexOf('(');
        if (space >= 0)
            end = space;
        if (parenthesis >= 0)
            end = Math.Min(end, parenthesis);
        return objectName.IndexOf("water", 0, end, StringComparison.OrdinalIgnoreCase) < 0 &&
               objectName.IndexOf("teleporter", 0, end, StringComparison.OrdinalIgnoreCase) < 0;
    }

    private Snapshot Get(int level)
    {
        if (!cache.TryGetValue(level, out Snapshot? snapshot))
        {
            snapshot = BuildSnapshot(level, null, null);
            cache[level] = snapshot;
        }
        return snapshot;
    }

    private static Snapshot BuildSnapshot(
        int level,
        Vector2Int? excludedFloor,
        string? excludedSupportPair)
    {
        Snapshot snapshot = new();
        List<VerticalSupport> supports = new();
        for (int x = LayerCoordinates.CanonicalMin;
             x <= LayerCoordinates.CanonicalMax;
             x++)
        {
            for (int y = LayerCoordinates.CanonicalMin;
                 y <= LayerCoordinates.CanonicalMax;
                 y++)
            {
                Vector2Int canonical = new(x, y);
                if (excludedFloor.HasValue && excludedFloor.Value == canonical)
                    continue;
                Vector2Int physical = LayerCoordinates.ToPhysical(canonical, level);
                if (!Turfs.TryGetTurfGameObjects(physical, out List<GameObject> objects))
                    continue;
                bool hasFloor = false;
                bool anchored = false;
                foreach (GameObject gameObject in objects)
                {
                    if (gameObject == null)
                        continue;
                    if (!hasFloor && IsFloorObject(gameObject))
                        hasFloor = true;
                    if (anchored)
                        continue;
                    if (gameObject.GetComponent<ProjectedUpperFloor>() != null)
                    {
                        anchored = true;
                        continue;
                    }
                    gameObject.GetComponents(supports);
                    foreach (VerticalSupport support in supports)
                        if (support.IsAnchor && support.PairId != excludedSupportPair)
                        {
                            anchored = true;
                            break;
                        }
                }
                if (!hasFloor)
                    continue;
                snapshot.Floors.Add(canonical);
                if (anchored)
                    snapshot.Anchors.Add(canonical);
            }
        }

        Queue<Vector2Int> queue = new();
        foreach (Vector2Int anchor in snapshot.Anchors)
        {
            snapshot.Distances[anchor] = 0;
            queue.Enqueue(anchor);
        }
        while (queue.Count > 0)
        {
            Vector2Int current = queue.Dequeue();
            int nextDistance = snapshot.Distances[current] + 1;
            foreach (Vector2Int direction in Directions)
            {
                Vector2Int neighbor = current + direction;
                if (!snapshot.Floors.Contains(neighbor) ||
                    snapshot.Distances.TryGetValue(neighbor, out int existing) &&
                    existing <= nextDistance)
                    continue;
                snapshot.Distances[neighbor] = nextDistance;
                queue.Enqueue(neighbor);
            }
        }
        return snapshot;
    }

    private static bool HasDependentObjects(
        GameObject floor,
        Vector2Int physical)
    {
        foreach (GameObject gameObject in Turfs.GetTurfGameObjects(physical))
        {
            if (gameObject == null || gameObject == floor ||
                gameObject == Player.Instance?.gameObject ||
                IsFloorObject(gameObject))
                continue;
            return true;
        }
        return false;
    }

    private static bool ValidateSupportDistances(
        Snapshot candidate,
        out string reason)
    {
        foreach (Vector2Int floor in candidate.Floors)
        {
            if (candidate.Distances.TryGetValue(floor, out int distance) &&
                distance <= Plugin.MaximumUnsupportedSpan.Value)
                continue;
            reason = $"This piece supports construction beyond the " +
                     $"{Plugin.MaximumUnsupportedSpan.Value}-tile limit.";
            return false;
        }
        reason = "";
        return true;
    }
}
