// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace TiltBrush
{
    public class CHRISNativePopup : PopUpWindow
    {
        CHRISUIResources m_Resources;
        CHRISPanel m_Model;
        TextMeshPro m_Status;
        TextMeshPro m_Detail;
        readonly CHRISNativeButton[] m_Choices = new CHRISNativeButton[4];
        bool m_Dirty;
        CHRISNativeButton m_Microphone, m_Edit;
        string m_PagedSummary;
        string[] m_ReviewPages;
        int m_ReviewPage, m_LastViewedPage;

        public override void Init(GameObject parent, string text)
        {
            m_Resources = CHRISUIResources.Load();
            m_Model = CHRISPanel.Instance;
            m_AutoPlaceButtons = Array.Empty<PopUpButton>();
            m_OrderedPageButtons = Array.Empty<NavButton>();
            m_Persistent = true;
            m_BlockUndoRedo = true;
            m_TransitionDuration = 0.1f;
            m_ReticleBounds = new Vector3(3.8f, 4, -0.35f);
            m_PopUpForwardOffset = -0.4f;
            BuildView();
            base.Init(parent, "");
            if (m_Model != null)
            {
                m_Model.Changed += MarkDirty;
                m_Model.Opened(this);
                m_OnClose += () => m_Model.Closed(this);
            }

            Draw();
        }

        // Also used by the editor-only layout verification, without starting a live App.
        public void BuildView()
        {
            m_Resources = CHRISUIResources.Load();
            m_Resources.Surface(transform, "CHRIS background", Vector3.zero, new Vector2(3.8f, 4), new Color(0.025f, 0.045f, 0.06f));
            m_Resources.Button(transform, "Drag CHRIS", new Vector3(-0.65f, 1.77f, -0.06f), new Vector2(2.25f, 0.34f),
                "CHRIS — hold to move", () => (m_ParentPanel as CHRISFloatingPanel)?.BeginDrag());
            m_Resources.Button(transform, "Local Stop", new Vector3(1.15f, 1.77f, -0.06f), new Vector2(1.2f, 0.34f), "STOP",
                () => m_Model?.StopLocal(), true);
            m_Microphone = m_Resources.Button(transform, "Record request", new Vector3(-0.9f, 1.34f, -0.06f),
                new Vector2(1.7f, 0.32f), "Record", () => m_Model?.TapMicrophone());
            m_Edit = m_Resources.Button(transform, "Edit request", new Vector3(0.9f, 1.34f, -0.06f),
                new Vector2(1.7f, 0.32f), "Edit request", EnterRequest);
            m_Status = m_Resources.Text(transform, "Status", new Vector3(0, 0.96f, -0.04f),
                new Vector2(3.5f, 0.38f), "Assist", 1.15f);
            m_Detail = m_Resources.Text(transform, "Review", new Vector3(0, -0.02f, -0.04f),
                new Vector2(3.5f, 1.48f), "", 1.15f);
            m_Detail.enableAutoSizing = true;
            m_Detail.fontSizeMin = 0.7f;
            m_Detail.fontSizeMax = 1.15f;
            // Up to five exact command rows must remain visible, not silently ellipsized.
            m_Detail.overflowMode = TextOverflowModes.Overflow;
            for (int i = 0; i < 4; i++)
                m_Choices[i] = m_Resources.Button(transform, "Choice " + i,
                    new Vector3(i % 2 == 0 ? -0.9f : 0.9f, -0.95f - i / 2 * 0.4f, -0.06f),
                    new Vector2(1.7f, 0.32f), "", null);
            m_Resources.Button(transform, "Close CHRIS", new Vector3(0.9f, -1.73f, -0.06f), new Vector2(1.7f, 0.32f), "Close",
                () => RequestClose(true));
        }

        void MarkDirty()
        {
            m_Dirty = true;
        }

        protected override void BaseUpdate()
        {
            base.BaseUpdate();
            if (m_Dirty)
            {
                m_Dirty = false;
                Draw();
            }
        }

        void ConfigureChoice(int slot, string label, Action click, bool available = true)
        {
            var button = m_Choices[slot];
            button.gameObject.SetActive(true);
            button.Label.text = label;
            button.Click = click;
            button.SetButtonAvailable(available && !m_Model.WaitingForRelease);
            button.SetColor(Color.white);
        }

        void Draw()
        {
            if (m_Model == null) return;
            foreach (var button in m_Choices)
            {
                button.gameObject.SetActive(false);
                button.Click = null;
            }
            var voice = m_Model.Voice;
            bool recording = voice?.Session.State == CHRISVoiceSession.Phase.Recording;
            bool voiceBusy = voice?.Session.IsActive == true;
            m_Microphone.Label.text = recording ? "Finish recording" : "Record / Re-record";
            m_Microphone.SetButtonAvailable(!voiceBusy || recording);
            m_Edit.SetButtonAvailable(true);
            m_Status.text = m_Model.AssistanceStatus();
            if (voiceBusy)
            {
                string transcript = voice.Session.Transcript;
                m_Detail.text = transcript.Length <= 320 ? transcript : "..." + transcript.Substring(transcript.Length - 320);
                return;
            }
            var client = m_Model.Assistance;
            if (m_Model.ReviewedApproval != null && !m_Model.Correcting)
            {
                DrawApprovalReview(client);
                return;
            }
            m_Detail.text = m_Model.HasReplacement ? "Replacement request: " + m_Model.Prompt :
                client.CanReview ? m_Model.Notice : client.StartBlockedReason ?? "Request: " + m_Model.Prompt;
            ConfigureChoice(0, "Retry", () => RetryAssistance(client), !client.Busy);
            ConfigureChoice(1, "Mic: " + (voice?.MicrophoneLabel ?? "Windows default"), m_Model.ChangeMicrophone);
        }

        void DrawApprovalReview(CHRISAssistanceClient client)
        {
            var reviewedApproval = m_Model.ReviewedApproval;
            if (m_PagedSummary != m_Model.ReviewText)
            {
                m_PagedSummary = m_Model.ReviewText;
                m_ReviewPages = ReviewPages(m_PagedSummary);
                m_ReviewPage = m_LastViewedPage = 0;
            }
            m_Detail.text = m_ReviewPages[m_ReviewPage];
            bool allViewed = m_LastViewedPage == m_ReviewPages.Length - 1;
            bool proposalMatchesReview = client.CanReview && JToken.DeepEquals(reviewedApproval, client.Task["approval"]) &&
                m_Model.ReviewText == (string)client.Task["summary"];
            var context = m_Model.Gateway.Capture();
            bool fresh = CHRISPanel.ApprovalCurrent(reviewedApproval, context, CHRISCommandGateway.Now);
            bool ready = (bool)context["ready"] && !(bool)context["stroke_active"];
            if (m_Model.WaitingForRelease) m_Status.text = m_Model.Notice;
            else if (!proposalMatchesReview) m_Status.text = "This proposal changed or was cancelled. Re-record or edit your request.";
            else if (!fresh) m_Status.text = "The sketch changed or this review expired. Re-record or edit your request.";
            else if (!ready) m_Status.text = "Wait for the sketch to be ready before confirming.";
            else m_Status.text = m_ReviewPages.Length == 1 ? "Review these exact commands. Confirm to apply them in order." :
                "Review page " + (m_ReviewPage + 1) + " of " + m_ReviewPages.Length + ". Read every page before confirming.";
            ConfigureChoice(0, "Confirm commands", () => m_Model.Decide(true),
                allViewed && proposalMatchesReview && fresh && ready && !client.Busy && !client.CancelWanted);
            ConfigureChoice(2, "Previous page", () => ChangeReviewPage(-1), m_ReviewPage > 0);
            ConfigureChoice(3, "Next page", () => ChangeReviewPage(1), m_ReviewPage + 1 < m_ReviewPages.Length);
        }

        void ChangeReviewPage(int delta)
        {
            m_ReviewPage = Math.Max(0, Math.Min(m_ReviewPages.Length - 1, m_ReviewPage + delta));
            m_LastViewedPage = Math.Max(m_LastViewedPage, m_ReviewPage);
            MarkDirty();
        }

        internal static string[] ReviewPages(string summary)
        {
            var pages = new List<string>();
            int offset = 0;
            while (offset < summary.Length)
            {
                int length = Math.Min(320, summary.Length - offset);
                int lines = 0;
                for (int i = offset; i < offset + length; i++)
                    if (summary[i] == '\n' && ++lines == 6) { length = i - offset + 1; break; }
                if (offset + length < summary.Length)
                {
                    int boundary = summary.LastIndexOf('\n', offset + length - 1, length);
                    if (boundary < offset) boundary = summary.LastIndexOf(' ', offset + length - 1, length);
                    if (boundary >= offset) length = boundary - offset + 1;
                    else if (char.IsHighSurrogate(summary[offset + length - 1])) length--;
                }
                pages.Add(summary.Substring(offset, length));
                offset += length;
            }
            return pages.Count == 0 ? new[] { "" } : pages.ToArray();
        }

        void RetryAssistance(CHRISAssistanceClient client)
        {
            if (client.CancelWanted)
                client.Cancel();
            else if (client.PendingRequest != null)
                client.RetrySubmission();
            else if ((string)client.Task?["status"] == "paused" || (string)client.Task?["status"] == "unverified")
                client.Resume();
            else
                client.Refresh();
        }

        void EnterRequest()
        {
            if (m_Model == null) return;
            long intent = m_Model.BeginCorrection();
            KeyboardPopUpWindow.m_InitialText = m_Model.Prompt;
            var obj = m_ParentPanel.CreatePopUp(m_Resources.KeyboardPrefab, transform.position - transform.forward * 0.3f, true, true);
            var keyboard = obj.GetComponentInChildren<KeyboardUI>();
            keyboard.KeyPressed += (sender, key) =>
            {
                if (key.IsPress && key.Key.KeyType == KeyboardKeyType.Enter)
                {
                    m_Model.CompleteTextEdit(intent, keyboard.ConsoleContent);
                }
            };
        }

        protected override void DestroyPopUpWindow()
        {
            var host = m_ParentPanel as CHRISFloatingPanel;
            base.DestroyPopUpWindow();
            if (host != null)
                host.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            if (m_Model != null)
            {
                m_Model.Changed -= MarkDirty;
                m_Model.Closed(this);
            }

            foreach (var renderer in GetComponentsInChildren<MeshRenderer>())
                if (renderer.GetComponent<TextMeshPro>() == null && renderer.GetComponent<CHRISNativeButton>() == null)
                {
                    if (Application.isPlaying)
                        Destroy(renderer.sharedMaterial);
                    else
                        DestroyImmediate(renderer.sharedMaterial);
                }
        }
    }
}
