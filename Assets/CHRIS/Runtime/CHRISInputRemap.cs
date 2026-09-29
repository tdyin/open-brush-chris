// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace TiltBrush
{
    // Inputs are named as the validator names them: "key.<control>", "mouse.left|right|middle".
    public interface ICHRISInputState
    {
        bool IsPressed(string input);
        bool StopPressedThisFrame { get; }
        // Vertical wheel this frame, in notches (+ is away from the user). May be fractional.
        float WheelNotches { get; }
        // Mouse movement this frame in pixels (+x right, +y forward/away from the user).
        Vector2 MouseDelta { get; }
    }

    // Applies an active v0.1.1 mapping once per frame. Plain class so editor checks can drive it.
    // Rules (C:\Dev\chris agent/README.md "v0.1.1 mapping execution path (September 24)"):
    // - a new mapping takes effect only when neutral: none of its or the current mapping's inputs
    //   held and no stroke in progress;
    // - Stop (Escape) releases everything and deactivates the mapping;
    // - focus loss releases everything; after it, a held input does nothing until pressed again;
    // - wheel notches, undo presses and view movement while unfocused are discarded; brush_size and
    //   move_view are ignored during a stroke (undo is gated by native CanUndo in the host).
    public sealed class CHRISInputRemap
    {
        CHRISInputMapping m_Active, m_Pending;
        readonly HashSet<string> m_Latched = new HashSet<string>();
        bool m_Unfocused, m_UndoHeld;
        float m_Wheel;

        public CHRISInputMapping Active => m_Active;
        public bool HasPending => m_Pending != null;
        public bool DrawHeld { get; private set; }
        public bool DrawDown { get; private set; }
        public bool DrawUp { get; private set; }
        public bool StoppedThisFrame { get; private set; }
        // One per press of the mapped undo input; holding does not repeat.
        public bool UndoPressed { get; private set; }
        // Whole wheel notches to apply this frame; fractions carry to the next frame.
        public int SizeNotches { get; private set; }
        // Held move_view keys as a direction (+x right, +y forward), length 0 or 1.
        public Vector2 ViewKeys { get; private set; }
        // Mapped mouse movement for move_view this frame, in pixels.
        public Vector2 ViewMouse { get; private set; }
        // Held move_brush keys as a direction (+x right, +y up on the brush plane), length 0 or 1.
        // Unlike move_view this is not ignored during a stroke: moving the brush is how you draw.
        public Vector2 BrushKeys { get; private set; }
        // Mapped mouse movement for move_brush this frame, in pixels.
        public Vector2 BrushMouse { get; private set; }
        public bool MouseDeltaMapped => Mapped(CHRISMappedAction.MoveView, CHRISMappingSource.MouseDelta) ||
            Mapped(CHRISMappedAction.MoveBrush, CHRISMappingSource.MouseDelta);

        public void Offer(CHRISInputMapping mapping) => m_Pending = mapping;

        public bool IsNeutralFor(CHRISInputMapping mapping, ICHRISInputState input, bool focused, bool strokeInProgress) =>
            focused && !strokeInProgress && !AnyHeld(input, mapping) && !AnyHeld(input, m_Active);

        public void Stop()
        {
            m_Active = null;
            m_Pending = null;
            m_Latched.Clear();
            SetDraw(false);
            ClearMotion();
            m_UndoHeld = false;
        }

        // A pose-mode entry requires fresh convenience presses; wheel fractions cannot cross UI
        // or hand-back boundaries. This does not affect the v0.1.1 mapping path.
        public void SuppressBimanualConvenience(ICHRISInputState input)
        {
            LatchHeld(input, m_Active);
            ClearMotion();
            m_UndoHeld = false;
        }

        public void SuppressForEditor(ICHRISInputState input)
        {
            LatchHeld(input, m_Active);
            SetDraw(false);
            ClearMotion();
            m_UndoHeld = false;
        }

        public void Tick(ICHRISInputState input, bool focused, bool strokeInProgress)
        {
            StoppedThisFrame = input.StopPressedThisFrame;
            if (input.StopPressedThisFrame)
            {
                Stop();
                return;
            }
            if (!focused)
            {
                m_Unfocused = true;
                SetDraw(false);
                ClearMotion();
                return;
            }
            if (m_Unfocused)
            {
                // Devices may report keys held through the switch; ignore them until released.
                m_Unfocused = false;
                LatchHeld(input, m_Active);
            }
            m_Latched.RemoveWhere(name => !input.IsPressed(name));

            if (m_Pending != null && IsNeutralFor(m_Pending, input, focused, strokeInProgress))
            {
                m_Active = m_Pending;
                m_Pending = null;
                ClearMotion();
                m_UndoHeld = false;
            }

            var draw = m_Active?.Find(CHRISMappedAction.Draw);
            bool held = false;
            if (draw != null)
                foreach (var name in CHRISInputMapping.Inputs(draw))
                    held |= Held(input, name);
            SetDraw(held);

            var undo = m_Active?.Find(CHRISMappedAction.Undo);
            bool undoHeld = undo != null && Held(input, CHRISInputMapping.Inputs(undo).First());
            UndoPressed = undoHeld && !m_UndoHeld;
            m_UndoHeld = undoHeld;

            SizeNotches = 0;
            if (m_Active?.Find(CHRISMappedAction.BrushSize) == null || strokeInProgress)
                m_Wheel = 0;
            else
            {
                m_Wheel += input.WheelNotches;
                SizeNotches = (int)m_Wheel; // truncates toward zero; the remainder carries
                m_Wheel -= SizeNotches;
            }

            ViewKeys = Vector2.zero;
            ViewMouse = Vector2.zero;
            var view = m_Active?.Find(CHRISMappedAction.MoveView);
            if (view != null && !strokeInProgress)
            {
                if (view.Source == CHRISMappingSource.MouseDelta)
                    ViewMouse = input.MouseDelta;
                else
                    ViewKeys = KeyDirection(input, view);
            }

            BrushKeys = Vector2.zero;
            BrushMouse = Vector2.zero;
            var brush = m_Active?.Find(CHRISMappedAction.MoveBrush);
            if (brush != null)
            {
                if (brush.Source == CHRISMappingSource.MouseDelta)
                    BrushMouse = input.MouseDelta;
                else
                    BrushKeys = KeyDirection(input, brush);
            }
        }

        Vector2 KeyDirection(ICHRISInputState input, CHRISInputMappingEntry entry)
        {
            var keys = new Vector2(
                (Held(input, "key." + entry.Right) ? 1 : 0) - (Held(input, "key." + entry.Left) ? 1 : 0),
                (Held(input, "key." + entry.Up) ? 1 : 0) - (Held(input, "key." + entry.Down) ? 1 : 0));
            return keys == Vector2.zero ? keys : keys.normalized;
        }

        bool Held(ICHRISInputState input, string name) => input.IsPressed(name) && !m_Latched.Contains(name);

        bool Mapped(CHRISMappedAction action, CHRISMappingSource source) => m_Active?.Find(action)?.Source == source;

        void ClearMotion()
        {
            UndoPressed = false;
            SizeNotches = 0;
            m_Wheel = 0;
            ViewKeys = Vector2.zero;
            ViewMouse = Vector2.zero;
            BrushKeys = Vector2.zero;
            BrushMouse = Vector2.zero;
        }

        void SetDraw(bool held)
        {
            DrawDown = held && !DrawHeld;
            DrawUp = !held && DrawHeld;
            DrawHeld = held;
        }

        void LatchHeld(ICHRISInputState input, CHRISInputMapping mapping)
        {
            if (mapping == null) return;
            foreach (var entry in mapping.Mappings)
                foreach (var name in CHRISInputMapping.Inputs(entry))
                    if (input.IsPressed(name)) m_Latched.Add(name);
        }

        static bool AnyHeld(ICHRISInputState input, CHRISInputMapping mapping)
        {
            if (mapping == null) return false;
            foreach (var entry in mapping.Mappings)
                foreach (var name in CHRISInputMapping.Inputs(entry))
                    if (input.IsPressed(name)) return true;
            return false;
        }
    }

    // The head-facing plane move_brush draws on, fixed when the mapping activates. Positions are in
    // room (Unity world) units. The tip starts at the plane origin and is clamped to +-Extent.
    public sealed class CHRISBrushPlane
    {
        public Vector3 Origin { get; private set; }
        public Vector3 Right { get; private set; } = Vector3.right;
        public Vector3 Up { get; private set; } = Vector3.up;
        public Vector3 Normal { get; private set; } = Vector3.forward;
        public Vector2 Offset { get; private set; }
        public float Extent { get; private set; }

        // Normal = head forward (pitch kept, roll dropped). Looking almost straight up or down uses
        // the heading's yaw for the axes, as move_view does.
        public void Activate(Vector3 headPosition, Vector3 headForward, Vector3 fallbackHeading, float distance, float extent)
        {
            Normal = headForward.normalized;
            var flat = new Vector3(Normal.x, 0, Normal.z);
            var yaw = flat.magnitude >= CHRISViewHeading.MinProjection ? flat.normalized : fallbackHeading;
            Right = Vector3.Cross(Vector3.up, yaw).normalized;
            Up = Vector3.Cross(Normal, Right).normalized;
            Origin = headPosition + Normal * distance;
            Extent = extent;
            Offset = Vector2.zero;
        }

        public void Move(Vector2 delta) =>
            Offset = new Vector2(Mathf.Clamp(Offset.x + delta.x, -Extent, Extent), Mathf.Clamp(Offset.y + delta.y, -Extent, Extent));

        public Vector3 Tip => Origin + Right * Offset.x + Up * Offset.y;
        public Quaternion Rotation => Quaternion.LookRotation(Normal, Up);
    }

    // Horizontal heading for move_view: the head's forward projected onto the horizontal room plane,
    // re-read every frame. Looking almost straight up or down keeps the last valid heading.
    public sealed class CHRISViewHeading
    {
        public const float MinProjection = 0.2f;
        public Vector3 Forward { get; private set; } = Vector3.forward;
        public Vector3 Right => Vector3.Cross(Vector3.up, Forward);

        public void Update(Vector3 headForward)
        {
            var flat = new Vector3(headForward.x, 0, headForward.z);
            if (flat.magnitude >= MinProjection) Forward = flat.normalized;
        }

        // x = right, y = forward, in the same units as the result.
        public Vector3 ToRoom(Vector2 local) => Right * local.x + Forward * local.y;
    }

    // Brush trigger as physical OR mapped draw, with edges from that combined state. Sampled once per
    // frame whether or not a mapping is active, so a physical trigger already held when a mapping
    // activates is not reported as a new press. InputManager.Update reads the brush trigger every
    // frame (ControllerInfo.UpdateStateFlags), so no frame goes unsampled outside XR startup.
    public sealed class CHRISCombinedTrigger
    {
        int m_Frame = -1;
        public bool Held { get; private set; }
        public bool Previous { get; private set; }
        public bool Down => Held && !Previous;
        public bool Up => !Held && Previous;

        public void Sample(int frame, bool physical, bool mapped)
        {
            if (frame == m_Frame) return;
            m_Frame = frame;
            Previous = Held;
            Held = physical || mapped;
        }
    }

    // Stop and focus loss end the current stroke even if the physical trigger remains held.
    // A new physical press is accepted only after focus returns and the trigger is released.
    public sealed class CHRISPhysicalTriggerGate
    {
        public bool WaitingForRelease { get; private set; }

        public void RequireRelease() => WaitingForRelease = true;

        public bool Filter(bool physicalHeld, bool focused)
        {
            if (!focused)
            {
                RequireRelease();
                return false;
            }
            if (!WaitingForRelease) return physicalHeld;
            if (!physicalHeld) WaitingForRelease = false;
            return false;
        }
    }

    // Reads keyboard and mouse through the Input System. Escape is always Stop.
    sealed class CHRISDeviceInput : ICHRISInputState
    {
        public static readonly CHRISDeviceInput Instance = new CHRISDeviceInput();
        readonly Dictionary<string, ButtonControl> m_Keys = new Dictionary<string, ButtonControl>();

        public bool StopPressedThisFrame => Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
        // InputSystem.inputsettings m_ScrollDeltaBehavior 0 (uniform): one wheel notch reads as 1.
        public float WheelNotches => Mouse.current != null ? Mouse.current.scroll.y.ReadValue() : 0f;
        public Vector2 MouseDelta => Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;

        public bool IsPressed(string input)
        {
            if (input.StartsWith("mouse."))
            {
                var mouse = Mouse.current;
                if (mouse == null) return false;
                switch (input)
                {
                    case "mouse.left": return mouse.leftButton.isPressed;
                    case "mouse.right": return mouse.rightButton.isPressed;
                    case "mouse.middle": return mouse.middleButton.isPressed;
                    default: return false; // mouse.delta and mouse.wheel are not buttons
                }
            }
            var keyboard = Keyboard.current;
            if (keyboard == null || !input.StartsWith("key.")) return false;
            // Re-resolve if the keyboard was replaced (reconnect, or Play mode without a domain reload).
            if (!m_Keys.TryGetValue(input, out var key) || key == null || key.device != keyboard)
            {
                string name = input.Substring(4);
                // Input System's control names are "0".."9"; digit2Key is only a C# property name.
                key = keyboard.TryGetChildControl<ButtonControl>(name);
                m_Keys[input] = key;
            }
            return key != null && key.isPressed;
        }
    }

    // Owns the process-wide mapping: loads it at startup and on explicit reload, ticks it before
    // controller input is read, and turns Open Brush keyboard shortcuts off while it is active.
    // The project enters Play mode without a domain reload, so all state is reset on entry.
    public static class CHRISInputMappingHost
    {
        // Native constants (Open Brush units; 10 units per metre by App.METERS_TO_UNITS, not a
        // physical measurement). Agreed with C:\Dev\chris agent/README.md step 3 rules.
        public const float BrushSizeStep01 = 0.05f;
        public const float ViewKeySpeed = 15f;       // units per second
        public const float ViewMouseScale = 0.02f;   // units per pixel of mouse movement
        public const float BrushPlaneDistance = 5f;  // units in front of the head at activation
        public const float BrushPlaneExtent = 6f;    // units either side of the plane origin
        public const float BrushKeySpeed = 3f;       // units per second
        public const float BrushMouseScale = 0.01f;  // units per pixel of mouse movement

        public static CHRISInputRemap Remap { get; private set; } = new CHRISInputRemap();
        // Cheap (no status object): called per frame by controller infos.
        internal static bool MappingInUse => Remap.Active != null || Remap.HasPending || s_Authority.HasPending;
        public static CHRISViewHeading Heading { get; private set; } = new CHRISViewHeading();
        public static bool DesktopControlsOwned => CHRISBimanualHost.OwnsDesktopControl ||
            CHRISBimanualHost.RecoveryUI;
        internal static bool AllowsMappedConvenience(CHRISInputMapping mapping, bool ownsMappedPose) =>
            mapping?.SchemaVersion != CHRISInputMapping.BimanualVersion || ownsMappedPose;
        internal static bool MappingOwnsKeyboardShortcuts(CHRISInputMapping mapping, bool desktopOwned) =>
            mapping != null && (mapping.SchemaVersion != CHRISInputMapping.BimanualVersion || desktopOwned);
        internal static bool MappingUsesCombinedTrigger(CHRISInputMapping mapping) =>
            mapping?.SchemaVersion == CHRISInputMapping.Version;
        public static bool MouseDeltaMapped => Remap.MouseDeltaMapped || DesktopControlsOwned;
        static CHRISInputMappingStore s_Store = new CHRISInputMappingStore();
        static CHRISMappingAuthority s_Authority = NewAuthority();
        static CHRISPhysicalTriggerGate s_PhysicalTriggerGate = new CHRISPhysicalTriggerGate();
        static bool s_Started, s_ShortcutsOwned, s_ShortcutsBefore;
        static int s_Frame = -1;

        static CHRISMappingAuthority NewAuthority() =>
            new CHRISMappingAuthority(CHRISInputMappingStore.PathUnder(Application.persistentDataPath));

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetForPlay()
        {
            Remap = new CHRISInputRemap();
            Heading = new CHRISViewHeading();
            s_Store = new CHRISInputMappingStore();
            s_Authority = NewAuthority();
            s_SavedFileRevision = 0;
            s_PhysicalTriggerGate = new CHRISPhysicalTriggerGate();
            s_Started = s_ShortcutsOwned = s_ShortcutsBefore = false;
            s_Frame = -1;
            s_Plane = new CHRISBrushPlane();
            s_PlaneMapping = null;
            s_Driver = null;
            s_DriverWasEnabled = false;
            s_HookedSdk = null;
            s_Latency.Clear();
            s_LastInputTime = -1;
            s_LastSampledEvent = -1;
            s_PrevViewKeys = s_PrevBrushKeys = Vector2.zero;
        }

        // Load once at startup so the log shows the result before any controller is read.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Start()
        {
            if (s_Started) return;
            s_Started = true;
            Reload();
        }

        // Called by the brush controller's trigger reads before it answers; ControllerInfo.Update
        // makes one every frame, so Stop, focus and activation are processed each frame.
        internal static void Tick()
        {
            if (s_Frame == Time.frameCount) return;
            s_Frame = Time.frameCount;
            Start();
            var pointers = PointerManager.m_Instance;
            bool stroke = pointers != null && PointerManager.MainPointerIsPainting();
            var controls = SketchControlsScript.m_Instance;
            bool bimanualGrab = CHRISBimanualHost.MappingActive &&
                (CHRISBimanualHost.Input.Brush.GripHeld || CHRISBimanualHost.Input.Wand.GripHeld ||
                 (controls != null && (controls.IsUserGrabbingWorld() || controls.IsUserInteractingWithAnyWidget())));
            bool interactionBusy = stroke || bimanualGrab;
            bool activationBusy = interactionBusy || (CHRISBimanualHost.MappingActive &&
                (CHRISDeviceInput.Instance.MouseDelta != Vector2.zero || CHRISDeviceInput.Instance.WheelNotches != 0));
            bool wasActive = Remap.Active != null;
            bool hadMappedControl = wasActive || s_PhysicalTriggerGate.WaitingForRelease;
            bool focused = InputFocused(Application.isFocused, App.VrSdk == null || !App.VrSdk.IsAppFocusBlocked());
            s_Authority.TryActivate(CHRISDeviceInput.Instance, focused, activationBusy, Remap);
            Remap.Tick(CHRISDeviceInput.Instance, focused, interactionBusy);
            if (Remap.StoppedThisFrame)
            {
                s_Authority.Stop();
                CHRISBimanualHost.ReleaseKeyboardUI();
            }
            else s_Authority.CompleteActivation(Remap);
            if (hadMappedControl && (Remap.StoppedThisFrame || !focused)) s_PhysicalTriggerGate.RequireRelease();
            if (!wasActive && Remap.Active != null) Debug.Log("CHRIS mapping active: Open Brush keyboard shortcuts off, Escape stops");
            if (wasActive && Remap.Active == null) Debug.Log("CHRIS mapping stopped: shortcuts restored; reload to use it again");
            if (!Remap.StoppedThisFrame && focused && Keyboard.current != null &&
                Keyboard.current.f1Key.wasPressedThisFrame)
                CHRISBimanualHost.RequestKeyboardUI();
            if (focused && Keyboard.current != null && Keyboard.current.f6Key.wasPressedThisFrame)
                CHRISBimanualHost.ReleaseKeyboardUI();
            // Legacy move_brush relinquishes its driver before two-hand control can take it.
            UpdateBrushOwnership(ViewpointScript.Head);
            CHRISBimanualHost.Tick(Remap.Active, CHRISDeviceInput.Instance, focused, stroke);
            if (CHRISBimanualHost.RecoveryUI)
                Remap.SuppressForEditor(CHRISDeviceInput.Instance);
            else if (CHRISBimanualHost.MappingActive && (!CHRISBimanualHost.OwnsMappedPose ||
                CHRISBimanualHost.Input.NeedsConvenienceRelease))
                Remap.SuppressBimanualConvenience(CHRISDeviceInput.Instance);
            SyncShortcuts();
            Apply();
        }

        internal static bool InputFocused(bool desktopFocused, bool xrFocused) => desktopFocused && xrFocused;
        internal static bool InputFocusedNow => InputFocused(Application.isFocused,
            App.VrSdk == null || !App.VrSdk.IsAppFocusBlocked());
        internal static bool PhysicalTriggerWaitingForRelease => s_PhysicalTriggerGate.WaitingForRelease;
        internal static bool PhysicalTriggerControlled => ShouldGatePhysicalTrigger(
            MappingUsesCombinedTrigger(Remap.Active),
            Remap.DrawUp, s_PhysicalTriggerGate.WaitingForRelease);
        internal static bool ShouldGatePhysicalTrigger(bool mappingActive, bool mappedDrawUp, bool waitingForRelease) =>
            mappingActive || mappedDrawUp || waitingForRelease;
        internal static bool FilterPhysicalTrigger(bool held) => s_PhysicalTriggerGate.Filter(held,
            InputFocusedNow);
        internal static JObject MappingStatus()
        {
            Start();
            return s_Authority.Status();
        }

        // Headset UI only (not in the wire status): the saved file failed validation, so a reviewed
        // apply would replace it. SavedFileRevision changes on every reload so the popup can re-read
        // the saved file only when it may have changed.
        internal static bool SavedMappingInvalid => s_Authority.SavedFileInvalid;
        // Changes on every reload and on every successful write of an approved mapping.
        internal static int SavedFileRevision => s_SavedFileRevision + s_Authority.PersistCount;
        internal static long MappingRevision => s_Authority.Revision;
        static int s_SavedFileRevision;

        internal static JObject ActivateMapping(JObject request)
        {
            Start();
            return s_Authority.Apply(request);
        }

        internal static JObject DiscardPendingMapping()
        {
            Start();
            return s_Authority.DiscardPending();
        }

        internal static void StopMapping()
        {
            Start();
            Remap.Stop();
            s_Authority.Stop();
            CHRISBimanualHost.ReleaseKeyboardUI();
            s_PhysicalTriggerGate.RequireRelease();
            bool stroke = PointerManager.m_Instance != null && PointerManager.MainPointerIsPainting();
            CHRISBimanualHost.Tick(null, CHRISDeviceInput.Instance, true, stroke);
            SyncShortcuts();
        }

        // Carries out this frame's undo, brush size and view movement through native operations.
        static void Apply()
        {
            bool convenienceAllowed = AllowsMappedConvenience(Remap.Active, CHRISBimanualHost.OwnsMappedPose);
            if (Remap.DrawDown) MarkLatency("draw_activate_set", CHRISMappedAction.Draw);
            var sketch = SketchControlsScript.m_Instance;
            if (convenienceAllowed && Remap.UndoPressed && sketch != null && sketch.CanUndo())
            {
                sketch.IssueGlobalCommand(SketchControlsScript.GlobalCommands.Undo);
                MarkLatency("undo_issued", CHRISMappedAction.Undo);
            }

            var pointers = PointerManager.m_Instance;
            if (convenienceAllowed && Remap.SizeNotches != 0 && pointers != null)
            {
                pointers.AdjustAllPointersBrushSize01(BrushSizeStep01 * Remap.SizeNotches);
                pointers.MarkAllBrushSizeUsed();
                App.Switchboard.TriggerBrushSizeChanged();
                MarkLatency("brush_size_applied", CHRISMappedAction.BrushSize);
            }

            var head = ViewpointScript.Head;
            if (head != null) Heading.Update(head.forward);
            Vector2 local = Remap.ViewKeys * (ViewKeySpeed * Time.deltaTime) + Remap.ViewMouse * ViewMouseScale;
            if (convenienceAllowed && local != Vector2.zero && App.Scene != null)
            {
                ApiMethods.MoveUserBy(Heading.ToRoom(local));
                MarkMovementLatency("move_view_applied", CHRISMappedAction.MoveView, Remap.ViewKeys, ref s_PrevViewKeys);
            }
            else s_PrevViewKeys = Vector2.zero;

            if (s_Driver != null)
            {
                Vector2 move = Remap.BrushKeys * (BrushKeySpeed * Time.deltaTime) + Remap.BrushMouse * BrushMouseScale;
                if (move != Vector2.zero)
                {
                    s_Plane.Move(move);
                    MarkMovementLatency("move_brush_plane_updated", CHRISMappedAction.MoveBrush, Remap.BrushKeys, ref s_PrevBrushKeys);
                    // Sample only frames that bring a new input event; a held key's last event is its
                    // press, so later held frames would otherwise count the time since the press.
                    var device = Remap.BrushMouse != Vector2.zero ? (UnityEngine.InputSystem.InputDevice)Mouse.current : Keyboard.current;
                    if (device != null && device.lastUpdateTime != s_LastSampledEvent)
                    {
                        s_LastSampledEvent = device.lastUpdateTime;
                        s_LastInputTime = device.lastUpdateTime;
                    }
                }
                else s_PrevBrushKeys = Vector2.zero;
            }
            else s_PrevBrushKeys = Vector2.zero;
        }

        // Measurement only (CHRISPerfProbe, off by default). A device-update timing estimate: the mapped
        // device's lastUpdateTime (its most recent state event, not the specific control; on Windows
        // stamped when Unity processes Windows input) to this point in Apply. Endpoints, named in the labels:
        // draw = mapped Activate set (before the stroke is created), undo = undo command issued,
        // brush_size = size step applied, move_view = view moved, move_brush = plane tip updated
        // (before the pose is written in onBeforeRender). Nothing here runs while sampling is off.
        static Vector2 s_PrevViewKeys, s_PrevBrushKeys;

        static void MarkLatency(string action, CHRISMappedAction mapped)
        {
            if (!CHRISPerfProbe.LatencyActive) return;
            var entry = Remap.Active?.Find(mapped);
            if (entry == null) return;
            bool keyboard = entry.Source == CHRISMappingSource.Key || entry.Source == CHRISMappingSource.KeyVector2;
            UnityEngine.InputSystem.InputDevice device = keyboard ? (UnityEngine.InputSystem.InputDevice)Keyboard.current : Mouse.current;
            if (device != null) CHRISPerfProbe.MarkAction(action, device.lastUpdateTime);
        }

        // Keyboard movement is sampled at the start of a movement episode only: the frame the direction
        // goes from zero to non-zero. A further direction key pressed while another is still held is NOT
        // sampled, and unrelated keyboard events during a hold cannot add samples. Mouse movement samples
        // each frame with a new mouse event (the probe drops repeats of the same event time).
        static void MarkMovementLatency(string action, CHRISMappedAction mapped, Vector2 keys, ref Vector2 previous)
        {
            bool sample = ShouldSampleMovement(keys, previous);
            previous = keys;
            if (sample && CHRISPerfProbe.LatencyActive) MarkLatency(action, mapped);
        }

        // Keys: only the start of a movement episode (zero -> non-zero). Zero keys means mouse-driven
        // movement, which is sampled every frame (the probe ignores repeated event times).
        internal static bool ShouldSampleMovement(Vector2 keys, Vector2 previous) =>
            keys == Vector2.zero || previous == Vector2.zero;

        // ---- move_brush: the single brush pose writer ----
        // While the active mapping maps move_brush, the brush controller's TrackedPoseDriver is off
        // and WriteBrushPose (VrSdk.OnNewControllerPosesApplied) is the only thing posing the brush.
        static CHRISBrushPlane s_Plane = new CHRISBrushPlane();
        static CHRISInputMapping s_PlaneMapping;
        static UnityEngine.SpatialTracking.TrackedPoseDriver s_Driver;
        static bool s_DriverWasEnabled;
        static VrSdk s_HookedSdk;
        static double s_LastInputTime = -1, s_LastSampledEvent = -1;
        static readonly List<double> s_Latency = new List<double>();

        public static bool BrushPoseOwned => s_Driver != null;
        internal static CHRISBrushPlane BrushPlane => s_Plane;

        static void UpdateBrushOwnership(Transform head)
        {
            var sdk = App.VrSdk;
            if (sdk != null && !ReferenceEquals(sdk, s_HookedSdk))
            {
                sdk.OnNewControllerPosesApplied += WriteBrushPose;
                s_HookedSdk = sdk;
            }
            var mapping = Remap.Active;
            bool mapped = mapping?.Find(CHRISMappedAction.MoveBrush) != null && head != null &&
                InputManager.m_Instance != null && InputManager.Brush != null;
            if (!mapped)
            {
                ReleaseBrushAfterStroke();
                s_PlaneMapping = null;
                return;
            }
            if (!InputFocused(Application.isFocused, sdk == null || !sdk.IsAppFocusBlocked()))
            {
                // Focus loss hands the brush back to the physical controller; refocus retakes it at
                // the last tip because the plane (and its offset) is kept for the same mapping.
                ReleaseBrushAfterStroke();
                return;
            }
            var driver = InputManager.Brush.Behavior.GetComponent<UnityEngine.SpatialTracking.TrackedPoseDriver>();
            if (!ReferenceEquals(driver, s_Driver))
            {
                // First activation, or the brush hand swapped: hand the old controller back first.
                ReleaseBrush();
                if (driver == null) return;
                s_Driver = driver;
                s_DriverWasEnabled = driver.enabled;
                driver.enabled = false;
            }
            EnsureBrushPlane(mapping, head.position, head.forward);
        }

        internal static void EnsureBrushPlane(CHRISInputMapping mapping, Vector3 headPosition, Vector3 headForward)
        {
            bool movementChanged = !SameBrushMovement(mapping, s_PlaneMapping);
            s_PlaneMapping = mapping;
            if (movementChanged)
            {
                s_Plane.Activate(headPosition, headForward, Heading.Forward, BrushPlaneDistance, BrushPlaneExtent);
                Debug.Log($"CHRIS move_brush owns the brush pose: plane {BrushPlaneDistance} units ahead, +-{BrushPlaneExtent} units");
            }
        }

        internal static bool SameBrushMovement(CHRISInputMapping first, CHRISInputMapping second)
        {
            var a = first?.Find(CHRISMappedAction.MoveBrush);
            var b = second?.Find(CHRISMappedAction.MoveBrush);
            return a != null && b != null && a.Source == b.Source && a.Key == b.Key &&
                a.Button == b.Button && a.Up == b.Up && a.Down == b.Down &&
                a.Left == b.Left && a.Right == b.Right;
        }

        static void ReleaseBrushAfterStroke()
        {
            // Keep the tip at its last mapped position until the forced trigger-up ends painting.
            // Restoring controller tracking earlier can connect that tip to a held physical hand.
            if (PointerManager.m_Instance != null && PointerManager.MainPointerIsPainting()) return;
            ReleaseBrush();
        }

        static void ReleaseBrush()
        {
            if (s_Driver == null) return;
            s_Driver.enabled = s_DriverWasEnabled;
            s_Driver = null;
            LogLatency();
            Debug.Log("CHRIS move_brush released the brush pose: controller tracking restored");
        }

        static void WriteBrushPose()
        {
            if (s_Driver == null) return;
            var brush = InputManager.Brush;
            if (brush == null) return;
            var xf = brush.Behavior.transform;
            xf.rotation = s_Plane.Rotation;
            var attach = brush.Geometry != null ? brush.Geometry.PointerAttachPoint : null;
            Vector3 tipOffset = attach != null ? attach.position - xf.position : Vector3.zero;
            xf.position = s_Plane.Tip - tipOffset;
            if (s_LastInputTime >= 0)
            {
                s_Latency.Add((UnityEngine.InputSystem.LowLevel.InputState.currentTime - s_LastInputTime) * 1000.0);
                s_LastInputTime = -1;
                if (s_Latency.Count >= 600) LogLatency();
            }
        }

        // Input System event time of the last mapped brush input to the frame's pose write.
        static void LogLatency()
        {
            if (s_Latency.Count == 0) return;
            s_Latency.Sort();
            double Percentile(double p) => s_Latency[System.Math.Min(s_Latency.Count - 1, (int)(p * s_Latency.Count))];
            Debug.Log($"CHRIS move_brush input-to-pose latency: n={s_Latency.Count} median={Percentile(0.5):F1} ms " +
                $"p95={Percentile(0.95):F1} ms max={s_Latency[s_Latency.Count - 1]:F1} ms; frame {Time.smoothDeltaTime * 1000:F1} ms");
            s_Latency.Clear();
        }

        [ApiEndpoint("chris.mapping.reload",
            "Reloads <persistentDataPath>/CHRIS/active-mapping.json; it takes effect when no mapped input is held and no stroke is in progress")]
        public static void ReloadMapping() => Reload();

        static void Reload()
        {
            string path = Path.GetFullPath(CHRISInputMappingStore.PathUnder(Application.persistentDataPath));
            bool loaded = s_Store.Reload(path);
            if (loaded) s_Authority.OfferLoaded(s_Store.Current, s_Store.CurrentBytes);
            else if (s_Store.LastResult == "missing") s_Authority.NoteFileMissing();
            else if (s_Store.LastResult != "unreadable") s_Authority.NoteFileRejected();
            s_SavedFileRevision++;
            string kept = Remap.Active != null ? "the previous mapping stays active" : "no mapping is active";
            if (loaded)
                Debug.Log($"CHRIS mapping loaded from {path}: {s_Store.LastDetail}; it takes effect when no mapped input is held and no stroke is in progress");
            else if (s_Store.LastResult == "missing")
                Debug.Log($"CHRIS mapping: no file at {path}; {kept}");
            else
                Debug.LogWarning($"CHRIS mapping rejected ({s_Store.LastResult}: {s_Store.LastDetail}) at {path}; {kept}");
        }

        static void SyncShortcuts()
        {
            var input = InputManager.m_Instance;
            if (input == null) return;
            bool mappingOwnsShortcuts = MappingOwnsKeyboardShortcuts(Remap.Active,
                CHRISBimanualHost.OwnsDesktopControl);
            bool active = ShouldOwnKeyboardShortcuts(mappingOwnsShortcuts, CHRISBimanualHost.RecoveryUI,
                false);
            if (active && !s_ShortcutsOwned)
            {
                s_ShortcutsBefore = input.DisableKeyboardShortcuts;
                input.DisableKeyboardShortcuts = true;
                s_ShortcutsOwned = true;
            }
            else if (!active && s_ShortcutsOwned)
            {
                input.DisableKeyboardShortcuts = s_ShortcutsBefore;
                s_ShortcutsOwned = false;
            }
        }

        internal static bool ShouldOwnKeyboardShortcuts(bool mappingActive, bool recoveryUI, bool capturing) =>
            mappingActive || recoveryUI || capturing;
    }
}
