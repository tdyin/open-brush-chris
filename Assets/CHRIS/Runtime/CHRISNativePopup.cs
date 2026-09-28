// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

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
        CHRISNativeButton m_ControlsTab;
        readonly CHRISNativeButton[] m_Choices = new CHRISNativeButton[4];
        readonly ReviewRow[] m_Rows = new ReviewRow[5];
        GameObject m_ReviewRows;
        bool m_Dirty, m_LastRightHand;
        string m_PagedSummary;
        string[] m_ReviewPages;
        int m_ReviewPage, m_LastViewedPage;
        bool m_ShowControls, m_CandidateSubmitted;
        string m_CandidateJson, m_MappingNotice;
        string m_CandidateRequestId;
        JObject m_CandidateBaseStatus;
        string[] m_MappingReviewPages;
        int m_MappingPage, m_MappingLastViewedPage;
        string m_BrowseSource;
        string[] m_BrowsePages;
        int m_BrowsePage;
        // Controls redraw only on a real change (mapping revision, saved-file reload, control state),
        // and the saved file is read only when Controls opens or a reload happens.
        long m_SeenMappingRevision = -1;
        int m_SeenSavedRevision = -1, m_SeenControlState = -1, m_ControlsOpenSerial, m_SavedReadKey = -1;
        string m_SavedCache, m_SavedCacheError;
        bool m_SavedCacheExists;
        public bool ControlsVisible => m_ShowControls;
        public bool BindingCaptureActive => m_CaptureType != null;
        CHRISMappingEditor m_Editor;
        bool m_EditingBindings, m_CaptureWaitRelease;
        int m_EditIndex, m_CapturePartIndex;
        string m_CaptureType;
        string[] m_CaptureParts;
        readonly Dictionary<string, string> m_CaptureValues = new Dictionary<string, string>();

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
            var stop = m_Resources.Button(transform, "Local Stop", new Vector3(1.45f, 2.18f, -0.06f), new Vector2(1.1f, 0.42f), "Stop", LocalStop, true);
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
            m_Speech = m_Resources.Button(transform, "AI confirmation voice",
                new Vector3(-1.36f, -2.2f, -0.06f), new Vector2(1.25f, 0.43f),
                "Speech: On", () => m_Model?.Speech.Toggle());
            m_ControlsTab = m_Resources.Button(transform, "Controls tab",
                new Vector3(0, -2.2f, -0.06f), new Vector2(1.25f, 0.43f),
                "Controls", () => ShowControls(true));
            m_Resources.Button(transform, "Close CHRIS", new Vector3(1.36f, -2.2f, -0.06f),
                new Vector2(1.25f, 0.43f), "Close", () => RequestClose(true));
        }

        void MarkDirty() => m_Dirty = true;

        public void ShowControls(bool show)
        {
            if (!show) CancelBindingCapture("Capture cancelled when leaving Controls.");
            else m_ControlsOpenSerial++;
            m_ShowControls = show;
            m_Dirty = true;
        }

        // A capture must never outlive the popup: its flag blocks drawing elsewhere, and a disabled
        // popup no longer samples input to finish it.
        void OnDisable() => CancelBindingCapture("Capture cancelled when CHRIS closed.");

        static int ControlStateKey()
        {
            var hand = CHRISBimanualHost.Input;
            return (CHRISBimanualHost.Focused ? 1 : 0) | (hand.HandBackPending ? 2 : 0) | (hand.RecenterPending ? 4 : 0) |
                (hand.Active ? 8 : 0) | (CHRISBimanualHost.OwnsBrushPose || CHRISBimanualHost.OwnsWandPose ? 16 : 0) |
                ((int)hand.Mode << 5) | (hand.Selected == "wand" ? 256 : 0) |
                (hand.Brush.GripHeld ? 512 : 0) | (hand.Wand.GripHeld ? 1024 : 0);
        }

        // Reads the saved file only when Controls was (re)opened or a reload changed it.
        void RefreshSavedMappingCache()
        {
            int key = CHRISInputMappingHost.SavedFileRevision * 65536 + m_ControlsOpenSerial;
            if (key == m_SavedReadKey) return;
            m_SavedReadKey = key;
            m_SavedCache = m_SavedCacheError = null;
            m_SavedCacheExists = SavedMappingAvailable();
            if (!m_SavedCacheExists) return;
            try { m_SavedCache = new UTF8Encoding(false, true).GetString(CHRISInputMapping.ReadBounded(
                CHRISInputMappingStore.PathUnder(Application.persistentDataPath))); }
            catch (Exception error) when (error is System.IO.IOException || error is DecoderFallbackException ||
                error is CHRISMappingException || error is UnauthorizedAccessException)
            { m_SavedCacheError = "Saved mapping could not be read."; }
        }

        // Retaking control after focus loss or hand-back needs a fresh mode key; name the real keys.
        internal static string RetakeHint(CHRISInputMapping mapping, bool focused, bool handBackWaiting)
        {
            if (mapping == null || (focused && !handBackWaiting)) return "";
            string position = KeyLabel(mapping.Find(CHRISMappedAction.ModePosition));
            string rotation = KeyLabel(mapping.Find(CHRISMappedAction.ModeRotation));
            if (position == null && rotation == null) return "";
            string keys = position != null && rotation != null ? $"{position} (position) or {rotation} (rotation)" :
                position != null ? position + " (position)" : rotation + " (rotation)";
            return (focused ? "Press " : "Return to Open Brush, then press ") + keys + " to take control";
        }

        static string KeyLabel(CHRISInputMappingEntry entry)
        {
            if (entry == null) return null;
            if (entry.Source == CHRISMappingSource.MouseButton) return "mouse " + entry.Button;
            if (entry.Source != CHRISMappingSource.Key || string.IsNullOrEmpty(entry.Key)) return null;
            return char.ToUpperInvariant(entry.Key[0]) + entry.Key.Substring(1);
        }

        static string WithInvalidSavedNote(string review) =>
            CHRISInputMappingHost.SavedMappingInvalid ? "Replaces invalid saved file\n" + review : review;

        void LocalStop()
        {
            bool stopMapping = CHRISInputMappingHost.MappingInUse;
            m_Model?.StopLocal();
            if (stopMapping) CHRISInputMappingHost.StopMapping();
            if (!m_ShowControls) return;
            CancelBindingCapture("Control stopped.");
            m_CandidateJson = null;
            m_CandidateRequestId = null;
            m_CandidateSubmitted = false;
            m_EditingBindings = false;
            m_MappingNotice = "Mapped control stopped. Review saved bindings to reactivate.";
            MarkDirty();
        }

        public void CancelBindingCapture(string reason)
        {
            if (!BindingCaptureActive) return;
            m_CaptureType = null;
            m_CaptureParts = null;
            m_CaptureValues.Clear();
            m_MappingNotice = reason;
            MarkDirty();
        }

        protected override void BaseUpdate()
        {
            base.BaseUpdate();
            if (BindingCaptureActive) SampleBindingCapture();
            if (m_ShowControls)
            {
                long revision = CHRISInputMappingHost.MappingRevision;
                int saved = CHRISInputMappingHost.SavedFileRevision, control = ControlStateKey();
                if (revision != m_SeenMappingRevision || saved != m_SeenSavedRevision || control != m_SeenControlState)
                {
                    m_SeenMappingRevision = revision;
                    m_SeenSavedRevision = saved;
                    m_SeenControlState = control;
                    m_Dirty = true;
                }
            }
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
            m_Detail.fontSize = 1.45f;
            m_Subtitle.fontSize = 1.0f;
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
            m_Speech.Click = () => m_Model?.Speech.Toggle();
            m_Speech.SetButtonAvailable(true);
            m_Speech.SetPrimary(m_Model.Speech.SpeechEnabled);
            m_ControlsTab.SetPrimary(m_ShowControls);
        }

        void Draw()
        {
            if (m_Model == null) return;
            m_LastRightHand = m_Model.NonDrawingHandIsRight;
            ResetActions();
            m_BlockUndoRedo = !m_ShowControls || m_EditingBindings ||
                (m_CandidateJson != null && !m_CandidateSubmitted);
            if (m_ShowControls)
            {
                DrawControls();
                return;
            }
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
                DrawVoiceFailure(voice);
                return;
            }
            string status = (string)client.Task?["status"];
            if (client.Error != null || client.CancelWanted || status == "paused" || status == "unverified" ||
                (client.Task == null && (client.TaskId != null || client.PendingRequest != null) && !client.Busy))
            {
                DrawRecovery(client);
                return;
            }
            if (m_Model.HasReplacement || (client.Busy && client.Task == null) || status == "proposing" || status == "pending")
            {
                DrawPreparing();
                return;
            }
            if (status == "executing")
            {
                DrawExecuting(client);
                return;
            }
            if (client.Task != null && CHRISAssistanceClient.Terminal(client.Task))
            {
                DrawCompleted(client, status);
                return;
            }
            DrawReady();
        }

        void ConfigureMappingChoice(ChoiceSlot slot, string label, Action click, bool available = true)
        {
            var button = m_Choices[(int)slot];
            button.gameObject.SetActive(true);
            button.SetContent(label, null);
            button.Click = click;
            button.SetButtonAvailable(available);
            button.SetPrimary(slot == ChoiceSlot.ConfirmOrRecover && available);
        }

        void DrawControls()
        {
            m_Subtitle.fontSize = 0.78f;
            m_ControlsTab.SetContent("Controls", "settings");
            m_ControlsTab.Click = () => ShowControls(true);
            m_Speech.SetContent("Voice", "mic");
            m_Speech.Click = () => ShowControls(false);
            m_Speech.SetPrimary(false);
            var status = CHRISInputMappingHost.MappingStatus();
            if (m_EditingBindings)
            {
                DrawBindingEditor();
                return;
            }
            string pendingDigest = (string)status["pending_digest"];
            string outcome = m_CandidateSubmitted ? CandidateOutcome(status, m_CandidateJson, m_CandidateRequestId) : null;
            if (outcome != null)
            {
                m_CandidateJson = null;
                m_CandidateRequestId = null;
                m_CandidateSubmitted = false;
                m_MappingNotice = outcome;
            }
            if (m_CandidateJson == null)
            {
                var hand = CHRISBimanualHost.Input;
                string controlState = !CHRISBimanualHost.Focused ? "Focus lost; controls released" :
                    hand.HandBackPending ? CHRISBimanualHost.OwnsBrushPose || CHRISBimanualHost.OwnsWandPose ?
                        "Hand-back waiting for grab or stroke" : "Hands back to controllers" :
                    hand.RecenterPending ? "Recenter queued until neutral" :
                    hand.Active ? hand.Mode + " / " + hand.Selected : "controllers";
                SetStateHeader("Keyboard + mouse", controlState, "settings");
                string browse = (string)status["active_mapping_json"];
                bool saved = false;
                if (browse == null)
                {
                    RefreshSavedMappingCache();
                    saved = m_SavedCacheExists;
                    browse = m_SavedCache;
                    if (m_SavedCacheError != null) m_MappingNotice = m_SavedCacheError;
                }
                string browseKey = browse + "\u0000" + m_MappingNotice;
                if (browseKey != m_BrowseSource || m_BrowsePages == null)
                {
                    m_BrowseSource = browseKey;
                    m_BrowsePage = 0;
                    try
                    {
                        var mapping = browse == null ? null : CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(browse));
                        string readable = mapping == null ? "No active or saved binding profile.\nReview the bundled preset to begin." :
                            (saved ? "Saved bindings\n" : "Current bindings\n") +
                            string.Join("\n", mapping.Mappings.Select(CHRISMappingEditor.Describe));
                        m_BrowsePages = FitPlainPages((m_MappingNotice == null ? "" : m_MappingNotice + "\n\n") + readable, 0.95f);
                    }
                    catch (CHRISMappingException)
                    { m_BrowsePages = new[] { "Saved binding profile is invalid. Review the bundled preset." }; }
                }
                m_Detail.fontSize = 0.95f;
                m_Detail.text = m_BrowsePages[m_BrowsePage];
                m_PageNumber.text = (m_BrowsePage + 1) + " / " + m_BrowsePages.Length;
                ConfigureMappingChoice(ChoiceSlot.ConfirmOrRecover, "Review preset", ReviewDefault, pendingDigest == null);
                if (pendingDigest != null)
                    ConfigureMappingChoice(ChoiceSlot.RetryRecording, "Discard pending", DiscardMappingReview);
                else
                {
                    ConfigureMappingChoice(ChoiceSlot.RetryRecording, "Edit bindings", BeginBindingEdit);
                }
                ConfigureMappingChoice(ChoiceSlot.PreviousPage, "Previous", () => ChangeBrowsePage(-1), m_BrowsePage > 0);
                ConfigureMappingChoice(ChoiceSlot.NextPage, "Next", () => ChangeBrowsePage(1), m_BrowsePage + 1 < m_BrowsePages.Length);
                if (saved)
                {
                    m_ControlsTab.SetContent("Review saved", "Path_List");
                    m_ControlsTab.Click = ReviewSaved;
                }
                else
                {
                    m_ControlsTab.SetContent("Controls", "settings");
                    m_ControlsTab.Click = () => ShowControls(true);
                }
                bool brushRight = InputManager.m_Instance != null && !InputManager.m_Instance.WandOnRight;
                m_Hint.text = hand.Active && !CHRISBimanualHost.OwnsDesktopControl &&
                    !hand.HandBackPending && CHRISBimanualHost.Focused ?
                    "Controllers active. F1 UI, F2 position, F3 rotation." :
                    RetakeHint(CHRISInputMappingHost.Remap.Active, CHRISBimanualHost.Focused,
                        hand.HandBackPending && !CHRISBimanualHost.OwnsBrushPose && !CHRISBimanualHost.OwnsWandPose);
                string compactState = !CHRISBimanualHost.Focused ? "Focus lost" :
                    hand.HandBackPending ? "Hands back" : hand.RecenterPending ? "Recenter queued" :
                    hand.Active && !CHRISBimanualHost.OwnsDesktopControl ? "physical controllers" :
                    hand.Active ? hand.Mode + "/" + hand.Selected : "controllers";
                m_Subtitle.text = (string)status["state"] + " | " + compactState +
                    " | B:" + (brushRight ? "R" : "L") + " W:" + (brushRight ? "L" : "R") +
                    " | grip:" + (hand.Brush.GripHeld ? "B" : "-") + (hand.Wand.GripHeld ? "W" : "-");
                return;
            }

            bool sameStatus = SameMappingStatus(m_CandidateBaseStatus, status);
            SetStateHeader(m_CandidateSubmitted ? "Waiting for neutral input" : "Review control preset",
                m_MappingNotice ?? (sameStatus || m_CandidateSubmitted ? "Before and after bindings" :
                    "Mapping changed. Discard and review again."),
                "Path_List");
            m_Detail.fontSize = 0.8f;
            m_Detail.text = m_MappingReviewPages[m_MappingPage];
            m_PageNumber.text = (m_MappingPage + 1) + " / " + m_MappingReviewPages.Length;
            bool readAll = m_MappingLastViewedPage == m_MappingReviewPages.Length - 1;
            ConfigureMappingChoice(ChoiceSlot.ConfirmOrRecover, "Confirm",
                ConfirmMappingReview, !m_CandidateSubmitted && sameStatus && readAll && pendingDigest == null);
            ConfigureMappingChoice(ChoiceSlot.RetryRecording, "Discard", DiscardMappingReview);
            ConfigureMappingChoice(ChoiceSlot.PreviousPage, "Previous", () => ChangeMappingPage(-1), m_MappingPage > 0);
            ConfigureMappingChoice(ChoiceSlot.NextPage, "Next", () => ChangeMappingPage(1),
                m_MappingPage + 1 < m_MappingReviewPages.Length);
            m_Hint.text = "";
        }

        // After Confirm: null while the candidate is still pending; otherwise the notice for how it ended
        // (activated, or the receipt for this request: rejected/cancelled; "superseded" if another request
        // took over).
        internal static string CandidateOutcome(JObject status, string candidateJson, string candidateRequestId)
        {
            string candidateDigest = CHRISCommandGateway.Hash(candidateJson);
            string pendingDigest = (string)status["pending_digest"];
            if (pendingDigest != null && pendingDigest == candidateDigest) return null;
            if (pendingDigest == null && (string)status["active_digest"] == candidateDigest) return "Mapping active.";
            string receipt = (string)status["last_request_id"] == candidateRequestId ?
                (string)status["last_request_result"] : "superseded";
            return "Mapping " + (receipt ?? "cancelled") + ". Review again before submitting.";
        }

        internal static JObject BuildApplyRequest(JObject baseStatus, string candidateJson, string requestId) => new JObject {
            ["request_id"] = requestId,
            ["expected_session_id"] = baseStatus["session_id"].DeepClone(),
            ["expected_revision"] = baseStatus["revision"].DeepClone(),
            ["expected_active_digest"] = baseStatus["active_digest"].DeepClone(),
            ["proposed_digest"] = CHRISCommandGateway.Hash(candidateJson),
            ["mapping_json"] = candidateJson
        };

        internal static bool SameMappingStatus(JObject before, JObject now) => before != null &&
            JToken.DeepEquals(before["session_id"], now["session_id"]) &&
            JToken.DeepEquals(before["revision"], now["revision"]) &&
            JToken.DeepEquals(before["active_digest"], now["active_digest"]);

        void ChangeBrowsePage(int delta)
        {
            m_BrowsePage = Math.Max(0, Math.Min(m_BrowsePages.Length - 1, m_BrowsePage + delta));
            MarkDirty();
        }

        void ReviewDefault()
        {
            var asset = Resources.Load<TextAsset>("CHRIS/TwoHandDefault");
            if (asset == null) { m_MappingNotice = "Bundled control preset is unavailable."; MarkDirty(); return; }
            try
            {
                string candidate = new UTF8Encoding(false, true).GetString(asset.bytes);
                ReviewCandidate(candidate);
            }
            catch (Exception error) when (error is DecoderFallbackException || error is CHRISMappingException)
            { m_MappingNotice = "Bundled control preset failed validation."; }
            MarkDirty();
        }

        void ReviewCandidate(string candidate)
        {
            m_CandidateBaseStatus = CHRISInputMappingHost.MappingStatus();
            string before = (string)m_CandidateBaseStatus["active_mapping_json"];
            var parsed = CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(candidate));
            m_Editor = parsed.SchemaVersion == CHRISInputMapping.BimanualVersion ?
                new CHRISMappingEditor(before, candidate) : null;
            m_CandidateJson = candidate;
            m_CandidateSubmitted = false;
            m_EditingBindings = false;
            m_MappingReviewPages = FitPlainPages(WithInvalidSavedNote(CHRISMappingEditor.ReviewText(before, candidate)), 0.8f);
            m_MappingPage = m_MappingLastViewedPage = 0;
            m_MappingNotice = null;
        }

        bool SavedMappingAvailable() => System.IO.File.Exists(CHRISInputMappingStore.PathUnder(Application.persistentDataPath));

        void ReviewSaved()
        {
            try
            {
                var bytes = CHRISInputMapping.ReadBounded(CHRISInputMappingStore.PathUnder(Application.persistentDataPath));
                ReviewCandidate(new UTF8Encoding(false, true).GetString(bytes));
            }
            catch (Exception error) when (error is System.IO.IOException || error is UnauthorizedAccessException ||
                error is DecoderFallbackException || error is CHRISMappingException)
            { m_MappingNotice = "Saved mapping cannot be reviewed: " + error.Message; }
            MarkDirty();
        }

        void BeginBindingEdit()
        {
            try
            {
                var status = CHRISInputMappingHost.MappingStatus();
                string before = (string)status["active_mapping_json"];
                string working = before;
                if (working == null || CHRISInputMapping.Parse(Encoding.UTF8.GetBytes(working)).SchemaVersion !=
                    CHRISInputMapping.BimanualVersion)
                {
                    working = null;
                    try
                    {
                        var saved = CHRISInputMapping.ReadBounded(CHRISInputMappingStore.PathUnder(Application.persistentDataPath));
                        if (CHRISInputMapping.Parse(saved).SchemaVersion == CHRISInputMapping.BimanualVersion)
                            working = new UTF8Encoding(false, true).GetString(saved);
                    }
                    catch (System.IO.IOException) { }
                    catch (CHRISMappingException) { }
                }
                if (working == null)
                    working = new UTF8Encoding(false, true).GetString(Resources.Load<TextAsset>("CHRIS/TwoHandDefault").bytes);
                m_CandidateBaseStatus = status;
                m_Editor = new CHRISMappingEditor(before, working);
                m_EditingBindings = true;
                m_EditIndex = 0;
                m_MappingNotice = "Select a binding, then capture its input. F1 cancels capture.";
            }
            catch (Exception error) when (error is CHRISMappingException || error is DecoderFallbackException ||
                error is NullReferenceException || error is UnauthorizedAccessException)
            { m_MappingNotice = "Cannot open bindings: " + error.Message; }
            MarkDirty();
        }

        void DrawBindingEditor()
        {
            m_ControlsTab.SetContent("Review", "Path_List");
            m_ControlsTab.Click = ReviewEditedBindings;
            var entry = m_Editor.Entries[m_EditIndex];
            var hand = CHRISBimanualHost.Input;
            SetStateHeader(BindingCaptureActive ? "Capture input" : "Edit bindings",
                (m_EditIndex + 1) + " / " + m_Editor.Entries.Count + "  |  " +
                (hand.Active ? hand.Mode + "  " + hand.Selected : "No active control"), "settings");
            m_Detail.text = CHRISMappingEditor.Describe(entry) +
                (BindingCaptureActive ? "\n\nPress " + m_CaptureParts[m_CapturePartIndex] +
                    " for " + m_CaptureType + ". F1 cancels." :
                    "\n\nSource type: " + CHRISMappingEditor.SourceType(entry.Source) +
                    "\n" + PhysicalHandLabel(entry)) +
                (m_MappingNotice == null ? "" : "\n" + m_MappingNotice);
            m_Detail.fontSize = 1.05f;
            if (!BindingCaptureActive)
            {
                bool fixedInput = entry.Action == CHRISMappedAction.ModeUI ||
                    entry.Action == CHRISMappedAction.BrushSize;
                ConfigureMappingChoice(ChoiceSlot.ConfirmOrRecover, "Capture input", BeginBindingCapture, !fixedInput);
                ConfigureMappingChoice(ChoiceSlot.RetryRecording, "Source type", CycleBindingSource,
                    CHRISMappingEditor.SourceTypes(entry.Action).Length > 1);
                ConfigureMappingChoice(ChoiceSlot.PreviousPage, "Previous", () => ChangeBinding(-1), m_EditIndex > 0);
                ConfigureMappingChoice(ChoiceSlot.NextPage, "Next", () => ChangeBinding(1),
                    m_EditIndex + 1 < m_Editor.Entries.Count);
            }
            m_Hint.text = "";
        }

        string PhysicalHandLabel(CHRISInputMappingEntry entry)
        {
            string target = entry.Target;
            if (target == null) return entry.Action == CHRISMappedAction.ModeUI ?
                "F1 recovery is fixed." : "Global control.";
            if (target == "selected")
            {
                var brush = m_Editor.Entries.FirstOrDefault(e => e.Action == CHRISMappedAction.SelectHand && e.Target == "brush");
                var wand = m_Editor.Entries.FirstOrDefault(e => e.Action == CHRISMappedAction.SelectHand && e.Target == "wand");
                return "Select: Brush " + (brush?.Key ?? "?") + ", Wand " + (wand?.Key ?? "?") + ".";
            }
            bool brushRight = InputManager.m_Instance != null && !InputManager.m_Instance.WandOnRight;
            return target == "brush" ? "Brush is on the " + (brushRight ? "right" : "left") + "." :
                "Wand is on the " + (brushRight ? "left" : "right") + ".";
        }

        void ChangeBinding(int delta)
        {
            m_EditIndex = Math.Max(0, Math.Min(m_Editor.Entries.Count - 1, m_EditIndex + delta));
            m_MappingNotice = null;
            MarkDirty();
        }

        void ReviewEditedBindings()
        {
            if (BindingCaptureActive) return;
            m_CandidateJson = m_Editor.WorkingJson;
            m_CandidateSubmitted = false;
            m_MappingReviewPages = FitPlainPages(WithInvalidSavedNote(m_Editor.ReviewText()), 0.8f);
            m_MappingPage = m_MappingLastViewedPage = 0;
            m_EditingBindings = false;
            m_MappingNotice = null;
            m_ControlsTab.SetContent("Controls", "settings");
            m_ControlsTab.Click = () => ShowControls(true);
            MarkDirty();
        }

        void BeginBindingCapture() => StartCapture(CHRISMappingEditor.SourceType(m_Editor.Entries[m_EditIndex].Source));

        void CycleBindingSource()
        {
            var entry = m_Editor.Entries[m_EditIndex];
            var types = CHRISMappingEditor.SourceTypes(entry.Action);
            int index = Array.IndexOf(types, CHRISMappingEditor.SourceType(entry.Source));
            StartCapture(types[(index + 1) % types.Length]);
        }

        void StartCapture(string type)
        {
            if (type == "mouse_delta" || type == "mouse_wheel")
            {
                ApplyEditedSource(new JObject { ["type"] = type });
                return;
            }
            m_CaptureType = type;
            m_CaptureParts = type == "key_vector2" ? new[] { "up", "down", "left", "right" } :
                type == "key_axis1" ? new[] { "negative", "positive" } :
                type == "mouse_button" ? new[] { "button" } : new[] { "key" };
            m_CapturePartIndex = 0;
            m_CaptureValues.Clear();
            m_CaptureWaitRelease = true;
            m_MappingNotice = "Release the selection button, then press " + m_CaptureParts[0] + ".";
            MarkDirty();
        }

        void SampleBindingCapture()
        {
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (keyboard != null && keyboard.f1Key.wasPressedThisFrame)
            {
                m_CaptureType = null;
                m_MappingNotice = "Capture cancelled.";
                MarkDirty();
                return;
            }
            if (m_CaptureWaitRelease)
            {
                if ((mouse != null && mouse.leftButton.isPressed) ||
                    (keyboard != null && keyboard.enterKey.isPressed)) return;
                m_CaptureWaitRelease = false;
                return;
            }
            string value = null;
            if (m_CaptureType == "mouse_button" && mouse != null)
            {
                if (mouse.leftButton.wasPressedThisFrame) value = "left";
                else if (mouse.rightButton.wasPressedThisFrame) value = "right";
                else if (mouse.middleButton.wasPressedThisFrame) value = "middle";
            }
            else if (keyboard != null)
            {
                foreach (var key in keyboard.allKeys)
                    if (key.wasPressedThisFrame)
                    {
                        value = key.name; // Input System key names already match the schema, including 0-9.
                        break;
                    }
            }
            if (value == null) return;
            m_CaptureValues[m_CaptureParts[m_CapturePartIndex++]] = value;
            if (m_CapturePartIndex < m_CaptureParts.Length)
            {
                m_MappingNotice = "Press " + m_CaptureParts[m_CapturePartIndex] + ".";
                MarkDirty();
                return;
            }
            var source = new JObject { ["type"] = m_CaptureType };
            foreach (var part in m_CaptureParts) source[part] = m_CaptureValues[part];
            m_CaptureType = null;
            ApplyEditedSource(source);
        }

        void ApplyEditedSource(JObject source)
        {
            m_MappingNotice = m_Editor.TryChangeSource(m_EditIndex, source, out string error) ?
                "Binding changed locally. Review before confirming." : "Input rejected: " + error;
            MarkDirty();
        }

        void ChangeMappingPage(int delta)
        {
            m_MappingPage = Math.Max(0, Math.Min(m_MappingReviewPages.Length - 1, m_MappingPage + delta));
            m_MappingLastViewedPage = Math.Max(m_MappingLastViewedPage, m_MappingPage);
            MarkDirty();
        }

        void ConfirmMappingReview()
        {
            var status = CHRISInputMappingHost.MappingStatus();
            if (m_CandidateJson == null || m_CandidateSubmitted ||
                m_MappingLastViewedPage != m_MappingReviewPages.Length - 1 ||
                !SameMappingStatus(m_CandidateBaseStatus, status))
            { m_MappingNotice = "Mapping changed. Discard and review again."; MarkDirty(); return; }
            m_CandidateRequestId = "headset-" + Guid.NewGuid().ToString("N");
            var reply = CHRISInputMappingHost.ActivateMapping(BuildApplyRequest(m_CandidateBaseStatus, m_CandidateJson, m_CandidateRequestId));
            m_CandidateSubmitted = (string)reply["result"] == "pending_neutral";
            m_MappingNotice = m_CandidateSubmitted ? "Pending neutral controls; no buttons need to stay held." :
                "Request rejected: " + (string)reply["reason"];
            MarkDirty();
        }

        void DiscardMappingReview()
        {
            if ((string)CHRISInputMappingHost.MappingStatus()["pending_digest"] != null)
                CHRISInputMappingHost.DiscardPendingMapping();
            m_CandidateJson = null;
            m_CandidateRequestId = null;
            m_CandidateBaseStatus = null;
            m_MappingReviewPages = null;
            m_CandidateSubmitted = false;
            m_MappingNotice = "Review discarded.";
            MarkDirty();
        }

        void DrawVoiceFailure(CHRISVoiceInput voice)
        {
            SetStateHeader("Microphone or speech unavailable", "Check the input device and connection.", "warning");
            m_Detail.text = voice.Status;
            ShowRecordingAction("Retry");
            ShowDevice();
        }

        void DrawRecovery(CHRISAssistanceClient client)
        {
            SetStateHeader("Outcome needs checking", "Last verified status", "warning");
            m_Detail.text = m_Model.AssistanceStatus();
            ConfigureChoice(ChoiceSlot.ConfirmOrRecover, "Retry", () => RetryAssistance(client), !client.Busy, "refresh", true);
            m_Hint.text = client.CancelWanted ? "Checks cancellation before more work." :
                client.PendingRequest != null ? "Recovers the same request." : "Checks status before continuing.";
        }

        void DrawPreparing()
        {
            SetStateHeader("Preparing commands", "Transcript complete", "more_solid");
            m_Detail.text = m_Model.Prompt;
            m_Hint.text = "Checking the request and sketch...\nExact commands appear next.";
            ShowCorrection();
        }

        void DrawExecuting(CHRISAssistanceClient client)
        {
            SetStateHeader("Executing", VerifiedProgress(client.Task), "settings");
            m_Detail.text = (string)client.Task["summary"] ?? m_Model.AssistanceStatus();
            m_Hint.text = "You can stop and take over.";
        }

        void DrawCompleted(CHRISAssistanceClient client, string status)
        {
            SetStateHeader(status == "succeeded" ? "Completed" : "Stopped", VerifiedProgress(client.Task), status == "succeeded" ? "ticktransparent" : "warning");
            m_Detail.text = m_Model.AssistanceStatus();
            ShowRecordingAction("Record");
        }

        void DrawReady()
        {
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

        // Controls renders plain text at a smaller size than the voice review's ordered rows.
        // Measure the actual text box so a short binding change does not need dozens of pages.
        string[] FitPlainPages(string content, float fontSize)
        {
            float previousSize = m_Detail.fontSize;
            m_Detail.fontSize = fontSize;
            try
            {
                var pages = new List<string>();
                int offset = 0;
                float width = m_Detail.rectTransform.sizeDelta.x;
                float height = m_Detail.rectTransform.sizeDelta.y * 0.92f;
                while (offset < content.Length)
                {
                    int low = 1, high = Math.Min(2000, content.Length - offset);
                    while (low < high)
                    {
                        int length = (low + high + 1) / 2;
                        if (m_Detail.GetPreferredValues(content.Substring(offset, length), width, float.PositiveInfinity).y <= height)
                            low = length;
                        else high = length - 1;
                    }
                    int count = low;
                    if (offset + count < content.Length && count > 20)
                    {
                        int newline = content.LastIndexOf('\n', offset + count - 1, count);
                        if (newline >= offset + count / 2) count = newline - offset + 1;
                    }
                    pages.Add(content.Substring(offset, count));
                    offset += count;
                }
                return pages.Count == 0 ? new[] { "" } : pages.ToArray();
            }
            finally { m_Detail.fontSize = previousSize; }
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
            CancelBindingCapture("Panel closed.");
            var host = m_ParentPanel as CHRISFloatingPanel;
            base.DestroyPopUpWindow();
            if (host != null) host.gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            CancelBindingCapture("Panel closed.");
            GetComponent<CHRISMaterialOwner>()?.Release();
            if (m_Model != null)
            {
                m_Model.Changed -= MarkDirty;
                m_Model.Closed(this);
            }
        }
    }
}
