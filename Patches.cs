#nullable enable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SilverpineVerticalLayers;

internal static class UpperFloorShelter
{
    private static readonly Dictionary<int, HashSet<Vector2Int>> Cache = new();
    private static readonly Vector2Int[] Directions =
    {
        Vector2Int.up,
        Vector2Int.right,
        Vector2Int.down,
        Vector2Int.left
    };

    internal static void Invalidate(int level = -1)
    {
        if (level < 0)
            Cache.Clear();
        else
            Cache.Remove(level);
    }

    internal static bool IsEnclosed(int level, Vector2Int canonical)
    {
        if (!Cache.TryGetValue(level, out HashSet<Vector2Int> enclosed))
        {
            enclosed = Build(level);
            Cache[level] = enclosed;
        }
        return enclosed.Contains(canonical);
    }

    private static HashSet<Vector2Int> Build(int level)
    {
        VerticalLayerManager? manager = VerticalLayerManager.Instance;
        if (manager == null)
            return new HashSet<Vector2Int>();
        HashSet<Vector2Int> floors =
            manager.FloorNetwork.GetFloors(level).ToHashSet();
        HashSet<Vector2Int> walls = new();
        foreach (Vector2Int canonical in floors)
        {
            Vector2Int physical = LayerCoordinates.ToPhysical(canonical, level);
            if (Turfs.GetTurfGameObjects(physical).Any(IsWallObject))
                walls.Add(canonical);
        }

        HashSet<Vector2Int> enclosed = new();
        HashSet<Vector2Int> visited = new(walls);
        foreach (Vector2Int seed in floors)
        {
            if (!visited.Add(seed))
                continue;
            List<Vector2Int> component = new() { seed };
            Queue<Vector2Int> queue = new();
            queue.Enqueue(seed);
            bool exposed = false;
            while (queue.Count > 0)
            {
                Vector2Int current = queue.Dequeue();
                foreach (Vector2Int direction in Directions)
                {
                    Vector2Int neighbor = current + direction;
                    if (walls.Contains(neighbor))
                        continue;
                    if (!LayerCoordinates.IsCanonicalSilverpine(neighbor) ||
                        !floors.Contains(neighbor))
                    {
                        exposed = true;
                        continue;
                    }
                    if (!visited.Add(neighbor))
                        continue;
                    component.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
            }
            if (!exposed)
                enclosed.UnionWith(component);
        }
        return enclosed;
    }

    private static bool IsWallObject(GameObject gameObject) =>
        gameObject != null && SerializationManager
            .GetPrefabName(gameObject)
            .StartsWith("prefab_wall", StringComparison.OrdinalIgnoreCase);
}

internal static class UpperLayerLocationIdentity
{
    internal static void Canonicalize(ref Vector2Int position)
    {
        if (LayerCoordinates.TryDecode(
                position, out int level, out Vector2Int canonical) &&
            level > 0)
            position = canonical;
    }
}

[HarmonyPatch(typeof(MapZone), nameof(MapZone.GetPositionZoneName))]
internal static class UpperLayerMapZonePatch
{
    private static void Prefix(ref Vector2Int position) =>
        UpperLayerLocationIdentity.Canonicalize(ref position);
}

[HarmonyPatch(typeof(MapZone), nameof(MapZone.GetNearestMapZone))]
internal static class UpperLayerNearestMapZonePatch
{
    private static void Prefix(ref Vector2Int position) =>
        UpperLayerLocationIdentity.Canonicalize(ref position);
}

[HarmonyPatch(typeof(NeuralNPC), "GetPositionZoneNameAddOutside")]
internal static class VerticalLayerNpcAreaDescriptionPatch
{
    private static void Postfix(NeuralNPC __instance, ref string __result)
    {
        Vector2Int physical = __instance.transform.GetVector2IntPosition();
        if (!LayerCoordinates.TryDecode(physical, out int level, out _))
            return;

        string floorName = GetFloorName(level);
        __result = string.IsNullOrWhiteSpace(__result)
            ? floorName
            : $"{__result} ({floorName})";
    }

    private static string GetFloorName(int level)
    {
        if (level == 0)
            return "Ground Floor";
        int floor = level + 1;
        int lastTwoDigits = floor % 100;
        string suffix = lastTwoDigits is >= 11 and <= 13
            ? "th"
            : (floor % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th"
            };
        return $"{floor}{suffix} Floor";
    }
}

[HarmonyPatch(typeof(PersistenceZone),
    nameof(PersistenceZone.IsInsidePersistenceZone))]
internal static class UpperLayerPersistenceZonePatch
{
    private static void Prefix(ref Vector2Int position) =>
        UpperLayerLocationIdentity.Canonicalize(ref position);
}

[HarmonyPatch(typeof(PersistenceZone),
    nameof(PersistenceZone.IsInsideAnyPersistenceZone))]
internal static class UpperLayerAnyPersistenceZonePatch
{
    private static void Prefix(ref Vector2Int position) =>
        UpperLayerLocationIdentity.Canonicalize(ref position);
}

[HarmonyPatch(typeof(PersistenceZone),
    nameof(PersistenceZone.GetPersistenceZoneName))]
internal static class UpperLayerPersistenceZoneNamePatch
{
    private static void Prefix(ref Vector2Int position) =>
        UpperLayerLocationIdentity.Canonicalize(ref position);
}

[HarmonyPatch(typeof(PersistenceZone),
    nameof(PersistenceZone.GetPositionAssociatedFaction))]
internal static class UpperLayerPersistenceFactionPatch
{
    private static void Prefix(ref Vector2Int position) =>
        UpperLayerLocationIdentity.Canonicalize(ref position);
}

[HarmonyPatch(typeof(EntityMover), nameof(EntityMover.TryMove))]
internal static class UpperFloorMovementPatch
{
    private static bool Prefix(
        EntityMover __instance,
        Direction direction,
        ref bool __result)
    {
        Vector2Int target =
            __instance.transform.GetDirectionPosition(direction);
        if (UpperFloorOccupancy.CanOccupy(target, __instance.Flying))
            return true;

        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(Pathfinding), nameof(Pathfinding.FindPath))]
internal static class UpperFloorPathfindingOccupancyPatch
{
    private static void Prefix(
        bool flying,
        ref Predicate<Vector2Int> specialPathfindingRule)
    {
        Predicate<Vector2Int>? original = specialPathfindingRule;
        specialPathfindingRule = position =>
            (original == null || original(position)) &&
            UpperFloorOccupancy.CanOccupy(position, flying);
    }
}

[HarmonyPatch(typeof(DungeonGenerationManager),
    nameof(DungeonGenerationManager.GetPositionWorldTile))]
internal static class UpperLayerWorldTilePatch
{
    private static bool Prefix(
        DungeonGenerationManager __instance,
        Vector2Int position,
        ref WorldTile __result)
    {
        if (!LayerCoordinates.TryDecode(position, out int level, out _) ||
            level <= 0)
            return true;
        __instance.worldTiles.TryGetValue(Vector2Int.zero, out __result);
        return false;
    }
}

[HarmonyPatch(typeof(WorldInfoManager),
    nameof(WorldInfoManager.IsOnPlayerOwnedPlot))]
internal static class UpperLayerBuildPermissionPatch
{
    private static void Postfix(Vector2Int position, ref bool __result)
    {
        if (LayerCoordinates.TryDecode(position, out int level, out _) &&
            level > 0)
            __result = true;
    }
}

[HarmonyPatch(typeof(WorldInfoManager),
    nameof(WorldInfoManager.IsPlayerOutside))]
internal static class UpperLayerOutsidePatch
{
    private static bool Prefix(ref bool __result)
    {
        if (Player.Instance == null)
            return true;
        Vector2Int position = Player.Instance.transform.GetVector2IntPosition();
        if (!LayerCoordinates.TryDecode(
                position, out int level, out Vector2Int canonical) ||
            level <= 0)
            return true;
        bool roofedHere = TileTags.HasTileTag(position, TileTag.Roofed);
        VerticalLayerManager? manager = VerticalLayerManager.Instance;
        bool floorAbove = level + 1 < Plugin.MaxLevels.Value &&
                          manager?.IsLevelActive(level + 1) == true &&
                          manager.FloorNetwork.HasFloor(
                              level + 1, canonical);
        bool enclosed = UpperFloorShelter.IsEnclosed(level, canonical);
        __result = !roofedHere && !floorAbove && !enclosed;
        return false;
    }
}

[HarmonyPatch(typeof(SerializationManager), "GetGameObjectsForSerialization")]
internal static class UpperLayerSavePatch
{
    private static void Postfix(ref HashSet<GameObject> __result)
    {
        __result ??= new HashSet<GameObject>();
        __result.RemoveWhere(PrefabFactory.IsTemplate);
        foreach (GameObject gameObject in
                 UtilityFunctions.GetGameObjectsWithComponent<ISerializableMonoBehavior>())
        {
            if (gameObject == null || PrefabFactory.IsTemplate(gameObject))
                continue;
            if (LayerCoordinates.TryDecode(
                    gameObject.transform.GetVector2IntPosition(),
                    out int level,
                    out _) && level > 0)
                __result.Add(gameObject);
        }
    }
}

[HarmonyPatch(typeof(SerializationManager),
    nameof(SerializationManager.DeserializeSerializable))]
internal static class UpperLayerLoadPatch
{
    private static void Postfix(List<GameObject> __result)
    {
        if (__result != null)
            foreach (GameObject gameObject in __result)
                PrefabFactory.RestoreInstance(gameObject);
        VerticalLayerManager.Instance?.FloorNetwork.Invalidate();
        UpperFloorShelter.Invalidate();
        VerticalLayerManager.Instance?.Renderer.MarkFloorMaskDirty();
    }
}

[HarmonyPatch(typeof(Deconstructable), nameof(Deconstructable.Deconstruct))]
internal static class VerticalDeconstructionPatch
{
    private static bool Prefix(Deconstructable __instance, ref Vector2Int __state)
    {
        __state = __instance.transform.GetVector2IntPosition();
        VerticalLayerManager? manager = VerticalLayerManager.Instance;
        if (manager == null || manager.CanDeconstruct(__instance.gameObject, out string reason))
            return true;
        UpperNotificationUI.Instance.OneOff(reason);
        return false;
    }

    private static void Postfix(Vector2Int __state)
    {
        VerticalLayerManager? manager = VerticalLayerManager.Instance;
        if (manager == null)
            return;
        manager.FloorNetwork.Invalidate();
        if (LayerCoordinates.TryDecode(__state, out int level, out _))
            UpperFloorShelter.Invalidate(level);
        manager.QueueProjectionForPhysical(__state);
        manager.Renderer.MarkFloorMaskDirty();
    }
}

[HarmonyPatch]
internal static class TurfLifecyclePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(TurfRegistrar), "Start");
        yield return AccessTools.Method(typeof(TurfRegistrar), "OnDestroy");
        yield return AccessTools.Method(typeof(TurfRegistrar),
            nameof(TurfRegistrar.Register));
    }

    private static void Postfix(TurfRegistrar __instance)
    {
        NotifyChanged(__instance.transform.GetVector2IntPosition());
    }

    internal static void NotifyChanged(Vector2Int position)
    {
        VerticalLayerManager? manager = VerticalLayerManager.Instance;
        if (manager == null)
            return;
        manager.FloorNetwork.Invalidate();
        if (LayerCoordinates.TryDecode(position, out int level, out _))
            UpperFloorShelter.Invalidate(level);
        manager.QueueProjectionForPhysical(position);
        manager.Renderer.MarkFloorMaskDirty();
    }
}

[HarmonyPatch]
internal static class TileTagLifecyclePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(TileTags), "Awake");
        yield return AccessTools.Method(typeof(TileTags), "Start");
        yield return AccessTools.Method(typeof(TileTags), "OnDestroy");
        yield return AccessTools.Method(typeof(TileTags),
            nameof(TileTags.Reregister));
        yield return AccessTools.Method(typeof(TileTags),
            nameof(TileTags.Unregister));
    }

    private static void Postfix(TileTags __instance) =>
        TurfLifecyclePatch.NotifyChanged(
            __instance.transform.GetVector2IntPosition());
}

[HarmonyPatch(typeof(Deroofer), "Start")]
internal static class DerooferProjectionPatch
{
    private static void Postfix(Deroofer __instance) =>
        TurfLifecyclePatch.NotifyChanged(
            __instance.transform.GetVector2IntPosition());
}

[HarmonyPatch(typeof(PathfindingEntity),
    nameof(PathfindingEntity.SetTargetPosition))]
internal static class CrossLayerPathfindingPatch
{
    private static bool Prefix(
        PathfindingEntity __instance,
        Vector2Int targetPosition)
    {
        if (CrossLayerRoutingGuard.Active)
            return true;
        Vector2Int source = __instance.transform.GetVector2IntPosition();
        if (!LayerCoordinates.TryDecode(source, out int sourceLevel, out _) ||
            !LayerCoordinates.TryDecode(targetPosition, out int targetLevel, out _))
            return true;

        if (sourceLevel == targetLevel)
        {
            // A player stair transition can move a follower directly to the
            // destination while its previous cross-floor route is still
            // active. A same-floor follow request supersedes that stale route.
            __instance.GetComponent<CrossLayerNavigator>()?.Cancel();
            return true;
        }

        CrossLayerNavigator navigator =
            __instance.GetComponent<CrossLayerNavigator>() ??
            __instance.gameObject.AddComponent<CrossLayerNavigator>();
        if (navigator.Begin(targetPosition))
            return false;

        Plugin.Log.LogDebug(
            $"Waiting to retry a connected stair route from {source} to " +
            $"{targetPosition} for {__instance.name}.");
        return false;
    }
}

[HarmonyPatch(typeof(NPCRoutineExecutor), "OnReachedTargetPosition")]
internal static class CrossLayerRoutineArrivalPatch
{
    private static bool Prefix(NPCRoutineExecutor __instance)
    {
        CrossLayerNavigator? navigator =
            __instance.GetComponent<CrossLayerNavigator>();
        return navigator?.AtIntermediatePortal != true;
    }
}

[HarmonyPatch(typeof(NPCRoutineExecutor), "OnPostProcessTurn")]
internal static class CrossLayerRoutineUnstuckPatch
{
    private static void Prefix(NPCRoutineExecutor __instance)
    {
        CrossLayerNavigator? navigator =
            __instance.GetComponent<CrossLayerNavigator>();
        if (navigator?.Active == true)
        {
            Traverse.Create(__instance).Field("unstuckTimer").SetValue(0);
            navigator.RetryPendingRoute();
        }
    }
}

internal static class ConstructionPatchContext
{
    [ThreadStatic]
    internal static string? CurrentPrefab;
}

[HarmonyPatch]
internal static class VanillaConstructionContextPatch
{
    private static MethodBase TargetMethod()
    {
        Type nested = AccessTools.Inner(typeof(PlayerAbility_Construct),
                          "ConstructionItem") ??
                      throw new MissingMemberException(
                          "PlayerAbility_Construct.ConstructionItem");
        return AccessTools.Method(nested, "StartPlacementMode") ??
               throw new MissingMethodException(
                   nested.FullName, "StartPlacementMode");
    }

    private static void Prefix(object __instance)
    {
        ConstructionPatchContext.CurrentPrefab =
            Traverse.Create(__instance).Field("prefabName").GetValue<string>();
    }

    private static void Finalizer() =>
        ConstructionPatchContext.CurrentPrefab = null;
}

[HarmonyPatch(typeof(PlacementModeManager),
    nameof(PlacementModeManager.StartPlacementMode))]
internal static class UpperFloorPlacementPatch
{
    private static void Prefix(ref Predicate<Vector2> predicate)
    {
        string? prefabName = ConstructionPatchContext.CurrentPrefab;
        if (string.IsNullOrWhiteSpace(prefabName) ||
            !prefabName.StartsWith("prefab_tile_", StringComparison.OrdinalIgnoreCase))
            return;

        Predicate<Vector2>? original = predicate;
        predicate = position =>
        {
            if (original != null && !original(position))
                return false;
            Vector2Int physical = Vector2Int.RoundToInt(position);
            if (!LayerCoordinates.TryDecode(
                    physical,
                    out int level,
                    out Vector2Int canonical) || level <= 0)
                return true;
            VerticalLayerManager? manager = VerticalLayerManager.Instance;
            return manager != null && manager.FloorNetwork.CanPlaceFloor(
                level, canonical, out _);
        };
    }
}
