# CHRIS offline speech (Windows PCVR)

This implementation uses a thin C# binding to the official Vosk C API. Recognition
runs locally on a worker thread; only finalized text goes to the existing CHRIS
planning service. Audio and partial transcripts are transient. No speech route,
cloud speech provider, continuously listening mode, or spoken confirmation exists.

## Choice and limits

- [Vosk's official Unity guidance](https://alphacephei.com/vosk/unity) provides a
  C#/Mono sample with streaming partial and final recognition. The inspected sample
  revision is `6cc1d5a2a2837e570e32eec4f0ada383a5e94d04`; the sample project is not
  imported. The bundled native library is the official Windows x64 **0.3.45**
  release. Newer inspected Vosk releases do not publish a Windows x64 artifact.
- The [small US English 0.15 model](https://alphacephei.com/vosk/models) is the
  development default: about 40 MB downloaded. Vosk documents roughly 300 MB
  runtime memory for small models. Other languages require a separately selected,
  tested, and pinned model. Accents, proper names, and brush names may need typing
  correction. Published benchmark accuracy is not headset acceptance evidence.
- [whisper.cpp's stream example](https://github.com/ggml-org/whisper.cpp/tree/master/examples/stream)
  was considered as another local option. Its official example repeatedly
  transcribes microphone windows and uses SDL2; Vosk's streaming API and official
  Unity sample offer a smaller integration for this bounded first version. No
  comparative accuracy/latency benchmark or claim is made.
- Record explicitly, then tap Finish. Capture is 16 kHz, downmixed to mono, with
  100 ms chunks and at most two seconds of queued audio. Recording is limited to
  60 seconds; reaching the limit cancels without submitting. Loading/finalizing
  has a 20-second timeout. A slow recognizer or missing device fails visibly and
  leaves typing available. The model loads on demand for each recording.
- Use the microphone selector to cycle Windows default and enumerated devices.
  Quest microphone routing over Link/Air Link, Windows privacy settings, live
  partial quality, model-load latency and finalization latency require user
  testing on the actual headset. Native input/build checks do not prove these.

## Reproducible preparation

From the Unity project root, with Python 3.12 or newer:

```powershell
python Support/Python/acquire-chris-voice.py
```

The script downloads the exact archives in `resources.json`, checks SHA-256 before
extracting, creates Windows-only plugin import settings, and copies license
notices. Resources go into ignored `Assets/Plugins/CHRISVoice` and
`Assets/StreamingAssets/CHRISVoice`; archives are cached under ignored `Build`.
Use `--cache-dir` to reuse verified archives. No acquisition occurs at runtime.

The Windows build preprocessor verifies the archive pins, every installed file
hash, and plugin import settings. The output includes the offline model and
licenses under `OpenBrush_Data/StreamingAssets/CHRISVoice`. Do not copy only the
EXE: keep the complete build directory together.

Vosk and the English model are Apache-2.0. The official Windows archive also
contains GCC runtime DLLs (GPL-3.0 with the GCC Runtime Library Exception) and
MinGW winpthreads. License texts are included in `licenses/` and copied into the
player; the existing DLLs are redistributed unchanged. Sources are available at
[Vosk v0.3.45](https://github.com/alphacep/vosk-api/tree/v0.3.45),
[GCC](https://gcc.gnu.org/git.html), and
[MinGW-w64](https://github.com/mingw-w64/mingw-w64).

The pinned Vosk Windows build recipe also links Kaldi, OpenFst, OpenBLAS 0.3.20
and CLAPACK 3.2.1 (including f2c). Their upstream license notices are included.
The recipe uses moving branches for Kaldi/OpenFst; the official binary archive
hash pins what is distributed here, rather than claiming a reproducible source
rebuild of those upstream dependencies.

## Review and execution

Finish automatically submits finalized text for planning, not execution. The
service supplies one summary of its exact validated list of one to five actions.
The native UI freezes that summary with its approval. Longer summaries have
Previous/Next pages; Confirm becomes available after every page is viewed.
Editing, re-recording, changing microphones or pressing STOP invalidates pending
input and approval. Replacement planning waits for the old task's cancellation
acknowledgment; late cancelled or superseded callbacks cannot submit a request.

The gateway validates the whole segment before applying anything. It dispatches
one action, verifies readback, then considers the next action on a later frame.
Stop/manual takeover/expiry stops remaining actions. Completed counts report only
verified actions; an uncertain mutation is `unverified`, never blindly replayed.
The Direct palette and its ownership contract are removed. Normal Open Brush
drawing and inputs, floating-panel interaction and local STOP buttons remain.

## Deterministic checks

Use Unity's **CHRIS / Verify native UI (Edit mode)** menu or batch entry point
`TiltBrush.TestCHRISNativeUI.Run`. This exercises the real native segment ledger
with in-memory action adapters, speech callback state without microphone/model
inference, correction/review guards, floating-panel interactions and rendered UI.
It exports native protocol fixtures under `Build/CHRISNativeUI`. User headset
acceptance remains a separate step before M3.
