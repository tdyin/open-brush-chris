// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace TiltBrush
{
    public class TestCHRISNativeUI
    {
        sealed class NeutralMappingInput : ICHRISInputState
        {
            public bool IsPressed(string input) => false;
            public bool StopPressedThisFrame => false;
            public float WheelNotches => 0;
            public Vector2 MouseDelta => Vector2.zero;
        }
        static void Set(object obj, string field, object value) => obj.GetType().GetField(field,
            BindingFlags.NonPublic | BindingFlags.Instance).SetValue(obj, value);
        static void Property(object obj, string name, object value) => Set(obj, "<" + name + ">k__BackingField", value);
        static void Reply(CHRISAssistanceClient client, bool success, long code, JObject task, bool submission, bool post) =>
            typeof(CHRISAssistanceClient).GetMethod("AcceptReply", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(client, new object[] { success, code, task, submission, post });
        static JObject Context() => new JObject { ["host_session"] = "session", ["revision"] = 4, ["authority_epoch"] = 2,
            ["brushes"] = new JObject(Enumerable.Range(0, 17).Select(i => new JProperty("brush" + i, "Brush " + i))),
            ["panels"] = new JObject { ["Brush"] = false, ["Color"] = true } };
        [Test]
        public void UncertainSubmissionAndStopNeverCreateAnotherProposal()
        {
            var obj = new GameObject("CHRIS client unit test");
            try
            {
                var client = obj.AddComponent<CHRISAssistanceClient>();
                Property(client, "TaskId", null); Property(client, "Task", null); Property(client, "CancelWanted", false);
                var pending = new JObject { ["request_id"] = "original_request", ["request"] = "Make my brush blue" };
                Property(client, "PendingRequest", pending);
                Reply(client, false, 0, null, true, true);
                Assert.That(client.PendingRequest, Is.SameAs(pending)); Assert.That(client.CanStart, Is.False);
                Property(client, "CancelWanted", true);
                Assert.That(client.RecoveryPath, Is.EqualTo("/requests/original_request"));
                Reply(client, false, 404, new JObject { ["detail"] = "Unknown request" }, true, false);
                Assert.That(client.PendingRequest, Is.SameAs(pending)); Assert.That(client.CanStart, Is.False);
                var task = new JObject { ["task_id"] = "original_task", ["status"] = "awaiting_approval", ["native_cancel_acknowledged"] = false };
                Reply(client, true, 200, task, true, false);
                Assert.That(client.TaskId, Is.EqualTo("original_task")); Assert.That(client.PendingRequest, Is.Null);
                Assert.That(client.CancelWanted, Is.True); Assert.That(client.CanStart, Is.False);
                task["status"] = "cancelled";
                Reply(client, true, 200, task, false, true);
                Assert.That(client.CanStart, Is.False, "unacknowledged cancellation remains locked");
                task["native_cancel_acknowledged"] = true;
                Reply(client, true, 200, task, false, true);
                Assert.That(client.CancelWanted, Is.False); Assert.That(client.CanStart, Is.True);
                foreach (string status in new[] { "paused", "unverified", "awaiting_approval", "proposing", "executing" })
                { task["status"] = status; Assert.That(CHRISAssistanceClient.Terminal(task), Is.False, status); }
                client.Decide(new JObject { ["approval_id"] = "different" }, true);
                Assert.That(client.Error, Does.Contain("Review")); Assert.That(client.Busy, Is.False);
                foreach (string invalid in new[] { "../task", "a/b", "", "task\n" }) Assert.That(CHRISAssistanceClient.ValidId(invalid), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }

        [Test]
        public void SavedExampleRequestsRecoverCancellationWithoutResubmission()
        {
            const string pendingKey = "CHRIS.AssistancePending", cancelKey = "CHRIS.AssistanceCancel";
            bool hadPending = PlayerPrefs.HasKey(pendingKey), hadCancel = PlayerPrefs.HasKey(cancelKey);
            string savedPending = PlayerPrefs.GetString(pendingKey, "");
            int savedCancel = PlayerPrefs.GetInt(cancelKey, 0);
            var obj = new GameObject("CHRIS retired example recovery");
            try
            {
                var client = obj.AddComponent<CHRISAssistanceClient>();
                foreach (JToken action in new JToken[] { new JObject { ["tool"] = "brush.size", ["number"] = 0.3 }, JValue.CreateNull() })
                {
                    var pending = new JObject { ["request_id"] = "saved_example", ["action"] = action };
                    PlayerPrefs.SetString(pendingKey, pending.ToString());
                    PlayerPrefs.SetInt(cancelKey, 0);
                    TestCHRISAssistance.Call(client, "Awake");
                    Assert.That(client.CancelWanted, Is.True);
                    Assert.That(client.CanStart, Is.False);
                    Assert.That(client.RecoveryPath, Is.EqualTo("/requests/saved_example"));
                    Reply(client, false, 404, null, true, false);
                    Assert.That(JToken.DeepEquals(client.PendingRequest, pending), Is.True);
                    Assert.That(client.CancelWanted, Is.True, "An unknown reply cannot authorize a replacement or replay");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(obj);
                if (hadPending) PlayerPrefs.SetString(pendingKey, savedPending); else PlayerPrefs.DeleteKey(pendingKey);
                if (hadCancel) PlayerPrefs.SetInt(cancelKey, savedCancel); else PlayerPrefs.DeleteKey(cancelKey);
                PlayerPrefs.Save();
            }
        }

        [Test]
        public void NativeAssetsProvideMenuPopupAndClickableButtons()
        {
            var resources = CHRISUIResources.Load();
            Assert.That(resources, Is.Not.Null); Assert.That(resources.Font, Is.Not.Null); Assert.That(resources.SurfaceShader, Is.Not.Null);
            Assert.That(resources.PopupPrefab.GetComponent<CHRISNativePopup>(), Is.Not.Null);
            Assert.That(resources.PopupPrefab.GetComponent<UIComponentManager>(), Is.Not.Null);
            Assert.That(resources.PopupPrefab.GetComponent<BoxCollider>().size,
                Is.EqualTo(new Vector3(CHRISNativePopup.Width, CHRISNativePopup.Height, 0.1f)));
            Assert.That(resources.BodyFont, Is.Not.Null);
            Assert.That(resources.HeadingFont, Is.Not.Null);
            Assert.That(resources.RoundedMesh, Is.Not.Null);
            Assert.That(resources.BorderMesh, Is.Not.Null);
            Assert.That(resources.IconShader, Is.Not.Null);
            var lab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Panels/LabsPanel.prefab");
            Assert.That(lab.GetComponentsInChildren<CHRISLabButton>(true).Length, Is.EqualTo(1));
            var launcher = lab.GetComponentInChildren<CHRISLabButton>(true);
            var labButtons = launcher.transform.parent.GetComponentsInChildren<BaseButton>(true)
                .Where(button => button.gameObject.activeSelf && button.transform.parent == launcher.transform.parent).ToArray();
            foreach (var button in labButtons)
            {
                Assert.That(button.transform.localPosition.y, Is.GreaterThan(-0.4f), "Lab icons stay above the tint slider");
                Assert.That(Math.Abs(button.transform.localPosition.x), Is.LessThan(0.7f));
                foreach (var other in labButtons.Where(other => other != button))
                    Assert.That(Vector3.Distance(button.transform.localPosition, other.transform.localPosition), Is.GreaterThan(0.34f));
            }
            Assert.That(resources.BodyMaterial.GetColor("_FaceColor").a, Is.EqualTo(1));
            Assert.That(resources.HeadingMaterial.GetColor("_FaceColor").a, Is.EqualTo(1));
        }

        [Test]
        public void MoreMenuHasNoLegacyStopAndKeepsItsOriginalHitBounds()
        {
            var menu = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/PopUps/PopUpWindow_Panels.prefab");
            var scripts = menu.GetComponentsInChildren<MonoBehaviour>(true);
            Assert.That(scripts.All(script => script != null), Is.True, "More menu must not contain missing scripts.");
            Assert.That(scripts.Select(script => script.GetType().Name), Does.Not.Contain("CHRISMenuEntry"));
            Assert.That(menu.GetComponentsInChildren<CHRISNativeButton>(true), Is.Empty);
            var menuCollider = menu.GetComponent<BoxCollider>();
            Assert.That(menuCollider.size, Is.EqualTo(new Vector3(1.45f, 1.26f, 0.01f)));
            Assert.That(menuCollider.center, Is.EqualTo(new Vector3(0, 0, -0.0125f)));
        }

        static readonly string[] DragStrips = { "Drag title" };

        internal static string[] Icons(GameObject root) => root.GetComponentsInChildren<CHRISNativeButton>()
            .Select(b => b.name).ToArray();

        [Test]
        public void StatusPanelHasStopAndBorderDragOnly()
        {
            var resources = CHRISUIResources.Load();
            var popupObject = UnityEngine.Object.Instantiate(resources.PopupPrefab);
            try
            {
                popupObject.GetComponent<CHRISNativePopup>().BuildView();
                // The frame is a copy of the wand panels' wireframe border with its outline baked.
                var border = popupObject.transform.Find("Native panel border");
                Assert.That(border.GetComponent<BakedMeshOutline>(), Is.Not.Null);
                Assert.That(border.GetComponent<MeshRenderer>().sharedMaterial.shader,
                    Is.EqualTo(resources.NativeBorder.GetComponent<MeshRenderer>().sharedMaterial.shader));
                Assert.That(border.GetComponent<MeshFilter>().sharedMesh.vertexCount,
                    Is.EqualTo(2 * resources.NativeBorder.GetComponent<MeshFilter>().sharedMesh.vertexCount), "outline baked");
                // Same transform as the Labs border, so lines and corners are not stretched.
                Assert.That(border.localScale, Is.EqualTo(resources.NativeBorder.transform.localScale));
                Physics.SyncTransforms();
                var size = border.GetComponent<MeshRenderer>().bounds.size;
                Assert.That(Mathf.Abs(size.x - CHRISNativePopup.Width), Is.LessThan(0.1f));
                Assert.That(Mathf.Abs(size.y - CHRISNativePopup.Height), Is.LessThan(0.1f));
                // Every visible control is a native icon button with an icon and a hover description.
                var icons = popupObject.GetComponentsInChildren<CHRISNativeButton>(true);
                Assert.That(icons.All(b => b.GetComponent<MeshFilter>().sharedMesh == resources.RoundedMesh &&
                    b.GetComponent<Renderer>().sharedMaterial.shader == resources.NativeIconMaterial.shader &&
                    b.GetComponent<Renderer>().sharedMaterial.mainTexture != null), Is.True);
                Assert.That(icons.Where(b => !b.name.StartsWith("Test case")).All(b => !string.IsNullOrEmpty(b.Hover)), Is.True);
                var stop = icons.Single(b => b.name == "Local Stop");
                Assert.That(stop.GetComponent<Renderer>().sharedMaterial.mainTexture.name, Is.EqualTo(CHRISNativePopup.StopIcon));
                Assert.That(stop.Hover, Is.EqualTo("Stop: CHRIS releases control now"));
                Assert.That(icons.Single(b => b.name == "Test approve").GetComponent<Renderer>().sharedMaterial.mainTexture.name, Is.EqualTo(CHRISNativePopup.ApproveIcon));
                Assert.That(icons.Single(b => b.name == "Test decline").GetComponent<Renderer>().sharedMaterial.mainTexture.name, Is.EqualTo(CHRISNativePopup.DeclineIcon));
                Assert.That(icons.Where(b => b.name.StartsWith("Test case")).All(b =>
                    b.GetComponent<Renderer>().sharedMaterial.mainTexture.name == CHRISNativePopup.StartIcon && b.Label != null), Is.True);
                // No Move button: the "CHRIS" title moves the panel and says so on hover.
                var strips = popupObject.GetComponentsInChildren<CHRISDragStrip>();
                Assert.That(strips.Select(b => b.name), Is.EquivalentTo(DragStrips));
                Assert.That(strips.All(b => b.Hover == "Drag to move" && b.GetComponent<Renderer>() == null), Is.True);
                Assert.That(popupObject.GetComponentsInChildren<UIComponent>(true).Any(b => b.name.Contains("Move")), Is.False);
                var title = popupObject.transform.Find("Title");
                Assert.That(Vector3.Distance(strips[0].transform.localPosition, title.localPosition), Is.LessThan(0.03f));
                // One What's-new black background piece fills the frame without a join, behind the content.
                var background = popupObject.transform.Find("Native panel background");
                Assert.That(background.GetComponent<MeshRenderer>().sharedMaterial,
                    Is.EqualTo(resources.NativeBackgroundPiece.GetComponent<MeshRenderer>().sharedMaterial));
                Assert.That(background.localPosition.z, Is.GreaterThan(border.localPosition.z));
                Assert.That(popupObject.GetComponentsInChildren<MeshRenderer>(true)
                    .Count(r => r.sharedMaterial == background.GetComponent<MeshRenderer>().sharedMaterial), Is.EqualTo(1));
                var fill = background.GetComponent<MeshRenderer>().bounds.size;
                Assert.That(Mathf.Abs(fill.x - (CHRISNativePopup.Width - 0.12f)), Is.LessThan(0.05f));
                Assert.That(Mathf.Abs(fill.y - (CHRISNativePopup.Height - 0.12f)), Is.LessThan(0.05f));
                Assert.That(popupObject.GetComponentsInChildren<TextMeshPro>(true).Where(t => t.font == resources.NativeBodyFont)
                    .All(t => t.fontSharedMaterial == resources.BodyMaterial), Is.True, "Body text is full white");
                Assert.That(Icons(popupObject), Is.EquivalentTo(new[] { "Local Stop" }));
                Assert.That(CHRISNativePopup.StatusLine("Mapping active", true),
                    Is.EqualTo("Mapping active  ·  Brush: Right  ·  Wand: Left"));
                Assert.That(CHRISNativePopup.ControlLabel("stopped", false, null, false, false, false),
                    Is.EqualTo("Mapping stopped"));
                Assert.That(CHRISNativePopup.ControlLabel("stopped", false, null, true, false, false),
                    Is.EqualTo("Keyboard menu control"));
                Assert.That(CHRISNativePopup.ControlLabel("pending_neutral", true, null, false, false, false),
                    Is.EqualTo("Mapping pending"));
                Assert.That(CHRISNativePopup.HandRoles(true), Is.EqualTo("Brush: Right  ·  Wand: Left"));
                var bimanual = CHRISInputMapping.Parse(Resources.Load<TextAsset>("CHRIS/TwoHandDefault").bytes);
                Assert.That(CHRISNativePopup.ControlLabel("active", false, bimanual, false, false, true),
                    Is.EqualTo("Hand-back pending"));
                Assert.That(CHRISNativePopup.ControlLabel("active", false, bimanual, false, false, false),
                    Is.EqualTo("Physical controllers"));
            }
            finally { UnityEngine.Object.DestroyImmediate(popupObject); }
        }

        [Test]
        public void FloatingPanelStaysInWorldAndDragStopsOnRelease()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var previousManager = PanelManager.m_Instance;
            try
            {
                var room = new GameObject("Room");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(room, scene);
                var head = new GameObject("Head").transform; head.SetParent(room.transform);
                var hand = new GameObject("Left hand").transform; hand.SetParent(room.transform);
                head.position = new Vector3(0, 16, 0);
                var panel = CHRISFloatingPanel.Create(room.transform);
                var manager = room.AddComponent<PanelManager>();
                PanelManager.m_Instance = manager;
                var components = panel.GetComponent<UIComponentManager>();
                typeof(UIComponentManager).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(components, null);
                panel.InitPanel();
                Assert.That(panel.gameObject.activeSelf, Is.False, "Starts closed");
                panel.gameObject.SetActive(true);
                Assert.That(panel.Type, Is.EqualTo(BasePanel.PanelType.CHRIS));
                Assert.That(panel.m_Fixed, Is.False);
                Assert.That(panel.transform.IsChildOf(hand), Is.False);
                panel.PlaceInFront(head);
                var placed = panel.transform.position; var rotation = panel.transform.rotation;
                Assert.That(placed.x, Is.GreaterThan(head.position.x));
                Assert.That(placed.z, Is.GreaterThan(head.position.z));
                hand.position += new Vector3(-8, 3, 2); hand.rotation = Quaternion.Euler(70, 30, 20);
                head.position += new Vector3(1, 0, 0); head.rotation = Quaternion.Euler(0, 30, 0);
                Assert.That(panel.transform.position, Is.EqualTo(placed), "Head and hand motion must not carry the window");
                panel.FaceUser(head.position);
                Assert.That(panel.transform.position, Is.EqualTo(placed), "Facing the user only rotates the window");
                Assert.That(Vector3.Dot(-panel.transform.forward, (head.position - placed).normalized), Is.GreaterThan(0.9999f));
                var facing = panel.transform.rotation;
                head.rotation = Quaternion.Euler(20, 90, 45); panel.FaceUser(head.position);
                Assert.That(Quaternion.Angle(panel.transform.rotation, facing), Is.LessThan(0.001f), "Follow head position, not head roll or gaze");
                panel.FaceUser(placed);
                Assert.That(panel.transform.rotation, Is.EqualTo(facing), "Coincident positions must not produce an invalid rotation");
                Physics.SyncTransforms();
                Assert.That(panel.RaycastAgainstMeshCollider(new Ray(placed - panel.transform.forward * 8,
                    panel.transform.forward), out _, 4), Is.True, "Floating controls remain reachable beyond the short wand-menu ray");
                var ray = new Ray(new Vector3(0, 12, 0), Vector3.forward);
                var hit = ray.GetPoint(7);
                panel.BeginDrag(ray, hit);
                Assert.That(panel.IsDragging, Is.True);
                panel.MoveDrag(ray, true);
                Assert.That(Vector3.Distance(panel.transform.position, placed), Is.LessThan(0.0001f), "No snap when drag starts");
                var movedRay = new Ray(ray.origin + new Vector3(2, 1, 1), ray.direction);
                panel.MoveDrag(movedRay, true);
                panel.FaceUser(head.position);
                Assert.That(Vector3.Dot(-panel.transform.forward, (head.position - panel.transform.position).normalized), Is.GreaterThan(0.9999f));
                Assert.That(Vector3.Distance(panel.transform.position, placed + new Vector3(2, 1, 1)), Is.LessThan(0.0001f));
                var released = panel.transform.position;
                panel.MoveDrag(ray, false); panel.MoveDrag(ray, true);
                Assert.That(panel.IsDragging, Is.False);
                Assert.That(panel.transform.position, Is.EqualTo(released), "Release leaves the window where placed");
                panel.BeginDrag(ray, hit); panel.EndDrag(); panel.MoveDrag(movedRay, true);
                Assert.That(panel.transform.position, Is.EqualTo(released), "Stop must end an active drag");
                Assert.That(manager.IsPanelUnique(panel.Type), Is.True, "One owner shared by basic and advanced modes");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); PanelManager.m_Instance = previousManager; }
        }

        [MenuItem("CHRIS/Verify native UI (Edit mode)")]
        public static void VerifyInEditor() => RunChecks();

        public static void Run()
        {
            try { RunChecks(); EditorApplication.Exit(0); }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        static void RunChecks()
        {
            const string output = "agent/logs/native-ui";
            Directory.CreateDirectory(output);
            CHRISCloudVoiceBuild.VerifyNoLegacyResources();
            string[] keys = { "CHRIS.AssistanceTask", "CHRIS.AssistancePending", "CHRIS.AssistanceCancel", CHRISConfirmationSpeech.Preference };
            var hadKeys = keys.Select(PlayerPrefs.HasKey).ToArray();
            var savedStrings = keys.Take(2).Select(k => PlayerPrefs.GetString(k, "")).ToArray();
            var savedInts = keys.Skip(2).Select(k => PlayerPrefs.GetInt(k, 0)).ToArray();
            try
            {
            TestCHRISAssistance.Exported.Clear();
            int tests = 0;
            foreach (var suite in new object[] { new TestCHRISNativeUI(), new TestCHRISAssistance(), new TestCHRISCloudVoice(), new TestCHRISCompanion(), new TestCHRISInputMapping(), new TestCHRISInputRemap(), new TestCHRISBimanualInput(), new TestCHRISMappingAuthority(), new TestCHRISPerfProbe(), new TestCHRISProcedure() })
                foreach (var method in suite.GetType().GetMethods().Where(m => m.GetCustomAttributes(typeof(TestAttribute), false).Length > 0))
                {
                    method.Invoke(suite, null);
                    tests++;
                }
            Render(output);
            File.WriteAllText(output + "/protocol-fixtures.json", TestCHRISAssistance.Exported.ToString());
            File.WriteAllText(output + "/companion-profiling.json", TestCHRISCompanion.Measurements.ToString());
            File.WriteAllText(output + "/mapping-cases.txt", TestCHRISInputMapping.Verdicts.ToString());
            File.WriteAllText(output + "/checks.txt", "PASS: " + tests + " native regression methods; voice and companion contracts, compact status UI, v0.1.1 and v0.1.2 mapping cases, byte-matched bundled preset, two-hand state transitions, and the controller-procedure lease (" + TestCHRISProcedure.SharedCasesChecked + " shared procedure message cases). No Play mode, HTTP, microphone recording, speech/model inference or sketch changes.");
            }
            finally
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    if (!hadKeys[i]) PlayerPrefs.DeleteKey(keys[i]);
                    else if (i >= 2) PlayerPrefs.SetInt(keys[i], savedInts[i - 2]);
                    else PlayerPrefs.SetString(keys[i], savedStrings[i]);
                }
                PlayerPrefs.Save();
            }
        }

        static void Render(string output)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            RenderTexture texture = null;
            Texture2D image = null;
            Camera camera = null;
            try
            {
                var popupObject = UnityEngine.Object.Instantiate(CHRISUIResources.Load().PopupPrefab);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(popupObject, scene);
                var popup = popupObject.GetComponent<CHRISNativePopup>();
                popup.BuildView();
                Physics.SyncTransforms();
                Assert.That(Icons(popupObject), Is.EquivalentTo(new[] { "Local Stop" }));
                var cameraObject = new GameObject("CHRIS status preview camera");
                camera = cameraObject.AddComponent<Camera>();
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, scene);
                camera.scene = scene;
                cameraObject.AddComponent<UniversalAdditionalCameraData>();
                camera.orthographic = true;
                camera.orthographicSize = CHRISNativePopup.Height / 2 + 0.1f;
                camera.transform.position = new Vector3(0, 0, -10);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.08f, 0.08f, 0.08f);
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 20;
                texture = new RenderTexture(1100, (int)(1100 * (CHRISNativePopup.Height + 0.2f) / (CHRISNativePopup.Width + 0.2f)), 24);
                texture.Create(); camera.targetTexture = texture;
                image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                void Capture(GameObject root, string file)
                {
                    Physics.SyncTransforms();
                    var visible = root.GetComponentsInChildren<UIComponent>().Where(b => b is CHRISNativeButton || b is CHRISDragStrip)
                        .Select(b => b.GetComponent<BoxCollider>().bounds).ToArray();
                    for (int a = 0; a < visible.Length; a++)
                        for (int b = a + 1; b < visible.Length; b++)
                            Assert.That(visible[a].Intersects(visible[b]), Is.False, file + ": buttons overlap");
                    foreach (var label in root.GetComponentsInChildren<TextMeshPro>()) label.ForceMeshUpdate();
                    camera.Render(); RenderTexture.active = texture;
                    image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); image.Apply();
                    File.WriteAllBytes(output + "/" + file, image.EncodeToPNG());
                }
                Newtonsoft.Json.Linq.JObject Display(string[] lines, string nonce, params (int id, string title)[] cases) =>
                    new Newtonsoft.Json.Linq.JObject {
                        ["lines"] = new Newtonsoft.Json.Linq.JArray(lines), ["nonce"] = nonce,
                        ["buttons"] = nonce == null ? new Newtonsoft.Json.Linq.JArray() : new Newtonsoft.Json.Linq.JArray("approve", "decline"),
                        ["cases"] = new Newtonsoft.Json.Linq.JArray(cases.Select(c => new Newtonsoft.Json.Linq.JObject { ["id"] = c.id, ["title"] = c.title })),
                        ["ttl_s"] = 60 };
                Capture(popupObject, "status-panel.png");
                try
                {
                    // The states the runner shows, rendered as the user sees them.
                    CHRISTestDisplay.Instance.Show(Display(new[] { "CHRIS tests: click Start with the brush ray",
                        "Next: case 4 - Request the current brush (no-op)" }, null,
                        (4, "Request the current brush"), (1, "Select a brush"), (3, "Squeeze the real trigger"),
                        (5, "Target not on the page"), (2, "Press Escape")), Time.realtimeSinceStartup);
                    popup.RefreshTest();
                    Capture(popupObject, "status-panel-idle.png");
                    Assert.That(Icons(popupObject).Count(n => n.StartsWith("Test case")), Is.EqualTo(5));
                    var first = popupObject.GetComponentsInChildren<CHRISNativeButton>().Single(b => b.name == "Test case 1");
                    Assert.That(first.Label.text, Is.EqualTo("4"));
                    Assert.That(first.Hover, Is.EqualTo("Start case 4: Request the current brush"));
                    CHRISTestDisplay.Instance.Show(Display(new[] { "Case 1 Happy path: select Light",
                        "Look at the palette, keep the brush trigger released", "Approve to start", "Last: case 4 PASS" },
                        "preview"), Time.realtimeSinceStartup);
                    popup.RefreshTest();
                    Capture(popupObject, "status-panel-test.png");
                    Assert.That(Icons(popupObject), Is.EquivalentTo(new[] { "Local Stop", "Test approve", "Test decline" }),
                        "a live approval shows only Approve/Decline beside Stop");
                    CHRISTestDisplay.Instance.Tick(Time.realtimeSinceStartup + CHRISTestDisplay.StaleSeconds + 1);
                    popup.RefreshTest();
                    Capture(popupObject, "status-panel-stale.png");
                    Assert.That(Icons(popupObject), Is.EquivalentTo(new[] { "Local Stop" }), "Stop stays available when stale");
                }
                finally { CHRISTestDisplay.ResetForPlay(); }
                // Open Brush's own Labs panel at the same scale, beside the idle CHRIS panel. Its
                // border and icons are set up here the way BasePanel.InitPanel and BaseButton do
                // at runtime, on copies, so the shared assets stay untouched.
                var native = UnityEngine.Object.Instantiate(
                    AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Panels/LabsPanel.prefab"));
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(native, scene);
                native.transform.rotation = Quaternion.identity;
                var generated = new System.Collections.Generic.List<UnityEngine.Object>();
                foreach (var bakery in native.GetComponentsInChildren<BakedMeshOutline>(true))
                {
                    var filter = bakery.GetComponent<MeshFilter>();
                    var copy = UnityEngine.Object.Instantiate(filter.sharedMesh);
                    generated.Add(copy);
                    filter.sharedMesh = copy;
                    bakery.Bake(Color.white, Color.black, 0.02f);
                    generated.Add(filter.sharedMesh);
                }
                foreach (var button in native.GetComponentsInChildren<BaseButton>(true))
                {
                    var renderer = button.GetComponent<Renderer>();
                    var icon = typeof(BaseButton).GetField("m_ButtonTexture", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(button) as Texture2D;
                    if (renderer == null || icon == null) continue;
                    var material = new Material(renderer.sharedMaterial) { mainTexture = icon };
                    generated.Add(material);
                    renderer.sharedMaterial = material;
                }
                try
                {
                    Physics.SyncTransforms();
                    var labBounds = native.GetComponentsInChildren<Renderer>(true).First(r => r.name == "Border").bounds;
                    File.WriteAllText(output + "/d80-layout.txt", "Labs border world size " + labBounds.size.x.ToString("F3") + " x " +
                        labBounds.size.y.ToString("F3") + "; CHRIS panel " + CHRISNativePopup.Width + " x " + CHRISNativePopup.Height + "\n");
                    popupObject.SetActive(false);
                    native.transform.position = Vector3.zero;
                    Capture(native, "native-labs-panel.png");
                    // Side by side: the two captures above, taken with one camera at one scale.
                    var labs = new Texture2D(2, 2); var chris = new Texture2D(2, 2);
                    try
                    {
                        labs.LoadImage(File.ReadAllBytes(output + "/native-labs-panel.png"));
                        chris.LoadImage(File.ReadAllBytes(output + "/status-panel-idle.png"));
                        var pair = new Texture2D(labs.width + chris.width, labs.height, TextureFormat.RGB24, false);
                        pair.SetPixels32(0, 0, labs.width, labs.height, labs.GetPixels32());
                        pair.SetPixels32(labs.width, 0, chris.width, chris.height, chris.GetPixels32());
                        pair.Apply();
                        File.WriteAllBytes(output + "/status-panel-next-to-labs.png", pair.EncodeToPNG());
                        UnityEngine.Object.DestroyImmediate(pair);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(labs); UnityEngine.Object.DestroyImmediate(chris); }
                }
                finally { foreach (var item in generated) if (item != null) UnityEngine.Object.DestroyImmediate(item); }
            }
            finally
            {
                RenderTexture.active = null;
                if (camera != null) camera.targetTexture = null;
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                if (texture != null) { texture.Release(); UnityEngine.Object.DestroyImmediate(texture); }
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
