// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace TiltBrush
{
    // A small, nonmodal status surface built from Open Brush's own pop-up look and
    // TextActionButton widgets. Mapping review and voice commands live outside the headset.
    public class CHRISNativePopup : PopUpWindow
    {
        public const float Width = 3.4f;
        public const float Height = 2.8f;
        public const float ButtonWidth = 1.0f;

        CHRISPanel m_Model;
        TextMeshPro m_Status;
        // Test runner area: instructions and buttons come from CHRISTestDisplay.
        readonly TextMeshPro[] m_TestLines = new TextMeshPro[CHRISTestDisplay.MaxLines];
        readonly ActionButton[] m_CaseButtons = new ActionButton[CHRISTestDisplay.MaxCases];
        ActionButton m_Approve, m_Decline;
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
            resources.NativeWindow(transform, new Vector2(Width, Height));
            resources.NativeButton(transform, "Move CHRIS panel", new Vector3(-1.05f, 1.12f, -0.06f), ButtonWidth,
                "Move", () => (m_ParentPanel as CHRISFloatingPanel)?.BeginDrag());
            resources.NativeText(transform, "Title", new Vector3(0, 1.12f, -0.04f), new Vector2(1.0f, 0.3f), "CHRIS", 1.1f);
            // Stop is always shown.
            resources.NativeButton(transform, "Local Stop", new Vector3(1.05f, 1.12f, -0.06f), ButtonWidth, "STOP", LocalStop);
            m_Status = resources.NativeText(transform, "Status", new Vector3(0, 0.8f, -0.04f),
                new Vector2(3.1f, 0.25f), "Physical controllers", 0.72f);
            BuildTestArea(resources);
        }

        // Up to four instruction lines, eight case Start buttons (three per row) and
        // Approve/Decline. Buttons stay hidden until the runner shows cases or a live nonce.
        void BuildTestArea(CHRISUIResources resources)
        {
            for (int i = 0; i < m_TestLines.Length; i++)
                m_TestLines[i] = resources.NativeText(transform, "Test line " + (i + 1),
                    new Vector3(0, 0.5f - i * 0.24f, -0.04f), new Vector2(3.1f, 0.24f), "", 0.8f);
            for (int i = 0; i < m_CaseButtons.Length; i++)
            {
                int slot = i;
                m_CaseButtons[i] = resources.NativeButton(transform, "Test case " + (i + 1),
                    new Vector3(-1.05f + (i % 3) * 1.05f, -0.62f - (i / 3) * 0.28f, -0.06f), ButtonWidth, "",
                    () => StartCase(slot));
                m_CaseButtons[i].gameObject.SetActive(false);
            }
            m_Approve = resources.NativeButton(transform, "Test approve", new Vector3(-0.7f, -0.75f, -0.06f),
                ButtonWidth * 1.2f, "Approve", () => Decide(true));
            m_Decline = resources.NativeButton(transform, "Test decline", new Vector3(0.7f, -0.75f, -0.06f),
                ButtonWidth * 1.2f, "Decline", () => Decide(false));
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
            return label.Length > 16 ? label.Substring(0, 14) + ".." : label;
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
                if (shown) CHRISUIResources.SetLabel(m_CaseButtons[i], CaseLabel(display.Cases[i].id, display.Cases[i].title));
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

        // The one compact status line: control state and which physical hand holds which role.
        internal static string StatusLine(string control, bool brushRight) => control + "  ·  " + HandRoles(brushRight);

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
            SetText(m_Status, StatusLine(ControlLabel((string)m_MappingStatus["state"], pending, mapping,
                keyboardUI, poseOwned, CHRISBimanualHost.Input.HandBackPending &&
                (CHRISBimanualHost.OwnsBrushPose || CHRISBimanualHost.OwnsWandPose)), brushRight));
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
            // TextActionButton gives each highlight its own material instance; release those
            // copies, never the shared Open Brush materials they were made from.
            foreach (var button in GetComponentsInChildren<TextActionButton>(true))
            {
                var highlight = button.m_Highlight != null ? button.m_Highlight.GetComponent<MeshRenderer>() : null;
                if (highlight != null && highlight.sharedMaterial != null && highlight.sharedMaterial.name.EndsWith("(Instance)"))
                    Destroy(highlight.sharedMaterial);
            }
            m_Model?.Closed(this);
        }
    }
}
