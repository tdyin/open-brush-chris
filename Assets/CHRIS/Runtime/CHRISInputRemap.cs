// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.Collections.Generic;
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
    }

    // Applies an active v0.1.1 mapping once per frame. Plain class so editor checks can drive it.
    // Rules (C:\Dev\chris agent/README.md "v0.1.1 mapping execution path (September 24)"):
    // - a new mapping takes effect only when neutral: none of its or the current mapping's inputs
    //   held and no stroke in progress;
    // - Stop (Escape) releases everything and deactivates the mapping;
    // - focus loss releases everything; after it, a held input does nothing until pressed again.
    public sealed class CHRISInputRemap
    {
        CHRISInputMapping m_Active, m_Pending;
        readonly HashSet<string> m_Latched = new HashSet<string>();
        bool m_Unfocused;

        public CHRISInputMapping Active => m_Active;
        public bool HasPending => m_Pending != null;
        public bool DrawHeld { get; private set; }
        public bool DrawDown { get; private set; }
        public bool DrawUp { get; private set; }

        public void Offer(CHRISInputMapping mapping) => m_Pending = mapping;

        public void Stop()
        {
            m_Active = null;
            m_Pending = null;
            m_Latched.Clear();
            SetDraw(false);
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
            }

            var draw = m_Active?.Find(CHRISMappedAction.Draw);
            bool held = false;
            if (draw != null)
                foreach (var name in CHRISInputMapping.Inputs(draw))
                    held |= input.IsPressed(name) && !m_Latched.Contains(name);
            SetDraw(held);
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
        public static CHRISInputRemap Remap { get; private set; } = new CHRISInputRemap();
        static CHRISInputMappingStore s_Store = new CHRISInputMappingStore();
        static bool s_Started, s_ShortcutsOwned, s_ShortcutsBefore;
        static int s_Frame = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetForPlay()
        {
            Remap = new CHRISInputRemap();
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
