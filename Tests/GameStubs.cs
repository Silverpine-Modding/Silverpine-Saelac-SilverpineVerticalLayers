// Minimal in-memory world for algorithm/cost regression tests. These are not
// included in the plugin and cannot validate Unity rendering or lifecycle order.
using UnityEngine;

namespace UnityEngine
{
    public readonly record struct Vector2Int(int x, int y)
    {
        public static Vector2Int up => new(0, 1);
        public static Vector2Int right => new(1, 0);
        public static Vector2Int down => new(0, -1);
        public static Vector2Int left => new(-1, 0);
        public static Vector2Int zero => new(0, 0);
        public static Vector2Int operator +(Vector2Int a, Vector2Int b) => new(a.x + b.x, a.y + b.y);
        public static Vector2Int RoundToInt(Vector3 value) => new((int)MathF.Round(value.x), (int)MathF.Round(value.y));
    }

    public readonly record struct Vector3(float x, float y, float z);

    public class Transform
    {
        public Vector3 position;
        public Vector2Int GetVector2IntPosition() => Vector2Int.RoundToInt(position);
    }

    public class Component
    {
        public GameObject gameObject = null!;
    }

    public class GameObject(string name)
    {
        public string name = name;
        public Transform transform = new();
        private readonly List<Component> components = new();
        public T Add<T>(T component) where T : Component
        {
            component.gameObject = this;
            components.Add(component);
            return component;
        }
        public T? GetComponent<T>() where T : class => components.OfType<T>().FirstOrDefault();
        public T[] GetComponents<T>() => components.OfType<T>().ToArray();
        public void GetComponents<T>(List<T> result)
        {
            result.Clear();
            foreach (Component component in components)
                if (component is T item)
                    result.Add(item);
        }
    }
}

public static class Turfs
{
    private static readonly Dictionary<Vector2Int, List<GameObject>> Objects = new();
    public static int Probes;
    public static int EmptyListAllocations;
    public static void Clear()
    {
        Objects.Clear();
        Probes = EmptyListAllocations = 0;
    }
    public static void Add(GameObject obj, Vector2Int position)
    {
        obj.transform.position = new(position.x, position.y, 0);
        if (!Objects.TryGetValue(position, out var list))
            Objects[position] = list = new();
        list.Add(obj);
    }
    public static void Remove(GameObject obj) => Objects[obj.transform.GetVector2IntPosition()].Remove(obj);
    public static bool TryGetTurfGameObjects(Vector2Int position, out List<GameObject> result)
    {
        Probes++;
        return Objects.TryGetValue(position, out result!);
    }
    public static List<GameObject> GetTurfGameObjects(Vector2Int position)
    {
        if (TryGetTurfGameObjects(position, out var result))
            return result;
        EmptyListAllocations++;
        return new();
    }
    public static List<T> GetTurfComponents<T>(Vector2Int position) =>
        GetTurfGameObjects(position).SelectMany(obj => obj.GetComponents<T>()).ToList();
    public static bool IsValidTurf(Vector2Int position) => Objects.ContainsKey(position);
}

public class Door : Component { }
public enum TileTag { Roofed, Submerge }
public class TileTags : Component
{
    public List<TileTag> tags = new();
    public static bool HasTileTag(Vector2Int position, TileTag tag) =>
        Turfs.TryGetTurfGameObjects(position, out var objects) &&
        objects.Any(obj => obj.GetComponent<TileTags>()?.tags.Contains(tag) == true);
}
public static class TurfCollider
{
    public static bool HasImpassableCollider(Vector2Int position) => false;
}
public class Player : Component
{
    public static Player? Instance;
}

namespace SilverpineVerticalLayers
{
    internal sealed class Setting<T>(T value) { public T Value = value; }
    internal static class Plugin
    {
        internal static Setting<int> MaximumUnsupportedSpan = new(4);
        internal static Setting<int> LayerOriginX = new(20000);
        internal static Setting<int> LayerStrideX = new(512);
        internal static Setting<int> MaxLevels = new(3);
    }
    public class ProjectedUpperFloor : Component { public int SourceLevel; }
    public class VerticalSupport : Component
    {
        public bool IsAnchor;
        public string PairId = "";
        public int AnchorLevel;
    }
    public class VerticalLayerManager
    {
        public static VerticalLayerManager? Instance;
        public bool IsLevelActive(int level) => true;
    }
}
