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
        public static void Run()
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
                Render(output);
                File.WriteAllText(output + "/checks.txt", "PASS: native UI assets, 35 validated direct choices, context invalidation, cancellation/recovery interlocks, native collider layout and rendered review. No Play mode, HTTP, paid model calls or sketch changes.");
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            { File.WriteAllText(output + "/checks.txt", "FAIL: " + ex); Debug.LogException(ex); EditorApplication.Exit(1); }
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
