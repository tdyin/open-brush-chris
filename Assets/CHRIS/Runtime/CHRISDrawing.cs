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
    public struct CHRISDrawFrame
    {
        public const float SizeMeters = 0.6f, DistanceMeters = 0.55f, BelowEyeMeters = 0.1f, DepthMeters = 0.05f;
        public Vector3 Center, Right, Up, Forward;
        public float Size;
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
        public const float IdleSeconds = 600, SnapshotIntervalSeconds = 1;
        // The snapshot camera sits this far in front of the frame (toward the user).
        const float CameraBackUnits = 3f;
        const float OutlineUnits = 0.04f;
        static readonly Color s_Background = new Color(0.125f, 0.125f, 0.125f);
        static readonly Color s_Outline = new Color(0.45f, 0.45f, 0.45f);

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
        public static string AcquireRefusal(string taskId, int strokeCount)
        {
            var run = Find(taskId);
            if (run == null) return strokeCount == 0 ? null : "Start a new sketch first";
            if (strokeCount > run.StrokesStarted) return "Sketch changed outside this drawing";
            if (run.StrokesStarted >= MaxStrokesPerRun) return "Stroke limit for this drawing reached";
            return null;
        }

        // The run for a granted draw lease; the first one fixes the frame in front of the head.
        public static Run Begin(string taskId, Vector3 headPosition, Vector3 headForward, float now, bool outline = true)
        {
            var run = Find(taskId);
            if (run == null)
            {
                while (s_Runs.Count >= MaxRuns) End(s_Runs.OrderBy(r => r.LastUse).First());
                run = new Run { TaskId = taskId, Frame = CHRISDrawFrame.Facing(headPosition, headForward, Guid.NewGuid().ToString("N")) };
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
            resources.Surface(root.transform, "Top", new Vector3(0, half, 0), new Vector2(frame.Size, OutlineUnits), s_Outline);
            resources.Surface(root.transform, "Bottom", new Vector3(0, -half, 0), new Vector2(frame.Size, OutlineUnits), s_Outline);
            resources.Surface(root.transform, "Left", new Vector3(-half, 0, 0), new Vector2(OutlineUnits, frame.Size), s_Outline);
            resources.Surface(root.transform, "Right", new Vector3(half, 0, 0), new Vector2(OutlineUnits, frame.Size), s_Outline);
            return root;
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

        // Read-only and rate limited; the frame must exist, no lease is needed.
        public static JObject Snapshot(string taskId, float now, int strokeCount, double capturedAt, out string error)
        {
            error = null;
            var run = Find(taskId);
            if (run == null) { error = "No drawing frame for this task"; return null; }
            if (now - run.LastSnapshot < SnapshotIntervalSeconds) { error = "Snapshot rate limit; retry after 1 s"; return null; }
            run.LastSnapshot = now;
            var image = RenderFrame(run.Frame, CanvasMask(), SnapshotPixels);
            try
            {
                return new JObject
                {
                    ["format"] = "jpeg", ["width"] = SnapshotPixels, ["height"] = SnapshotPixels,
                    ["data"] = Convert.ToBase64String(image.EncodeToJPG(SnapshotQuality)),
                    ["frame_id"] = run.Frame.Id, ["stroke_count"] = strokeCount, ["captured_at"] = capturedAt,
                };
            }
            finally { UnityEngine.Object.DestroyImmediate(image); }
        }

        // An orthographic camera square on the frame, on the user's side, fitted exactly to it.
        // The caller owns the returned texture.
        internal static Texture2D RenderFrame(CHRISDrawFrame frame, int cullingMask, int pixels,
            UnityEngine.SceneManagement.Scene? scene = null)
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
                obj.transform.SetPositionAndRotation(frame.Center - frame.Forward * CameraBackUnits, frame.PenRotation);
                camera.orthographic = true;
                camera.orthographicSize = frame.Size / 2;
                camera.aspect = 1;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = CameraBackUnits + CHRISDrawFrame.DepthMeters * App.METERS_TO_UNITS + 1;
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
