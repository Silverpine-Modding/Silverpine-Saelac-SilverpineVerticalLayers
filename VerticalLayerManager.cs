#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SilverpineVerticalLayers;

public sealed class VerticalLayerManager : MonoBehaviour,
    IPersistentSerializableMonoBehavior
{
    private readonly FloorNetwork floorNetwork = new();
    private readonly HashSet<(int SourceLevel, Vector2Int Canonical)>
        dirtyProjectionCells = new();
    private readonly List<(int SourceLevel, Vector2Int Canonical)>
        projectionWork = new();
    private int activeLevelMask = 1;
    private bool transitionInProgress;
    private bool suppressPairRemoval;

    public static VerticalLayerManager? Instance { get; private set; }
    internal LowerLayerRenderer Renderer { get; private set; } = null!;
    internal FloorNetwork FloorNetwork => floorNetwork;

    internal static void Attach(GameObject persistentHost)
    {
        VerticalLayerManager manager =
            persistentHost.GetComponent<VerticalLayerManager>() ??
            persistentHost.AddComponent<VerticalLayerManager>();
        Instance = manager;
        manager.enabled = true;
        if (manager.Renderer == null)
            manager.Renderer =
                persistentHost.GetComponent<LowerLayerRenderer>() ??
                persistentHost.AddComponent<LowerLayerRenderer>();
    }

    private void Awake()
    {
        Instance = this;
        Renderer = GetComponent<LowerLayerRenderer>() ??
                   gameObject.AddComponent<LowerLayerRenderer>();
        Plugin.Log.LogInfo(
            "Initialized the persistent Vertical Layer Manager on " +
            gameObject.name + ".");
    }

    private void Start()
    {
        if (ActionQueue.Instance != null)
            ActionQueue.Instance.OnPostProcessTurn += OnPostProcessTurn;
        SerializationManager.On2FramesAfterFinishedLoading += AfterLoad;
        AfterLoad();
    }

    private void OnDestroy()
    {
        Plugin.Log.LogDebug(
            "Vertical Layer Manager was destroyed (loadingSave=" +
            SerializationManager.loadingSave + ", host=" + gameObject.name + ").");
        if (ActionQueue.Instance != null)
            ActionQueue.Instance.OnPostProcessTurn -= OnPostProcessTurn;
        SerializationManager.On2FramesAfterFinishedLoading -= AfterLoad;
        if (ReferenceEquals(Instance, this))
            Instance = null;
    }

    private void Update()
    {
        ProcessDirtyProjectionCells();
    }

    private void LateUpdate()
    {
        Renderer.Tick();
    }

    internal int CurrentPlayerLevel =>
        Player.Instance == null
            ? 0
            : LayerCoordinates.GetLevel(
                Player.Instance.transform.GetVector2IntPosition());

    internal bool IsLevelActive(int level) =>
        level >= 0 && (activeLevelMask & (1 << level)) != 0;

    internal IEnumerable<int> ActiveLevels =>
        Enumerable.Range(0, Plugin.MaxLevels.Value)
            .Where(IsLevelActive);

    internal void ActivateLevel(int level)
    {
        if (level <= 0 || level >= Plugin.MaxLevels.Value)
            return;
        if (IsLevelActive(level))
            return;
        activeLevelMask |= 1 << level;
        RebuildProjection(level - 1);
        floorNetwork.Invalidate(level);
        Renderer.MarkFloorMaskDirty();
    }

    internal void RebuildProjection(int sourceLevel)
    {
        int destinationLevel = sourceLevel + 1;
        if (!IsLevelActive(destinationLevel))
            return;
        for (int x = LayerCoordinates.CanonicalMin;
             x <= LayerCoordinates.CanonicalMax;
             x++)
        {
            for (int y = LayerCoordinates.CanonicalMin;
                 y <= LayerCoordinates.CanonicalMax;
                 y++)
                ReconcileProjection(sourceLevel, new Vector2Int(x, y));
        }
        floorNetwork.Invalidate(destinationLevel);
        Renderer.MarkFloorMaskDirty();
    }

    internal void QueueProjectionForPhysical(Vector2Int physical)
    {
        if (!LayerCoordinates.TryDecode(
                physical,
                out int sourceLevel,
                out Vector2Int canonical) ||
            !IsLevelActive(sourceLevel + 1))
            return;
        dirtyProjectionCells.Add((sourceLevel, canonical));
    }

    internal bool TryBuildStairPair(
        Vector2Int sourcePhysical,
        int rotation,
        out string reason)
    {
        if (!LayerCoordinates.TryDecode(
                sourcePhysical,
                out int sourceLevel,
                out Vector2Int canonical) ||
            !LayerCoordinates.IsCanonicalSilverpine(canonical))
        {
            reason = "Transition stairs can only be built on the Silverpine tile.";
            return false;
        }
        int destinationLevel = sourceLevel + 1;
        if (destinationLevel >= Plugin.MaxLevels.Value)
        {
            reason = "The configured maximum number of floors has been reached.";
            return false;
        }
        if (!TileTags.HasTileTag(sourcePhysical, TileTag.Roofed))
        {
            reason = "Transition stairs must be placed on a roofed floor.";
            return false;
        }
        if (Turfs.GetTurfComponents<FloorPortal>(sourcePhysical).Count > 0)
        {
            reason = "There is already a floor transition here.";
            return false;
        }

        ActivateLevel(destinationLevel);
        Vector2Int destinationPhysical =
            LayerCoordinates.ToPhysical(canonical, destinationLevel);
        if (!floorNetwork.HasFloor(destinationLevel, canonical))
        {
            PrefabFactory.InstantiateProjectedFloor(
                destinationPhysical,
                sourceLevel,
                canonical);
            floorNetwork.Invalidate(destinationLevel);
        }

        string pairId = Guid.NewGuid().ToString("N");
        PrefabFactory.InstantiateStairs(
            sourcePhysical,
            pairId,
            destinationLevel,
            canonical,
            rotation);
        PrefabFactory.InstantiateStairs(
            destinationPhysical,
            pairId,
            sourceLevel,
            canonical,
            (rotation + 2) % 4);
        Renderer.MarkFloorMaskDirty();
        reason = "";
        return true;
    }

    internal bool TryBuildUpperFloor(
        Vector2Int physical,
        out string reason)
    {
        reason = "";
        if (!LayerCoordinates.TryDecode(
                physical,
                out int level,
                out Vector2Int canonical) ||
            !floorNetwork.CanPlaceFloor(level, canonical, out reason))
            return false;

        GameObject prefab = SerializationManager.GetPrefabFromName("prefab_tile_wood");
        GameObject floor = UnityEngine.Object.Instantiate(
            prefab,
            new Vector3(physical.x, physical.y, prefab.transform.position.z),
            prefab.transform.rotation);
        foreach (IConstructedHandler handler in floor.GetComponents<IConstructedHandler>())
            handler.OnConstructed();
        floorNetwork.Invalidate(level);
        QueueProjectionForPhysical(physical);
        Renderer.MarkFloorMaskDirty();
        return true;
    }

    internal bool TryBuildSupport(
        Vector2Int upperPhysical,
        out string reason)
    {
        reason = "";
        if (!LayerCoordinates.TryDecode(
                upperPhysical,
                out int level,
                out Vector2Int canonical) ||
            !floorNetwork.CanPlaceSupport(level, canonical, out reason))
            return false;

        string pairId = Guid.NewGuid().ToString("N");
        PrefabFactory.InstantiateSupport(
            upperPhysical,
            pairId,
            level,
            isAnchor: true);
        PrefabFactory.InstantiateSupport(
            LayerCoordinates.ToPhysical(canonical, level - 1),
            pairId,
            level,
            isAnchor: false);
        floorNetwork.Invalidate(level);
        Renderer.MarkFloorMaskDirty();
        return true;
    }

    internal void BeginPlayerTransition(FloorPortal portal)
    {
        if (transitionInProgress || BlackScreen.Instance.IsActive())
            return;
        StartCoroutine(PlayerTransition(portal));
    }

    internal void TeleportEntityThroughPortal(GameObject entity, FloorPortal portal)
    {
        Vector2Int destination = LayerCoordinates.ToPhysical(
            portal.DestinationCanonical,
            portal.DestinationLevel);
        EntityMover? mover = entity.GetComponent<EntityMover>();
        if (mover != null)
            mover.Teleport(destination);
        else
            entity.transform.position = new Vector3(
                destination.x,
                destination.y,
                entity.transform.position.z);
    }

    internal void RemovePortalPair(string pairId, FloorPortal except)
    {
        if (suppressPairRemoval || string.IsNullOrEmpty(pairId))
            return;
        suppressPairRemoval = true;
        try
        {
            foreach (FloorPortal portal in FloorPortal.All
                         .Where(portal => portal != except && portal.PairId == pairId)
                         .ToArray())
                if (portal != null)
                    DestroyImmediate(portal.gameObject);
        }
        finally
        {
            suppressPairRemoval = false;
        }
        Renderer.MarkFloorMaskDirty();
    }

    internal void RemoveSupportPair(string pairId, VerticalSupport except)
    {
        if (suppressPairRemoval || string.IsNullOrEmpty(pairId))
            return;
        suppressPairRemoval = true;
        try
        {
            foreach (VerticalSupport support in VerticalSupport.All
                         .Where(support => support != except && support.PairId == pairId)
                         .ToArray())
                if (support != null)
                    DestroyImmediate(support.gameObject);
        }
        finally
        {
            suppressPairRemoval = false;
        }
        floorNetwork.Invalidate();
        Renderer.MarkDirty();
    }

    internal bool CanDeconstruct(GameObject target, out string reason) =>
        floorNetwork.CanDeconstruct(target, out reason);

    internal void RebuildAllActiveProjections()
    {
        for (int destinationLevel = 1;
             destinationLevel < Plugin.MaxLevels.Value;
             destinationLevel++)
            if (IsLevelActive(destinationLevel))
                RebuildProjection(destinationLevel - 1);
        floorNetwork.Invalidate();
        Renderer.MarkDirty();
    }

    internal void EmergencyReturnToGround()
    {
        if (Player.Instance == null || CurrentPlayerLevel <= 0)
            return;
        Vector2Int current = Player.Instance.transform.GetVector2IntPosition();
        Vector2Int canonical = LayerCoordinates.GetCanonical(current);
        Player.Instance.GetComponent<EntityMover>()?.Teleport(canonical);
        TransferFollowingNpcsToPlayerFloor();
        Renderer.MarkDirty();
    }

    internal void FinishStructureHandlerImport(
        IEnumerable<GameObject> importedObjects)
    {
        int highestLevel = 0;
        foreach (GameObject gameObject in importedObjects)
        {
            if (gameObject == null || !LayerCoordinates.TryDecode(
                    gameObject.transform.GetVector2IntPosition(),
                    out int level,
                    out _))
                continue;
            highestLevel = Math.Max(highestLevel, level);
        }
        for (int level = 1; level <= highestLevel; level++)
            ActivateLevel(level);
        floorNetwork.Invalidate();
        RebuildAllActiveProjections();
        Renderer.MarkDirty();
    }

    private IEnumerator PlayerTransition(FloorPortal portal)
    {
        transitionInProgress = true;
        Player.Instance.SetInputBlock(true);
        yield return BlackScreen.Instance.FadeInCoroutine();
        TeleportEntityThroughPortal(Player.Instance.gameObject, portal);
        TransferFollowingNpcsToPlayerFloor();
        Renderer.MarkDirty();
        yield return null;
        yield return null;
        Player.Instance.SetInputBlock(false);
        yield return BlackScreen.Instance.FadeOutCoroutine();
        transitionInProgress = false;
    }

    private static void TransferFollowingNpcsToPlayerFloor()
    {
        if (Player.Instance == null)
            return;
        Vector2Int playerPosition =
            Player.Instance.transform.GetVector2IntPosition();
        List<Vector2Int> available = PathfindingEntity
            .FindFreeTurfsAroundPosition3x3(playerPosition)
            .Where(position => UpperFloorOccupancy.CanOccupy(
                position, flying: false))
            .OrderBy(_ => Guid.NewGuid())
            .ToList();
        List<NeuralNPC> followers = NeuralNPC.neuralNPCs.Values
            .Where(IFollowingPlayerNPCRoutineArgument
                .DoesNeuralNPCHaveIFollowingPlayerNPCRoutineArgument)
            .ToList();
        for (int index = 0; index < followers.Count; index++)
        {
            NeuralNPC follower = followers[index];
            follower.GetComponent<CrossLayerNavigator>()?.Cancel();
            follower.GetComponent<PathfindingEntity>()?
                .ClearPathAndDeclareSuccess();
            Vector2Int destination = index < available.Count
                ? available[index]
                : playerPosition;
            EntityMover? mover = follower.GetComponent<EntityMover>();
            mover?.Teleport(destination);
            mover?.TurnTowards(playerPosition);
        }
    }

    private void ReconcileProjection(int sourceLevel, Vector2Int canonical)
    {
        int destinationLevel = sourceLevel + 1;
        if (!IsLevelActive(destinationLevel))
            return;
        Vector2Int sourcePhysical =
            LayerCoordinates.ToPhysical(canonical, sourceLevel);
        Vector2Int destinationPhysical =
            LayerCoordinates.ToPhysical(canonical, destinationLevel);
        bool roofed = TileTags.HasTileTag(sourcePhysical, TileTag.Roofed);
        ProjectedUpperFloor? projected = null;
        if (Turfs.TryGetTurfGameObjects(destinationPhysical, out var objects))
            foreach (GameObject gameObject in objects)
            {
                if (gameObject == null)
                    continue;
                ProjectedUpperFloor? candidate = gameObject.GetComponent<ProjectedUpperFloor>();
                if (candidate != null && candidate.SourceLevel == sourceLevel)
                {
                    projected = candidate;
                    break;
                }
            }

        if (roofed && projected == null &&
            !floorNetwork.HasFloor(destinationLevel, canonical))
        {
            PrefabFactory.InstantiateProjectedFloor(
                destinationPhysical,
                sourceLevel,
                canonical);
            floorNetwork.Invalidate(destinationLevel);
        }
        else if (!roofed && projected != null &&
                 !projected.PlayerModified &&
                 CanRemoveProjection(projected.gameObject))
        {
            DestroyImmediate(projected.gameObject);
            floorNetwork.Invalidate(destinationLevel);
        }
    }

    private static bool CanRemoveProjection(GameObject projected)
    {
        Vector2Int position = projected.transform.GetVector2IntPosition();
        foreach (GameObject gameObject in Turfs.GetTurfGameObjects(position))
        {
            if (gameObject == null || gameObject == projected ||
                FloorNetwork.IsFloorObject(gameObject))
                continue;
            return false;
        }
        return true;
    }

    private void ProcessDirtyProjectionCells()
    {
        if (dirtyProjectionCells.Count == 0)
            return;
        // Reconciliation can queue more cells through registration callbacks.
        // Snapshot into a reusable buffer, leaving those new notifications queued.
        projectionWork.Clear();
        projectionWork.AddRange(dirtyProjectionCells);
        dirtyProjectionCells.Clear();
        foreach ((int sourceLevel, Vector2Int canonical) in projectionWork)
            ReconcileProjection(sourceLevel, canonical);
        projectionWork.Clear();
        Renderer.MarkFloorMaskDirty();
    }

    private void OnPostProcessTurn()
    {
        floorNetwork.Invalidate();
        Renderer.MarkDirty();
    }

    private void AfterLoad()
    {
        floorNetwork.Invalidate();
        UpperFloorShelter.Invalidate();
        int inferred = CurrentPlayerLevel;
        if (inferred > 0)
            activeLevelMask |= 1 << inferred;
        Renderer.MarkFloorMaskDirty();
    }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(1);
        writer.Write(activeLevelMask);
    }

    public void Deserialize(BinaryReader reader)
    {
        if (reader.BaseStream.Position >= reader.BaseStream.Length)
            return;
        _ = reader.ReadInt32();
        activeLevelMask = reader.ReadInt32() | 1;
        floorNetwork.Invalidate();
        ActionQueue.Instance?.DoAfterXFrames(2, AfterLoad);
    }
}
