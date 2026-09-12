using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Homestead;

internal sealed class HomesteadIconRenderer
{
    internal static readonly HomesteadIconRenderer Instance = new();
    internal static readonly Quaternion IsometricRotation = Quaternion.Euler(23f, 51f, 25.8f);
    internal sealed class RenderRequest
    {
        internal readonly GameObject Target;
        internal int Width = 256, Height = 256;
        internal Quaternion Rotation = IsometricRotation;
        internal RenderRequest(GameObject target) => Target = target;
    }
    private readonly Queue<(RenderRequest request, Action<Sprite?> callback)> _queue = new();
    private Coroutine? _worker;

    internal bool EnqueueRender(RenderRequest request, Action<Sprite?> callback)
    {
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return false;
        _queue.Enqueue((request, callback));
        _worker ??= HomesteadPlugin.Instance.StartCoroutine(Process());
        return true;
    }

    private IEnumerator Process()
    {
        // Yield before assigning _worker, including the one-item case.
        yield return null;
        while (_queue.Count > 0)
        {
            var item = _queue.Dequeue();
            Sprite? icon = null;
            try { icon = Render(item.request); }
            catch (Exception ex) { HomesteadPlugin.HomesteadLogger.LogWarning($"Icon render failed: {ex.Message}"); }
            try { item.callback(icon); }
            catch (Exception ex) { HomesteadPlugin.HomesteadLogger.LogWarning($"Icon completion failed: {ex.Message}"); }
            yield return null;
        }
        _worker = null;
    }

    internal void Shutdown()
    {
        if (_worker != null && HomesteadPlugin.Instance) HomesteadPlugin.Instance.StopCoroutine(_worker);
        _worker = null;
        while (_queue.Count > 0)
        {
            try { _queue.Dequeue().callback(null); }
            catch (Exception ex) { HomesteadPlugin.HomesteadLogger.LogWarning(ex.Message); }
        }
    }

    internal Sprite? Render(RenderRequest request)
    {
        if (!request.Target || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return null;
        GameObject root = request.Target;
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return null;
        Transform[] children = root.GetComponentsInChildren<Transform>(true);
        int[] layers = new int[children.Length];
        Vector3 position = root.transform.position;
        bool active = root.activeSelf;
        GameObject? rig = null;
        RenderTexture? target = null;
        RenderTexture previous = RenderTexture.active;
        Texture2D? texture = null;
        bool fog = RenderSettings.fog;
        Color ambient = RenderSettings.ambientLight;
        UnityEngine.Rendering.AmbientMode ambientMode = RenderSettings.ambientMode;
        try
        {
            for (int i = 0; i < children.Length; i++) { layers[i] = children[i].gameObject.layer; children[i].gameObject.layer = 30; }
            root.transform.position = new Vector3(100000f, 100000f, 100000f);
            root.SetActive(true);
            Bounds bounds = renderers[0].bounds;
            foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
            rig = new GameObject("HomesteadIconCamera", typeof(Camera));
            Camera camera = rig.GetComponent<Camera>();
            camera.enabled = false;
            // Valheim's piece shaders lose their alpha with an orthographic camera.
            // Match the former Jotunn capture with a narrow perspective instead.
            camera.fieldOfView = 0.5f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.cullingMask = 1 << 30;
            camera.transform.rotation = Quaternion.Inverse(request.Rotation) * Quaternion.Euler(0f, 180f, 0f);
            Quaternion inverse = Quaternion.Inverse(camera.transform.rotation);
            Vector3 extents = bounds.extents;
            float halfWidth = 0f, halfHeight = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = inverse * Vector3.Scale(extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                halfWidth = Mathf.Max(halfWidth, Mathf.Abs(corner.x));
                halfHeight = Mathf.Max(halfHeight, Mathf.Abs(corner.y));
            }
            camera.aspect = (float)request.Width / request.Height;
            float halfSize = Mathf.Max(0.1f, Mathf.Max(halfHeight, halfWidth / camera.aspect) * 1.08f);
            float radius = Mathf.Max(1f, extents.magnitude);
            float distance = halfSize / Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) + radius;
            camera.transform.position = bounds.center - camera.transform.forward * distance;
            camera.nearClipPlane = Mathf.Max(0.1f, distance - radius - 1f);
            camera.farClipPlane = distance + radius + 1f;
            Light light = rig.AddComponent<Light>();
            light.type = LightType.Directional;
            light.cullingMask = 1 << 30;
            light.intensity = 1.1f;
            light.shadows = LightShadows.None;
            RenderSettings.fog = false;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.65f, 0.65f, 0.65f);
            target = RenderTexture.GetTemporary(request.Width, request.Height, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            texture = new Texture2D(request.Width, request.Height, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, request.Width, request.Height), 0, 0);
            texture.Apply();
            if (!ZoneBlueprintVisuals.HasVisiblePixels(texture)) return null;
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, request.Width, request.Height), new Vector2(0.5f, 0.5f));
            texture = null; // sprite/cache owns the texture from here
            return sprite;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderSettings.fog = fog;
            RenderSettings.ambientLight = ambient;
            RenderSettings.ambientMode = ambientMode;
            if (rig) { rig.GetComponent<Camera>().targetTexture = null; rig.SetActive(false); Object.Destroy(rig); }
            if (target) RenderTexture.ReleaseTemporary(target);
            if (texture) Object.Destroy(texture);
            if (root)
            {
                root.SetActive(active);
                root.transform.position = position;
                for (int i = 0; i < children.Length; i++) if (children[i]) children[i].gameObject.layer = layers[i];
            }
        }
    }
}
