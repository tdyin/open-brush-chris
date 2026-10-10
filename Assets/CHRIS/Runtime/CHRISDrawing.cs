// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace TiltBrush
{
    // The fixed drawing frame of one agent drawing run (D95): a square in room space in front of
    // the user, set at the run's first draw lease and kept for the task. Normalized frame
    // coordinates follow image convention: x right, y down, origin at the top left, so a
    // snapshot pixel (u, v) / size is the same frame coordinate.
    //
    // A 3D run (D106) uses a box instead: a BoxMeters cube at arm's length whose near face is
    // 0.40 m from the head. Its coordinates add z, from the near face (0) to the far face (1).
    public struct CHRISDrawFrame
    {
        public const float SizeMeters = 0.6f, DistanceMeters = 0.55f, BelowEyeMeters = 0.1f, DepthMeters = 0.05f;
        public const float BoxMeters = 0.6f, BoxDistanceMeters = 0.70f, BoxBelowEyeMeters = 0.15f;
        public Vector3 Center, Right, Up, Forward;
        public float Size;
        public bool Box;
        public string Id;

        public static CHRISDrawFrame Facing(Vector3 headPosition, Vector3 headForward, string id)
        {
            Vector3 forward = Vector3.ProjectOnPlane(headForward, Vector3.up);
            forward = forward.sqrMagnitude < 1e-4f ? Vector3.forward : forward.normalized;
            float units = App.METERS_TO_UNITS;
            return new CHRISDrawFrame
            {
                Forward = forward, Up = Vector3.up, Right = Vector3.Cross(Vector3.up, forward),
                Center = headPosition + forward * (DistanceMeters * units) - Vector3.up * (BelowEyeMeters * units),
                Size = SizeMeters * units, Id = id,
            };
        }

        public static CHRISDrawFrame Boxed(Vector3 headPosition, Vector3 headForward, string id)
        {
            var frame = Facing(headPosition, headForward, id);
            float units = App.METERS_TO_UNITS;
            frame.Box = true;
            frame.Size = BoxMeters * units;
            frame.Center = headPosition + frame.Forward * (BoxDistanceMeters * units) - Vector3.up * (BoxBelowEyeMeters * units);
            return frame;
        }

        // A box point: x right, y down, z away from the user, each 0..1 across the cube.
        public Vector3 Point3(float x, float y, float z) =>
            Center + Right * ((x - 0.5f) * Size) + Up * ((0.5f - y) * Size) + Forward * ((z - 0.5f) * Size);

        // depth 0..1 maps to DepthMeters behind the plane at 0 and in front of it at 1.
        public Vector3 Point(float x, float y, float depth) =>
            Center + Right * ((x - 0.5f) * Size) + Up * ((0.5f - y) * Size) +
            Forward * ((0.5f - depth) * 2 * DepthMeters * App.METERS_TO_UNITS);

        // The pen points into the frame, upright.
        public Quaternion PenRotation => Quaternion.LookRotation(Forward, Up);
    }

    // Drawing runs by task: the frame, the strokes the run has started and the outline the user
    // sees. A run ends after IdleSeconds without a lease; at most MaxRuns are kept.
    public static class CHRISDrawingRuns
    {
        public const int MaxStrokesPerRun = 60, MaxRuns = 2, SnapshotPixels = 512, SnapshotQuality = 85;
        // A 3D snapshot is one 2 x 2 grid of 512 px tiles: front, side (from the user's right),
        // top (from above) and an informative perspective view.
        public const int GridPixels = 2 * SnapshotPixels;
        public const string GridLayout = "grid2x2";
        public const float IdleSeconds = 600, SnapshotIntervalSeconds = 1;
        // The snapshot camera sits this far in front of the frame (toward the user).
        const float CameraBackUnits = 3f;
        const float OutlineUnits = 0.04f;
        static readonly Color s_Background = new Color(0.125f, 0.125f, 0.125f);
        static readonly Color s_Outline = new Color(0.45f, 0.45f, 0.45f);
        static readonly Color s_TileBorder = new Color(0.35f, 0.35f, 0.35f);

        public sealed class Run
        {
            public string TaskId;
            public CHRISDrawFrame Frame;
            public int StrokesStarted;
            public float LastUse, LastSnapshot = float.NegativeInfinity;
            public GameObject Outline;
        }

        static readonly List<Run> s_Runs = new List<Run>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetForPlay()
        {
            foreach (var run in s_Runs) DestroyOutline(run);
            s_Runs.Clear();
        }

        public static Run Find(string taskId) => s_Runs.FirstOrDefault(r => r.TaskId == taskId);

        // Why a draw lease may not start for this task now, or null. The first lease of a run
        // needs an empty sketch; later ones need no strokes beyond those the run started.
        public static string AcquireRefusal(string taskId, int strokeCount, bool box = false)
        {
            var run = Find(taskId);
            if (run == null) return strokeCount == 0 ? null : "Start a new sketch first";
            if (run.Frame.Box != box) return box ? "This task has a 2D drawing frame" : "This task has a 3D drawing box";
            if (strokeCount > run.StrokesStarted) return "Sketch changed outside this drawing";
            if (run.StrokesStarted >= MaxStrokesPerRun) return "Stroke limit for this drawing reached";
            return null;
        }

        // The run for a granted draw lease; the first one fixes the frame in front of the head.
        public static Run Begin(string taskId, Vector3 headPosition, Vector3 headForward, float now, bool outline = true,
            bool box = false)
        {
            var run = Find(taskId);
            if (run == null)
            {
                while (s_Runs.Count >= MaxRuns) End(s_Runs.OrderBy(r => r.LastUse).First());
                string id = Guid.NewGuid().ToString("N");
                run = new Run { TaskId = taskId, Frame = box ? CHRISDrawFrame.Boxed(headPosition, headForward, id)
                    : CHRISDrawFrame.Facing(headPosition, headForward, id) };
                s_Runs.Add(run);
                if (outline) run.Outline = BuildOutline(run.Frame);
            }
            run.LastUse = now;
            return run;
        }

        public static void LeaseEnded(string taskId, int strokesStarted, float now)
        {
            var run = Find(taskId);
            if (run == null) return;
            run.StrokesStarted += strokesStarted;
            run.LastUse = now;
        }

        public static void Tick(float now, string activeTaskId)
        {
            foreach (var run in s_Runs.Where(r => r.TaskId != activeTaskId && now - r.LastUse > IdleSeconds).ToArray())
                End(run);
        }

        // A revoked or expired draw lease and any gateway Stop end the run: the task cannot
        // lease again, so an interruption is never followed by more strokes.
        public static void EndRun(string taskId)
        {
            var run = Find(taskId);
            if (run != null) End(run);
        }

        public static void EndAll()
        {
            foreach (var run in s_Runs.ToArray()) End(run);
        }

        static void End(Run run)
        {
            DestroyOutline(run);
            s_Runs.Remove(run);
        }

        static void DestroyOutline(Run run)
        {
            if (run.Outline == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(run.Outline);
            else UnityEngine.Object.DestroyImmediate(run.Outline);
            run.Outline = null;
        }

        // A faint square on the panels layer, so the user sees where the agent draws; the
        // snapshot camera never renders it.
        static GameObject BuildOutline(CHRISDrawFrame frame)
        {
            var resources = CHRISUIResources.Load();
            var root = new GameObject("CHRIS drawing frame");
            int layer = LayerMask.NameToLayer("Panels");
            root.layer = layer < 0 ? 0 : layer;
            root.transform.SetPositionAndRotation(frame.Center, frame.PenRotation);
            float half = frame.Size / 2;
            if (frame.Box)
            {
                // The 12 edges of the cube as thin bars, visible from every side.
                foreach (var a in new[] { -half, half })
                foreach (var b in new[] { -half, half })
                {
                    Edge(resources, root.transform, new Vector3(0, a, b), new Vector3(frame.Size, OutlineUnits, OutlineUnits));
                    Edge(resources, root.transform, new Vector3(a, 0, b), new Vector3(OutlineUnits, frame.Size, OutlineUnits));
                    Edge(resources, root.transform, new Vector3(a, b, 0), new Vector3(OutlineUnits, OutlineUnits, frame.Size));
                }
                return root;
            }
            resources.Surface(root.transform, "Top", new Vector3(0, half, 0), new Vector2(frame.Size, OutlineUnits), s_Outline);
            resources.Surface(root.transform, "Bottom", new Vector3(0, -half, 0), new Vector2(frame.Size, OutlineUnits), s_Outline);
            resources.Surface(root.transform, "Left", new Vector3(-half, 0, 0), new Vector2(OutlineUnits, frame.Size), s_Outline);
            resources.Surface(root.transform, "Right", new Vector3(half, 0, 0), new Vector2(OutlineUnits, frame.Size), s_Outline);
            return root;
        }

        static void Edge(CHRISUIResources resources, Transform parent, Vector3 position, Vector3 size)
        {
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = "Edge";
            bar.layer = parent.gameObject.layer;
            UnityEngine.Object.DestroyImmediate(bar.GetComponent<Collider>());
            bar.transform.SetParent(parent, false);
            bar.transform.localPosition = position;
            bar.transform.localScale = size;
            var material = new Material(resources.SurfaceShader);
            material.SetColor("_Color", s_Outline);
            material.SetColor("_SecondaryColor", s_Outline * 0.7f);
            CHRISMaterialOwner.Assign(bar.GetComponent<Renderer>(), material);
        }

        // Strokes only: the canvas layers, never panels, controllers, the user or the room.
        internal static int CanvasMask()
        {
            int mask = 0;
            // App.Scene throws before the app exists (edit-mode checks); layer names still apply.
            var scene = App.Instance != null ? App.Scene : null;
            if (scene != null)
            {
                if (scene.MainCanvas != null) mask |= 1 << scene.MainCanvas.gameObject.layer;
                if (scene.ActiveCanvas != null) mask |= 1 << scene.ActiveCanvas.gameObject.layer;
            }
            foreach (var name in new[] { "MainCanvas", "SelectionCanvas" })
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0) mask |= 1 << layer;
            }
            return mask;
        }

        // Read-only and rate limited; the frame must exist, no lease is needed. Every call logs
        // one line: the size and time, or the exact reason it was refused or failed.
        public static JObject Snapshot(string taskId, float now, int strokeCount, double capturedAt, out string error)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            error = null;
            var run = Find(taskId);
            if (run == null) error = "No drawing frame for this task (no run, or the run ended)";
            else if (now - run.LastSnapshot < SnapshotIntervalSeconds) error = "Snapshot rate limit; retry after 1 s";
            if (error != null)
            {
                Debug.Log($"CHRIS drawing snapshot: task {taskId} refused: {error}");
                return null;
            }
            run.LastSnapshot = now;
            Texture2D image = null;
            bool box = run.Frame.Box;
            int pixels = box ? GridPixels : SnapshotPixels;
            try
            {
                image = box ? RenderGrid(run.Frame, CanvasMask(), SnapshotPixels) : RenderFrame(run.Frame, CanvasMask(), SnapshotPixels);
                byte[] jpeg = image.EncodeToJPG(SnapshotQuality);
                if (jpeg == null || jpeg.Length == 0) throw new InvalidOperationException("JPEG encode returned no data");
                Debug.Log($"CHRIS drawing snapshot: task {taskId} frame {run.Frame.Id} ok; {jpeg.Length} bytes, " +
                    $"{timer.Elapsed.TotalMilliseconds:F0} ms, {strokeCount} strokes" + (box ? ", grid2x2" : ""));
                var reply = new JObject
                {
                    ["format"] = "jpeg", ["width"] = pixels, ["height"] = pixels,
                    ["data"] = Convert.ToBase64String(jpeg),
                    ["frame_id"] = run.Frame.Id, ["stroke_count"] = strokeCount, ["captured_at"] = capturedAt,
                };
                if (box) reply["layout"] = GridLayout;
                return reply;
            }
            catch (Exception exception)
            {
                error = "Snapshot render failed: " + exception.GetType().Name + ": " + exception.Message;
                Debug.LogWarning($"CHRIS drawing snapshot: task {taskId} frame {run.Frame.Id} failed: {exception}");
                return null;
            }
            finally { if (image != null) UnityEngine.Object.DestroyImmediate(image); }
        }

        // An orthographic camera square on the frame, on the user's side, fitted exactly to it.
        // The caller owns the returned texture.
        internal static Texture2D RenderFrame(CHRISDrawFrame frame, int cullingMask, int pixels,
            UnityEngine.SceneManagement.Scene? scene = null) =>
            RenderView(frame.Center - frame.Forward * CameraBackUnits, frame.PenRotation, frame.Size / 2, 0,
                CameraBackUnits + CHRISDrawFrame.DepthMeters * App.METERS_TO_UNITS + 1, cullingMask, pixels, scene);

        // The 3D snapshot: four views of the box in one image. Each orthographic tile is fitted
        // exactly to a box face; (u, v) grows left-right and top-bottom:
        //   top-left FRONT (x, y), top-right SIDE from the user's right (z, y),
        //   bottom-left TOP from above (x, 1 - z), bottom-right PERSPECTIVE (informative only).
        internal static Texture2D RenderGrid(CHRISDrawFrame frame, int cullingMask, int tile,
            UnityEngine.SceneManagement.Scene? scene = null)
        {
            float half = frame.Size / 2, back = CameraBackUnits, far = back + frame.Size + 1;
            Vector3 c = frame.Center, f = frame.Forward, r = frame.Right, up = frame.Up;
            Vector3 box = c;
            // The perspective tile frames what has been drawn (the whole box when nothing has), so
            // the shape fills it; it is informative only, so its framing may change between looks.
            var target = StrokeBounds(frame, cullingMask, scene) ??
                new Bounds(c, Vector3.one * frame.Size);
            float radius = Mathf.Max(target.extents.magnitude, frame.Size * 0.1f);
            Vector3 eye = target.center + (r * 0.8f + up * 0.6f - f * 1.0f).normalized *
                (radius / Mathf.Sin(PerspectiveFov / 2 * Mathf.Deg2Rad) * 1.05f);
            c = target.center;
            var views = new[]
            {
                (RenderView(box - f * (half + back), Quaternion.LookRotation(f, up), half, 0, far, cullingMask, tile, scene), 0, 1),
                (RenderView(box + r * (half + back), Quaternion.LookRotation(-r, up), half, 0, far, cullingMask, tile, scene), 1, 1),
                (RenderView(box + up * (half + back), Quaternion.LookRotation(-up, f), half, 0, far, cullingMask, tile, scene), 0, 0),
                (RenderView(eye, Quaternion.LookRotation(c - eye, Vector3.up), 0, PerspectiveFov,
                    Vector3.Distance(eye, c) + radius * 2, cullingMask, tile, scene), 1, 0),
            };
            var grid = new Texture2D(2 * tile, 2 * tile, TextureFormat.RGB24, false);
            try
            {
                foreach (var (image, column, row) in views)
                    grid.SetPixels32(column * tile, row * tile, tile, tile, image.GetPixels32());
                // Texture rows start at the bottom: row 1 is the top half of the image.
                var border = (Color32)s_TileBorder;
                for (int i = 0; i < 2 * tile; i++)
                {
                    grid.SetPixel(tile - 1, i, border); grid.SetPixel(tile, i, border);
                    grid.SetPixel(i, tile - 1, border); grid.SetPixel(i, tile, border);
                }
                grid.Apply();
                return grid;
            }
            finally { foreach (var (image, _, _) in views) UnityEngine.Object.DestroyImmediate(image); }
        }

        const float PerspectiveFov = 42;

        // World bounds of canvas-layer renderers inside the box (the run's strokes, since the
        // sketch was empty when it began), clipped to the box; null when nothing is drawn.
        static Bounds? StrokeBounds(CHRISDrawFrame frame, int cullingMask, UnityEngine.SceneManagement.Scene? scene)
        {
            var reach = new Bounds(frame.Center, Vector3.one * frame.Size * 1.8f);
            var scenes = scene.HasValue ? new[] { scene.Value } :
                Enumerable.Range(0, UnityEngine.SceneManagement.SceneManager.sceneCount)
                    .Select(UnityEngine.SceneManagement.SceneManager.GetSceneAt).ToArray();
            Bounds? found = null;
            foreach (var loaded in scenes.Where(x => x.isLoaded))
            foreach (var root in loaded.GetRootGameObjects())
            foreach (var renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || (cullingMask & (1 << renderer.gameObject.layer)) == 0) continue;
                var b = renderer.bounds;
                if (!b.Intersects(reach)) continue;
                var min = Vector3.Max(b.min, reach.min);
                var max = Vector3.Min(b.max, reach.max);
                var clipped = new Bounds((min + max) / 2, max - min);
                if (found is Bounds so) { so.Encapsulate(clipped); found = so; }
                else found = clipped;
            }
            return found;
        }

        // One camera render: orthographic when fov is 0, otherwise perspective.
        static Texture2D RenderView(Vector3 position, Quaternion rotation, float orthoSize, float fov, float far,
            int cullingMask, int pixels, UnityEngine.SceneManagement.Scene? scene)
        {
            var obj = new GameObject("CHRIS drawing snapshot camera") { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture target = null;
            var previous = RenderTexture.active;
            Camera camera = null;
            try
            {
                camera = obj.AddComponent<Camera>();
                obj.AddComponent<UniversalAdditionalCameraData>();
                camera.enabled = false;
                if (scene.HasValue) camera.scene = scene.Value;
                obj.transform.SetPositionAndRotation(position, rotation);
                camera.orthographic = fov <= 0;
                if (camera.orthographic) camera.orthographicSize = orthoSize;
                else camera.fieldOfView = fov;
                camera.aspect = 1;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = far;
                camera.cullingMask = cullingMask;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = s_Background;
                target = RenderTexture.GetTemporary(pixels, pixels, 24);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                var image = new Texture2D(pixels, pixels, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, pixels, pixels), 0, 0);
                image.Apply();
                return image;
            }
            finally
            {
                RenderTexture.active = previous;
                if (camera != null) camera.targetTexture = null;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(obj);
            }
        }
    }
}
