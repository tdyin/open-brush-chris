// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using UnityEngine;
using UnityEngine.SpatialTracking;

namespace TiltBrush
{
    // Tracks an explicit F1 UI request separately from a saved mapping's default UI mode.
    // Granting waits for physical interaction to finish unless CHRIS already owns the pose.
    internal sealed class CHRISKeyboardUIOwnership
    {
        public bool Requested { get; private set; }
        public bool Granted { get; private set; }
        public void Request() => Requested = true;
        public void Release() => Requested = Granted = false;

        public void UpdateActive(bool focused, bool modeUI, bool handBackPending, bool busy, bool ownsPose)
        {
            if (!focused || !modeUI || handBackPending) { Release(); return; }
            if (Requested && (!busy || ownsPose)) Granted = true;
        }

        public void UpdateRecovery(bool focused, bool busy, bool ownsPose)
        {
            if (!focused) { Release(); return; }
            if (Requested && !busy && !ownsPose) Granted = true;
        }
    }

    // Bridges the testable two-hand state machine to Open Brush's existing controller objects.
    // The physical pose drivers are restored only after virtual buttons have been released.
    public static class CHRISBimanualHost
    {
        public const float VirtualUIPanelReach = 20f; // two metres from the head, in Open Brush units
        static CHRISBimanualInput s_Input = new CHRISBimanualInput();
        static TrackedPoseDriver s_BrushDriver, s_WandDriver;
        static bool s_BrushDriverWasEnabled, s_WandDriverWasEnabled;
        static VrSdk s_HookedSdk;
        static bool s_PhysicalReleaseGated;
        static Vector2 s_UIPointerOffset;
        static CHRISControlMode s_PreviousMode = CHRISControlMode.UI;
        static bool s_Focused = true;
        static CHRISKeyboardUIOwnership s_KeyboardUI = new CHRISKeyboardUIOwnership();

        public static CHRISBimanualInput Input => s_Input;
        public static bool MappingActive => s_Input.Active;
        internal static bool WantsKeyboardUI(bool active, CHRISControlMode mode, bool requested,
            bool handBackPending) => active && mode == CHRISControlMode.UI && requested && !handBackPending;
        internal static bool UsesVirtualHands(bool keyboardUI, bool ownsPose) => keyboardUI || ownsPose;
        public static bool InUIMode => WantsKeyboardUI(s_Input.Active, s_Input.Mode,
            s_KeyboardUI.Granted, s_Input.HandBackPending);
        public static bool OwnsBrushPose => s_BrushDriver != null;
        public static bool OwnsWandPose => s_WandDriver != null;
        public static bool OwnsDesktopControl => UsesVirtualHands(InUIMode, OwnsBrushPose || OwnsWandPose);
        public static bool OwnsMappedPose => s_Input.Active && s_Input.Mode != CHRISControlMode.UI &&
            !s_Input.HandBackPending && (OwnsBrushPose || OwnsWandPose);
        public static bool Focused => s_Focused;
        public static bool RecoveryUI
        {
            get { return !s_Input.Active && s_KeyboardUI.Granted; }
        }

        public static void ResetUIPointer() => s_UIPointerOffset = Vector2.zero;
        internal static void RequestKeyboardUI() => s_KeyboardUI.Request();
        internal static void ReleaseKeyboardUI() => s_KeyboardUI.Release();

        internal static float PanelRayReach(float physicalReach, bool virtualUIPointer) =>
            virtualUIPointer ? Mathf.Max(physicalReach, VirtualUIPanelReach) : physicalReach;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetForPlay()
        {
            RestoreDriverOwnership();
            s_Input = new CHRISBimanualInput();
            s_BrushDriverWasEnabled = s_WandDriverWasEnabled = false;
            s_HookedSdk = null;
            s_PhysicalReleaseGated = false;
            s_UIPointerOffset = Vector2.zero;
            s_PreviousMode = CHRISControlMode.UI;
            s_Focused = true;
            s_KeyboardUI = new CHRISKeyboardUIOwnership();
        }

        // Each hand answers from its own owner: a procedure lease (Brush only), the mapping
        // while it holds that hand's pose or the keyboard UI pointer, otherwise physical input.
        public static CHRISVirtualHand HandFor(ControllerInfo controller)
        {
            if (InputManager.m_Instance == null || InputManager.Controllers == null) return null;
            if (ReferenceEquals(controller, InputManager.Brush))
                return CHRISHandAuthority.BrushHand ?? (InUIMode || s_BrushDriver != null ? s_Input.Brush : null);
            if (ReferenceEquals(controller, InputManager.Wand))
                return InUIMode || s_WandDriver != null ? s_Input.Wand : null;
            return null;
        }

        public static bool TryGetUIPointerRay(out Ray ray)
        {
            ray = default;
            if ((!InUIMode && !RecoveryUI) || !s_Focused || ViewpointScript.Head == null) return false;
            var head = ViewpointScript.Head;
            Vector3 center = head.forward;
            Vector3 right = Vector3.Cross(Vector3.up, center).normalized;
            Vector3 up = Vector3.Cross(center, right).normalized;
            ray = new Ray(head.position, (center + right * s_UIPointerOffset.x + up * s_UIPointerOffset.y).normalized);
            return true;
        }

        internal static void Tick(CHRISInputMapping mapping, ICHRISInputState deviceInput,
            bool focused, bool strokeInProgress)
        {
            s_Focused = focused;
            bool active = mapping != null && mapping.IsBimanual;
            var head = active ? ViewpointScript.Head : null;
            var controls = SketchControlsScript.m_Instance;
            bool busy = strokeInProgress || (controls != null &&
                (controls.IsUserGrabbingWorld() || controls.IsUserInteractingWithAnyWidget()));
            bool procedure = CHRISHandAuthority.Tick(focused, busy, deviceInput);
            // Keys held through a procedure hand-back need a fresh press under the mapping.
            if (CHRISHandAuthority.TakeEnded() && s_Input.Active) s_Input.SuspendForEditor(deviceInput);
            if (procedure)
            {
                var owner = InputManager.m_Instance;
                if (active && head != null)
                    s_Input.Step(mapping, deviceInput, focused, busy, head.position, head.forward,
                        owner != null && !owner.WandOnRight, Time.deltaTime);
                else if (s_Input.Active) { s_Input.Stop(); s_KeyboardUI.Release(); }
                TakeProcedurePose();
                return;
            }
            if (!active || head == null)
            {
                bool wasActive = s_Input.Active;
                if (wasActive || s_BrushDriver != null || s_WandDriver != null)
                    GatePhysicalRelease();
                s_Input.Stop();
                ReleaseWhenSafe(busy);
                if (wasActive) s_KeyboardUI.Release();
                s_KeyboardUI.UpdateRecovery(focused, busy,
                    s_BrushDriver != null || s_WandDriver != null);
                if (focused && RecoveryUI) UpdateUIPointer(deviceInput);
                return;
            }

            var manager = InputManager.m_Instance;
            s_Input.Step(mapping, deviceInput, focused, busy, head.position, head.forward,
                manager != null && !manager.WandOnRight, Time.deltaTime);
            s_KeyboardUI.UpdateActive(focused, s_Input.Mode == CHRISControlMode.UI,
                s_Input.HandBackPending, busy, s_BrushDriver != null || s_WandDriver != null);
            if (s_Input.Mode != s_PreviousMode)
            {
                s_UIPointerOffset = Vector2.zero;
                s_PreviousMode = s_Input.Mode;
            }
            if (focused && InUIMode) UpdateUIPointer(deviceInput);

            if (!focused || s_Input.HandBackPending)
            {
                GatePhysicalRelease();
                ReleaseWhenSafe(busy);
                return;
            }
            // After hand-back, the physical hand may still be finishing a stroke or grab.
            // Keep virtual controls released and latch held keys until it becomes neutral.
            if (busy && s_BrushDriver == null && s_WandDriver == null)
            {
                s_Input.SuspendForEditor(deviceInput);
                return;
            }
            s_PhysicalReleaseGated = false;

            // UI mode keeps a previously owned pose fixed. First activation stays physical until
            // the user explicitly chooses a pose mode, so startup menus remain reachable.
            if (s_Input.Mode == CHRISControlMode.UI && s_BrushDriver == null && s_WandDriver == null)
                return;
            if (manager == null || InputManager.Brush == null || InputManager.Wand == null) return;
            HookPoseWrites();
            var brushDriver = InputManager.Brush.Behavior.GetComponent<TrackedPoseDriver>();
            var wandDriver = InputManager.Wand.Behavior.GetComponent<TrackedPoseDriver>();
            ReconcileDriverOwnership(brushDriver, wandDriver);
            WritePoses();
        }

        // The procedure owns the Brush pose; the Wand keeps whichever owner it already had.
        static void TakeProcedurePose()
        {
            if (InputManager.m_Instance == null || InputManager.Brush == null || InputManager.Wand == null) return;
            HookPoseWrites();
            var brushDriver = InputManager.Brush.Behavior.GetComponent<TrackedPoseDriver>();
            var wandDriver = s_WandDriver != null ? InputManager.Wand.Behavior.GetComponent<TrackedPoseDriver>() : null;
            ReconcileDriverOwnership(brushDriver, wandDriver);
            // Let the hand-back gate physical release once.
            s_PhysicalReleaseGated = false;
            WritePoses();
        }

        internal static void ReconcileDriverOwnership(TrackedPoseDriver brushDriver, TrackedPoseDriver wandDriver)
        {
            if (brushDriver != null && ReferenceEquals(brushDriver, wandDriver))
            { RestoreDriverOwnership(); return; }
            // A handedness swap exchanges both roles. Restore both original drivers before
            // sampling either one's enabled state for its new role.
            if (!ReferenceEquals(brushDriver, s_BrushDriver) || !ReferenceEquals(wandDriver, s_WandDriver))
                RestoreDriverOwnership();
            TakeOwnership(brushDriver, ref s_BrushDriver, ref s_BrushDriverWasEnabled);
            TakeOwnership(wandDriver, ref s_WandDriver, ref s_WandDriverWasEnabled);
        }

        static void HookPoseWrites()
        {
            var sdk = App.VrSdk;
            if (sdk == null || ReferenceEquals(sdk, s_HookedSdk)) return;
            sdk.OnNewControllerPosesApplied += WritePoses;
            s_HookedSdk = sdk;
        }

        static void UpdateUIPointer(ICHRISInputState input)
        {
            Vector2 keys = new Vector2(
                (input.IsPressed("key.rightArrow") ? 1 : 0) - (input.IsPressed("key.leftArrow") ? 1 : 0),
                (input.IsPressed("key.upArrow") ? 1 : 0) - (input.IsPressed("key.downArrow") ? 1 : 0));
            s_UIPointerOffset += input.MouseDelta * 0.002f + keys * (0.5f * Time.deltaTime);
            s_UIPointerOffset.x = Mathf.Clamp(s_UIPointerOffset.x, -0.45f, 0.45f);
            s_UIPointerOffset.y = Mathf.Clamp(s_UIPointerOffset.y, -0.45f, 0.45f);
        }

        static void TakeOwnership(TrackedPoseDriver driver, ref TrackedPoseDriver current, ref bool wasEnabled)
        {
            if (ReferenceEquals(driver, current)) return;
            Restore(ref current, ref wasEnabled);
            if (driver == null) return;
            current = driver;
            wasEnabled = driver.enabled;
            driver.enabled = false;
        }

        static void ReleaseWhenSafe(bool busy)
        {
            if (busy) return;
            RestoreDriverOwnership();
        }

        internal static void RestoreDriverOwnership()
        {
            Restore(ref s_BrushDriver, ref s_BrushDriverWasEnabled);
            Restore(ref s_WandDriver, ref s_WandDriverWasEnabled);
        }

        static void GatePhysicalRelease()
        {
            if (s_PhysicalReleaseGated || InputManager.Controllers == null ||
                InputManager.Brush == null || InputManager.Wand == null) return;
            s_PhysicalReleaseGated = true;
            (InputManager.Brush as UnityXRControllerInfo)?.RequirePhysicalRelease();
            (InputManager.Wand as UnityXRControllerInfo)?.RequirePhysicalRelease();
        }

        static void Restore(ref TrackedPoseDriver driver, ref bool wasEnabled)
        {
            if (driver != null) driver.enabled = wasEnabled;
            driver = null;
            wasEnabled = false;
        }

        static void WritePoses()
        {
            if (s_BrushDriver != null) WritePose(InputManager.Brush, CHRISHandAuthority.BrushHand ?? s_Input.Brush);
            if (s_WandDriver != null) WritePose(InputManager.Wand, s_Input.Wand);
        }

        static void WritePose(ControllerInfo controller, CHRISVirtualHand hand)
        {
            if (controller == null) return;
            var transform = controller.Behavior.transform;
            transform.SetPositionAndRotation(hand.Position, hand.Rotation);
        }
    }
}
