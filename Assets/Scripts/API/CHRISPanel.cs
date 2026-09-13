// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TiltBrush
{
    // Persistent state. The view uses Open Brush's native popup, pointer and button machinery.
    [DefaultExecutionOrder(9000)]
    public class CHRISPanel : MonoBehaviour
    {
        internal const int DirectChoicesPerPage = 4;
        const float InputReleaseTimeoutSeconds = 8;
        const float AssistancePollIntervalSeconds = 1;

        public static CHRISPanel Instance { get; private set; }

        public CHRISCommandGateway Gateway;
        public CHRISAssistanceClient Assistance { get; private set; }
        public CHRISNativePopup Popup { get; private set; }
        public string Mode { get; private set; } = "Choose controls";
        public string Category { get; private set; } = "";
        public string Notice { get; private set; } = "Choose Direct or Assistance.";
        public string Prompt { get; set; } = "Make my brush blue";
        public int Page { get; private set; }
        public JObject ReviewedAction { get; private set; }
        public JObject ReviewedApproval { get; private set; }

        JObject m_ReviewContext;
        JObject m_LastDirectAction;
        System.Action m_AfterRelease;
        float m_ReleaseDeadline;
        float m_NextPoll;
        string m_ResultShown;
        public bool WaitingForRelease => m_AfterRelease != null;

        public event System.Action Changed;

        void Start()
        {
            Instance = this;
            Assistance = GetComponent<CHRISAssistanceClient>() ?? gameObject.AddComponent<CHRISAssistanceClient>();
            Assistance.Changed += Refresh;
        }

        public void Opened(CHRISNativePopup popup)
        {
            if (Popup != null && Popup != popup)
                Popup.RequestClose(true);
            Popup = popup;
            ClearReview();
            Mode = "Choose controls";
            Category = "";
            Gateway.DirectPaletteOpen = false;
            Notice = "Direct changes stay local. Assistance uses the Python service.";
            if (Assistance.TaskId != null)
                Assistance.Refresh();
            Refresh();
        }

        public void Closed(CHRISNativePopup popup)
        {
            if (Popup == popup)
            {
                Popup = null;
                ClearReview();
                Gateway.DirectPaletteOpen = false;
                Mode = "Choose controls";
            }
        }

        public void SwitchMode(string mode)
        {
            if (Mode == mode)
                return;
            if (mode == "Direct" && !Assistance.CanStart)
            {
                Notice = "Cancel or finish assistance first. Check / Retry if disconnected.";
                Refresh();
                return;
            }

            ClearReview();
            Gateway.Stop("CHRIS control mode changed");
            Mode = mode;
            Category = "";
            Page = 0;
            Gateway.DirectPaletteOpen = mode == "Direct";
            Notice = mode == "Direct" ? "Direct control. Choose a change, review it, then Confirm." : "Assistance control. Proposals require your approval.";
            Refresh();
        }

        public void SetCategory(string category)
        {
            ClearReview();
            Category = category;
            Page = 0;
            Refresh();
        }

        public void ChangePage(int delta)
        {
            if (Mode != "Direct" || Category == "")
                return;
            int pages = Math.Max(1, (Choices(Category, Gateway.Capture()).Count + DirectChoicesPerPage - 1) / DirectChoicesPerPage);
            Page = Mathf.Clamp(Page + delta, 0, pages - 1);
            Refresh();
        }

        public void Back()
        {
            if (ReviewedAction != null || ReviewedApproval != null || WaitingForRelease)
                ClearReview();
            else
            {
                Category = "";
                Page = 0;
            }

            Notice = "Review cancelled. No new change sent.";
            Refresh();
        }

        void ClearReview()
        {
            ReviewedAction = null;
            ReviewedApproval = null;
            m_ReviewContext = null;
            m_AfterRelease = null;
        }

        public void StopLocal()
        {
            (Popup?.GetParentPanel() as CHRISFloatingPanel)?.EndDrag();
            ClearReview();
            Gateway.Stop();
            Assistance?.Cancel();
            m_ResultShown = ResultKey(Gateway.LastDirectResult);
            Notice = "STOP received locally. Completed changes remain. Check assistance cancellation acknowledgement.";
            Refresh();
        }

        public static bool InputReleased()
        {
            if (InputManager.m_Instance == null || InputManager.Controllers == null)
                return false;
            return !InputManager.m_Instance.GetCommand(InputManager.SketchCommands.Activate) &&
                !InputManager.Controllers.Any(c => c != null &&
                (c.GetVrInput(VrInput.Trigger) ||
                c.GetVrInput(VrInput.Grip))) &&
                !(Mouse.current?.leftButton.isPressed ?? false);
        }

        public static bool PassiveNativeUIHover()
        {
            var popup = Instance?.Popup;
            var controls = SketchControlsScript.m_Instance;
            return popup != null &&
                popup.IsOpen() &&
                popup.GetParentPanel() is CHRISFloatingPanel floating &&
                !floating.IsDragging &&
                popup.GetParentPanel()?.PanelPopUp == popup &&
                controls != null &&
                controls.IsUserLookingAtPanel(popup.GetParentPanel()) &&
                InputReleased();
        }

        void RunAfterInputRelease(System.Action action)
        {
            if (m_AfterRelease != null)
                return;
            m_AfterRelease = action;
            m_ReleaseDeadline = Time.unscaledTime + InputReleaseTimeoutSeconds;
            Notice = "Release the trigger / mouse button to continue.";
            Refresh();
        }

        public static bool SameContext(JObject reviewedContext, JObject currentContext) =>
            reviewedContext != null && currentContext != null &&
            (string)reviewedContext["host_session"] == (string)currentContext["host_session"] &&
            (long)reviewedContext["revision"] == (long)currentContext["revision"] &&
            (long)reviewedContext["authority_epoch"] == (long)currentContext["authority_epoch"];

        public static bool ApprovalCurrent(JObject approval, JObject context, double now) => SameContext(approval, context) &&
            (double?)approval["expires_at"] > now;
        bool IsDirectHostReady(JObject context) => (bool)context["ready"] &&
            !(bool)context["stroke_active"] &&
            context["active_task"].Type == JTokenType.Null;
        public void Review(JObject action)
        {
            var context = Gateway.Capture();
            if (Mode != "Direct" || !IsDirectHostReady(context))
            {
                Notice = "Host unavailable or busy. Finish drawing / active work first.";
                Refresh();
                return;
            }

            ReviewedAction = (JObject)action.DeepClone();
            m_ReviewContext = context;
            Notice = "Review this one change, then Confirm or Cancel.";
            Refresh();
        }

        public void ConfirmDirect()
        {
            if (ReviewedAction == null || Mode != "Direct")
                return;
            var action = (JObject)ReviewedAction.DeepClone();
            var context = m_ReviewContext;
            RunAfterInputRelease(() =>
            {
                var current = Gateway.Capture();
                if (!SameContext(context, current) || !IsDirectHostReady(current))
                {
                    ClearReview();
                    Notice = "State changed. Choose and review again.";
                    return;
                }

                m_LastDirectAction = action;
                Gateway.Direct(action);
                ClearReview();
                Notice = "Sent. Waiting for native readback.";
            });
        }

        public void Propose(bool example)
        {
            if (Mode == "Assistance")
                RunAfterInputRelease(() => Assistance.Submit(Prompt, example));
        }

        public void ReviewProposal()
        {
            if (!Assistance.CanReview)
                return;
            ReviewedApproval = (JObject)Assistance.Task["approval"].DeepClone();
            Notice = "Review the proposed change. Approval expires or becomes stale after manual changes.";
            Refresh();
        }

        public void Decide(bool approve)
        {
            if (Mode != "Assistance" || ReviewedApproval == null)
                return;
            var reviewed = (JObject)ReviewedApproval.DeepClone();
            RunAfterInputRelease(() =>
            {
                if (approve && !ApprovalCurrent(reviewed, Gateway.Capture(), CHRISCommandGateway.Now))
                {
                    Notice = "Approval expired or native state changed. Reject / cancel and request a fresh proposal.";
                    return;
                }

                Assistance.Decide(reviewed, approve);
                ReviewedApproval = null;
            });
        }

        public static JObject Action(string tool, string parameter, JToken value) => new JObject
        {
            ["action_id"] = Guid.NewGuid().ToString("N"),
            ["version"] = 1,
            ["tool"] = tool,
            ["number"] = null,
            ["text"] = null,
            ["vector"] = null,
            ["visible"] = null,
            [parameter] = value
        };
        public static List<KeyValuePair<string, JObject>> Choices(string category, JObject context)
        {
            var list = new List<KeyValuePair<string, JObject>>();
            System.Action<string, JObject> add = (label, action) => list.Add(new KeyValuePair<string, JObject>(label, action));
            switch (category)
            {
                case "Brush":
                    foreach (var brush in ((JObject)context["brushes"]).Properties().OrderBy(p => (string)p.Value))
                        add((string)brush.Value, Action("brush.select", "text", brush.Name));
                    break;
                case "Color":
                    foreach (var color in new[]
                    {
                        "#FFFFFF",
                        "#FF4040",
                        "#40A0FF",
                        "#40FF80"
                    }

                    )
                        add(color, Action("brush.color", "text", color));
                    break;
                case "Size":
                    foreach (double size in new[]
                    {
                        0.1,
                        0.3,
                        0.5,
                        0.8
                    }

                    )
                        add("Size " + size.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                            Action("brush.size", "number", size));
                    break;
                case "Panels":
                    foreach (string panel in new[]
                    {
                        "Brush",
                        "Color"
                    }

                    )
                        foreach (bool visible in new[]
                        {
                            true,
                            false
                        }

                        )
                        {
                            var action = Action("panel.visibility", "text", panel);
                            action["visible"] = visible;
                            add((visible ? "Open " : "Close ") + panel, action);
                        }

                    break;
                case "Move":
                    add("Move +X", Action("view.move", "vector", new JArray(0.25, 0, 0)));
                    add("Move -X", Action("view.move", "vector", new JArray(-0.25, 0, 0)));
                    add("Move +Z", Action("view.move", "vector", new JArray(0, 0, 0.25)));
                    add("Move -Z", Action("view.move", "vector", new JArray(0, 0, -0.25)));
                    break;
                case "Turn":
                    add("Turn left 15°", Action("view.turn", "number", -15));
                    add("Turn right 15°", Action("view.turn", "number", 15));
                    break;
            }

            return list;
        }

        public static string Summary(JObject action, JObject context)
        {
            if (action == null)
                return "No change selected.";
            switch ((string)action["tool"])
            {
                case "brush.select":
                    return "Select brush: " + ((string)context?["brushes"]?[(string)action["text"]] ?? (string)action["text"]);
                case "brush.color":
                    return "Set color " + (string)action["text"];
                case "brush.size":
                    return "Set brush size " + action["number"] + " (0–1). Draw a new stroke to compare.";
                case "panel.visibility":
                    return ((bool)action["visible"] ? "Open " : "Close ") + action["text"] + " panel";
                case "view.move":
                    return "Move scene by " + string.Join(", ", (JArray)action["vector"]) + " native units";
                case "view.turn":
                    return "Turn scene " + action["number"] + " degrees";
                default:
                    return "Unsupported action";
            }
        }

        public string AssistanceStatus()
        {
            if (Assistance.CancelWanted)
                return "Cancellation is unconfirmed. Check / Retry before more work. " + AssistanceReason(Assistance.Error);
            if (Assistance.Error != null)
                return AssistanceReason(Assistance.Error);
            var task = Assistance.Task;
            if (task == null)
                return Assistance.Busy ? "Contacting assistance…" : "Enter a request or try the no-model example.";
            string result = ((string)task["status"])?.Replace('_', ' ') + ": " + AssistanceReason((string)task["reason"]);
            if (task["result"] is JObject observed)
                result += "\n" + Outcome(observed, (JObject)task["approval"]?["action"]);
            return result;
        }

        public static string AssistanceReason(string reason)
        {
            // Display-only compatibility for historical task records.
            if (reason == "Direct palette owns control" || reason == "Close the CHRIS palette with F8, then submit a new browser request")
                return "Switch CHRIS to Assistance mode (or close CHRIS), then submit a new request.";
            return reason;
        }

        public static string Outcome(JObject result, JObject action)
        {
            string text = (string)result["status"] + ": " + AssistanceReason((string)result["reason"]);
            if ((string)result["status"] != "succeeded" || !(result["observed"] is JObject observed))
                return text;
            switch ((string)action?["tool"])
            {
                case "brush.size":
                    return "Applied. Observed brush size: " + observed["brush_size"] + " (0–1). Draw a new stroke to compare.";
                case "brush.color":
                    return "Applied. Observed color: " + observed["brush_color"];
                case "brush.select":
                    return "Applied. Observed brush: " + ((string)observed["brushes"]?[(string)observed["brush_id"]] ?? (string)observed["brush_id"]);
                case "panel.visibility":
                    return "Applied. " + action["text"] + " panel is " + ((bool?)observed["panels"]?[(string)action["text"]] == true ? "open." : "closed.");
                case "view.move":
                    return "Applied. Observed scene position: " + string.Join(", ", (JArray)observed["scene_position"]);
                case "view.turn":
                    return "Applied " + action["number"] + "° scene turn. Native rotation verified; compare existing artwork.";
                default:
                    return text;
            }
        }

        public void Refresh()
        {
            Changed?.Invoke();
        }

        static string ResultKey(JObject result) => result == null ? null : (string)result["command_id"] + ":" + (string)result["status"];
        void Update()
        {
            if (Gateway == null || Assistance == null)
                return;
            ProcessPendingInputRelease();
            InvalidateStaleDirectReview();
            ShowLatestDirectResult();
            PollOpenPopup();
        }

        void ProcessPendingInputRelease()
        {
            if (m_AfterRelease != null)
            {
                if (Popup == null || Time.unscaledTime > m_ReleaseDeadline)
                {
                    m_AfterRelease = null;
                    Notice = "Input was not released. Nothing new sent; review again.";
                    Refresh();
                }
                else if (InputReleased() && !CHRISCommandGateway.NativeInteractionBusy())
                {
                    var action = m_AfterRelease;
                    m_AfterRelease = null;
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Notice = "Change rejected: " + ex.Message;
                    }

                    Refresh();
                }
            }
        }

        void InvalidateStaleDirectReview()
        {
            if (ReviewedAction != null && !WaitingForRelease && !SameContext(m_ReviewContext, Gateway.Capture()))
            {
                ClearReview();
                Notice = "State changed. Review the choice again.";
                Refresh();
            }
        }

        void ShowLatestDirectResult()
        {
            var result = Gateway.LastDirectResult;
            if (result != null && (string)result["status"] != "queued")
            {
                string key = ResultKey(result);
                if (key != m_ResultShown)
                {
                    m_ResultShown = key;
                    Notice = Outcome(result, m_LastDirectAction);
                    Refresh();
                }
            }
        }

        void PollOpenPopup()
        {
            if (Popup != null && Time.unscaledTime >= m_NextPoll)
            {
                m_NextPoll = Time.unscaledTime + AssistancePollIntervalSeconds;
                if (Mode == "Assistance")
                    Assistance.Refresh();
                Refresh();
            }
        }

        void OnDisable()
        {
            ClearReview();
            if (Gateway != null)
            {
                Gateway.DirectPaletteOpen = false;
                Gateway.Stop("CHRIS UI disabled");
            }
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (Assistance != null)
                Assistance.Changed -= Refresh;
        }
    }
}
