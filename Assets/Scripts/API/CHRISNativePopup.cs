// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
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
        readonly CHRISNativeButton[] m_Choices = new CHRISNativeButton[6];
        bool m_Dirty;

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
            m_Resources.Button(transform, "Direct mode", new Vector3(-0.9f, 1.34f, -0.06f), new Vector2(1.7f, 0.32f), "Direct controls",
                () => m_Model?.SwitchMode("Direct"));
            m_Resources.Button(transform, "Assistance mode", new Vector3(0.9f, 1.34f, -0.06f), new Vector2(1.7f, 0.32f), "Assistance",
                () => m_Model?.SwitchMode("Assistance"));
            m_Status = m_Resources.Text(transform, "Status", new Vector3(0, 0.87f, -0.04f), new Vector2(3.5f, 0.55f),
                "Waiting for native gateway", 1.35f);
            m_Detail = m_Resources.Text(transform, "Review", new Vector3(0, 0.19f, -0.04f), new Vector2(3.5f, 0.72f), "", 1.4f);
            for (int i = 0; i < 6; i++)
                m_Choices[i] = m_Resources.Button(transform, "Choice " + i,
                    new Vector3(i % 2 == 0 ? -0.9f : 0.9f, -0.46f - i / 2 * 0.4f, -0.06f), new Vector2(1.7f, 0.32f), "", null);
            m_Resources.Button(transform, "Cancel review / Back", new Vector3(-0.9f, -1.73f, -0.06f), new Vector2(1.7f, 0.32f),
                "Cancel / Back", () => m_Model?.Back());
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
            if (m_Model == null)
                return;
            foreach (var button in m_Choices)
            {
                button.gameObject.SetActive(false);
                button.Click = null;
            }

            m_Status.text = m_Model.Mode + "\n" + m_Model.Notice;
            m_Detail.text = "";
            if (m_Model.Mode == "Choose controls")
            {
                m_Detail.text = "Use the mode buttons above.\nHold the title bar with the trigger to move this window. Release to leave it in place.";
                return;
            }

            if (m_Model.Mode == "Direct")
            {
                DrawDirect();
                return;
            }

            DrawAssistance();
        }

        void DrawDirect()
        {
            var context = m_Model.Gateway.Capture();
            if (m_Model.ReviewedAction != null)
            {
                m_Detail.text = CHRISPanel.Summary(m_Model.ReviewedAction, context);
                ConfigureChoice(0, "Confirm change", m_Model.ConfirmDirect);
                ConfigureChoice(1, "Cancel review", m_Model.Back);
                return;
            }

            if (m_Model.Category == "")
            {
                m_Detail.text = (bool)context["ready"] ? "Choose a control. Changes require a review and confirmation." : "Native host unavailable. Wait for the sketch to finish loading.";
                var categories = new[]
                {
                    "Brush",
                    "Color",
                    "Size",
                    "Panels",
                    "Move",
                    "Turn"
                };
                for (int i = 0; i < categories.Length; i++)
                {
                    string category = categories[i];
                    ConfigureChoice(i, category, () => m_Model.SetCategory(category), (bool)context["ready"]);
                }

                return;
            }

            var choices = CHRISPanel.Choices(m_Model.Category, context);
            const int choicesPerPage = CHRISPanel.DirectChoicesPerPage;
            int pages = Math.Max(1, (choices.Count + choicesPerPage - 1) / choicesPerPage);
            int page = Math.Min(m_Model.Page, pages - 1);
            m_Detail.text = m_Model.Category + " — page " + (page + 1) + " / " + pages + "\nSelect an option to review.";
            for (int i = 0; i < choicesPerPage && page * choicesPerPage + i < choices.Count; i++)
            {
                var option = choices[page * choicesPerPage + i];
                ConfigureChoice(i, option.Key, () => m_Model.Review(option.Value));
            }

            ConfigureChoice(4, "Previous page", () => m_Model.ChangePage(-1), page > 0);
            ConfigureChoice(5, "Next page", () => m_Model.ChangePage(1), page + 1 < pages);
        }

        void DrawAssistance()
        {
            var client = m_Model.Assistance;
            m_Status.text = "Assistance\n" + (m_Model.WaitingForRelease ? m_Model.Notice : m_Model.AssistanceStatus());
            if (m_Model.Notice.StartsWith("STOP received"))
                m_Status.text = "STOP received locally.\n" + m_Model.AssistanceStatus();
            if (m_Model.ReviewedApproval != null)
            {
                DrawApprovalReview(client);
                return;
            }

            bool hostReady = (bool)m_Model.Gateway.Capture()["ready"];
            string blocked = client.StartBlockedReason ?? (hostReady ? null : "Native host not ready. Open a sketch and wait for loading to finish.");
            m_Detail.text = blocked ?? "Request: " + m_Model.Prompt;
            if (client.CanReview)
            {
                DrawPendingProposal(client);
                return;
            }

            DrawRequestControls(client, hostReady);
        }

        void DrawApprovalReview(CHRISAssistanceClient client)
        {
            var reviewedApproval = m_Model.ReviewedApproval;
            m_Detail.text = CHRISPanel.Summary((JObject)reviewedApproval["action"], (JObject)client.Task?["context"]);
            bool proposalMatchesReview = (string)client.Task?["status"] == "awaiting_approval" &&
                JToken.DeepEquals(reviewedApproval, client.Task["approval"]);
            var nativeContext = m_Model.Gateway.Capture();
            bool approvalIsFresh = CHRISPanel.ApprovalCurrent(reviewedApproval, nativeContext, CHRISCommandGateway.Now);
            bool hostIsReady = (bool)nativeContext["ready"] && !(bool)nativeContext["stroke_active"];
            if (!approvalIsFresh)
                m_Status.text = "Approval expired or state changed. Reject / cancel, then request a fresh proposal.";
            else if (!hostIsReady)
                m_Status.text = "Native host unavailable or drawing. Wait before approving.";
            ConfigureChoice(0, "Approve change", () => m_Model.Decide(true),
                proposalMatchesReview && approvalIsFresh && hostIsReady && !client.Busy && !client.CancelWanted);
            ConfigureChoice(1, "Reject proposal", () => m_Model.Decide(false), proposalMatchesReview && !client.Busy);
            ConfigureChoice(2, "Cancel task", m_Model.StopLocal);
            ConfigureChoice(3, "Back", m_Model.Back);
        }

        void DrawPendingProposal(CHRISAssistanceClient client)
        {
            var approval = (JObject)client.Task["approval"];
            m_Detail.text = CHRISPanel.Summary((JObject)approval["action"],
                (JObject)client.Task["context"]) + "\n" + client.StartBlockedReason;
            ConfigureChoice(0, "Review proposal", m_Model.ReviewProposal);
            ConfigureChoice(1, "Cancel task", m_Model.StopLocal);
            ConfigureChoice(2, "No-model example", null, false);
            ConfigureChoice(3, "Enter request…", null, false);
            ConfigureChoice(4, "Check / Retry", client.Refresh, !client.Busy);
        }

        void DrawRequestControls(CHRISAssistanceClient client, bool hostReady)
        {
            ConfigureChoice(0, "Enter request…", EnterRequest, client.CanStart);
            ConfigureChoice(1, "Propose change", () => m_Model.Propose(false),
                hostReady && client.CanStart && !string.IsNullOrWhiteSpace(m_Model.Prompt) &&
                m_Model.Prompt.Length <= CHRISAssistanceClient.MaxPromptLength);
            ConfigureChoice(2, "No-model example", () => m_Model.Propose(true), hostReady && client.CanStart);
            ConfigureChoice(3, "Review proposal", m_Model.ReviewProposal, client.CanReview);
            ConfigureChoice(4, "Cancel task", m_Model.StopLocal, client.TaskId != null || client.PendingRequest != null);
            ConfigureChoice(5, "Check / Retry", () => RetryAssistance(client),
                !client.Busy && (client.TaskId != null || client.PendingRequest != null));
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
            KeyboardPopUpWindow.m_InitialText = m_Model.Prompt;
            var obj = m_ParentPanel.CreatePopUp(m_Resources.KeyboardPrefab, transform.position - transform.forward * 0.3f, true, true);
            var keyboard = obj.GetComponentInChildren<KeyboardUI>();
            keyboard.KeyPressed += (sender, key) =>
            {
                if (key.IsPress && key.Key.KeyType == KeyboardKeyType.Enter)
                {
                    m_Model.Prompt = keyboard.ConsoleContent.Trim();
                    m_Model.Refresh();
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
