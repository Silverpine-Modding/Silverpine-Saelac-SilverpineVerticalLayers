#nullable enable

using Silverpine.ModdingTools;
using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SilverpineVerticalLayers;

internal sealed class VerticalLayersControls : ModToolBehaviour
{
    private static VerticalLayersControls? instance;
    private GameObject canvasRoot = null!;
    private InventoryUI inventory = null!;
    private bool open;

    internal static void Open(
        InventoryUI inventory,
        ModToolSession session)
    {
        Plugin.Log.LogInfo(
            "Opened Vertical Layers from the Inventory Mods tab.");
        if (instance != null && instance.canvasRoot != null)
            Destroy(instance.canvasRoot);

        ModOverlay overlay = ModUi.CreateOverlay(
            inventory,
            "Silverpine Vertical Layers Controls",
            new Vector2(680f, 760f));
        instance = overlay.Panel.AddComponent<VerticalLayersControls>();
        instance.canvasRoot = overlay.Root;
        instance.inventory = inventory;
        instance.AttachSession(session);
        instance.open = true;
        instance.Build();
    }

    private void Update()
    {
        if (open && Input.GetKeyDown(KeyCode.Escape))
            Close();
    }

    private void Build()
    {
        VerticalLayoutGroup layout =
            GetComponent<VerticalLayoutGroup>() ??
            gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(42, 42, 30, 30);
        layout.spacing = 10f;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlHeight = false;
        layout.childControlWidth = true;
        layout.childForceExpandWidth = true;

        Button template = ModUi.GetInventoryButtonTemplate(inventory);
        ModUi.CloneTitle(template, transform, "Vertical Layers");
        AddStatus(template);
        AddButton(template,
            $"Lower-floor view: {Plugin.ViewMode.Value}",
            ToggleViewMode);
        AddButton(template,
            $"Lower render resolution: {Plugin.LowerResolutionScale.Value:P0}",
            CycleResolution);
        AddButton(template,
            $"Idle lower refresh: {Plugin.AmbientRefreshFps.Value} FPS",
            CycleAmbientRefresh);
        AddButton(template, "Refresh lower-floor display", () =>
        {
            VerticalLayerManager.Instance?.Renderer.MarkDirty();
            UpperNotificationUI.Instance.OneOff(
                "Lower-floor display queued for refresh.");
        });
        AddButton(template, "Rebuild projected roof floors", () =>
        {
            VerticalLayerManager.Instance?.RebuildAllActiveProjections();
            UpperNotificationUI.Instance.OneOff(
                "Projected roof floors rebuilt.");
            Rebuild();
        });
        AddButton(template, "Emergency return to ground", () =>
        {
            VerticalLayerManager.Instance?.EmergencyReturnToGround();
            Rebuild();
        });
        AddButton(template, "Close", Close);
    }

    private void AddStatus(Button template)
    {
        VerticalLayerManager? manager = VerticalLayerManager.Instance;
        string levels = manager == null
            ? "not initialized"
            : string.Join(", ", manager.ActiveLevels.Select(level =>
                (level + 1).ToString()));
        string status = manager == null
            ? "Waiting for a loaded game."
            : $"Current floor: {manager.CurrentPlayerLevel + 1}\n" +
              $"Generated floors: {levels}\n" +
              $"Maximum unsupported span: {Plugin.MaximumUnsupportedSpan.Value} tiles";
        TextMeshProUGUI text = ModUi.CloneTitle(
            template, transform, status, preferredHeight: 105f);
        text.fontSize = 23f;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Ellipsis;
    }

    private void AddButton(Button template, string label, Action action) =>
        ModUi.CloneButton(template, transform, label, action);

    private void ToggleViewMode()
    {
        Plugin.ViewMode.Value = Plugin.ViewMode.Value == LowerLayerViewMode.Live
            ? LowerLayerViewMode.Void
            : LowerLayerViewMode.Live;
        VerticalLayerManager.Instance?.Renderer.ApplyModeImmediately();
        Rebuild();
    }

    private void CycleResolution()
    {
        float current = Plugin.LowerResolutionScale.Value;
        Plugin.LowerResolutionScale.Value = current < 0.58f
            ? 2f / 3f
            : current < 0.84f
                ? 1f
                : 0.5f;
        VerticalLayerManager.Instance?.Renderer.RecreateTargets();
        Rebuild();
    }

    private void CycleAmbientRefresh()
    {
        int current = Plugin.AmbientRefreshFps.Value;
        Plugin.AmbientRefreshFps.Value = current switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            _ => 0
        };
        Rebuild();
    }

    private void Rebuild()
    {
        foreach (Transform child in transform.Cast<Transform>().ToArray())
            Destroy(child.gameObject);
        Build();
    }

    private void Close()
    {
        if (!open)
            return;
        open = false;
        ReleaseSession();
        if (canvasRoot != null)
            Destroy(canvasRoot);
        else
            Destroy(gameObject);
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(instance, this))
            instance = null;
        base.OnDestroy();
    }
}
