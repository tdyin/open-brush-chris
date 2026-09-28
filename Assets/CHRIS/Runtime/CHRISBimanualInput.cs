// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TiltBrush
{
    public enum CHRISControlMode { UI, Position, Rotation }

    // One immutable-for-the-frame logical hand snapshot. Open Brush asks for the same controls
    // through several APIs, so levels, edges and analog values must come from one sample.
    public sealed class CHRISVirtualHand
    {
        bool m_Trigger, m_Grip, m_Primary, m_Secondary, m_StickClick;
        bool m_OldTrigger, m_OldGrip, m_OldPrimary, m_OldSecondary, m_OldStickClick;
        bool m_OldAny;
        public Vector2 Stick { get; private set; }
        public Vector3 Position { get; internal set; }
        // This hand's own recenter position; its virtual position is clamped to a radius around it.
        public Vector3 Origin { get; internal set; }
        public Quaternion Rotation { get; internal set; } = Quaternion.identity;
        public bool GripHeld => m_Grip;
        public bool TriggerHeld => m_Trigger;
        public bool IsNeutral => !m_Trigger && !m_Grip && !m_Primary && !m_Secondary && !m_StickClick && Stick == Vector2.zero;

        internal void Sample(bool trigger, bool grip, bool primary, bool secondary, bool stickClick, Vector2 stick)
        {
            m_OldAny = !IsNeutral;
            m_OldTrigger = m_Trigger;
            m_OldGrip = m_Grip;
            m_OldPrimary = m_Primary;
            m_OldSecondary = m_Secondary;
            m_OldStickClick = m_StickClick;
            m_Trigger = trigger;
            m_Grip = grip;
            m_Primary = primary;
            m_Secondary = secondary;
            m_StickClick = stickClick;
            Stick = stick;
        }

        internal void Release() => Sample(false, false, false, false, false, Vector2.zero);

        bool Held(VrInput input)
        {
            switch (input)
            {
                case VrInput.Trigger: return m_Trigger;
                case VrInput.Grip: return m_Grip;
                case VrInput.Button01:
                case VrInput.Button04:
                case VrInput.Button06: return m_Primary;
                case VrInput.Button02:
                case VrInput.Button03:
                case VrInput.Button05: return m_Secondary;
                case VrInput.Directional:
                case VrInput.Thumbstick:
                case VrInput.Touchpad: return m_StickClick;
                case VrInput.Any: return !IsNeutral;
                default: return false;
            }
        }

        bool Old(VrInput input)
        {
            switch (input)
            {
                case VrInput.Trigger: return m_OldTrigger;
                case VrInput.Grip: return m_OldGrip;
                case VrInput.Button01:
                case VrInput.Button04:
                case VrInput.Button06: return m_OldPrimary;
                case VrInput.Button02:
                case VrInput.Button03:
                case VrInput.Button05: return m_OldSecondary;
                case VrInput.Directional:
                case VrInput.Thumbstick:
                case VrInput.Touchpad: return m_OldStickClick;
                case VrInput.Any: return m_OldAny;
                default: return false;
            }
        }

        public bool Get(VrInput input) => Held(input);
        public bool Down(VrInput input) => Held(input) && !Old(input);
        public bool Up(VrInput input) => !Held(input) && Old(input);
        public bool Touch(VrInput input) => input == VrInput.Thumbstick || input == VrInput.Directional ||
            input == VrInput.Touchpad ? Stick != Vector2.zero || m_StickClick : Held(input);
        public float TriggerValue => m_Trigger ? 1f : 0f;
        public float GripValue => m_Grip ? 1f : 0f;
    }

    // Pure state machine for v0.1.2 keyboard/mouse control. Pose ownership and OpenXR lifecycle
    // are handled by the host; no service or model participates in per-frame control.
    public sealed class CHRISBimanualInput
    {
        public const float MousePositionUnitsPerPixel = 0.01f;
        public const float KeyPositionUnitsPerSecond = 3f;
        public const float DepthUnitsPerSecond = 3f;
        public const float MouseDegreesPerPixel = 0.3f;
        public const float KeyDegreesPerSecond = 90f;
        public const float WheelDepthUnitsPerNotch = 0.3f;
        public const float WheelRollDegreesPerNotch = 9f;
        // Generous bound on each virtual hand's offset from its own recenter origin (native units;
        // 10 units per metre is a code constant, not a measurement). Hands start 5 units ahead.
        public const float MaxHandOffsetUnits = 20f;

        internal static Vector3 ClampToOrigin(Vector3 position, Vector3 origin, float radius) =>
            origin + Vector3.ClampMagnitude(position - origin, radius);

        readonly Dictionary<string, bool> m_Previous = new Dictionary<string, bool>();
        readonly HashSet<string> m_Latched = new HashSet<string>();
        CHRISInputMapping m_Mapping;
        bool m_WasUnfocused;
        bool m_BrushGrip, m_WandGrip;
        Vector3 m_Right = Vector3.right, m_Forward = Vector3.forward;
        public CHRISControlMode Mode { get; private set; } = CHRISControlMode.UI;
        public string Selected { get; private set; } = "brush";
        public bool RecenterPending { get; private set; }
        public bool HandBackPending { get; private set; }
        public bool NeedsConvenienceRelease { get; private set; }
        public CHRISVirtualHand Brush { get; } = new CHRISVirtualHand();
        public CHRISVirtualHand Wand { get; } = new CHRISVirtualHand();
        public bool Active => m_Mapping != null;

        public void Stop()
        {
            m_Mapping = null;
            m_WasUnfocused = false;
            m_Previous.Clear();
            m_Latched.Clear();
            m_BrushGrip = m_WandGrip = false;
            RecenterPending = HandBackPending = false;
            NeedsConvenienceRelease = false;
            Brush.Release();
            Wand.Release();
            Mode = CHRISControlMode.UI;
        }

        void ReleaseButtons()
        {
            m_BrushGrip = m_WandGrip = false;
            Brush.Release();
            Wand.Release();
        }

        static Vector3 FlatForward(Vector3 forward)
        {
            var flat = Vector3.ProjectOnPlane(forward, Vector3.up);
            return flat.sqrMagnitude < 0.01f ? Vector3.forward : flat.normalized;
        }

        void Recenter(Vector3 headPosition, Vector3 headForward, bool brushOnRight)
        {
            m_Forward = FlatForward(headForward);
            m_Right = Vector3.Cross(Vector3.up, m_Forward);
            float brushSide = brushOnRight ? 1f : -1f;
            Brush.Position = headPosition + m_Forward * 5f + m_Right * (brushSide * 2f) - Vector3.up;
            Wand.Position = headPosition + m_Forward * 5f - m_Right * (brushSide * 2f) - Vector3.up;
            Brush.Rotation = Wand.Rotation = Quaternion.LookRotation(m_Forward, Vector3.up);
            Brush.Origin = Brush.Position;
            Wand.Origin = Wand.Position;
            RecenterPending = false;
        }

        bool Held(ICHRISInputState input, string name) => input.IsPressed(name) && !m_Latched.Contains(name);
        bool Fresh(ICHRISInputState input, CHRISInputMappingEntry entry)
        {
            if (entry == null) return false;
            string name = CHRISInputMapping.Inputs(entry).FirstOrDefault();
            return name != null && Held(input, name) && (!m_Previous.TryGetValue(name, out bool was) || !was);
        }

        bool Button(ICHRISInputState input, CHRISMappedAction action, string target) =>
            m_Mapping?.Find(action, target) is CHRISInputMappingEntry entry && Held(input, CHRISInputMapping.Inputs(entry).First());

        Vector2 Axis(ICHRISInputState input, CHRISMappedAction action, string target)
        {
            var entry = m_Mapping?.Find(action, target);
            if (entry == null) return Vector2.zero;
            if (entry.Source == CHRISMappingSource.MouseDelta) return input.MouseDelta;
            var value = new Vector2(
                (Held(input, "key." + entry.Right) ? 1 : 0) - (Held(input, "key." + entry.Left) ? 1 : 0),
                (Held(input, "key." + entry.Up) ? 1 : 0) - (Held(input, "key." + entry.Down) ? 1 : 0));
            return value.sqrMagnitude > 1 ? value.normalized : value;
        }

        float Axis1(ICHRISInputState input, CHRISMappedAction action)
        {
            var entry = m_Mapping?.Find(action, "selected");
            if (entry == null) return 0;
            if (entry.Source == CHRISMappingSource.MouseWheel) return input.WheelNotches;
            return (Held(input, "key." + entry.Positive) ? 1 : 0) -
                (Held(input, "key." + entry.Negative) ? 1 : 0);
        }

        void Remember(ICHRISInputState input)
        {
            if (m_Mapping == null) return;
            foreach (var entry in m_Mapping.Mappings)
                foreach (string name in CHRISInputMapping.Inputs(entry))
                    if (name.StartsWith("key.") || name.StartsWith("mouse.")) m_Previous[name] = input.IsPressed(name);
        }

        void LatchHeld(ICHRISInputState input)
        {
            if (m_Mapping == null) return;
            foreach (var entry in m_Mapping.Mappings)
                foreach (string name in CHRISInputMapping.Inputs(entry))
                    if (input.IsPressed(name)) m_Latched.Add(name);
        }

        public void SuspendForEditor(ICHRISInputState input)
        {
            NeedsConvenienceRelease = true;
            ReleaseButtons();
            LatchHeld(input);
            Remember(input);
        }

        public void Step(CHRISInputMapping mapping, ICHRISInputState input, bool focused,
            bool strokeOrGrab, Vector3 headPosition, Vector3 headForward, bool brushOnRight, float deltaTime)
        {
            NeedsConvenienceRelease = false;
            if (mapping == null || mapping.SchemaVersion != CHRISInputMapping.BimanualVersion) { Stop(); return; }
            if (!ReferenceEquals(mapping, m_Mapping))
            {
                NeedsConvenienceRelease = true;
                bool firstActivation = m_Mapping == null;
                ReleaseButtons();
                m_Previous.Clear();
                m_Latched.Clear();
                m_BrushGrip = m_WandGrip = false;
                HandBackPending = RecenterPending = false;
                m_Mapping = mapping;
                if (firstActivation) Recenter(headPosition, headForward, brushOnRight);
                LatchHeld(input);
                Remember(input);
                return;
            }
            if (!focused)
            {
                NeedsConvenienceRelease = true;
                m_WasUnfocused = true;
                HandBackPending = true;
                ReleaseButtons();
                LatchHeld(input);
                Remember(input);
                return;
            }
            if (m_WasUnfocused) { m_WasUnfocused = false; LatchHeld(input); }
            m_Latched.RemoveWhere(name => !input.IsPressed(name));

            CHRISControlMode next = Mode;
            bool modePressed = false;
            if (Fresh(input, mapping.Find(CHRISMappedAction.ModeUI))) { next = CHRISControlMode.UI; modePressed = true; }
            else if (Fresh(input, mapping.Find(CHRISMappedAction.ModePosition))) { next = CHRISControlMode.Position; modePressed = true; }
            else if (Fresh(input, mapping.Find(CHRISMappedAction.ModeRotation))) { next = CHRISControlMode.Rotation; modePressed = true; }
            if (modePressed && (next != Mode || HandBackPending))
            {
                NeedsConvenienceRelease = true;
                Mode = next;
                HandBackPending = false;
                ReleaseButtons();
                LatchHeld(input);
                Remember(input);
                return;
            }
            if (Fresh(input, mapping.Find(CHRISMappedAction.HandBack)))
            { HandBackPending = true; NeedsConvenienceRelease = true; }
            if (HandBackPending) { ReleaseButtons(); Remember(input); return; }
            if (Fresh(input, mapping.Find(CHRISMappedAction.SelectHand, "brush"))) Selected = "brush";
            if (Fresh(input, mapping.Find(CHRISMappedAction.SelectHand, "wand"))) Selected = "wand";
            if (Fresh(input, mapping.Find(CHRISMappedAction.Recenter))) RecenterPending = true;
            if (RecenterPending && !strokeOrGrab && Brush.IsNeutral && Wand.IsNeutral &&
                !AnyHandControlHeld(input))
                Recenter(headPosition, headForward, brushOnRight);

            if (Mode == CHRISControlMode.UI)
            {
                ReleaseButtons();
                Remember(input);
                return;
            }

            if (Fresh(input, mapping.Find(CHRISMappedAction.GripToggle, "brush"))) m_BrushGrip = !m_BrushGrip;
            if (Fresh(input, mapping.Find(CHRISMappedAction.GripToggle, "wand"))) m_WandGrip = !m_WandGrip;
            Brush.Sample(Button(input, CHRISMappedAction.Trigger, "brush"), m_BrushGrip,
                Button(input, CHRISMappedAction.PrimaryButton, "brush"),
                Button(input, CHRISMappedAction.SecondaryButton, "brush"),
                Button(input, CHRISMappedAction.StickClick, "brush"), Axis(input, CHRISMappedAction.StickAxis, "brush"));
            Wand.Sample(Button(input, CHRISMappedAction.Trigger, "wand"), m_WandGrip,
                Button(input, CHRISMappedAction.PrimaryButton, "wand"),
                Button(input, CHRISMappedAction.SecondaryButton, "wand"),
                Button(input, CHRISMappedAction.StickClick, "wand"), Axis(input, CHRISMappedAction.StickAxis, "wand"));

            var selected = Selected == "brush" ? Brush : Wand;
            if (Mode == CHRISControlMode.Position)
            {
                var entry = mapping.Find(CHRISMappedAction.PoseXY, "selected");
                Vector2 xy = Axis(input, CHRISMappedAction.PoseXY, "selected");
                float scale = entry != null && entry.Source == CHRISMappingSource.MouseDelta ? MousePositionUnitsPerPixel :
                    KeyPositionUnitsPerSecond * deltaTime;
                var depth = mapping.Find(CHRISMappedAction.PoseDepth, "selected");
                float depthScale = depth.Source == CHRISMappingSource.MouseWheel ?
                    WheelDepthUnitsPerNotch : DepthUnitsPerSecond * deltaTime;
                selected.Position = ClampToOrigin(selected.Position + m_Right * (xy.x * scale) + Vector3.up * (xy.y * scale) +
                    m_Forward * (Axis1(input, CHRISMappedAction.PoseDepth) * depthScale), selected.Origin, MaxHandOffsetUnits);
            }
            else
            {
                var entry = mapping.Find(CHRISMappedAction.RotateXY, "selected");
                Vector2 xy = Axis(input, CHRISMappedAction.RotateXY, "selected");
                float scale = entry != null && entry.Source == CHRISMappingSource.MouseDelta ? MouseDegreesPerPixel :
                    KeyDegreesPerSecond * deltaTime;
                selected.Rotation = Quaternion.AngleAxis(xy.x * scale, Vector3.up) *
                    Quaternion.AngleAxis(-xy.y * scale, m_Right) * selected.Rotation;
                var roll = mapping.Find(CHRISMappedAction.RotateRoll, "selected");
                float rollScale = roll.Source == CHRISMappingSource.MouseWheel ?
                    WheelRollDegreesPerNotch : KeyDegreesPerSecond * deltaTime;
                selected.Rotation = Quaternion.AngleAxis(
                    Axis1(input, CHRISMappedAction.RotateRoll) * rollScale,
                    selected.Rotation * Vector3.forward) * selected.Rotation;
            }
            Remember(input);
        }

        bool AnyHandControlHeld(ICHRISInputState input)
        {
            if (input.MouseDelta != Vector2.zero || input.WheelNotches != 0) return true;
            foreach (var entry in m_Mapping.Mappings)
            {
                switch (entry.Action)
                {
                    case CHRISMappedAction.Trigger:
                    case CHRISMappedAction.GripToggle:
                    case CHRISMappedAction.PrimaryButton:
                    case CHRISMappedAction.SecondaryButton:
                    case CHRISMappedAction.StickClick:
                    case CHRISMappedAction.StickAxis:
                    case CHRISMappedAction.PoseXY:
                    case CHRISMappedAction.PoseDepth:
                    case CHRISMappedAction.RotateXY:
                    case CHRISMappedAction.RotateRoll:
                    case CHRISMappedAction.MoveView:
                        if (CHRISInputMapping.Inputs(entry).Any(name => Held(input, name))) return true;
                        break;
                }
            }
            return false;
        }
    }
}
