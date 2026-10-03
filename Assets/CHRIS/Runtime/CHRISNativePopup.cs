// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace TiltBrush
{
    // A small, nonmodal status surface. Mapping review and voice commands live outside the headset.
    public class CHRISNativePopup : PopUpWindow
    {
        public const float Width = 3.4f;
        public const float Height = 1.7f;

        CHRISPanel m_Model;
        TextMeshPro m_Status, m_Mode, m_Roles;
        float m_NextStatusPoll;
        long m_LastMappingRevision = -1;
        JObject m_MappingStatus;

        public override void Init(GameObject parent, string text)
        {
            m_Model = CHRISPanel.Instance;
            m_AutoPlaceButtons = Array.Empty<PopUpButton>();
            m_OrderedPageButtons = Array.Empty<NavButton>();
            m_Persistent = true;
            m_BlockUndoRedo = false;
            m_TransitionDuration = 0.1f;
            m_ReticleBounds = new Vector3(Width, Height, -0.35f);
            m_PopUpForwardOffset = -0.4f;
            BuildView();
            base.Init(parent, "");
            m_Model?.Opened(this);
            RefreshStatus();
        }

        public void BuildView()
        {
            var resources = CHRISUIResources.Load();
            resources.Surface(transform, "CHRIS status background", Vector3.zero,
                new Vector2(Width, Height), new Color(0.025f, 0.03f, 0.035f));
            resources.Frame(transform, new Vector2(Width, Height));
            var drag = resources.Button(transform, "Drag CHRIS status", new Vector3(-0.55f, 0.59f, -0.06f),
                new Vector2(2.05f, 0.38f), "CHRIS status",
                () => (m_ParentPanel as CHRISFloatingPanel)?.BeginDrag());
            drag.Label.fontSize = 1.1f;
            resources.Button(transform, "Local Stop", new Vector3(1.15f, 0.59f, -0.06f),
                new Vector2(0.78f, 0.38f), "Stop", LocalStop, true);
            m_Status = resources.Text(transform, "Status", new Vector3(0, 0.13f, -0.04f),
                new Vector2(3.0f, 0.28f), "Physical controllers", 1.12f, TextAlignmentOptions.Center);
            m_Mode = resources.Text(transform, "Control mode", new Vector3(0, -0.22f, -0.04f),
                new Vector2(3.0f, 0.28f), "No mapping", 0.94f, TextAlignmentOptions.Center);
            m_Roles = resources.Text(transform, "Physical hand roles", new Vector3(0, -0.56f, -0.04f),
                new Vector2(3.0f, 0.23f), HandRoles(InputManager.m_Instance != null &&
                    !InputManager.m_Instance.WandOnRight), 0.7f, TextAlignmentOptions.Center);
        }

        internal static string ControlLabel(string wireState, bool pending, CHRISInputMapping mapping,
            bool keyboardUI, bool poseOwned, bool handBackPending)
        {
            if (pending) return "Mapping pending";
            if (mapping == null) return keyboardUI ? "Keyboard menu control" :
                wireState == "stopped" ? "Mapping stopped" : "Physical controllers";
            if (mapping.SchemaVersion == CHRISInputMapping.Version) return "Mapping active";
            if (handBackPending) return "Hand-back pending";
            if (!keyboardUI && !poseOwned) return "Physical controllers";
            return keyboardUI ? "Keyboard menu control" : "Virtual hands active";
        }

        internal static string ModeLabel(CHRISInputMapping mapping, CHRISBimanualInput hand,
            bool keyboardUI, bool poseOwned, bool brushRight)
        {
            if (mapping == null) return "No active mapping";
            if (mapping.SchemaVersion == CHRISInputMapping.Version) return "Keyboard mapping";
            string selected = hand.Selected == "wand" ? "Wand" : "Brush";
            string side = (hand.Selected == "wand" ? !brushRight : brushRight) ? "Right" : "Left";
            string mode = keyboardUI ? "Menu" : poseOwned ? hand.Mode.ToString() : "Physical";
            if (poseOwned && !keyboardUI && mapping.SchemaVersion == CHRISInputMapping.DirectPoseVersion)
                mode = "Pose";
            return mode + " · " + selected + " (" + side + ")";
        }

        internal static string HandRoles(bool brushRight) => brushRight ?
            "Brush: Right  ·  Wand: Left" : "Brush: Left  ·  Wand: Right";

        void LocalStop()
        {
            m_Model?.StopLocal();
            if (CHRISInputMappingHost.MappingInUse) CHRISInputMappingHost.StopMapping();
            RefreshStatus();
        }

        internal void RefreshStatus()
        {
            if (m_Status == null) return;
            if (m_MappingStatus == null || m_LastMappingRevision != CHRISInputMappingHost.MappingRevision ||
                Time.unscaledTime >= m_NextStatusPoll)
            {
                m_MappingStatus = CHRISInputMappingHost.MappingStatus();
                m_LastMappingRevision = CHRISInputMappingHost.MappingRevision;
                m_NextStatusPoll = Time.unscaledTime + 0.25f;
            }
            var mapping = CHRISInputMappingHost.Remap.Active;
            bool pending = m_MappingStatus["pending_digest"].Type != JTokenType.Null;
            bool keyboardUI = CHRISBimanualHost.InUIMode || CHRISBimanualHost.RecoveryUI;
            bool poseOwned = CHRISBimanualHost.OwnsMappedPose;
            bool brushRight = InputManager.m_Instance != null && !InputManager.m_Instance.WandOnRight;
            SetText(m_Status, ControlLabel((string)m_MappingStatus["state"], pending, mapping,
                keyboardUI, poseOwned, CHRISBimanualHost.Input.HandBackPending &&
                (CHRISBimanualHost.OwnsBrushPose || CHRISBimanualHost.OwnsWandPose)));
            SetText(m_Mode, ModeLabel(mapping, CHRISBimanualHost.Input, keyboardUI, poseOwned, brushRight));
            SetText(m_Roles, HandRoles(brushRight));
        }

        static void SetText(TextMeshPro text, string value)
        {
            if (text.text != value) text.text = value;
        }

        protected override void BaseUpdate()
        {
            base.BaseUpdate();
            RefreshStatus();
        }

        protected override void DestroyPopUpWindow()
        {
            var host = m_ParentPanel as CHRISFloatingPanel;
            base.DestroyPopUpWindow();
            if (host != null) host.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            GetComponent<CHRISMaterialOwner>()?.Release();
            m_Model?.Closed(this);
        }
    }
}
