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
                if (CHRISHandAuthority.ActiveTaskId == task) CHRISHandAuthority.Revoke("Task cancelled");
                // Between batches no lease is active; the run must still end so the task cannot
                // lease again. Strokes already drawn stay in the sketch.
                CHRISDrawingRuns.EndRun(task);
                return new JObject { ["cancelled"] = true, ["host_session"] = m_Session };
            }
            if (request.Method == "POST" && request.Path == "/chris/commands")
                return Submit(Parse(request.Body));
            if (request.Path.StartsWith(ProcedureRoute)) return RouteProcedure(request);
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
            if (data == null || !data.Properties().All(p => fields.Contains(p.Name) || p.Name == "scope") || !fields.All(f => data[f] != null))
                return "Invalid lease request fields";
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
            var lease = AcquireHand(task, scope, out string refusal);
            if (lease == null) return Error(refusal);
            // One lease per task: the task cannot lease again or run one-shot commands.
            Invalidate(task);
            double granted = Now;
            lease["host_session"] = m_Session;
            lease["granted_at"] = granted;
            lease["expires_at"] = granted + CHRISProcedureExecutor.MaxLeaseSeconds;
            return lease;
        }

        protected virtual JObject AcquireHand(string task, string scope, out string refusal) =>
            CHRISHandAuthority.Acquire(task, IsNativeInteractionBusy(), scope, out refusal);

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
            if (revokeLease)
            {
                CHRISHandAuthority.Revoke(reason);
                CHRISDrawingRuns.EndAll();
            }
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
            m_BrushNames?.Dispose();
            Stop("Host closed");
            lock (m_Requests)
                while (m_Requests.Count > 0)
                { var r = m_Requests.Dequeue(); r.Reply = Error("Host closed"); r.Done.Set(); }
        }
    }
}
