// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace TiltBrush
{
    // Playback is informational. It never calls approval, execution, or transcription.
    public sealed class CHRISConfirmationSpeech : MonoBehaviour
    {
        internal const string Preference = "CHRIS.ConfirmationSpeech";
        const string SpeechUrl = "http://127.0.0.1:8765/voice/speech";
        internal readonly CHRISReadoutSession Session = new CHRISReadoutSession();
        public bool SpeechEnabled => Session.Enabled;
        public string Error { get; private set; }
        public event Action Changed;
        public CHRISPanel Model { private get; set; }
        AudioSource m_Source;
        UnityWebRequest m_Request;
        string m_SpeechId;

        void Awake()
        {
            Session.SetEnabled(PlayerPrefs.GetInt(Preference, 1) != 0);
            m_Source = gameObject.AddComponent<AudioSource>();
            m_Source.playOnAwake = false;
            m_Source.loop = false;
            m_Source.spatialBlend = 0;
        }

        public void Toggle()
        {
            Session.SetEnabled(!Session.Enabled);
            StopAudio();
            PlayerPrefs.SetInt(Preference, Session.Enabled ? 1 : 0);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        public void Cancel()
        {
            Session.Cancel();
            StopAudio();
            Error = null;
        }

        void StopAudio()
        {
            if (m_Source != null)
            {
                m_Source.Stop();
                if (m_Source.clip != null) Destroy(m_Source.clip);
                m_Source.clip = null;
            }
            string speechId = m_SpeechId;
            m_SpeechId = null;
            if (m_Request != null)
            {
                m_Request.Abort();
                m_Request.Dispose();
                m_Request = null;
            }
            if (speechId != null) CancelSynthesis(speechId);
        }

        static void CancelSynthesis(string speechId)
        {
            var request = new UnityWebRequest(SpeechUrl + "/" + speechId + "/cancel", "POST");
            try
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 8;
                request.redirectLimit = 0;
                request.SendWebRequest().completed += _ => request.Dispose();
            }
            catch (Exception)
            {
                // Local Stop and callback invalidation must complete even if the relay is unavailable.
                request.Dispose();
            }
        }

        internal string EligibleReviewKey()
        {
            if (Model == null || Model.Popup == null || !Model.Popup.IsOpen() || Model.Correcting ||
                Model.Voice.Session.IsActive || !Model.Assistance.CanReview || Model.ReviewedApproval == null ||
                !JToken.DeepEquals(Model.ReviewedApproval, Model.Assistance.Task["approval"]) ||
                Model.ReviewText != (string)Model.Assistance.Task["summary"] ||
                !CHRISPanel.ApprovalCurrent(Model.ReviewedApproval, Model.Gateway.Capture(), CHRISCommandGateway.Now)) return null;
            return CHRISCommandGateway.Hash(Model.ReviewedApproval.ToString(Formatting.None) + "\n" + Model.ReviewText);
        }

        void Update()
        {
            string key = EligibleReviewKey();
            if (Session.Observe(key))
            {
                StopAudio();
                Error = null;
            }
            if (Session.TryBegin())
                StartCoroutine(ReadConfirmation(Session.Generation, key, (JObject)Model.ReviewedApproval.DeepClone(), Model.ReviewText));
        }

        IEnumerator ReadConfirmation(long generation, string key, JObject approval, string summary)
        {
            string id = Guid.NewGuid().ToString("N");
            var body = new JObject { ["speech_id"] = id, ["task_id"] = approval["task_id"],
                ["approval_id"] = approval["approval_id"], ["action_digest"] = approval["action_digest"], ["summary"] = summary };
            var download = new CHRISAudioDownload();
            using (var request = new UnityWebRequest(SpeechUrl, "POST"))
            {
                m_Request = request;
                m_SpeechId = id;
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString(Formatting.None)));
                request.downloadHandler = download;
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 35;
                request.redirectLimit = 0;
                try
                {
                    yield return request.SendWebRequest();
                    if (!Session.Accepts(generation, key) || EligibleReviewKey() != key) yield break;
                    PlayResponse(request, download, id);
                    Changed?.Invoke();
                }
                finally
                {
                    if (m_Request == request) { m_Request = null; m_SpeechId = null; }
                    download.Clear();
                }
            }
        }

        void PlayResponse(UnityWebRequest request, CHRISAudioDownload download, string speechId)
        {
            bool valid = request.result == UnityWebRequest.Result.Success && !download.TooLarge &&
                request.GetResponseHeader("X-Speech-Id") == speechId &&
                (request.GetResponseHeader("Content-Type") ?? "").Split(';')[0].Trim() == "audio/pcm";
            try
            {
                if (!valid) throw new InvalidOperationException();
                float[] samples = CHRISAudio.Decode(download.TakeBytes());
                var clip = AudioClip.Create("CHRIS AI confirmation", samples.Length, 1, CHRISAudio.SampleRate, false);
                clip.SetData(samples, 0);
                m_Source.clip = clip;
                m_Source.Play();
            }
            catch (Exception)
            {
                Error = "AI speech unavailable. Review the text before confirming.";
            }
        }

        void OnDisable() => Cancel();
        void OnDestroy() => Cancel();
    }
}
