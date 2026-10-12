// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

namespace TiltBrush
{
    public enum CHRISHandOwner { Physical, Mapped, Procedure, Agent }

    // What must hold before a procedure may take the Brush hand. Plain data so the refusal
    // order is testable without Open Brush running.
    internal struct CHRISAcquireFacts
    {
        public bool Focused, Busy, LeaseActive, InputLeaseActive, KeyboardUI, MappingChangePending, LegacyBrushPose,
            PhysicalHeld, MappedHeld, MouseHeld, ControllersReady;
    }

    // The single answer to "who owns each hand" for the native input path. Physical input,
    // the keyboard/mouse mapping and one bounded procedure lease never own the same channel.
    // CHRISBimanualHost applies the answer; the procedure only ever takes the logical Brush hand
    // (pose and trigger) and the Wand stays with its current owner.
    // Two kinds of lease share the one Brush slot and exclude each other: the legacy procedure
    // lease (aim, press, stroke) and the input-v0.2 lease, which plays agent-chosen poses and
    // trigger samples through CHRISInputExecutor and nothing else.
    public static class CHRISHandAuthority
    {
        const int MaxLedger = 64;
        static CHRISProcedureExecutor s_Lease;
        static readonly Dictionary<string, CHRISProcedureExecutor> s_Ledger = new Dictionary<string, CHRISProcedureExecutor>();
        static CHRISInputExecutor s_Input;
        static readonly Dictionary<string, CHRISInputExecutor> s_InputLedger = new Dictionary<string, CHRISInputExecutor>();
        // The hand of whichever lease began last, and whether that lease was an input lease.
        static CHRISVirtualHand s_Hand;
        static bool s_InputOwns;
        static JObject s_LastReturnMode;
        static ControllerInfo s_BrushController;
        static bool s_WandOnRight;
        static CHRISInputMapping s_Mapping;
        static CHRISControlMode s_Mode;
        static bool s_HandBackPending, s_RecenterPending;
        static Vector3 s_MappedBrushPosition;
        static Quaternion s_MappedBrushRotation;
        static int s_GraceUntilFrame = -1;
        static bool s_Ended;
        static Vector3 s_AttachPosition;
        static Quaternion s_AttachRotation = Quaternion.identity;

        static bool ProcedureActive => s_Lease?.Active == true;
        public static bool InputActive => s_Input?.Active == true;
        public static bool LeaseActive => ProcedureActive || InputActive;
        // The lease keeps presenting its released hand for one frame after it ends, so Open
        // Brush sees the release edge before input returns to the previous owner.
        public static bool OwnsBrush => LeaseActive || Time.frameCount <= s_GraceUntilFrame;
        public static CHRISVirtualHand BrushHand => OwnsBrush ? s_Hand : null;
        // Whether an input lease (not a procedure lease) presents the Brush hand.
        public static bool AgentOwnsBrush => OwnsBrush && s_InputOwns;

        public static CHRISHandOwner Owner(bool brush, bool mappedOwnsPose) =>
            brush && OwnsBrush ? (s_InputOwns ? CHRISHandOwner.Agent : CHRISHandOwner.Procedure) :
            mappedOwnsPose ? CHRISHandOwner.Mapped : CHRISHandOwner.Physical;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetForPlay()
        {
            s_Lease = null;
            s_Ledger.Clear();
            s_Input = null;
            s_InputLedger.Clear();
            s_Hand = null;
            s_InputOwns = false;
            s_LastReturnMode = null;
            s_TrackingLogged = false;
            s_BrushController = null;
            s_Mapping = null;
            s_GraceUntilFrame = -1;
            s_Ended = false;
        }

        internal static string AcquireRefusal(CHRISAcquireFacts f)
        {
            if (!f.ControllersReady) return "Controllers unavailable";
            if (!f.Focused) return "Not focused";
            if (f.LeaseActive) return "Another procedure lease is active";
            if (f.InputLeaseActive) return "An input lease is active";
            if (f.Busy) return "Stroke, grab or widget interaction in progress";
            if (f.KeyboardUI) return "Keyboard UI pointer is active";
            if (f.MappingChangePending) return "Mapping change pending";
            if (f.LegacyBrushPose) return "move_brush mapping owns the brush pose";
            if (f.PhysicalHeld) return "Physical brush controller input held";
            if (f.MappedHeld) return "Mapped brush input held";
            if (f.MouseHeld) return "Mouse button held";
            return null;
        }

        // A mapped mode, recenter or hand-back key pressed during the lease takes control back.
        // Compared with the values at acquire: focus loss sets HandBackPending on its own, and a
        // flag already set when the lease began is not a new request.
        internal static string MappedTakeover(CHRISControlMode modeAtAcquire, bool handBackAtAcquire,
            bool recenterAtAcquire, CHRISControlMode mode, bool handBack, bool recenter) =>
            mode != modeAtAcquire || handBack != handBackAtAcquire || recenter != recenterAtAcquire
                ? "Manual takeover" : null;

        // Open Brush also reads mouse buttons as Activate; any press ends the lease.
        static bool MouseHeld() => Mouse.current != null && (Mouse.current.leftButton.isPressed ||
            Mouse.current.rightButton.isPressed || Mouse.current.middleButton.isPressed);

        static void Log(string message) => Debug.Log("CHRIS procedure: " + message);

        static bool ControllersPresent => InputManager.m_Instance != null && InputManager.Controllers != null;

        static UnityXRControllerInfo BrushXR => !ControllersPresent ? null : InputManager.Brush as UnityXRControllerInfo;

        // D62: test Start, Approve and Decline count only from the user's own brush controller.
        // Any virtual source of the same click (a procedure lease, the keyboard/mouse mapping's
        // UI pointer, recovery UI, mapped draw keys or a mouse button) makes it not physical.
        internal static bool PhysicalClick(bool rawTrigger, bool leaseOwnsBrush, bool mouseHeld, bool mappedDraw,
            bool keyboardUI, bool recoveryUI, bool mappedBrushPose) =>
            rawTrigger && !leaseOwnsBrush && !mouseHeld && !mappedDraw && !keyboardUI && !recoveryUI && !mappedBrushPose;

        // Tests replace the live source; null in normal use.
        internal static Func<bool> PhysicalClickSource;

        public static bool IsPhysicalBrushClick()
        {
            if (PhysicalClickSource != null) return PhysicalClickSource();
            var xr = BrushXR;
            return PhysicalClick(xr != null && xr.RawVrInput(VrInput.Trigger), OwnsBrush, MouseHeld(),
                CHRISInputMappingHost.Remap.DrawHeld, CHRISBimanualHost.InUIMode, CHRISBimanualHost.RecoveryUI,
                CHRISBimanualHost.OwnsBrushPose);
        }

        static bool PhysicalHeld()
        {
            var xr = BrushXR;
            return xr != null && (xr.RawVrInput(VrInput.Trigger) || xr.RawVrInput(VrInput.Grip));
        }

        static bool PhysicalTracked()
        {
            var xr = BrushXR;
            return xr != null && InputDevices.GetDeviceAtXRNode(xr.PhysicalRightHand ? XRNode.RightHand : XRNode.LeftHand).isValid;
        }

        static UnityEngine.XR.InputDevice BrushDevice()
        {
            var xr = BrushXR;
            return xr == null ? default : InputDevices.GetDeviceAtXRNode(xr.PhysicalRightHand ? XRNode.RightHand : XRNode.LeftHand);
        }

        // input-v0.2 hand-back: a physical controller counts only while it reports isTracked
        // with both position and rotation tracking. A runtime that does not report a feature
        // (null) is not held to it, so the check never waits forever on a missing usage.
        internal static bool PoseTracked(bool valid, bool? isTracked, InputTrackingState? state) =>
            valid && isTracked != false && (state == null || (state.Value & (InputTrackingState.Position | InputTrackingState.Rotation)) ==
                (InputTrackingState.Position | InputTrackingState.Rotation));

        static bool s_TrackingLogged;

        static bool PhysicalPoseTracked()
        {
            var device = BrushDevice();
            if (!device.isValid) return false;
            bool? tracked = device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool t) ? t : (bool?)null;
            InputTrackingState? state = device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trackingState, out InputTrackingState st)
                ? st : (InputTrackingState?)null;
            if (!s_TrackingLogged)
            {
                s_TrackingLogged = true;
                Log($"brush tracking features on {device.name}: isTracked {(tracked.HasValue ? tracked.ToString() : "not reported")}, " +
                    $"trackingState {(state.HasValue ? state.ToString() : "not reported")}");
            }
            return PoseTracked(true, tracked, state);
        }

        // The controller's own release gate (it waits for every button and axis to be neutral
        // after CHRIS used the hand). Read without changing the controller code.
        static readonly System.Reflection.FieldInfo s_AwaitNeutral = typeof(UnityXRControllerInfo).GetField(
            "m_PhysicalAwaitNeutral", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        static bool PhysicalReleaseGated()
        {
            var xr = BrushXR;
            return xr != null && s_AwaitNeutral != null && (bool)s_AwaitNeutral.GetValue(xr);
        }

        // Stick, stick click and face buttons on the leased hand. Trigger and grip are PhysicalHeld.
        static bool PhysicalOtherInput()
        {
            var xr = BrushXR;
            if (xr == null) return false;
            if (xr.RawVrInput(VrInput.Thumbstick) || xr.RawVrInput(VrInput.Button01) || xr.RawVrInput(VrInput.Button02)) return true;
            var device = BrushDevice();
            return device.isValid && device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 stick) && stick.sqrMagnitude > 0.04f;
        }

        // The physical controller's own pose in the room frame (metres), whoever owns the hand.
        internal static bool TrackedPose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            var brush = !ControllersPresent ? null : InputManager.Brush;
            var device = BrushDevice();
            if (brush == null || !PhysicalPoseTracked() ||
                !device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out Vector3 local) ||
                !device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out Quaternion localRotation)) return false;
            var parent = brush.Behavior.transform.parent;
            position = (parent != null ? parent.TransformPoint(local) : local) / App.METERS_TO_UNITS;
            rotation = parent != null ? parent.rotation * localRotation : localRotation;
            return true;
        }

        static bool MappedOwnsBrush => CHRISBimanualHost.MappingActive &&
            (CHRISBimanualHost.OwnsBrushPose || CHRISBimanualHost.InUIMode);

        static JObject ReturnMode()
        {
            if (!MappedOwnsBrush)
                return new JObject { ["source"] = "physical", ["mode"] = null, ["selected_hand"] = null };
            var input = CHRISBimanualHost.Input;
            return new JObject { ["source"] = "mapped", ["mode"] = input.Mode.ToString().ToLowerInvariant(),
                ["selected_hand"] = input.Selected };
        }

        // Called by the gateway after session, epoch, revision and one-shot exclusion checks.
        internal static JObject Acquire(string taskId, bool busy, out string refusal) =>
            Acquire(taskId, busy, CHRISProcedureExecutor.PaletteScope, out refusal);

        // D131: a renew is checked like an acquire, except that its own lease does not count.
        internal static string RenewRefusal(bool busy) => LiveAcquireRefusal(busy, renewing: true);

        static string LiveAcquireRefusal(bool busy, bool renewing = false)
        {
            var bimanual = CHRISBimanualHost.Input;
            return AcquireRefusal(new CHRISAcquireFacts
            {
                ControllersReady = ControllersPresent &&
                    InputManager.Brush != null && InputManager.Wand != null && ViewpointScript.Head != null,
                Focused = CHRISInputMappingHost.InputFocusedNow,
                LeaseActive = !renewing && OwnsBrush && !s_InputOwns,
                InputLeaseActive = !renewing && OwnsBrush && s_InputOwns,
                Busy = busy,
                KeyboardUI = CHRISBimanualHost.InUIMode || CHRISBimanualHost.RecoveryUI,
                MappingChangePending = CHRISInputMappingHost.MappingChangePending,
                LegacyBrushPose = CHRISInputMappingHost.BrushPoseOwned,
                PhysicalHeld = PhysicalHeld(),
                MappedHeld = CHRISInputMappingHost.Remap.DrawHeld || (CHRISBimanualHost.MappingActive && !bimanual.Brush.IsNeutral),
                MouseHeld = MouseHeld(),
            });
        }

        // Where the leased hand starts, in Open Brush units.
        static void StartPose(bool requirePoseTracking, out Vector3 position, out Quaternion rotation)
        {
            var bimanual = CHRISBimanualHost.Input;
            var brush = InputManager.Brush;
            if (MappedOwnsBrush) { position = bimanual.Brush.Position; rotation = bimanual.Brush.Rotation; }
            else if (requirePoseTracking ? PhysicalPoseTracked() : PhysicalTracked())
            { position = brush.Transform.position; rotation = brush.Transform.rotation; }
            else
            {
                // A resting controller's last transform may be anywhere; start where the
                // two-hand mapping would place the Brush hand.
                var head = ViewpointScript.Head;
                Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
                forward = forward.sqrMagnitude < 0.01f ? Vector3.forward : forward.normalized;
                Vector3 right = Vector3.Cross(Vector3.up, forward) * (InputManager.m_Instance.WandOnRight ? -1f : 1f);
                position = head.position + forward * 5f + right * 2f - Vector3.up;
                rotation = Quaternion.LookRotation(forward, Vector3.up);
            }
        }

        // What takeover detection compares against for the rest of the lease.
        static void CaptureOwners()
        {
            var bimanual = CHRISBimanualHost.Input;
            s_BrushController = InputManager.Brush;
            s_WandOnRight = InputManager.m_Instance.WandOnRight;
            s_Mapping = CHRISInputMappingHost.Remap.Active;
            s_Mode = bimanual.Mode;
            s_HandBackPending = bimanual.HandBackPending;
            s_RecenterPending = bimanual.RecenterPending;
            s_MappedBrushPosition = bimanual.Brush.Position;
            s_MappedBrushRotation = bimanual.Brush.Rotation;
        }

        static bool PhysicalRight() => InputManager.Brush is UnityXRControllerInfo xr ? xr.PhysicalRightHand : !s_WandOnRight;

        // A draw lease also opens (or continues) the task's drawing run; its first lease fixes
        // the frame in front of the head. The gateway has already checked the sketch guard.
        internal static JObject Acquire(string taskId, bool busy, string scope, out string refusal)
        {
            refusal = LiveAcquireRefusal(busy);
            if (refusal != null) Log($"acquire refused for task {taskId}: {refusal}");
            if (refusal != null) return null;
            StartPose(false, out Vector3 position, out Quaternion rotation);
            var viewpoint = ViewpointScript.Head;
            bool box = scope == CHRISProcedureExecutor.Draw3dScope;
            var run = box || scope == CHRISProcedureExecutor.DrawScope
                ? CHRISDrawingRuns.Begin(taskId, viewpoint.position, viewpoint.forward, Time.realtimeSinceStartup, true, box) : null;
            var lease = Begin(taskId, ReturnMode(), position, rotation, run);
            CaptureOwners();
            Log($"lease {lease.LeaseId} acquired for task {taskId}; return {lease.ReturnMode["source"]}" +
                (run != null ? $"; draw {(run.Frame.Box ? "box" : "frame")} {run.Frame.Id}, {run.StrokesStarted} strokes so far" : ""));
            return Grant(lease, PhysicalRight());
        }

        // input-v0.2. The gateway has already checked session, epoch, tombstone, deadline and
        // the limits' shape; this checks the hand itself and where the bounds sit.
        internal static CHRISInputExecutor AcquireInput(string taskId, bool busy, CHRISInputLimits limits, out string refusal)
        {
            refusal = LiveAcquireRefusal(busy);
            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            if (refusal == null)
            {
                StartPose(true, out position, out rotation);
                refusal = CHRISInputExecutor.BoundsRefusal(ViewpointScript.Head.position / App.METERS_TO_UNITS, limits,
                    position / App.METERS_TO_UNITS);
            }
            if (refusal != null) { Log($"input acquire refused for task {taskId}: {refusal}"); return null; }
            var lease = BeginInput(taskId, ReturnMode(), CHRISRoomFrame.Epoch, limits, position / App.METERS_TO_UNITS, rotation);
            CaptureOwners();
            Log($"input lease {lease.LeaseId} acquired for task {taskId}; return {lease.ReturnMode["source"]}; " +
                $"frame epoch {lease.FrameEpoch}, bounds {limits.RadiusM:F2} m, {limits.MaxSpeedMps:F2} m/s, {limits.MaxTurnDps:F0} deg/s");
            return lease;
        }

        // Starts an input lease record itself; the gateway test host calls this without controllers.
        internal static CHRISInputExecutor BeginInput(string taskId, JObject returnMode, long frameEpoch, CHRISInputLimits limits,
            Vector3 position, Quaternion rotation)
        {
            if (s_InputLedger.Count >= MaxLedger) s_InputLedger.Clear();
            s_Input = new CHRISInputExecutor(Guid.NewGuid().ToString("N"), taskId, returnMode, frameEpoch, limits,
                position, rotation, Time.realtimeSinceStartup);
            s_InputLedger[s_Input.LeaseId] = s_Input;
            s_Input.Log = message => Debug.Log("CHRIS input: " + message);
            s_Hand = s_Input.Hand;
            s_InputOwns = true;
            s_LastReturnMode = returnMode;
            s_Ended = false;
            return s_Input;
        }

        internal static JObject PhysicalHand() => new JObject { ["hand_physical"] = PhysicalRight() ? "R" : "L" };

        // Starts the lease record itself; the gateway test host calls this without controllers.
        internal static CHRISProcedureExecutor Begin(string taskId, JObject returnMode, Vector3 position, Quaternion rotation,
            CHRISDrawingRuns.Run run = null)
        {
            if (s_Ledger.Count >= MaxLedger) s_Ledger.Clear();
            s_Lease = run == null
                ? new CHRISProcedureExecutor(Guid.NewGuid().ToString("N"), taskId, returnMode, position, rotation, Time.realtimeSinceStartup)
                : new CHRISProcedureExecutor(Guid.NewGuid().ToString("N"), taskId, returnMode, position, rotation,
                    Time.realtimeSinceStartup, run.Frame.Box ? CHRISProcedureExecutor.Draw3dScope : CHRISProcedureExecutor.DrawScope, run.Frame,
                    CHRISDrawingRuns.MaxStrokesPerRun - run.StrokesStarted);
            s_Ledger[s_Lease.LeaseId] = s_Lease;
            s_Lease.Log = Log;
            s_Hand = s_Lease.Hand;
            s_InputOwns = false;
            s_LastReturnMode = returnMode;
            s_Ended = false;
            return s_Lease;
        }

        internal static JObject Grant(CHRISProcedureExecutor lease, bool physicalRight) => new JObject
        {
            ["lease_id"] = lease.LeaseId,
            ["task_id"] = lease.TaskId,
            ["hand"] = "brush",
            ["hand_physical"] = physicalRight ? "R" : "L",
            ["return_mode"] = lease.ReturnMode.DeepClone(),
        };

        // The task of the active procedure lease (drawing runs belong to these only).
        internal static string ActiveTaskId => ProcedureActive ? s_Lease.TaskId : null;
        internal static string InputTaskId => InputActive ? s_Input.TaskId : null;
        internal static string InputLeaseId => InputActive ? s_Input.LeaseId : null;
        internal static bool LastReturnPhysical => s_LastReturnMode != null && (string)s_LastReturnMode["source"] == "physical";

        // The logical Brush hand has no trigger or grip held by any source.
        public static bool BrushButtonsNeutral => !PhysicalHeld() && !CHRISInputMappingHost.Remap.DrawHeld &&
            (BrushHand?.IsNeutral ?? true) && (!CHRISBimanualHost.MappingActive || CHRISBimanualHost.Input.Brush.IsNeutral);

        internal static CHRISProcedureExecutor Find(string leaseId) =>
            leaseId != null && s_Ledger.TryGetValue(leaseId, out var lease) ? lease : null;

        internal static CHRISInputExecutor FindInput(string leaseId) =>
            leaseId != null && s_InputLedger.TryGetValue(leaseId, out var lease) ? lease : null;

        public static void Revoke(string reason)
        {
            if (ProcedureActive)
            {
                s_Lease.Revoke(reason);
                Log($"lease {s_Lease.LeaseId} revoked: {reason}");
                OnEnded();
            }
            // Every external end (Stop, Escape, cancel, host close) is "Stop" on the input wire.
            if (InputActive) RevokeInput(CHRISInputExecutor.Stop);
        }

        internal static void RevokeInput(string reason)
        {
            if (!InputActive) return;
            s_Input.Revoke(reason);
            Log($"input lease {s_Input.LeaseId} revoked: {reason}");
            OnInputEnded();
        }

        internal static void Release(CHRISInputExecutor lease)
        {
            if (!lease.Active) return;
            lease.Release();
            Log($"input lease {lease.LeaseId} released");
            if (ReferenceEquals(lease, s_Input)) OnInputEnded();
        }

        internal static void Release(CHRISProcedureExecutor lease)
        {
            if (!lease.Active) return;
            lease.Release();
            Log($"lease {lease.LeaseId} released");
            if (ReferenceEquals(lease, s_Lease)) OnEnded();
        }

        static void OnEnded()
        {
            if (s_Lease != null && s_Lease.Draws)
            {
                if (s_Lease.State == CHRISLeaseState.Released)
                    CHRISDrawingRuns.LeaseEnded(s_Lease.TaskId, s_Lease.StrokesStarted, Time.realtimeSinceStartup);
                else CHRISDrawingRuns.EndRun(s_Lease.TaskId);
            }
            HandBack();
        }

        // Kept apart from OnEnded so the input path never reaches drawing-run code.
        static void OnInputEnded()
        {
            ReturnMappedPose();
            HandBack();
        }

        static void HandBack()
        {
            s_GraceUntilFrame = Time.frameCount + 1;
            s_Ended = true;
            // Returning hands need a fresh press before physical input counts again.
            BrushXR?.RequirePhysicalRelease();
        }

        // A mapped return continues from the last applied pose, clamped into the mapping's reach
        // around the Brush hand's own origin. Units.
        internal static Vector3 MappedReturnPosition(Vector3 appliedMeters, Vector3 originUnits) =>
            CHRISBimanualInput.ClampToOrigin(appliedMeters * App.METERS_TO_UNITS, originUnits, CHRISBimanualInput.MaxHandOffsetUnits);

        static void ReturnMappedPose()
        {
            if (s_Input == null || (string)s_Input.ReturnMode["source"] != "mapped" || !MappedOwnsBrush) return;
            var hand = CHRISBimanualHost.Input.Brush;
            hand.Position = MappedReturnPosition(s_Input.AppliedPosition, hand.Origin);
            hand.Rotation = s_Input.AppliedRotation;
        }

        // True once after a lease ends, so the host can latch keys still held for the mapping.
        internal static bool TakeEnded()
        {
            bool ended = s_Ended;
            s_Ended = false;
            return ended;
        }

        // After a lease ends: the physical hand is not yet back while CHRIS still owns its pose
        // or the controller is not tracked. A mapped return is immediate unless mapping stopped.
        internal static bool HandBackPending(CHRISProcedureExecutor lease)
        {
            if (lease.Active || ReferenceEquals(lease, s_Lease) && OwnsBrush) return true;
            if ((string)lease.ReturnMode["source"] == "mapped" && MappedOwnsBrush) return false;
            return CHRISBimanualHost.OwnsBrushPose || !PhysicalTracked();
        }

        internal static JObject RestoredMode(CHRISProcedureExecutor lease)
        {
            if (lease.Active) return null;
            if ((string)lease.ReturnMode["source"] == "mapped" && MappedOwnsBrush) return ReturnMode();
            return new JObject { ["source"] = "physical", ["mode"] = null, ["selected_hand"] = null };
        }

        // input-v0.2 physical hand-back is pending while CHRIS still holds the pose driver, the
        // controller is not tracked in position and rotation, or its release gate is closed.
        internal static bool PhysicalHandBackPending(bool ownsPose, bool poseTracked, bool releaseGated) =>
            ownsPose || !poseTracked || releaseGated;

        internal static bool HandBackPending(CHRISInputExecutor lease)
        {
            if (lease.Active || ReferenceEquals(lease, s_Input) && OwnsBrush) return true;
            if ((string)lease.ReturnMode["source"] == "mapped" && MappedOwnsBrush) return false;
            return PhysicalHandBackPending(CHRISBimanualHost.OwnsBrushPose, PhysicalPoseTracked(), PhysicalReleaseGated());
        }

        internal static JObject RestoredMode(CHRISInputExecutor lease)
        {
            if (lease.Active) return null;
            if ((string)lease.ReturnMode["source"] == "mapped" && MappedOwnsBrush) return ReturnMode();
            return new JObject { ["source"] = "physical", ["mode"] = null, ["selected_hand"] = null };
        }

        // The owner of each hand for observation: agent (any lease), mapped or physical.
        internal static JObject Owners()
        {
            string brush = OwnsBrush ? "agent" : MappedOwnsBrush ? "mapped" : "physical";
            string wand = CHRISBimanualHost.InUIMode || CHRISBimanualHost.OwnsWandPose ? "mapped" : "physical";
            return new JObject { ["brush"] = brush, ["wand"] = wand };
        }

        // The logical Brush trigger as Open Brush receives it, from whichever source owns it.
        internal static bool BrushTriggerHeld()
        {
            if (OwnsBrush) return s_Hand?.TriggerHeld == true;
            if (CHRISInputMappingHost.Remap.DrawHeld) return true;
            if (MappedOwnsBrush) return CHRISBimanualHost.Input.Brush.TriggerHeld;
            var xr = BrushXR;
            return xr != null && xr.RawVrInput(VrInput.Trigger);
        }

        internal static JObject Pointer(CHRISProcedureExecutor lease)
        {
            if (!lease.Active) return null;
            var hand = lease.Hand;
            Vector3 origin = hand.Position + hand.Rotation * s_AttachPosition;
            Vector3 direction = hand.Rotation * s_AttachRotation * Vector3.forward;
            return new JObject { ["origin"] = new JArray(origin.x, origin.y, origin.z),
                ["direction"] = new JArray(direction.x, direction.y, direction.z) };
        }

        // Advances the lease once per frame before the host writes poses. Returns whether the
        // procedure owns the Brush hand this frame.
        // A draw lease's own stroke is not a takeover: every other way to start one (physical
        // trigger, mapped draw, mouse) revokes the lease on its own.
        internal static bool Tick(bool focused, bool strokeInProgress, bool interacting, ICHRISInputState input)
        {
            CHRISRoomFrame.Poll();
            if (InputActive) return TickInput(focused, interacting, input);
            if (!LeaseActive) return OwnsBrush;
            bool busy = interacting || strokeInProgress && !s_Lease.Draws;
            var frame = new CHRISProcedureFrame { Now = Time.realtimeSinceStartup, Revocation = Revocation(focused, busy, input) };
            var brush = !ControllersPresent ? null : InputManager.Brush;
            var attach = brush?.Geometry != null ? brush.Geometry.PointerAttachPoint : null;
            if (attach != null)
            {
                var xf = brush.Behavior.transform;
                var inverse = Quaternion.Inverse(xf.rotation);
                s_AttachPosition = inverse * (attach.position - xf.position);
                s_AttachRotation = inverse * attach.rotation;
            }
            frame.AttachLocalPosition = s_AttachPosition;
            frame.AttachLocalRotation = s_AttachRotation;
            if (frame.Revocation == null && s_Lease.WantedTargetId != null)
            {
                frame.HoverTargetId = CHRISPaletteObserver.HoverTargetId();
                frame.TargetFound = CHRISPaletteObserver.TryResolve(s_Lease.WantedTargetId,
                    out frame.TargetCenter, out frame.TargetForward, out frame.TargetInteractable);
            }
            s_Lease.Tick(frame);
            if (!s_Lease.Active)
            {
                Log($"lease {s_Lease.LeaseId} {s_Lease.State.ToString().ToLowerInvariant()}: {s_Lease.Reason}");
                OnEnded();
            }
            return true;
        }

        // The agent's own trigger never revokes its lease (a stroke or a pressed button is what it
        // asked for); every other way to start one is a takeover by itself. No motion-only takeover.
        static bool TickInput(bool focused, bool interacting, ICHRISInputState input)
        {
            string revocation = InputRevocation(s_Input.FrameEpoch, CHRISRoomFrame.Epoch,
                () => Revocation(focused, interacting, input) ?? (PhysicalOtherInput() ? "Physical input on the leased hand" : null));
            s_Input.Tick(new CHRISInputFrame { Now = Time.realtimeSinceStartup, Index = Time.frameCount, Revocation = revocation });
            if (!s_Input.Active)
            {
                Log($"input lease {s_Input.LeaseId} {s_Input.State.ToString().ToLowerInvariant()}: {s_Input.Reason}");
                OnInputEnded();
            }
            return true;
        }

        // A recenter since the grant wins over everything else; then the generic takeovers.
        internal static string InputRevocation(long leaseEpoch, long roomEpoch, Func<string> takeover) =>
            leaseEpoch != roomEpoch ? CHRISInputExecutor.Recenter : InputReason(takeover());

        // The shared input-v0.2 reasons: Stop, Focus lost, or "Takeover: <source>".
        internal static string InputReason(string revocation)
        {
            if (revocation == null) return null;
            if (revocation == "Stopped locally") return CHRISInputExecutor.Stop;
            if (revocation == "Focus lost") return CHRISInputExecutor.FocusLost;
            return CHRISInputExecutor.TakeoverPrefix + char.ToLowerInvariant(revocation[0]) + revocation.Substring(1);
        }

        static string Revocation(bool focused, bool busy, ICHRISInputState input)
        {
            if (input.StopPressedThisFrame) return "Stopped locally";
            if (!focused) return "Focus lost";
            if (busy) return "Stroke, grab or widget interaction started";
            if (!ControllersPresent || !ReferenceEquals(InputManager.Brush, s_BrushController) ||
                InputManager.m_Instance.WandOnRight != s_WandOnRight) return "Handedness changed";
            if (PhysicalHeld()) return "Physical input on the leased hand";
            if (MouseHeld()) return "Mouse input during the lease";
            if (!ReferenceEquals(CHRISInputMappingHost.Remap.Active, s_Mapping)) return "Mapping changed";
            if (CHRISInputMappingHost.Remap.DrawHeld) return "Mapped input on the leased hand";
            if (CHRISBimanualHost.InUIMode || CHRISBimanualHost.RecoveryUI) return "Manual takeover";
            var bimanual = CHRISBimanualHost.Input;
            if (bimanual.Active)
            {
                string takeover = MappedTakeover(s_Mode, s_HandBackPending, s_RecenterPending,
                    bimanual.Mode, bimanual.HandBackPending, bimanual.RecenterPending);
                if (takeover != null) return takeover;
                if (!bimanual.Brush.IsNeutral || bimanual.Brush.Position != s_MappedBrushPosition ||
                    bimanual.Brush.Rotation != s_MappedBrushRotation) return "Mapped input on the leased hand";
            }
            return null;
        }
    }
}
