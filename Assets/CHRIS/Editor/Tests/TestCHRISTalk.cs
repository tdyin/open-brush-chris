// Copyright 2026 The Open Brush Authors
// Licensed under the Apache License, Version 2.0.
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace TiltBrush
{
    // D129/D133 tap-to-talk on the CHRIS panel, with the existing voice input driven by its own
    // worker messages: no microphone, socket or provider.
    public class TestCHRISTalk
    {
        static (GameObject obj, CHRISVoiceInput voice, CHRISTestDisplay display, CHRISTalk talk) Make()
        {
            var obj = new GameObject("CHRIS talk test");
            var voice = obj.AddComponent<CHRISVoiceInput>();
            voice.Devices = () => new CHRISMicrophoneDevice[0];
            var display = new CHRISTestDisplay();
            return (obj, voice, display, new CHRISTalk(voice, () => display));
        }

        static JArray Events(CHRISTestDisplay display) => (JArray)display.Events(0)["events"];

        [Test]
        public void TwoClicksSendOneCleanUtterance()
        {
            var (obj, voice, display, talk) = Make();
            try
            {
                talk.Click(0, CHRISTalk.BusyNotice);
                Assert.That(talk.Current, Is.EqualTo(CHRISTalk.State.Idle), "busy: nothing starts");
                Assert.That(talk.NoticeText(display.Version), Is.EqualTo("Wait for the current task"));

                talk.Click(0, null);
                Assert.That(talk.Current, Is.EqualTo(CHRISTalk.State.Recording));
                Assert.That(talk.Phase, Is.EqualTo("Connecting"));
                long id = voice.Session.Id;
                voice.Receive(id, "listening", "");
                Assert.That(talk.Phase, Is.EqualTo("Listening"));
                voice.Receive(id, "partial", "select the");
                Assert.That(talk.HeardText(display.Version), Is.EqualTo("select the"), "live transcript");
                Assert.That(Events(display), Is.Empty, "nothing is sent while recording");

                talk.Click(2, null);
                Assert.That(talk.Current, Is.EqualTo(CHRISTalk.State.Sending));
                voice.Receive(id, "final", "select the\n\tfire   brush ");
                Assert.That(talk.Current, Is.EqualTo(CHRISTalk.State.Idle));
                var events = Events(display);
                Assert.That(events.Count, Is.EqualTo(1));
                var utterance = (JObject)events[0];
                Assert.That((string)utterance["kind"], Is.EqualTo("utterance"));
                Assert.That((string)utterance["text"], Is.EqualTo("select the fire brush"));
                Assert.That((string)utterance["id"], Is.EqualTo("u1"));
                Assert.That(utterance["case"].Type, Is.EqualTo(JTokenType.Null));
                Assert.That(utterance["nonce"].Type, Is.EqualTo(JTokenType.Null));
                Assert.That(CHRISTestDisplay.ValidateEvent(utterance), Is.Null);
                Assert.That(talk.HeardText(display.Version), Is.EqualTo("select the fire brush"), "shown until the next display");
                display.Show(new JObject { ["lines"] = new JArray("Heard: select the fire brush"), ["nonce"] = "n1",
                    ["buttons"] = new JArray("approve", "decline"), ["cases"] = new JArray(), ["ttl_s"] = 30 }, 3);
                Assert.That(talk.HeardText(display.Version), Is.Null, "the session's review replaces it");
            }
            finally { Object.DestroyImmediate(obj); }
        }

        [Test]
        public void CancelStopFocusErrorsAndSilenceSendNothing()
        {
            var (obj, voice, display, talk) = Make();
            try
            {
                void Listening() { talk.Click(0, null); voice.Receive(voice.Session.Id, "listening", ""); }

                Listening();
                voice.Receive(voice.Session.Id, "partial", "draw a");
                talk.Cancel("Cancelled; nothing sent");
                Assert.That(talk.Current, Is.EqualTo(CHRISTalk.State.Idle));
                Assert.That(voice.Session.IsActive, Is.False, "the recording itself is cancelled");

                Listening();
                talk.Tick(1, false);
                Assert.That(talk.NoticeText(display.Version), Is.EqualTo("Focus lost; nothing sent"));

                // A second click while still connecting captured nothing.
                talk.Click(0, null);
                talk.Click(0.2f, null);
                Assert.That(talk.Current, Is.EqualTo(CHRISTalk.State.Idle));
                Assert.That(talk.NoticeText(display.Version), Is.EqualTo("Nothing sent: speech was not ready yet"));

                Listening();
                voice.Receive(voice.Session.Id, "error", "Speech unavailable. Check the CHRIS service and connection, then Retry.");
                talk.Tick(1, true);
                Assert.That(talk.Current, Is.EqualTo(CHRISTalk.State.Idle));
                StringAssert.StartsWith("Speech unavailable", talk.NoticeText(display.Version));

                Listening();
                talk.Click(1, null);
                voice.Receive(voice.Session.Id, "final", "   ");
                talk.Tick(2, true);
                Assert.That(talk.Current, Is.EqualTo(CHRISTalk.State.Idle), "silence ends the recording");

                Assert.That(Events(display), Is.Empty, "no event for cancel, focus loss, not ready, error or silence");
            }
            finally { Object.DestroyImmediate(obj); }
        }

        // PR #6 review P2: a final queued before the focus tick is not published after focus loss.
        [Test]
        public void AFinalAfterFocusLossIsNotPublished()
        {
            var obj = new GameObject("CHRIS talk focus test");
            try
            {
                var voice = obj.AddComponent<CHRISVoiceInput>();
                voice.Devices = () => new CHRISMicrophoneDevice[0];
                var display = new CHRISTestDisplay();
                bool focused = true;
                var talk = new CHRISTalk(voice, () => display, () => focused);
                talk.Click(0, null);
                voice.Receive(voice.Session.Id, "listening", "");
                talk.Click(1, null);
                Assert.That(talk.Current, Is.EqualTo(CHRISTalk.State.Sending));
                focused = false;
                voice.Receive(voice.Session.Id, "final", "select the fire brush");
                Assert.That(Events(display), Is.Empty, "delivered before the cancellation tick, still not sent");
                Assert.That(talk.Current, Is.EqualTo(CHRISTalk.State.Idle));
                Assert.That(talk.NoticeText(display.Version), Is.EqualTo("Focus lost; nothing sent"));
                talk.Tick(2, false);
                Assert.That(Events(display), Is.Empty);
            }
            finally { Object.DestroyImmediate(obj); }
        }

        [Test]
        public void LongRecordingsSendBeforeTheRelayCap()
        {
            var (obj, voice, display, talk) = Make();
            try
            {
                talk.Click(0, null);
                voice.Receive(voice.Session.Id, "listening", "");
                talk.Tick(57, true);
                Assert.That(talk.Current, Is.EqualTo(CHRISTalk.State.Recording));
                talk.Tick(58.5f, true);
                Assert.That(talk.Current, Is.EqualTo(CHRISTalk.State.Sending), "sent before the 60 s relay limit");
                voice.Receive(voice.Session.Id, "final", new string('a', 450));
                var text = (string)Events(display)[0]["text"];
                Assert.That(text.Length, Is.EqualTo(400), "at most 400 characters");
            }
            finally { Object.DestroyImmediate(obj); }
        }

        [Test]
        public void UtteranceTextAndEventRules()
        {
            Assert.That(CHRISTestDisplay.CleanUtterance(" a\r\nb\tc  "), Is.EqualTo("a b c"));
            Assert.That(CHRISTestDisplay.CleanUtterance("\n \t"), Is.Null);
            Assert.That(CHRISTestDisplay.CleanUtterance(null), Is.Null);
            var display = new CHRISTestDisplay();
            Assert.That(display.Utterance("hello", "u 1", 1), Is.False, "the id is an identifier");
            Assert.That(display.Utterance("", "u1", 1), Is.False);
            Assert.That(display.Utterance("hello", "u1", 1), Is.True);
            var start = new JObject { ["seq"] = 1, ["kind"] = "start", ["case"] = 10, ["nonce"] = null, ["at"] = 1.0 };
            Assert.That(CHRISTestDisplay.ValidateEvent(start), Is.Null);
            start["text"] = "x";
            Assert.That(CHRISTestDisplay.ValidateEvent(start), Is.Not.Null, "start carries no text");

            var headset = new CHRISMicrophoneDevice("b", "Headset Microphone (Oculus Virtual Audio Device)");
            var realtek = new CHRISMicrophoneDevice("a", "Microphone (Realtek(R) Audio)");
            Assert.That(CHRISVoiceInput.HeadsetMicrophone(new[] { realtek, headset }), Is.SameAs(headset));
            Assert.That(CHRISVoiceInput.HeadsetMicrophone(new[] { realtek }), Is.Null, "else the Windows default");

            Assert.That(CHRISNativePopup.TalkHeading("Listening", null), Is.EqualTo("Listening  ·  Speech goes to OpenAI"));
            Assert.That(CHRISNativePopup.TalkHeading(null, "Cancelled; nothing sent"), Is.EqualTo("Cancelled; nothing sent"));
            Assert.That(CHRISNativePopup.TalkBody("draw a line", "Headset Microphone"), Is.EqualTo("Heard: draw a line\nMic: Headset Microphone"));
        }
    }
}
