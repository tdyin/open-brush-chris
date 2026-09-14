// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace TiltBrush
{
    public class TestCHRISCloudVoice
    {
        [Test]
        public void CaptureWaitsForTheCueAndQuietIntervalAndStopCancelsPendingCapture()
        {
            var obj = new GameObject("CHRIS cue timing");
            try
            {
                var voice = obj.AddComponent<CHRISVoiceInput>();
                int deviceQueries = 0, cues = 0;
                voice.Devices = () => { deviceQueries++; return Array.Empty<string>(); };
                voice.PlayStartCue = () => { cues++; return 2; };
                long id = voice.Session.Begin();
                float before = Time.realtimeSinceStartup;
                voice.Receive(id, "ready", "");
                TestCHRISAssistance.Call(voice, "Update");
                float captureAt = (float)typeof(CHRISVoiceInput).GetField("m_CaptureNotBefore",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(voice);
                Assert.That(captureAt, Is.GreaterThanOrEqualTo(before + 2.25f));
                Assert.That(deviceQueries, Is.Zero, "No microphone access while the cue plays");
                Assert.That(cues, Is.EqualTo(1));
                voice.CancelRecording();
                TestCHRISAssistance.Set(voice, "m_CaptureNotBefore", 0f);
                TestCHRISAssistance.Call(voice, "Update");
                voice.Receive(id, "ready", "");
                Assert.That(deviceQueries, Is.Zero);
                Assert.That(cues, Is.EqualTo(1), "Late readiness cannot play another cue or start capture");
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }

        [Test]
        public void ReadoutRequiresTheExactLiveApprovalAndCorrectionInvalidatesItsGeneration()
        {
            var obj = new GameObject("CHRIS exact readout authority");
            try
            {
                var host = obj.AddComponent<CHRISGatewayTestHost>(); host.Initialize();
                var model = obj.AddComponent<CHRISPanel>(); model.Gateway = host;
                TestCHRISAssistance.Call(model, "Start");
                model.Speech.Session.SetEnabled(true);
                var popup = obj.AddComponent<CHRISNativePopup>();
                var stateType = typeof(PopUpWindow).GetNestedType("State", BindingFlags.NonPublic);
                TestCHRISAssistance.Set(popup, "m_CurrentState", Enum.Parse(stateType, "Standard"));
                TestCHRISAssistance.Property(model, "Popup", popup);
                TestCHRISAssistance.Property(model.Assistance, "TaskId", null);
                TestCHRISAssistance.Property(model.Assistance, "PendingRequest", null);
                TestCHRISAssistance.Property(model.Assistance, "CancelWanted", false);
                var task = TestCHRISAssistance.TaskFor(host);
                TestCHRISAssistance.Property(model.Assistance, "Task", task);
                model.Refresh();
                string key = model.Speech.EligibleReviewKey();
                Assert.That(key, Is.Not.Null);
                task["summary"] = "A different spoken value";
                Assert.That(model.Speech.EligibleReviewKey(), Is.Null);
                task["summary"] = model.ReviewText;
                TestCHRISAssistance.Property(model.Assistance, "CancelWanted", true);
                Assert.That(model.Speech.EligibleReviewKey(), Is.Null);
                TestCHRISAssistance.Property(model.Assistance, "CancelWanted", false);
                model.ReviewedApproval["expires_at"] = CHRISCommandGateway.Now - 1;
                task["approval"] = model.ReviewedApproval.DeepClone();
                Assert.That(model.Speech.EligibleReviewKey(), Is.Null);
                task = TestCHRISAssistance.TaskFor(host);
                TestCHRISAssistance.Property(model.Assistance, "Task", task); model.Refresh();
                key = model.Speech.EligibleReviewKey(); Assert.That(key, Is.Not.Null);
                model.Speech.Session.Observe(key); Assert.That(model.Speech.Session.TryBegin(), Is.True);
                long generation = model.Speech.Session.Generation;
                model.BeginCorrection();
                Assert.That(model.Speech.Session.Accepts(generation, key), Is.False);
                Assert.That(model.Speech.EligibleReviewKey(), Is.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(obj); }
        }

        // In-memory WebSocket peer; exercises the production relay without an HTTP server/provider.
        sealed class SpeechPeer : WebSocket
        {
            readonly ConcurrentQueue<byte[]> m_Incoming = new ConcurrentQueue<byte[]>();
            readonly SemaphoreSlim m_Available = new SemaphoreSlim(0);
            public readonly List<string> Sent = new List<string>();
            public string Fault;
            string m_Id;
            WebSocketState m_State = WebSocketState.Open;
            public override WebSocketCloseStatus? CloseStatus => null;
            public override string CloseStatusDescription => null;
            public override string SubProtocol => null;
            public override WebSocketState State => m_State;
            public override void Abort() { m_State = WebSocketState.Aborted; }
            public override void Dispose() { m_Available.Dispose(); }
            public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken token) => Task.CompletedTask;
            public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token) => Task.CompletedTask;
            void Reply(string type, string text = "")
            {
                m_Incoming.Enqueue(Encoding.UTF8.GetBytes(new JObject {
                    ["type"] = type, ["session_id"] = Fault == "session" ? "wrong" : m_Id, ["text"] = text }.ToString()));
                m_Available.Release();
            }
            public override Task SendAsync(ArraySegment<byte> bytes, WebSocketMessageType type, bool end, CancellationToken token)
            {
                if (type == WebSocketMessageType.Binary)
                {
                    Assert.That(bytes.Count, Is.EqualTo(4800));
                    Sent.Add("audio");
                    Reply("partial", "make the brush blue");
                }
                else
                {
                    var control = JObject.Parse(Encoding.UTF8.GetString(bytes.Array, bytes.Offset, bytes.Count));
                    m_Id = (string)control["session_id"];
                    string command = (string)control["type"];
                    Sent.Add(command);
                    if (command == "start") { Reply("ready"); if (Fault == "early-final") Reply("final", "must not submit"); }
                    if (command == "finish") Reply("final", "make the brush blue");
                }
                return Task.CompletedTask;
            }
            public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
            {
                await m_Available.WaitAsync(token);
                m_Incoming.TryDequeue(out byte[] bytes);
                Array.Copy(bytes, 0, buffer.Array, buffer.Offset, bytes.Length);
                return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, true);
            }
        }

        [Test]
        public void CloudWireStreamsPartialsBeforeExplicitFinishAndRejectsWrongOrEarlyFinals()
        {
            using (var audio = new BlockingCollection<short[]>(20))
            using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (var peer = new SpeechPeer())
            {
                audio.Add(new short[2400]);
                var events = new List<string>();
                Task.Run(() => new CHRISCloudTranscription().RunConnected(peer, audio, cancel.Token, (kind, text) =>
                {
                    events.Add(kind);
                    if (kind == "partial")
                    {
                        Assert.That(audio.IsAddingCompleted, Is.False, "Partial arrived during recording");
                        Assert.That(text, Is.EqualTo("make the brush blue"));
                        audio.CompleteAdding(); // Explicit Finish only after a live partial.
                    }
                })).GetAwaiter().GetResult();
                Assert.That(events, Is.EqualTo(new[] { "ready", "partial", "final" }));
                Assert.That(peer.Sent, Is.EqualTo(new[] { "start", "audio", "finish" }));
            }
            foreach (string fault in new[] { "session", "early-final" })
                using (var audio = new BlockingCollection<short[]>(20))
                using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                using (var peer = new SpeechPeer { Fault = fault })
                {
                    int finals = 0;
                    Assert.Throws<IOException>(() => Task.Run(() => new CHRISCloudTranscription().RunConnected(peer, audio, cancel.Token,
                        (kind, text) => { if (kind == "final") finals++; })).GetAwaiter().GetResult());
                    Assert.That(finals, Is.Zero);
                }
        }

        [Test]
        public void SpeechPreferencePersistsAndToggleInvalidatesPendingAudio()
        {
            bool had = PlayerPrefs.HasKey(CHRISConfirmationSpeech.Preference);
            int saved = PlayerPrefs.GetInt(CHRISConfirmationSpeech.Preference, 1);
            var first = new GameObject("CHRIS readout preference");
            var second = new GameObject("CHRIS readout restored preference");
            try
            {
                PlayerPrefs.DeleteKey(CHRISConfirmationSpeech.Preference);
                var speech = first.AddComponent<CHRISConfirmationSpeech>();
                TestCHRISAssistance.Call(speech, "Awake");
                Assert.That(speech.SpeechEnabled, Is.True);
                speech.Session.Observe("review"); speech.Session.TryBegin();
                long generation = speech.Session.Generation;
                speech.Toggle();
                Assert.That(speech.Session.Accepts(generation, "review"), Is.False);
                var restored = second.AddComponent<CHRISConfirmationSpeech>();
                TestCHRISAssistance.Call(restored, "Awake");
                Assert.That(restored.SpeechEnabled, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first); UnityEngine.Object.DestroyImmediate(second);
                if (had) PlayerPrefs.SetInt(CHRISConfirmationSpeech.Preference, saved); else PlayerPrefs.DeleteKey(CHRISConfirmationSpeech.Preference);
                PlayerPrefs.Save();
            }
        }

        [Test]
        public void PcmPreservesSignedSamplesAndRejectsMalformedOrOversizedAudio()
        {
            short[] values = { short.MinValue, -1, 0, 1, short.MaxValue };
            byte[] bytes = CHRISAudio.Encode(values);
            Assert.That(bytes, Is.EqualTo(new byte[] { 0, 128, 255, 255, 0, 0, 1, 0, 255, 127 }));
            float[] decoded = CHRISAudio.Decode(bytes);
            for (int i = 0; i < values.Length; i++) Assert.That(decoded[i], Is.EqualTo(values[i] / 32768f));
            foreach (byte[] invalid in new[] { Array.Empty<byte>(), new byte[3], new byte[CHRISAudio.MaxPlaybackBytes + 2] })
                Assert.Throws<ArgumentException>(() => CHRISAudio.Decode(invalid));
        }

        [Test]
        public void ReadoutIsOncePerReviewAndLateAudioCannotSurviveInvalidation()
        {
            var state = new CHRISReadoutSession();
            Assert.That(state.Enabled, Is.True);
            state.Observe("approval-one"); Assert.That(state.TryBegin(), Is.True);
            long first = state.Generation;
            for (int i = 0; i < 5; i++) { state.Observe("approval-one"); Assert.That(state.TryBegin(), Is.False); }
            Assert.That(state.Accepts(first, "approval-one"), Is.True);
            state.SetEnabled(false);
            Assert.That(state.Accepts(first, "approval-one"), Is.False);
            state.Observe("approval-one"); Assert.That(state.TryBegin(), Is.False);
            state.SetEnabled(true); state.Observe("approval-one"); Assert.That(state.TryBegin(), Is.False);
            state.Observe("approval-two"); Assert.That(state.TryBegin(), Is.True);
            long second = state.Generation;
            state.Cancel(); Assert.That(state.Accepts(second, "approval-two"), Is.False);
            state.Observe("approval-two"); Assert.That(state.TryBegin(), Is.False);
            state.Observe("approval-three"); Assert.That(state.TryBegin(), Is.True);
            long third = state.Generation;
            state.Observe(null); Assert.That(state.Accepts(third, "approval-three"), Is.False);
        }

        [Test]
        public void PanelJoystickClickRequiresFreshPressAndLeavesOtherControlsAlone()
        {
            foreach (bool rightHand in new[] { false, true })
            {
                var shortcut = new CHRISVoiceShortcut();
                Assert.That(shortcut.Sample(false, true, rightHand, false), Is.False);
                Assert.That(shortcut.Sample(false, true, rightHand, true), Is.False);
                Assert.That(shortcut.SuppressClick, Is.False, "Native input is unchanged outside CHRIS");
                Assert.That(shortcut.Sample(true, true, rightHand, true), Is.False, "Opening while held cannot record");
                shortcut.Sample(true, true, rightHand, false);
                Assert.That(shortcut.Sample(true, true, rightHand, true), Is.True);
                foreach (VrInput alias in new[] { VrInput.Thumbstick, VrInput.Directional })
                    Assert.That(shortcut.Suppresses(alias), Is.True);
                foreach (VrInput input in new[] { VrInput.Grip, VrInput.Trigger, VrInput.Touchpad,
                    VrInput.Button01, VrInput.Button02, VrInput.Button03, VrInput.Button04, VrInput.Button05, VrInput.Button06 })
                    Assert.That(shortcut.Suppresses(input), Is.False, "Only the joystick click is reserved");
                for (int i = 0; i < 3; i++) Assert.That(shortcut.Sample(true, true, rightHand, true), Is.False);
                shortcut.Sample(true, true, rightHand, false);
                Assert.That(shortcut.Sample(true, true, rightHand, true), Is.True, "Release then click again to finish");
            }
        }

        [Test]
        public void ClosingRoleSwapAndTrackingRecoveryQuarantineHeldJoystickClicks()
        {
            foreach (string transition in new[] { "close", "swap", "tracking" })
            {
                var shortcut = new CHRISVoiceShortcut();
                shortcut.Sample(true, true, false, false);
                shortcut.Sample(true, true, false, true);
                bool scope = transition != "close", right = transition == "swap";
                if (transition == "tracking") shortcut.Sample(scope, false, right, false);
                Assert.That(shortcut.Sample(scope, true, right, true), Is.False);
                Assert.That(shortcut.SuppressClick, Is.True, transition);
                shortcut.Sample(scope, true, right, false);
                Assert.That(shortcut.SuppressClick, Is.True, "Consume the held click's release edge");
                shortcut.Sample(scope, true, right, false);
                Assert.That(shortcut.SuppressClick, Is.EqualTo(scope));
                Assert.That(shortcut.Sample(scope, true, right, true), Is.EqualTo(scope));
            }
        }
    }
}
