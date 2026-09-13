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
        public void AllDirectChoicesAreValidatedAndBrushesAreNotTruncated()
        {
            var c = Context();
            var validate = typeof(CHRISCommandGateway).GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Static);
            int total = 0;
            foreach (string category in new[] { "Brush", "Color", "Size", "Panels", "Move", "Turn" })
            {
                var choices = CHRISPanel.Choices(category, c);
                Assert.That(choices.Count, Is.GreaterThan(0)); total += choices.Count;
                foreach (var choice in choices)
                {
                    // Exercise the exact JObject serialized on the native approval path.
                    var wire = JObject.Parse(choice.Value.ToString());
                    Assert.DoesNotThrow(() => validate.Invoke(null, new object[] { wire, c }), choice.Key);
                    Assert.That(CHRISPanel.Summary(wire, c), Is.Not.EqualTo("Unsupported action"));
                }
            }
            Assert.That(total, Is.EqualTo(35));
            Assert.That(CHRISPanel.Choices("Brush", c).Count, Is.EqualTo(17));
            Assert.That(CHRISPanel.SameContext(c, (JObject)c.DeepClone()), Is.True);
            var approval = (JObject)c.DeepClone(); approval["expires_at"] = 100.0;
            Assert.That(CHRISPanel.ApprovalCurrent(approval, c, 99), Is.True);
            Assert.That(CHRISPanel.ApprovalCurrent(approval, c, 100), Is.False);
            foreach (string key in new[] { "host_session", "revision", "authority_epoch" })
            {
                var changed = (JObject)c.DeepClone(); changed[key] = key == "host_session" ? (JToken)"new" : new JValue(999);
                Assert.That(CHRISPanel.SameContext(c, changed), Is.False, key);
            }
            Assert.That(CHRISPanel.PassiveNativeUIHover(), Is.False, "No live UI must not bypass native interaction safeguards");
        }
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
        public void ConfirmWaitsForReleaseAndLocalStopClearsIt()
        {
            var obj = new GameObject("CHRIS deferred click test");
            try
            {
                var model = obj.AddComponent<CHRISPanel>(); model.Gateway = obj.AddComponent<CHRISCommandGateway>();
                Property(model, "Assistance", obj.AddComponent<CHRISAssistanceClient>());
                Property(model, "Popup", obj.AddComponent<CHRISNativePopup>());
                Property(model, "Mode", "Direct");
                Property(model, "ReviewedAction", CHRISPanel.Action("brush.size", "number", 0.3));
                Set(model, "m_ReviewContext", model.Gateway.Capture());
                model.ConfirmDirect(); Assert.That(model.WaitingForRelease, Is.True);
                typeof(CHRISPanel).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(model, null);
                Assert.That(model.WaitingForRelease, Is.True, "no initialized controller input means no dispatch");
                Assert.That(model.Gateway.LastDirectResult, Is.Null);
                Property(model.Assistance, "TaskId", null); Property(model.Assistance, "PendingRequest", null);
                long count = model.Gateway.StopCount; model.StopLocal();
                Assert.That(model.WaitingForRelease, Is.False); Assert.That(model.ReviewedAction, Is.Null);
                Assert.That(model.Gateway.StopCount, Is.EqualTo(count + 1));
                Assert.That(model.Notice, Does.Contain("STOP received"));
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

        [Test]
        public void AssistanceExplainsBlockedRequestsAndReviewDoesNotDispatch()
        {
            var obj = new GameObject("CHRIS pending task test");
            try
            {
                var client = obj.AddComponent<CHRISAssistanceClient>();
                var model = obj.AddComponent<CHRISPanel>(); Property(model, "Assistance", client);
                Property(client, "TaskId", "saved-task");
                Assert.That(client.CanStart, Is.False);
                Assert.That(client.StartBlockedReason, Does.Contain("Saved task"));
                var task = new JObject { ["task_id"] = "saved-task", ["status"] = "awaiting_approval",
                    ["reason"] = "Review the proposed change", ["approval"] = new JObject { ["approval_id"] = "pending" } };
                Property(client, "Task", task);
                foreach (bool polling in new[] { false, true })
                {
                    Property(client, "Busy", polling);
                    Assert.That(client.CanStart, Is.False);
                    Assert.That(client.StartBlockedReason, Does.Contain("Review proposal or Cancel task"));
                    Assert.That(client.CanReview, Is.True, "Read-only review is available during status polls");
                    model.ReviewProposal();
                    Assert.That(JToken.DeepEquals(model.ReviewedApproval, task["approval"]), Is.True);
                    Assert.That(model.ReviewedApproval, Is.Not.SameAs(task["approval"]));
                    Assert.That(model.WaitingForRelease, Is.False, "Opening review cannot queue approval");
                    Assert.That((string)task["status"], Is.EqualTo("awaiting_approval"));
                }
                Property(client, "CancelWanted", true);
                Assert.That(client.CanReview, Is.False); Assert.That(client.CanStart, Is.False);
                Assert.That(client.StartBlockedReason, Does.Contain("Cancellation unconfirmed"));
                Property(client, "CancelWanted", false); Property(client, "Busy", false);
                Property(client, "PendingRequest", new JObject { ["request_id"] = "uncertain" });
                Assert.That(client.StartBlockedReason, Does.Contain("Submission reply missing"));
                Property(client, "PendingRequest", null);
                foreach (string state in new[] { "paused", "unverified", "running" })
                {
                    task["status"] = state;
                    Assert.That(client.CanStart, Is.False); Assert.That(client.CanReview, Is.False);
                    Assert.That(client.StartBlockedReason, Does.Contain("Cancel task"));
                }
                task["status"] = "rejected";
                Assert.That(client.CanStart, Is.True); Assert.That(client.StartBlockedReason, Is.Null);
                foreach (string reason in new[] { "Direct palette owns control",
                    "Close the CHRIS palette with F8, then submit a new browser request" })
                {
                    task["reason"] = reason;
                    Assert.That(model.AssistanceStatus(), Does.Contain("Assistance mode"));
                    Assert.That(model.AssistanceStatus(), Does.Not.Contain("F8"));
                    Assert.That((string)task["reason"], Is.EqualTo(reason), "Historical records stay intact");
                }
                Assert.That(CHRISPanel.AssistanceReason("Other failure"), Is.EqualTo("Other failure"));
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }

        [MenuItem("CHRIS/Verify native UI (Edit mode)")]
        public static void VerifyInEditor()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play mode before running CHRIS editor checks.");
            RunChecks();
        }

        public static void Run()
        {
            try { RunChecks(); EditorApplication.Exit(0); }
            catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
        }
        static void RunChecks()
        {
            string output = Path.GetFullPath("Build/CHRISNativeUI");
            Directory.CreateDirectory(output);
            try
            {
                var tests = new TestCHRISNativeUI();
                tests.AllDirectChoicesAreValidatedAndBrushesAreNotTruncated();
                tests.UncertainSubmissionAndStopNeverCreateAnotherProposal();
                tests.ConfirmWaitsForReleaseAndLocalStopClearsIt();
                tests.NativeAssetsProvideMenuPopupKeyboardAndClickableButtons();
                tests.FloatingPanelStaysInWorldAndDragStopsOnRelease();
                tests.AssistanceExplainsBlockedRequestsAndReviewDoesNotDispatch();
                Render(output);
                File.WriteAllText(output + "/checks.txt", "PASS: native UI assets, 35 validated direct choices, context invalidation, cancellation/recovery interlocks, floating placement independent of hand/head, drag/release/stop, native collider layout and rendered review. No Play mode, HTTP, paid model calls or sketch changes.");
            }
            catch (Exception ex)
            { File.WriteAllText(output + "/checks.txt", "FAIL: " + ex); throw; }
        }
        static void Render(string output)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            RenderTexture rt = null; Texture2D image = null; Camera camera = null;
            try
            {
                var resources = CHRISUIResources.Load();
                var obj = UnityEngine.Object.Instantiate(resources.PopupPrefab); obj.name = "CHRIS layout verification";
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(obj, scene);
                var popup = obj.GetComponent<CHRISNativePopup>(); popup.BuildView();
                var modelObj = new GameObject("CHRIS preview model");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(modelObj, scene);
                var model = modelObj.AddComponent<CHRISPanel>(); model.Gateway = modelObj.AddComponent<CHRISCommandGateway>();
                var client = modelObj.AddComponent<CHRISAssistanceClient>(); Property(model, "Assistance", client);
                Property(model, "Mode", "Assistance");
                var approval = new JObject { ["approval_id"] = "preview", ["action_digest"] = new string('a', 64),
                    ["action"] = CHRISPanel.Action("brush.size", "number", 0.3) };
                var nativeContext = model.Gateway.Capture();
                foreach (var key in new[] { "host_session", "revision", "authority_epoch" }) approval[key] = nativeContext[key];
                approval["expires_at"] = CHRISCommandGateway.Now + 300;
                Property(client, "Task", new JObject { ["task_id"] = "preview", ["status"] = "awaiting_approval", ["reason"] = "Review the proposed change", ["approval"] = approval, ["context"] = Context() });
                Property(client, "TaskId", "preview");
                Property(model, "ReviewedApproval", approval); Set(popup, "m_Model", model);
                typeof(CHRISNativePopup).GetMethod("Draw", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(popup, null);
                Physics.SyncTransforms();
                foreach (var button in obj.GetComponentsInChildren<CHRISNativeButton>())
                {
                    Assert.That(button.GetComponent<BoxCollider>(), Is.Not.Null);
                    Assert.That(button.Label, Is.Not.Null);
                    var collider = button.GetComponent<BoxCollider>();
                    Assert.That(collider.Raycast(new Ray(collider.bounds.center - Vector3.forward * 2, Vector3.forward), out _, 3), Is.True, button.name);
                }
                var cameraObj = new GameObject("CHRIS preview camera"); camera = cameraObj.AddComponent<Camera>();
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(cameraObj, scene); camera.scene = scene;
                cameraObj.AddComponent<UniversalAdditionalCameraData>(); camera.orthographic = true; camera.orthographicSize = 2.25f;
                camera.transform.position = new Vector3(0, 0, -10); camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.08f, 0.08f, 0.08f); camera.nearClipPlane = 0.01f; camera.farClipPlane = 20;
                rt = new RenderTexture(1100, 1200, 24); rt.Create(); camera.targetTexture = rt;
                foreach (var text in obj.GetComponentsInChildren<TextMeshPro>()) text.ForceMeshUpdate();
                camera.Render();
                RenderTexture.active = rt; image = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); image.Apply();
                File.WriteAllBytes(output + "/native-review.png", image.EncodeToPNG());
                RenderTexture.active = null;
                Property(model, "ReviewedApproval", null);
                typeof(CHRISNativePopup).GetMethod("Draw", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(popup, null);
                var pendingButtons = obj.GetComponentsInChildren<CHRISNativeButton>();
                Assert.That(pendingButtons.Single(b => b.name == "Choice 0").Label.text, Is.EqualTo("Review proposal"));
                Assert.That(pendingButtons.Single(b => b.name == "Choice 0").IsAvailable(), Is.True);
                Assert.That(pendingButtons.Single(b => b.name == "Choice 2").IsAvailable(), Is.False);
                Assert.That(obj.GetComponentsInChildren<TextMeshPro>().Any(t => t.text.Contains("Finish this proposal")), Is.True);
                foreach (var text in obj.GetComponentsInChildren<TextMeshPro>()) text.ForceMeshUpdate();
                camera.Render(); RenderTexture.active = rt; image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); image.Apply();
                File.WriteAllBytes(output + "/native-pending.png", image.EncodeToPNG()); RenderTexture.active = null;
                Property(model, "ReviewedApproval", null); Property(model, "Mode", "Direct");
                typeof(CHRISNativePopup).GetMethod("Draw", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(popup, null);
                foreach (var text in obj.GetComponentsInChildren<TextMeshPro>()) text.ForceMeshUpdate();
                camera.Render(); RenderTexture.active = rt; image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); image.Apply();
                File.WriteAllBytes(output + "/native-direct.png", image.EncodeToPNG()); RenderTexture.active = null;
                obj.SetActive(false);
                var menu = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/PopUps/PopUpWindow_Panels.prefab"));
                menu.transform.position = Vector3.zero; menu.transform.rotation = Quaternion.identity;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(menu, scene);
                typeof(CHRISMenuEntry).GetMethod("Start", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(menu.GetComponent<CHRISMenuEntry>(), null);
                camera.orthographicSize = 1.15f;
                foreach (var text in menu.GetComponentsInChildren<TextMeshPro>()) text.ForceMeshUpdate();
                camera.Render(); RenderTexture.active = rt; image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); image.Apply();
                File.WriteAllBytes(output + "/native-menu.png", image.EncodeToPNG()); RenderTexture.active = null;
            }
            finally
            {
                if (camera != null) camera.targetTexture = null;
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
