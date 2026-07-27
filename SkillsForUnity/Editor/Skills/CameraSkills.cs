using UnityEngine;
using UnityEditor;
using System.Linq;
using System.Collections.Generic;
using UnitySkills.Internal;

namespace UnitySkills.Internal
{
    /// <summary>
    /// Shared PNG downscale + base64 encoding for screenshot skills that support
    /// <c>returnImage=true</c> (camera_screenshot, camera_sceneview_screenshot, scene_screenshot) —
    /// lets AI clients without filesystem access (e.g. remote/MCP) consume pixel data directly
    /// from the REST response instead of reading the file saved at <c>path</c>.
    /// </summary>
    internal static class ScreenshotImageEncoder
    {
        internal const int MinMaxDimension = 256;
        internal const int MaxMaxDimension = 4096;

        // Encoded PNG->base64 grows ~1.33x; this cap keeps responses well clear of
        // SkillsHttpServer's own 10MB request-body ceiling (MaxBodySizeBytes).
        internal const int MaxBase64Bytes = 8 * 1024 * 1024;

        internal static int ClampMaxDimension(int maxDimension) =>
            Mathf.Clamp(maxDimension, MinMaxDimension, MaxMaxDimension);

        /// <summary>
        /// Base64-encodes an already-captured PNG, downscaling first if either dimension exceeds
        /// <paramref name="maxDimension"/>. Reuses <paramref name="pngBytes"/> unchanged when no
        /// resize is needed (no decode/re-encode round trip).
        /// Returns the fields to merge into the caller's response (imageBase64/imageWidth/
        /// imageHeight/imageBytes) on success. On failure returns null and sets
        /// <paramref name="error"/> to a response-shaped error object; the caller's file save is
        /// unaffected — only the returnImage payload failed.
        /// </summary>
        internal static Dictionary<string, object> Encode(byte[] pngBytes, int width, int height, int maxDimension, out object error)
        {
            error = null;
            if (pngBytes == null || pngBytes.Length == 0 || width <= 0 || height <= 0)
            {
                error = new { error = "No captured pixels available to encode for returnImage." };
                return null;
            }

            var clamp = ClampMaxDimension(maxDimension);
            var outBytes = pngBytes;
            int outW = width, outH = height;

            if (width > clamp || height > clamp)
            {
                Texture2D src = null, scaled = null;
                try
                {
                    src = new Texture2D(2, 2);
                    src.LoadImage(pngBytes); // resizes src to the PNG's own dimensions

                    float scale = (float)clamp / Mathf.Max(src.width, src.height);
                    int dstW = Mathf.Max(1, Mathf.RoundToInt(src.width * scale));
                    int dstH = Mathf.Max(1, Mathf.RoundToInt(src.height * scale));

                    scaled = Downscale(src, dstW, dstH);
                    outBytes = scaled.EncodeToPNG();
                    outW = scaled.width;
                    outH = scaled.height;
                }
                finally
                {
                    if (src != null) Object.DestroyImmediate(src);
                    if (scaled != null) Object.DestroyImmediate(scaled);
                }
            }

            var base64 = System.Convert.ToBase64String(outBytes);
            if (base64.Length > MaxBase64Bytes)
            {
                error = new
                {
                    error = $"Encoded image too large ({base64.Length} base64 bytes > {MaxBase64Bytes}). The screenshot file was already saved to disk; retry returnImage with a smaller maxDimension, or omit returnImage and read the file instead.",
                    errorCode = "IMAGE_TOO_LARGE"
                };
                return null;
            }

            return new Dictionary<string, object>
            {
                ["imageBase64"] = base64,
                ["imageWidth"] = outW,
                ["imageHeight"] = outH,
                ["imageBytes"] = outBytes.Length
            };
        }

        private static Texture2D Downscale(Texture2D src, int dstW, int dstH)
        {
            var rt = RenderTexture.GetTemporary(dstW, dstH, 0, RenderTextureFormat.ARGB32);
            var prevActive = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var dst = new Texture2D(dstW, dstH, TextureFormat.RGB24, false);
                dst.ReadPixels(new Rect(0, 0, dstW, dstH), 0, 0);
                dst.Apply();
                return dst;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}

namespace UnitySkills
{
    /// <summary>
    /// Camera skills - Control Scene View and Game cameras.
    /// </summary>
    public static class CameraSkills
    {
        [UnitySkill("camera_align_view_to_object", "Align Scene View camera to look at an object.",
            Category = SkillCategory.Camera, Operation = SkillOperation.Execute,
            Tags = new[] { "scene-view", "align", "look-at", "focus" },
            Outputs = new[] { "message" },
            RequiresInput = new[] { "gameObject" })]
        public static object CameraAlignViewToObject(string name = null, int instanceId = 0, string path = null)
        {
            var (go, findErr) = GameObjectFinder.FindOrError(name: name, instanceId: instanceId, path: path);
            if (findErr != null) return findErr;

            if (SceneView.lastActiveSceneView != null)
            {
                SceneView.lastActiveSceneView.AlignViewToObject(go.transform);
                return new { success = true, message = $"Aligned view to {go.name}" };
            }
            
            return new { error = "No active Scene View found" };
        }

        [UnitySkill("camera_get_info", "Get Scene View camera position and rotation.",
            Category = SkillCategory.Camera, Operation = SkillOperation.Query,
            Tags = new[] { "scene-view", "position", "rotation", "info" },
            Outputs = new[] { "position", "rotation", "pivot", "size", "orthographic" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object CameraGetInfo()
        {
            if (SceneView.lastActiveSceneView != null)
            {
                var cam = SceneView.lastActiveSceneView.camera;
                return new
                {
                    position = new { x = cam.transform.position.x, y = cam.transform.position.y, z = cam.transform.position.z },
                    rotation = new { x = cam.transform.eulerAngles.x, y = cam.transform.eulerAngles.y, z = cam.transform.eulerAngles.z },
                    pivot = new { x = SceneView.lastActiveSceneView.pivot.x, y = SceneView.lastActiveSceneView.pivot.y, z = SceneView.lastActiveSceneView.pivot.z },
                    size = SceneView.lastActiveSceneView.size,
                    orthographic = SceneView.lastActiveSceneView.orthographic
                };
            }
            return new { error = "No active Scene View found" };
        }

        [UnitySkill("camera_set_transform", "Set Scene View camera position/rotation manually.",
            Category = SkillCategory.Camera, Operation = SkillOperation.Modify,
            Tags = new[] { "scene-view", "position", "rotation", "transform" },
            Outputs = new[] { "message" })]
        public static object CameraSetTransform(
            float posX, float posY, float posZ,
            float rotX, float rotY, float rotZ,
            float size = 5f,
            bool instant = true
        )
        {
            if (SceneView.lastActiveSceneView != null)
            {
                var sceneView = SceneView.lastActiveSceneView;
                var position = new Vector3(posX, posY, posZ);
                var rotation = Quaternion.Euler(rotX, rotY, rotZ);
                
                sceneView.LookAt(position, rotation, size);
                
                return new { success = true, message = "Scene View camera updated" };
            }
            return new { error = "No active Scene View found" };
        }
        
        [UnitySkill("camera_look_at", "Focus Scene View camera on a world-space point (x/y/z only, not object name).",
            Category = SkillCategory.Camera, Operation = SkillOperation.Execute,
            Tags = new[] { "scene-view", "look-at", "focus", "navigate" },
            Outputs = new[] { "success" })]
        public static object CameraLookAt(float x, float y, float z)
        {
             if (SceneView.lastActiveSceneView != null)
            {
                var sceneView = SceneView.lastActiveSceneView;
                sceneView.LookAt(new Vector3(x, y, z), sceneView.rotation, sceneView.size);
                return new { success = true };
            }
            return new { error = "No active Scene View found" };
        }

        [UnitySkill("camera_create", "Create a new Game Camera",
            Category = SkillCategory.Camera, Operation = SkillOperation.Create,
            Tags = new[] { "camera", "game-camera", "create", "audio-listener" },
            Outputs = new[] { "name", "instanceId" },
            TracksWorkflow = true)]
        public static object CameraCreate(string name = "New Camera", float x = 0, float y = 1, float z = -10, bool addAudioListener = false)
        {
            var go = new GameObject(name);
            var cam = go.AddComponent<Camera>();
            if (addAudioListener) go.AddComponent<AudioListener>();
            go.transform.position = new Vector3(x, y, z);
            Undo.RegisterCreatedObjectUndo(go, "Create Camera");
            WorkflowManager.SnapshotObject(go, SnapshotType.Created);
            return new { success = true, name = go.name, entityId = UnityObjectIdUtility.GetEntityId(go), instanceId = UnityObjectIdUtility.GetObjectId(go) };
        }

        [UnitySkill("camera_get_properties", "Get Game Camera properties (supports name/instanceId/path)",
            Category = SkillCategory.Camera, Operation = SkillOperation.Query,
            Tags = new[] { "camera", "properties", "fov", "clip-plane" },
            Outputs = new[] { "name", "fieldOfView", "nearClipPlane", "farClipPlane", "orthographic", "depth", "clearFlags" },
            RequiresInput = new[] { "gameObject" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object CameraGetProperties(string name = null, int instanceId = 0, string path = null)
        {
            var (cam, err) = GameObjectFinder.FindComponentOrError<Camera>(name, instanceId, path);
            if (err != null) return err;
            return new
            {
                success = true, name = cam.gameObject.name,
                fieldOfView = cam.fieldOfView, nearClipPlane = cam.nearClipPlane, farClipPlane = cam.farClipPlane,
                orthographic = cam.orthographic, orthographicSize = cam.orthographicSize,
                depth = cam.depth, cullingMask = cam.cullingMask,
                clearFlags = cam.clearFlags.ToString(),
                backgroundColor = new { r = cam.backgroundColor.r, g = cam.backgroundColor.g, b = cam.backgroundColor.b, a = cam.backgroundColor.a },
                rect = new { x = cam.rect.x, y = cam.rect.y, w = cam.rect.width, h = cam.rect.height }
            };
        }

        [UnitySkill("camera_set_properties", "Set Game Camera properties (FOV, clip planes, clear flags, background color, depth)",
            Category = SkillCategory.Camera, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "properties", "fov", "background" },
            Outputs = new[] { "name" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
        public static object CameraSetProperties(
            string name = null, int instanceId = 0, string path = null,
            float? fieldOfView = null, float? nearClipPlane = null, float? farClipPlane = null,
            float? depth = null, string clearFlags = null,
            float? bgR = null, float? bgG = null, float? bgB = null)
        {
            var (cam, err) = GameObjectFinder.FindComponentOrError<Camera>(name, instanceId, path);
            if (err != null) return err;
            WorkflowManager.SnapshotObject(cam);
            Undo.RecordObject(cam, "Set Camera Properties");
            if (fieldOfView.HasValue) cam.fieldOfView = fieldOfView.Value;
            if (nearClipPlane.HasValue) cam.nearClipPlane = nearClipPlane.Value;
            if (farClipPlane.HasValue) cam.farClipPlane = farClipPlane.Value;
            if (depth.HasValue) cam.depth = depth.Value;
            if (!string.IsNullOrEmpty(clearFlags) && System.Enum.TryParse<CameraClearFlags>(clearFlags, true, out var cf)) cam.clearFlags = cf;
            if (bgR.HasValue || bgG.HasValue || bgB.HasValue)
            {
                var c = cam.backgroundColor;
                cam.backgroundColor = new Color(bgR ?? c.r, bgG ?? c.g, bgB ?? c.b, c.a);
            }
            return new { success = true, name = cam.gameObject.name };
        }

        [UnitySkill("camera_set_culling_mask", "Set Game Camera culling mask by layer names (comma-separated)",
            Category = SkillCategory.Camera, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "culling-mask", "layer", "visibility" },
            Outputs = new[] { "cullingMask" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
        public static object CameraSetCullingMask(string layerNames, string name = null, int instanceId = 0, string path = null)
        {
            if (Validate.Required(layerNames, "layerNames") is object layerNamesErr) return layerNamesErr;
            var (cam, err) = GameObjectFinder.FindComponentOrError<Camera>(name, instanceId, path);
            if (err != null) return err;
            WorkflowManager.SnapshotObject(cam);
            Undo.RecordObject(cam, "Set Culling Mask");
            int mask = 0;
            foreach (var ln in layerNames.Split(','))
            {
                var layer = LayerMask.NameToLayer(ln.Trim());
                if (layer >= 0) mask |= 1 << layer;
            }
            cam.cullingMask = mask;
            return new { success = true, cullingMask = mask };
        }

        [UnitySkill("camera_screenshot", "Capture a screenshot from a Game Camera to file. Set returnImage=true to also get the PNG as base64 in the response, for clients without filesystem access (e.g. remote/MCP).",
            Category = SkillCategory.Camera, Operation = SkillOperation.Execute,
            Tags = new[] { "screenshot", "capture", "render", "png" },
            Outputs = new[] { "path", "width", "height", "imageBase64", "imageWidth", "imageHeight", "imageBytes" },
            RequiresInput = new[] { "gameObject" })]
        public static object CameraScreenshot(string savePath = "Assets/screenshot.png", int width = 1920, int height = 1080, string name = null, int instanceId = 0, string path = null, bool returnImage = false, int maxDimension = 1280)
        {
            var (cam, err) = GameObjectFinder.FindComponentOrError<Camera>(name, instanceId, path);
            if (err != null) return err;
            if (Validate.SafePath(savePath, "savePath") is object pathErr) return pathErr;
            if (!savePath.EndsWith(".png")) savePath += ".png";
            var dir = System.IO.Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            var rt = new RenderTexture(width, height, 24);
            Texture2D tex = null;
            RenderTexture oldTarget = cam.targetTexture;
            byte[] pngBytes = null;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                pngBytes = tex.EncodeToPNG();
                System.IO.File.WriteAllBytes(savePath, pngBytes);
            }
            finally
            {
                cam.targetTexture = oldTarget;
                RenderTexture.active = null;
                if (rt != null) Object.DestroyImmediate(rt);
                if (tex != null) Object.DestroyImmediate(tex);
            }
            AssetDatabase.ImportAsset(savePath);

            var result = new Dictionary<string, object> { ["success"] = true, ["path"] = savePath, ["width"] = width, ["height"] = height };
            if (returnImage)
            {
                var imageFields = ScreenshotImageEncoder.Encode(pngBytes, width, height, maxDimension, out var imageError);
                if (imageError != null) return imageError;
                foreach (var kv in imageFields) result[kv.Key] = kv.Value;
            }
            return result;
        }

        [UnitySkill("camera_sceneview_screenshot", "Capture the editor SCENE VIEW (the developer's editing viewport — can overlook the whole scene incl. off-camera objects; distinct from scene_screenshot which is the Game View/player camera, and camera_screenshot which is one Game Camera). By default captures the full Scene View incl. grid/gizmos/selection (on-screen read); auto-falls back to a clean offscreen render if the editor build doesn't support it. filename is a bare filename only (no path separators); saved under Assets/Screenshots/. Set returnImage=true to also get the PNG as base64 in the response, for clients without filesystem access (e.g. remote/MCP).",
            Category = SkillCategory.Camera, Operation = SkillOperation.Execute,
            Tags = new[] { "screenshot", "capture", "scene-view", "editor", "gizmo" },
            Outputs = new[] { "path", "width", "height", "mode", "note", "imageBase64", "imageWidth", "imageHeight", "imageBytes" })]
        public static object CameraSceneViewScreenshot(string filename = "sceneview.png", bool includeOverlays = true, bool returnImage = false, int maxDimension = 1280)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null)
                return new { error = "No active Scene View found. Open a Scene View window (Window > General > Scene)." };

            // Resolve path the same safe way as scene_screenshot: strip any path components,
            // force .png, save under Assets/Screenshots/.
            filename = System.IO.Path.GetFileName(filename);
            if (string.IsNullOrEmpty(filename)) filename = "sceneview";
            if (!System.IO.Path.HasExtension(filename)) filename += ".png";
            var path = System.IO.Path.Combine(Application.dataPath, "Screenshots", filename);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

            string mode = null;
            string note = null;
            int outW = 0, outH = 0;

            // Method 2 (default): full Scene View incl. grid/gizmos via internal ReadScreenPixel (reflection).
            if (includeOverlays)
            {
                var (ok, w, h, err) = TryCaptureSceneViewScreen(sv, path);
                if (ok)
                {
                    mode = "screen_with_overlays";
                    outW = w; outH = h;
                    note = "Full editor Scene View (grid, gizmos, selection highlight). Reads the on-screen window, so the Scene View must be visible and unobscured.";
                }
                else
                {
                    note = $"Overlay capture unavailable ({err}); fell back to a clean offscreen render.";
                }
            }

            // Method 1 (fallback / includeOverlays=false): clean offscreen render from the Scene View camera.
            if (mode == null)
            {
                var (w, h) = CaptureSceneViewCameraOffscreen(sv, path);
                mode = "offscreen_clean";
                outW = w; outH = h;
                if (note == null)
                    note = "Clean scene render from the Scene View camera angle (no grid/gizmos).";
            }

            // The PNG is written synchronously above; refresh the AssetDatabase next tick so it shows in the Project window.
            EditorApplication.delayCall += () => AssetDatabase.Refresh();

            var result = new Dictionary<string, object> { ["success"] = true, ["path"] = path, ["width"] = outW, ["height"] = outH, ["mode"] = mode, ["note"] = note };
            if (returnImage)
            {
                byte[] pngBytes;
                try { pngBytes = System.IO.File.ReadAllBytes(path); }
                catch (System.Exception e) { return new { error = $"Scene View screenshot was saved but could not be read back for returnImage: {e.Message}" }; }

                var imageFields = ScreenshotImageEncoder.Encode(pngBytes, outW, outH, maxDimension, out var imageError);
                if (imageError != null) return imageError;
                foreach (var kv in imageFields) result[kv.Key] = kv.Value;
            }
            return result;
        }

        // Method 2: read the actual on-screen pixels of the Scene View window (incl. grid/gizmos/selection).
        // Uses internal UnityEditorInternal.InternalEditorUtility.ReadScreenPixel via reflection so the assembly
        // still compiles on Unity versions where the internal API is absent (graceful runtime fallback).
        private static (bool ok, int width, int height, string error) TryCaptureSceneViewScreen(SceneView sv, string path)
        {
            Texture2D tex = null;
            try
            {
                var ieuType = System.Type.GetType("UnityEditorInternal.InternalEditorUtility, UnityEditor");
                var method = ieuType?.GetMethod("ReadScreenPixel",
                    new[] { typeof(Vector2), typeof(int), typeof(int) });
                if (method == null)
                    return (false, 0, 0, "ReadScreenPixel not available in this Unity version");

                // ReadScreenPixel expects logical (point) coordinates, not physical pixels —
                // do NOT scale the window rect by EditorGUIUtility.pixelsPerPoint.
                var ew = (EditorWindow)sv;
                var pos = ew.position;
                int w = (int)pos.width;
                int h = (int)pos.height;
                if (w <= 0 || h <= 0)
                    return (false, 0, 0, "invalid window size");

                var pixels = method.Invoke(null, new object[] { new Vector2(pos.x, pos.y), w, h }) as Color[];
                if (pixels == null || pixels.Length < w * h)
                    return (false, 0, 0, "empty or short pixel buffer (window may be hidden)");

                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.SetPixels(pixels);
                tex.Apply();
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                return (true, w, h, null);
            }
            catch (System.Exception e)
            {
                return (false, 0, 0, e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                if (tex != null) Object.DestroyImmediate(tex);
            }
        }

        // Method 1: render the Scene View camera into an offscreen RenderTexture (clean, no editor overlays).
        // Mirrors the camera_screenshot offscreen pattern.
        private static (int width, int height) CaptureSceneViewCameraOffscreen(SceneView sv, string path)
        {
            var cam = sv.camera;
            // The camera's pixel dimensions are the true viewport size (excludes the toolbar),
            // so the offscreen render keeps the correct aspect ratio.
            int w = Mathf.Max(1, cam.pixelWidth);
            int h = Mathf.Max(1, cam.pixelHeight);

            var rt = new RenderTexture(w, h, 24);
            Texture2D tex = null;
            RenderTexture oldTarget = cam.targetTexture;
            RenderTexture oldActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                cam.targetTexture = oldTarget;
                RenderTexture.active = oldActive;
                if (rt != null) Object.DestroyImmediate(rt);
                if (tex != null) Object.DestroyImmediate(tex);
            }
            return (w, h);
        }

        [UnitySkill("camera_set_orthographic", "Switch Game Camera between orthographic and perspective mode",
            Category = SkillCategory.Camera, Operation = SkillOperation.Modify,
            Tags = new[] { "camera", "orthographic", "perspective", "projection" },
            Outputs = new[] { "orthographic", "orthographicSize" },
            RequiresInput = new[] { "gameObject" },
            TracksWorkflow = true)]
        public static object CameraSetOrthographic(bool orthographic, float? orthographicSize = null, string name = null, int instanceId = 0, string path = null)
        {
            var (cam, err) = GameObjectFinder.FindComponentOrError<Camera>(name, instanceId, path);
            if (err != null) return err;
            WorkflowManager.SnapshotObject(cam);
            Undo.RecordObject(cam, "Set Orthographic");
            cam.orthographic = orthographic;
            if (orthographicSize.HasValue) cam.orthographicSize = orthographicSize.Value;
            return new { success = true, orthographic, orthographicSize = cam.orthographicSize };
        }

        [UnitySkill("camera_list", "List all cameras in the scene",
            Category = SkillCategory.Camera, Operation = SkillOperation.Query,
            Tags = new[] { "camera", "list", "scene", "enumerate" },
            Outputs = new[] { "count", "cameras" },
            ReadOnly = true,
            Mode = SkillMode.SemiAuto)]
        public static object CameraList()
        {
            var cameras = FindHelper.FindAll<Camera>();
            var list = cameras.Select(c => new
            {
                name = c.gameObject.name,
                entityId = UnityObjectIdUtility.GetEntityId(c.gameObject),
                instanceId = UnityObjectIdUtility.GetObjectId(c.gameObject),
                path = GameObjectFinder.GetPath(c.gameObject),
                depth = c.depth, orthographic = c.orthographic, enabled = c.enabled
            }).OrderBy(c => c.depth).ToArray();
            return new { count = list.Length, cameras = list };
        }
    }
}

// Producer:Betsy
