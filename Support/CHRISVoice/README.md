# CHRIS cloud voice (Windows PCVR)

Unity captures microphone audio and plays approval readouts. The CHRIS Python
service owns credentials, exact model selection and structured logging. This
replaces Vosk; there is no local-model fallback. Use Record/Finish; re-record to replace a request.
Only finalized text starts planning; only explicit Confirm authorizes execution.

## Transcription

Exact `gpt-4o-transcribe` runs through a Realtime transcription session. Phrase
updates arrive during the manual recording, often after pauses. This does not
promise word-by-word text during uninterrupted speech. VAD completion never ends
the recording or starts planning. Finish flushes and waits for one ordered final.
The [model page](https://developers.openai.com/api/docs/models/gpt-4o-transcribe)
lists Realtime transcription support; the current
[live guide](https://developers.openai.com/api/docs/guides/realtime-transcription)
also describes newer models, which this integration does not substitute.

The fixed loopback WebSocket is `ws://127.0.0.1:8765/voice/transcribe`:

- First text frame: `{"type":"start","session_id":"<fresh GUID>"}`.
- Wait for matching `ready`, then binary PCM16 little-endian mono 24 kHz,
  at most 4,800 bytes/frame (100 ms), at most two seconds of queued audio.
- Finish: `{"type":"finish","session_id":"<same GUID>"}`. Cancel/disconnect
  closes the stream. There is no reconnect or audio replay.
- Server events carry `type`, `session_id`, and full-so-far `text` for `partial`
  and `final`; errors carry a safe `code`. Only one final after Finish is accepted.

Recording is limited to 60 seconds and 2,000 transcript characters. Startup times
out after 10 seconds; finalization after 20. Failures stop capture and show connection/microphone guidance for re-recording. Audio and partial text are transient. Provider availability, quality
and latency need live acceptance with the configured account and headset.

## Spoken confirmation

**AI voice: On/Off** defaults On and remembers the local choice. A fresh valid
review requests one readout of its complete immutable summary, including every
ordered action/value and review page. Polling/redraw and Off/On do not replay an
already attempted approval. Confirm never waits for speech to end.

`POST /voice/speech` sends `speech_id`, `task_id`, `approval_id`, `action_digest`
and exact `summary`. Python checks identity, validity and summary before and
after synthesis using exact `gpt-4o-mini-tts`. The response is `audio/pcm`, mono
signed16 little-endian 24 kHz with matching `X-Speech-Id`. Unity bounds it to
2,880,000 bytes (60 seconds) before decoding. Invalid, oversized or stale audio
never plays. The full summary is sent; failures never silently truncate it.
See the official [TTS guide](https://developers.openai.com/api/docs/guides/text-to-speech).

Off stops audio immediately. Stop, correction, new recording, panel close,
approval change and expiry stop playback and discard late responses. Pending
synthesis is aborted and cancelled with `POST /voice/speech/<speech_id>/cancel`
and `{}`. The native selection sound plays before capture; capture waits for its
duration plus a 250 ms quiet interval. Haptics mark actual listening and Finish;
the Finish sound plays after capture stops. Device acoustic echo still needs testing.

## Controller shortcut

On **Quest/OpenXR**, while CHRIS is open, click the **non-drawing hand's joystick**
(Wand role) to start. Release, then click again to finish. Holding never repeats.
Record/Finish remains available on the panel, including other controller profiles.

The source audit found no command binding for this click: `ThumbButton` maps to
`VrInput.Thumbstick` and `Directional`; context/reset/duplicate use primary buttons,
menu/redo use secondary, and grabbing uses grip. Stick touch and axes still navigate
the native panels. The shortcut leaves grip, trigger and face buttons unchanged.
Steam Frame has an additional PadButton alias, so support is restricted to the
audited Oculus Touch profile or detected Oculus Touch hardware.

Opening, closing, hand-role changes and tracking recovery require a held joystick
click to release before another action. This applies in Basic/Advanced and follows
handedness rather than fixed left. Confirm still checks physical grip/trigger
release. There is no grip reservation or global grab delay.

## Build and verification

No speech binaries/model downloads are needed. Older checkouts must remove the
generated `Assets/Plugins/CHRISVoice` and `Assets/StreamingAssets/CHRISVoice`
directories; a preprocessor rejects these retired resources during builds.

Run `TiltBrush.TestCHRISNativeUI.Run` or the CHRIS editor menu. Checks use in-memory
command adapters, a mocked WebSocket, PCM samples and generation/shortcut state;
they do not launch a player, access a microphone or call a model. Headset
acceptance must cover account access, mic routing, phrase updates, exact readout,
Off/Stop/late audio, both handedness settings, Basic/Advanced, and grab/menu
conflicts. Physical comfort and recognition quality remain unverified before M3.
