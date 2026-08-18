#nullable enable

using HarmonyLib;
using Silverpine.ModdingTools;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SilverpineVerticalLayers;

internal static class StructureHandlerCompatibility
{
    internal const string SupportedVersion = "1.1.10";
    internal static Type? PluginType { get; private set; }
    internal static Type? TransferType { get; private set; }
    internal static Type? EditorType { get; private set; }
    internal static bool Available { get; private set; }

    internal static void Initialize()
    {
        PluginType = AccessTools.TypeByName("StructureHandler.Plugin");
        TransferType = AccessTools.TypeByName("StructureHandler.StructureTransfer");
        EditorType = AccessTools.TypeByName("StructureHandler.StructureEditorUI");
        if (PluginType == null || TransferType == null || EditorType == null)
        {
            Plugin.Log.LogInfo(
                "Structure Handler was not detected; optional floor-aware integration is disabled.");
            return;
        }

        string version = AccessTools.Field(PluginType, "PluginVersion")
            ?.GetRawConstantValue()?.ToString() ?? "unknown";
        if (!string.Equals(version, SupportedVersion, StringComparison.Ordinal))
        {
            Plugin.Log.LogWarning(
                $"Structure Handler {version} was detected, but Vertical Layers targets " +
                $"{SupportedVersion}; optional integration is disabled.");
            return;
        }

        string[] transferMethods =
        {
            "FindPlayerStructures",
            "IsInsideSelectedExportBounds",
            "IsTerrainObject",
            "RegisterImportedObjects"
        };
        if (transferMethods.Any(name =>
                AccessTools.Method(TransferType, name) == null) ||
            AccessTools.Method(PluginType, "GetWorldTile") == null ||
            AccessTools.Method(EditorType, "OnGUI") == null)
        {
            Plugin.Log.LogWarning(
                "Structure Handler integration signatures did not match 1.1.10; integration is disabled.");
            return;
        }

        Available = true;
        Plugin.Log.LogInfo(
            "Enabled floor-aware Structure Handler 1.1.10 integration.");
    }

    internal static MethodBase RequireMethod(Type? type, string name) =>
        AccessTools.Method(type, name) ??
        throw new MissingMethodException(type?.FullName, name);

    internal static void ExtendExportCandidates(ref List<GameObject> candidates)
    {
        candidates ??= new List<GameObject>();
        candidates.RemoveAll(gameObject =>
            gameObject == null ||
            PrefabFactory.IsTemplate(gameObject) ||
            gameObject.GetComponent<ProjectedUpperFloor>() != null);

        HashSet<GameObject> expanded = new(candidates);
        foreach (FloorPortal portal in candidates
                     .Where(gameObject => gameObject != null)
                     .SelectMany(gameObject => gameObject.GetComponents<FloorPortal>()))
        {
            FloorPortal? pair = portal.FindPair();
            if (pair != null && !PrefabFactory.IsTemplate(pair.gameObject))
                expanded.Add(pair.gameObject);
        }
        foreach (VerticalSupport support in candidates
                     .Where(gameObject => gameObject != null)
                     .SelectMany(gameObject => gameObject.GetComponents<VerticalSupport>()))
        {
            foreach (VerticalSupport pair in VerticalSupport.All.Where(pair =>
                         pair != null && pair.PairId == support.PairId &&
                         !PrefabFactory.IsTemplate(pair.gameObject)))
                expanded.Add(pair.gameObject);
        }
        candidates = expanded
            .Where(gameObject => gameObject != null &&
                                 gameObject.GetComponent<ProjectedUpperFloor>() == null)
            .ToList();
    }

    internal static List<GameObject> PrepareImportedObjects(
        IEnumerable<GameObject>? objects)
    {
        List<GameObject> imported = objects?
            .Where(gameObject => gameObject != null)
            .Distinct()
            .ToList() ?? new List<GameObject>();
        foreach (GameObject gameObject in imported)
            PrefabFactory.RestoreInstance(gameObject);

        foreach (IGrouping<string, FloorPortal> group in imported
                     .SelectMany(gameObject => gameObject.GetComponents<FloorPortal>())
                     .Where(portal => !string.IsNullOrWhiteSpace(portal.PairId))
                     .GroupBy(portal => portal.PairId))
        {
            FloorPortal[] pair = group.ToArray();
            if (pair.Length != 2)
            {
                Plugin.Log.LogWarning(
                    $"Imported stair pair '{group.Key}' had {pair.Length} end(s); " +
                    "the original destination metadata was retained.");
                continue;
            }
            string newId = Guid.NewGuid().ToString("N");
            pair[0].ReconfigureImportedPair(
                newId, pair[1].SourceLevel, pair[1].SourceCanonical);
            pair[1].ReconfigureImportedPair(
                newId, pair[0].SourceLevel, pair[0].SourceCanonical);
        }

        foreach (IGrouping<string, VerticalSupport> group in imported
                     .SelectMany(gameObject => gameObject.GetComponents<VerticalSupport>())
                     .Where(support => !string.IsNullOrWhiteSpace(support.PairId))
                     .GroupBy(support => support.PairId))
        {
            VerticalSupport[] pair = group.ToArray();
            VerticalSupport? anchor = pair.FirstOrDefault(support => support.IsAnchor);
            if (pair.Length != 2 || anchor == null)
            {
                Plugin.Log.LogWarning(
                    $"Imported support pair '{group.Key}' was incomplete; " +
                    "the original pair metadata was retained.");
                continue;
            }
            string newId = Guid.NewGuid().ToString("N");
            int anchorLevel = LayerCoordinates.GetLevel(
                anchor.transform.GetVector2IntPosition());
            foreach (VerticalSupport support in pair)
                support.ReconfigureImportedPair(newId, anchorLevel);
        }
        return imported;
    }

    internal static bool TryGetCanonicalBounds(
        out Vector2Int topLeft,
        out Vector2Int bottomRight)
    {
        topLeft = default;
        bottomRight = default;
        if (TransferType == null)
            return false;
        object? top = AccessTools.Field(TransferType, "ExportTopLeft")
            ?.GetValue(null);
        object? bottom = AccessTools.Field(TransferType, "ExportBottomRight")
            ?.GetValue(null);
        if (top is not Vector2Int rawTop || bottom is not Vector2Int rawBottom)
            return false;
        topLeft = LayerCoordinates.GetCanonical(rawTop);
        bottomRight = LayerCoordinates.GetCanonical(rawBottom);
        return true;
    }
}

[HarmonyPatch]
internal static class StructureHandlerWorldTilePatch
{
    private static bool Prepare() => StructureHandlerCompatibility.Available;
    private static MethodBase TargetMethod() =>
        StructureHandlerCompatibility.RequireMethod(
            StructureHandlerCompatibility.PluginType, "GetWorldTile");

    private static bool Prefix(Vector2 __0, ref Vector2Int __result)
    {
        Vector2Int physical = Vector2Int.RoundToInt(__0);
        if (!LayerCoordinates.TryDecode(physical, out int level, out _) ||
            level <= 0)
            return true;
        __result = Vector2Int.zero;
        return false;
    }
}

[HarmonyPatch]
internal static class StructureHandlerCanonicalBoundsPatch
{
    private static bool Prepare() => StructureHandlerCompatibility.Available;
    private static MethodBase TargetMethod() =>
        StructureHandlerCompatibility.RequireMethod(
            StructureHandlerCompatibility.TransferType,
            "IsInsideSelectedExportBounds");

    private static void Postfix(Vector2Int __0, ref bool __result)
    {
        if (!StructureHandlerCompatibility.TryGetCanonicalBounds(
                out Vector2Int topLeft,
                out Vector2Int bottomRight))
            return;
        Vector2Int canonical = LayerCoordinates.GetCanonical(__0);
        __result = canonical.x >= topLeft.x &&
                   canonical.x <= bottomRight.x &&
                   canonical.y <= topLeft.y &&
                   canonical.y >= bottomRight.y;
    }
}

[HarmonyPatch]
internal static class StructureHandlerUpperFloorObjectPatch
{
    private static bool Prepare() => StructureHandlerCompatibility.Available;
    private static MethodBase TargetMethod() =>
        StructureHandlerCompatibility.RequireMethod(
            StructureHandlerCompatibility.TransferType, "IsTerrainObject");

    private static bool Prefix(GameObject __0, ref bool __result)
    {
        if (__0 == null || !LayerCoordinates.TryDecode(
                __0.transform.GetVector2IntPosition(),
                out int level,
                out _) || level <= 0)
            return true;
        string prefabName = SerializationManager.GetPrefabName(__0);
        if (!prefabName.StartsWith(
                "prefab_tile_", StringComparison.OrdinalIgnoreCase))
            return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch]
internal static class StructureHandlerExportPairPatch
{
    private static bool Prepare() => StructureHandlerCompatibility.Available;
    private static MethodBase TargetMethod() =>
        StructureHandlerCompatibility.RequireMethod(
            StructureHandlerCompatibility.TransferType,
            "FindPlayerStructures");

    private static void Postfix(ref List<GameObject> __result) =>
        StructureHandlerCompatibility.ExtendExportCandidates(ref __result);
}

[HarmonyPatch]
internal static class StructureHandlerImportPatch
{
    private static bool Prepare() => StructureHandlerCompatibility.Available;
    private static MethodBase TargetMethod() =>
        StructureHandlerCompatibility.RequireMethod(
            StructureHandlerCompatibility.TransferType,
            "RegisterImportedObjects");

    private static void Prefix(
        IEnumerable<GameObject> __0,
        out List<GameObject> __state)
    {
        __state = StructureHandlerCompatibility.PrepareImportedObjects(__0);
    }

    private static void Postfix(List<GameObject> __state)
    {
        VerticalLayerManager.Instance?.FinishStructureHandlerImport(__state);
    }
}

internal static class StructureEditorFloorView
{
    private static bool loggedSelector;

    private sealed class RecordState
    {
        internal int Level;
        internal float PhysicalX;
    }

    private sealed class ViewState
    {
        internal object Editor = null!;
        internal object Structure = null!;
        internal FieldInfo ObjectsField = null!;
        internal FieldInfo TerrainField = null!;
        internal IList OriginalObjects = null!;
        internal IList OriginalTerrain = null!;
        internal IList ViewObjects = null!;
        internal IList ViewTerrain = null!;
        internal readonly Dictionary<object, RecordState> ObjectStates = new();
        internal readonly Dictionary<object, RecordState> TerrainStates = new();
    }

    private static ViewState? active;
    private static bool viewDisabledAfterError;

    internal static void BeginSafely(object editor, string caller)
    {
        if (viewDisabledAfterError)
            return;
        try
        {
            Begin(editor);
        }
        catch (Exception exception)
        {
            viewDisabledAfterError = true;
            Plugin.Log.LogError(
                $"Disabled the Structure Editor floor view after {caller} failed; " +
                "the base editor will remain usable. " + exception);
            SetEditorField(
                editor,
                "status",
                "Vertical Layers floor filtering was disabled after a compatibility error. " +
                "The base Structure Editor remains active.");
        }
    }

    internal static void EndSafely(object editor, string caller)
    {
        try
        {
            End(editor);
        }
        catch (Exception exception)
        {
            viewDisabledAfterError = true;
            Plugin.Log.LogError(
                $"Structure Editor floor-view cleanup failed during {caller}; " +
                "the base editor session was preserved. " + exception);
            SetEditorField(
                editor,
                "status",
                "Vertical Layers floor filtering encountered an error. " +
                "Close and reopen the editor before editing this structure.");
        }
    }

    internal static void Begin(object editor)
    {
        if (!StructureHandlerCompatibility.Available || active != null ||
            StructureHandlerCompatibility.EditorType == null)
            return;
        FieldInfo? structureField = AccessTools.Field(
            StructureHandlerCompatibility.EditorType, "structure");
        object? structure = structureField?.GetValue(editor);
        if (structure == null)
            return;
        FieldInfo? objectsField = AccessTools.Field(structure.GetType(), "objects");
        FieldInfo? terrainField = AccessTools.Field(
            structure.GetType(), "supportingTerrain");
        if (objectsField?.GetValue(structure) is not IList objects ||
            terrainField?.GetValue(structure) is not IList terrain)
            return;

        ViewState state = new()
        {
            Editor = editor,
            Structure = structure,
            ObjectsField = objectsField,
            TerrainField = terrainField,
            OriginalObjects = objects,
            OriginalTerrain = terrain,
            ViewObjects = CreateCompatibleList(objects),
            ViewTerrain = CreateCompatibleList(terrain)
        };
        int selected = Plugin.StructureEditorFloor.Value;
        try
        {
            PopulateView(objects, state.ViewObjects, state.ObjectStates, selected);
            PopulateView(terrain, state.ViewTerrain, state.TerrainStates, selected);
            objectsField.SetValue(structure, state.ViewObjects);
            terrainField.SetValue(structure, state.ViewTerrain);
            SetEditorField(editor, "renderOrderDirty", true);
            SetEditorField(editor, "renderOrderStructure", null);
            active = state;
        }
        catch
        {
            RestoreOriginalCoordinates(state.ObjectStates);
            RestoreOriginalCoordinates(state.TerrainStates);
            objectsField.SetValue(structure, objects);
            terrainField.SetValue(structure, terrain);
            throw;
        }
    }

    internal static void End(object editor)
    {
        ViewState? state = active;
        if (state == null || !ReferenceEquals(state.Editor, editor))
            return;
        active = null;
        IList currentObjects =
            state.ObjectsField.GetValue(state.Structure) as IList ??
            state.ViewObjects;
        IList currentTerrain =
            state.TerrainField.GetValue(state.Structure) as IList ??
            state.ViewTerrain;
        try
        {
            MergeView(
                state.OriginalObjects, currentObjects, state.ObjectStates);
            MergeView(
                state.OriginalTerrain, currentTerrain, state.TerrainStates);
        }
        finally
        {
            state.ObjectsField.SetValue(state.Structure, state.OriginalObjects);
            state.TerrainField.SetValue(state.Structure, state.OriginalTerrain);
        }
    }

    internal static void DrawSelector(object editor)
    {
        if (!StructureHandlerCompatibility.Available)
            return;
        using ModGuiScope scope = ModGui.BeginScaled(1920f, 1080f);
        GUI.depth = -2000;
        Color oldBackground = GUI.backgroundColor;
        GUI.backgroundColor = new Color(0.25f, 0.85f, 0.72f, 1f);
        Rect panel = new(430f, 4f, 1250f, 72f);
        GUI.Box(panel, "VERTICAL LAYERS — EDITOR FLOOR");
        GUI.backgroundColor = oldBackground;
        GUILayout.BeginArea(new Rect(448f, 30f, 1214f, 40f));
        GUILayout.BeginHorizontal();
        GUILayout.Label("Floor view", GUILayout.Width(100f));
        DrawChoice(editor, -1, "All");
        for (int level = 0; level < Plugin.MaxLevels.Value; level++)
            DrawChoice(editor, level, FloorLabel(level));
        GUILayout.EndHorizontal();
        GUILayout.EndArea();

        if (!loggedSelector)
        {
            loggedSelector = true;
            Plugin.Log.LogInfo(
                "Drew the Vertical Layers floor selector in Structure Editor.");
        }
    }

    private static void DrawChoice(object editor, int value, string label)
    {
        bool selected = Plugin.StructureEditorFloor.Value == value;
        bool next = GUILayout.Toggle(
            selected, label, GUI.skin.button, GUILayout.Width(112f));
        if (!next || selected)
            return;
        Plugin.StructureEditorFloor.Value = value;
        SetEditorField(editor, "selectedObject", -1);
        SetEditorField(editor, "selectedTerrain", -1);
        SetEditorField(editor, "previewPan", Vector2.zero);
        SetEditorField(editor, "previewOrigin", Vector2.zero);
        SetEditorField(editor, "renderOrderDirty", true);
        SetEditorField(editor, "renderOrderStructure", null);
        SetEditorField(
            editor,
            "status",
            value < 0
                ? "Showing all floors at canonical coordinates; new records default to ground."
                : $"Showing {FloorLabel(value)} at canonical Silverpine coordinates.");
    }

    private static string FloorLabel(int level) => level switch
    {
        0 => "Ground",
        1 => "Second",
        2 => "Third",
        _ => $"Floor {level + 1}"
    };

    private static IList CreateCompatibleList(IList source) =>
        Activator.CreateInstance(source.GetType()) as IList ??
        throw new InvalidOperationException(
            "Could not create a Structure Handler record list.");

    private static void PopulateView(
        IList original,
        IList view,
        IDictionary<object, RecordState> states,
        int selectedFloor)
    {
        foreach (object record in original)
        {
            int level = GetRecordLevel(record);
            if (selectedFloor >= 0 && level != selectedFloor)
                continue;
            float physicalX = GetRecordX(record);
            states[record] = new RecordState
            {
                Level = level,
                PhysicalX = physicalX
            };
            SetRecordX(record, ToCanonicalX(physicalX, level));
            view.Add(record);
        }
    }

    private static void RestoreOriginalCoordinates(
        IDictionary<object, RecordState> states)
    {
        foreach ((object record, RecordState state) in states)
            SetRecordX(record, state.PhysicalX);
    }

    private static void MergeView(
        IList original,
        IList view,
        IDictionary<object, RecordState> states)
    {
        foreach (object record in states.Keys.ToArray())
        {
            if (!view.Contains(record))
                original.Remove(record);
        }

        foreach (object record in view)
        {
            int level = states.TryGetValue(record, out RecordState state)
                ? state.Level
                : Plugin.StructureEditorFloor.Value >= 0
                    ? Plugin.StructureEditorFloor.Value
                    : 0;
            SetRecordX(record, ToPhysicalX(GetRecordX(record), level));
            if (!original.Contains(record))
                original.Add(record);
        }
    }

    private static int GetRecordLevel(object record)
    {
        float x = GetRecordX(record);
        float y = GetRecordCoordinate(record, "y");
        if (y < LayerCoordinates.CanonicalMin - 0.5f ||
            y > LayerCoordinates.CanonicalMax + 0.5f)
            return 0;
        for (int level = 1; level < Plugin.MaxLevels.Value; level++)
        {
            float canonical = ToCanonicalX(x, level);
            if (canonical >= LayerCoordinates.CanonicalMin - 0.5f &&
                canonical <= LayerCoordinates.CanonicalMax + 0.5f)
                return level;
        }
        return 0;
    }

    private static float ToCanonicalX(float x, int level) =>
        level <= 0
            ? x
            : x - LayerCoordinates.OriginX -
              (level - 1) * LayerCoordinates.StrideX;

    private static float ToPhysicalX(float x, int level) =>
        level <= 0
            ? x
            : x + LayerCoordinates.OriginX +
              (level - 1) * LayerCoordinates.StrideX;

    private static float GetRecordX(object record) =>
        GetRecordCoordinate(record, "x");

    private static float GetRecordCoordinate(object record, string name)
    {
        object? value = AccessTools.Field(record.GetType(), name)?.GetValue(record);
        return value == null ? 0f : Convert.ToSingle(value);
    }

    private static void SetRecordX(object record, float value)
    {
        FieldInfo? field = AccessTools.Field(record.GetType(), "x");
        if (field == null)
            return;
        object converted = field.FieldType == typeof(int)
            ? (object)Mathf.RoundToInt(value)
            : field.FieldType == typeof(float)
                ? value
                : Convert.ChangeType(value, field.FieldType);
        field.SetValue(record, converted);
    }

    private static void SetEditorField(
        object editor,
        string name,
        object? value)
    {
        AccessTools.Field(editor.GetType(), name)?.SetValue(editor, value);
    }
}

[HarmonyPatch]
internal static class StructureEditorOnGuiPatch
{
    private static bool Prepare() => StructureHandlerCompatibility.Available;
    private static MethodBase TargetMethod() =>
        StructureHandlerCompatibility.RequireMethod(
            StructureHandlerCompatibility.EditorType, "OnGUI");

    private static void Prefix(object __instance) =>
        StructureEditorFloorView.BeginSafely(__instance, "OnGUI prefix");

    private static void Postfix(object __instance)
    {
        StructureEditorFloorView.EndSafely(__instance, "OnGUI postfix");
        StructureEditorFloorView.DrawSelector(__instance);
    }

    private static Exception? Finalizer(
        object __instance,
        Exception? __exception)
    {
        StructureEditorFloorView.EndSafely(__instance, "OnGUI finalizer");
        return __exception;
    }
}

[HarmonyPatch]
internal static class StructureEditorMutationPatch
{
    private static bool Prepare() => StructureHandlerCompatibility.Available;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        Type? type = StructureHandlerCompatibility.EditorType;
        foreach (string name in new[]
                 {
                     "Save",
                     "SaveAs",
                     "Load",
                     "CreateBlankStructure",
                     "CombineCheckedFiles",
                     "Close"
                 })
        {
            MethodInfo? method = AccessTools.Method(type, name);
            if (method != null)
                yield return method;
        }
    }

    private static void Prefix(object __instance, MethodBase __originalMethod) =>
        StructureEditorFloorView.EndSafely(
            __instance, __originalMethod.Name + " prefix");

    private static void Postfix(object __instance, MethodBase __originalMethod) =>
        StructureEditorFloorView.BeginSafely(
            __instance, __originalMethod.Name + " postfix");

    private static Exception? Finalizer(
        Exception? __exception)
    {
        return __exception;
    }
}
