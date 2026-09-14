# CHRIS native engineering

## Working checkout and ownership

Use this repository root on
`codex/chris-native-companion`. The former `Build/CHRISUI-worktree` checkout
has been retired after root verification and local evidence preservation.
Pixel owns native implementation
and shared Git operations, Iris owns design content, and Logic owns the Python
service. Coordinate Git changes before switching or retiring either checkout.
Do not push as part of local cleanup.

`design/chris/native-companion/` holds the approved v5 boards, exact image
prompts, and design notes. The PNGs are concepts; editor renders and headset
acceptance are separate evidence. CHRIS runtime code is in
`Assets/CHRIS/Runtime`, editor code and checks in `Assets/CHRIS/Editor`,
resource assets in `Assets/CHRIS/Resources/CHRIS`, and the icon shader in
`Assets/CHRIS/Shaders`. The editor-conditional gateway test host stays under
`Runtime/TestSupport` to preserve its existing runtime assembly membership.

Keep engineering guidance in this file, reusable helpers in `agent/scripts`,
generated evidence in ignored `agent/logs`, and failure captures in ignored
`agent/errs`. Preserve the upstream README, licenses, and project documentation.

## Behavior that cleanup must preserve

- The optional CHRIS panel opens from Labs. It floats independently, can be
  dragged, and faces the user's current position. Native menus and controller
  startup do not depend on constructing it.
- CHRIS owns only its generated materials. Native tooltip materials and shared
  fonts/textures survive closing the panel. Do not destroy materials found by
  walking every child renderer.
- Recording uses Record/Finish or the scoped non-drawing-hand joystick click.
  Retry replaces voice input where appropriate; outcome recovery never blindly
  replays commands. Keep the visible left/right hint synchronized with handedness.
- Preserve the complete ordered review text and values across pages, explicit
  Confirm, release gating, cancellation, expiry and local manual takeover.
  Stop stays on the CHRIS panel; the legacy More-menu Stop has been removed.
- Python owns speech credentials and models. The spoken-only introduction is
  `Is your commands are:`; it is not part of the displayed approval or digest.
- The CHRIS `view.move` contract forwards to native `user.move` behavior through
  `ApiMethods.MoveUserBy`, using fixed room/world XYZ and native units. Do not
  reinterpret values in head or sketch coordinates during cleanup.

## Upstream integration points

CHRIS uses the existing TiltBrush namespace and compilation boundaries.
Directory grouping does not require new generic interfaces or assembly splits.

| Upstream file | Required integration |
| --- | --- |
| `Assets/Scripts/API/ApiManager.cs` | Attach the native command gateway to the API host. |
| `Assets/Scripts/App.cs` | Allow editor checks to exercise internal runtime contracts through the existing editor assembly. |
| `Assets/Scripts/GUI/BasePanel.cs` | Reserve the CHRIS panel type. |
| `Assets/Scripts/GUI/PanelManager.cs` | Check availability and lazily construct the optional floating panel. |
| `Assets/Scripts/Input/UnityXRControllerInfo.cs` | Sample and scope the audited joystick recording shortcut. |
| `Assets/Prefabs/Panels/LabsPanel.prefab` | Provide the native CHRIS launcher. |

Shared native meshes, fonts, icon atlas and UI base classes remain in their
upstream locations. Preserve existing `.meta` GUIDs when moving CHRIS assets.
Keep resource loading at `Resources.Load("CHRIS/UI")`; Unity's `Resources` and
`Editor` special-folder semantics must remain intact.

## Verification and retained evidence

Use Unity `6000.6.0f1`. The editor entry point is
`TiltBrush.TestCHRISNativeUI.Run`, also available as **CHRIS > Verify native UI
(Edit mode)**. It runs the native assistance, voice, companion and UI checks
and creates layout renders. Do not run `CHRISCompanionAssets.ConfigureAndVerify`
for routine verification: Configure rewrites authored assets.

Run `./agent/scripts/verify-native-ui.ps1` from PowerShell; `-UnityPath` can
override the editor executable. The helper resolves this checkout from its own
location, rejects a second editor for the same project, and records its editor
log and exit code in `agent/logs/native-ui-runs/<timestamp>/`. Current layout
renders and protocol fixtures go to `agent/logs/native-ui/`.

The approved directory relocation passed all 30 native editor checks. All 11
preview PNGs matched the pre-move root renders byte-for-byte, with no source
snapshot differences. Existing file GUIDs and runtime content were preserved;
only editor asset/fixture paths and generated-output placement changed.

The pre-consolidation source `48760d294d50ef4a1cdd7992cba3752e87ccd79b`
passed 30 native checks and produced the Windows/OpenXR player at
`Build/CHRIS-M2-final-polish-20260914/OpenBrush.exe`. Evidence is retained in
`Build/CHRISFinalPolishReview/`. Its managed assembly SHA256 is
`008771a89466d12d832ea560f484a4cafaac8ffe049344582ff9a467aeadbe3e`.

The user reported that the functional acceptance sequence passed for earlier
source `0b7dae2e3e1e93ee3fdf73c3428bc559d54cc828`; keep its player at
`Build/CHRIS-M2-hover-fix-20260914/` as the accepted backup. This does not establish
headset acceptance of later polish or a quantitative usability study.

Checkpoint `c42330f488ce83c68d1e88df7c304393741d5b8e` preserves the preexisting
font state used by verified players. Do not revert that font as generated dirt.
`checkpoint/chris-root-20260914` preserves the earlier root implementation,
which is patch-equivalent to retained commit `dab369e3`.
`checkpoint/chris-design-20260914` preserves all 15 Iris originals; only the
four current v5 files are imported into `design/`.

`agent/logs/consolidation/` contains the checkpoint manifest, original file
hashes, the verified 566-file root review archive, the asset move map and the
39-file archive of the retired worktree's local evidence. Iris's task now shares
this checkout. The old app-managed detached worktree remains retained.
`removed-local-branches.json` records the nine removed local branches and the
commits/tags that preserve their work. Delete local branches only after proving
the work is reachable or equivalent and no worktree uses them; retain checkpoint
tags. Never delete remote branches as part of this cleanup.

Unity builds can rewrite project settings, font and generated shader assets.
Snapshot the actual working state before a build, preserve generated outputs,
and restore only known build-mutated files to their snapshot. Verify the final
source diff before recording a build as clean.

Use `python agent/scripts/preserve-unity-state.py save agent/logs/<run>/snapshot`
before a run and `check` with that same directory afterwards. `restore` archives
and restores only the explicitly listed Unity-generated file changes; it fails
if any other source hash or Git status differs. Newly generated build files must
be inspected and archived separately before removal. Never restore over another
agent's concurrent edits; keep shared-checkout writes paused during verification.

## Cloud voice (Windows PCVR)

Unity captures microphone audio and plays approval readouts. The CHRIS Python
service owns credentials, exact model selection and structured logging. This
replaces Vosk; there is no local-model fallback. Use Record/Finish; re-record to replace a request.
Only finalized text starts planning; only explicit Confirm authorizes execution.

### Transcription

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

### Spoken confirmation

**Speech On/Off** defaults On and remembers the local choice. A fresh valid
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

### Controller shortcut

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

### Voice verification

No speech binaries/model downloads are needed. Older checkouts must remove the
generated `Assets/Plugins/CHRISVoice` and `Assets/StreamingAssets/CHRISVoice`
directories; a preprocessor rejects these retired resources during builds.

Run `TiltBrush.TestCHRISNativeUI.Run` or the CHRIS editor menu. Checks use in-memory
command adapters, a mocked WebSocket, PCM samples and generation/shortcut state;
they do not launch a player, access a microphone or call a model. Headset
acceptance must cover account access, mic routing, phrase updates, exact readout,
Off/Stop/late audio, both handedness settings, Basic/Advanced, and grab/menu
conflicts. Physical comfort and recognition quality remain unverified before M3.


## Repository rules

- **HTTP API testing:** The Open Brush HTTP API is available only while the Open Brush application is running or the Unity Editor is in Play mode. Check this before testing HTTP commands.

- For every brush with noticeable visual differences from the old Unity version, excluding surface shaders, copy the old Unity shader and make only the minor changes required to support URP.
- For brush visual-parity work and screenshot analysis, use the normal `m_Material` path and ignore `m_TestingMaterial`. Do not infer that `m_TestingMaterial` is the active material, a URP migration target, or suitable for repurposing. Verify the material actually used at runtime when material selection is relevant.
- For old-versus-new brush screenshot prioritisation, run `.venv-ssim\Scripts\python.exe Support\Python\compare-brush-screenshots.py --old-dir ..\open-brush-fast\Support\Screenshots\brushes-postfx-disabled --new-dir Support\Screenshots\brushes-postfx-disabled`. The script reports unchanged full-image RGB SSIM and prioritises using SSIM averaged near the dilated union of rendered brush pixels. Do not return to full-frame pixel MAE. Create the environment with `uv venv .venv-ssim --python 3.14` and install it with `uv pip install --python .venv-ssim\Scripts\python.exe -r Support\Python\requirements-brush-screenshots.txt` if it is missing.
- Unity editor logs for this project are project-local: `Logs/Editor.log`, with `Logs/AssetImportWorkerHW*.log` and `Logs/shadercompiler-*.log` alongside it. The `C:/Users/andyb/AppData/Local/Unity/Editor/Editor.log` path can be stale here - check mtime before trusting it.
- **UnityGLTF shader variants:** The empty/no-keyword variant is required and must be retained when filtering a Shader Variant Collection. For the current UnityGLTF package, the audited importer-reachable URP-only set contains 384 PBRGraph entries plus 8 UnlitGraph entries (392 total), including each empty set. It retains alpha test, texture transforms, instancing, clearcoat, iridescence, sheen, specular, transmission, and dispersion while excluding only keyword states the importer cannot produce. Do not substitute the older 75-entry graph subset as complete coverage; re-audit importer keyword behavior when UnityGLTF changes.
