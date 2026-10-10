// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;

namespace TiltBrush
{
    // An invisible native UIComponent along the panel border: hovering shows its native
    // description and pressing the trigger on it starts a drag. Focus is held while pressed.
    public class CHRISDragStrip : UIComponent
    {
        public Action Begin;

        public string Hover => m_DescriptionText;

        public void SetHover(string text)
        {
            if (App.Config != null) SetDescriptionText(text);
            else m_DescriptionText = text;
        }

        public override void GainFocus() => SetDescriptionActive(true);

        public override void LostFocus() => SetDescriptionActive(false);

        public override void ButtonPressed(UnityEngine.RaycastHit hit) => Begin?.Invoke();

        public override void ResetState()
        {
            base.ResetState();
            SetDescriptionActive(false);
        }
    }
}
