using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Mono.Cecil;
using Silverpine.ModdingTools;

if (args.Length != 1)
    throw new ArgumentException("Usage: CompatibilityChecks <Silverpine game directory>");
string game = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (string folder in new[] { "Silverpine_Data/Managed", "BepInEx/core" })
    {
        string path = Path.Combine(game, folder, name.Name + ".dll");
        if (File.Exists(path))
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
// Exercise the installed framework, not merely the project-reference build.
string installed = Path.Combine(game, "BepInEx/plugins/ModdingTools/ModdingTools.dll");
Checks.Run(installed);

internal static class Checks
{
    private static int passed;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                      BindingFlags.Static | BindingFlags.Instance;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        passed++;
        Console.WriteLine("PASS: " + message);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Run(string installed)
    {
        Assembly vertical = typeof(SilverpineVerticalLayers.Plugin).Assembly;
        using var plugin = AssemblyDefinition.ReadAssembly(vertical.Location);
        using var framework = AssemblyDefinition.ReadAssembly(installed);
        var methods = framework.MainModule.Types.SelectMany(t => t.Methods)
            .Select(m => m.FullName).ToHashSet();
        var references = plugin.MainModule.GetMemberReferences()
            .Where(m => m.DeclaringType.Scope is AssemblyNameReference a && a.Name == "ModdingTools").ToArray();
        Check(references.All(m => methods.Contains(m.FullName)),
            $"all {references.Length} framework member references resolve against installed {framework.Name.Version}");
        Check(File.ReadAllBytes(typeof(ConstructionMenu).Assembly.Location)
                .SequenceEqual(File.ReadAllBytes(installed)),
            "registration tests use a byte-identical copy of the installed framework DLL");
        Check(vertical.GetType("SilverpineVerticalLayers.NativeConstructMenuPatch") == null &&
              vertical.GetType("SilverpineVerticalLayers.NativeConstructAbilityPatch") == null,
            "obsolete radial/context patches are absent");
        var pluginType = plugin.MainModule.Types.Single(t => t.FullName == "SilverpineVerticalLayers.Plugin");
        Check(pluginType.CustomAttributes.Any(a => a.AttributeType.Name == "BepInDependency" &&
            a.ConstructorArguments.Count == 2 &&
            Equals(a.ConstructorArguments[0].Value, "Saelac.Silverpine.ModdingTools") &&
            Equals(a.ConstructorArguments[1].Value, "1.11.0")),
            "framework minimum version is declared to BepInEx");
        var awake = pluginType.Methods.Single(m => m.Name == "Awake");
        var calls = awake.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToArray();
        int prefabs = Array.FindIndex(calls, m => m.DeclaringType.Name == "PrefabFactory" && m.Name == "RegisterAll");
        int menu = Array.FindIndex(calls, m => m.DeclaringType.Name == "VerticalConstructionEntries" && m.Name == "Register");
        Check(prefabs >= 0 && menu > prefabs, "menu registration follows prefab initialization");
        Check(!pluginType.Methods.Any(m => m.Name is "OnDestroy" or "OnDisable"),
            "bootstrap host destruction does not unregister gameplay/menu integration");

        Type entriesType = vertical.GetType("SilverpineVerticalLayers.VerticalConstructionEntries", true)!;
        MethodInfo register = entriesType.GetMethod("Register", Flags)!;
        IDictionary entries = (IDictionary)typeof(ConstructionMenu).GetField("Entries", Flags)!.GetValue(null)!;
        register.Invoke(null, null);
        const string owner = "renegadex.silverpine.verticallayers";
        string category = owner + ".construction";
        Check(ConstructionMenu.TryGetCategory(category, out var categoryHandle) &&
            categoryHandle.OwnerId == owner && categoryHandle.Label == "Vertical Layers",
            "owned Vertical Layers category is registered");
        Check(entries.Count == 2, "exactly two vertical construction entries are registered");
        var stairs = Read(entries, owner + ".stairs");
        var support = Read(entries, owner + ".support");
        Check(stairs.Label == "Floor Transition Stairs" && support.Label == "Structural Support",
            "searchable stairs/support names are retained");
        Check(stairs.Category == category && support.Category == category,
            "both entries belong to the shared Vertical Layers category");
        Check(stairs.Order < support.Order && support.Order < 0, "vertical entries precede native entries in All");
        Check(stairs.PrefabName == "svl_stairs_wood" && support.PrefabName == "svl_support_pillar",
            "existing prefab/save identifiers are unchanged");
        Check(stairs.Mode == ConstructionMode.ConstructionSite && support.Mode == ConstructionMode.ConstructionSite,
            "both entries advertise construction sites");
        Check(stairs.StartPlacement?.Method.Name == "StartStairsPlacement" &&
              support.StartPlacement?.Method.Name == "StartSupportPlacement",
            "custom paired-floor placement callbacks are retained");
        Check(stairs.RequiresHammer && support.RequiresHammer && !stairs.PlayerPropertyOnly && !support.PlayerPropertyOnly,
            "hammer metadata and custom placement ownership policy are retained");
        Check(stairs.WorkMinutes == 35 && support.WorkMinutes == 35,
            "displayed duration matches 10 setup plus 60 work turns");
        Check(stairs.Materials.Count == 2 && stairs.Materials.Any(m => m.ItemName == "Plank" && m.Amount == 8) &&
            stairs.Materials.Any(m => m.ItemName == "Nails" && m.Amount == 4), "stairs retain Plank x8 and Nails x4");
        Check(support.Materials.Count == 1 && support.Materials[0].ItemName == "Rock" && support.Materials[0].Amount == 4,
            "supports retain Rock x4");
        register.Invoke(null, null);
        Check(entries.Count == 2 && ConstructionMenu.TryGetCategory(category, out _),
            "repeated registration replaces owned entries without duplication");

        var definition = plugin.MainModule.Types.Single(t => t.Name == "VerticalConstructionEntries");
        var setup = definition.Methods.Single(m => m.Name == "SetUpConstructionSite");
        Check(setup.Body.Instructions.Any(i => i.Operand is MethodReference m &&
            m.DeclaringType.Name == "SaltFreeBuildCompatibility" && m.Name == "get_IsEnabled"),
            "construction callbacks still consult Salt Free Build");
        Console.WriteLine($"{passed} compatibility checks passed. Live menu rendering and placement still require an in-game check.");
    }

    private static ConstructionDefinition Read(IDictionary entries, string id)
    {
        object entry = entries[id] ?? throw new Exception("Missing construction " + id);
        Check((string)entry.GetType().GetField("OwnerId", Flags)!.GetValue(entry)! ==
              "renegadex.silverpine.verticallayers", "entry owner: " + id);
        return (ConstructionDefinition)entry.GetType().GetField("Definition", Flags)!.GetValue(entry)!;
    }
}
