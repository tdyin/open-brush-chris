// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using TMPro;
using UnityEngine;

namespace TiltBrush
{
    // Native UIComponent: pointer focus, press/release and input capture belong to Open Brush.
    public class CHRISNativeButton : BaseButton
    {
        public Action Click;
        public TextMeshPro Label;
        public MeshRenderer Icon;
        public Color Tint = new Color(0.15f, 0.25f, 0.34f);
        public void SetContent(string label, string icon = null)
        {
            Label.text = label;
            if (Icon == null) return;
            Icon.gameObject.SetActive(icon != null);
            if (icon != null) CHRISUIResources.SetIcon(Icon, icon);
            var position = Label.transform.localPosition;
            position.x = icon == null ? 0 : 0.16f;
            Label.transform.localPosition = position;
        }

        public void SetPrimary(bool primary)
        {
            Tint = primary ? new Color(0, 0.48f, 0.63f) : new Color(0.09f, 0.105f, 0.12f);
            SetColor(Color.white);
        }
        public void Configure()
        {
            m_AtlasTexture = false;
            m_HoverScale = 1.025f;
            m_ButtonHasPressedAudio = true;
            m_ButtonRenderer = GetComponent<Renderer>();
            m_Collider = GetComponent<BoxCollider>();
            m_CurrentButtonState = ButtonState.Untouched;
            m_ScaleBase = transform.localScale;
            m_ZAdjustBase = transform.localPosition.z;
        }

        protected override void Awake()
        {
            Configure();
            base.Awake();
        }

        protected override void OnButtonPressed()
        {
            Click?.Invoke();
        }

        protected override void SetMaterialFloat(string name, float value)
        {
            if (m_ButtonRenderer != null && m_ButtonRenderer.sharedMaterial.HasProperty(name))
                m_ButtonRenderer.sharedMaterial.SetFloat(name, value);
        }

        protected override void SetMaterialColor(Color color)
        {
            if (m_ButtonRenderer != null)
                m_ButtonRenderer.sharedMaterial.SetColor("_Color", color);
        }

        public override void SetColor(Color color)
        {
            base.SetColor(color * Tint);
            if (Label != null)
                Label.color = IsAvailable() ? Color.white : Color.gray;
            if (Icon != null) Icon.sharedMaterial.color = IsAvailable() ? Color.white : Color.gray;
        }

        protected override void OnDestroy()
        {
            if (m_ButtonRenderer != null)
            {
                if (Application.isPlaying)
                    Destroy(m_ButtonRenderer.sharedMaterial);
                else
                    DestroyImmediate(m_ButtonRenderer.sharedMaterial);
            }

            base.OnDestroy();
        }
    }
}
