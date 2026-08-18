#nullable enable

using HarmonyLib;
using System.IO;
using UnityEngine;

namespace SilverpineVerticalLayers;

public enum VerticalConstructionKind
{
    Stairs = 0,
    Support = 1
}

/// <summary>
/// Serializable payload attached to a normal Silverpine ConstructionSite.
/// The base component retains ownership of materials, work turns, interaction,
/// NPC visibility, deconstruction/refunds, and save data; this component only
/// describes the atomic multi-object result that replaces the site.
/// </summary>
public sealed class VerticalConstructionSite : MonoBehaviour,
    ISerializableMonoBehavior
{
    public VerticalConstructionKind Kind { get; private set; }
    public int Rotation { get; private set; }

    internal void Configure(VerticalConstructionKind kind, int rotation)
    {
        Kind = kind;
        Rotation = ((rotation % 4) + 4) % 4;
    }

    internal bool TryComplete(out string reason)
    {
        VerticalLayerManager? manager = RuntimeHost.EnsureManager();
        if (manager == null)
        {
            reason = "Vertical Layers has not initialized yet.";
            return false;
        }

        Vector2Int position = transform.GetVector2IntPosition();
        return Kind switch
        {
            VerticalConstructionKind.Stairs =>
                manager.TryBuildStairPair(position, Rotation, out reason),
            VerticalConstructionKind.Support =>
                manager.TryBuildSupport(position, out reason),
            _ => Fail(out reason)
        };
    }

    private static bool Fail(out string reason)
    {
        reason = "This vertical construction site has invalid save data.";
        return false;
    }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(1);
        writer.Write((int)Kind);
        writer.Write(Rotation);
    }

    public void Deserialize(BinaryReader reader)
    {
        _ = reader.ReadInt32();
        Configure((VerticalConstructionKind)reader.ReadInt32(), reader.ReadInt32());
    }
}

[HarmonyPatch(typeof(ConstructionSite), nameof(ConstructionSite.FinishConstruction))]
internal static class VerticalConstructionCompletionPatch
{
    private static bool Prefix(ConstructionSite __instance, Item hammerItem)
    {
        VerticalConstructionSite? vertical =
            __instance.GetComponent<VerticalConstructionSite>();
        if (vertical == null)
            return true;

        if (!vertical.TryComplete(out string reason))
        {
            UpperNotificationUI.Instance.OneOff(reason);
            return false;
        }

        ItemComponent_Durability? durability =
            hammerItem?.GetItemComponent<ItemComponent_Durability>();
        if (durability != null)
            durability.Durability--;

        AudioPlayer.Instance.PlayAudioAtPosition(
            "sound_interaction_hammering",
            1f,
            __instance.transform.GetVector2IntPosition());
        Object.Destroy(__instance.gameObject);
        return false;
    }
}
