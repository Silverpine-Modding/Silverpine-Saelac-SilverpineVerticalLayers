#nullable enable

using UnityEngine;

namespace SilverpineVerticalLayers;

internal static class UpperFloorOccupancy
{
    internal static bool HasActualFloor(Vector2Int physical)
    {
        // Called for every pathfinding candidate and during bulk roof generation.
        // An empty tile must not allocate a list or build a town-wide support graph.
        if (!Turfs.TryGetTurfGameObjects(physical, out var objects))
            return false;
        for (int index = 0; index < objects.Count; index++)
            if (FloorNetwork.IsFloorObject(objects[index]))
                return true;
        return false;
    }

    internal static bool CanOccupy(Vector2Int physical, bool flying)
    {
        if (flying ||
            !LayerCoordinates.TryDecode(physical, out int level, out _))
            return true;
        return level <= 0 || HasActualFloor(physical);
    }
}
