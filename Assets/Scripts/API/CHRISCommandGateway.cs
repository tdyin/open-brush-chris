// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TiltBrush
{
    // All Unity access and mutation happens on the main thread. The existing listener stays loopback.
    [DefaultExecutionOrder(10000)]
    public class CHRISCommandGateway : MonoBehaviour
    {
        private sealed class Request
        {
            public string Method, Path, Body;
            public JObject Reply;
            public bool Abandoned;
            public readonly object Gate = new object();
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim();
        }
        private sealed class Record
        {
            public JObject Envelope, Before, Result;
            public string Wire;
            public float Deadline;
            public int Completed;
            public bool AwaitingReadback;
            public JArray Actions => (JArray)Envelope["approval"]["actions"];
            public JObject CurrentAction => (JObject)Actions[Completed];
        }
        private readonly Queue<Request> m_Requests = new Queue<Request>();
        private readonly Dictionary<string, Record> m_Records = new Dictionary<string, Record>();
        private readonly HashSet<string> m_InvalidTasks = new HashSet<string>();
        private readonly string m_Session = Guid.NewGuid().ToString("N");
        private long m_Revision, m_Epoch;
        private string m_ActiveTask;
        private JObject m_ObservedContext;
        private Record m_Pending;
        private bool m_Closed, m_Registered;
        private HttpServer m_Server;
        public string Status { get; private set; } = "Waiting for native host";
        public double LastStopMilliseconds { get; private set; }
        public long StopCount { get; private set; }
        public event Action Stopped;
        private int m_StopLogs;
        public static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        void Start()
        {
            m_Server = App.HttpServer;
            if (m_Server == null) return;
            m_Server.AddHttpHandler("/chris/", (Action<HttpListenerContext>)HandleHttp);
            m_Registered = true;
            var panel = GetComponent<CHRISPanel>() ?? gameObject.AddComponent<CHRISPanel>();
            panel.Gateway = this;
        }

        void HandleHttp(HttpListenerContext ctx)
        {
            JObject reply;
            if (ctx.Request.RemoteEndPoint == null || !IPAddress.IsLoopback(ctx.Request.RemoteEndPoint.Address) ||
                ctx.Request.Headers["Origin"] != null || ctx.Request.ContentLength64 > 16384 ||
                (ctx.Request.HttpMethod != "GET" && ctx.Request.HttpMethod != "POST") ||
                (ctx.Request.HttpMethod == "POST" && (ctx.Request.ContentLength64 < 0 ||
                 !string.Equals((ctx.Request.ContentType ?? "").Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))))
            { ctx.Response.StatusCode = 400; reply = Error("Invalid request"); }
            else
            {
                var request = new Request { Method = ctx.Request.HttpMethod,
                    Path = ctx.Request.Url.AbsolutePath };
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    var body = new char[16385];
                    int count = reader.ReadBlock(body, 0, body.Length);
                    if (count > 16384) request.Reply = Error("Request body too large");
                    request.Body = new string(body, 0, count);
                }
                lock (m_Requests)
                {
                    if (m_Requests.Count >= 32 || m_Closed) request.Reply = Error("Gateway unavailable");
                    else if (request.Reply == null) m_Requests.Enqueue(request);
                }
                if (request.Reply == null && !request.Done.Wait(2000))
                {
                    // If the main thread already started, a timeout has an uncertain outcome.
                    lock (request.Gate) request.Abandoned = true;
                }
                reply = request.Reply ?? Error("Request timed out; query command result before recovery");
                if (reply["error"] != null) ctx.Response.StatusCode = 409;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(reply.ToString(Formatting.None));
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        static JObject Error(string reason) => new JObject { ["error"] = reason };
        public static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value)))
                    .Replace("-", "").ToLowerInvariant();
        }
        static bool Id(string value) => value != null && Regex.IsMatch(value, "\\A[A-Za-z0-9_-]{1,80}\\z");
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static void Require(bool valid, string reason)
        { if (!valid) throw new ArgumentException(reason); }

        public JObject Capture()
        {
            Observe(true);
            return ReadContext();
        }

        protected virtual JObject ReadContext()
        {
            var pointer = m_Closed || App.CurrentState != App.AppState.Standard || PointerManager.m_Instance == null
                ? null : PointerManager.m_Instance.MainPointer;
            bool ready = !m_Closed && m_Registered && App.CurrentState == App.AppState.Standard &&
                pointer != null && pointer.CurrentBrush != null && App.Scene != null &&
                PanelManager.m_Instance != null && BrushCatalog.m_Instance != null &&
                !BrushCatalog.m_Instance.IsLoading && SketchControlsScript.m_Instance != null &&
                App.BrushColor != null && SceneSettings.m_Instance != null &&
                !SceneSettings.m_Instance.IsTransitioning && PanelManager.m_Instance.StandardActive();
            var c = new JObject { ["host_session"] = m_Session, ["revision"] = m_Revision,
                ["captured_at"] = Now, ["authority_epoch"] = m_Epoch, ["ready"] = ready,
                ["stroke_active"] = !ready || PointerManager.MainPointerIsPainting(),
                ["brush_id"] = null, ["brush_size"] = null, ["brush_color"] = null,
                ["brushes"] = new JObject(), ["panels"] = new JObject(),
                ["scene_position"] = null, ["scene_rotation"] = null, ["scene_scale"] = null,
                ["active_task"] = m_ActiveTask == null ? JValue.CreateNull() : new JValue(m_ActiveTask),
                ["unknown"] = new JArray("active_layer", "selected_model", "physical_scale", "generation") };
            if (!ready) return c;
            c["brush_id"] = pointer.CurrentBrush.m_Guid.ToString();
            c["brush_size"] = pointer.BrushSize01;
            c["brush_color"] = "#" + ColorUtility.ToHtmlStringRGB(pointer.GetCurrentColor());
            foreach (var brush in BrushCatalog.m_Instance.AllBrushes.Where(b => !b.m_HiddenInGui)
                         .OrderBy(b => b.m_Guid.ToString()))
                ((JObject)c["brushes"])[brush.m_Guid.ToString()] = brush.Description;
            foreach (var name in new[] { "Brush", "Color" })
            {
                var panel = FindPanel(name);
                if (panel != null) ((JObject)c["panels"])[name] = PanelVisible(panel);
            }
            var pose = App.Scene.Pose;
            c["scene_position"] = new JArray(pose.translation.x, pose.translation.y, pose.translation.z);
            c["scene_rotation"] = new JArray(pose.rotation.x, pose.rotation.y, pose.rotation.z, pose.rotation.w);
            c["scene_scale"] = pose.scale;
            return c;
        }

        static BasePanel FindPanel(string name)
        {
            if (PanelManager.m_Instance == null) return null;
            return PanelManager.m_Instance.GetAllPanels().Select(p => p.m_Panel)
                .FirstOrDefault(p => p != null && p.Type.ToString() == name &&
                    PanelManager.m_Instance.IsPanelAvailable(p));
        }
        static bool PanelVisible(BasePanel panel) => panel.gameObject.activeInHierarchy &&
            (panel.WidgetSibling == null || panel.WidgetSibling.Showing);

        public static bool NativeInteractionBusy()
        {
            var controls = SketchControlsScript.m_Instance;
            return (CHRISPanel.Instance?.Popup?.GetParentPanel() as CHRISFloatingPanel)?.IsDragging == true ||
                controls == null || (controls.IsUserInteractingWithUI() && !CHRISPanel.PassiveNativeUIHover()) ||
                controls.IsUserInteractingWithAnyWidget() || controls.IsUserGrabbingWorld();
        }

        // Compare material native state, not serialized float text. Keep the last accepted
        // baseline until a threshold is crossed so small deliberate changes accumulate.
        static bool NearVector(JToken a, JToken b, double tolerance)
        {
            if (!(a is JArray av) || !(b is JArray bv)) return JToken.DeepEquals(a, b);
            if (av.Count != bv.Count) return false;
            double squared = 0;
            for (int i = 0; i < av.Count; i++)
            { double d = (double)av[i] - (double)bv[i]; squared += d * d; }
            return Finite(squared) && squared <= tolerance * tolerance;
        }
        static bool NearNumber(JToken a, JToken b, double tolerance)
        {
            if (!NumberValue(a) || !NumberValue(b)) return JToken.DeepEquals(a, b);
            return Math.Abs((double)a - (double)b) <= tolerance;
        }
        static bool NearRotation(JToken a, JToken b)
        {
            if (!(a is JArray av) || !(b is JArray bv)) return JToken.DeepEquals(a, b);
            if (av.Count != 4 || bv.Count != 4) return false;
            double dot = 0, aa = 0, bb = 0;
            for (int i = 0; i < 4; i++)
            { double x = (double)av[i], y = (double)bv[i]; dot += x * y; aa += x * x; bb += y * y; }
            if (aa <= 0 || bb <= 0 || !Finite(dot)) return false;
            // Normalizing in double precision avoids spurious Quaternion.Angle differences.
            double cosine = Math.Min(1, Math.Abs(dot) / Math.Sqrt(aa * bb));
            return 2 * Math.Acos(cosine) * 180 / Math.PI <= 0.01;
        }
        static bool MateriallyChanged(JObject before, JObject after)
        {
            foreach (var key in new[] { "host_session", "ready", "stroke_active", "brush_id", "brush_color", "brushes", "panels", "unknown" })
                if (!JToken.DeepEquals(before[key], after[key])) return true;
            double scale = NumberValue(before["scene_scale"]) ? Math.Abs((double)before["scene_scale"]) : 1;
            return !NearNumber(before["brush_size"], after["brush_size"], 0.0001) ||
                !NearVector(before["scene_position"], after["scene_position"], 0.0001) ||
                !NearRotation(before["scene_rotation"], after["scene_rotation"]) ||
                !NearNumber(before["scene_scale"], after["scene_scale"], Math.Max(0.0000001, scale * 0.00001));
        }
        static bool ExpectedPendingChange(Record record, JObject current)
        {
            if (!record.AwaitingReadback) return false;
            var action = record.CurrentAction;
            // Panel visibility may change on a later animation frame. Other setters are
            // synchronous and have already established their baseline in Apply/Observe(false).
            if ((string)action["tool"] != "panel.visibility" ||
                (bool?)current["panels"]?[(string)action["text"]] != (bool)action["visible"]) return false;
            var expected = (JObject)record.Before.DeepClone();
            expected["panels"][(string)action["text"]] = action["visible"];
            return !MateriallyChanged(expected, current);
        }
        void Observe(bool manual)
        {
            var context = ReadContext();
            if (m_ObservedContext == null) { m_ObservedContext = context; return; }
            if (MateriallyChanged(m_ObservedContext, context))
            {
                m_Revision++;
                if (manual && !(m_Pending != null && ExpectedPendingChange(m_Pending, context)))
                    Stop("Manual state changed; pending work cancelled", announce: m_Pending != null);
                m_ObservedContext = context;
            }
            else if (!manual) m_ObservedContext = context;
        }

        void Update()
        {
            if (m_Closed) return;
            Observe(true);
            if (m_Pending != null && IsNativeInteractionBusy()) Stop("Native interaction took control");
            AdvanceSegment();
            // Bound request work per frame so an HTTP caller cannot starve native controls.
            for (int i = 0; i < 4; ++i)
            {
                Request request;
                lock (m_Requests)
                { if (m_Requests.Count == 0) break; request = m_Requests.Dequeue(); }
                lock (request.Gate)
                {
                    if (!request.Abandoned)
                    {
                        try { request.Reply = Route(request); }
                        catch (Exception) { request.Reply = Error("Rejected invalid or unavailable operation"); }
                    }
                    request.Done.Set();
                }
            }
        }

        protected virtual double AuthorityTime => Now;

        protected virtual bool IsNativeInteractionBusy() => NativeInteractionBusy();

        void AdvanceSegment()
        {
            var record = m_Pending;
            if (record == null) return;
            var current = ReadContext();
            if (!record.AwaitingReadback)
            {
                if ((double)record.Envelope["approval"]["expires_at"] <= AuthorityTime)
                {
                    FinishSegment(record, record.Completed > 0 ? "partial" : "rejected", "Approval expired; remaining actions stopped");
                    return;
                }
                if (!(bool)current["ready"] || (bool)current["stroke_active"] || MateriallyChanged(record.Before, current))
                {
                    FinishSegment(record, record.Completed > 0 ? "partial" : "rejected", "Native state changed before next action");
                    return;
                }
                DispatchAction(record, current);
                return;
            }
            if (Verify(record.CurrentAction, record.Before, current))
            {
                record.Completed++;
                record.AwaitingReadback = false;
                if (record.Completed == record.Actions.Count)
                    FinishSegment(record, "succeeded", "All approved actions verified");
                else
                {
                    record.Before = current;
                    record.Result = Result(record, "queued", "Action verified; next action pending", current);
                }
            }
            else if (Time.realtimeSinceStartup >= record.Deadline)
                FinishSegment(record, "unverified", "Native readback did not match; remaining actions stopped");
        }

        void DispatchAction(Record record, JObject before)
        {
            record.Before = before;
            try
            {
                // Mark dispatch before the adapter: exceptions can occur after a mutation.
                record.AwaitingReadback = true;
                Apply(record.CurrentAction);
                Observe(false);
                record.Deadline = Time.realtimeSinceStartup + 3;
                record.Result = Result(record, "queued", "Awaiting native readback", null);
            }
            catch (Exception)
            {
                FinishSegment(record, "unverified", "Operation failed; inspect native state before further work");
            }
        }

        void FinishSegment(Record record, string status, string reason)
        {
            m_Pending = null;
            Invalidate((string)record.Envelope["task_id"]);
            m_ActiveTask = null;
            record.Result = Result(record, status, reason, ReadContext());
            Status = reason;
        }

        JObject Route(Request request)
        {
            if (request.Method == "GET" && request.Path == "/chris/context") return Capture();
            if (request.Method == "GET" && request.Path.StartsWith("/chris/commands/"))
            {
                string id = request.Path.Substring("/chris/commands/".Length);
                return m_Records.TryGetValue(id, out var record) ? record.Result : Error("Unknown command");
            }
            if (request.Method == "POST" && request.Path == "/chris/cancel")
            {
                var data = Parse(request.Body);
                Fields(data, "task_id", "host_session");
                Require(StringValue(data["task_id"]) && StringValue(data["host_session"]), "Invalid cancellation types");
                string task = (string)data["task_id"];
                Require(Id(task) && (string)data["host_session"] == m_Session, "Invalid cancellation");
                Require(m_InvalidTasks.Contains(task) || m_InvalidTasks.Count < 4096, "Session ledger full; restart host");
                Invalidate(task);
                if (m_ActiveTask == task) Stop("Task cancelled");
                return new JObject { ["cancelled"] = true, ["host_session"] = m_Session };
            }
            if (request.Method == "POST" && request.Path == "/chris/commands")
                return Submit(Parse(request.Body));
            return Error("Unknown route");
        }

        static JObject Parse(string json)
        {
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 16 })
            {
                var obj = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                Require(!reader.Read(), "Trailing JSON data");
                return obj;
            }
        }
        static void Fields(JObject obj, params string[] fields) =>
            Require(obj != null && obj.Properties().All(p => fields.Contains(p.Name)) &&
                fields.All(f => obj[f] != null), "Invalid object fields");
        static bool StringValue(JToken token) => token != null && token.Type == JTokenType.String;
        static bool NumberValue(JToken token) => token != null &&
            (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) && Finite((double)token);

        public JObject Submit(JObject envelope)
        {
            Require(!m_Closed && m_Registered, "Host closed or unavailable");
            Fields(envelope, "command_id", "task_id", "payload", "payload_digest", "approval");
            Require(StringValue(envelope["command_id"]) && StringValue(envelope["task_id"]), "Invalid IDs");
            string command = (string)envelope["command_id"], task = (string)envelope["task_id"];
            Require(Id(command) && Id(task) && command == task + "-1", "Invalid segment IDs");
            string wire = envelope.ToString(Formatting.None);
            if (m_Records.TryGetValue(command, out var existing))
            {
                Require(existing.Wire == wire, "Command ID reused with different payload");
                return existing.Result;
            }
            Require(m_Records.Count < 4096 && m_InvalidTasks.Count < 4096, "Session ledger full; restart host");
            var approval = (JObject)envelope["approval"];
            Fields(approval, "approval_id", "task_id", "actions", "action_digest", "host_session", "revision", "authority_epoch", "expires_at", "scope");
            Require(new[] { "approval_id", "task_id", "action_digest", "host_session", "scope" }.All(k => StringValue(approval[k])) &&
                approval["revision"].Type == JTokenType.Integer && approval["authority_epoch"].Type == JTokenType.Integer &&
                (long)approval["revision"] >= 0 && (long)approval["authority_epoch"] >= 0 && NumberValue(approval["expires_at"]) &&
                StringValue(envelope["payload"]) && StringValue(envelope["payload_digest"]), "Invalid approval types");
            Require(approval["actions"] is JArray, "Actions must be an array");
            var actions = (JArray)approval["actions"];
            Require(actions.Count >= 1 && actions.Count <= 5 && actions.All(a => a is JObject), "Expected 1–5 actions");
            string payload = (string)envelope["payload"], hash = Hash(payload);
            var parsedPayload = Parse(payload);
            Fields(parsedPayload, "actions");
            Require(payload.Length <= 4096 && hash == (string)envelope["payload_digest"] &&
                hash == (string)approval["action_digest"] && JToken.DeepEquals(parsedPayload["actions"], actions) &&
                Id((string)approval["approval_id"]) && (string)approval["task_id"] == task &&
                (string)approval["scope"] == "control_segment", "Approval binding mismatch");
            Observe(true);
            var before = ReadContext();
            string rejection = null;
            if (m_InvalidTasks.Contains(task)) rejection = "Task authority invalidated";
            else if (m_Pending != null) rejection = "Another command is active";
            else if ((string)approval["host_session"] != m_Session ||
                (long)approval["authority_epoch"] != m_Epoch || (long)approval["revision"] != m_Revision)
                rejection = "Stale context or host session";
            else if (!Finite((double)approval["expires_at"]) || (double)approval["expires_at"] <= AuthorityTime ||
                (double)approval["expires_at"] > AuthorityTime + 301) rejection = "Invalid approval expiry";
            else if (!(bool)before["ready"] || (bool)before["stroke_active"] || IsNativeInteractionBusy()) rejection = "Host busy or unavailable";
            var record = new Record { Envelope = (JObject)envelope.DeepClone(), Wire = wire, Before = before };
            m_Records.Add(command, record);
            if (rejection != null)
            {
                record.Result = Result(record, "rejected", rejection, before);
                return record.Result;
            }
            try
            {
                ValidateActions(actions, before);
            }
            catch (Exception)
            {
                record.Result = Result(record, "rejected", "Invalid control segment", before);
                Invalidate(task);
                return record.Result;
            }
            m_ActiveTask = task;
            m_Pending = record;
            DispatchAction(record, before);
            return record.Result;
        }

        internal static void ValidateActions(JArray actions, JObject context)
        {
            Require(actions != null && actions.Count >= 1 && actions.Count <= 5, "Expected 1-5 actions");
            var actionIds = new HashSet<string>();
            foreach (var item in actions)
            {
                Require(item is JObject, "Invalid action object");
                var action = (JObject)item;
                Fields(action, "action_id", "version", "tool", "number", "text", "vector", "visible");
                Validate(action, context);
                Require(actionIds.Add((string)action["action_id"]), "Duplicate action ID");
            }
        }

        static void Validate(JObject a, JObject c)
        {
            Require(StringValue(a["action_id"]) && Id((string)a["action_id"]) && a["version"]?.Type == JTokenType.Integer && (int)a["version"] == 1 && StringValue(a["tool"]), "Invalid action version");
            Require(a.Properties().All(p => new[] { "action_id", "version", "tool", "number", "text", "vector", "visible" }.Contains(p.Name)), "Unknown field");
            Require(a["number"] == null || a["number"].Type == JTokenType.Null || NumberValue(a["number"]), "Invalid number");
            Require(a["text"] == null || a["text"].Type == JTokenType.Null || (StringValue(a["text"]) && ((string)a["text"]).Length <= 80), "Invalid text");
            string tool = (string)a["tool"];
            string[] required;
            switch (tool)
            {
                case "brush.size": required = new[] { "number" }; Require(Finite((double)a["number"]) && (double)a["number"] >= 0 && (double)a["number"] <= 1, "Size out of range"); break;
                case "brush.color": required = new[] { "text" }; Require(Regex.IsMatch((string)a["text"], "\\A#[0-9a-fA-F]{6}\\z"), "Invalid color"); break;
                case "brush.select": required = new[] { "text" }; Require(c["brushes"][(string)a["text"]] != null, "Unknown brush"); break;
                case "panel.visibility": required = new[] { "text", "visible" }; Require(c["panels"][(string)a["text"]] != null && a["visible"].Type == JTokenType.Boolean, "Unknown panel"); break;
                case "view.move":
                    required = new[] { "vector" };
                    var v = (JArray)a["vector"];
                    Require(v.Count == 3 && v.All(NumberValue) && v.Sum(x => (double)x * (double)x) <= 0.25, "Movement out of range"); break;
                case "view.turn": required = new[] { "number" }; Require((double)a["number"] == -15 || (double)a["number"] == 15, "Invalid turn"); break;
                default: throw new ArgumentException("Unsupported action");
            }
            foreach (var key in new[] { "number", "text", "vector", "visible" })
                Require((a[key] != null && a[key].Type != JTokenType.Null) == required.Contains(key), "Mismatched parameters");
        }

        protected virtual void Apply(JObject a)
        {
            switch ((string)a["tool"])
            {
                case "brush.size":
                    PointerManager.m_Instance.SetAllPointersBrushSize01((float)a["number"]);
                    PointerManager.m_Instance.MarkAllBrushSizeUsed();
                    break;
                case "brush.color": ApiMethods.SetColorHTML((string)a["text"]); break;
                case "brush.select": ApiMethods.Brush((string)a["text"]); break;
                case "panel.visibility":
                    string name = (string)a["text"];
                    var panel = FindPanel(name);
                    Require(panel != null && panel.WidgetSibling != null, "Panel unavailable");
                    bool visible = (bool)a["visible"];
                    if (PanelVisible(panel) == visible) break;
                    if (!visible)
                    {
                        panel.ResetPanel();
                        panel.WidgetSibling.Show(false, false);
                    }
                    else
                    {
                        var target = TrTransform.TRS(panel.transform.position, panel.transform.rotation, 1);
                        var spawn = target;
                        spawn.scale = 0;
                        panel.WidgetSibling.InitIntroAnim(spawn, target, false, null, true);
                        panel.WidgetSibling.Show(true);
                    }
                    break;
                case "view.move": ApiMethods.MoveUserBy(Vector((JArray)a["vector"])); break;
                case "view.turn": ApiMethods.UserYaw((float)a["number"]); break;
            }
        }
        static Vector3 Vector(JArray v) => new Vector3((float)v[0], (float)v[1], (float)v[2]);
        static Quaternion Rotation(JArray v) => new Quaternion((float)v[0], (float)v[1], (float)v[2], (float)v[3]);
        protected virtual bool Verify(JObject a, JObject before, JObject after)
        {
            if (!(bool)after["ready"]) return false;
            switch ((string)a["tool"])
            {
                case "brush.size": return Math.Abs((double)after["brush_size"] - (double)a["number"]) < 0.0001;
                case "brush.color": return string.Equals((string)after["brush_color"], (string)a["text"], StringComparison.OrdinalIgnoreCase);
                case "brush.select": return (string)after["brush_id"] == (string)a["text"];
                case "panel.visibility":
                    var panel = FindPanel((string)a["text"]);
                    return panel != null && (bool?)after["panels"][(string)a["text"]] == (bool)a["visible"] &&
                        (panel.WidgetSibling == null || (!panel.WidgetSibling.IsAnimating() && !panel.WidgetSibling.IsHiding())) &&
                        (!(bool)a["visible"] || panel.transform.lossyScale.sqrMagnitude > 0.0001f);
                case "view.move": return Vector3.Distance(Vector((JArray)after["scene_position"]), Vector((JArray)before["scene_position"]) - Vector((JArray)a["vector"])) < 0.0001;
                case "view.turn":
                    var expectedRotation = Rotation((JArray)before["scene_rotation"]) *
                        Quaternion.AngleAxis(-(float)a["number"], Vector3.up);
                    return NearRotation(after["scene_rotation"], new JArray(expectedRotation.x,
                        expectedRotation.y, expectedRotation.z, expectedRotation.w));
                default: return false;
            }
        }
        JObject Result(Record record, string status, string reason, JObject observed) => new JObject {
            ["task_id"] = record.Envelope["task_id"], ["command_id"] = record.Envelope["command_id"], ["host_session"] = m_Session,
            ["status"] = status, ["reason"] = reason, ["observed"] = observed,
            ["completed"] = record.Completed, ["remaining"] = record.Actions.Count - record.Completed };
        void Invalidate(string task)
        {
            if (task != null && m_InvalidTasks.Count < 4096) m_InvalidTasks.Add(task);
        }
        public void Stop(string reason = "Stopped locally", bool announce = true)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            m_Epoch++;
            Invalidate(m_ActiveTask);
            m_ActiveTask = null;
            if (m_Pending != null)
            {
                var record = m_Pending;
                string status = record.AwaitingReadback ? "unverified" : record.Completed > 0 ? "partial" : "cancelled";
                record.Result = Result(record, status, "Stopped; verified changes kept and remaining actions cancelled", ReadContext());
            }
            m_Pending = null;
            if (announce) Status = reason == "Stopped locally"
                ? "STOP received. Pending work cancelled; completed changes kept." : reason;
            Stopped?.Invoke();
            LastStopMilliseconds = timer.Elapsed.TotalMilliseconds;
            if (StopCount < long.MaxValue) StopCount++;
            // Bounded, local measurement from handler entry through authority invalidation.
            // This does not include device polling, frame scheduling, or HTTP transport time.
            if (reason == "Stopped locally" && m_StopLogs++ < 64)
                Debug.LogFormat("CHRIS Stop: count={0} handler_ms={1:F3}", StopCount, LastStopMilliseconds);
        }
        void OnDisable() { Close(); }
        void OnDestroy() { Close(); }
        void Close()
        {
            if (m_Closed) return;
            lock (m_Requests) m_Closed = true;
            if (m_Registered && m_Server != null) m_Server.RemoveHttpHandler("/chris/");
            Stop("Host closed");
            lock (m_Requests)
                while (m_Requests.Count > 0)
                { var r = m_Requests.Dequeue(); r.Reply = Error("Host closed"); r.Done.Set(); }
        }
    }
}
