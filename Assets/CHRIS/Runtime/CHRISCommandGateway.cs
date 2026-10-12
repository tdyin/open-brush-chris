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
            public string Method, Path, Query, Body;
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
        // input-v0.2: tasks that have held an input lease, and tasks whose input ended any way
        // other than a normal release (the tombstone: a new approval is needed).
        private readonly HashSet<string> m_InputTasks = new HashSet<string>();
        private readonly HashSet<string> m_Tombstones = new HashSet<string>();
        private CHRISInputExecutor m_InputLease;
        private bool m_WasFocused = true;
        private readonly string m_Session = Guid.NewGuid().ToString("N");
        private long m_Revision, m_Epoch;
        private string m_ActiveTask;
        private JObject m_ObservedContext;
        private Record m_Pending;
        private bool m_Closed, m_Registered;
        private HttpServer m_Server;
        private CHRISBrushNames m_BrushNames;
        private string m_PaletteError;
        private float m_NextPanelOpen;
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
            // The mapping is itself capped at 16 KiB. Escaping it inside a JSON string can make
            // the typed envelope larger; only this route receives the wider transport limit.
            int bodyLimit = MaxBodyForPath(ctx.Request.Url?.AbsolutePath);
            if (ctx.Request.RemoteEndPoint == null || !IPAddress.IsLoopback(ctx.Request.RemoteEndPoint.Address) ||
                ctx.Request.Headers["Origin"] != null || ctx.Request.ContentLength64 > bodyLimit ||
                (ctx.Request.HttpMethod != "GET" && ctx.Request.HttpMethod != "POST") ||
                (ctx.Request.HttpMethod == "POST" && (ctx.Request.ContentLength64 < 0 ||
                 !string.Equals((ctx.Request.ContentType ?? "").Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))))
            { ctx.Response.StatusCode = 400; reply = Error("Invalid request"); }
            else
            {
                var request = new Request { Method = ctx.Request.HttpMethod,
                    Path = ctx.Request.Url.AbsolutePath, Query = ctx.Request.Url.Query };
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    var body = new char[bodyLimit + 1];
                    int count = reader.ReadBlock(body, 0, body.Length);
                    if (count > bodyLimit) request.Reply = Error("Request body too large");
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
        internal static int MaxBodyForPath(string path) => path == "/chris/mapping/activate" ? 49152 : 16384;
        static bool Id(string value) => value != null && Regex.IsMatch(value, "\\A[A-Za-z0-9_-]{1,80}\\z");
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static void Require(bool valid, string reason)
        { if (!valid) throw new ArgumentException(reason); }

        public JObject Capture()
        {
            return Observe(true);
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
                ["unknown"] = new JArray("active_layer", "selected_model", "physical_scale", "generation"),
                ["palette"] = null, ["hover_target_id"] = null, ["palette_in_view"] = null,
                ["grab_active"] = null, ["focus"] = null, ["buttons_neutral"] = null, ["stroke_count"] = null };
            if (!ready) return c;
            c["stroke_count"] = SketchStrokeCount();
            // Read-only procedure observations; not part of MateriallyChanged, so hover alone
            // never advances the revision.
            // A failed palette read reports null; it must never fail the context for one-shot tasks.
            try
            {
                c["palette"] = CHRISPaletteObserver.Snapshot();
                c["hover_target_id"] = CHRISPaletteObserver.HoverTargetId();
                c["palette_in_view"] = CHRISPaletteObserver.InView();
            }
            catch (Exception error)
            {
                c["palette"] = c["hover_target_id"] = c["palette_in_view"] = null;
                string message = error.GetType().Name + ": " + error.Message;
                if (message != m_PaletteError) Debug.LogWarning("CHRIS palette read failed: " + message);
                m_PaletteError = message;
            }
            c["grab_active"] = SketchControlsScript.m_Instance.IsUserGrabbingWorld();
            c["focus"] = CHRISInputMappingHost.InputFocusedNow;
            c["buttons_neutral"] = CHRISHandAuthority.BrushButtonsNeutral;
            c["brush_id"] = pointer.CurrentBrush.m_Guid.ToString();
            c["brush_size"] = pointer.BrushSize01;
            c["brush_color"] = "#" + ColorUtility.ToHtmlStringRGB(pointer.GetCurrentColor());
            if (m_BrushNames == null) m_BrushNames = new CHRISBrushNames();
            c["brushes"] = m_BrushNames.Snapshot(BrushCatalog.m_Instance);
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

        internal static BasePanel FindPanel(string name)
        {
            if (PanelManager.m_Instance == null) return null;
            return PanelManager.m_Instance.GetAllPanels().Select(p => p.m_Panel)
                .FirstOrDefault(p => p != null && p.Type.ToString() == name &&
                    PanelManager.m_Instance.IsPanelAvailable(p));
        }
        internal static bool PanelVisible(BasePanel panel) => panel.gameObject.activeInHierarchy &&
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
        JObject Observe(bool manual)
        {
            var context = ReadContext();
            if (m_ObservedContext == null) m_ObservedContext = (JObject)context.DeepClone();
            else if (MateriallyChanged(m_ObservedContext, context))
            {
                m_Revision++;
                // A procedure's own brush change must not revoke it; its lease has local triggers.
                if (manual && !(m_Pending != null && ExpectedPendingChange(m_Pending, context)))
                    Stop("Manual state changed; pending work cancelled", announce: m_Pending != null, revokeLease: false);
                m_ObservedContext = (JObject)context.DeepClone();
            }
            else if (!manual) m_ObservedContext = (JObject)context.DeepClone();
            context["revision"] = m_Revision;
            context["authority_epoch"] = m_Epoch;
            context["active_task"] = m_ActiveTask == null ? JValue.CreateNull() : new JValue(m_ActiveTask);
            return context;
        }

        void Update()
        {
            if (m_Closed) return;
            // Focus loss changes authority whether or not a lease is active.
            bool focused = InputFocused;
            if (m_WasFocused && !focused) m_Epoch++;
            m_WasFocused = focused;
            SyncInput();
            Observe(true);
            if (m_Pending != null && IsNativeInteractionBusy()) Stop("Native interaction took control");
            OpenTestPanel();
            CHRISDrawingRuns.Tick(Time.realtimeSinceStartup, CHRISHandAuthority.ActiveTaskId);
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
                        catch (Exception error)
                        {
                            Debug.LogWarning($"CHRIS route exception for {request.Method} {request.Path}: {error}");
                            request.Reply = Error("Rejected invalid or unavailable operation");
                        }
                    }
                    request.Done.Set();
                }
            }
        }

        protected virtual double AuthorityTime => Now;

        protected virtual bool InputFocused => CHRISInputMappingHost.InputFocusedNow;

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
            catch (Exception error)
            {
                Debug.LogWarning("CHRIS action adapter exception: " + error);
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
            if (request.Method == "GET" && request.Path == "/chris/mapping/status")
                return CHRISInputMappingHost.MappingStatus();
            if (request.Method == "POST" && request.Path == "/chris/mapping/activate")
                return CHRISInputMappingHost.ActivateMapping(Parse(request.Body));
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
                if (CHRISHandAuthority.ActiveTaskId == task || CHRISHandAuthority.InputTaskId == task)
                    CHRISHandAuthority.Revoke("Task cancelled");
                SyncInput();
                Tombstone(task);
                // Between batches no lease is active; the run must still end so the task cannot
                // lease again. Strokes already drawn stay in the sketch.
                CHRISDrawingRuns.EndRun(task);
                return new JObject { ["cancelled"] = true, ["host_session"] = m_Session };
            }
            if (request.Method == "POST" && request.Path == "/chris/commands")
                return Submit(Parse(request.Body));
            if (request.Path.StartsWith(ProcedureRoute)) return RouteProcedure(request);
            if (request.Method == "GET" && request.Path == "/chris/observe") return Observation();
            if (request.Path.StartsWith(InputRoute)) return RouteInput(request);
            if (request.Method == "GET" && request.Path == "/chris/drawing/snapshot")
            {
                string task = SnapshotTask(request.Query);
                if (task == null) return Error("Invalid task_id");
                var snapshot = CHRISDrawingRuns.Snapshot(task, Time.realtimeSinceStartup, SketchStrokeCount(), Now, out string error);
                return snapshot ?? Error(error);
            }
            if (request.Method == "POST" && request.Path == "/chris/test/display")
            {
                var display = Parse(request.Body);
                string invalid = CHRISTestDisplay.Validate(display);
                if (invalid != null) return Error(invalid);
                long seq = CHRISTestDisplay.Instance.Show(display, Time.realtimeSinceStartup);
                OpenTestPanel();
                return new JObject { ["shown"] = true, ["seq"] = seq };
            }
            if (request.Method == "GET" && request.Path == "/chris/test/events")
                return EventsAfter(request.Query) is long after ? CHRISTestDisplay.Instance.Events(after) : Error("Invalid after");
            return Error("Unknown route");
        }

        // A test session starting opens the status panel in front of the user once. Open Brush
        // may still be loading when the first display arrives, so retry until it is open.
        void OpenTestPanel()
        {
            var display = CHRISTestDisplay.Instance;
            if (!display.OpenPending || Time.realtimeSinceStartup < m_NextPanelOpen) return;
            m_NextPanelOpen = Time.realtimeSinceStartup + 1;
            bool open = CHRISPanel.Instance?.Popup?.IsOpen() ?? false;
            if (!open && !CHRISFloatingPanel.Show()) return;
            display.PanelOpened();
            Debug.Log(open ? "CHRIS test panel already open" : "CHRIS test panel opened");
        }

        // "?task_id=<id>" and nothing else.
        internal static string SnapshotTask(string query)
        {
            var match = Regex.Match(query ?? "", "\\A\\?task_id=([A-Za-z0-9_-]{1,80})\\z");
            return match.Success ? match.Groups[1].Value : null;
        }

        // Strokes in the sketch, including any the user made; the drawing guard compares it.
        protected virtual int SketchStrokeCount() => SketchMemoryScript.m_Instance != null ? SketchMemoryScript.m_Instance.StrokeCount : 0;

        // "?after=N" with N a non-negative integer; nothing else is accepted.
        internal static long? EventsAfter(string query)
        {
            var match = Regex.Match(query ?? "", "\\A\\?after=(0|[1-9][0-9]{0,17})\\z");
            return match.Success ? long.Parse(match.Groups[1].Value) : (long?)null;
        }

        // ---- Controller procedure lease (Brush hand pose and trigger only) ----
        const string ProcedureRoute = "/chris/procedure/";
        static readonly Regex s_LeaseId = new Regex("\\A[0-9a-f]{32}\\z");

        JObject RouteProcedure(Request request)
        {
            string rest = request.Path.Substring(ProcedureRoute.Length);
            if (request.Method == "POST" && rest == "acquire") return AcquireLease(Parse(request.Body));
            string[] parts = rest.Split('/');
            if (parts.Length > 2 || !s_LeaseId.IsMatch(parts[0])) return Error("Unknown route");
            var lease = CHRISHandAuthority.Find(parts[0]);
            if (lease == null) return Error("Unknown lease");
            float now = Time.realtimeSinceStartup;
            if (request.Method == "GET" && parts.Length == 1)
            {
                lease.Heartbeat(now);
                var status = lease.Status();
                status["host_session"] = m_Session;
                status["captured_at"] = Now;
                status["pointer"] = CHRISHandAuthority.Pointer(lease);
                return status;
            }
            if (request.Method != "POST" || parts.Length != 2) return Error("Unknown route");
            var body = Parse(request.Body);
            if (parts[1] == "steps")
            {
                string error = lease.Submit(body, now);
                return error != null ? Error(error) :
                    new JObject { ["lease_id"] = lease.LeaseId, ["step_id"] = body["step_id"], ["accepted"] = true };
            }
            if (parts[1] == "release")
            {
                Fields(body);
                // Idempotent: a revoked, expired or released lease reports its current state.
                CHRISHandAuthority.Release(lease);
                return new JObject { ["lease_id"] = lease.LeaseId, ["released"] = true,
                    ["buttons_neutral"] = lease.ButtonsNeutral,
                    ["restored_mode"] = CHRISHandAuthority.RestoredMode(lease),
                    ["hand_back_pending"] = CHRISHandAuthority.HandBackPending(lease) };
            }
            return Error("Unknown route");
        }

        // Message shape only, as the shared procedure cases describe (stateless).
        internal static string ValidateAcquire(JObject data)
        {
            var fields = new[] { "task_id", "host_session", "authority_epoch", "revision", "hand", "channels" };
            if (data == null || !data.Properties().All(p => fields.Contains(p.Name) || p.Name == "scope" || p.Name == "task_deadline") ||
                !fields.All(f => data[f] != null))
                return "Invalid lease request fields";
            if (data["task_deadline"] != null && (!NumberValue(data["task_deadline"]) || (double)data["task_deadline"] < 0))
                return "Invalid task_deadline";
            if (data["scope"] != null && (!StringValue(data["scope"]) || (string)data["scope"] != CHRISProcedureExecutor.PaletteScope &&
                (string)data["scope"] != CHRISProcedureExecutor.DrawScope && (string)data["scope"] != CHRISProcedureExecutor.Draw3dScope))
                return "Invalid scope";
            if (!StringValue(data["task_id"]) || !StringValue(data["host_session"]) || !StringValue(data["hand"]) ||
                data["authority_epoch"].Type != JTokenType.Integer || data["revision"].Type != JTokenType.Integer ||
                (long)data["authority_epoch"] < 0 || (long)data["revision"] < 0) return "Invalid lease request types";
            if (!Id((string)data["task_id"])) return "Invalid task_id";
            if ((string)data["hand"] != "brush") return "Only the brush hand can be leased";
            if (!JToken.DeepEquals(data["channels"], new JArray("pose", "trigger"))) return "Channels must be pose and trigger";
            return null;
        }

        JObject AcquireLease(JObject data)
        {
            Require(!m_Closed && m_Registered, "Host closed or unavailable");
            string invalid = ValidateAcquire(data);
            if (invalid != null) return Error(invalid);
            string task = (string)data["task_id"];
            string scope = data["scope"] != null ? (string)data["scope"] : CHRISProcedureExecutor.PaletteScope;
            bool box = scope == CHRISProcedureExecutor.Draw3dScope, draw = box || scope == CHRISProcedureExecutor.DrawScope;
            Observe(true);
            var context = ReadContext();
            // A drawing run leases once per stroke batch under one task; any other task leases once.
            if (m_InvalidTasks.Contains(task) && !(draw && CHRISDrawingRuns.Find(task) != null))
                return Error("Task authority invalidated");
            if (m_Pending != null) return Error("A one-shot command is active");
            if ((string)data["host_session"] != m_Session || (long)data["authority_epoch"] != m_Epoch ||
                (long)data["revision"] != m_Revision) return Error("Stale context or host session");
            if (!(bool)context["ready"] || (bool)context["stroke_active"]) return Error("Host busy or unavailable");
            Require(m_InvalidTasks.Count < 4096, "Session ledger full; restart host");
            if (draw && CHRISDrawingRuns.AcquireRefusal(task, SketchStrokeCount(), box) is string guard) return Error(guard);
            // The approved task deadline caps the lease; a deadline about to pass grants nothing.
            double? deadline = data["task_deadline"] != null ? (double)data["task_deadline"] : (double?)null;
            if (deadline.HasValue && deadline.Value <= AuthorityTime + 1) return Error("Task deadline already reached");
            var lease = AcquireHand(task, scope, out string refusal);
            if (lease == null) return Error(refusal);
            double seconds = CHRISProcedureExecutor.MaxLeaseSeconds;
            if (deadline.HasValue)
            {
                double remaining = deadline.Value - AuthorityTime;
                CHRISHandAuthority.Find((string)lease["lease_id"])?.LimitTo(Time.realtimeSinceStartup + (float)remaining);
                seconds = Math.Min(seconds, remaining);
            }
            // One lease per task: the task cannot lease again or run one-shot commands.
            Invalidate(task);
            double granted = Now;
            lease["host_session"] = m_Session;
            lease["granted_at"] = granted;
            lease["expires_at"] = granted + seconds;
            return lease;
        }

        protected virtual JObject AcquireHand(string task, string scope, out string refusal) =>
            CHRISHandAuthority.Acquire(task, IsNativeInteractionBusy(), scope, out refusal);

        // ---- input-v0.2: primitive controller input (Brush hand pose and trigger only) ----
        // Nothing on these routes names a target, stroke or brush, and nothing here calls the
        // procedure steps or drawing runs.
        const string InputRoute = "/chris/input/";
        const string AuthorityRefusal = "Authority changed; new approval needed", TombstoneRefusal = "Task stopped; new approval needed",
            BusyRefusal = "Host busy or unavailable", CapRefusal = "Requested limit above the native cap";
        static readonly string[] s_InputAcquireFields = { "task_id", "host_session", "authority_epoch", "hand", "channels",
            "task_deadline", "bounds", "max_speed_mps", "max_turn_dps" };

        // A lease that ended other than by a normal release tombstones its task and changes
        // authority. bump is false when the caller (Stop) changes authority itself.
        void SyncInput(bool bump = true)
        {
            var lease = m_InputLease;
            if (lease == null || lease.Active) return;
            m_InputLease = null;
            if (lease.State == CHRISLeaseState.Released) return;
            if (bump) m_Epoch++;
            Tombstone(lease.TaskId);
        }

        void Tombstone(string task)
        {
            if (task != null && m_Tombstones.Count < 4096) m_Tombstones.Add(task);
        }

        double UnixAt(float realtime) => Now - (Time.realtimeSinceStartup - realtime);

        // Message shape only, as the shared input cases describe (stateless).
        internal static string ValidateInputAcquire(JObject data)
        {
            if (data == null) return "Invalid input acquire";
            if (!data.Properties().All(p => s_InputAcquireFields.Contains(p.Name))) return CHRISInputExecutor.FieldRefusal;
            if (!s_InputAcquireFields.All(f => data[f] != null)) return "Invalid input acquire fields";
            if (!StringValue(data["task_id"]) || !StringValue(data["host_session"]) || !StringValue(data["hand"]) ||
                data["authority_epoch"].Type != JTokenType.Integer || (long)data["authority_epoch"] < 0)
                return "Invalid input acquire types";
            if (!Id((string)data["task_id"]) || !Id((string)data["host_session"])) return "Invalid task_id";
            if ((string)data["hand"] != "brush") return "Only the brush hand can be leased";
            if (!JToken.DeepEquals(data["channels"], new JArray("pose", "trigger"))) return "Channels must be pose and trigger";
            if (!NumberValue(data["task_deadline"]) || (double)data["task_deadline"] < 0) return "Invalid task_deadline";
            var bounds = data["bounds"] as JObject;
            if (bounds == null || !bounds.Properties().All(p => p.Name == "center" || p.Name == "radius_m") ||
                !(bounds["center"] is JArray center) || center.Count != 3 || !center.All(NumberValue) ||
                !NumberValue(bounds["radius_m"])) return "Invalid bounds";
            if (!NumberValue(data["max_speed_mps"]) || !NumberValue(data["max_turn_dps"])) return "Invalid limits";
            double radius = (double)bounds["radius_m"], speed = (double)data["max_speed_mps"], turn = (double)data["max_turn_dps"];
            if (radius <= 0 || speed <= 0 || turn <= 0) return "Limits must be positive";
            if (radius > CHRISInputExecutor.MaxRadiusM || speed > CHRISInputExecutor.MaxSpeedMps ||
                turn > CHRISInputExecutor.MaxTurnDps) return CapRefusal;
            return null;
        }

        internal static CHRISInputLimits InputLimits(JObject data)
        {
            var center = (JArray)data["bounds"]["center"];
            return new CHRISInputLimits { Center = new Vector3((float)center[0], (float)center[1], (float)center[2]),
                RadiusM = (float)data["bounds"]["radius_m"], MaxSpeedMps = (float)data["max_speed_mps"],
                MaxTurnDps = (float)data["max_turn_dps"] };
        }

        JObject RouteInput(Request request)
        {
            // A recenter between frames revokes the lease even before the next input tick.
            var active = CHRISHandAuthority.FindInput(CHRISHandAuthority.InputLeaseId);
            if (active != null && active.FrameEpoch != CHRISRoomFrame.Epoch) CHRISHandAuthority.RevokeInput(CHRISInputExecutor.Recenter);
            SyncInput();
            string rest = request.Path.Substring(InputRoute.Length);
            if (request.Method == "POST" && rest == "acquire") return AcquireInput(Parse(request.Body));
            string[] parts = rest.Split('/');
            if (parts.Length > 2 || !s_LeaseId.IsMatch(parts[0])) return Error("Unknown route");
            var lease = CHRISHandAuthority.FindInput(parts[0]);
            if (lease == null) return Error("Unknown lease");
            float now = Time.realtimeSinceStartup;
            if (request.Method == "GET" && parts.Length == 1)
            {
                // The status read is also the heartbeat.
                lease.Heartbeat(now);
                var status = lease.Status(UnixAt);
                status["host_session"] = m_Session;
                status["captured_at"] = Now;
                return status;
            }
            if (request.Method != "POST" || parts.Length != 2) return Error("Unknown route");
            var body = Parse(request.Body);
            if (parts[1] == "batches")
            {
                string error = lease.Submit(body, now, ObservationToken);
                if (error == CHRISInputExecutor.TokenRefusal && StringValue(body["if_token"]))
                    Debug.Log($"CHRIS input: lease {lease.LeaseId} batch {body["batch_id"]} refused at admission (stale observation): " +
                        $"changed {TokenChange((string)body["if_token"])}");
                return error != null ? Error(error) :
                    new JObject { ["lease_id"] = lease.LeaseId, ["batch_id"] = body["batch_id"], ["accepted"] = true };
            }
            if (parts[1] == "release")
            {
                Fields(body);
                // Idempotent: a revoked, expired or released lease reports its current state.
                CHRISHandAuthority.Release(lease);
                SyncInput();
                return new JObject { ["lease_id"] = lease.LeaseId, ["released"] = true,
                    ["buttons_neutral"] = lease.ButtonsNeutral,
                    ["restored_mode"] = CHRISHandAuthority.RestoredMode(lease),
                    ["hand_back_pending"] = CHRISHandAuthority.HandBackPending(lease) };
            }
            return Error("Unknown route");
        }

        JObject AcquireInput(JObject data)
        {
            Require(!m_Closed && m_Registered, "Host closed or unavailable");
            string invalid = ValidateInputAcquire(data);
            if (invalid != null) return Error(invalid);
            string task = (string)data["task_id"];
            // No revision check on this path (input-v0.2): session and authority epoch only.
            var context = ReadContext();
            // Re-acquire is allowed after a normal release while the task is valid; any other
            // end, a cancel, or a task already used another way needs a new approval.
            if (m_Tombstones.Contains(task) || m_InvalidTasks.Contains(task) && !m_InputTasks.Contains(task))
                return Error(TombstoneRefusal);
            if ((string)data["host_session"] != m_Session || (long)data["authority_epoch"] != m_Epoch) return Error(AuthorityRefusal);
            if (m_Pending != null || !(bool)context["ready"] || (bool)context["stroke_active"])
            {
                Debug.Log($"CHRIS procedure: input acquire refused for task {task}: host busy (" +
                    (m_Pending != null ? "one-shot command pending" : !(bool)context["ready"] ? "host not ready" : "stroke active") + ")");
                return Error(BusyRefusal);
            }
            double deadline = (double)data["task_deadline"];
            if (deadline <= AuthorityTime + 1) return Error("Task deadline already reached");
            Require(m_InvalidTasks.Count < 4096 && m_Tombstones.Count < 4096, "Session ledger full; restart host");
            var lease = AcquireInputHand(task, InputLimits(data), out string refusal);
            if (lease == null)
                return Error(refusal == CHRISInputExecutor.HeadRefusal || refusal == CHRISInputExecutor.OutsideRefusal ? refusal : BusyRefusal);
            double remaining = deadline - AuthorityTime;
            lease.LimitTo(Time.realtimeSinceStartup + (float)remaining);
            lease.TokenSource = ObservationToken;
            lease.TokenChange = TokenChange;
            m_InputLease = lease;
            m_InputTasks.Add(task);
            // One-shot commands cannot use a task that has held an input lease.
            Invalidate(task);
            double granted = Now;
            return new JObject
            {
                ["lease_id"] = lease.LeaseId,
                ["task_id"] = task,
                ["host_session"] = m_Session,
                ["granted_at"] = granted,
                ["expires_at"] = granted + Math.Min(CHRISInputExecutor.MaxLeaseSeconds, remaining),
                ["frame"] = FrameObject(lease.FrameEpoch),
                ["start"] = PoseObject(lease.AppliedPosition, lease.AppliedRotation),
                ["return_mode"] = lease.ReturnMode.DeepClone(),
            };
        }

        protected virtual CHRISInputExecutor AcquireInputHand(string task, CHRISInputLimits limits, out string refusal) =>
            CHRISHandAuthority.AcquireInput(task, IsNativeInteractionBusy(), limits, out refusal);

        JObject FrameObject(long epoch) => new JObject { ["id"] = CHRISRoomFrame.Id, ["session"] = m_Session, ["epoch"] = epoch };

        internal static JObject PoseObject(Vector3 position, Quaternion rotation) => new JObject {
            ["position"] = new JArray(position.x, position.y, position.z),
            ["rotation"] = new JArray(rotation.x, rotation.y, rotation.z, rotation.w) };

        // GET /chris/observe: read-only structured observation in the room frame.
        JObject Observation()
        {
            Require(!m_Closed && m_Registered, "Host closed or unavailable");
            SyncInput();
            var body = ReadObservation();
            if (body == null) return Error(BusyRefusal);
            var observation = new JObject
            {
                ["observation_id"] = Guid.NewGuid().ToString("N"),
                ["token"] = Remember(body),
                ["frame"] = FrameObject(CHRISRoomFrame.Epoch),
                ["frame_index"] = Time.frameCount,
                ["captured_at"] = Now,
                ["authority_epoch"] = m_Epoch,
            };
            foreach (var property in body.Properties()) observation[property.Name] = property.Value;
            return observation;
        }

        // The freshness token (if_token): palette visibility and page, hover, brush, stroke
        // count, focus and owners. Never a pose: not the leased controller, the head or the
        // palette surface (D120: a hand-held wand's tremor moves the panel; at the press edge the
        // hover itself is the atomic condition). Computed on demand: at admission and again just
        // before each trigger-down edge.
        string ObservationToken()
        {
            var body = ReadObservation();
            return body == null ? null : TokenOf(body);
        }

        internal static string TokenOf(JObject observation) => Hash(TokenFields(observation).ToString(Formatting.None)).Substring(0, 32);

        // The fields of each token this host handed out, so a stale guard can say what changed.
        readonly Dictionary<string, JObject> m_TokenFields = new Dictionary<string, JObject>();

        string Remember(JObject observation)
        {
            var fields = TokenFields(observation);
            string token = Hash(fields.ToString(Formatting.None)).Substring(0, 32);
            if (m_TokenFields.Count >= 64) m_TokenFields.Clear();
            m_TokenFields[token] = fields;
            return token;
        }

        // Diagnostic only: the token fields that differ now from when the token was observed.
        string TokenChange(string token)
        {
            var body = ReadObservation();
            if (body == null) return "observation unavailable";
            if (token == null || !m_TokenFields.TryGetValue(token, out var before)) return "unknown (token not issued by this host)";
            return ChangedFields(before, TokenFields(body));
        }

        // "palette.surface (...)" style: dotted names for changed parts of nested objects, then
        // old -> new values for each changed field.
        internal static string ChangedFields(JObject before, JObject after)
        {
            var changed = new List<string>();
            var values = new List<string>();
            foreach (var property in after.Properties())
            {
                var old = before[property.Name];
                if (JToken.DeepEquals(old, property.Value)) continue;
                if (old is JObject a && property.Value is JObject b)
                    foreach (var name in a.Properties().Select(p => p.Name).Union(b.Properties().Select(p => p.Name)))
                    {
                        if (JToken.DeepEquals(a[name], b[name])) continue;
                        changed.Add(property.Name + "." + name);
                        values.Add($"{property.Name}.{name} {a[name]?.ToString(Formatting.None) ?? "null"} -> {b[name]?.ToString(Formatting.None) ?? "null"}");
                    }
                else
                {
                    changed.Add(property.Name);
                    values.Add($"{property.Name} {old?.ToString(Formatting.None) ?? "null"} -> {property.Value.ToString(Formatting.None)}");
                }
            }
            return changed.Count == 0 ? "nothing" : string.Join(", ", changed) + " (" + string.Join("; ", values) + ")";
        }

        internal static JObject TokenFields(JObject observation)
        {
            var palette = observation["palette"] as JObject;
            var semantic = observation["semantic"] as JObject;
            return new JObject
            {
                ["palette"] = palette == null ? null : new JObject
                {
                    ["visible"] = palette["visible"], ["page"] = palette["page"], ["page_count"] = palette["page_count"],
                },
                ["hover_target_id"] = semantic?["hover_target_id"], ["brush_id"] = semantic?["brush_id"],
                ["stroke_count"] = semantic?["stroke_count"], ["focus"] = observation["focus"],
                ["owners"] = observation["owners"],
            };
        }

        // The observation body; the envelope (id, token, frame, time, authority) is added by
        // Observation(). Null when the controllers, head or sketch are unavailable.
        protected virtual JObject ReadObservation()
        {
            if (m_Closed || App.CurrentState != App.AppState.Standard || ViewpointScript.Head == null ||
                InputManager.m_Instance == null || InputManager.Controllers == null || InputManager.Brush == null ||
                SketchControlsScript.m_Instance == null || PointerManager.m_Instance == null) return null;
            float units = App.METERS_TO_UNITS;
            var head = ViewpointScript.Head;
            var brush = InputManager.Brush;
            var root = brush.Transform;
            var attach = brush.Geometry != null ? brush.Geometry.PointerAttachPoint : null;
            var inverse = Quaternion.Inverse(root.rotation);
            JObject ray = null;
            Vector3 attachLocal = Vector3.zero;
            Quaternion attachRotation = Quaternion.identity;
            if (attach != null)
            {
                attachLocal = inverse * (attach.position - root.position) / units;
                attachRotation = inverse * attach.rotation;
                Vector3 origin = attach.position / units, direction = attach.forward;
                ray = new JObject { ["origin"] = new JArray(origin.x, origin.y, origin.z),
                    ["direction"] = new JArray(direction.x, direction.y, direction.z) };
            }
            var pointer = PointerManager.m_Instance.MainPointer;
            JObject palette = null;
            string hover = null;
            try
            {
                palette = CHRISPaletteObserver.Observe();
                hover = CHRISPaletteObserver.HoverTargetId();
            }
            catch (Exception error)
            {
                string message = error.GetType().Name + ": " + error.Message;
                if (message != m_PaletteError) Debug.LogWarning("CHRIS palette read failed: " + message);
                m_PaletteError = message;
            }
            return new JObject
            {
                ["focus"] = InputFocused,
                ["buttons_neutral"] = CHRISHandAuthority.BrushButtonsNeutral,
                ["grab_active"] = SketchControlsScript.m_Instance.IsUserGrabbingWorld(),
                ["owners"] = CHRISHandAuthority.Owners(),
                ["head"] = PoseObject(head.position / units, head.rotation),
                ["brush"] = new JObject
                {
                    ["pose"] = PoseObject(root.position / units, root.rotation),
                    ["tracked_pose"] = CHRISHandAuthority.TrackedPose(out Vector3 tracked, out Quaternion trackedRotation)
                        ? PoseObject(tracked, trackedRotation) : null,
                    ["trigger"] = CHRISHandAuthority.BrushTriggerHeld(),
                    ["pointer_attachment"] = PoseObject(attachLocal, attachRotation),
                    ["pointer_ray"] = ray,
                },
                ["semantic"] = new JObject
                {
                    ["hover_target_id"] = hover,
                    ["brush_id"] = pointer?.CurrentBrush != null ? pointer.CurrentBrush.m_Guid.ToString() : null,
                    ["stroke_count"] = SketchStrokeCount(),
                    ["stroke_active"] = PointerManager.MainPointerIsPainting(),
                },
                ["palette"] = palette,
            };
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
            Require(actions.Count >= 1 && actions.Count <= 5 && actions.All(a => a is JObject), "Expected 1-5 actions");
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
            else if (CHRISHandAuthority.OwnsBrush) rejection = "A controller procedure is active";
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
        public void Stop(string reason = "Stopped locally", bool announce = true, bool revokeLease = true)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            // Observe() also stops on every material context change without revoking the lease
            // (a drawing lease changes stroke_active with each stroke); only a revoking Stop
            // ends drawing runs.
            // Only a revoking Stop changes authority (input-v0.2): the soft Stop on a manual
            // state change advances the revision alone.
            if (revokeLease)
            {
                CHRISHandAuthority.Revoke(reason);
                CHRISDrawingRuns.EndAll();
                SyncInput(false);
                m_Epoch++;
            }
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
            m_BrushNames?.Dispose();
            Stop("Host closed");
            lock (m_Requests)
                while (m_Requests.Count > 0)
                { var r = m_Requests.Dequeue(); r.Reply = Error("Host closed"); r.Done.Set(); }
        }
    }
}
