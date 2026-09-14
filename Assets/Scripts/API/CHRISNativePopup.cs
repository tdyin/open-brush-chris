// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;

namespace TiltBrush
{
    public class CHRISNativePopup : PopUpWindow
    {
        public const float Width = 4.4f;
        public const float Height = 5.1f;
        const int LiveTranscriptLimit = 230;
        const int ReviewPageCharacterLimit = 320;
        const float ReviewRowWidth = 3.75f;
        const float ReviewBodyWidth = 3.25f;
        const float ReviewRowPadding = 0.06f;
        const float ReviewRowGap = 0.04f;

        sealed class ReviewRow
        {
            public GameObject Background;
            public TextMeshPro Number, Body;
        }

        enum ChoiceSlot { ConfirmOrRecover, RetryRecording, PreviousPage, NextPage }

        CHRISUIResources m_Resources;
        CHRISPanel m_Model;
        TextMeshPro m_Status, m_Subtitle, m_Detail, m_Hint, m_PageNumber;
        MeshRenderer m_StateIcon;
        CHRISNativeButton m_Microphone, m_Device, m_Speech;
        readonly CHRISNativeButton[] m_Choices = new CHRISNativeButton[4];
        readonly ReviewRow[] m_Rows = new ReviewRow[5];
        GameObject m_ReviewRows;
        bool m_Dirty, m_LastRightHand;
        string m_PagedSummary;
        string[] m_ReviewPages;
        int m_ReviewPage, m_LastViewedPage;

        public override void Init(GameObject parent, string text)
        {
            m_Model = CHRISPanel.Instance;
            m_AutoPlaceButtons = Array.Empty<PopUpButton>();
            m_OrderedPageButtons = Array.Empty<NavButton>();
            m_Persistent = m_BlockUndoRedo = true;
            m_TransitionDuration = 0.1f;
            m_ReticleBounds = new Vector3(Width, Height, -0.35f);
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

        // Shared by the live popup and deterministic editor layout checks.
        public void BuildView()
        {
            m_Resources = CHRISUIResources.Load();
            m_Resources.Surface(transform, "CHRIS background", Vector3.zero, new Vector2(Width, Height), new Color(0.025f, 0.03f, 0.035f));
            m_Resources.Frame(transform, new Vector2(Width, Height));
            BuildHeader();
            BuildTranscript();
            BuildActions();
            BuildFooter();
        }

        void BuildHeader()
        {
            var move = m_Resources.Button(transform, "Drag CHRIS", new Vector3(-0.7f, 2.18f, -0.06f), new Vector2(2.55f, 0.42f),
                "CHRIS", () => (m_ParentPanel as CHRISFloatingPanel)?.BeginDrag());
            move.Label.font = m_Resources.Font;
            move.Label.fontSharedMaterial = m_Resources.Font.material;
            move.Label.fontSize = 1.8f;
            move.SetContent("CHRIS", "more_solid");
            var stop = m_Resources.Button(transform, "Local Stop", new Vector3(1.45f, 2.18f, -0.06f), new Vector2(1.1f, 0.42f), "Stop", () => m_Model?.StopLocal(), true);
            stop.SetContent("Stop", "blank");
            stop.Icon.sharedMaterial.mainTexture = Texture2D.whiteTexture;
            stop.Icon.transform.localScale = Vector3.one * 0.15f;
            m_StateIcon = m_Resources.Icon(transform, "mic", new Vector3(-1.73f, 1.55f, -0.045f), 0.46f);
            m_Status = m_Resources.Text(transform, "Status", new Vector3(0.27f, 1.56f, -0.04f), new Vector2(3.25f, 0.48f), "Ready to listen", 1.65f);
            m_Status.font = m_Resources.HeadingFont;
            m_Status.fontSharedMaterial = m_Resources.HeadingMaterial;
            m_Subtitle = m_Resources.Text(transform, "Subtitle", new Vector3(0, 1.12f, -0.04f), new Vector2(3.85f, 0.32f), "", 1.0f);
        }

        void BuildTranscript()
        {
            m_Resources.Surface(transform, "Transcript card", new Vector3(0, 0.175f, -0.02f), new Vector2(3.95f, 1.55f), new Color(0.055f, 0.065f, 0.075f));
            m_Detail = m_Resources.Text(transform, "Review", new Vector3(0, 0.175f, -0.045f), new Vector2(3.65f, 1.32f), "", 1.45f);
            m_Detail.overflowMode = TextOverflowModes.Overflow;
            m_Detail.enableAutoSizing = false;
            m_ReviewRows = new GameObject("Ordered review rows");
            m_ReviewRows.transform.SetParent(transform, false);
            for (int i = 0; i < m_Rows.Length; i++)
            {
                m_Rows[i] = new ReviewRow {
                    Background = m_Resources.Surface(m_ReviewRows.transform, "Command " + (i + 1), Vector3.zero,
                        new Vector2(ReviewRowWidth, 1), new Color(0.075f, 0.085f, 0.095f)),
                    Number = m_Resources.Text(m_ReviewRows.transform, "Command number", Vector3.zero, new Vector2(0.3f, 1), "", m_Detail.fontSize),
                    Body = m_Resources.Text(m_ReviewRows.transform, "Command text", Vector3.zero, new Vector2(ReviewBodyWidth, 1), "", m_Detail.fontSize)
                };
                m_Rows[i].Body.overflowMode = TextOverflowModes.Overflow;
            }
            m_ReviewRows.SetActive(false);
        }

        void BuildActions()
        {
            m_Microphone = m_Resources.Button(transform, "Record request", new Vector3(0, -1.11f, -0.06f), new Vector2(2.85f, 0.49f), "Record", () => m_Model?.TapMicrophone());
            m_Device = m_Resources.Button(transform, "Microphone device", new Vector3(0, -0.735f, -0.06f), new Vector2(3.65f, 0.22f), "Windows default", () => m_Model?.ChangeMicrophone());
            m_Device.Label.fontSize = 0.74f;
            for (int i = 0; i < m_Choices.Length; i++)
            {
                bool navigation = i >= (int)ChoiceSlot.PreviousPage;
                m_Choices[i] = m_Resources.Button(transform, "Choice " + i,
                    new Vector3(i % 2 == 0 ? -1.02f : 1.02f, navigation ? -1.7f : -1.11f, -0.06f),
                    new Vector2(navigation ? 1.25f : 1.9f, navigation ? 0.28f : 0.49f), "", null);
                if (navigation) m_Choices[i].Label.fontSize = 0.95f;
            }
            m_PageNumber = m_Resources.Text(transform, "Review page", new Vector3(0, -1.7f, -0.04f), new Vector2(0.72f, 0.26f), "", 0.9f, TextAlignmentOptions.Center);
            m_Hint = m_Resources.Text(transform, "Recording hint", new Vector3(0, -1.66f, -0.04f), new Vector2(4f, 0.53f), "", 0.95f, TextAlignmentOptions.Center);
        }

        void BuildFooter()
        {
            m_Speech = m_Resources.Button(transform, "AI confirmation voice", new Vector3(-1.01f, -2.2f, -0.06f), new Vector2(1.92f, 0.43f), "Speech: On", () => m_Model?.Speech.Toggle());
            m_Resources.Button(transform, "Close CHRIS", new Vector3(1.01f, -2.2f, -0.06f), new Vector2(1.92f, 0.43f), "Close", () => RequestClose(true));
        }

        void MarkDirty() => m_Dirty = true;

        protected override void BaseUpdate()
        {
            base.BaseUpdate();
            if (m_Model != null && m_LastRightHand != m_Model.NonDrawingHandIsRight) m_Dirty = true;
            if (m_Dirty)
            {
                m_Dirty = false;
                Draw();
            }
        }

        void ConfigureChoice(ChoiceSlot slot, string label, Action click, bool available = true, string icon = null, bool primary = false)
        {
            var button = m_Choices[(int)slot];
            button.gameObject.SetActive(true);
            button.SetContent(label, icon);
            button.Click = click;
            button.SetButtonAvailable(available && !m_Model.WaitingForRelease);
            button.SetPrimary(primary);
        }

        void SetStateHeader(string title, string subtitle, string icon)
        {
            m_Status.text = title;
            m_Subtitle.text = subtitle ?? "";
            CHRISUIResources.SetIcon(m_StateIcon, icon);
        }

        void ShowRecordingAction(string label)
        {
            m_Microphone.gameObject.SetActive(true);
            m_Microphone.SetContent(label, "mic");
            m_Microphone.SetPrimary(true);
            m_Microphone.SetButtonAvailable(true);
            m_Hint.text = m_Model.RecordingShortcutHint;
        }

        void ShowCorrection() => ConfigureChoice(ChoiceSlot.RetryRecording, "Retry", m_Model.RetryRecording, icon: "mic");

        void ResetActions()
        {
            m_Detail.gameObject.SetActive(true);
            m_ReviewRows.SetActive(false);
            foreach (var button in m_Choices)
            {
                button.gameObject.SetActive(false);
                button.Click = null;
            }
            m_Microphone.gameObject.SetActive(false);
            m_Device.gameObject.SetActive(false);
            m_PageNumber.text = m_Hint.text = "";
            m_Speech.SetContent(m_Model.Speech.SpeechEnabled ? "Speech: On" : "Speech: Off", "video_audio");
            m_Speech.SetButtonAvailable(true);
            m_Speech.SetPrimary(m_Model.Speech.SpeechEnabled);
        }

        void Draw()
        {
            if (m_Model == null) return;
            m_LastRightHand = m_Model.NonDrawingHandIsRight;
            ResetActions();
            var voice = m_Model.Voice;
            var client = m_Model.Assistance;
            if (voice.Session.IsActive)
            {
                DrawVoice();
                return;
            }
            if (m_Model.ReviewedApproval != null && !m_Model.Correcting)
            {
                DrawApprovalReview(client);
                return;
            }
            if (voice.Session.State == CHRISVoiceSession.Phase.Failed)
            {
                SetStateHeader("Microphone or speech unavailable", "Check the input device and connection.", "warning");
                m_Detail.text = voice.Status;
                ShowRecordingAction("Retry");
                ShowDevice();
                return;
            }
            string status = (string)client.Task?["status"];
            if (client.Error != null || client.CancelWanted || status == "paused" || status == "unverified" ||
                (client.Task == null && (client.TaskId != null || client.PendingRequest != null) && !client.Busy))
            {
                SetStateHeader("Outcome needs checking", "Last verified status", "warning");
                m_Detail.text = m_Model.AssistanceStatus();
                ConfigureChoice(ChoiceSlot.ConfirmOrRecover, "Retry", () => RetryAssistance(client), !client.Busy, "refresh", true);
                m_Hint.text = client.CancelWanted ? "Checks cancellation before more work." :
                    client.PendingRequest != null ? "Recovers the same request." : "Checks status before continuing.";
                return;
            }
            if (m_Model.HasReplacement || (client.Busy && client.Task == null) || status == "proposing" || status == "pending")
            {
                SetStateHeader("Preparing commands", "Transcript complete", "more_solid");
                m_Detail.text = m_Model.Prompt;
                m_Hint.text = "Checking the request and sketch...\nExact commands appear next.";
                ShowCorrection();
                return;
            }
            if (status == "executing")
            {
                SetStateHeader("Executing", VerifiedProgress(client.Task), "settings");
                m_Detail.text = (string)client.Task["summary"] ?? m_Model.AssistanceStatus();
                m_Hint.text = "You can stop and take over.";
                return;
            }
            if (client.Task != null && CHRISAssistanceClient.Terminal(client.Task))
            {
                SetStateHeader(status == "succeeded" ? "Completed" : "Stopped", VerifiedProgress(client.Task), status == "succeeded" ? "ticktransparent" : "warning");
                m_Detail.text = m_Model.AssistanceStatus();
                ShowRecordingAction("Record");
                return;
            }
            SetStateHeader("Ready to listen", "Speak a request for your sketch.", "mic");
            m_Detail.text = string.IsNullOrEmpty(m_Model.Prompt) ? "What would you like to change?" : m_Model.Prompt;
            ShowRecordingAction("Record");
            ShowDevice();
        }

        void ShowDevice()
        {
            m_Device.gameObject.SetActive(true);
            m_Device.SetContent(m_Model.Voice.MicrophoneLabel, "mic");
            m_Device.SetButtonAvailable(!m_Model.Voice.Session.IsActive);
        }

        void DrawVoice()
        {
            var voice = m_Model.Voice;
            bool recording = voice.Session.State == CHRISVoiceSession.Phase.Recording;
            bool finalizing = voice.Session.State == CHRISVoiceSession.Phase.Finalizing;
            SetStateHeader(recording ? "Listening" : finalizing ? "Finalizing transcript" : "Connecting microphone",
                recording ? "Live transcript" : finalizing ? "Provisional transcript" : "Preparing speech input", "mic");
            string transcript = voice.Session.Transcript;
            // Only the live viewport tails a long utterance; finalized review text is never shortened.
            m_Detail.text = transcript.Length <= LiveTranscriptLimit ? transcript :
                "..." + transcript.Substring(transcript.Length - LiveTranscriptLimit);
            if (recording)
            {
                ShowRecordingAction("Finish recording");
                ShowDevice();
            }
            else
            {
                m_Hint.text = finalizing ? (voice.MicrophoneStopped ? "Microphone off. Finishing transcription..." : "Stopping microphone...") +
                    "\nText may still change." : m_Model.RecordingShortcutHint;
                ShowCorrection();
            }
        }

        static string VerifiedProgress(JObject task)
        {
            var result = task?["result"] as JObject;
            return result?["completed"] == null ? "Awaiting verification" :
                result["completed"] + " commands verified complete; " + result["remaining"] + " remaining.";
        }

        void DrawApprovalReview(CHRISAssistanceClient client)
        {
            var approval = m_Model.ReviewedApproval;
            if (m_PagedSummary != m_Model.ReviewText)
            {
                m_PagedSummary = m_Model.ReviewText;
                m_ReviewPages = FitReviewPages(m_PagedSummary);
                m_ReviewPage = m_LastViewedPage = 0;
            }
            m_Detail.text = m_ReviewPages[m_ReviewPage];
            DrawReviewRows(m_Detail.text);
            bool matches = client.CanReview && JToken.DeepEquals(approval, client.Task["approval"]) && m_Model.ReviewText == (string)client.Task["summary"];
            var context = m_Model.Gateway.Capture();
            bool fresh = CHRISPanel.ApprovalCurrent(approval, context, CHRISCommandGateway.Now);
            bool ready = (bool)context["ready"] && !(bool)context["stroke_active"];
            string reason = m_Model.WaitingForRelease ? m_Model.Notice : !matches ? "Proposal changed. Retry for a new review." :
                !fresh ? "Sketch changed or review expired. Retry." : !ready ? "Wait for the sketch to be ready." :
                m_Model.Speech.Error ?? (m_LastViewedPage < m_ReviewPages.Length - 1 ? "Read every page before confirming." : "");
            SetStateHeader("Review commands", reason, "Path_List");
            ConfigureChoice(ChoiceSlot.ConfirmOrRecover, "Confirm", () => m_Model.Decide(true),
                m_LastViewedPage == m_ReviewPages.Length - 1 && matches && fresh && ready && !client.Busy && !client.CancelWanted,
                "ticktransparent", true);
            ShowCorrection();
            if (m_ReviewPages.Length > 1)
            {
                ConfigureChoice(ChoiceSlot.PreviousPage, "Previous", () => ChangeReviewPage(-1), m_ReviewPage > 0);
                ConfigureChoice(ChoiceSlot.NextPage, "Next", () => ChangeReviewPage(1), m_ReviewPage + 1 < m_ReviewPages.Length);
                m_PageNumber.text = (m_ReviewPage + 1) + " / " + m_ReviewPages.Length;
            }
        }

        string[] FitReviewPages(string summary)
        {
            var pages = new List<string>();
            foreach (string candidate in ReviewPages(summary))
            {
                string remaining = candidate;
                while (remaining.Length > 0)
                {
                    int length = FittingPrefixLength(remaining);
                    if (length < remaining.Length) length = FindPageBoundary(remaining, length);
                    pages.Add(remaining.Substring(0, length));
                    remaining = remaining.Substring(length);
                }
            }
            return pages.Count == 0 ? new[] { "" } : pages.ToArray();
        }

        int FittingPrefixLength(string value)
        {
            int low = 1, high = value.Length;
            var bounds = m_Detail.rectTransform.sizeDelta;
            while (low < high)
            {
                int length = (low + high + 1) / 2;
                if (ReviewPageHeight(value.Substring(0, length)) <= bounds.y)
                    low = length;
                else high = length - 1;
            }
            return low;
        }

        static string[] ReviewParagraphs(string page) => Regex.Split(page, @"(?m)(?<=\n)(?=[1-5]\. )");

        static void SplitCommand(string paragraph, out string number, out string body)
        {
            bool numbered = paragraph.Length >= 3 && paragraph[0] >= '1' && paragraph[0] <= '5' &&
                paragraph[1] == '.' && paragraph[2] == ' ';
            number = numbered ? paragraph.Substring(0, 2) : "";
            body = (numbered ? paragraph.Substring(3) : paragraph).TrimEnd('\n', '\r');
        }

        float ReviewRowHeight(string paragraph)
        {
            SplitCommand(paragraph, out _, out string body);
            return m_Rows[0].Body.GetPreferredValues(body, ReviewBodyWidth, float.PositiveInfinity).y + 2 * ReviewRowPadding;
        }

        float ReviewPageHeight(string page)
        {
            var paragraphs = ReviewParagraphs(page);
            if (paragraphs.Length > m_Rows.Length) return float.PositiveInfinity;
            float height = Math.Max(0, paragraphs.Length - 1) * ReviewRowGap;
            foreach (string paragraph in paragraphs) height += ReviewRowHeight(paragraph);
            return height;
        }

        void DrawReviewRows(string page)
        {
            m_Detail.gameObject.SetActive(false);
            m_ReviewRows.SetActive(true);
            var paragraphs = ReviewParagraphs(page);
            float top = m_Detail.transform.localPosition.y + m_Detail.rectTransform.sizeDelta.y / 2;
            for (int i = 0; i < m_Rows.Length; i++)
            {
                var row = m_Rows[i];
                bool visible = i < paragraphs.Length;
                row.Background.SetActive(visible);
                row.Number.gameObject.SetActive(visible);
                row.Body.gameObject.SetActive(visible);
                if (!visible) continue;
                SplitCommand(paragraphs[i], out string number, out string body);
                float height = ReviewRowHeight(paragraphs[i]);
                float center = top - height / 2;
                row.Background.transform.localPosition = new Vector3(0, center, -0.03f);
                var mesh = row.Background.GetComponent<MeshFilter>().sharedMesh;
                row.Background.transform.localScale = new Vector3(ReviewRowWidth / mesh.bounds.size.x, height / mesh.bounds.size.y, 1);
                row.Number.transform.localPosition = new Vector3(-1.645f, center, -0.045f);
                row.Body.transform.localPosition = new Vector3(0.17f, center, -0.045f);
                row.Number.rectTransform.sizeDelta = new Vector2(0.3f, height - 2 * ReviewRowPadding);
                row.Body.rectTransform.sizeDelta = new Vector2(ReviewBodyWidth, height - 2 * ReviewRowPadding);
                row.Number.text = number;
                row.Body.text = body;
                top -= height + ReviewRowGap;
            }
        }

        static int FindPageBoundary(string value, int length)
        {
            int boundary = value.LastIndexOf('\n', length - 1, length);
            if (boundary < 0) boundary = value.LastIndexOf(' ', length - 1, length);
            if (boundary >= 0) return boundary + 1;
            return length > 1 && char.IsHighSurrogate(value[length - 1]) ? length - 1 : length;
        }

        internal static string[] ReviewPages(string summary)
        {
            var pages = new List<string>();
            int offset = 0;
            while (offset < summary.Length)
            {
                int length = Math.Min(ReviewPageCharacterLimit, summary.Length - offset);
                if (offset + length < summary.Length) length = FindPageBoundary(summary.Substring(offset, length), length);
                pages.Add(summary.Substring(offset, length));
                offset += length;
            }
            return pages.Count == 0 ? new[] { "" } : pages.ToArray();
        }

        void ChangeReviewPage(int delta)
        {
            m_ReviewPage = Math.Max(0, Math.Min(m_ReviewPages.Length - 1, m_ReviewPage + delta));
            m_LastViewedPage = Math.Max(m_LastViewedPage, m_ReviewPage);
            MarkDirty();
        }

        void RetryAssistance(CHRISAssistanceClient client)
        {
            if (client.CancelWanted) client.Cancel();
            else if (client.PendingRequest != null) client.RetrySubmission();
            else if ((string)client.Task?["status"] == "paused" || (string)client.Task?["status"] == "unverified") client.Resume();
            else client.Refresh();
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
            if (m_Model != null)
            {
                m_Model.Changed -= MarkDirty;
                m_Model.Closed(this);
            }
        }
    }
}
