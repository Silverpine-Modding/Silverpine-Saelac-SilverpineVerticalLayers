#nullable enable

using HarmonyLib;
using Silverpine.ModdingTools;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SilverpineVerticalLayers;

/// <summary>
/// Registers vertical structures with Modding Tools' shared Construct menu.
/// Ordinary wood/rock floors remain the base game's own entries, whose
/// placement predicates are extended by UpperFloorPlacementPatch.
/// </summary>
internal static class VerticalConstructionEntries
{
    private const int SiteSetupTurns = 10;
    private const int SiteWorkTurns = 60;
    internal const string CategoryId = Plugin.PluginGuid + ".construction";
    internal const string StairsId = Plugin.PluginGuid + ".stairs";
    internal const string SupportId = Plugin.PluginGuid + ".support";

    internal static void Register()
    {
        ConstructionMenu.RegisterCategory(Plugin.PluginGuid,
            new ConstructionCategoryDefinition
            {
                Id = CategoryId,
                Label = "Vertical Layers",
                Order = 120
            });
        RegisterEntry(StairsId, "Floor Transition Stairs", PrefabFactory.StairsPrefabName,
            -200, StairsCost(), StartStairsPlacement);
        RegisterEntry(SupportId, "Structural Support", PrefabFactory.SupportPrefabName,
            -199, SupportCost(), StartSupportPlacement);
    }

    private static void RegisterEntry(
        string id,
        string label,
        string prefabName,
        int order,
        List<ItemNameAmountPair> cost,
        Action startPlacement)
    {
        var definition = new ConstructionDefinition
        {
            Id = id,
            Label = label,
            PrefabName = prefabName,
            Category = CategoryId,
            Order = order,
            Mode = ConstructionMode.ConstructionSite,
            WorkMinutes = (SiteSetupTurns + SiteWorkTurns) / 2,
            PlayerPropertyOnly = false,
            RequiresHammer = true,
            // The framework closes its window/releases input before invoking
            // this. Keep our paired-floor site creation and Salt Free Build path.
            StartPlacement = startPlacement
        };
        foreach (ItemNameAmountPair material in cost)
            definition.Materials.Add(new ConstructionMaterial(material.itemName, material.amount));
        ConstructionMenu.Register(Plugin.PluginGuid, definition);
    }

    private static void StartStairsPlacement()
    {
        GenericListUI.Instance.Close();
        if (!CanBeginPlacement())
            return;

        PlacementModeManager.Instance.StartPlacementMode(
            rotationIndicator: true,
            rotation => PrefabFactory.StairSprites[NormalizeRotation(rotation)],
            snappingOffset: 0,
            (rotation, position) => SetUpConstructionSite(
                Vector2Int.RoundToInt(position),
                VerticalConstructionKind.Stairs,
                NormalizeRotation(rotation),
                StairsCost,
                CanPlaceStairs),
            position => CanPlaceStairs(
                Vector2Int.RoundToInt(position), out _));
    }

    private static void StartSupportPlacement()
    {
        GenericListUI.Instance.Close();
        if (!CanBeginPlacement())
            return;

        Sprite preview = PrefabPreviewGenerator.GeneratePreview(
            SerializationManager.GetPrefabFromName(
                PrefabFactory.SupportPrefabName));
        PlacementModeManager.Instance.StartPlacementMode(
            rotationIndicator: false,
            _ => preview,
            snappingOffset: 0,
            (_, position) => SetUpConstructionSite(
                Vector2Int.RoundToInt(position),
                VerticalConstructionKind.Support,
                0,
                SupportCost,
                CanPlaceSupport),
            position => CanPlaceSupport(
                Vector2Int.RoundToInt(position), out _));
    }

    private static bool CanBeginPlacement()
    {
        if (RuntimeHost.EnsureManager() == null)
        {
            UpperNotificationUI.Instance.OneOff(
                "Vertical Layers has not initialized yet.");
            return false;
        }
        if (!SaltFreeBuildCompatibility.IsEnabled && !HasHammer())
        {
            UpperNotificationUI.Instance.OneOff(
                "You need a hammer to set up this construction site.");
            return false;
        }
        return true;
    }

    private static void SetUpConstructionSite(
        Vector2Int physical,
        VerticalConstructionKind kind,
        int rotation,
        Func<List<ItemNameAmountPair>> createCost,
        PlacementValidator validate)
    {
        if (!validate(physical, out string reason))
        {
            UpperNotificationUI.Instance.OneOff(reason);
            return;
        }

        bool freeBuild = SaltFreeBuildCompatibility.IsEnabled;
        if (!freeBuild && !HasHammer())
        {
            UpperNotificationUI.Instance.OneOff(
                "You no longer have a hammer to set up this construction site.");
            return;
        }

        void PlaceSite()
        {
            if (!validate(physical, out string currentReason))
            {
                UpperNotificationUI.Instance.OneOff(currentReason);
                return;
            }

            PrefabFactory.InstantiateConstructionSite(
                physical,
                kind,
                rotation,
                freeBuild ? 0 : SiteWorkTurns,
                freeBuild ? new List<ItemNameAmountPair>() : createCost());
            TurfLifecyclePatch.NotifyChanged(physical);
        }

        if (freeBuild)
        {
            PlaceSite();
            return;
        }

        Vector2Int playerPosition =
            Player.Instance.transform.GetVector2IntPosition();
        AudioPlayer.Instance.PlayLoopAtPosition(
            "sound_loop_hammering", 0.5f, playerPosition);
        Player.Instance.StartLongInteraction(
            PlaceSite,
            SiteSetupTurns,
            null,
            () => AudioPlayer.Instance.StopLoop(playerPosition));
    }

    private static bool CanPlaceStairs(
        Vector2Int physical,
        out string reason)
    {
        if (!LayerCoordinates.TryDecode(
                physical,
                out int level,
                out Vector2Int canonical) ||
            !LayerCoordinates.IsCanonicalSilverpine(canonical))
        {
            reason = "Transition stairs can only be built on the Silverpine tile.";
            return false;
        }
        if (level + 1 >= Plugin.MaxLevels.Value)
        {
            reason = "The configured maximum number of floors has been reached.";
            return false;
        }
        if (!TileTags.HasTileTag(physical, TileTag.Roofed))
        {
            reason = "Transition stairs must be placed on a roofed floor.";
            return false;
        }
        if (Turfs.GetTurfComponents<FloorPortal>(physical).Count > 0)
        {
            reason = "There is already a floor transition here.";
            return false;
        }
        if (HasVerticalSite(physical) ||
            TurfCollider.HasImpassableCollider(physical))
        {
            reason = "Something blocks the construction site.";
            return false;
        }
        reason = "";
        return true;
    }

    private static bool CanPlaceSupport(
        Vector2Int physical,
        out string reason)
    {
        if (!LayerCoordinates.TryDecode(
                physical,
                out int level,
                out Vector2Int canonical))
        {
            reason = "Supports can only be built on a vertical floor.";
            return false;
        }
        if (HasVerticalSite(physical) ||
            TurfCollider.HasImpassableCollider(physical))
        {
            reason = "Something blocks the construction site.";
            return false;
        }
        VerticalLayerManager? manager = VerticalLayerManager.Instance;
        if (manager == null)
        {
            reason = "Vertical Layers has not initialized yet.";
            return false;
        }
        return manager.FloorNetwork.CanPlaceSupport(level, canonical, out reason);
    }

    private static bool HasVerticalSite(Vector2Int physical) =>
        Turfs.GetTurfComponents<VerticalConstructionSite>(physical).Count > 0;

    private static bool HasHammer() =>
        Player.Instance.worldInventory.inventory.GetItemWithPredicate(item =>
            item.GetItemComponent<ItemComponent_Hammer>() != null) != null;

    private static List<ItemNameAmountPair> StairsCost() => new()
    {
        new ItemNameAmountPair("Plank", 8),
        new ItemNameAmountPair("Nails", 4)
    };

    private static List<ItemNameAmountPair> SupportCost() => new()
    {
        new ItemNameAmountPair("Rock", 4)
    };

    private static int NormalizeRotation(int rotation) =>
        ((rotation % 4) + 4) % 4;

    private delegate bool PlacementValidator(
        Vector2Int physical,
        out string reason);
}

internal static class SaltFreeBuildCompatibility
{
    private static FieldInfo? freeBuildField;
    private static PropertyInfo? valueProperty;

    internal static bool IsEnabled
    {
        get
        {
            try
            {
                Type? pluginType = AccessTools.TypeByName("SaltExtraDebug.Plugin");
                if (pluginType == null)
                    return false;
                freeBuildField ??= AccessTools.Field(pluginType, "FreeBuildEnabled");
                object? entry = freeBuildField?.GetValue(null);
                if (entry == null)
                    return false;
                valueProperty ??= entry.GetType().GetProperty("Value");
                return valueProperty?.GetValue(entry) is true;
            }
            catch (Exception exception)
            {
                Plugin.Log.LogWarning(
                    "Could not read Salt Extra Debug Free Build state: " +
                    exception.Message);
                return false;
            }
        }
    }
}
