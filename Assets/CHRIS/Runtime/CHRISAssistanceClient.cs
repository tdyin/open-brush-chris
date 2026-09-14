// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace TiltBrush
{
    // Fixed loopback endpoint. Model selection, secrets, proposals and approval ledger remain in Python.
    public class CHRISAssistanceClient : MonoBehaviour
    {
        internal const int MaxPromptLength = 2000;
        const int RequestTimeoutSeconds = 8;
        const int MaxSavedRequestLength = 16384;
        const string ServiceBaseUrl = "http://127.0.0.1:8765";
        string m_SavedTask, m_SavedPending;
        bool? m_SavedCancel;
        public double LastRecoverySaveMilliseconds { get; private set; }
        public int RecoverySaveCount { get; private set; }

        public JObject Task { get; private set; }
        public string TaskId { get; private set; }
        public bool Busy { get; private set; }
        public bool CancelWanted { get; private set; }
        public string Error { get; private set; }
        public JObject PendingRequest { get; private set; }
        public string RecoveryPath => PendingRequest == null ? null : CancelWanted ? "/requests/" + (string)PendingRequest["request_id"] : "/tasks";

        public event Action Changed;

        public static bool ValidId(string value) => value != null && Regex.IsMatch(value, "\\A[A-Za-z0-9_-]{1,80}\\z");
        public static bool Terminal(JObject task) => task != null &&
            !((bool?)task["cancelled"] == true && (bool?)task["native_cancel_acknowledged"] != true) &&
            ((string)task["status"] == "succeeded" ||
             (string)task["status"] == "rejected" ||
             (string)task["status"] == "failed" ||
             (string)task["status"] == "partial" ||
             ((string)task["status"] == "cancelled" && (bool?)task["native_cancel_acknowledged"] == true));

        public bool CanStart => !Busy && !CancelWanted && PendingRequest == null && (TaskId == null || Terminal(Task));
        // Reviewing a captured proposal does not dispatch anything. A status poll must not
        // make the review button flicker; approval still waits for Busy to clear and revalidates.
        public bool CanReview => !CancelWanted && (string)Task?["status"] == "awaiting_approval" &&
            Task?["approval"] is JObject approval && (string)approval["scope"] == "control_segment" &&
            approval["actions"] is JArray actions && actions.Count >= 1 && actions.Count <= 5 &&
            JToken.DeepEquals(actions, Task["actions"]) && Task["summary"]?.Type == JTokenType.String &&
            !string.IsNullOrWhiteSpace((string)Task["summary"]) && ((string)Task["summary"]).Length <= 4000;

        public string StartBlockedReason
        {
            get
            {
                if (CanStart)
                    return null;
                if (CancelWanted)
                    return "Cancellation unconfirmed. Select Retry.";
                if (PendingRequest != null)
                    return "Submission reply missing. Select Retry.";
                if (TaskId != null && !Terminal(Task))
                {
                    if (Task == null)
                        return "Saved task needs checking. Select Retry.";
                    if (CanReview)
                        return "Review the commands, or re-record to replace this request.";
                    if ((string)Task["status"] == "awaiting_approval")
                        return "This old or invalid proposal must be cancelled. Re-record to request a new one.";
                    if ((string)Task["status"] == "paused" || (string)Task["status"] == "unverified")
                        return "Task needs reconciliation. Select Retry or STOP.";
                    return "A task is in progress. Wait or select STOP.";
                }

                return "Contacting assistance. Please wait.";
            }
        }

        void Awake()
        {
            string id = PlayerPrefs.GetString("CHRIS.AssistanceTask", "");
            TaskId = ValidId(id) ? id : null;
            string pending = PlayerPrefs.GetString("CHRIS.AssistancePending", "");
            m_SavedTask = id;
            m_SavedPending = pending;
            m_SavedCancel = PlayerPrefs.GetInt("CHRIS.AssistanceCancel", 0) != 0;
            try
            {
                if (pending.Length > 0 && pending.Length <= MaxSavedRequestLength)
                    PendingRequest = JObject.Parse(pending);
            }
            catch (JsonException)
            {
                Error = "Saved request is unreadable. Check the assistance service before starting more work.";
            }

            // Retired example submissions may already exist remotely. Reconcile and cancel
            // their original request ID instead of posting the obsolete action payload again.
            CancelWanted = PlayerPrefs.GetInt("CHRIS.AssistanceCancel", 0) != 0 ||
                PendingRequest?.Property("action") != null;
        }

        void SaveRecoveryState()
        {
            string task = TaskId ?? "", pending = PendingRequest?.ToString(Formatting.None) ?? "";
            if (task == m_SavedTask && pending == m_SavedPending && CancelWanted == m_SavedCancel) return;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            PlayerPrefs.SetString("CHRIS.AssistanceTask", task);
            PlayerPrefs.SetString("CHRIS.AssistancePending", pending);
            PlayerPrefs.SetInt("CHRIS.AssistanceCancel", CancelWanted ? 1 : 0);
            PlayerPrefs.Save();
            LastRecoverySaveMilliseconds = timer.Elapsed.TotalMilliseconds;
            RecoverySaveCount++;
            m_SavedTask = task;
            m_SavedPending = pending;
            m_SavedCancel = CancelWanted;
        }

        public void Submit(string prompt)
        {
            if (!CanStart)
                return;
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > MaxPromptLength)
            {
                Error = "Enter a request of 1–2000 characters.";
                Changed?.Invoke();
                return;
            }

            Task = null;
            TaskId = null;
            PendingRequest = new JObject
            {
                ["request_id"] = Guid.NewGuid().ToString("N"),
                ["request"] = prompt
            };
            SaveRecoveryState();
            RetrySubmission();
        }

        public void RetrySubmission()
        {
            if (!Busy && PendingRequest != null)
            {
                if (CancelWanted)
                    StartCoroutine(SendRequest(RecoveryPath, null, true));
                else
                    StartCoroutine(SendRequest("/tasks", PendingRequest, true));
            }
        }

        public void Refresh()
        {
            if (!Busy && ValidId(TaskId))
                StartCoroutine(SendRequest("/tasks/" + TaskId, null));
        }

        public void Decide(JObject reviewed, bool approve)
        {
            if (Busy ||
                CancelWanted ||
                reviewed == null ||
                !CanReview ||
                !JToken.DeepEquals(reviewed, Task["approval"]))
            {
                Error = "Proposal changed. Review it again.";
                Changed?.Invoke();
                return;
            }

            StartCoroutine(SendRequest("/tasks/" + TaskId + "/approval", new JObject { ["approval_id"] = reviewed["approval_id"], ["action_digest"] = reviewed["action_digest"], ["approve"] = approve }));
        }

        public void Cancel()
        {
            CancelWanted = TaskId != null || PendingRequest != null || Busy;
            SaveRecoveryState();
            if (!Busy && PendingRequest != null)
                RetrySubmission(); // Recover the same request, never create another.
            else if (!Busy && TaskId != null)
                StartCoroutine(SendRequest("/tasks/" + TaskId + "/cancel", new JObject()));
        }

        public void Resume()
        {
            if (!Busy && ValidId(TaskId))
                StartCoroutine(SendRequest("/tasks/" + TaskId + "/resume", new JObject()));
        }

        IEnumerator SendRequest(string path, JObject body, bool submission = false)
        {
            Busy = true;
            Error = null;
            Changed?.Invoke();
            using (var request = new UnityWebRequest(ServiceBaseUrl + path, body == null ? "GET" : "POST"))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = RequestTimeoutSeconds;
                request.redirectLimit = 0;
                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Formatting.None)));
                    request.SetRequestHeader("Content-Type", "application/json");
                }

                yield return request.SendWebRequest();
                JObject value = null;
                try
                {
                    value = JObject.Parse(request.downloadHandler.text);
                }
                catch (JsonException)
                {
                }

                AcceptReply(request.result == UnityWebRequest.Result.Success, request.responseCode, value, submission, body != null);
            }

            Busy = false;
            SaveRecoveryState();
            Changed?.Invoke();
            if (CancelWanted && Error == null && TaskId != null && !path.EndsWith("/cancel"))
                StartCoroutine(SendRequest("/tasks/" + TaskId + "/cancel", new JObject()));
        }

        // Kept separate from transport so uncertain and cancelled replies can be tested without a service/model.
        internal void AcceptReply(bool success, long code, JObject value, bool submission, bool isPost)
        {
            if (success && ValidId((string)value?["task_id"]))
            {
                Error = null;
                Task = value;
                TaskId = (string)value["task_id"];
                if (submission)
                    PendingRequest = null;
                if (CancelWanted && PendingRequest == null && (bool?)value["native_cancel_acknowledged"] == true)
                    CancelWanted = false;
            }
            else
            {
                Error = value?["detail"]?.Type == JTokenType.String ? (string)value["detail"] : "Assistance service unavailable (127.0.0.1:8765). Start the Python service, then select Retry.";
                if (submission && isPost && code >= 400 && code < 500)
                {
                    PendingRequest = null;
                    CancelWanted = false;
                }
            // Timeout or lookup failure: retain the exact request ID and cancellation interlock.
            }
        }
    }
}
