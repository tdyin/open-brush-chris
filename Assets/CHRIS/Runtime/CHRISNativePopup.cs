// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace TiltBrush
{
    // A small, nonmodal status surface drawn like Open Brush's wand panels: their wireframe
    // border around the What's-new black background, and native icon buttons with hover
    // descriptions. The "CHRIS" title moves the panel.
    // Mapping review and voice commands live outside the headset.
    public class CHRISNativePopup : PopUpWindow
    {
        public const float Width = 2.4f;
        public const float Height = 3.2f;
        public const float IconSize = 0.34f;
        // Inset of the black background inside the wire border.
        const float BackgroundInset = 0.06f;
        // Icons from Assets/Resources/Icons; one line each to swap.
        public const string StopIcon = "power";
        public const string StartIcon = "play";
        public const string ApproveIcon = "approve";
        public const string DeclineIcon = "decline";

        CHRISPanel m_Model;
        Renderer m_Border;
        TextMeshPro m_Status;
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
            var manager = PanelManager.m_Instance;
            BuildView(manager == null || manager.AdvancedModeActive());
            base.Init(parent, "");
            m_Model?.Opened(this);
            RefreshStatus();
        }

        public void BuildView(bool advanced = true)
        {
            var resources = CHRISUIResources.Load();
            resources.NativeBackground(transform, new Vector2(Width - 2 * BackgroundInset, Height - 2 * BackgroundInset));
            m_Border = resources.NativeWindow(transform, new Vector2(Width, Height), advanced);
            resources.NativeText(transform, "Title", new Vector3(0, 1.22f, -0.04f), new Vector2(1.2f, 0.34f), "CHRIS", 1.2f);
            // The title is the drag handle; the content and border are not.
            resources.DragStrip(transform, "Drag title", new Vector3(0, 1.22f, -0.06f), new Vector2(1.2f, 0.34f),
                () => (m_ParentPanel as CHRISFloatingPanel)?.BeginDrag());
            // Stop is always shown.
            resources.NativeIconButton(transform, "Local Stop", new Vector3(0.86f, 1.22f, -0.06f), IconSize,
                StopIcon, "Stop: CHRIS releases control now", LocalStop);
            m_Status = resources.NativeBody(transform, "Status", new Vector3(0, 0.86f, -0.04f),
                new Vector2(2.0f, 0.42f), "Physical controllers", 0.72f);
            BuildTestArea(resources);
        }

        // Up to four instruction lines, eight case Start icons (two rows of four, each with its
        // case number) and Approve/Decline. Buttons stay hidden until the runner shows cases or
        // a live nonce.
        void BuildTestArea(CHRISUIResources resources)
        {
            for (int i = 0; i < m_TestLines.Length; i++)
                m_TestLines[i] = resources.NativeBody(transform, "Test line " + (i + 1),
                    new Vector3(0, 0.5f - i * 0.27f, -0.04f), new Vector2(2.0f, 0.27f), "", 0.8f);
            for (int i = 0; i < m_CaseButtons.Length; i++)
            {
                int slot = i;
                var button = resources.NativeIconButton(transform, "Test case " + (i + 1),
                    new Vector3(-0.69f + (i % 4) * 0.46f, -0.66f - (i / 4) * 0.46f, -0.06f), IconSize, StartIcon, "",
                    () => StartCase(slot));
                // The case number sits under the icon, sized in panel units despite the icon scale.
                button.Label = resources.NativeText(button.transform, "Case number", new Vector3(0, -0.66f, 0),
                    new Vector2(1.4f, 0.4f), "", 0.62f / IconSize);
                button.gameObject.SetActive(false);
                m_CaseButtons[i] = button;
            }
            m_Approve = resources.NativeIconButton(transform, "Test approve", new Vector3(-0.4f, -0.85f, -0.06f),
                IconSize * 1.25f, ApproveIcon, "Approve this case", () => Decide(true));
            m_Decline = resources.NativeIconButton(transform, "Test decline", new Vector3(0.4f, -0.85f, -0.06f),
                IconSize * 1.25f, DeclineIcon, "Decline", () => Decide(false));
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

        internal static string CaseHover(int id, string title) => "Start case " + id + ": " + title;

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
                if (!shown) continue;
                CHRISUIResources.SetLabel(m_CaseButtons[i], display.Cases[i].id.ToString());
                m_CaseButtons[i].SetHover(CaseHover(display.Cases[i].id, display.Cases[i].title));
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
            // Brighten the border under gaze exactly like a native panel border.
            if (m_Border != null && m_ParentPanel != null && PanelManager.m_Instance != null)
                m_Border.sharedMaterial.SetColor("_Color", m_ParentPanel.GetGazeColorFromActiveGazePercent());
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
