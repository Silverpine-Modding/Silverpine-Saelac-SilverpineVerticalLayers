using System.Reflection;
using BepInEx.Configuration;
using Mono.Cecil;

internal static class EditorFloorViewChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public sealed class Record { public float x, y = 0; }
    public sealed class Document
    {
        public List<Record> objects = new();
        public List<Record> supportingTerrain = new();
    }
    public sealed class Editor
    {
        public Document structure = new();
        public bool open = true, historyDirty, brushStroke;
        public float historyDeadline = 0;
        public int editRevision, invalidations, conflictRevision = 10;
        public void InvalidateRenderOrder() => invalidations++;
    }
    internal static void Run(Assembly assembly, AssemblyDefinition compiled, Action<bool, string> check)
    {
        Type plugin = assembly.GetType("SilverpineVerticalLayers.Plugin", true)!;
        Type bridge = assembly.GetType("SilverpineVerticalLayers.StructureHandlerCompatibility", true)!;
        Type view = assembly.GetType("SilverpineVerticalLayers.StructureEditorFloorView", true)!;
        var config = new ConfigFile(Path.Combine(Path.GetTempPath(), "vertical-editor-check.cfg"), false) { SaveOnConfigSet = false };
        ConfigEntry<int> Bind(string name, int value)
        {
            var entry = config.Bind("Test", name, value);
            plugin.GetField(name, Flags)!.SetValue(null, entry);
            return entry;
        }
        Bind("LayerOriginX", 20000); Bind("LayerStrideX", 512); Bind("MaxLevels", 3);
        var selected = Bind("StructureEditorFloor", 1);
        bridge.GetField("<Available>k__BackingField", Flags)!.SetValue(null, true);
        bridge.GetField("<EditorType>k__BackingField", Flags)!.SetValue(null, typeof(Editor));
        void Begin(Editor e) => view.GetMethod("Begin", Flags)!.Invoke(null, new object[] { e });
        void End(Editor e) => view.GetMethod("End", Flags)!.Invoke(null, new object[] { e });
        IDisposable Scope(Editor e) => (IDisposable)Activator.CreateInstance(view.GetNestedType("DocumentScope", Flags)!,
            Flags, null, new object[] { e }, null)!;
        Record ground = new() { x = 3 }, upper = new() { x = 20005.5f }, third = new() { x = 20520 };
        var terrain = new Record { x = 20006 };
        var editor = new Editor();
        editor.structure.objects.AddRange(new[] { ground, upper, third });
        editor.structure.supportingTerrain.Add(terrain);
        var originals = editor.structure.objects;
        Begin(editor);
        var visible = editor.structure.objects;
        check(visible.Count == 1 && visible[0] == upper && upper.x == 5.5f && terrain.x == 6,
            "floor projection filters upper floor and preserves fractional coordinates");
        for (int i = 0; i < 1000; i++) Begin(editor);
        check(ReferenceEquals(visible, editor.structure.objects) && editor.invalidations == 1 && upper.x == 5.5f,
            "1000 idle GUI events reuse the same projected lists without invalidation or coordinate drift");
        check(!(bool)view.GetMethod("HasPendingHistory", Flags)!.Invoke(null, new object[] { editor, false })!,
            "idle history does not request a full-document merge");
        editor.historyDirty = true;
        check((bool)view.GetMethod("HasPendingHistory", Flags)!.Invoke(null, new object[] { editor, true })!,
            "pending committed edits request complete-document history");
        editor.brushStroke = true;
        check(!(bool)view.GetMethod("HasPendingHistory", Flags)!.Invoke(null, new object[] { editor, true })!,
            "an active brush stroke remains grouped without per-cell history merges");
        editor.brushStroke = false;
        upper.x = 7.5f; editor.editRevision++;
        editor.conflictRevision = 10;
        using (Scope(editor))
        {
            check(ReferenceEquals(originals, editor.structure.objects) && upper.x == 20007.5f && originals.Count == 3,
                "history/save scope restores every floor in physical coordinates");
            check(editor.conflictRevision == -1, "save cannot reuse a visible-floor placement-conflict result");
            using (Scope(editor)) { }
            check(ReferenceEquals(originals, editor.structure.objects) && upper.x == 20007.5f,
                "nested save/history scope does not prematurely reproject the document");
        }
        check(editor.structure.objects.Count == 1 && upper.x == 7.5f && editor.invalidations == 2,
            "leaving a document scope resumes the cached floor view");
        var added = new Record { x = 9 };
        editor.structure.objects.Clear(); editor.structure.objects.Add(added);
        selected.Value = 2; // External config change must still assign new records to the OLD floor.
        Begin(editor);
        check(originals.Count == 3 && originals.Contains(ground) && originals.Contains(third) &&
            originals.Contains(added) && !originals.Contains(upper) && added.x == 20009 && third.x == 8,
            "floor switching merges additions/deletions to their original floor without dropping hidden records");
        using (Scope(editor))
        {
            editor.structure = new Document();
            editor.structure.objects.AddRange(new[] { new Record { x = 1 }, new Record { x = 20518 } });
            editor.editRevision++;
        }
        check(editor.structure.objects.Count == 1 && editor.structure.objects[0].x == 6,
            "undo/load document replacement projects the new document, not stale lists");
        editor.open = false;
        Begin(editor);
        check(editor.structure.objects.Count == 2 && editor.structure.objects[1].x == 20518,
            "closing restores the complete physical document");
        End(editor); // idempotent cleanup

        var gui = compiled.MainModule.Types.Single(t => t.Name == "StructureEditorOnGuiPatch");
        check(!gui.Methods.Single(m => m.Name == "Postfix").Body.Instructions.Any(i =>
            i.Operand is MethodReference m && m.Name == "EndSafely"), "normal GUI postfix no longer merges every record");
        var mutation = compiled.MainModule.Types.Single(t => t.Name == "StructureEditorMutationPatch");
        var strings = mutation.NestedTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<string>().ToHashSet();
        check(new[] { "Save", "SaveAs", "Load", "ResetHistory", "Undo", "Redo", "Close", "OnFrameworkSessionClosed" }.All(strings.Contains),
            "save, undo, initialization and emergency-close paths all enter document scopes");
        check(compiled.MainModule.Types.Any(t => t.Name == "StructureEditorHistoryPatch"),
            "new FlushHistory implementation has a dedicated conditional scope patch");
        bridge.GetField("<Available>k__BackingField", Flags)!.SetValue(null, false);
    }
}
