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
    public enum CHRISLeaseState { Active, Released, Revoked, Expired }

    // What the host observed this frame. Tests construct it directly; the host fills it from
    // Open Brush. Target fields describe WantedTargetId only.
    public struct CHRISProcedureFrame
    {
        public float Now;               // realtime seconds
        public string Revocation;       // non-null: revoke the lease with this reason
        public string HoverTargetId;
        public bool TargetFound, TargetInteractable;
        public Vector3 TargetCenter, TargetForward;
        public Vector3 AttachLocalPosition;
        public Quaternion AttachLocalRotation;
    }

    // One bounded controller-procedure lease on the logical Brush hand. Pure state: the host
    // applies Hand to the brush controller and supplies observations. Python sends the steps;
    // every duration and release here is local and independent of Python replies.
    // The agent path never sets the brush, invokes a button callback or writes panel state:
    // the palette reacts to the virtual trigger exactly as it would to a physical one.
    public sealed class CHRISProcedureExecutor
    {
        public const float MaxLeaseSeconds = 30f;
        public const float HeartbeatSeconds = 1f;
        public const int MaxStepMilliseconds = 2000;
        public const int MaxSteps = 16;
        public const float PressHoldSeconds = 0.1f;
        public const float MaxPressHoldSeconds = 0.25f;
        public const int HoverFrames = 3;
        // Open Brush panel rays reach 4 units (Main.unity m_GazeControllerPointingDistance);
        // keep a margin. 10 units per metre is a code constant, not a measurement.
        public const float AimReachUnits = 3.5f;
        public const float ApproachUnits = 2f;
        public const float MaxApproachAngle = 60f;
        public const float MoveUnitsPerSecond = 10f;
        public const float TurnDegreesPerSecond = 360f;

        static readonly Regex s_Id = new Regex("\\A[A-Za-z0-9_-]{1,80}\\z");
        static readonly Regex s_Target = new Regex(
            "\\Abrush:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\z");

        sealed class Step
        {
            public string Id, Kind, Target, Status = "pending", Reason;
            public int Seq, Hovered, Frames;
            public float Started, Deadline, PressStart = -1, Held;
            public bool Released;
        }

        readonly List<Step> m_Steps = new List<Step>();
        float m_Expiry, m_LastHeartbeat, m_LastTick;
        Step m_Current;

        public string LeaseId { get; }
        public string TaskId { get; }
        public JObject ReturnMode { get; }
        public CHRISLeaseState State { get; private set; } = CHRISLeaseState.Active;
        public string Reason { get; private set; }
        public bool Active => State == CHRISLeaseState.Active;
        public CHRISVirtualHand Hand { get; } = new CHRISVirtualHand();
        public Vector3 Origin { get; }
        public string WantedTargetId => m_Current?.Target;
        public bool ButtonsNeutral => Hand.IsNeutral;
        // Optional evidence sink; the host logs one line per step start and end.
        public Action<string> Log;
        void Note(string message) { if (Log != null) Log(message); }

        public CHRISProcedureExecutor(string leaseId, string taskId, JObject returnMode,
            Vector3 position, Quaternion rotation, float now)
        {
            LeaseId = leaseId;
            TaskId = taskId;
            ReturnMode = returnMode;
            Hand.Position = Origin = position;
            Hand.Rotation = rotation;
            m_Expiry = now + MaxLeaseSeconds;
            m_LastHeartbeat = m_LastTick = now;
        }

        public float SecondsLeft(float now) => Mathf.Max(0, m_Expiry - now);

        public void Heartbeat(float now)
        {
            if (Active) m_LastHeartbeat = now;
        }

        // Message shape only, as the shared procedure cases describe (stateless).
        public static string ValidateStep(JObject step)
        {
            if (step == null) return "Invalid step";
            string kind = step["kind"]?.Type == JTokenType.String ? (string)step["kind"] : null;
            string[] fields = kind == "aim" ? new[] { "step_id", "seq", "kind", "target_id", "timeout_ms" } :
                kind == "press" ? new[] { "step_id", "seq", "kind", "button", "expected_hover_target_id", "timeout_ms" } : null;
            if (fields == null) return "Unknown step kind";
            if (!step.Properties().All(p => fields.Contains(p.Name)) || !fields.All(f => step[f] != null))
                return "Invalid step fields";
            if (step["step_id"].Type != JTokenType.String || !s_Id.IsMatch((string)step["step_id"]))
                return "Invalid step_id";
            if (step["seq"].Type != JTokenType.Integer || step["timeout_ms"].Type != JTokenType.Integer)
                return "Invalid step numbers";
            long seq = (long)step["seq"], timeout = (long)step["timeout_ms"];
            if (timeout < 1 || timeout > MaxStepMilliseconds) return "timeout_ms out of range";
            if (seq < 1 || seq > MaxSteps) return "seq out of range";
            var target = step[kind == "aim" ? "target_id" : "expected_hover_target_id"];
            if (target.Type != JTokenType.String || !s_Target.IsMatch((string)target)) return "Invalid target_id";
            if (kind == "press" && (step["button"].Type != JTokenType.String || (string)step["button"] != "trigger"))
                return "Unsupported button";
            return null;
        }

        // Queues one step. Returns an error reason, or null once queued. A rejected step does
        // not consume its seq; steps are never resent, so a repeated seq or step_id is refused.
        public string Submit(JObject step, float now)
        {
            Heartbeat(now);
            if (!Active) return "Lease is not active";
            string invalid = ValidateStep(step);
            if (invalid != null) return invalid;
            string id = (string)step["step_id"], kind = (string)step["kind"];
            if ((long)step["seq"] != m_Steps.Count + 1) return "Unexpected seq";
            if (m_Steps.Any(s => s.Id == id)) return "Duplicate step_id";
            if (m_Current != null) return "Another step is in progress";
            if (m_Steps.Count >= MaxSteps) return "Step limit reached";
            m_Current = new Step { Id = id, Kind = kind, Seq = m_Steps.Count + 1,
                Target = (string)step[kind == "aim" ? "target_id" : "expected_hover_target_id"],
                Started = now, Deadline = now + (long)step["timeout_ms"] / 1000f };
            m_Steps.Add(m_Current);
            Note($"lease {LeaseId} step {m_Current.Seq} {kind} {m_Current.Target} started");
            return null;
        }

        public void Revoke(string reason) => End(CHRISLeaseState.Revoked, reason);
        public void Release() => End(CHRISLeaseState.Released, "Released by procedure");

        void End(CHRISLeaseState state, string reason)
        {
            if (!Active) return;
            State = state;
            Reason = reason;
            Hand.Release();
            if (m_Current != null) Finish(m_Current, "cancelled", reason);
        }

        void Finish(Step step, string status, string reason)
        {
            step.Status = status;
            step.Reason = reason;
            Note($"lease {LeaseId} step {step.Seq} {step.Kind} {status}" + (reason != null ? $" ({reason})" : "") +
                $"; {step.Frames} frames, {(m_LastTick - step.Started) * 1000:F0} ms" +
                (step.Kind == "aim" ? $", hover frames {step.Hovered}" : $", trigger held {step.Held * 1000:F0} ms"));
            if (ReferenceEquals(step, m_Current)) m_Current = null;
        }

        public void Tick(CHRISProcedureFrame frame)
        {
            float dt = Mathf.Clamp(frame.Now - m_LastTick, 0, 0.1f);
            m_LastTick = frame.Now;
            if (!Active) return;
            if (frame.Revocation != null) { Revoke(frame.Revocation); return; }
            if (frame.Now >= m_Expiry) { End(CHRISLeaseState.Expired, "Lease reached 30 s limit"); return; }
            if (frame.Now - m_LastHeartbeat > HeartbeatSeconds) { Revoke("Heartbeat lost"); return; }
            var step = m_Current;
            if (step == null) { Hand.Release(); return; }
            step.Frames++;
            if (step.Kind == "aim") TickAim(step, frame, dt);
            else TickPress(step, frame);
        }

        void TickAim(Step step, CHRISProcedureFrame frame, float dt)
        {
            // No button can be down while aiming, so a ray sweep over other buttons only hovers.
            Hand.Release();
            if (!frame.TargetFound) { Finish(step, "failed", "Target not on the current palette page"); return; }
            step.Hovered = frame.TargetInteractable && frame.HoverTargetId == step.Target ? step.Hovered + 1 : 0;
            if (step.Hovered >= HoverFrames) { Finish(step, "succeeded", null); return; }
            if (frame.Now >= step.Deadline)
            { Finish(step, "timeout", frame.TargetInteractable ? "Target not hovered in time" : "Target not interactable"); return; }
            if (!frame.TargetInteractable) return;
            Vector3 position = Hand.Position;
            Quaternion rotation = Hand.Rotation;
            AimTowards(ref position, ref rotation, frame.AttachLocalPosition, frame.AttachLocalRotation,
                frame.TargetCenter, frame.TargetForward, Origin, dt);
            Hand.Position = position;
            Hand.Rotation = rotation;
        }

        void TickPress(Step step, CHRISProcedureFrame frame)
        {
            // The pose stays where aim left it for the whole press.
            if (step.PressStart < 0)
            {
                if (frame.HoverTargetId != step.Target)
                { Hand.Release(); Finish(step, "rejected", "Hover target differs from expected target"); return; }
                step.PressStart = frame.Now;
                Hand.Sample(true, false, false, false, false, Vector2.zero);
                return;
            }
            if (!step.Released)
            {
                float held = frame.Now - step.PressStart;
                bool timedOut = frame.Now >= step.Deadline;
                if (held >= PressHoldSeconds || held >= MaxPressHoldSeconds || timedOut)
                {
                    step.Held = held;
                    Hand.Release();
                    step.Released = true;
                    if (timedOut) Finish(step, "timeout", "Step deadline reached; trigger released");
                }
                else Hand.Sample(true, false, false, false, false, Vector2.zero);
                return;
            }
            // One frame with the release edge has been presented; the press is complete.
            Hand.Release();
            Finish(step, "succeeded", null);
        }

        // Moves the controller only when the target is beyond reach or at a grazing angle, then
        // turns the pointer attach point toward the target. Both are rate-limited.
        internal static void AimTowards(ref Vector3 position, ref Quaternion rotation,
            Vector3 attachLocalPosition, Quaternion attachLocalRotation, Vector3 target,
            Vector3 targetForward, Vector3 origin, float dt)
        {
            Vector3 forward = targetForward.sqrMagnitude > 1e-6f ? targetForward.normalized : Vector3.forward;
            Vector3 attach = position + rotation * attachLocalPosition;
            Vector3 toTarget = target - attach;
            if (toTarget.magnitude > AimReachUnits || Vector3.Angle(toTarget, forward) > MaxApproachAngle)
            {
                Vector3 goal = target - forward * ApproachUnits - (attach - position);
                position = CHRISBimanualInput.ClampToOrigin(Vector3.MoveTowards(position, goal, MoveUnitsPerSecond * dt),
                    origin, CHRISBimanualInput.MaxHandOffsetUnits);
                toTarget = target - (position + rotation * attachLocalPosition);
            }
            if (toTarget.sqrMagnitude < 1e-8f) return;
            Vector3 up = Mathf.Abs(Vector3.Dot(toTarget.normalized, Vector3.up)) > 0.99f ? forward : Vector3.up;
            Quaternion goalRotation = Quaternion.LookRotation(toTarget, up) * Quaternion.Inverse(attachLocalRotation);
            rotation = Quaternion.RotateTowards(rotation, goalRotation, TurnDegreesPerSecond * dt);
        }

        public JObject Status() => new JObject
        {
            ["lease_id"] = LeaseId,
            ["task_id"] = TaskId,
            ["state"] = State.ToString().ToLowerInvariant(),
            ["reason"] = Reason,
            ["steps"] = new JArray(m_Steps.Select(s => new JObject
            {
                ["step_id"] = s.Id, ["seq"] = s.Seq, ["kind"] = s.Kind,
                ["status"] = s.Status, ["reason"] = s.Reason,
            })),
        };
    }
}
