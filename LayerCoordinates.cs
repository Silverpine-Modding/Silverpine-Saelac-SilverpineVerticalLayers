#nullable enable

using UnityEngine;

namespace SilverpineVerticalLayers;

internal static class LayerCoordinates
{
    internal const int CanonicalMin = -50;
    internal const int CanonicalMax = 49;

    internal static int OriginX => Plugin.LayerOriginX.Value;
    internal static int StrideX => Plugin.LayerStrideX.Value;

    internal static bool IsCanonicalSilverpine(Vector2Int position) =>
        position.x >= CanonicalMin && position.x <= CanonicalMax &&
        position.y >= CanonicalMin && position.y <= CanonicalMax;

    internal static Vector2Int ToPhysical(Vector2Int canonical, int level)
    {
        if (level <= 0)
            return canonical;
        return new Vector2Int(
            canonical.x + OriginX + (level - 1) * StrideX,
            canonical.y);
    }

    internal static bool TryDecode(
        Vector2Int physical,
        out int level,
        out Vector2Int canonical)
    {
        if (IsCanonicalSilverpine(physical))
        {
            level = 0;
            canonical = physical;
            return true;
        }

        for (int candidate = 1; candidate < Plugin.MaxLevels.Value; candidate++)
        {
            Vector2Int decoded = new(
                physical.x - OriginX - (candidate - 1) * StrideX,
                physical.y);
            if (!IsCanonicalSilverpine(decoded))
                continue;
            level = candidate;
            canonical = decoded;
            return true;
        }

        level = 0;
        canonical = physical;
        return false;
    }

    internal static int GetLevel(Vector2Int physical) =>
        TryDecode(physical, out int level, out _) ? level : 0;

    internal static Vector2Int GetCanonical(Vector2Int physical) =>
        TryDecode(physical, out _, out Vector2Int canonical)
            ? canonical
            : physical;

    internal static Vector3 ToLayerPosition(Vector3 position, int level)
    {
        Vector2Int physical = ToPhysical(
            GetCanonical(Vector2Int.RoundToInt(position)),
            level);
        return new Vector3(physical.x, physical.y, position.z);
    }

    internal static Vector2Int DirectionVector(int rotation) =>
        (((rotation % 4) + 4) % 4) switch
        {
            0 => Vector2Int.up,
            1 => Vector2Int.right,
            2 => Vector2Int.down,
            _ => Vector2Int.left
        };
}
