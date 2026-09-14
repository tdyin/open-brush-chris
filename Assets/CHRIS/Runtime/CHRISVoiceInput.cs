// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace TiltBrush
{
    // The frame loop only updates presentation and signals workers. Capture and device cleanup own no Unity objects.
    public sealed class CHRISVoiceInput : MonoBehaviour
    {
        public const int SampleRate = CHRISAudio.SampleRate;
        public const int MaxRecordingSeconds = 60;
        const float StartCueQuietSeconds = 0.25f;
        const float ConnectingTimeoutSeconds = 10;
        const float FinalizingTimeoutSeconds = 20;
        const int MaxQueuedPartialMessages = 32;

        sealed class RecordingRun
        {
            public long Id;
            public int Cancelled, Finishing;
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public readonly BlockingCollection<short[]> Audio = new BlockingCollection<short[]>(20);
            public Task Relay, Capture = Task.CompletedTask, CancellationWork = Task.CompletedTask;
            public bool Completed => Relay.IsCompleted && Capture.IsCompleted && CancellationWork.IsCompleted;

            public void Cancel()
            {
                if (Interlocked.Exchange(ref Cancelled, 1) == 0)
                    CancellationWork = Task.Run(() => Cancellation.Cancel());
            }

            public void Dispose()
            {
                Audio.Dispose();
                Cancellation.Dispose();
            }
        }

        struct WorkerMessage
        {
            public long Id;
            public string Kind;
            public string Text;
        }
        public CHRISVoiceSession Session { get; } = new CHRISVoiceSession();
        public string SelectedMicrophone { get; private set; }
        public bool MicrophoneStopped { get; private set; } = true;
        string m_SelectedMicrophoneLabel;
        string m_DeviceQueryError;
        public string MicrophoneLabel => m_DeviceQuery != null ? "Finding microphones..." :
            m_DeviceQueryError ?? m_SelectedMicrophoneLabel ?? "Windows default";
        public event Action Changed;
        public event Action<string> Finalized;
        public event Action CaptureStopped;
        internal Func<CHRISMicrophoneDevice[]> Devices { get; set; } = CHRISMicrophoneCapture.Devices;
        internal Func<float> PlayStartCue { get; set; }
        readonly ConcurrentQueue<WorkerMessage> m_Messages = new ConcurrentQueue<WorkerMessage>();
        RecordingRun m_Run;
        Task<CHRISMicrophoneDevice[]> m_DeviceQuery;
        float m_PhaseStarted, m_CaptureNotBefore;
        bool m_StartRequested, m_ReadyPending;

        public string Status
        {
            get
            {
                switch (Session.State)
                {
                    case CHRISVoiceSession.Phase.Loading: return "Connecting microphone and speech...";
                    case CHRISVoiceSession.Phase.Recording: return "Listening";
                    case CHRISVoiceSession.Phase.Finalizing: return "Finalizing transcript";
                    case CHRISVoiceSession.Phase.Failed: return Session.Error;
                    default: return "Ready to listen";
                }
            }
        }

        public void StartRecording()
        {
            CancelRecording();
            Session.Begin();
            m_PhaseStarted = Time.realtimeSinceStartup;
            m_CaptureNotBefore = m_PhaseStarted + StartCueQuietSeconds;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            m_StartRequested = true;
#else
            Session.Fail(Session.Id, "Speech input requires the Windows PCVR player.");
#endif
            Changed?.Invoke();
        }

        public void FinishRecording()
        {
            if (!Session.RequestFinish()) return;
            m_PhaseStarted = Time.realtimeSinceStartup;
            if (m_Run != null) Volatile.Write(ref m_Run.Finishing, 1);
            Changed?.Invoke();
        }

        public void CancelRecording()
        {
            Session.Cancel();
            m_StartRequested = m_ReadyPending = false;
            m_Run?.Cancel();
            Changed?.Invoke();
        }

        public void NextMicrophone()
        {
            CancelRecording();
            m_DeviceQueryError = null;
            if (m_DeviceQuery == null) m_DeviceQuery = Task.Run(Devices);
            Changed?.Invoke();
        }

        void StartWorker()
        {
            m_StartRequested = false;
            var run = new RecordingRun { Id = Session.Id };
            m_Run = run;
            run.Relay = Task.Run(async () =>
            {
                try
                {
                    await new CHRISCloudTranscription().Run(run.Audio, run.Cancellation.Token,
                        (kind, text) => Post(run, kind, text), () => Volatile.Read(ref run.Cancelled) != 0);
                }
                catch (OperationCanceledException) { }
                catch (Exception) { Post(run, "error", "Speech unavailable. Check the CHRIS service and connection, then Retry."); }
            });
        }

        void BeginMicrophone(long id)
        {
            if (id != Session.Id || Session.State != CHRISVoiceSession.Phase.Loading || m_Run == null) return;
            var run = m_Run;
            string selected = SelectedMicrophone;
            var devices = Devices;
            MicrophoneStopped = false;
            run.Capture = Task.Run(() =>
            {
                try
                {
                    if (devices().Length == 0) { Post(run, "error", "Microphone unavailable. Select an available Windows input device."); return; }
                    CHRISMicrophoneCapture.Record(selected, run.Audio,
                        () => Volatile.Read(ref run.Finishing) != 0, () => Volatile.Read(ref run.Cancelled) != 0,
                        (kind, text) => Post(run, kind, text));
                }
                catch (Exception) { Post(run, "error", "Microphone unavailable or recording limit reached. Check the input device, then Retry."); }
            });
        }

        void Post(RecordingRun run, string kind, string text)
        {
            if (Volatile.Read(ref run.Cancelled) != 0) return;
            if (kind == "partial" && m_Messages.Count >= MaxQueuedPartialMessages) return;
            m_Messages.Enqueue(new WorkerMessage { Id = run.Id, Kind = kind, Text = text });
        }

        void Fail(string error)
        {
            Session.Fail(Session.Id, error);
            m_StartRequested = m_ReadyPending = false;
            m_Run?.Cancel();
            Changed?.Invoke();
        }

        void Update()
        {
            while (m_Messages.TryDequeue(out var message)) Receive(message.Id, message.Kind, message.Text);
            CompleteDeviceQuery();
            UpdateRecordingRun();
            CheckPhaseTimeout();
        }

        void UpdateRecordingRun()
        {
            if (m_Run != null && m_Run.Completed)
            {
                m_Run.Dispose();
                m_Run = null;
            }
            if (m_StartRequested && m_Run == null && m_DeviceQuery == null) StartWorker();
            if (m_ReadyPending && m_DeviceQuery == null && Time.realtimeSinceStartup >= m_CaptureNotBefore)
            {
                m_ReadyPending = false;
                BeginMicrophone(Session.Id);
            }
        }

        void CheckPhaseTimeout()
        {
            if (Session.IsActive && Time.realtimeSinceStartup - m_PhaseStarted >
                (Session.State == CHRISVoiceSession.Phase.Recording ? MaxRecordingSeconds :
                 Session.State == CHRISVoiceSession.Phase.Loading ? ConnectingTimeoutSeconds : FinalizingTimeoutSeconds))
                Fail("Speech timed out or reached 60 seconds. Check the connection and Retry.");
        }

        void CompleteDeviceQuery()
        {
            if (m_DeviceQuery != null && m_DeviceQuery.IsCompleted)
            {
                if (m_DeviceQuery.Status == TaskStatus.RanToCompletion)
                {
                    var devices = m_DeviceQuery.Result;
                    int next = SelectedMicrophone == null ? 0 : Array.FindIndex(devices, device => device.Id == SelectedMicrophone) + 1;
                    var selected = next >= devices.Length ? null : devices[next];
                    SelectedMicrophone = selected?.Id;
                    m_SelectedMicrophoneLabel = selected?.Label;
                }
                else
                {
                    // Only a start waiting on this selection depends on the failed query.
                    // A late failure after Cancel must not resurrect a recording error.
                    _ = m_DeviceQuery.Exception;
                    m_DeviceQueryError = "Cannot list microphones. Check Windows microphone access.";
                    if (m_StartRequested) Fail(m_DeviceQueryError);
                }
                m_DeviceQuery = null;
                Changed?.Invoke();
            }
        }

        internal void Receive(long id, string kind, string text)
        {
            if (id != Session.Id || !Session.IsActive) return;
            if (kind == "ready")
            {
                float cueDuration = PlayStartCue?.Invoke() ?? 0;
                m_PhaseStarted = Time.realtimeSinceStartup;
                m_CaptureNotBefore = m_PhaseStarted + cueDuration + StartCueQuietSeconds;
                m_ReadyPending = true;
            }
            else if (kind == "listening")
            {
                Session.Ready(id);
                m_PhaseStarted = Time.realtimeSinceStartup;
            }
            else if (kind == "microphone-stopped")
            {
                MicrophoneStopped = true;
                CaptureStopped?.Invoke();
            }
            else if (kind == "partial") Session.Partial(id, text);
            else if (kind == "error") Fail(text);
            else if (kind == "final" && Session.Complete(id, text)) Finalized?.Invoke(Session.Transcript);
            if (Session.State == CHRISVoiceSession.Phase.Failed) Fail(Session.Error);
            Changed?.Invoke();
        }

        void OnDisable()
        {
            CancelRecording();
            RetireWorker();
        }

        void OnDestroy()
        {
            CancelRecording();
            RetireWorker();
        }

        void RetireWorker()
        {
            var run = m_Run;
            m_Run = null;
            if (run != null)
                Task.WhenAll(run.Relay, run.Capture, run.CancellationWork).ContinueWith(_ => run.Dispose(), TaskScheduler.Default);
        }
    }
}
