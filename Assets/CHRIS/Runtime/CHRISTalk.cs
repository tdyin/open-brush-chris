// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using UnityEngine;

namespace TiltBrush
{
    // D129 tap-to-talk on the CHRIS panel. The first physical click on the mic button starts a
    // recording, the second sends it; Cancel, Stop or focus loss end it with nothing sent. Audio
    // goes through the existing voice relay (CHRISVoiceInput -> the service's /voice/transcribe,
    // which calls OpenAI); only the final transcript leaves as an "utterance" panel event. Talking
    // never starts a task: the test session still needs a physical Approve.
    public sealed class CHRISTalk
    {
        public enum State { Idle, Recording, Sending }
        // The voice relay itself fails at 60 s; send just before, so speech is not lost.
        public const float AutoSendSeconds = 58f;
        public const string PrivacyLine = "Speech goes to OpenAI";
        public const string BusyNotice = "Wait for the current task";

        readonly CHRISVoiceInput m_Voice;
        readonly Func<CHRISTestDisplay> m_Display;
        readonly Func<bool> m_Focused;
        int m_Count;
        float m_Started;
        int m_HeardVersion = -1, m_NoticeVersion = -1;

        public State Current { get; private set; }
        // The last transcript sent; shown until the test session's next display replaces it.
        public string Heard { get; private set; }
        // Why the last click did nothing, or why the last recording ended without sending.
        public string Notice { get; private set; }
        // Bumped whenever the panel must redraw the talk area.
        public int Version { get; private set; }
        public string Microphone => m_Voice.MicrophoneLabel;

        public CHRISTalk(CHRISVoiceInput voice, Func<CHRISTestDisplay> display, Func<bool> focused = null)
        {
            m_Voice = voice;
            m_Display = display;
            m_Focused = focused ?? (() => true);
            m_Voice.Finalized += OnFinal;
            m_Voice.Changed += () => Version++;
        }

        // What the talk area shows as heard: the live transcript while recording or sending, then
        // the sent text until the display changes.
        public string HeardText(int displayVersion) => Current != State.Idle ? m_Voice.Session.Transcript :
            displayVersion == m_HeardVersion ? Heard : null;

        // A notice shows until the test session's next display.
        public string NoticeText(int displayVersion) => displayVersion == m_NoticeVersion ? Notice : null;

        public string Phase => Current == State.Recording
            ? m_Voice.Session.State == CHRISVoiceSession.Phase.Recording ? "Listening" : "Connecting"
            : Current == State.Sending ? "Sending" : null;

        // A physical click on the mic button. busy: why talking is not allowed now, or null.
        public void Click(float now, string busy)
        {
            if (Current == State.Idle)
            {
                if (busy != null) { Changed(busy); return; }
                Notice = Heard = null;
                m_Started = now;
                Current = State.Recording;
                m_Voice.StartRecording();
                Debug.Log("CHRIS voice: recording started on " + Microphone);
                Changed(null);
            }
            else if (Current == State.Recording) Send(now);
        }

        void Send(float now)
        {
            if (m_Voice.Session.State != CHRISVoiceSession.Phase.Recording)
            {
                // Still connecting: nothing was captured yet.
                Cancel("Nothing sent: speech was not ready yet");
                return;
            }
            m_Voice.FinishRecording();
            Current = State.Sending;
            Debug.Log($"CHRIS voice: recording finished after {(now - m_Started) * 1000:F0} ms; waiting for the transcript");
            Changed(null);
        }

        public void Cancel(string reason)
        {
            if (Current == State.Idle) return;
            m_Voice.CancelRecording();
            Current = State.Idle;
            Debug.Log("CHRIS voice: recording cancelled (" + reason + "); nothing sent");
            Changed(reason);
        }

        // Once per frame while the panel exists.
        public void Tick(float now, bool focused)
        {
            if (Current == State.Idle) return;
            if (!focused) { Cancel("Focus lost; nothing sent"); return; }
            if (m_Voice.Session.State == CHRISVoiceSession.Phase.Failed)
            {
                Current = State.Idle;
                Debug.Log("CHRIS voice: recording failed; nothing sent");
                Changed(m_Voice.Session.Error);
                return;
            }
            if (Current == State.Recording && now - m_Started >= AutoSendSeconds) Send(now);
        }

        void OnFinal(string transcript)
        {
            if (Current != State.Sending) return;
            // A final can arrive before this frame's focus tick (PR #6 review P2): check focus
            // right before publishing, so focus loss always means nothing is sent.
            if (!m_Focused())
            {
                Current = State.Idle;
                Debug.Log("CHRIS voice: transcript dropped (focus lost); nothing sent");
                Changed("Focus lost; nothing sent");
                return;
            }
            Current = State.Idle;
            string text = CHRISTestDisplay.CleanUtterance(transcript);
            var display = m_Display();
            if (text == null) { Changed("Nothing heard; nothing sent"); return; }
            string id = "u" + ++m_Count;
            display.Utterance(text, id, CHRISCommandGateway.Now);
            Heard = text;
            m_HeardVersion = display.Version;
            Debug.Log($"CHRIS voice: utterance {id} sent, {text.Length} characters");
            Changed(null);
        }

        void Changed(string notice)
        {
            Notice = notice;
            m_NoticeVersion = m_Display().Version;
            Version++;
        }
    }
}
