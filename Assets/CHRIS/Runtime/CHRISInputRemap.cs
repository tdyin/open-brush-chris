// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.Collections.Generic;
using System.Linq;
using System.IO;
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
        // One per press of the mapped undo input; holding does not repeat.
        public bool UndoPressed { get; private set; }
        // Whole wheel notches to apply this frame; fractions carry to the next frame.
        public int SizeNotches { get; private set; }
        // Held move_view keys as a direction (+x right, +y forward), length 0 or 1.
        public Vector2 ViewKeys { get; private set; }
        // Mapped mouse movement for move_view this frame, in pixels.
        public Vector2 ViewMouse { get; private set; }
        public bool MouseDeltaMapped => Mapped(CHRISMappedAction.MoveView, CHRISMappingSource.MouseDelta) ||
            Mapped(CHRISMappedAction.MoveBrush, CHRISMappingSource.MouseDelta);

        public void Offer(CHRISInputMapping mapping) => m_Pending = mapping;

        public void Stop()
        {
            m_Active = null;
            m_Pending = null;
            m_Latched.Clear();
            SetDraw(false);
            ClearMotion();
            m_UndoHeld = false;
        }

        public void Tick(ICHRISInputState input, bool focused, bool strokeInProgress)
        {
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

            if (m_Pending != null && !strokeInProgress && !AnyHeld(input, m_Pending) && !AnyHeld(input, m_Active))
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
                {
                    var keys = new Vector2(
                        (Held(input, "key." + view.Right) ? 1 : 0) - (Held(input, "key." + view.Left) ? 1 : 0),
                        (Held(input, "key." + view.Up) ? 1 : 0) - (Held(input, "key." + view.Down) ? 1 : 0));
                    ViewKeys = keys == Vector2.zero ? keys : keys.normalized;
                }
            }
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
            if (!m_Keys.TryGetValue(input, out var key) || (key != null && key.device != keyboard))
            {
                key = keyboard.TryGetChildControl<ButtonControl>(input.Substring(4));
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

        public static CHRISInputRemap Remap { get; private set; } = new CHRISInputRemap();
        public static CHRISViewHeading Heading { get; private set; } = new CHRISViewHeading();
        public static bool MouseDeltaMapped => Remap.MouseDeltaMapped;
        static CHRISInputMappingStore s_Store = new CHRISInputMappingStore();
        static bool s_Started, s_ShortcutsOwned, s_ShortcutsBefore;
        static int s_Frame = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetForPlay()
        {
            Remap = new CHRISInputRemap();
            Heading = new CHRISViewHeading();
            s_Store = new CHRISInputMappingStore();
            s_Started = s_ShortcutsOwned = s_ShortcutsBefore = false;
            s_Frame = -1;
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
            bool wasActive = Remap.Active != null;
            Remap.Tick(CHRISDeviceInput.Instance, Application.isFocused, stroke);
            if (!wasActive && Remap.Active != null) Debug.Log("CHRIS mapping active: Open Brush keyboard shortcuts off, Escape stops");
            if (wasActive && Remap.Active == null) Debug.Log("CHRIS mapping stopped: shortcuts restored; reload to use it again");
            SyncShortcuts();
            Apply();
        }

        // Carries out this frame's undo, brush size and view movement through native operations.
        static void Apply()
        {
            var sketch = SketchControlsScript.m_Instance;
            if (Remap.UndoPressed && sketch != null && sketch.CanUndo())
                sketch.IssueGlobalCommand(SketchControlsScript.GlobalCommands.Undo);

            var pointers = PointerManager.m_Instance;
            if (Remap.SizeNotches != 0 && pointers != null)
            {
                pointers.AdjustAllPointersBrushSize01(BrushSizeStep01 * Remap.SizeNotches);
                pointers.MarkAllBrushSizeUsed();
                App.Switchboard.TriggerBrushSizeChanged();
            }

            var head = ViewpointScript.Head;
            if (head != null) Heading.Update(head.forward);
            Vector2 local = Remap.ViewKeys * (ViewKeySpeed * Time.deltaTime) + Remap.ViewMouse * ViewMouseScale;
            if (local != Vector2.zero && App.Scene != null)
                ApiMethods.MoveUserBy(Heading.ToRoom(local));
        }

        [ApiEndpoint("chris.mapping.reload",
            "Reloads <persistentDataPath>/CHRIS/active-mapping.json; it takes effect when no mapped input is held and no stroke is in progress")]
        public static void ReloadMapping() => Reload();

        static void Reload()
        {
            string path = Path.GetFullPath(CHRISInputMappingStore.PathUnder(Application.persistentDataPath));
            bool loaded = s_Store.Reload(path);
            if (loaded) Remap.Offer(s_Store.Current);
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
            bool active = Remap.Active != null;
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
    }
}
