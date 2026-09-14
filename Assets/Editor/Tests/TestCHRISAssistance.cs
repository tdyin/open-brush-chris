// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace TiltBrush
{
    public class TestCHRISAssistance
    {
        internal static readonly JArray Exported = new JArray();
        const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static void Set(object target, string name, object value)
        {
            var type = target.GetType();
            FieldInfo field = null;
            while (type != null && (field = type.GetField(name, InstanceFlags)) == null) type = type.BaseType;
            field.SetValue(target, value);
        }
        internal static void Property(object target, string name, object value) => Set(target, "<" + name + ">k__BackingField", value);
        static object Get(object target, string name) => typeof(CHRISCommandGateway).GetField(name, InstanceFlags).GetValue(target);
        static object Static(string name, params object[] args) => typeof(CHRISCommandGateway).GetMethod(name,
            BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
        internal static void Call(object target, string name) => target.GetType().GetMethod(name, InstanceFlags).Invoke(target, null);
        internal static JObject Action(string tool, string parameter, JToken value, string id = null) => new JObject {
            ["action_id"] = id ?? Guid.NewGuid().ToString("N"), ["version"] = 1, ["tool"] = tool,
            ["number"] = null, ["text"] = null, ["vector"] = null, ["visible"] = null,
            [parameter] = parameter == "number" ? new JValue((double)value) :
                parameter == "vector" ? new JArray(((JArray)value).Select(v => (double)v)) : value };
        static JToken Canonical(JToken value) => value is JObject obj ? new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new JProperty(p.Name, Canonical(p.Value)))) : value is JArray array ? new JArray(array.Select(Canonical)) : value.DeepClone();
        internal static JObject Envelope(CHRISGatewayTestHost host, JArray actions, string task = null)
        {
            task = task ?? Guid.NewGuid().ToString("N");
            var context = host.Capture();
            string payload = Canonical(new JObject { ["actions"] = actions }).ToString(Formatting.None);
            var approval = new JObject { ["actions"] = JArray.Parse(((JObject)JObject.Parse(payload))["actions"].ToString()),
                ["approval_id"] = Guid.NewGuid().ToString("N"), ["task_id"] = task,
                ["action_digest"] = CHRISCommandGateway.Hash(payload), ["host_session"] = context["host_session"],
                ["revision"] = context["revision"], ["authority_epoch"] = context["authority_epoch"],
                ["expires_at"] = host.Clock + 30, ["scope"] = "control_segment" };
            return new JObject { ["command_id"] = task + "-1", ["task_id"] = task, ["payload"] = payload,
                ["payload_digest"] = CHRISCommandGateway.Hash(payload), ["approval"] = approval };
        }
        static JArray ThreeActions() => new JArray(Action("brush.size", "number", 0.3),
            Action("brush.color", "text", "#0000FF"), Action("view.turn", "number", 15));
        static void Host(Action<CHRISGatewayTestHost> test)
        {
            var obj = new GameObject("CHRIS deterministic native host");
            try { var host = obj.AddComponent<CHRISGatewayTestHost>(); host.Initialize(); test(host); }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }
        static JObject Lookup(CHRISGatewayTestHost host, JObject envelope)
        {
            var requestType = typeof(CHRISCommandGateway).GetNestedType("Request", BindingFlags.NonPublic);
            var request = Activator.CreateInstance(requestType, true);
            Set(request, "Method", "GET"); Set(request, "Path", "/chris/commands/" + (string)envelope["command_id"]);
            return (JObject)typeof(CHRISCommandGateway).GetMethod("Route", InstanceFlags).Invoke(host, new[] { request });
        }
        static void Result(JObject result, string status, int completed, int count)
        {
            Assert.That((string)result["status"], Is.EqualTo(status));
            Assert.That((int)result["completed"], Is.EqualTo(completed));
            Assert.That((int)result["remaining"], Is.EqualTo(count - completed));
        }

        [Test]
        public void OrderedSegmentReadbackAndDuplicateRequestsNeverReplay()
        {
            Host(host =>
            {
                var envelope = Envelope(host, ThreeActions());
                Result(host.Submit(envelope), "queued", 0, 3);
                Assert.That(host.Applied, Is.EqualTo(1));
                for (int completed = 1; completed <= 3; completed++)
                {
                    host.Tick();
                    Result(Lookup(host, envelope), completed == 3 ? "succeeded" : "queued", completed, 3);
                    Assert.That(host.Applied, Is.EqualTo(completed), "Readback precedes the next dispatch");
                    if (completed < 3)
                    {
                        Assert.That((string)host.Capture()["active_task"], Is.EqualTo((string)envelope["task_id"]));
                        host.Tick();
                    }
                }
                Assert.That(host.Capture()["active_task"].Type, Is.EqualTo(JTokenType.Null));
                Result(host.Submit((JObject)envelope.DeepClone()), "succeeded", 3, 3);
                Assert.That(host.Applied, Is.EqualTo(3));
                var changed = (JObject)envelope.DeepClone(); changed["payload"] = "{}";
                Assert.Throws<ArgumentException>(() => host.Submit(changed));
                Exported.Add(new JObject { ["context"] = host.Capture(), ["envelope"] = envelope,
                    ["result"] = Lookup(host, envelope) }.DeepClone());
            });
        }

        [Test]
        public void StopTakeoverExpiryAndUnknownReadbackPreserveCompletedCount()
        {
            foreach (string reason in new[] { "stop", "manual", "busy", "expiry", "failure" })
                Host(host =>
                {
                    var envelope = Envelope(host, ThreeActions());
                    if (reason == "expiry") envelope["approval"]["expires_at"] = host.Clock + 1;
                    host.Submit(envelope); host.Tick();
                    Result(Lookup(host, envelope), "queued", 1, 3);
                    switch (reason)
                    {
                        case "stop": host.Stop(); break;
                        case "manual": host.State["brush_color"] = "#FF0000"; host.Tick(); break;
                        case "busy": host.Busy = true; host.Tick(); break;
                        case "expiry": host.Clock += 2; host.Tick(); break;
                        case "failure": host.ThrowOnAction = 2; host.Tick(); break;
                    }
                    string status = reason == "failure" ? "unverified" : "partial";
                    Result(host.Submit(envelope), status, 1, 3);
                    Assert.That(host.Applied, Is.EqualTo(reason == "failure" ? 2 : 1));
                    host.Tick();
                    Result(Lookup(host, envelope), status, 1, 3);
                    Exported.Add(new JObject { ["context"] = host.Capture(), ["envelope"] = envelope,
                        ["result"] = Lookup(host, envelope) }.DeepClone());
                });
            Host(host =>
            {
                var envelope = Envelope(host, ThreeActions()); host.Submit(envelope); host.Stop();
                Result(Lookup(host, envelope), "unverified", 0, 3);
            });
            Host(host =>
            {
                host.SkipMutation = true;
                var envelope = Envelope(host, ThreeActions()); host.Submit(envelope);
                Set(Get(host, "m_Pending"), "Deadline", -1f);
                host.Tick(); Result(Lookup(host, envelope), "unverified", 0, 3);
                Assert.That(host.Applied, Is.EqualTo(1));
            });
        }

        [Test]
        public void EntireSegmentAndAuthorityAreValidatedBeforeMutation()
        {
            foreach (string invalid in new[] { "empty", "six", "duplicate", "unknown", "extra", "missing", "number", "legacy", "digest", "stale", "expiry", "cancelled" })
                Host(host =>
                {
                    var actions = ThreeActions();
                    if (invalid == "empty") actions.Clear();
                    if (invalid == "six") for (int i = 0; i < 3; i++) actions.Add(Action("brush.size", "number", 0.2));
                    if (invalid == "duplicate") actions[1]["action_id"] = actions[0]["action_id"];
                    if (invalid == "unknown") actions[1]["tool"] = "file.delete";
                    if (invalid == "extra") actions[1]["other"] = true;
                    if (invalid == "missing") ((JObject)actions[1]).Remove("visible");
                    if (invalid == "number") actions[0]["number"] = "0.3";
                    var envelope = Envelope(host, actions);
                    if (invalid == "legacy") envelope["approval"]["scope"] = "single_action";
                    if (invalid == "digest") envelope["payload_digest"] = new string('0', 64);
                    if (invalid == "stale") host.Stop();
                    if (invalid == "expiry") envelope["approval"]["expires_at"] = host.Clock - 1;
                    if (invalid == "cancelled") ((HashSet<string>)Get(host, "m_InvalidTasks")).Add((string)envelope["task_id"]);
                    try { Assert.That((string)host.Submit(envelope)["status"], Is.EqualTo("rejected"), invalid); }
                    catch (ArgumentException) { }
                    Assert.That(host.Applied, Is.Zero, invalid);
                });
            Assert.Throws<TargetInvocationException>(() => Static("Parse", "{\"a\":1,\"a\":2}"));
            Assert.Throws<TargetInvocationException>(() => Static("Parse", "{} {}"));
            Assert.That((string)((JObject)Static("Parse", "{\"text\":\"2026-09-13T00:00:00Z\"}"))["text"], Is.EqualTo("2026-09-13T00:00:00Z"));
        }

        [Test]
        public void DelayedPanelsAndNumericReadbackRetainTakeoverProtection()
        {
            foreach (bool unrelated in new[] { false, true })
                Host(host =>
                {
                    host.DelayPanel = true;
                    var panel = Action("panel.visibility", "text", "Brush"); panel["visible"] = true;
                    var envelope = Envelope(host, new JArray(panel, Action("brush.size", "number", 0.3)));
                    host.Submit(envelope); host.Tick(); Result(Lookup(host, envelope), "queued", 0, 2);
                    host.State["panels"]["Brush"] = true;
                    if (unrelated) host.State["brush_color"] = "#FF0000";
                    host.Tick();
                    Result(Lookup(host, envelope), unrelated ? "unverified" : "queued", unrelated ? 0 : 1, 2);
                    Assert.That(host.Applied, Is.EqualTo(1));
                });
            Host(host =>
            {
                var before = host.Capture();
                var noise = (JObject)before.DeepClone(); noise["brush_size"] = 0.50000001;
                noise["scene_position"][0] = 0.00000001; noise["scene_rotation"] = new JArray(0, 0, 0, -1);
                Assert.That((bool)Static("MateriallyChanged", before, noise), Is.False);
                foreach (string field in new[] { "brush_size", "scene_scale", "brush_color", "ready", "stroke_active" })
                {
                    var changed = (JObject)before.DeepClone();
                    if (field == "brush_color") changed[field] = "#FF0000";
                    else if (field == "ready" || field == "stroke_active") changed[field] = !(bool)changed[field];
                    else changed[field] = (double)changed[field] + 0.001;
                    Assert.That((bool)Static("MateriallyChanged", before, changed), Is.True, field);
                }
                var initial = Quaternion.Euler(23, 67, -9); before["scene_rotation"] = Q(initial);
                var turn = Action("view.turn", "number", 15);
                var expected = initial * Quaternion.AngleAxis(-15, Vector3.up);
                foreach (float error in new[] { 0f, 0.005f, 0.02f, 0.1f, 1f })
                {
                    var after = (JObject)before.DeepClone(); after["scene_rotation"] = Q(expected * Quaternion.AngleAxis(error, Vector3.up));
                    Assert.That(host.Readback(turn, before, after), Is.EqualTo(error <= 0.01f));
                }
                foreach (float scale in new[] { 0.999998f, 1f, 1.000002f })
                {
                    var after = (JObject)before.DeepClone(); after["scene_rotation"] = new JArray(expected.x * scale, expected.y * scale, expected.z * scale, expected.w * scale);
                    Assert.That(host.Readback(turn, before, after), Is.True);
                }
            });
        }
        static JArray Q(Quaternion q) => new JArray(q.x, q.y, q.z, q.w);

        [Test]
        public void SignedViewpointCommandsVerifyInverseSceneMotion()
        {
            foreach (int sign in new[] { -1, 1 })
                Host(host =>
                {
                    host.State["scene_position"] = new JArray(1.0, 2.0, 3.0);
                    host.State["scene_rotation"] = Q(Quaternion.Euler(0, 90, 0));
                    var before = host.Capture();
                    var move = Action("view.move", "vector", new JArray(sign * 0.25, 0.0, sign * -0.25));
                    var turn = Action("view.turn", "number", sign * 15);
                    var envelope = Envelope(host, new JArray(move, turn));
                    host.Submit(envelope);
                    for (int i = 0; i < 4; i++) host.Tick();
                    var result = Lookup(host, envelope);
                    Result(result, "succeeded", 2, 2);
                    var after = host.Capture();
                    Assert.That((double)after["scene_position"][0], Is.EqualTo(sign > 0 ? 0.75 : 1.25));
                    Assert.That((double)after["scene_position"][1], Is.EqualTo(2.0));
                    Assert.That((double)after["scene_position"][2], Is.EqualTo(sign > 0 ? 3.25 : 2.75));
                    var rotation = after["scene_rotation"];
                    var actual = new Quaternion((float)rotation[0], (float)rotation[1], (float)rotation[2], (float)rotation[3]);
                    Assert.That(Quaternion.Angle(actual, Quaternion.Euler(0, sign > 0 ? 75 : 105, 0)), Is.LessThan(0.01f));
                    var wrongDirection = (JObject)after.DeepClone();
                    wrongDirection["scene_position"] = new JArray(sign > 0 ? 1.25 : 0.75, 2.0, sign > 0 ? 2.75 : 3.25);
                    wrongDirection["scene_rotation"] = Q(Quaternion.Euler(0, sign > 0 ? 105 : 75, 0));
                    Assert.That(host.Readback(move, before, wrongDirection), Is.False);
                    Assert.That(host.Readback(turn, before, wrongDirection), Is.False);
                    Exported.Add(new JObject { ["before"] = before, ["context"] = after,
                        ["envelope"] = envelope, ["result"] = result }.DeepClone());
                });
        }

        [Test]
        public void PythonSegmentsValidateAndAllSixActionFamiliesExecute()
        {
            var fixtures = JArray.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Editor/Tests/Fixtures/CHRISControlSegments.json")));
            foreach (JObject fixture in fixtures)
                Host(host =>
                {
                    var envelope = (JObject)fixture["envelope"].DeepClone();
                    var current = host.Capture();
                    foreach (string key in new[] { "host_session", "revision", "authority_epoch" }) envelope["approval"][key] = current[key];
                    envelope["approval"]["expires_at"] = host.Clock + 30;
                    var actions = (JArray)envelope["approval"]["actions"];
                    CHRISCommandGateway.ValidateActions(actions, (JObject)fixture["context"]);
                    host.Submit(envelope);
                    for (int i = 0; i < actions.Count * 2; i++) host.Tick();
                    Result(Lookup(host, envelope), "succeeded", actions.Count, actions.Count);
                    Assert.That(host.Capture()["direct_palette_open"], Is.Null);
                    Exported.Add(new JObject { ["context"] = host.Capture(), ["envelope"] = envelope, ["result"] = Lookup(host, envelope) }.DeepClone());
                });
        }

        [Test]
        public void VoiceFinalizationCancellationAndProviderFailuresNeverAuthorizeCommands()
        {
            var obj = new GameObject("CHRIS simulated speech callbacks");
            try
            {
                var voice = obj.AddComponent<CHRISVoiceInput>();
                voice.Devices = () => Array.Empty<CHRISMicrophoneDevice>();
                int finalized = 0; voice.Finalized += _ => finalized++;
                long id = voice.Session.Begin(); voice.Receive(id, "ready", "");
                voice.Receive(id, "error", "Microphone unavailable. Select an available Windows input device.");
                Assert.That(voice.Session.State, Is.EqualTo(CHRISVoiceSession.Phase.Failed));
                Assert.That(voice.Status, Does.Contain("Microphone unavailable"));
                id = voice.Session.Begin(); voice.Session.Ready(id);
                voice.Receive(id, "partial", "make the brush");
                Assert.That(voice.Session.Transcript, Is.EqualTo("make the brush"));
                voice.Receive(id, "final", "make the brush blue");
                Assert.That(finalized, Is.Zero, "Only an explicit Finish can finalize");
                voice.Session.RequestFinish(); voice.CancelRecording();
                voice.Receive(id, "final", "cancelled text"); Assert.That(finalized, Is.Zero);
                id = voice.Session.Begin(); voice.Session.Ready(id); voice.Session.RequestFinish();
                long newer = voice.Session.Begin(); voice.Receive(id, "partial", "old partial");
                voice.Receive(id, "final", "old final"); Assert.That(voice.Session.Transcript, Is.Empty);
                voice.Session.Ready(newer); voice.Session.RequestFinish(); voice.Receive(newer, "final", "make brush blue");
                voice.Receive(newer, "final", "duplicate final"); Assert.That(finalized, Is.EqualTo(1));
                foreach (string invalid in new[] { "", "  ", new string('x', 2001), "invalid\0text" })
                {
                    id = voice.Session.Begin(); voice.Session.Ready(id); voice.Session.RequestFinish(); voice.Receive(id, "final", invalid);
                    Assert.That(voice.Session.State, Is.EqualTo(CHRISVoiceSession.Phase.Failed));
                    Assert.That(finalized, Is.EqualTo(1));
                }
                id = voice.Session.Begin(); voice.Receive(id, "error", "Provider unavailable");
                voice.Receive(id, "final", "late result"); Assert.That(finalized, Is.EqualTo(1));
                id = voice.Session.Begin(); voice.Session.Ready(id); voice.Session.RequestFinish(); Call(voice, "OnDisable");
                voice.Receive(id, "final", "disabled result"); Assert.That(finalized, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }

        internal static JObject TaskFor(CHRISGatewayTestHost host, string summary = "1. Set brush size to 0.3 (0 to 1 scale).")
        {
            var envelope = Envelope(host, new JArray(Action("brush.size", "number", 0.3)));
            return new JObject { ["task_id"] = envelope["task_id"], ["status"] = "awaiting_approval", ["reason"] = "Review the commands",
                ["approval"] = envelope["approval"], ["actions"] = envelope["approval"]["actions"], ["context"] = host.Capture(), ["summary"] = summary };
        }

        [Test]
        public void CorrectionsInvalidateReviewAndRejectLegacyOrMalformedSummaries()
        {
            Host(host =>
            {
                var model = host.gameObject.AddComponent<CHRISPanel>(); model.Gateway = host; Call(model, "Start");
                Property(model.Assistance, "TaskId", null); Property(model.Assistance, "PendingRequest", null);
                var popup = host.gameObject.AddComponent<CHRISNativePopup>(); Property(model, "Popup", popup);
                var task = TaskFor(host); Property(model.Assistance, "Task", task); Property(model.Assistance, "Busy", true);
                model.Refresh();
                Assert.That(model.ReviewText, Is.EqualTo((string)task["summary"]));
                Assert.That(model.ReviewedApproval, Is.Not.SameAs(task["approval"]));
                Property(model.Assistance, "Busy", false);
                model.Decide(true); Assert.That(model.WaitingForRelease, Is.True);
                long first = model.BeginCorrection(); Assert.That(model.WaitingForRelease, Is.False);
                Assert.That(model.ReviewedApproval, Is.Null);
                long replacement = model.BeginCorrection(); model.AcceptFinalTranscript(first, "superseded request");
                Assert.That(model.HasReplacement, Is.False);
                model.AcceptFinalTranscript(replacement, "make my brush blue"); Assert.That(model.HasReplacement, Is.True);
                model.StopLocal(); Assert.That(model.HasReplacement, Is.False);
                model.AcceptFinalTranscript(replacement, "late correction"); Assert.That(model.HasReplacement, Is.False);
                foreach (JToken bad in new JToken[] { JValue.CreateNull(), "", new string('x', 4001), new JObject() })
                {
                    var malformed = TaskFor(host); malformed["summary"] = bad;
                    Property(model.Assistance, "Task", malformed); model.Refresh();
                    Assert.That(model.Assistance.CanReview, Is.False); Assert.That(model.ReviewedApproval, Is.Null);
                }
                var legacy = TaskFor(host); legacy["approval"]["scope"] = "single_action";
                Property(model.Assistance, "Task", legacy); model.Refresh(); Assert.That(model.Assistance.CanReview, Is.False);
                Property(model.Assistance, "TaskId", "old");
                Assert.That(model.Assistance.StartBlockedReason, Does.Contain("old or invalid"));
                Property(model.Assistance, "TaskId", null);
                var partial = new JObject { ["status"] = "partial" };
                Assert.That(CHRISAssistanceClient.Terminal(partial), Is.True);
                partial["status"] = "unverified"; Assert.That(CHRISAssistanceClient.Terminal(partial), Is.False);
                string longSummary = string.Join("\n", Enumerable.Repeat(new string('x', 200), 5));
                var pages = CHRISNativePopup.ReviewPages(longSummary);
                Assert.That(pages.Length, Is.GreaterThan(1)); Assert.That(string.Concat(pages), Is.EqualTo(longSummary));
                Assert.That(pages.All(p => p.Length <= 320), Is.True);
            });
        }
    }
}
