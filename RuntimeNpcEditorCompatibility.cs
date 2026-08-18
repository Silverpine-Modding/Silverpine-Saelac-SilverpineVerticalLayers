#nullable enable

using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SilverpineVerticalLayers;

internal sealed class RoutineFloorUiState : MonoBehaviour
{
    internal int Level;
    internal Button? Button;
}

internal static class RuntimeNpcEditorCompatibility
{
    private const string EditorTypeName =
        "RuntimeNPCEditor.NativeNPCEditorWindow";
    private static Type? editorType;
    private static MethodInfo? createPageButton;
    private static MethodInfo? setButtonLabel;

    internal static void Initialize(Harmony harmony)
    {
        editorType = AccessTools.TypeByName(EditorTypeName);
        if (editorType == null)
        {
            Plugin.Log.LogDebug(
                "Runtime NPC Editor was not detected; its optional routine " +
                "floor selector is disabled.");
            return;
        }
        MethodInfo? build = AccessTools.Method(editorType, "BuildRoutinePage");
        MethodInfo? load = AccessTools.Method(editorType, "LoadRoutineFields");
        MethodInfo? save = AccessTools.Method(editorType, "SaveRoutine");
        createPageButton = AccessTools.Method(editorType, "CreatePageButton");
        setButtonLabel = AccessTools.Method(editorType, "SetButtonLabel");
        if (build == null || load == null || save == null ||
            createPageButton == null || setButtonLabel == null)
        {
            Plugin.Log.LogWarning(
                "Runtime NPC Editor was detected, but its routine UI no " +
                "longer matches Vertical Layers 0.4.3. The floor selector " +
                "was not installed.");
            return;
        }

        harmony.Patch(build, postfix: new HarmonyMethod(
            typeof(RuntimeNpcEditorCompatibility), nameof(BuildPostfix)));
        harmony.Patch(load, postfix: new HarmonyMethod(
            typeof(RuntimeNpcEditorCompatibility), nameof(LoadPostfix)));
        harmony.Patch(save,
            prefix: new HarmonyMethod(
                typeof(RuntimeNpcEditorCompatibility), nameof(SavePrefix)),
            postfix: new HarmonyMethod(
                typeof(RuntimeNpcEditorCompatibility), nameof(SavePostfix)));
        Plugin.Log.LogInfo(
            "Enabled floor-aware Runtime NPC Editor routine integration.");
    }

    private static void BuildPostfix(object __instance)
    {
        Component editor = (Component)__instance;
        RoutineFloorUiState state =
            editor.GetComponent<RoutineFloorUiState>() ??
            editor.gameObject.AddComponent<RoutineFloorUiState>();
        Action cycle = () =>
        {
            state.Level = (state.Level + 1) % Plugin.MaxLevels.Value;
            UpdateButton(__instance, state);
        };
        state.Button = (Button?)createPageButton?.Invoke(
            __instance,
            new object[]
            {
                FloorLabel(state.Level),
                new Vector2(500f, 295f),
                new Vector2(110f, 36f),
                cycle
            });
        RefreshFromFields(__instance, state);
    }

    private static void LoadPostfix(object __instance)
    {
        RoutineFloorUiState? state =
            ((Component)__instance).GetComponent<RoutineFloorUiState>();
        if (state != null && state.Button != null)
            RefreshFromFields(__instance, state);
    }

    private static void SavePrefix(object __instance, out string __state)
    {
        __state = "";
        RoutineFloorUiState? state =
            ((Component)__instance).GetComponent<RoutineFloorUiState>();
        TMP_InputField? targetX = GetInput(__instance, "targetX");
        TMP_InputField? targetY = GetInput(__instance, "targetY");
        if (state == null || state.Level <= 0 || targetX == null ||
            targetY == null || !int.TryParse(targetX.text, out int x) ||
            !int.TryParse(targetY.text, out int y) ||
            !LayerCoordinates.IsCanonicalSilverpine(new Vector2Int(x, y)))
            return;
        __state = targetX.text;
        targetX.text = LayerCoordinates
            .ToPhysical(new Vector2Int(x, y), state.Level)
            .x.ToString();
    }

    private static void SavePostfix(object __instance, string __state)
    {
        if (string.IsNullOrEmpty(__state))
            return;
        TMP_InputField? targetX = GetInput(__instance, "targetX");
        if (targetX != null)
            targetX.text = __state;
    }

    private static void RefreshFromFields(
        object editor,
        RoutineFloorUiState state)
    {
        TMP_InputField? targetX = GetInput(editor, "targetX");
        TMP_InputField? targetY = GetInput(editor, "targetY");
        if (targetX == null || targetY == null ||
            !int.TryParse(targetX.text, out int x) ||
            !int.TryParse(targetY.text, out int y))
            return;
        Vector2Int physical = new(x, y);
        if (LayerCoordinates.TryDecode(
                physical, out int level, out Vector2Int canonical))
        {
            state.Level = level;
            targetX.text = canonical.x.ToString();
            targetY.text = canonical.y.ToString();
        }
        else
        {
            state.Level = 0;
        }
        UpdateButton(editor, state);
    }

    private static TMP_InputField? GetInput(object editor, string name) =>
        Traverse.Create(editor).Field(name).GetValue<TMP_InputField>();

    private static void UpdateButton(object editor, RoutineFloorUiState state)
    {
        if (state.Button != null)
            setButtonLabel?.Invoke(
                editor, new object[] { state.Button, FloorLabel(state.Level) });
    }

    private static string FloorLabel(int level) => level switch
    {
        0 => "Ground",
        1 => "Floor 2",
        2 => "Floor 3",
        _ => $"Floor {level + 1}"
    };
}
