#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace SilverpineVerticalLayers;

public sealed class ProjectedUpperFloor : MonoBehaviour, ISerializableMonoBehavior
{
    public int SourceLevel { get; private set; }
    public Vector2Int CanonicalPosition { get; private set; }
    public bool PlayerModified { get; set; }

    internal void Configure(int sourceLevel, Vector2Int canonical)
    {
        SourceLevel = sourceLevel;
        CanonicalPosition = canonical;
    }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(1);
        writer.Write(SourceLevel);
        writer.Write(CanonicalPosition.x);
        writer.Write(CanonicalPosition.y);
        writer.Write(PlayerModified);
    }

    public void Deserialize(BinaryReader reader)
    {
        int version = reader.ReadInt32();
        SourceLevel = reader.ReadInt32();
        CanonicalPosition = new Vector2Int(reader.ReadInt32(), reader.ReadInt32());
        PlayerModified = version >= 1 && reader.ReadBoolean();
    }
}

public sealed class VerticalRotation : MonoBehaviour, ISerializableMonoBehavior
{
    public int Rotation { get; private set; }

    internal void Configure(int rotation)
    {
        Rotation = ((rotation % 4) + 4) % 4;
        Apply();
    }

    private void Start() => Apply();

    private void Apply()
    {
        if (PrefabFactory.StairSprites.Count != 4)
            return;
        SpriteRenderer? renderer = GetComponent<SpriteRenderer>();
        if (renderer != null)
            renderer.sprite = PrefabFactory.StairSprites[Rotation];
    }

    public void Serialize(BinaryWriter writer) => writer.Write(Rotation);

    public void Deserialize(BinaryReader reader) => Configure(reader.ReadInt32());
}

public sealed class FloorPortal : MonoBehaviour,
    ISerializableMonoBehavior,
    IInteractionHandler,
    IBumpHandler,
    IDeconstructedHandler
{
    private static readonly HashSet<FloorPortal> Portals = new();

    public string PairId { get; private set; } = "";
    public int DestinationLevel { get; private set; }
    public Vector2Int DestinationCanonical { get; private set; }
    public int Rotation { get; private set; }

    public int SourceLevel =>
        LayerCoordinates.GetLevel(transform.GetVector2IntPosition());
    public Vector2Int SourceCanonical =>
        LayerCoordinates.GetCanonical(transform.GetVector2IntPosition());
    public string InteractionName =>
        DestinationLevel > SourceLevel ? "Go Upstairs" : "Go Downstairs";
    public bool ProcessTurnOnInteract => false;

    internal static IReadOnlyCollection<FloorPortal> All => Portals;

    internal void Configure(
        string pairId,
        int destinationLevel,
        Vector2Int destinationCanonical,
        int rotation)
    {
        PairId = pairId;
        DestinationLevel = destinationLevel;
        DestinationCanonical = destinationCanonical;
        Rotation = ((rotation % 4) + 4) % 4;
    }

    internal void ReconfigureImportedPair(
        string pairId,
        int destinationLevel,
        Vector2Int destinationCanonical) =>
        Configure(pairId, destinationLevel, destinationCanonical, Rotation);

    private void Awake() => Portals.Add(this);
    private void OnDestroy() => Portals.Remove(this);

    public void OnInteract(GameObject sender)
    {
        if (sender == Player.Instance.gameObject)
            VerticalLayerManager.Instance?.BeginPlayerTransition(this);
    }

    public void OnBump(GameObject sender) => OnInteract(sender);

    public void OnDeconstructed()
    {
        VerticalLayerManager.Instance?.RemovePortalPair(PairId, this);
    }

    internal FloorPortal? FindPair() => Portals.FirstOrDefault(
        portal => portal != this && portal.PairId == PairId);

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(1);
        writer.Write(PairId ?? "");
        writer.Write(DestinationLevel);
        writer.Write(DestinationCanonical.x);
        writer.Write(DestinationCanonical.y);
        writer.Write(Rotation);
    }

    public void Deserialize(BinaryReader reader)
    {
        _ = reader.ReadInt32();
        PairId = reader.ReadString();
        DestinationLevel = reader.ReadInt32();
        DestinationCanonical = new Vector2Int(reader.ReadInt32(), reader.ReadInt32());
        Rotation = reader.ReadInt32();
    }
}

public sealed class VerticalSupport : MonoBehaviour,
    ISerializableMonoBehavior,
    IDeconstructedHandler
{
    private static readonly HashSet<VerticalSupport> Supports = new();

    public string PairId { get; private set; } = "";
    public int AnchorLevel { get; private set; }
    public bool IsAnchor { get; private set; }

    internal static IReadOnlyCollection<VerticalSupport> All => Supports;

    internal void Configure(string pairId, int anchorLevel, bool isAnchor)
    {
        PairId = pairId;
        AnchorLevel = anchorLevel;
        IsAnchor = isAnchor;
    }

    internal void ReconfigureImportedPair(
        string pairId,
        int anchorLevel) =>
        Configure(pairId, anchorLevel, IsAnchor);

    private void Awake() => Supports.Add(this);
    private void OnDestroy() => Supports.Remove(this);

    public void OnDeconstructed()
    {
        VerticalLayerManager.Instance?.RemoveSupportPair(PairId, this);
    }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write(1);
        writer.Write(PairId ?? "");
        writer.Write(AnchorLevel);
        writer.Write(IsAnchor);
    }

    public void Deserialize(BinaryReader reader)
    {
        _ = reader.ReadInt32();
        PairId = reader.ReadString();
        AnchorLevel = reader.ReadInt32();
        IsAnchor = reader.ReadBoolean();
    }
}
