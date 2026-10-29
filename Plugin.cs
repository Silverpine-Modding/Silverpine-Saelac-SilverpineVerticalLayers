#nullable enable

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Silverpine.ModdingTools;
using UnityEngine;

namespace SilverpineVerticalLayers;

public enum LowerLayerViewMode
{
    Live,
    Void
}

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(
    Silverpine.ModdingTools.Plugin.PluginGuid,
    "1.11.0")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "renegadex.silverpine.verticallayers";
    public const string PluginName = "Silverpine Vertical Layers";
    public const string PluginVersion = "0.4.9";

    internal static ManualLogSource Log = null!;
    internal static Plugin Instance = null!;
    internal static ConfigEntry<int> MaxLevels = null!;
    internal static ConfigEntry<int> MaximumUnsupportedSpan = null!;
    internal static ConfigEntry<int> LayerOriginX = null!;
    internal static ConfigEntry<int> LayerStrideX = null!;
    internal static ConfigEntry<LowerLayerViewMode> ViewMode = null!;
    internal static ConfigEntry<float> LowerResolutionScale = null!;
    internal static ConfigEntry<int> MovementBurstFps = null!;
    internal static ConfigEntry<int> AmbientRefreshFps = null!;
    internal static ConfigEntry<Color> VoidColor = null!;
    internal static ConfigEntry<int> StructureEditorFloor = null!;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        MaxLevels = Config.Bind(
            "Layers", "MaximumLevels", 3,
            "Maximum total levels including ground. Supported range: 2-6.");
        MaximumUnsupportedSpan = Config.Bind(
            "Construction", "MaximumUnsupportedSpan", 4,
            "Maximum orthogonal floor distance from a projected roof or support.");
        LayerOriginX = Config.Bind(
            "Coordinates", "FirstUpperLayerX", 20000,
            "Physical X origin reserved for Silverpine's second level.");
        LayerStrideX = Config.Bind(
            "Coordinates", "LayerStrideX", 512,
            "X distance between logical upper layers.");
        ViewMode = Config.Bind(
            "Rendering", "LowerLayerViewMode", LowerLayerViewMode.Live,
            "Live composites lower floors; Void renders only the current floor.");
        LowerResolutionScale = Config.Bind(
            "Rendering", "LowerResolutionScale", 2f / 3f,
            "Resolution scale used by lower-layer render textures.");
        MovementBurstFps = Config.Bind(
            "Rendering", "MovementBurstFPS", 12,
            "Maximum event refresh rate while stationary. Player and camera movement are synchronized every frame.");
        AmbientRefreshFps = Config.Bind(
            "Rendering", "AmbientRefreshFPS", 2,
            "Idle lower-layer refresh rate. Set to 0 for fully event-driven rendering.");
        VoidColor = Config.Bind(
            "Rendering", "VoidColor", Color.black,
            "Background color used when lower-layer rendering is disabled.");
        StructureEditorFloor = Config.Bind(
            "Compatibility", "StructureEditorFloor", -1,
            "Structure Handler editor floor: -1 shows all floors, 0 is ground, 1 is second, and so on.");

        MaxLevels.Value = Mathf.Clamp(MaxLevels.Value, 2, 6);
        MaximumUnsupportedSpan.Value = Mathf.Clamp(MaximumUnsupportedSpan.Value, 1, 20);
        LowerResolutionScale.Value = Mathf.Clamp(LowerResolutionScale.Value, 0.25f, 1f);
        MovementBurstFps.Value = Mathf.Clamp(MovementBurstFps.Value, 1, 60);
        AmbientRefreshFps.Value = Mathf.Clamp(AmbientRefreshFps.Value, 0, 30);
        StructureEditorFloor.Value = Mathf.Clamp(
            StructureEditorFloor.Value, -1, MaxLevels.Value - 1);

        PrefabFactory.RegisterAll();
        VerticalConstructionEntries.Register();
        Log.LogInfo("Registered stairs and structural support in the shared Construct menu (Vertical Layers category).");
        RuntimeHost.Create();
        SerializationManager.OnFinishedLoadingSave -=
            RuntimeHost.EnsureAfterSaveLoad;
        SerializationManager.OnFinishedLoadingSave +=
            RuntimeHost.EnsureAfterSaveLoad;
        StructureHandlerCompatibility.Initialize();

        Harmony harmony = new(PluginGuid);
        harmony.PatchAll(typeof(Plugin).Assembly);
        RuntimeNpcEditorCompatibility.Initialize(harmony);

        InventoryModTools.RegisterSession(
            PluginGuid + ".controls",
            "Vertical Layers",
            (inventory, session) =>
                VerticalLayersControls.Open(inventory, session),
            order: 120);
        Log.LogInfo(
            "Registered Vertical Layers in the Inventory Mods tab.");

        ViewMode.SettingChanged += (_, _) =>
            VerticalLayerManager.Instance?.Renderer.ApplyModeImmediately();
        LowerResolutionScale.SettingChanged += (_, _) =>
            VerticalLayerManager.Instance?.Renderer.RecreateTargets();
    }

}

internal sealed class RuntimeHost : MonoBehaviour
{
    private static RuntimeHost? instance;

    internal static void Create()
    {
        if (instance != null)
            return;
        GameObject host = new("SilverpineVerticalLayers.Runtime");
        DontDestroyOnLoad(host);
        instance = host.AddComponent<RuntimeHost>();
        VerticalLayerManager.Attach(host);
    }

    internal static VerticalLayerManager? EnsureManager()
    {
        if (instance == null)
            Create();
        if (instance == null)
            return null;
        VerticalLayerManager.Attach(instance.gameObject);
        return VerticalLayerManager.Instance;
    }

    internal static void EnsureAfterSaveLoad()
    {
        VerticalLayerManager? manager = EnsureManager();
        if (manager == null)
        {
            Plugin.Log.LogError(
                "Could not restore the Vertical Layer Manager after save loading.");
            return;
        }
        Plugin.Log.LogInfo(
            "Verified the Vertical Layer Manager after save loading on " +
            manager.gameObject.name + ".");
    }

    private void Update()
    {
        if (VerticalLayerManager.Instance == null)
            EnsureManager();
    }
}
