# CHRIS native engineering

## Working checkout and ownership

Use this repository root at `C:/Users/Yin/Dev/open-brush-chris`, currently on
`main-chris`, restored from `e6775e838741f7691cae64fd9c2cae9a1d61710c`, with the
v0.1.3 implementation described below. The former `Build/CHRISUI-worktree` checkout
has been retired after root verification and local evidence preservation.
Pixel owns native implementation and build work; Logic owns the Python service;
Atlas coordinates and reviews. Iris, Quill and Vector are inactive; preserve their
existing work. Keep one writer per file and coordinate repository operations.
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
- The floating headset panel shows only mapping/control status and local Stop.
  It remains optional and nonmodal. Its Brush/Wand role hint follows handedness.
  The legacy More-menu Stop has been removed.
- Voice and mapping authority endpoints remain available for external clients.
  Opening the status panel does not start microphone, speech or review work.
- Python owns speech credentials and models. The spoken-only introduction is
  `Is your commands are:`; it is not part of the displayed approval or digest.
- The CHRIS `view.move` contract forwards to native `user.move` behavior through
  `ApiMethods.MoveUserBy`, using fixed room/world XYZ and native units. Do not
  reinterpret values in head or sketch coordinates during cleanup.
- Keyboard/mouse mapping (v0.1.1): `C:\Dev\chris` owns the schema (`schemas/`),
  the shared cases and the rules in its `agent/README.md` ("v0.1.1 mapping
  execution path"). `CHRISInputMapping` must give the same result code as its
  generated, gitignored `schemas/v0.1.1/fixtures/cases.json`, which the editor
  check reads in place (sibling `../chris` or `CHRIS_REPO`). If the folder is
  missing, run `uv run python -m chris.core.mapping_cases` in that repository;
  Unity never runs Python. The mapping loads
  from `<persistentDataPath>/CHRIS/active-mapping.json` at startup and on the
  `chris.mapping.reload` API command, with no file watcher and no Python.
  With no active mapping, the brush trigger path is exactly the native one.
  A proposed mapping waits in memory until desktop and XR display focus are
  present, no stroke is in progress, and neither the old nor proposed mapping's
  inputs are held. Only then does native persist its exact approved bytes and
  switch runtime mapping. Escape Stop cancels pending work and deactivates the
  runtime mapping; after mapped drawing or focus loss, a physical trigger held
  through the transition needs release and a fresh press. The logical Brush
  role follows Open Brush's handedness swap. A failed reload preserves the
  previous active mapping. `GET /chris/mapping/status` reports exact active and
  pending JSON/digests plus session and mapping revision; the latest request
  receipt (`last_request_id`/`last_request_result`) is separate from active and
  pending ownership IDs, so a lost apply reply is checked by status rather than
  blindly retried. A duplicate request is rejected without replacing that
  receipt.
  If the default generated fixture directory is inaccessible, set the test-only
  `CHRIS_MAPPING_TEST_FIXTURES` environment variable to a readable directory
  containing `cases.json` and its case files. The current byte-matched local copy
  is `agent/logs/mapping-shared-cases-20260925/fixtures/`; native checks can use it
  without touching the original ignored fixture directory. The mapping wire
  regression reads `mapping_wire.json` there when present, otherwise from the
  sibling CHRIS repository's checked-in `tests/core/fixtures/` directory.
  The committed Enter Play Mode Options skip the domain and scene reloads, so
  CHRIS static state resets itself on `SubsystemRegistration`. Startup logs
  `HttpListener listening on http://127.0.0.1:40074/`, `API commands
  registered: N; chris.mapping.reload present` and a `CHRIS mapping` line
  naming the full path it checked.

## Two-hand keyboard and mouse control (v0.1.2)

### v0.1.3 update (October 2)

The optional v0.1.3 schema keeps the two-hand contract and recovery gates while
combining position and rotation in F2 control mode. Mouse XY moves the selected
hand, Q/E controls depth, W/S tilts up/down, A/D turns left/right, and arrows move
the viewpoint. In F1 menu mode arrows still navigate without viewpoint movement.
R/F rolls the selected controller. F3 and the Brush stick axis are unbound in the new standard
preset; all other bindings below remain. Custom roll bindings are optional and
must not overlap position inputs. `mode_rotation` is not part of v0.1.3.

Use the backend's `schemas/v0.1.3/openbrush_keyboard-mouse_two-hand.default.json`
through explicit saved-file/reload with a compatible player. Existing v0.1.1 and
v0.1.2 files retain their behavior, and the bundled v0.1.2 preset is unchanged.
Offline verification: 89 native regression methods pass, including 16 shared v0.1.3
validation fixtures, opposite R/F roll, combined selected-hand motion, arrow movement
and recovery checks. Evidence: `agent/logs/native-ui-runs/20261002-223537/` (exit 0).
The snapshot at `agent/logs/v013-roll-20261002/source-before-editor/` reports zero differences across
10,322 source paths and unchanged Git status after Unity. Backend verification
passes 697 tests and Ruff (backend evidence supplied by Atlas; Pixel did not rerun it).
Pixel reviewed the existing native implementation against the approved controls and
found no concrete issue requiring a runtime change. A fresh native run passed all 89
methods at `agent/logs/native-ui-runs/20261002-223950/`.

Fresh Windows/OpenXR player: `Build/CHRIS-v013-pixel-20261002-223950/OpenBrush.exe`.
Build result Success, editor exit 0; log and included source patch are under
`agent/logs/builds/v013-pixel-20261002-223950/`. The previous
`Build/CHRIS-local-20261002/` player remains intact; `Build/CHRIS-current` points to
the new folder. Assembly-CSharp.dll SHA256:
`8f3c5d3ada6c21ac785ae76773193433a2622605247e7219d41b5275289c9f5c`.
The helper archived and restored 17 known generated paths. Full snapshot check at
`agent/logs/pixel-v013-20261002/source-before-build/` found zero differences across
10,322 paths and unchanged Git status before this documentation update.

Simulator gate: **NOT RUN**. The Operator layer manifest
`sdk/package/Editor/MetaXROperator/x64/XrApiLayer_METAX_operator.json` and Meta XR
Simulator runtime distribution are absent under both `C:/Users/Yin/Dev/xr-operator`
and the historical `C:/Dev/xr-operator`. The retained gate also launches the player
and changes the saved mapping, which this assignment excludes. Prerequisite evidence:
`agent/logs/pixel-v013-20261002/simulator-prerequisites.json`. No tooling installed
or restored, player launched, saved mapping changed, or LocalLow restore attempted.
This is a compiled, offline-verified player, not an acceptance-ready handoff.
Owner headset acceptance on this PC remains pending for both handedness settings,
connected/asleep controllers, simultaneous hand position/rotation/roll, F1 menu
isolation, arrow viewpoint motion, and focus/release/Stop/hand-back timing.
The owner authorized committing, pushing and opening a PR after this verification;
hardware acceptance remains unconfirmed. No model call accompanied this work.
Setup diagnosis found a manually copied `active-mapping.json.json`; subsequent
physical-volume inspection verified the correct `CHRIS/active-mapping.json` now
matches the v0.1.3 preset byte-for-byte (2,875 bytes). Pixel did not write or reload
it. Startup deliberately retains physical control: focus the player window and
press F2 for pose control, then 1/2 to select Brush/Wand. F1 remains menu control.

### Existing v0.1.2 behavior

The optional v0.1.2 profile controls logical Brush and Wand independently while
the XR controllers remain connected, including when they are asleep. Fully
disconnected controllers are not supported. v0.1.1 files still load unchanged;
they are never rewritten into v0.1.2. Unity validates v0.1.2 independently of
the Python service, using the schema and shared cases in
`C:\Dev\chris\schemas\v0.1.2\` and
`C:\Dev\chris\tests\core\fixtures\mapping_v012\` for editor checks only. The
player bundles a byte-identical copy of the default preset at
`Assets/CHRIS/Resources/CHRIS/TwoHandDefault.json`; it has no runtime dependency
on the sibling repository.

F1 explicitly enters keyboard UI pointer control, including after Stop or with
no active mapping; it does not open a CHRIS popup. Mouse movement and arrow keys
point at native menus, and mouse left or Enter activates the hovered native
control. They cannot draw in UI mode. F6 returns this pointer to physical
controllers, including when an F1 request is still waiting for a grab to end.
The floating CHRIS status panel is opened from Labs and can be dragged. It
contains status, selected mode/hand, physical Brush/Wand handedness, and Stop.
There is no headset binding browser, editor, review page, or voice-command panel.
The mapping authority API still checks exact bytes, digest, session and revision,
waits for neutral input, and persists atomically. To use a custom v0.1.2
profile, author valid JSON at `<persistentDataPath>/CHRIS/active-mapping.json`
and call `chris.mapping.reload` to activate it; the CLI currently edits v0.1.1
only. Stop cancels mapped control; the saved file remains for explicit reload.

The saved v0.1.2 profile loads at startup with physical controller pose,
pointing, buttons and trigger still in control; its default UI mode does not
take over until F1. F2 enters virtual position mode, F3 virtual rotation mode,
1 selects Brush, 2 selects Wand, F5 requests a neutral recenter, and F6 returns
to physical controllers. Escape
is local Stop and also restores physical pointing and input.
The fixed room frame follows head yaw only when the profile first activates or
F5 completes; looking around does not move either hand. Mouse movement moves
the selected hand in position mode or yaws/pitches it in rotation mode. Q/E
controls depth in position mode and roll in rotation mode. Brush uses Space
trigger, G grip toggle, X/C face buttons, B stick click and arrows for stick
axis. Wand uses Enter trigger, H grip toggle, T/Y face buttons, U stick click
and I/J/K/L for stick axis. Z is undo, wheel changes brush size, and W/A/S/D
moves the view in pose modes. Triggers are binary; adjustable analog pressure
is outside this profile. Both grips are independent, and mode changes, Stop,
focus loss and hand-back release their virtual buttons. Pose hand-back waits
for an active stroke or grab to finish and requires a fresh physical release.

Use `./agent/scripts/verify-native-ui.ps1` for edit-mode parser, state and UI
checks. A compact status-panel render appears in `agent/logs/native-ui/`.
A Windows/OpenXR player and the bounded Simulator
gate are separate from edit-mode checks; headset comfort and physical timing
still require user acceptance.

## Upstream integration points

CHRIS uses the existing TiltBrush namespace and compilation boundaries.
Directory grouping does not require new generic interfaces or assembly splits.

| Upstream file | Required integration |
| --- | --- |
| `Assets/Scripts/API/ApiManager.cs` | Attach the native command gateway to the API host. |
| `Assets/Scripts/App.cs` | Allow editor checks to exercise internal runtime contracts through the existing editor assembly. |
| `Assets/Scripts/GUI/BasePanel.cs` | Reserve the CHRIS panel type. |
| `Assets/Scripts/GUI/PanelManager.cs` | Check availability and lazily construct the optional floating panel. |
| `Assets/Scripts/InputManager.cs` | While a CHRIS mapping uses mouse movement, drop the mouse branch of `GetBrushScrollAmount` and `GetMouseMoveDelta`. |
| `Assets/Scripts/SketchControlsScript.cs` | Make `CanUndo` internal so mapped undo uses the native gate. |
| `Assets/Scripts/Input/UnityXRControllerInfo.cs` | OR the active mapping's draw into the brush trigger (level, edges and value 1); report the brush as present while CHRIS move_brush owns its pose. |
| `Assets/Prefabs/Panels/LabsPanel.prefab` | Provide the native CHRIS launcher. |

Shared native meshes, fonts, icon atlas and UI base classes remain in their
upstream locations. Preserve existing `.meta` GUIDs when moving CHRIS assets.
Keep resource loading at `Resources.Load("CHRIS/UI")`; Unity's `Resources` and
`Editor` special-folder semantics must remain intact.

## Verification and retained evidence

Use Unity `6000.6.0f1`. The editor entry point is
`TiltBrush.TestCHRISNativeUI.Run`, also available as **CHRIS > Verify native UI
(Edit mode)**. It runs the native assistance, voice backend, mapping and compact
status UI checks and creates a layout render. Do not run `CHRISCompanionAssets.ConfigureAndVerify`
for routine verification: Configure rewrites authored assets.

Run `./agent/scripts/verify-native-ui.ps1` from PowerShell; `-UnityPath` can
override the editor executable. The helper resolves this checkout from its own
location, rejects a second editor for the same project, and records its editor
log and exit code in `agent/logs/native-ui-runs/<timestamp>/`. Current layout
renders and protocol fixtures go to `agent/logs/native-ui/`.
Run `./agent/scripts/test-build-source-patch.ps1` to verify that the build
helper's Git patch replays text line endings and binary bytes in an isolated
local fixture. It writes only ignored evidence under `agent/logs/`.
Run `./agent/scripts/test-unity-generated-sidecars.ps1` to check that a build
can retain a new Unity `.cs.meta` only for a source file already added before
the build. The helper records each retained path, owner, and SHA256 in
`unity-state/generated-sidecars.tsv`; other new paths still stop for review.

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
On September 25, after the `55e042bb` native activation/status checkpoint and
the corresponding backend `6e3cfb` checkpoint, the user reported that the
supplied 12-item headset recheck sequence passed ("Tests are good"). No
per-step logs, timings, or independent headset measurements were captured.

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

## Voice backend retained for external workflows

The headset status panel has no microphone, voice-command, approval, or readout
controls. Opening it does not instantiate `CHRISAssistanceClient`,
`CHRISVoiceInput`, or `CHRISConfirmationSpeech`. The voice backend classes and
Python relay protocol remain in source for explicitly enabled workflows and
in-memory regression checks; they are not started by the status panel. The
external CHRIS service owns credentials and models. The native command gateway
and mapping status/activation/reload APIs remain independent of the voice UI.

`TiltBrush.TestCHRISNativeUI.Run` exercises the retained backend with mocked
WebSocket, PCM, and command adapters. It does not open a microphone, contact a
provider, or run the player. Any future headset voice workflow needs separate
UI and acceptance work before it can be offered to users.

## Repository rules

- **HTTP API testing:** The Open Brush HTTP API is available only while the Open Brush application is running or the Unity Editor is in Play mode. Check this before testing HTTP commands.

- For every brush with noticeable visual differences from the old Unity version, excluding surface shaders, copy the old Unity shader and make only the minor changes required to support URP.
- For brush visual-parity work and screenshot analysis, use the normal `m_Material` path and ignore `m_TestingMaterial`. Do not infer that `m_TestingMaterial` is the active material, a URP migration target, or suitable for repurposing. Verify the material actually used at runtime when material selection is relevant.
- For old-versus-new brush screenshot prioritisation, run `.venv-ssim\Scripts\python.exe Support\Python\compare-brush-screenshots.py --old-dir ..\open-brush-fast\Support\Screenshots\brushes-postfx-disabled --new-dir Support\Screenshots\brushes-postfx-disabled`. The script reports unchanged full-image RGB SSIM and prioritises using SSIM averaged near the dilated union of rendered brush pixels. Do not return to full-frame pixel MAE. Create the environment with `uv venv .venv-ssim --python 3.14` and install it with `uv pip install --python .venv-ssim\Scripts\python.exe -r Support\Python\requirements-brush-screenshots.txt` if it is missing.
- Unity editor logs for this project are project-local: `Logs/Editor.log`, with `Logs/AssetImportWorkerHW*.log` and `Logs/shadercompiler-*.log` alongside it. The `C:/Users/andyb/AppData/Local/Unity/Editor/Editor.log` path can be stale here - check mtime before trusting it.
- **UnityGLTF shader variants:** The empty/no-keyword variant is required and must be retained when filtering a Shader Variant Collection. For the current UnityGLTF package, the audited importer-reachable URP-only set contains 384 PBRGraph entries plus 8 UnlitGraph entries (392 total), including each empty set. It retains alpha test, texture transforms, instancing, clearcoat, iridescence, sheen, specular, transmission, and dispersion while excluding only keyword states the importer cannot produce. Do not substitute the older 75-entry graph subset as complete coverage; re-audit importer keyword behavior when UnityGLTF changes.
