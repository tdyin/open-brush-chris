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
                foreach (string status in new[] { "paused", "unverified", "awaiting_approval", "running" })
                { task["status"] = status; Assert.That(CHRISAssistanceClient.Terminal(task), Is.False, status); }
                client.Decide(new JObject { ["approval_id"] = "different" }, true);
                Assert.That(client.Error, Does.Contain("Review")); Assert.That(client.Busy, Is.False);
                foreach (string invalid in new[] { "../task", "a/b", "", "task\n" }) Assert.That(CHRISAssistanceClient.ValidId(invalid), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }

        [Test]
        public void NativeAssetsProvideMenuPopupKeyboardAndClickableButtons()
        {
            var resources = CHRISUIResources.Load();
            Assert.That(resources, Is.Not.Null); Assert.That(resources.Font, Is.Not.Null); Assert.That(resources.SurfaceShader, Is.Not.Null);
            Assert.That(resources.KeyboardPrefab.GetComponent<KeyboardPopUpWindow>(), Is.Not.Null);
            Assert.That(resources.PopupPrefab.GetComponent<CHRISNativePopup>(), Is.Not.Null);
            Assert.That(resources.PopupPrefab.GetComponent<UIComponentManager>(), Is.Not.Null);
            Assert.That(resources.PopupPrefab.GetComponent<BoxCollider>().size, Is.EqualTo(new Vector3(3.8f, 4, 0.1f)));
            var menu = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/PopUps/PopUpWindow_Panels.prefab");
            Assert.That(menu.GetComponent<CHRISMenuEntry>(), Is.Not.Null);
            var menuCollider = menu.GetComponent<BoxCollider>();
            Assert.That(menuCollider.center.y - menuCollider.size.y / 2, Is.LessThan(-0.86f));
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
            const string output = "Build/CHRISNativeUI";
            Directory.CreateDirectory(output);
            CHRISVoiceBuild.VerifyResources();
            string[] keys = { "CHRIS.AssistanceTask", "CHRIS.AssistancePending", "CHRIS.AssistanceCancel" };
            var hadKeys = keys.Select(PlayerPrefs.HasKey).ToArray();
            var savedStrings = keys.Take(2).Select(k => PlayerPrefs.GetString(k, "")).ToArray();
            int savedCancel = PlayerPrefs.GetInt(keys[2], 0);
            try
            {
            TestCHRISAssistance.Exported.Clear();
            int tests = 0;
            foreach (var suite in new object[] { new TestCHRISNativeUI(), new TestCHRISAssistance() })
                foreach (var method in suite.GetType().GetMethods().Where(m => m.GetCustomAttributes(typeof(TestAttribute), false).Length > 0))
                {
                    method.Invoke(suite, null);
                    tests++;
                }
            Render(output);
            File.WriteAllText(output + "/protocol-fixtures.json", TestCHRISAssistance.Exported.ToString());
            File.WriteAllText(output + "/checks.txt", "PASS: " + tests + " native regression methods; ordered segments, whole-list validation, replay, Stop/takeover/expiry, delayed panels, numeric/rotation tolerances, Python wire fixtures, cancelled/replaced speech callbacks, missing microphone/provider errors, correction/legacy-summary guards, floating native UI. No Play mode, HTTP, microphone recording, speech/model inference or sketch changes.");
            }
            finally
            {
                for (int i = 0; i < keys.Length; i++)
                {
                    if (!hadKeys[i]) PlayerPrefs.DeleteKey(keys[i]);
                    else if (i == 2) PlayerPrefs.SetInt(keys[i], savedCancel);
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
            CHRISPanel model = null;
            try
            {
                var resources = CHRISUIResources.Load();
                var popupObject = UnityEngine.Object.Instantiate(resources.PopupPrefab);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(popupObject, scene);
                var popup = popupObject.GetComponent<CHRISNativePopup>(); popup.BuildView();
                Assert.That(popupObject.GetComponentsInChildren<CHRISNativeButton>().Any(b => b.name == "Direct mode" || b.Label.text == "Shortcuts"), Is.False);
                var hostObject = new GameObject("CHRIS preview host");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(hostObject, scene);
                var host = hostObject.AddComponent<CHRISGatewayTestHost>(); host.Initialize();
                model = hostObject.AddComponent<CHRISPanel>(); model.Gateway = host;
                TestCHRISAssistance.Call(model, "Start");
                Property(model, "Popup", popup); Set(popup, "m_Model", model);
                var fixtures = JArray.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Editor/Tests/Fixtures/CHRISControlSegments.json")));
                var fixture = (JObject)fixtures[0];
                var approval = (JObject)fixture["envelope"]["approval"].DeepClone();
                var context = host.Capture();
                foreach (string key in new[] { "host_session", "revision", "authority_epoch" }) approval[key] = context[key];
                approval["expires_at"] = CHRISCommandGateway.Now + 300;
                var task = new JObject { ["task_id"] = approval["task_id"], ["status"] = "awaiting_approval", ["reason"] = "Review the commands",
                    ["actions"] = approval["actions"], ["approval"] = approval, ["context"] = context, ["summary"] = fixture["summary"] };
                Property(model.Assistance, "Task", task); Property(model.Assistance, "TaskId", (string)task["task_id"]);
                Property(model.Assistance, "Busy", false); Property(model.Assistance, "CancelWanted", false);
                model.Refresh();
                TestCHRISAssistance.Call(popup, "Draw");
                Physics.SyncTransforms();
                foreach (var button in popupObject.GetComponentsInChildren<CHRISNativeButton>())
                {
                    var collider = button.GetComponent<BoxCollider>();
                    Assert.That(button.Label, Is.Not.Null); Assert.That(collider, Is.Not.Null);
                    Assert.That(collider.Raycast(new Ray(collider.bounds.center - Vector3.forward * 2, Vector3.forward), out _, 3), Is.True);
                }
                var cameraObject = new GameObject("CHRIS preview camera"); camera = cameraObject.AddComponent<Camera>();
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObject, scene); camera.scene = scene;
                cameraObject.AddComponent<UniversalAdditionalCameraData>(); camera.orthographic = true; camera.orthographicSize = 2.25f;
                camera.transform.position = new Vector3(0, 0, -10); camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.08f, 0.08f, 0.08f); camera.nearClipPlane = 0.01f; camera.farClipPlane = 20;
                texture = new RenderTexture(1100, 1200, 24); texture.Create(); camera.targetTexture = texture;
                image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                System.Action<string, GameObject> capture = (name, obj) =>
                {
                    foreach (var text in obj.GetComponentsInChildren<TextMeshPro>()) text.ForceMeshUpdate();
                    camera.Render(); RenderTexture.active = texture;
                    image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); image.Apply();
                    File.WriteAllBytes(output + "/" + name + ".png", image.EncodeToPNG()); RenderTexture.active = null;
                };
                capture("assist-review", popupObject);
                var detail = popupObject.GetComponentsInChildren<TextMeshPro>().Single(t => t.gameObject.name == "Review");
                Assert.That(detail.isTextOverflowing, Is.False, "Exact commands must fit the review page");
                Assert.That(model.ReviewText, Is.EqualTo((string)fixture["summary"]));
                var confirm = popupObject.GetComponentsInChildren<CHRISNativeButton>().Single(b => b.Label.text == "Confirm commands");
                Assert.That(confirm.IsAvailable(), Is.True); confirm.Click();
                Assert.That(model.WaitingForRelease, Is.True);
                Property(model.Assistance, "TaskId", null); Property(model.Assistance, "PendingRequest", null);
                long stops = host.StopCount;
                popupObject.GetComponentsInChildren<CHRISNativeButton>().Single(b => b.name == "Local Stop").Click();
                Assert.That(host.StopCount, Is.EqualTo(stops + 1)); Assert.That(model.WaitingForRelease, Is.False);
                Property(model.Assistance, "Task", null); model.BeginCorrection();
                TestCHRISAssistance.Call(popup, "Draw"); capture("assist-ready", popupObject);
                long recording = model.Voice.Session.Begin(); model.Voice.Session.Ready(recording);
                model.Voice.Receive(recording, "partial", "make my brush blue and smaller");
                TestCHRISAssistance.Call(popup, "Draw"); capture("assist-listening", popupObject);
                model.Voice.CancelRecording();
                popupObject.SetActive(false);
                var menu = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/PopUps/PopUpWindow_Panels.prefab"));
                menu.transform.position = Vector3.zero; menu.transform.rotation = Quaternion.identity;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(menu, scene);
                TestCHRISAssistance.Call(menu.GetComponent<CHRISMenuEntry>(), "Start");
                stops = host.StopCount;
                var stop = menu.GetComponentsInChildren<CHRISNativeButton>().Single(b => b.name == "CHRIS local Stop");
                Assert.That(stop.IsAvailable(), Is.True); stop.Click(); Assert.That(host.StopCount, Is.EqualTo(stops + 1));
                camera.orthographicSize = 1.15f; capture("assist-menu", menu);
            }
            finally
            {
                if (model != null && model.Assistance != null)
                { Property(model.Assistance, "TaskId", null); Property(model.Assistance, "PendingRequest", null); Property(model.Assistance, "Busy", false); }
                if (camera != null) camera.targetTexture = null;
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                if (texture != null) { texture.Release(); UnityEngine.Object.DestroyImmediate(texture); }
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
