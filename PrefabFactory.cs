#nullable enable

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SilverpineVerticalLayers;

internal static class PrefabFactory
{
    internal const string StairsPrefabName = "svl_stairs_wood";
    internal const string ProjectedFloorPrefabName = "svl_projected_roof_floor";
    internal const string SupportPrefabName = "svl_support_pillar";
    internal const string ConstructionSitePrefabName =
        "svl_vertical_constructionsite";

    private static readonly FieldInfo PrefabsField =
        AccessTools.Field(typeof(SerializationManager), "prefabs");
    private static readonly Dictionary<string, GameObject> Templates =
        new(StringComparer.OrdinalIgnoreCase);
    private static Sprite[] stairSprites = Array.Empty<Sprite>();

    internal static IReadOnlyList<Sprite> StairSprites => stairSprites;

    internal static void RegisterAll()
    {
        stairSprites = new[]
        {
            LoadEmbeddedSprite("stairs_wood_up.png"),
            LoadEmbeddedSprite("stairs_wood_right.png"),
            LoadEmbeddedSprite("stairs_wood_down.png"),
            LoadEmbeddedSprite("stairs_wood_left.png")
        };

        Register(StairsPrefabName, CreateStairsTemplate());
        Register(ProjectedFloorPrefabName, CreateProjectedFloorTemplate());
        Register(SupportPrefabName, CreateSupportTemplate());
        Register(ConstructionSitePrefabName, CreateConstructionSiteTemplate());
    }

    internal static GameObject InstantiateStairs(
        Vector2Int physical,
        string pairId,
        int destinationLevel,
        Vector2Int destinationCanonical,
        int rotation)
    {
        GameObject instance = UnityEngine.Object.Instantiate(
            Templates[StairsPrefabName],
            new Vector3(physical.x, physical.y, -0.15f),
            Quaternion.identity);
        instance.hideFlags = HideFlags.None;
        ActivateInstance(instance);
        instance.GetComponent<VerticalRotation>().Configure(rotation);
        instance.GetComponent<FloorPortal>().Configure(
            pairId,
            destinationLevel,
            destinationCanonical,
            rotation);
        return instance;
    }

    internal static GameObject InstantiateProjectedFloor(
        Vector2Int physical,
        int sourceLevel,
        Vector2Int canonical)
    {
        GameObject instance = UnityEngine.Object.Instantiate(
            Templates[ProjectedFloorPrefabName],
            new Vector3(
                physical.x,
                physical.y,
                Templates[ProjectedFloorPrefabName].transform.position.z),
            Templates[ProjectedFloorPrefabName].transform.rotation);
        instance.hideFlags = HideFlags.None;
        ActivateInstance(instance);
        instance.GetComponent<ProjectedUpperFloor>().Configure(
            sourceLevel,
            canonical);
        return instance;
    }

    internal static GameObject InstantiateSupport(
        Vector2Int physical,
        string pairId,
        int anchorLevel,
        bool isAnchor)
    {
        GameObject instance = UnityEngine.Object.Instantiate(
            Templates[SupportPrefabName],
            new Vector3(
                physical.x,
                physical.y,
                Templates[SupportPrefabName].transform.position.z),
            Templates[SupportPrefabName].transform.rotation);
        instance.hideFlags = HideFlags.None;
        ActivateInstance(instance);
        instance.GetComponent<VerticalSupport>().Configure(
            pairId,
            anchorLevel,
            isAnchor);
        return instance;
    }

    internal static GameObject InstantiateConstructionSite(
        Vector2Int physical,
        VerticalConstructionKind kind,
        int rotation,
        int workTurns,
        List<ItemNameAmountPair> requiredItems)
    {
        GameObject instance = UnityEngine.Object.Instantiate(
            Templates[ConstructionSitePrefabName],
            new Vector3(
                physical.x,
                physical.y,
                Templates[ConstructionSitePrefabName].transform.position.z),
            Templates[ConstructionSitePrefabName].transform.rotation);
        instance.hideFlags = HideFlags.None;
        ActivateInstance(instance);

        ConstructionSite site = instance.GetComponent<ConstructionSite>();
        site.prefabName = kind == VerticalConstructionKind.Stairs
            ? StairsPrefabName
            : SupportPrefabName;
        site.workTurns = workTurns;
        site.requiredItems = requiredItems;
        instance.GetComponent<VerticalConstructionSite>().Configure(kind, rotation);
        return instance;
    }

    internal static bool IsTemplate(GameObject gameObject) =>
        gameObject != null && Templates.Values.Any(
            template => ReferenceEquals(template, gameObject));

    internal static bool IsInstance(GameObject gameObject) =>
        gameObject != null &&
        (gameObject.GetComponent<FloorPortal>() != null ||
         gameObject.GetComponent<ProjectedUpperFloor>() != null ||
         gameObject.GetComponent<VerticalSupport>() != null ||
         gameObject.GetComponent<VerticalConstructionSite>() != null);

    internal static void RestoreInstance(GameObject gameObject)
    {
        if (!IsInstance(gameObject) || IsTemplate(gameObject))
            return;
        ActivateInstance(gameObject);
    }

    internal static void Dispose()
    {
        Dictionary<string, GameObject> prefabs = GetPrefabDictionary();
        foreach ((string name, GameObject template) in Templates.ToArray())
        {
            if (prefabs.TryGetValue(name, out GameObject existing) &&
                ReferenceEquals(existing, template))
                prefabs.Remove(name);
            if (template != null)
                UnityEngine.Object.Destroy(template);
        }
        Templates.Clear();
        foreach (Sprite sprite in stairSprites)
        {
            if (sprite == null)
                continue;
            Texture2D texture = sprite.texture;
            UnityEngine.Object.Destroy(sprite);
            if (texture != null)
                UnityEngine.Object.Destroy(texture);
        }
        stairSprites = Array.Empty<Sprite>();
    }

    private static void Register(string name, GameObject template)
    {
        Dictionary<string, GameObject> prefabs = GetPrefabDictionary();
        if (prefabs.ContainsKey(name))
            throw new InvalidOperationException(
                $"Silverpine already has a registered prefab named '{name}'.");
        template.name = name;
        template.SetActive(false);
        template.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(template);
        prefabs.Add(name, template);
        Templates.Add(name, template);
    }

    private static Dictionary<string, GameObject> GetPrefabDictionary() =>
        (Dictionary<string, GameObject>)PrefabsField.GetValue(null);

    private static GameObject CreateStairsTemplate()
    {
        GameObject template = new(StairsPrefabName);
        template.SetActive(false);
        SpriteRenderer renderer = template.AddComponent<SpriteRenderer>();
        renderer.sprite = stairSprites[0];
        renderer.sortingLayerName = "Characters";
        BoxCollider2D collider = template.AddComponent<BoxCollider2D>();
        collider.size = new Vector2(0.9f, 1.25f);
        template.AddComponent<TurfRegistrar>();
        TurfCollider turfCollider = template.AddComponent<TurfCollider>();
        turfCollider.passable = true;
        turfCollider.abyss = false;
        NPCVisibleObject visible = template.AddComponent<NPCVisibleObject>();
        visible.description = "wooden floor-transition stairs";
        template.AddComponent<YSorter>();
        template.AddComponent<Targetable>();
        Deconstructable deconstructable = template.AddComponent<Deconstructable>();
        deconstructable.playerPropertyOnly = false;
        template.AddComponent<VerticalRotation>();
        template.AddComponent<FloorPortal>();
        return template;
    }

    private static GameObject CreateProjectedFloorTemplate()
    {
        GameObject source = SerializationManager.GetPrefabFromName("prefab_tile_wood")
            ?? throw new InvalidOperationException("Missing base prefab_tile_wood.");
        GameObject template = UnityEngine.Object.Instantiate(source);
        template.name = ProjectedFloorPrefabName;
        PrepareClonedBaseTemplate(template);
        template.AddComponent<ProjectedUpperFloor>();
        return template;
    }

    private static GameObject CreateSupportTemplate()
    {
        GameObject source =
            SerializationManager.GetPrefabFromName("prefab_furniture_pillar_stone")
            ?? throw new InvalidOperationException(
                "Missing base prefab_furniture_pillar_stone.");
        GameObject template = UnityEngine.Object.Instantiate(source);
        template.name = SupportPrefabName;
        PrepareClonedBaseTemplate(template);
        NPCVisibleObject? visible = template.GetComponent<NPCVisibleObject>();
        if (visible != null)
            visible.description = "structural support pillar";
        template.AddComponent<VerticalSupport>();
        return template;
    }

    private static GameObject CreateConstructionSiteTemplate()
    {
        GameObject source =
            SerializationManager.GetPrefabFromName("prefab_misc_constructionsite")
            ?? throw new InvalidOperationException(
                "Missing base prefab_misc_constructionsite.");
        GameObject template = UnityEngine.Object.Instantiate(source);
        template.name = ConstructionSitePrefabName;
        PrepareClonedBaseTemplate(template);
        template.AddComponent<VerticalConstructionSite>();
        return template;
    }

    private static void PrepareClonedBaseTemplate(GameObject template)
    {
        template.SetActive(false);
        TurfRegistrar? registrar = template.GetComponent<TurfRegistrar>();
        if (registrar != null)
            registrar.preventRegistration = true;
        TileTags? tags = template.GetComponent<TileTags>();
        tags?.Unregister();
    }

    private static void ActivateInstance(GameObject instance)
    {
        TurfRegistrar? registrar = instance.GetComponent<TurfRegistrar>();
        if (registrar != null)
            registrar.preventRegistration = false;
        if (!instance.activeSelf)
            instance.SetActive(true);
    }

    private static Sprite LoadEmbeddedSprite(string fileName)
    {
        Assembly assembly = typeof(Plugin).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException(
                $"Embedded staircase sprite '{fileName}' was not found.");
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(resourceName);
        byte[] bytes = new byte[stream.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read <= 0)
                break;
            offset += read;
        }
        Texture2D texture = new(2, 2, TextureFormat.RGBA32, mipChain: false)
        {
            name = Path.GetFileNameWithoutExtension(fileName),
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        if (!ImageConversion.LoadImage(texture, bytes, markNonReadable: false))
            throw new InvalidDataException(
                $"Could not decode embedded staircase sprite '{fileName}'.");
        return Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            32f,
            0,
            SpriteMeshType.FullRect);
    }
}
