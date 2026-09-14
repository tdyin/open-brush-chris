// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
namespace TiltBrush
{
    // Uses the same serialized geometry, atlas and native hover/press handling as peer Lab features.
    public class CHRISLabButton : BaseButton
    {
        protected override void OnButtonPressed()
        {
            var popup = CHRISPanel.Instance?.Popup;
            if (popup != null && popup.IsOpen()) popup.RequestClose(true);
            else CHRISFloatingPanel.Show();
        }

        public override void UpdateVisuals()
        {
            base.UpdateVisuals();
            bool open = CHRISPanel.Instance?.Popup?.IsOpen() == true;
            if (m_ToggleActive != open) { m_ToggleActive = open; SetButtonActivated(open); }
        }
    }
}
