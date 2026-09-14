// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
namespace TiltBrush
{
    // A fresh joystick click toggles recording only while the non-drawing hand owns CHRIS input.
    // Opening, changing hands or recovering tracking while held requires release first.
    internal sealed class CHRISVoiceShortcut
    {
        bool m_Initialized, m_Scope, m_Tracked, m_RightHand;
        bool m_Quarantine, m_Armed, m_Pressed;
        public bool SuppressClick { get; private set; }

        public bool Sample(bool scope, bool tracked, bool rightHand, bool pressed)
        {
            bool changed = !m_Initialized || scope != m_Scope || tracked != m_Tracked || rightHand != m_RightHand;
            if (changed)
            {
                m_Armed = false;
                m_Quarantine = scope || m_Scope || m_Quarantine;
            }
            bool releaseFrame = !pressed && (m_Quarantine || (m_Scope && m_Pressed));
            if (tracked && !pressed)
            {
                m_Quarantine = false;
                m_Armed = true;
            }
            bool toggle = scope && tracked && !m_Quarantine && m_Armed && pressed && !m_Pressed;
            if (toggle) m_Armed = false;
            SuppressClick = scope || m_Quarantine || releaseFrame;
            m_Initialized = true;
            m_Scope = scope;
            m_Tracked = tracked;
            m_RightHand = rightHand;
            m_Pressed = pressed;
            return toggle;
        }

        public bool Suppresses(VrInput input) =>
            (input == VrInput.Thumbstick || input == VrInput.Directional) && SuppressClick;
    }
}
