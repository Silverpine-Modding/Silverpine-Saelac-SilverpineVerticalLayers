#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SilverpineVerticalLayers;

internal static class CrossLayerRoutingGuard
{
    [ThreadStatic]
    private static int depth;

    internal static bool Active => depth > 0;

    internal static IDisposable Enter()
    {
        depth++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => depth--;
    }
}

internal sealed class CrossLayerNavigator : MonoBehaviour
{
    private PathfindingEntity pathfinding = null!;
    private readonly Queue<CrossLayerRouteStep> steps = new();
    private CrossLayerRouteStep? currentStep;
    private Vector2Int finalTarget;
    private Vector2Int? pendingTarget;
    private bool active;
    private Vector2Int? lastLoggedTarget;

    internal bool Active => active;
    internal bool AtIntermediatePortal => active && currentStep != null;

    private void Awake()
    {
        pathfinding = GetComponent<PathfindingEntity>();
        pathfinding.OnReachedTargetPosition += OnReachedTarget;
    }

    private void OnDestroy()
    {
        if (pathfinding != null)
            pathfinding.OnReachedTargetPosition -= OnReachedTarget;
    }

    internal bool Begin(Vector2Int target)
    {
        if (!CrossLayerRoutePlanner.TryPlan(
                gameObject,
                transform.GetVector2IntPosition(),
                target,
                out List<CrossLayerRouteStep> route,
                out Vector2Int resolvedTarget))
        {
            pendingTarget = target;
            active = true;
            if (lastLoggedTarget != target)
            {
                Plugin.Log.LogInfo(
                    $"No cross-floor route for {gameObject.name}: " +
                    $"{transform.GetVector2IntPosition()} -> {target}; " +
                    $"registered stairs={FloorPortal.All.Count}. " +
                    CrossLayerRoutePlanner.LastFailureDetails);
                lastLoggedTarget = target;
            }
            return false;
        }
        if (lastLoggedTarget != target)
        {
            Plugin.Log.LogInfo(
                $"Cross-floor route for {gameObject.name}: " +
                $"{transform.GetVector2IntPosition()} -> {resolvedTarget} " +
                $"using {route.Count} stair transition(s).");
            lastLoggedTarget = target;
        }
        steps.Clear();
        foreach (CrossLayerRouteStep step in route)
            steps.Enqueue(step);
        finalTarget = resolvedTarget;
        pendingTarget = null;
        active = true;
        StartNextSegment();
        return true;
    }

    internal void Cancel()
    {
        active = false;
        pendingTarget = null;
        steps.Clear();
        currentStep = null;
    }

    internal void RetryPendingRoute()
    {
        if (pendingTarget.HasValue)
            Begin(pendingTarget.Value);
    }

    private void StartNextSegment()
    {
        if (!active)
            return;
        using (CrossLayerRoutingGuard.Enter())
        {
            if (steps.Count > 0)
            {
                currentStep = steps.Dequeue();
                pathfinding.SetTargetPosition(currentStep.Approach);
            }
            else
            {
                currentStep = null;
                pathfinding.SetTargetPosition(finalTarget);
            }
        }
    }

    private void OnReachedTarget()
    {
        if (!active)
            return;
        if (currentStep == null)
        {
            active = false;
            pendingTarget = null;
            return;
        }
        FloorPortal portal = currentStep.Portal;
        currentStep = null;
        VerticalLayerManager.Instance?.TeleportEntityThroughPortal(
            gameObject,
            portal);
        StartCoroutine(ContinueNextFrame());
    }

    private IEnumerator ContinueNextFrame()
    {
        yield return null;
        StartNextSegment();
    }
}

internal sealed class CrossLayerRouteStep
{
    internal FloorPortal Portal { get; }
    internal Vector2Int Approach { get; }

    internal CrossLayerRouteStep(FloorPortal portal, Vector2Int approach)
    {
        Portal = portal;
        Approach = approach;
    }
}

internal static class CrossLayerRoutePlanner
{
    internal static string LastFailureDetails { get; private set; } = "";

    private sealed class RouteDiagnostics
    {
        internal readonly List<string> Failures = new();

        internal void Add(string value)
        {
            if (Failures.Count < 12 && !Failures.Contains(value))
                Failures.Add(value);
        }
    }

    private sealed class Plan
    {
        internal int Cost;
        internal readonly List<CrossLayerRouteStep> Steps = new();
    }

    internal static bool TryPlan(
        GameObject requester,
        Vector2Int start,
        Vector2Int goal,
        out List<CrossLayerRouteStep> steps,
        out Vector2Int resolvedGoal)
    {
        steps = new List<CrossLayerRouteStep>();
        resolvedGoal = goal;
        LastFailureDetails = "";
        if (!LayerCoordinates.TryDecode(start, out int startLevel, out _) ||
            !LayerCoordinates.TryDecode(
                goal, out int goalLevel, out Vector2Int goalCanonical) ||
            startLevel == goalLevel)
            return false;
        bool flying = requester.GetComponent<EntityMover>()?.Flying ?? false;
        RouteDiagnostics diagnostics = new();
        int maximumReplacementRadius = flying ? 0 : 4;
        for (int radius = 0; radius <= maximumReplacementRadius; radius++)
        {
            Plan? best = null;
            Vector2Int bestGoal = goal;
            foreach (Vector2Int candidate in CandidateGoals(
                         goalLevel, goalCanonical, radius, flying))
            {
                Plan? plan = Search(
                    requester,
                    flying,
                    start,
                    startLevel,
                    candidate,
                    goalLevel,
                    depth: 0,
                    diagnostics);
                if (plan == null || (best != null && best.Cost <= plan.Cost))
                    continue;
                best = plan;
                bestGoal = candidate;
            }
            if (best == null)
                continue;
            steps = best.Steps;
            resolvedGoal = bestGoal;
            return true;
        }
        LastFailureDetails =
            (diagnostics.Failures.Count == 0
                ? "No directional portal matched the requested floor change."
                : string.Join(" | ", diagnostics.Failures)) +
            " " + DescribePortals();
        return false;
    }

    private static IEnumerable<Vector2Int> CandidateGoals(
        int level,
        Vector2Int center,
        int radius,
        bool flying)
    {
        for (int dx = -radius; dx <= radius; dx++)
        {
            int dyMagnitude = radius - Math.Abs(dx);
            foreach (int dy in dyMagnitude == 0
                         ? new[] { 0 }
                         : new[] { -dyMagnitude, dyMagnitude })
            {
                Vector2Int canonical = center + new Vector2Int(dx, dy);
                if (!LayerCoordinates.IsCanonicalSilverpine(canonical))
                    continue;
                Vector2Int physical =
                    LayerCoordinates.ToPhysical(canonical, level);
                if (flying || UpperFloorOccupancy.HasActualFloor(physical))
                    yield return physical;
            }
        }
    }

    private static Plan? Search(
        GameObject requester,
        bool flying,
        Vector2Int current,
        int currentLevel,
        Vector2Int goal,
        int goalLevel,
        int depth,
        RouteDiagnostics diagnostics)
    {
        if (depth > Plugin.MaxLevels.Value + 1)
            return null;
        if (currentLevel == goalLevel)
        {
            if (!TryLocalCost(requester, flying, current, goal, out int finalCost))
            {
                diagnostics.Add($"floor {currentLevel} segment {current}->{goal} is blocked");
                return null;
            }
            return new Plan { Cost = finalCost };
        }

        int nextLevel = currentLevel + Math.Sign(goalLevel - currentLevel);
        Plan? best = null;
        foreach (FloorPortal portal in FloorPortal.All.Where(portal =>
                     portal != null &&
                     portal.SourceLevel == currentLevel &&
                     portal.DestinationLevel == nextLevel))
        {
            Vector2Int entrance = portal.transform.GetVector2IntPosition();
            if (!TryPortalApproachCost(
                    requester,
                    flying,
                    current,
                    portal,
                    out Vector2Int approach,
                    out int entryCost))
            {
                diagnostics.Add(
                    $"cannot approach stair {portal.SourceLevel}:{entrance} " +
                    $"->{portal.DestinationLevel}:{portal.DestinationCanonical}");
                continue;
            }
            Vector2Int exit = LayerCoordinates.ToPhysical(
                portal.DestinationCanonical,
                portal.DestinationLevel);
            Plan? remainder = Search(
                requester,
                flying,
                exit,
                nextLevel,
                goal,
                goalLevel,
                depth + 1,
                diagnostics);
            if (remainder == null)
                continue;
            int total = entryCost + 1 + remainder.Cost;
            if (best != null && best.Cost <= total)
                continue;
            best = new Plan { Cost = total };
            best.Steps.Add(new CrossLayerRouteStep(portal, approach));
            best.Steps.AddRange(remainder.Steps);
        }
        return best;
    }

    private static bool TryPortalApproachCost(
        GameObject requester,
        bool flying,
        Vector2Int start,
        FloorPortal portal,
        out Vector2Int approach,
        out int cost)
    {
        Vector2Int entrance = portal.transform.GetVector2IntPosition();
        approach = entrance;
        if (TryLocalCost(requester, flying, start, entrance, out cost))
            return true;

        cost = int.MaxValue;
        bool found = false;
        foreach (Vector2Int offset in new[]
                 {
                     Vector2Int.up,
                     Vector2Int.right,
                     Vector2Int.down,
                     Vector2Int.left
                 })
        {
            Vector2Int candidate = entrance + offset;
            if (LayerCoordinates.GetLevel(candidate) != portal.SourceLevel ||
                (!flying && portal.SourceLevel > 0 &&
                 !UpperFloorOccupancy.HasActualFloor(candidate)) ||
                !TryLocalCost(
                    requester,
                    flying,
                    start,
                    candidate,
                    out int candidateCost) ||
                (found && candidateCost >= cost))
                continue;
            found = true;
            approach = candidate;
            cost = candidateCost;
        }
        return found;
    }

    private static string DescribePortals() =>
        "registered portals: " + string.Join(
            ", ",
            FloorPortal.All.Where(portal => portal != null).Select(portal =>
                $"[{portal.SourceLevel}:{portal.transform.GetVector2IntPosition()} " +
                $"-> {portal.DestinationLevel}:{portal.DestinationCanonical}, " +
                $"pair={portal.PairId}]"));

    private static bool TryLocalCost(
        GameObject requester,
        bool flying,
        Vector2Int start,
        Vector2Int goal,
        out int cost)
    {
        if (start == goal)
        {
            cost = 0;
            return true;
        }
        List<Direction> path = Pathfinding.FindPath(
            start,
            goal,
            requester,
            flying,
            specialPathfindingRule: null,
            ignorePenalty: true,
            printNoPath: false);
        cost = path.Count;
        return path.Count > 0;
    }
}
