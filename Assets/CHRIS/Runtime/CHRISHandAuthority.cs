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
    public enum CHRISHandOwner { Physical, Mapped, Procedure }

    // What must hold before a procedure may take the Brush hand. Plain data so the refusal
    // order is testable without Open Brush running.
    internal struct CHRISAcquireFacts
    {
        public bool Focused, Busy, LeaseActive, KeyboardUI, MappingChangePending, LegacyBrushPose,
            PhysicalHeld, MappedHeld, MouseHeld, ControllersReady;
    }

    // The single answer to "who owns each hand" for the native input path. Physical input,
    // the keyboard/mouse mapping and one bounded procedure lease never own the same channel.
    // CHRISBimanualHost applies the answer; the procedure only ever takes the logical Brush hand
    // (pose and trigger) and the Wand stays with its current owner.
    public static class CHRISHandAuthority
    {
        const int MaxLedger = 64;
        static CHRISProcedureExecutor s_Lease;
        static readonly Dictionary<string, CHRISProcedureExecutor> s_Ledger = new Dictionary<string, CHRISProcedureExecutor>();
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

        public static bool LeaseActive => s_Lease?.Active == true;
        // The procedure keeps presenting its released hand for one frame after the lease ends,
        // so Open Brush sees the release edge before input returns to the previous owner.
        public static bool OwnsBrush => LeaseActive || Time.frameCount <= s_GraceUntilFrame;
        public static CHRISVirtualHand BrushHand => OwnsBrush ? s_Lease?.Hand : null;

        public static CHRISHandOwner Owner(bool brush, bool mappedOwnsPose) =>
            brush && OwnsBrush ? CHRISHandOwner.Procedure :
            mappedOwnsPose ? CHRISHandOwner.Mapped : CHRISHandOwner.Physical;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        internal static void ResetForPlay()
        {
            s_Lease = null;
            s_Ledger.Clear();
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
        internal static JObject Acquire(string taskId, bool busy, out string refusal)
        {
            var bimanual = CHRISBimanualHost.Input;
            refusal = AcquireRefusal(new CHRISAcquireFacts
            {
                ControllersReady = ControllersPresent &&
                    InputManager.Brush != null && InputManager.Wand != null && ViewpointScript.Head != null,
                Focused = CHRISInputMappingHost.InputFocusedNow,
                LeaseActive = OwnsBrush,
                Busy = busy,
                KeyboardUI = CHRISBimanualHost.InUIMode || CHRISBimanualHost.RecoveryUI,
                MappingChangePending = CHRISInputMappingHost.MappingChangePending,
                LegacyBrushPose = CHRISInputMappingHost.BrushPoseOwned,
                PhysicalHeld = PhysicalHeld(),
                MappedHeld = CHRISInputMappingHost.Remap.DrawHeld || (CHRISBimanualHost.MappingActive && !bimanual.Brush.IsNeutral),
                MouseHeld = MouseHeld(),
            });
            if (refusal != null) Log($"acquire refused for task {taskId}: {refusal}");
            if (refusal != null) return null;

            var brush = InputManager.Brush;
            Vector3 position;
            Quaternion rotation;
            if (MappedOwnsBrush) { position = bimanual.Brush.Position; rotation = bimanual.Brush.Rotation; }
            else if (PhysicalTracked()) { position = brush.Transform.position; rotation = brush.Transform.rotation; }
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
            var lease = Begin(taskId, ReturnMode(), position, rotation);
            s_BrushController = brush;
            s_WandOnRight = InputManager.m_Instance.WandOnRight;
            s_Mapping = CHRISInputMappingHost.Remap.Active;
            s_Mode = bimanual.Mode;
            s_HandBackPending = bimanual.HandBackPending;
            s_RecenterPending = bimanual.RecenterPending;
            s_MappedBrushPosition = bimanual.Brush.Position;
            s_MappedBrushRotation = bimanual.Brush.Rotation;
            var xr = brush as UnityXRControllerInfo;
            Log($"lease {lease.LeaseId} acquired for task {taskId}; return {lease.ReturnMode["source"]}");
            return Grant(lease, xr != null ? xr.PhysicalRightHand : !s_WandOnRight);
        }

        // Starts the lease record itself; the gateway test host calls this without controllers.
        internal static CHRISProcedureExecutor Begin(string taskId, JObject returnMode, Vector3 position, Quaternion rotation)
        {
            if (s_Ledger.Count >= MaxLedger) s_Ledger.Clear();
            s_Lease = new CHRISProcedureExecutor(Guid.NewGuid().ToString("N"), taskId, returnMode,
                position, rotation, Time.realtimeSinceStartup);
            s_Ledger[s_Lease.LeaseId] = s_Lease;
            s_Lease.Log = Log;
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

        internal static string ActiveTaskId => LeaseActive ? s_Lease.TaskId : null;
        internal static bool LastReturnPhysical => s_Lease != null && (string)s_Lease.ReturnMode["source"] == "physical";

        // The logical Brush hand has no trigger or grip held by any source.
        public static bool BrushButtonsNeutral => !PhysicalHeld() && !CHRISInputMappingHost.Remap.DrawHeld &&
            (BrushHand?.IsNeutral ?? true) && (!CHRISBimanualHost.MappingActive || CHRISBimanualHost.Input.Brush.IsNeutral);

        internal static CHRISProcedureExecutor Find(string leaseId) =>
            leaseId != null && s_Ledger.TryGetValue(leaseId, out var lease) ? lease : null;

        public static void Revoke(string reason)
        {
            if (!LeaseActive) return;
            s_Lease.Revoke(reason);
            Log($"lease {s_Lease.LeaseId} revoked: {reason}");
            OnEnded();
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
            s_GraceUntilFrame = Time.frameCount + 1;
            s_Ended = true;
            // Returning hands need a fresh press before physical input counts again.
            BrushXR?.RequirePhysicalRelease();
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
        internal static bool Tick(bool focused, bool busy, ICHRISInputState input)
        {
            if (!LeaseActive) return OwnsBrush;
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
