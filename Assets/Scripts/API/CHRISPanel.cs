// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TiltBrush
{
    // Assistance is the only CHRIS mode. Native drawing and panel input remain Open Brush's.
    [DefaultExecutionOrder(9000)]
    public class CHRISPanel : MonoBehaviour
    {
        public static CHRISPanel Instance { get; private set; }
        public CHRISCommandGateway Gateway;
        public CHRISAssistanceClient Assistance { get; private set; }
        public CHRISVoiceInput Voice { get; private set; }
        public CHRISNativePopup Popup { get; private set; }
        public string Notice { get; private set; } = "Record or type a request. Confirm only after reviewing the commands.";
        public string Prompt { get; private set; } = "Make my brush blue";
        public JObject ReviewedApproval { get; private set; }
        public string ReviewText { get; private set; }
        public bool WaitingForRelease => m_AfterRelease != null;
        public bool Correcting { get; private set; }
        public bool HasReplacement => m_ReplacementPrompt != null;
        public event Action Changed;
        Action m_AfterRelease;
        float m_ReleaseDeadline, m_NextPoll;
        long m_IntentVersion, m_RecordingIntent;
        string m_ReplacementPrompt;
        bool m_ReplacementExample;

        void Start()
        {
            Instance = this;
            Assistance = GetComponent<CHRISAssistanceClient>() ?? gameObject.AddComponent<CHRISAssistanceClient>();
            Voice = GetComponent<CHRISVoiceInput>() ?? gameObject.AddComponent<CHRISVoiceInput>();
            Assistance.Changed += Refresh;
            Voice.Changed += Refresh;
            Voice.Finalized += AcceptVoice;
            Gateway.Stopped += OnGatewayStopped;
        }

        public void Opened(CHRISNativePopup popup)
        {
            if (Popup != null && Popup != popup) Popup.RequestClose(true);
            Popup = popup;
            ClearReview();
            if (Assistance.TaskId != null) Assistance.Refresh();
            Refresh();
        }

        public void Closed(CHRISNativePopup popup)
        {
            if (Popup != popup) return;
            Popup = null;
            CancelInput();
            ClearReview();
        }

        void ClearReview()
        {
            ReviewedApproval = null;
            ReviewText = null;
            m_AfterRelease = null;
        }

        void CancelInput()
        {
            m_IntentVersion++;
            Voice?.CancelRecording();
            m_ReplacementPrompt = null;
            Correcting = false;
        }

        void OnGatewayStopped()
        {
            CancelInput();
            ClearReview();
            Assistance?.Cancel();
            Notice = "Stopped locally. Completed changes remain; pending work is cancelled.";
            Refresh();
        }

        public void StopLocal()
        {
            (Popup?.GetParentPanel() as CHRISFloatingPanel)?.EndDrag();
            Gateway.Stop();
        }

        public void Back()
        {
            StopLocal();
        }

        public long BeginCorrection()
        {
            StopLocal();
            Correcting = true;
            ClearReview();
            Notice = "Previous request cancelled. Record or edit a replacement.";
            Refresh();
            return m_IntentVersion;
        }

        public void TapMicrophone()
        {
            if (Voice.Session.State == CHRISVoiceSession.Phase.Recording)
            {
                Voice.FinishRecording();
                return;
            }
            m_RecordingIntent = BeginCorrection();
            Voice.StartRecording();
        }

        public void ChangeMicrophone()
        {
            BeginCorrection();
            Voice.NextMicrophone();
        }

        void AcceptVoice(string transcript)
        {
            CompleteTextEdit(m_RecordingIntent, transcript);
        }

        public void CompleteTextEdit(long intent, string text)
        {
            if (intent != m_IntentVersion || !Correcting) return;
            text = text?.Trim();
            if (!CHRISVoiceSession.ValidText(text, allowEmpty: false))
            {
                Notice = "Enter a request of 1-2000 characters, or re-record.";
                Refresh();
                return;
            }
            Prompt = text;
            m_ReplacementPrompt = text;
            m_ReplacementExample = false;
            Notice = "Preparing your request. Previous cancellation must finish first.";
            Refresh();
        }

        public void ProposeExample()
        {
            BeginCorrection();
            m_ReplacementPrompt = Prompt;
            m_ReplacementExample = true;
            Refresh();
        }

        public static bool InputReleased()
        {
            if (InputManager.m_Instance == null || InputManager.Controllers == null) return false;
            return !InputManager.m_Instance.GetCommand(InputManager.SketchCommands.Activate) &&
                !InputManager.Controllers.Any(c => c != null && (c.GetVrInput(VrInput.Trigger) || c.GetVrInput(VrInput.Grip))) &&
                !(Mouse.current?.leftButton.isPressed ?? false);
        }

        public static bool PassiveNativeUIHover()
        {
            var popup = Instance?.Popup;
            var controls = SketchControlsScript.m_Instance;
            return popup != null && popup.IsOpen() && popup.GetParentPanel() is CHRISFloatingPanel floating &&
                !floating.IsDragging && popup.GetParentPanel()?.PanelPopUp == popup &&
                controls != null && controls.IsUserLookingAtPanel(popup.GetParentPanel()) && InputReleased();
        }

        public static bool SameContext(JObject reviewed, JObject current) => reviewed != null && current != null &&
            (string)reviewed["host_session"] == (string)current["host_session"] &&
            (long)reviewed["revision"] == (long)current["revision"] &&
            (long)reviewed["authority_epoch"] == (long)current["authority_epoch"];

        public static bool ApprovalCurrent(JObject approval, JObject context, double now) =>
            SameContext(approval, context) && (double?)approval["expires_at"] > now;

        public void Decide(bool approve)
        {
            if (Correcting || Voice?.Session.IsActive == true || ReviewedApproval == null ||
                WaitingForRelease || Assistance.Busy || Assistance.CancelWanted) return;
            var reviewed = (JObject)ReviewedApproval.DeepClone();
            string summary = ReviewText;
            m_AfterRelease = () =>
            {
                if (approve && !ApprovalCurrent(reviewed, Gateway.Capture(), CHRISCommandGateway.Now))
                {
                    Notice = "The sketch changed or the review expired. Re-record or edit your request.";
                    return;
                }
                if ((string)Assistance.Task?["summary"] != summary)
                {
                    Notice = "The command summary changed. Cancel and request a new review.";
                    return;
                }
                Assistance.Decide(reviewed, approve);
                ClearReview();
            };
            m_ReleaseDeadline = Time.unscaledTime + 8;
            Notice = "Release the trigger or mouse button to continue.";
            Changed?.Invoke();
        }

        public string AssistanceStatus()
        {
            if (Voice?.Session.IsActive == true || Voice?.Session.State == CHRISVoiceSession.Phase.Failed)
                return Voice.Status;
            if (WaitingForRelease) return Notice;
            if (Correcting) return HasReplacement ? Assistance.StartBlockedReason ?? "Preparing your replacement request..." : Notice;
            if (Assistance.CancelWanted) return "Waiting for cancellation. Check / Retry before more work.";
            if (Assistance.Error != null) return Assistance.Error;
            var task = Assistance.Task;
            if (task == null) return Assistance.Busy ? "Planning your request..." : Notice;
            string status = ((string)task["status"])?.Replace('_', ' ') + ": " + (string)task["reason"];
            if (task["result"] is JObject result)
                status += "\nVerified " + result["completed"] + "; remaining " + result["remaining"] + ". " + result["reason"];
            return status;
        }

        public void Refresh()
        {
            if (ReviewedApproval != null && Assistance != null &&
                (!Assistance.CanReview || !JToken.DeepEquals(ReviewedApproval, Assistance.Task["approval"]) ||
                 ReviewText != (string)Assistance.Task["summary"]))
                ClearReview();
            if (!Correcting && !WaitingForRelease && Assistance != null && Assistance.CanReview && ReviewedApproval == null)
            {
                try
                {
                    var approval = (JObject)Assistance.Task["approval"];
                    var context = (JObject)Assistance.Task["context"];
                    CHRISCommandGateway.ValidateActions((JArray)approval["actions"], context);
                    ReviewedApproval = (JObject)approval.DeepClone();
                    ReviewText = (string)Assistance.Task["summary"];
                }
                catch (Exception)
                {
                    ClearReview();
                    Notice = "This proposal cannot be reviewed. Cancel it and request a new one.";
                }
            }
            Changed?.Invoke();
        }

        void Update()
        {
            if (Gateway == null || Assistance == null) return;
            if (m_AfterRelease != null)
            {
                if (Popup == null || Time.unscaledTime > m_ReleaseDeadline)
                {
                    m_AfterRelease = null;
                    Notice = "Input was not released. Review and confirm again.";
                    Refresh();
                }
                else if (InputReleased() && !CHRISCommandGateway.NativeInteractionBusy())
                {
                    var action = m_AfterRelease;
                    m_AfterRelease = null;
                    action();
                    Refresh();
                }
            }
            if (m_ReplacementPrompt != null && Assistance.CanStart && InputReleased() &&
                !CHRISCommandGateway.NativeInteractionBusy())
            {
                string prompt = m_ReplacementPrompt;
                m_ReplacementPrompt = null;
                Correcting = false;
                Assistance.Submit(prompt, m_ReplacementExample);
            }
            if (Popup != null && Time.unscaledTime >= m_NextPoll)
            {
                m_NextPoll = Time.unscaledTime + 1;
                Assistance.Refresh();
                Refresh();
            }
        }

        void OnDisable()
        {
            CancelInput();
            ClearReview();
            Gateway?.Stop("CHRIS UI disabled");
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (Assistance != null) Assistance.Changed -= Refresh;
            if (Voice != null) { Voice.Changed -= Refresh; Voice.Finalized -= AcceptVoice; }
            if (Gateway != null) Gateway.Stopped -= OnGatewayStopped;
        }
    }
}
