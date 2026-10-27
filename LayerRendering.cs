#nullable enable

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace SilverpineVerticalLayers;

public sealed class LowerLayerRenderer : MonoBehaviour
{
    private readonly List<Camera> captureCameras = new();
    private readonly List<Plane[]> captureFrusta = new();
    private readonly List<(CullInvisible cull, SpriteRenderer renderer)> visibilityCandidates = new();
    private readonly List<SpriteRenderer> temporarilyEnabled = new();
    private Vector3 candidateCameraPosition;
    private Quaternion candidateCameraRotation;
    private Matrix4x4 candidateProjection;
    private int candidateRegistryRevision = -1;
    private int candidateLevel = -1;
    private float nextCandidateRefreshTime;
    private RenderTexture? composite;
    private Camera? displayCamera;
    private GameObject? displayQuad;
    private Material? displayMaterial;
    private Camera? mainCamera;
    private CameraClearFlags originalClearFlags;
    private Color originalBackgroundColor;
    private bool originalCameraStateCaptured;
    private bool dirty = true;
    private int lastLevel = -1;
    private float nextCaptureTime;
    private float nextAmbientTime;
    private int displayLayer = 31;
    private Vector3 capturedCameraPosition;
    private Vector3 capturedPlayerPosition;
    private float capturedOrthographicSize;
    private float capturedAspect;
    private bool capturedViewState;

    internal void Tick()
    {
        if (Player.Instance == null)
            return;
        Camera? currentMain = Camera.main;
        if (currentMain == null)
            return;
        BindMainCamera(currentMain);

        int level = LayerCoordinates.GetLevel(
            Player.Instance.transform.GetVector2IntPosition());
        if (Plugin.ViewMode.Value == LowerLayerViewMode.Void || level <= 0)
        {
            ApplyVoidOrGround(level);
            lastLevel = level;
            return;
        }

        EnsureLiveResources(level);
        if (lastLevel != level)
            dirty = true;

        float now = Time.unscaledTime;
        if (Plugin.AmbientRefreshFps.Value > 0 && now >= nextAmbientTime)
        {
            dirty = true;
            nextAmbientTime = now + 1f / Plugin.AmbientRefreshFps.Value;
        }
        bool viewMoving = HasViewChanged();
        if ((viewMoving || dirty) &&
            (viewMoving || now >= nextCaptureTime))
        {
            RenderLowerLayers(level);
            RecordViewState();
            dirty = false;
            nextCaptureTime = now + 1f / Plugin.MovementBurstFps.Value;
        }
        lastLevel = level;
    }

    internal new void MarkDirty() => dirty = true;

    internal void MarkFloorMaskDirty()
    {
        dirty = true;
    }

    internal void ApplyModeImmediately()
    {
        dirty = true;
        if (Plugin.ViewMode.Value == LowerLayerViewMode.Void)
            ReleaseLiveResources();
    }

    internal void RecreateTargets()
    {
        ReleaseLiveResources();
        dirty = true;
    }

    private void BindMainCamera(Camera camera)
    {
        if (ReferenceEquals(mainCamera, camera))
            return;
        RestoreMainCamera();
        mainCamera = camera;
        originalClearFlags = camera.clearFlags;
        originalBackgroundColor = camera.backgroundColor;
        originalCameraStateCaptured = true;
        capturedViewState = false;
        dirty = true;
    }

    private void ApplyVoidOrGround(int level)
    {
        ReleaseLiveResources();
        if (mainCamera == null)
            return;
        if (level > 0 && Plugin.ViewMode.Value == LowerLayerViewMode.Void)
        {
            mainCamera.clearFlags = CameraClearFlags.SolidColor;
            mainCamera.backgroundColor = Plugin.VoidColor.Value;
        }
        else
        {
            RestoreMainCameraSettingsOnly();
        }
    }

    private void EnsureLiveResources(int lowerLayerCount)
    {
        if (mainCamera == null)
            return;
        int targetWidth = RoundToEight(Mathf.Max(
            256,
            Mathf.RoundToInt(
                Screen.width * Plugin.LowerResolutionScale.Value)));
        int targetHeight = RoundToEight(Mathf.Max(
            256,
            Mathf.RoundToInt(
                Screen.height * Plugin.LowerResolutionScale.Value)));
        if (composite == null || composite.width != targetWidth ||
            composite.height != targetHeight)
        {
            ReleaseTargetOnly();
            composite = new RenderTexture(
                targetWidth,
                targetHeight,
                24,
                RenderTextureFormat.ARGB32)
            {
                name = "SVL Lower Layer View",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false
            };
            composite.Create();
            dirty = true;
        }

        while (captureCameras.Count < lowerLayerCount)
        {
            GameObject cameraObject = new($"SVL Capture Camera {captureCameras.Count}");
            cameraObject.transform.SetParent(transform, worldPositionStays: false);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            captureCameras.Add(camera);
            captureFrusta.Add(new Plane[6]);
        }
        while (captureCameras.Count > lowerLayerCount)
        {
            Camera camera = captureCameras[^1];
            captureCameras.RemoveAt(captureCameras.Count - 1);
            captureFrusta.RemoveAt(captureFrusta.Count - 1);
            Destroy(camera.gameObject);
        }

        EnsureDisplay();
        if (displayMaterial != null && displayMaterial.mainTexture != composite)
            displayMaterial.mainTexture = composite;
        if (mainCamera.clearFlags != CameraClearFlags.Depth)
            mainCamera.clearFlags = CameraClearFlags.Depth;
    }

    private void EnsureDisplay()
    {
        if (mainCamera == null || composite == null)
            return;
        if (displayCamera == null)
        {
            displayLayer = FindUnusedLayer();
            GameObject cameraObject = new("SVL Composite Display Camera");
            cameraObject.transform.SetParent(transform, false);
            displayCamera = cameraObject.AddComponent<Camera>();
            displayCamera.orthographic = true;
            displayCamera.orthographicSize = 1f;
            displayCamera.nearClipPlane = 0.1f;
            displayCamera.farClipPlane = 20f;
            displayCamera.transform.position = new Vector3(0f, 0f, -10f);
            displayCamera.clearFlags = CameraClearFlags.SolidColor;
            displayCamera.backgroundColor = Plugin.VoidColor.Value;
            displayCamera.cullingMask = 1 << displayLayer;
            displayCamera.depth = mainCamera.depth - 10f;

            displayQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            displayQuad.name = "SVL Lower Layer Display";
            displayQuad.layer = displayLayer;
            displayQuad.transform.SetParent(cameraObject.transform, false);
            displayQuad.transform.localPosition = new Vector3(0f, 0f, 10f);
            Collider? collider = displayQuad.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);
            Shader shader = Shader.Find("Unlit/Texture") ??
                            Shader.Find("Sprites/Default");
            displayMaterial = new Material(shader)
            {
                name = "SVL Lower Layer Display Material",
                mainTexture = composite
            };
            displayQuad.GetComponent<MeshRenderer>().sharedMaterial = displayMaterial;
        }

        if (displayCamera != null && displayQuad != null)
        {
            if (displayCamera.depth != mainCamera.depth - 10f)
                displayCamera.depth = mainCamera.depth - 10f;
            float aspect = Mathf.Max(0.01f, mainCamera.aspect);
            Vector3 scale = new(2f * aspect, 2f, 1f);
            if (displayQuad.transform.localScale != scale)
                displayQuad.transform.localScale = scale;
        }
    }

    private void RenderLowerLayers(int currentLevel)
    {
        if (mainCamera == null || composite == null)
            return;

        Profiler.BeginSample("SVL.RenderLowerLayers");
        try
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = composite;
                GL.Clear(true, true, Plugin.VoidColor.Value);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            Vector2 canonicalCamera = GetCanonicalCameraPosition(currentLevel);
            for (int level = 0; level < currentLevel; level++)
            {
                Camera capture = captureCameras[level];
                capture.CopyFrom(mainCamera);
                capture.enabled = false;
                capture.targetTexture = composite;
                capture.clearFlags = CameraClearFlags.Depth;
                capture.cullingMask &= ~(1 << displayLayer);
                Vector2Int layerOffset = LayerCoordinates.ToPhysical(Vector2Int.zero, level);
                capture.transform.position = new Vector3(
                    canonicalCamera.x + layerOffset.x,
                    canonicalCamera.y,
                    mainCamera.transform.position.z);
                GeometryUtility.CalculateFrustumPlanes(capture, captureFrusta[level]);
            }

            PrepareLowerVisibility(currentLevel);
            for (int level = 0; level < currentLevel; level++)
                captureCameras[level].Render();
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError("Lower-layer rendering failed: " + exception);
        }
        finally
        {
            // Only disabled sprites are changed. This also deduplicates renderers
            // shared by multiple CullInvisible components without a dictionary.
            foreach (SpriteRenderer renderer in temporarilyEnabled)
                if (renderer != null)
                    renderer.enabled = false;
            temporarilyEnabled.Clear();
            Profiler.EndSample();
        }
    }

    private void PrepareLowerVisibility(int currentLevel)
    {
        Profiler.BeginSample("SVL.PrepareLowerVisibility");
        try
        {
            RefreshVisibilityCandidates(currentLevel);
            foreach ((CullInvisible cull, SpriteRenderer renderer) in visibilityCandidates)
            {
                if (cull == null || renderer == null || renderer.enabled ||
                    !renderer.gameObject.activeInHierarchy)
                    continue;
                int level = LayerCoordinates.GetLevel(cull.transform.GetVector2IntPosition());
                if (level >= currentLevel ||
                    !GeometryUtility.TestPlanesAABB(captureFrusta[level], renderer.bounds))
                    continue;
                temporarilyEnabled.Add(renderer);
                renderer.enabled = true;
            }
        }
        finally
        {
            Profiler.EndSample();
        }
    }

    private void RefreshVisibilityCandidates(int currentLevel)
    {
        if (mainCamera == null)
            return;
        Vector3 position = mainCamera.transform.position;
        Quaternion rotation = mainCamera.transform.rotation;
        Matrix4x4 projection = mainCamera.projectionMatrix;
        if (!dirty && candidateLevel == currentLevel &&
            candidateRegistryRevision == LowerLayerCullRegistry.Revision &&
            (position - candidateCameraPosition).sqrMagnitude <= 1f &&
            rotation == candidateCameraRotation && projection == candidateProjection &&
            Time.unscaledTime < nextCandidateRefreshTime)
            return;

        Profiler.BeginSample("SVL.RefreshVisibilityCandidates");
        try
        {
            visibilityCandidates.Clear();
            // CullInvisible already maintains this set across spawns and save loads.
            // Do not search every loaded Unity object on each animation frame.
            foreach (CullInvisible cull in LowerLayerCullRegistry.All)
            {
                if (cull == null || !cull.gameObject.scene.IsValid())
                    continue;
                int level = LayerCoordinates.GetLevel(cull.transform.GetVector2IntPosition());
                if (level >= currentLevel)
                    continue;
                SpriteRenderer renderer = LowerLayerCullRegistry.SpriteRenderer(cull);
                if (renderer == null)
                    continue;
                Bounds bounds = renderer.bounds;
                // Two tiles of padding allow reuse through a one-tile camera/NPC
                // movement. Exact frustum checks still happen on every capture.
                bounds.Expand(4f);
                if (GeometryUtility.TestPlanesAABB(captureFrusta[level], bounds))
                    visibilityCandidates.Add((cull, renderer));
            }
            candidateCameraPosition = position;
            candidateCameraRotation = rotation;
            candidateProjection = projection;
            candidateLevel = currentLevel;
            candidateRegistryRevision = LowerLayerCullRegistry.Revision;
            nextCandidateRefreshTime = Time.unscaledTime + 0.25f;
        }
        finally
        {
            Profiler.EndSample();
        }
    }

    private Vector2 GetCanonicalCameraPosition(int level)
    {
        if (mainCamera == null)
            return Vector2.zero;
        Vector2Int offset =
            LayerCoordinates.ToPhysical(Vector2Int.zero, level);
        return new Vector2(
            mainCamera.transform.position.x - offset.x,
            mainCamera.transform.position.y);
    }

    private bool HasViewChanged()
    {
        if (!capturedViewState || mainCamera == null ||
            Player.Instance == null)
            return true;
        return (mainCamera.transform.position - capturedCameraPosition)
                   .sqrMagnitude > 0.000001f ||
               (Player.Instance.transform.position - capturedPlayerPosition)
                   .sqrMagnitude > 0.000001f ||
               Mathf.Abs(mainCamera.orthographicSize -
                         capturedOrthographicSize) > 0.0001f ||
               Mathf.Abs(mainCamera.aspect - capturedAspect) > 0.0001f;
    }

    private void RecordViewState()
    {
        if (mainCamera == null || Player.Instance == null)
            return;
        capturedCameraPosition = mainCamera.transform.position;
        capturedPlayerPosition = Player.Instance.transform.position;
        capturedOrthographicSize = mainCamera.orthographicSize;
        capturedAspect = mainCamera.aspect;
        capturedViewState = true;
    }

    private void ReleaseLiveResources()
    {
        foreach (Camera camera in captureCameras)
            if (camera != null)
                Destroy(camera.gameObject);
        captureCameras.Clear();
        captureFrusta.Clear();
        visibilityCandidates.Clear();
        candidateLevel = -1;
        if (displayCamera != null)
            Destroy(displayCamera.gameObject);
        displayCamera = null;
        displayQuad = null;
        if (displayMaterial != null)
            Destroy(displayMaterial);
        displayMaterial = null;
        capturedViewState = false;
        ReleaseTargetOnly();
        RestoreMainCameraSettingsOnly();
    }

    private void ReleaseTargetOnly()
    {
        if (composite == null)
            return;
        composite.Release();
        Destroy(composite);
        composite = null;
    }

    private void RestoreMainCameraSettingsOnly()
    {
        if (mainCamera == null || !originalCameraStateCaptured)
            return;
        mainCamera.clearFlags = originalClearFlags;
        mainCamera.backgroundColor = originalBackgroundColor;
    }

    private void RestoreMainCamera()
    {
        RestoreMainCameraSettingsOnly();
        originalCameraStateCaptured = false;
    }

    private void OnDestroy()
    {
        ReleaseLiveResources();
        RestoreMainCamera();
    }

    private static int FindUnusedLayer()
    {
        for (int layer = 31; layer >= 8; layer--)
            if (string.IsNullOrEmpty(LayerMask.LayerToName(layer)))
                return layer;
        return 31;
    }

    private static int RoundToEight(int value) =>
        Mathf.Max(8, ((value + 4) / 8) * 8);
}

// These fields are the game's own initialized registry and cached extended
// sprite renderer (which can be on a child), not a second scene-wide cache.
[HarmonyPatch]
internal static class LowerLayerCullRegistry
{
    internal static readonly HashSet<CullInvisible> All =
        (HashSet<CullInvisible>)AccessTools.Field(typeof(CullInvisible), "all")
            .GetValue(null);
    internal static readonly AccessTools.FieldRef<CullInvisible, SpriteRenderer>
        SpriteRenderer = AccessTools.FieldRefAccess<CullInvisible, SpriteRenderer>(
            "spriteRenderer");
    internal static int Revision { get; private set; }

    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CullInvisible), "Awake");
        yield return AccessTools.Method(typeof(CullInvisible), "OnDestroy");
    }

    private static void Postfix()
    {
        Revision++;
        VerticalLayerManager.Instance?.Renderer.MarkDirty();
    }
}
