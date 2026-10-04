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
        public const float Height = 3.2f;
        const float StatusTop = 0.75f; // status rows sit above the test area

        CHRISPanel m_Model;
        TextMeshPro m_Status, m_Mode, m_Roles;
        // Test runner area: instructions and buttons come from CHRISTestDisplay.
        readonly TextMeshPro[] m_TestLines = new TextMeshPro[CHRISTestDisplay.MaxLines];
        readonly CHRISNativeButton[] m_CaseButtons = new CHRISNativeButton[CHRISTestDisplay.MaxCases];
        CHRISNativeButton m_Approve, m_Decline;
        int m_TestVersion = -1;
        bool m_ButtonsLive;
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
            var drag = resources.Button(transform, "Drag CHRIS status", new Vector3(-0.55f, StatusTop + 0.59f, -0.06f),
                new Vector2(2.05f, 0.38f), "CHRIS status",
                () => (m_ParentPanel as CHRISFloatingPanel)?.BeginDrag());
            drag.Label.fontSize = 1.1f;
            resources.Button(transform, "Local Stop", new Vector3(1.15f, StatusTop + 0.59f, -0.06f),
                new Vector2(0.78f, 0.38f), "Stop", LocalStop, true);
            m_Status = resources.Text(transform, "Status", new Vector3(0, StatusTop + 0.13f, -0.04f),
                new Vector2(3.0f, 0.28f), "Physical controllers", 1.12f, TextAlignmentOptions.Center);
            m_Mode = resources.Text(transform, "Control mode", new Vector3(0, StatusTop - 0.22f, -0.04f),
                new Vector2(3.0f, 0.28f), "No mapping", 0.94f, TextAlignmentOptions.Center);
            m_Roles = resources.Text(transform, "Physical hand roles", new Vector3(0, StatusTop - 0.56f, -0.04f),
                new Vector2(3.0f, 0.23f), HandRoles(InputManager.m_Instance != null &&
                    !InputManager.m_Instance.WandOnRight), 0.7f, TextAlignmentOptions.Center);
            BuildTestArea(resources);
        }

        // Up to four instruction lines, eight case Start buttons (two rows) and Approve/Decline.
        // Buttons stay hidden until the runner shows cases or a live approval nonce.
        void BuildTestArea(CHRISUIResources resources)
        {
            for (int i = 0; i < m_TestLines.Length; i++)
                m_TestLines[i] = resources.Text(transform, "Test line " + (i + 1), new Vector3(0, -0.05f - i * 0.22f, -0.04f),
                    new Vector2(3.1f, 0.23f), "", 0.72f, TextAlignmentOptions.Center);
            for (int i = 0; i < m_CaseButtons.Length; i++)
            {
                int slot = i;
                m_CaseButtons[i] = resources.Button(transform, "Test case " + (i + 1),
                    new Vector3(-1.22f + (i % 4) * 0.81f, -1.0f - (i / 4) * 0.36f, -0.06f), new Vector2(0.76f, 0.3f), "",
                    () => StartCase(slot));
                m_CaseButtons[i].Label.fontSize = 0.6f;
                m_CaseButtons[i].gameObject.SetActive(false);
            }
            m_Approve = resources.Button(transform, "Test approve", new Vector3(-0.6f, -1.2f, -0.06f),
                new Vector2(1.0f, 0.36f), "Approve", () => Decide(true));
            m_Approve.SetPrimary(true); // blue, distinct from the red Stop
            m_Decline = resources.Button(transform, "Test decline", new Vector3(0.6f, -1.2f, -0.06f),
                new Vector2(1.0f, 0.36f), "Decline", () => Decide(false));
            m_Approve.gameObject.SetActive(false);
            m_Decline.gameObject.SetActive(false);
        }

        void StartCase(int slot)
        {
            var cases = CHRISTestDisplay.Instance.Cases;
            if (slot < cases.Length) CHRISTestDisplay.Instance.Start(cases[slot].id, CHRISCommandGateway.Now);
        }

        void Decide(bool approve) =>
            CHRISTestDisplay.Instance.Decide(approve, Time.realtimeSinceStartup, CHRISCommandGateway.Now);

        internal static string CaseLabel(int id, string title)
        {
            string label = id + " " + title;
            return label.Length > 14 ? label.Substring(0, 12) + ".." : label;
        }

        internal void RefreshTest()
        {
            var display = CHRISTestDisplay.Instance;
            display.Tick(Time.realtimeSinceStartup);
            bool live = display.ButtonsLive(Time.realtimeSinceStartup);
            if (display.Version == m_TestVersion && live == m_ButtonsLive) return;
            m_TestVersion = display.Version;
            m_ButtonsLive = live;
            for (int i = 0; i < m_TestLines.Length; i++)
                SetText(m_TestLines[i], i < display.Lines.Length ? display.Lines[i] : "");
            // While an approval is live, only Approve/Decline can be pressed.
            for (int i = 0; i < m_CaseButtons.Length; i++)
            {
                bool shown = !live && i < display.Cases.Length;
                m_CaseButtons[i].gameObject.SetActive(shown);
                if (shown) m_CaseButtons[i].SetContent(CaseLabel(display.Cases[i].id, display.Cases[i].title));
            }
            m_Approve.gameObject.SetActive(live);
            m_Decline.gameObject.SetActive(live);
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
            RefreshTest();
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
