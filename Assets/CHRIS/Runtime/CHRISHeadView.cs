// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace TiltBrush
{
    // input-v0.2 image on demand (D122): a read-only render of what the user sees from the head
    // pose (scene, strokes, panels, controllers), for the model's `look` tool. A symmetric square
    // camera at the head pose, so the projection is exact: centre pixel = head forward. Separate
    // from the strokes-only drawing snapshot; nothing here changes the sketch or any input.
    public static class CHRISHeadView
    {
        public const string View = "head";
        public static readonly int[] Widths = { 512, 768, 1024 };
        public const float FovDegrees = 90f, MinSecondsBetween = 1f;
        public const int Quality = 80;
        public const string RateRefusal = "Image rate limit";
        static float s_LastTaken = float.NegativeInfinity;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetForPlay() => s_LastTaken = float.NegativeInfinity;

        // Message shape only, as the shared image_request cases describe (stateless).
        public static string ValidateRequest(JObject request)
        {
            if (request == null || !request.Properties().All(p => p.Name == "view" || p.Name == "width") ||
                request["view"]?.Type != JTokenType.String || (string)request["view"] != View ||
                request["width"]?.Type != JTokenType.Integer || !Widths.Contains((int)(long)request["width"]))
                return CHRISInputExecutor.FieldRefusal;
            return null;
        }

        // The rate limit counts only images actually taken.
        internal static bool RateLimited(float now) => now - s_LastTaken < MinSecondsBetween;
        internal static void Taken(float now) => s_LastTaken = now;

        // Renders one image from the live head and VR camera. Null with an error when either is
        // missing. Positions in the reply are room metres; the caller adds frame and time.
        internal static JObject Capture(int width, out string error)
        {
            error = null;
            var head = ViewpointScript.Head;
            var source = App.VrSdk != null ? App.VrSdk.GetVrCamera() : Camera.main;
            if (head == null || source == null) { error = "Host busy or unavailable"; return null; }
            return Capture(width, head, source);
        }

        // The render itself: the source camera's layers, clear and clip planes, with a symmetric
        // square 90 degree projection at the head pose.
        internal static JObject Capture(int width, Transform head, Camera source)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var obj = new GameObject("CHRIS head view camera") { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture target = null;
            Texture2D image = null;
            var previous = RenderTexture.active;
            Camera camera = null;
            try
            {
                camera = obj.AddComponent<Camera>();
                obj.AddComponent<UniversalAdditionalCameraData>();
                camera.enabled = false;
                camera.cullingMask = source.cullingMask;
                camera.clearFlags = source.clearFlags;
                camera.backgroundColor = source.backgroundColor;
                camera.nearClipPlane = source.nearClipPlane;
                camera.farClipPlane = source.farClipPlane;
                camera.stereoTargetEye = StereoTargetEyeMask.None;
                camera.fieldOfView = FovDegrees;
                camera.aspect = 1;
                obj.transform.SetPositionAndRotation(head.position, head.rotation);
                target = RenderTexture.GetTemporary(width, width, 24);
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image = new Texture2D(width, width, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, width, width), 0, 0);
                image.Apply();
                byte[] jpeg = image.EncodeToJPG(Quality);
                float units = App.METERS_TO_UNITS;
                var reply = new JObject
                {
                    ["format"] = "jpeg",
                    ["width"] = width,
                    ["height"] = width,
                    ["data"] = Convert.ToBase64String(jpeg),
                    ["camera"] = CHRISCommandGateway.PoseObject(head.position / units, head.rotation),
                    ["fov_v_deg"] = FovDegrees,
                    ["aspect"] = 1.0,
                    ["near_m"] = source.nearClipPlane / units,
                    ["far_m"] = source.farClipPlane / units,
                };
                Debug.Log($"CHRIS observe image: {width}x{width}, {jpeg.Length} bytes, {timer.Elapsed.TotalMilliseconds:F0} ms");
                return reply;
            }
            finally
            {
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                RenderTexture.active = previous;
                if (camera != null) camera.targetTexture = null;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(obj);
            }
        }
    }
}
