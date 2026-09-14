// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace TiltBrush
{
    // Unity microphone access is main-thread only. Transient PCM goes to the loopback relay.
    public sealed class CHRISVoiceInput : MonoBehaviour
    {
        public const int SampleRate = CHRISAudio.SampleRate;
        public const int MaxRecordingSeconds = 60;
        const int ChunkFrames = 2400;
        const float ProviderTimeoutSeconds = 20;

        sealed class Run
        {
            public long Id;
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            public readonly BlockingCollection<short[]> Audio = new BlockingCollection<short[]>(20);
            public Task Worker;
        }

        struct Message
        {
            public long Id;
            public string Kind, Text;
        }

        public CHRISVoiceSession Session { get; } = new CHRISVoiceSession();
        public string SelectedMicrophone { get; private set; }
        public string MicrophoneLabel => SelectedMicrophone ?? "Windows default";
        public event Action Changed;
        public event Action<string> Finalized;
        internal Func<string[]> Devices { get; set; } = () => Microphone.devices;
        internal Func<float> PlayStartCue { get; set; }
        readonly ConcurrentQueue<Message> m_Messages = new ConcurrentQueue<Message>();
        Run m_Run;
        AudioClip m_Clip;
        string m_RecordingDevice;
        int m_ReadFrames;
        float m_PhaseStarted;
        bool m_StartRequested;
        bool m_MicrophoneOwned;
        bool m_ReadyPending;
        float m_CaptureNotBefore;

        public string Status
        {
            get
            {
                switch (Session.State)
                {
                    case CHRISVoiceSession.Phase.Loading: return "Connecting speech recognition...";
                    case CHRISVoiceSession.Phase.Recording: return "Listening. Text updates after pauses. Finish when done.";
                    case CHRISVoiceSession.Phase.Finalizing: return "Finishing transcript...";
                    case CHRISVoiceSession.Phase.Failed: return Session.Error;
                    default: return "Tap Record or click the non-drawing joystick while CHRIS is open.";
                }
            }
        }

        public void StartRecording()
        {
            CancelRecording();
            Session.Begin();
            m_PhaseStarted = Time.realtimeSinceStartup;
            m_CaptureNotBefore = m_PhaseStarted + 0.25f;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            m_StartRequested = true;
#else
            Session.Fail(Session.Id, "Speech input is available in the Windows PCVR build. Use the Windows PCVR player.");
#endif
            Changed?.Invoke();
        }

        public void FinishRecording()
        {
            if (Session.State != CHRISVoiceSession.Phase.Recording) return;
            CaptureAudio(flush: true);
            if (!Session.RequestFinish()) return;
            ReleaseMicrophone();
            m_PhaseStarted = Time.realtimeSinceStartup;
            m_Run.Audio.CompleteAdding();
            Changed?.Invoke();
        }

        public void CancelRecording()
        {
            Session.Cancel();
            m_StartRequested = false;
            m_ReadyPending = false;
            ReleaseMicrophone();
            m_Run?.Cancel.Cancel();
            Changed?.Invoke();
        }

        public void NextMicrophone()
        {
            CancelRecording();
            string[] devices = Devices();
            int next = SelectedMicrophone == null ? 0 : Array.IndexOf(devices, SelectedMicrophone) + 1;
            SelectedMicrophone = next >= devices.Length ? null : devices[next];
            Changed?.Invoke();
        }

        void StartWorker()
        {
            m_StartRequested = false;
            var run = new Run { Id = Session.Id };
            m_Run = run;
            run.Worker = Task.Run(() => Recognize(run));
        }

        async Task Recognize(Run run)
        {
            try
            {
                await new CHRISCloudTranscription().Run(run.Audio, run.Cancel.Token, (kind, text) => Post(run, kind, text));
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                Post(run, "error", "Cloud speech unavailable. Check the CHRIS service and connection, then re-record.");
            }
        }

        void Post(Run run, string kind, string text)
        {
            if (run.Cancel.IsCancellationRequested) return;
            // Partials can be coalesced under frame stalls; final/error messages are never dropped.
            if (kind == "partial" && m_Messages.Count >= 32) return;
            m_Messages.Enqueue(new Message { Id = run.Id, Kind = kind, Text = text });
        }

        void BeginMicrophone(long id)
        {
            if (id != Session.Id || Session.State != CHRISVoiceSession.Phase.Loading) return;
            string[] devices = Devices();
            if (devices.Length == 0 || (SelectedMicrophone != null && !devices.Contains(SelectedMicrophone)))
            {
                Fail("Microphone unavailable. Choose an available Windows input device.");
                return;
            }
            try
            {
                m_RecordingDevice = SelectedMicrophone;
                m_MicrophoneOwned = true;
                m_Clip = Microphone.Start(m_RecordingDevice, false, MaxRecordingSeconds + 1, SampleRate);
                if (m_Clip == null || m_Clip.frequency != SampleRate) throw new InvalidOperationException();
                m_ReadFrames = 0;
                m_PhaseStarted = Time.realtimeSinceStartup;
                Session.Ready(id);
            }
            catch (Exception)
            {
                Fail("Cannot access the microphone. Check Windows microphone permission.");
            }
        }

        void CaptureAudio(bool flush)
        {
            if (m_Clip == null || m_Run == null) return;
            try
            {
                int position = Microphone.GetPosition(m_RecordingDevice);
                if (position < m_ReadFrames) throw new InvalidOperationException("Microphone stopped unexpectedly.");
                while (position - m_ReadFrames >= ChunkFrames || (flush && position > m_ReadFrames))
                {
                    int frames = Math.Min(ChunkFrames, position - m_ReadFrames);
                    var samples = new float[frames * m_Clip.channels];
                    if (!m_Clip.GetData(samples, m_ReadFrames)) throw new InvalidOperationException();
                    var mono = new short[frames];
                    for (int frame = 0; frame < frames; frame++)
                    {
                        float sample = 0;
                        for (int channel = 0; channel < m_Clip.channels; channel++)
                            sample += samples[frame * m_Clip.channels + channel];
                        mono[frame] = (short)Mathf.RoundToInt(Mathf.Clamp(sample / m_Clip.channels, -1, 1) * 32767);
                    }
                    if (!m_Run.Audio.TryAdd(mono)) throw new InvalidOperationException("Recognition fell behind.");
                    m_ReadFrames += frames;
                }
                if (!Microphone.IsRecording(m_RecordingDevice) ||
                    (position == 0 && Time.realtimeSinceStartup - m_PhaseStarted > 3))
                    throw new InvalidOperationException("Microphone is not delivering audio.");
            }
            catch (Exception)
            {
                Fail("Microphone or recognition could not keep up. Re-record a shorter request.");
            }
        }

        void Fail(string error)
        {
            Session.Fail(Session.Id, error);
            m_StartRequested = false;
            m_ReadyPending = false;
            ReleaseMicrophone();
            m_Run?.Cancel.Cancel();
            Changed?.Invoke();
        }

        void ReleaseMicrophone()
        {
            if (!m_MicrophoneOwned && m_Clip == null) return;
            try { if (m_MicrophoneOwned) Microphone.End(m_RecordingDevice); }
            catch (Exception) { /* Stop must still invalidate callbacks if the device disappeared. */ }
            finally
            {
                if (m_Clip != null) Destroy(m_Clip);
                m_Clip = null;
                m_MicrophoneOwned = false;
                m_RecordingDevice = null;
            }
        }

        void Update()
        {
            while (m_Messages.TryDequeue(out var message))
                Receive(message.Id, message.Kind, message.Text);
            if (m_Run != null && m_Run.Worker.IsCompleted)
            {
                m_Run.Audio.Dispose();
                m_Run.Cancel.Dispose();
                m_Run = null;
            }
            if (m_StartRequested && m_Run == null) StartWorker();
            if (m_ReadyPending && Time.realtimeSinceStartup >= m_CaptureNotBefore)
            {
                m_ReadyPending = false;
                BeginMicrophone(Session.Id);
                Changed?.Invoke();
            }
            if (Session.State == CHRISVoiceSession.Phase.Recording)
            {
                if (Time.realtimeSinceStartup - m_PhaseStarted >= MaxRecordingSeconds)
                    Fail("Recording reached 60 seconds. Re-record a shorter request, then tap Finish.");
                else CaptureAudio(flush: false);
            }
            else if (Session.IsActive && Time.realtimeSinceStartup - m_PhaseStarted >
                (Session.State == CHRISVoiceSession.Phase.Loading ? 10 : ProviderTimeoutSeconds))
                Fail("Speech recognition timed out. Check the connection and re-record.");
        }

        internal void Receive(long id, string kind, string text)
        {
            if (id != Session.Id || !Session.IsActive) return;
            if (kind == "ready")
            {
                // The cue completes before capture begins; the haptic marks actual listening.
                float cueDuration = PlayStartCue?.Invoke() ?? 0;
                m_PhaseStarted = Time.realtimeSinceStartup;
                m_CaptureNotBefore = m_PhaseStarted + cueDuration + 0.25f;
                m_ReadyPending = true;
            }
            else if (kind == "partial") Session.Partial(id, text);
            else if (kind == "error") Fail(text);
            else if (kind == "final" && Session.Complete(id, text)) Finalized?.Invoke(Session.Transcript);
            if (Session.State == CHRISVoiceSession.Phase.Failed) Fail(Session.Error);
            Changed?.Invoke();
        }

        void OnDisable() { CancelRecording(); RetireWorker(); }
        void OnDestroy() { CancelRecording(); RetireWorker(); }

        void RetireWorker()
        {
            // No Unity calls and no blocking join. Cleanup still completes with Update disabled.
            var run = m_Run;
            if (run == null) return;
            m_Run = null;
            run.Worker.ContinueWith(_ =>
            {
                run.Audio.Dispose();
                run.Cancel.Dispose();
            }, TaskScheduler.Default);
        }
    }
}
