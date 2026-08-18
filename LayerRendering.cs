#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SilverpineVerticalLayers;

public sealed class LowerLayerRenderer : MonoBehaviour
{
    private readonly List<Camera> captureCameras = new();
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
        UpdateDisplayRegistration(level);
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
        }
        while (captureCameras.Count > lowerLayerCount)
        {
            Camera camera = captureCameras[^1];
            captureCameras.RemoveAt(captureCameras.Count - 1);
            Destroy(camera.gameObject);
        }

        EnsureDisplay();
        if (displayMaterial != null)
            displayMaterial.mainTexture = composite;
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
            displayCamera.depth = mainCamera.depth - 10f;
            float aspect = Mathf.Max(0.01f, mainCamera.aspect);
            displayQuad.transform.localScale = new Vector3(2f * aspect, 2f, 1f);
            displayCamera.gameObject.SetActive(true);
        }
    }

    private void RenderLowerLayers(int currentLevel)
    {
        if (mainCamera == null || composite == null)
            return;

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = composite;
        GL.Clear(true, true, Plugin.VoidColor.Value);
        RenderTexture.active = previous;

        List<(SpriteRenderer renderer, bool enabled)> visibility =
            PrepareLowerVisibility(currentLevel);
        try
        {
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
                capture.Render();
            }
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError("Lower-layer rendering failed: " + exception);
        }
        finally
        {
            foreach ((SpriteRenderer renderer, bool enabled) in visibility)
                if (renderer != null)
                    renderer.enabled = enabled;
        }
    }

    private static List<(SpriteRenderer, bool)> PrepareLowerVisibility(
        int currentLevel)
    {
        Dictionary<SpriteRenderer, bool> states = new();
        foreach (CullInvisible cull in Resources.FindObjectsOfTypeAll<CullInvisible>())
        {
            if (cull == null || !cull.gameObject.scene.IsValid() ||
                !cull.gameObject.activeInHierarchy)
                continue;
            int level = LayerCoordinates.GetLevel(cull.transform.GetVector2IntPosition());
            if (level >= currentLevel)
                continue;
            SpriteRenderer? renderer = cull.GetComponent<SpriteRenderer>() ??
                                       cull.GetComponentInChildren<SpriteRenderer>();
            if (renderer == null)
                continue;
            SetTemporaryVisibility(states, renderer, true);
        }

        return states.Select(pair => (pair.Key, pair.Value)).ToList();
    }

    private static void SetTemporaryVisibility(
        IDictionary<SpriteRenderer, bool> states,
        SpriteRenderer renderer,
        bool enabled)
    {
        if (!states.ContainsKey(renderer))
            states.Add(renderer, renderer.enabled);
        renderer.enabled = enabled;
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

    private void UpdateDisplayRegistration(int currentLevel)
    {
        if (displayMaterial == null)
            return;
        displayMaterial.mainTextureScale = Vector2.one;
        displayMaterial.mainTextureOffset = Vector2.zero;
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
