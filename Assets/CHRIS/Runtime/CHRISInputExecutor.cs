// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace TiltBrush
{
    // What the host observed this frame for an input lease. Tests construct it directly.
    public struct CHRISInputFrame
    {
        public float Now;           // realtime seconds
        public int Index;           // Time.frameCount
        public string Revocation;   // non-null: a takeover; end the lease with this reason
    }

    // The limits granted at acquire (input-v0.2). Room metres and degrees.
    public struct CHRISInputLimits
    {
        public Vector3 Center;
        public float RadiusM, MaxSpeedMps, MaxTurnDps;
    }

    // input-v0.2: one bounded lease on the logical Brush hand that plays agent-chosen
    // controller-root poses and trigger samples, in batches of at most 1 s. Generic by design:
    // nothing here knows about palettes, targets, strokes or drawings, and nothing here aims,
    // draws or chooses a pose. An invalid batch is refused whole and nothing is clamped.
    // Poses are the room frame: Unity global space in metres (global units / METERS_TO_UNITS),
    // left-handed, +x right, +y up, +z forward; rotations are [x, y, z, w].
    public sealed class CHRISInputExecutor
    {
        public const float MaxLeaseSeconds = 30f;
        public const float HeartbeatSeconds = 1f;
        public const int MinSamples = 1, MaxSamples = 64, MaxBatchMilliseconds = 1000, MinEdgeMilliseconds = 40;
        public const float MaxLateMilliseconds = 100f;
        public const int MaxBatches = 128;
        public const double StartToleranceM = 0.002, StartToleranceDeg = 1, UnitTolerance = 1e-3;
        public const float MaxRadiusM = 1f, MaxSpeedMps = 1.5f, MaxTurnDps = 360f;
        public const float MinHeadDistanceM = 0.25f, MaxHeadDistanceM = 1f;
        const double Slack = 1e-9;

        // The shared input-v0.2 strings (schemas/input/v0.2 "refusals"); Python matches them exactly.
        public const string StartRefusal = "Batch must start at the current pose", BoundsRefusalText = "Sample outside the bounds",
            SpeedRefusal = "Segment exceeds the speed limit", TurnRefusal = "Segment exceeds the turn limit",
            EdgesRefusal = "Trigger edges closer than 40 ms", NeutralRefusal = "Batch must end with the trigger released",
            BusyBatch = "Batch in progress", StaleSeq = "Duplicate or stale batch", Inactive = "Lease is not active",
            DeadlineRefusal = "Batch would pass the task deadline", TokenRefusal = "Observation is stale",
            FrameRefusal = "Frame changed; observe again", FieldRefusal = "Unsupported field",
            HeadRefusal = "Bounds centre must be 0.25-1.0 m from the head", OutsideRefusal = "Controller outside the bounds";
        // Interruption and revocation reasons.
        public const string Late = "Execution late", PressToken = "Observation changed before a press",
            DeadlineReached = "Task deadline reached", Recenter = "Recenter", HeartbeatLost = "Heartbeat lost",
            FocusLost = "Focus lost", Stop = "Stop", TakeoverPrefix = "Takeover: ";

        // Fields of the legacy procedure steps; a batch carrying any of them is not input-v0.2.
        internal static readonly string[] LegacyFields = { "target_id", "expected_hover_target_id", "kind", "points", "depth",
            "stroke", "stroke3d", "draw3d", "scope", "brush", "brush_id", "label", "plane" };
        static readonly string[] s_BatchFields = { "batch_id", "seq", "frame_epoch", "samples", "if_token" };
        static readonly string[] s_SampleFields = { "t_ms", "position", "rotation", "trigger" };
        static readonly Regex s_Id = new Regex("\\A[A-Za-z0-9_-]{1,80}\\z");

        sealed class Sample
        {
            public int T;
            public Vector3 Position;
            public Quaternion Rotation;
            public bool Trigger;
        }

        sealed class Edge
        {
            public int Index, Frame;
            public bool Trigger;
            public float At;
        }

        sealed class Batch
        {
            public string Id, State = "running", Reason, Token;
            public int Seq, LastApplied = -1, NextEdge;
            public float Started = -1, Ended = -1, LateMs;
            public Sample[] Samples;
            public int[] Edges;
            public readonly List<Edge> Applied = new List<Edge>();
        }

        readonly List<Batch> m_Batches = new List<Batch>();
        Batch m_Current;
        float m_Expiry, m_LastHeartbeat;
        bool m_TaskDeadline, m_Trigger;
        Vector3 m_Position;
        Quaternion m_Rotation;

        public string LeaseId { get; }
        public string TaskId { get; }
        public JObject ReturnMode { get; }
        public long FrameEpoch { get; }
        public CHRISInputLimits Limits { get; }
        public CHRISLeaseState State { get; private set; } = CHRISLeaseState.Active;
        public string Reason { get; private set; }
        public bool Active => State == CHRISLeaseState.Active;
        public CHRISVirtualHand Hand { get; } = new CHRISVirtualHand();
        public bool ButtonsNeutral => Hand.IsNeutral;
        public bool BatchRunning => m_Current != null;
        // The exact last applied controller-root pose, room metres.
        public Vector3 AppliedPosition => m_Position;
        public Quaternion AppliedRotation => m_Rotation;
        // The current freshness token, computed on demand; the host sets it at acquire.
        public Func<string> TokenSource;
        public Action<string> Log;
        void Note(string message) { if (Log != null) Log(message); }

        public CHRISInputExecutor(string leaseId, string taskId, JObject returnMode, long frameEpoch,
            CHRISInputLimits limits, Vector3 position, Quaternion rotation, float now)
        {
            LeaseId = leaseId;
            TaskId = taskId;
            ReturnMode = returnMode;
            FrameEpoch = frameEpoch;
            Limits = limits;
            Place(position, rotation);
            m_Expiry = now + MaxLeaseSeconds;
            m_LastHeartbeat = now;
        }

        void Place(Vector3 position, Quaternion rotation)
        {
            m_Position = position;
            m_Rotation = rotation;
            Hand.Position = position * App.METERS_TO_UNITS;
            Hand.Rotation = rotation;
        }

        public float SecondsLeft(float now) => Mathf.Max(0, m_Expiry - now);

        // The approved task deadline ends the lease if it is earlier than the 30 s cap.
        public void LimitTo(float deadline)
        {
            if (deadline >= m_Expiry) return;
            m_Expiry = deadline;
            m_TaskDeadline = true;
        }

        public void Heartbeat(float now)
        {
            if (Active) m_LastHeartbeat = now;
        }

        static bool Number(JToken token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) &&
            !double.IsNaN((double)token) && !double.IsInfinity((double)token);

        static bool Numbers(JToken token, int count) => token is JArray array && array.Count == count && array.All(Number);

        static double[] Doubles(JToken token) => ((JArray)token).Select(v => (double)v).ToArray();

        static double Norm(double[] q) => Math.Sqrt(q.Sum(v => v * v));

        // Degrees between two unit quaternions, in double precision.
        static double Angle(double[] a, double[] b)
        {
            double dot = 0;
            for (int i = 0; i < 4; i++) dot += a[i] * b[i];
            return 2 * Math.Acos(Math.Min(1, Math.Abs(dot) / (Norm(a) * Norm(b)))) * 180 / Math.PI;
        }

        static double Distance(double[] a, double[] b) =>
            Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));

        static double[] Of(Vector3 v) => new double[] { v.x, v.y, v.z };
        static double[] Of(Quaternion q) => new double[] { q.x, q.y, q.z, q.w };

        // Message shape only, as the shared input cases describe (stateless).
        public static string ValidateBatch(JObject batch)
        {
            if (batch == null) return "Invalid batch";
            var samples = batch["samples"] as JArray;
            // Legacy step fields and any other unknown field are refused alike.
            if (!batch.Properties().All(p => s_BatchFields.Contains(p.Name)) ||
                samples != null && samples.OfType<JObject>().Any(s => !s.Properties().All(p => s_SampleFields.Contains(p.Name))))
                return FieldRefusal;
            if (s_BatchFields.Take(4).Any(f => batch[f] == null)) return "Invalid batch fields";
            if (batch["batch_id"].Type != JTokenType.String || !s_Id.IsMatch((string)batch["batch_id"])) return "Invalid batch_id";
            if (batch["seq"].Type != JTokenType.Integer || batch["frame_epoch"].Type != JTokenType.Integer ||
                (long)batch["seq"] < 1 || (long)batch["frame_epoch"] < 0) return "Invalid batch numbers";
            if (batch["if_token"] != null && (batch["if_token"].Type != JTokenType.String ||
                ((string)batch["if_token"]).Length < 1 || ((string)batch["if_token"]).Length > 128)) return "Invalid if_token";
            if (samples == null || samples.Count < MinSamples || samples.Count > MaxSamples) return "Batch needs 1 to 64 samples";
            foreach (var item in samples)
            {
                if (!(item is JObject s) || s_SampleFields.Any(f => s[f] == null)) return "Invalid sample fields";
                if (s["t_ms"].Type != JTokenType.Integer || !Numbers(s["position"], 3) || !Numbers(s["rotation"], 4) ||
                    s["trigger"].Type != JTokenType.Boolean) return "Invalid sample values";
                if (Math.Abs(Norm(Doubles(s["rotation"])) - 1) > UnitTolerance) return "Rotation must be a unit quaternion";
            }
            if ((long)samples[0]["t_ms"] != 0) return "First sample must be at t_ms 0";
            for (int i = 1; i < samples.Count; i++)
                if ((long)samples[i]["t_ms"] <= (long)samples[i - 1]["t_ms"]) return "t_ms must strictly increase";
            if ((long)samples[samples.Count - 1]["t_ms"] > MaxBatchMilliseconds) return "Batch longer than 1000 ms";
            long lastEdge = long.MinValue;
            bool previous = false;
            foreach (var s in samples)
            {
                bool trigger = (bool)s["trigger"];
                if (trigger == previous) continue;
                long t = (long)s["t_ms"];
                if (lastEdge != long.MinValue && t - lastEdge < MinEdgeMilliseconds) return EdgesRefusal;
                lastEdge = t;
                previous = trigger;
            }
            if ((bool)samples[samples.Count - 1]["trigger"]) return NeutralRefusal;
            return null;
        }

        // The lease-state checks, in the shared order (frame, start, bounds, speed, turn), for
        // a held pose and limits; the shared batch_in_lease cases call this directly.
        internal static string LeaseProblem(JObject batch, long frameEpoch, Vector3 held, Quaternion heldRotation,
            CHRISInputLimits limits)
        {
            if ((long)batch["frame_epoch"] != frameEpoch) return FrameRefusal;
            var samples = (JArray)batch["samples"];
            var positions = samples.Select(s => Doubles(s["position"])).ToArray();
            var rotations = samples.Select(s => Doubles(s["rotation"])).ToArray();
            if (Distance(positions[0], Of(held)) > StartToleranceM || Angle(rotations[0], Of(heldRotation)) > StartToleranceDeg)
                return StartRefusal;
            var center = Of(limits.Center);
            if (positions.Any(p => Distance(p, center) > limits.RadiusM + Slack)) return BoundsRefusalText;
            for (int i = 1; i < samples.Count; i++)
            {
                double dt = ((long)samples[i]["t_ms"] - (long)samples[i - 1]["t_ms"]) / 1000.0;
                if (Distance(positions[i], positions[i - 1]) / dt > limits.MaxSpeedMps + Slack) return SpeedRefusal;
                if (Angle(rotations[i], rotations[i - 1]) / dt > limits.MaxTurnDps + Slack) return TurnRefusal;
            }
            return null;
        }

        // Admits one batch, or returns why it was refused. A refused batch does not consume its
        // seq and changes nothing. currentToken is the host's freshness token, computed only
        // when the batch carries if_token.
        public string Submit(JObject batch, float now, Func<string> currentToken = null)
        {
            Heartbeat(now);
            if (!Active) return Inactive;
            string invalid = ValidateBatch(batch);
            if (invalid != null) return invalid;
            if ((long)batch["frame_epoch"] != FrameEpoch) return FrameRefusal;
            string id = (string)batch["batch_id"];
            if (m_Batches.Any(b => b.Id == id) || (long)batch["seq"] != m_Batches.Count + 1) return StaleSeq;
            if (m_Current != null) return BusyBatch;
            if (m_Batches.Count >= MaxBatches) return "Batch limit reached";
            string problem = LeaseProblem(batch, FrameEpoch, m_Position, m_Rotation, Limits);
            if (problem != null) return problem;
            var samples = (JArray)batch["samples"];
            var positions = samples.Select(s => Doubles(s["position"])).ToArray();
            var rotations = samples.Select(s => Doubles(s["rotation"])).ToArray();
            // The whole batch must fit before the lease ends (the task deadline or the 30 s cap).
            float duration = (long)samples[samples.Count - 1]["t_ms"] / 1000f;
            if (now + duration > m_Expiry) return DeadlineRefusal;
            string token = batch["if_token"] != null ? (string)batch["if_token"] : null;
            if (token != null && (currentToken == null || currentToken() != token)) return TokenRefusal;
            var parsed = new Sample[samples.Count];
            var edges = new List<int>();
            bool previous = false;
            for (int i = 0; i < samples.Count; i++)
            {
                var q = rotations[i];
                double n = Norm(q);
                parsed[i] = new Sample { T = (int)(long)samples[i]["t_ms"], Trigger = (bool)samples[i]["trigger"],
                    Position = new Vector3((float)positions[i][0], (float)positions[i][1], (float)positions[i][2]),
                    Rotation = new Quaternion((float)(q[0] / n), (float)(q[1] / n), (float)(q[2] / n), (float)(q[3] / n)) };
                if (parsed[i].Trigger != previous) edges.Add(i);
                previous = parsed[i].Trigger;
            }
            m_Current = new Batch { Id = id, Seq = m_Batches.Count + 1, Samples = parsed, Edges = edges.ToArray(), Token = token };
            m_Batches.Add(m_Current);
            Note($"lease {LeaseId} batch {m_Current.Seq} {id} admitted: {parsed.Length} samples, {duration * 1000:F0} ms, " +
                $"{edges.Count} trigger edges" + (token != null ? ", guarded" : ""));
            return null;
        }

        public void Revoke(string reason) => End(CHRISLeaseState.Revoked, reason);
        public void Release() => End(CHRISLeaseState.Released, "Released by agent");

        // Any end: the trigger is released in this frame and the remaining samples are discarded.
        void End(CHRISLeaseState state, string reason, float now = -1)
        {
            if (!Active) return;
            State = state;
            Reason = reason;
            m_Trigger = false;
            Hand.Release();
            if (m_Current != null) Finish(m_Current, "interrupted", reason, now);
        }

        void Finish(Batch batch, string state, string reason, float now)
        {
            batch.State = state;
            batch.Reason = reason;
            batch.Ended = now >= 0 ? now : batch.Started;
            Note($"lease {LeaseId} batch {batch.Seq} {batch.Id} {state}" + (reason != null ? $" ({reason})" : "") +
                $"; last sample {batch.LastApplied}, {batch.Applied.Count} of {batch.Edges.Length} edges, late {batch.LateMs:F0} ms");
            if (ReferenceEquals(batch, m_Current)) m_Current = null;
        }

        // Exactly one Sample of the virtual hand per frame, so Open Brush sees every edge.
        public void Tick(CHRISInputFrame frame)
        {
            if (!Active) return;
            if (frame.Revocation != null) { End(CHRISLeaseState.Revoked, frame.Revocation, frame.Now); return; }
            if (frame.Now >= m_Expiry)
            { End(CHRISLeaseState.Expired, m_TaskDeadline ? DeadlineReached : "Lease reached 30 s limit", frame.Now); return; }
            if (frame.Now - m_LastHeartbeat > HeartbeatSeconds) { End(CHRISLeaseState.Revoked, HeartbeatLost, frame.Now); return; }
            if (m_Current != null) Advance(m_Current, frame);
            Hand.Sample(m_Trigger, false, false, false, false, Vector2.zero);
        }

        // Linear position and shortest-path rotation between samples; the trigger takes at most
        // one transition per frame, in sample order, so bunched edges land on successive frames.
        void Advance(Batch batch, CHRISInputFrame frame)
        {
            if (batch.Started < 0) batch.Started = frame.Now;
            float elapsed = (frame.Now - batch.Started) * 1000f;
            var s = batch.Samples;
            int k = 0;
            while (k < s.Length - 1 && s[k + 1].T <= elapsed) k++;
            if (k >= s.Length - 1) Place(s[k].Position, s[k].Rotation);
            else
            {
                float u = (elapsed - s[k].T) / (s[k + 1].T - s[k].T);
                Quaternion a = s[k].Rotation, b = s[k + 1].Rotation;
                if (Quaternion.Dot(a, b) < 0) b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
                Place(Vector3.Lerp(s[k].Position, s[k + 1].Position, u), Quaternion.Slerp(a, b, u));
            }
            batch.LastApplied = k;
            if (batch.NextEdge < batch.Edges.Length)
            {
                var edge = s[batch.Edges[batch.NextEdge]];
                if (edge.T <= elapsed)
                {
                    float late = elapsed - edge.T;
                    batch.LateMs = Mathf.Max(batch.LateMs, late);
                    if (late > MaxLateMilliseconds) { Interrupt(batch, Late, frame.Now); return; }
                    // The guard runs just before a press: if what was observed has changed, the
                    // trigger stays released.
                    if (edge.Trigger && batch.Token != null && (TokenSource == null || TokenSource() != batch.Token))
                    { Interrupt(batch, PressToken, frame.Now); return; }
                    m_Trigger = edge.Trigger;
                    batch.Applied.Add(new Edge { Index = batch.Edges[batch.NextEdge], Trigger = edge.Trigger,
                        Frame = frame.Index, At = frame.Now });
                    batch.NextEdge++;
                }
            }
            // After the last sample the pose holds there with the trigger released.
            if (k >= s.Length - 1 && batch.NextEdge >= batch.Edges.Length) Finish(batch, "completed", null, frame.Now);
        }

        void Interrupt(Batch batch, string reason, float now)
        {
            m_Trigger = false;
            Finish(batch, "interrupted", reason, now);
        }

        static JArray Array(Vector3 v) => new JArray(v.x, v.y, v.z);
        static JArray Array(Quaternion q) => new JArray(q.x, q.y, q.z, q.w);

        // Delivery status. A completed batch says the inputs were applied, never that a task
        // succeeded. controller is the exact last applied root pose. The host adds host_session
        // and captured_at.
        public JObject Status(Func<float, double> unixTime) => new JObject
        {
            ["lease_id"] = LeaseId,
            ["task_id"] = TaskId,
            ["state"] = State.ToString().ToLowerInvariant(),
            ["reason"] = Reason,
            ["batches"] = new JArray(m_Batches.Select(b => new JObject
            {
                ["batch_id"] = b.Id, ["seq"] = b.Seq, ["state"] = b.State, ["reason"] = b.Reason,
                ["last_applied_index"] = b.LastApplied,
                ["edges"] = new JArray(b.Applied.Select(e => new JObject { ["index"] = e.Index, ["trigger"] = e.Trigger,
                    ["frame"] = e.Frame, ["at"] = unixTime(e.At) })),
                ["late_ms"] = Math.Round(b.LateMs, 1),
            })),
            ["controller"] = new JObject { ["position"] = Array(m_Position), ["rotation"] = Array(m_Rotation) },
        };

        // Acquire-time placement of the bounds: in front of the user, containing the controller.
        internal static string BoundsRefusal(Vector3 head, CHRISInputLimits limits, Vector3 start)
        {
            float distance = Vector3.Distance(head, limits.Center);
            if (distance < MinHeadDistanceM || distance > MaxHeadDistanceM) return HeadRefusal;
            if (Vector3.Distance(start, limits.Center) > limits.RadiusM) return OutsideRefusal;
            return null;
        }
    }
}
