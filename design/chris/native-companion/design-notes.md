# CHRIS — Native companion

Approved design by Iris, revision 5, authorized by the user for implementation. The recording shortcut is the non-drawing-hand joystick click, scoped to the open CHRIS panel. Iris owns design artifacts and design QA only; Pixel owns all native implementation and Logic the service. No additional design approval gate is required for the assigned changes.

Current location: `C:/Dev/open-brush-chris/design/chris/native-companion/` in the shared checkout. The approved v5 PNG pair, `prompts-v5.md` and this file are the current design references. All 15 original files, including superseded boards and prompts, are preserved under `Design/CHRIS/iris-native-companion/` at tag `checkpoint/chris-design-20260914` (commit `db13e497`). Consult root `agent/README.md` for current implementation and delivery status.

Acceptance evidence: Iris accepted the panel screenshots timestamped 2026-09-14 00:57:59. Separately, Pixel reports that the user passed the functional sequence on source `0b7dae2e`. That report is not Iris runtime testing and does not establish individual handedness/Labs cases or acceptance of newer source `48760d29`. Pending/untested statements in the dated QA below describe the evidence available to Iris at those times.

## Recommendation

Keep a single floating panel with a stable header and one prominent local **Stop**. Use Open Brush's rounded charcoal panel, light rim and bold white icon language. Give the current transcript or exact command list the largest area. Use one primary action appropriate to the state, with **Retry** for corrections. The transcript is read-only. Hide irrelevant action rows rather than leaving disabled grids on every screen.

The generated boards are visual concepts, not application screenshots or tested VR layouts. The examples illustrate validated command review; they do not add an example/demo feature.

Current deliverables: `01-listening-review-v5.png`, `02-processing-recovery-v5.png`, and exact built-in imagegen prompts in `prompts-v5.md`. Earlier boards are superseded historical concepts. Revision 5 applies the user's wording changes: a concrete left/right joystick hint, Retry for recording corrections, and removal of the redundant review and hold instructions. The listening image illustrates the left-joystick configuration; automatic hand detection is specified below, not implemented by a static mockup. Both boards were visually inspected for the requested wording and preserved exact command values, Confirm, Stop and speech toggle. Typography is illustrative; these boards alone do not establish headset readability.

## Historical reference basis

The source revisions and Build paths in this section and the dated QA are retained as inspection provenance, not current checkout or artifact locations.

- Read-only source inspected at native checkout `Build/CHRISUI-worktree`, HEAD `4af216896cb162493e1db55e49b4c8104102159d`: `CHRISNativePopup.cs`, `CHRISUIResources.cs`, `CHRISFloatingPanel.cs` and the then-current `Build/CHRISNativeUI/assist-review.png` and `assist-listening.png`.
- Those baseline renders show a dark rectangular CHRIS panel, narrow typography, live transcript, exact ordered command review and several redundant actions. They are editor layout evidence; no live headset inspection was performed.
- Subsequent coordination update from Atlas: native cleanup `fd9e9b1` is reviewed and integrated, including removal of Cancel recording because it uses the same Stop callback. This status is reported by Atlas; the visual reference inspection above predates that integration.
- The older `Build/CHRISCleanupReview/native-ui-checks/native-menu.png` supplies a real rounded native panel shell. Its blank button faces are a limitation of that render, not a proposed icon style. Its older CHRIS behavior is superseded by the source and voice renders above.
- Actual project assets inspected: `Assets/Resources/AtlasCatalog.png` (bold monochrome microphone, check, square, keyboard/text, speaker, close and other native glyphs); `Assets/Materials/Panel.mat` (neutral grey panel/tint); `PanelButton.mat`; rounded border/button mesh families in `Assets/Models/`.
- [Open Brush UI](https://docs.openbrush.app/user-guide/using-the-open-brush-tools-quick-tools-and-menu-panels) documents painting-hand secondary-button Quick Tools access. [UI Elements](https://docs.openbrush.app/developer-notes/ui-elements) documents floating/docked panels and native popups/trays. Web image references did not render usefully, so visual grounding uses the local assets and editor renders above.

## Interaction requirements

| State | Main content | Actions |
|---|---|---|
| Ready | Short invitation to speak; retained request if present | Record starts capture through the panel or scoped non-drawing-hand joystick click. |
| Listening | Clearly labeled live transcript; visible microphone state | Finish recording ends capture and starts finalization. One centered Finish recording action; local Stop aborts. |
| Finalizing transcript | Microphone off; retained transcript labeled Provisional transcript | Finishing transcription; text may still change. Retry replaces the pending request; Stop remains reachable. No command preparation claim yet. |
| Preparing commands | Final transcript labeled Transcript complete | Checking the request and sketch; exact commands appear next. Retry replaces the request; Stop remains reachable. No Confirm yet. |
| Review | Full ordered, exact validated command text and all numeric values | Confirmation speech On by default, automatically reading the exact approval text. Off immediately silences playback and suppresses future autoplay. Confirm remains explicit, with Retry secondary. No spoken response, audio completion, gaze dwell or held trigger counts as approval. |
| Executing | Verified completed count and current command, with pending work distinguished | Stable local Stop. Do not show Confirm or invite an immediate duplicate submission. |
| Stopped | Verified completed count; explicit manual control status where known | Record a new request after state reconciliation. No claim that already-applied commands were undone. |
| Unknown outcome | Last verified count and the particular uncertainty | Retry is a status/recovery check, never blind replay. Explain this beneath the button. Keep unknown commands visually distinct from verified completion. |
| Recoverable input/service error | One plain-language problem and next step | Retry only when valid for that state. Retry after input is available, or select/reconnect the microphone when needed. No keyboard or text-entry prompt. Details stays secondary. |

The board's “Checks status. Does not repeat commands.” copy applies only to unknown-outcome reconciliation. It is not a universal description of Retry: other states can retry submission, cancellation or another state-appropriate operation. The visible explanation must match the actual recovery operation.

Revision 5 uses the same requested label **Retry** for two context-specific actions. In Finalizing transcript, Preparing commands and Review, the microphone-icon Retry starts a fresh recording and invalidates the old proposal/work. In Outcome unknown, circular-arrow Retry retains its reconciliation behavior and existing explanation. This wording change does not convert recovery into a new recording or replay, and does not add two Retry buttons to the same state.

## Current wording specification

- Shortcut label is exactly “Click left joystick to toggle recording.” when the non-drawing controller is left, or “Click right joystick to toggle recording.” when it is right. Resolve the current controller role/hand mapping and update the text when it changes. Do not literally show “[left/right]” or assume left permanently. Retain the separate “While CHRIS is open:” scope line.
- Replace every correction-button label “Re-record” with “Retry”; recording-again behavior remains as described above.
- Remove “No need to hold.”, “Apply these in order”, and both “Reading exact commands...” / “Only Confirm applies commands” from panel copy. Do not substitute other helper prose into that freed space. These are copy removals, not changes to exact ordered review, automatic speech or explicit approval behavior.

Long command reviews must paginate at a readable size and preserve complete exact text, including values, directions, units and reference frame. Do not ellipsize or shrink to fit. Show Previous/Next only when pages exist, keep page count, and retain the current all-pages-viewed gate. Review invalidation on changed sketch/proposal, expiry, corrections, moves and required trigger release remains authoritative. Confirm should show a reason when unavailable; enable only after a fresh valid review and a new intentional activation.

## Visual and controller rationale

- Keep Stop in the same place through all states and separate its hit area from the move grip, Close and Confirm. Red plus a square and the word Stop conveys meaning without color alone.
- Reuse native icon assets where suitable in the implementation. The mockup glyphs and cyan action tint are design approximations, not replacement production assets.
- Retain the tall native character in the CHRIS heading; use a medium-weight, wider body face for command values. Pixel should use an available appropriate font for the approved visual revision; generated glyphs are not font assets.
- Keep the panel's present floating behavior as the starting point. Exact world size, angular text size, pointer target area and spacing must be tested on Quest 3 PCVR at the actual placement distance. Image pixel dimensions cannot establish VR readability.
- Use stable line wrapping and no animated transcript reflow around the action row. A restrained live indicator is sufficient; no decorative waveform is necessary.
- Recording again is the correction path. Microphone selection and diagnostic details remain secondary, not a permanent service dashboard.

## Scope and decisions

Already approved cleanup reflected here: remove No-model example, remove duplicate Cancel task, Cancel / Back and Cancel recording, keep one prominent Stop, rename Check / Retry to Retry. Atlas confirms the Cancel recording consolidation is already included in the reviewed, integrated native cleanup `fd9e9b1`; it does not require separate approval.

Approved interaction scope, recorded from the user's instructions and Atlas's handovers:

- Latest user request relayed by Atlas removes Type instead and Edit request (the user's “edit quest”), favoring recording again over controller typing. This supersedes every earlier typed-fallback direction for the CHRIS VR UI. Do not expose keyboard/text entry in listening, processing, review or error states. Retry stops/discards old speech and pending request work, invalidates the old approval, and begins fresh capture; the replacement still requires its own exact review and explicit Confirm. It must not replay an uncertain execution or silently undo completed changes.

- GPT-4o Transcribe voice input with live transcript; GPT-4o Mini TTS automatically speaks the exact confirmation. These are authorized implementation tasks, not functionality verified by the design boards.
- Transcript timing clarification from Atlas: exact `gpt-4o-transcribe` aggregates phrase/turn updates while manual recording remains open. Word-by-word updates during uninterrupted speech are not established. Retain the label “Live transcript,” allow phrase-sized updates, and use the separate finalizing status. A pause never submits the request; only the user's Finish action ends recording and starts preparation. The mockup text is illustrative and is not evidence of provider timing.
- Confirmation speech defaults On and remembers the local choice. Turning Off stops current audio and prevents later autoplay. This supersedes the earlier on-demand Read aloud recommendation. No new choice is needed from the user about automatic versus on-demand speech.
- Automatic speech runs once per fresh valid approval, using the same immutable exact approval text displayed, including ordered actions and values across all pages. Stop, correction/new recording, or a changed/expired proposal stops playback and discards late audio. Confirm is independent of speech completion. If TTS fails, visual review and explicit Confirm remain; show a quiet message such as “Speech unavailable. Review the text.” Prevent playback being captured as a new voice command.
- Click the non-drawing hand joystick to start recording; click again to finish. No sustained hold is needed. Give short audio/haptic cues and retain panel Record/Finish as the alternative to the shortcut.
- The recording shortcut uses joystick click **only while CHRIS is open**. The earlier grip reservation/release requirements are superseded; the recording shortcut no longer reserves grip. The latest direct user wording request requires a concrete left/right joystick label selected automatically from the non-drawing-controller mapping, as specified above. Pixel owns detection, handedness changes and fresh-click handling. Iris has only revised the design artifacts.
- Atlas reported no competing Quest/OpenXR stick-click action in its source review; Pixel owns mapping implementation and validation. This is historical coordination evidence, not an Iris runtime test or a claim that every controller configuration is conflict-free. See the separate acceptance evidence above.
- structlog service logging is approved for Logic. This does not add a diagnostics dashboard. Details remains a small optional recovery disclosure; its inclusion should be justified by useful user-facing recovery information.

Close stays in the current footer role with current behavior. This revision does not disable it or introduce stop-before-close. At the historical source inspection, the question about Close during execution was unresolved: the inspected `CHRISPanel.Closed` cancels input and clears review but does not directly call `StopLocal`. Do not infer from that historical inspection that Close stops execution, and do not treat either earlier alternative as an approved requirement.

## Placement coordination for Pixel

Revision 3 adopts Pixel's refined placement in the images: one always-visible stateful speech button in the footer-left slot, opposite Close. This supersedes the earlier proposal to move it between choice slots. A stable footer position keeps Off reachable during recording, audio fetching, review and trigger-release wait, when ordinary choice buttons can be disabled. The control must not inherit those ordinary-choice availability restrictions. Retry replaces the former Edit request action beside Confirm in review and the former correction action during processing. The listening state uses one centered Finish recording action. No typing controls or keyboard fallback remain.

The images use “Speech: On” / “Speech: Off” with a speaker glyph and a confirmation-speech detail inset. A longer “Confirmation speech: On/Off” is suitable if it fits at readable size. Distinguish this from the microphone control. Default On, remembered local choice, Off silences current audio and suppresses future autoplay. A native stateful button is sufficient. Preserve exact-text space, pagination, stable Stop and current Close. The full panel design is authorized for implementation.

## Implementation handover and design QA

- Use the current revision 5 boards as the approved visual basis: rounded charcoal backing, restrained pale rim, white native glyphs, wider readable body text, generous exact-command area, one stable Stop in the header, and speech On/Off opposite Close in the footer. Reuse actual native assets rather than tracing generated raster icons.
- Replace the old More-menu CHRIS launcher entries with **one native-style CHRIS icon in Labs**. Reuse `Assets/Resources/Icons/mic.png` with the existing Labs icon-button styling and a CHRIS hover/description label. Match neighboring tile geometry, spacing and hover feedback. The panel's own Close remains. No separate new Labs close/stop entry is proposed. Pixel's latest handover says the independent More-menu local Stop is retained per Atlas; the single-Stop rule concerns duplicate cancellation within the CHRIS panel. Verify both relevant Labs variants so there is one discoverable launcher per active UI layout.
- The button that records a correction is still labeled **Retry**, per the user's latest direct wording request. Atlas's later phrase “voice-only Re-record” describes that behavior, not a reversal of the requested label. Keep the microphone glyph in recording-correction states and the circular-arrow glyph with the existing explanation in unknown-outcome reconciliation.
- The shortcut targets the **current non-drawing controller**. Display exactly “Click left joystick to toggle recording.” or “Click right joystick to toggle recording.” from the actual role-to-hand mapping; refresh on handedness changes while the panel is open. Keep the panel-only scope line and do not restore a generic non-drawing hint, grip instructions or hold instructions.
- Preserve the complete exact approval and values. Paginate before making long reviews unreadably small; retain all-pages-viewed, freshness and trigger-release gates. Do not restore the removed helper sentences or typing controls. Confirm remains explicit and independent of speech completion.
- For screenshot QA, useful views are Labs with CHRIS visible, listening with left and right mappings, single-page and long/multipage review, speech Off during trigger-release wait, and separate finalizing/preparing states. A runtime handedness switch needs behavior evidence in addition to two static labels. Headset comfort and actual angular readability remain user acceptance items.

Baseline inspected read-only before Pixel's redesign handover: `Build/CHRISUI-worktree/Build/CHRISNativeUI/assist-review.png`, `assist-listening.png` and `assist-speech-toggle.png`, file timestamps 2026-09-13 23:27:39 as reported by the filesystem. These still show flat rectangular surfaces, narrow body type, a generic non-drawing joystick hint and the old Record / Re-record label. They do show a stable footer voice toggle and full five-command review text. These are earlier editor renders, not a submitted final redesign; no final fidelity sign-off is implied. Subsequent screenshot inspections are recorded below.

The following dated QA entries are historical records. Later entries supersede earlier visual findings; their runtime limitations are scoped to Iris's inspections and do not override the separately reported user acceptance above.

### WIP render QA — 2026-09-14

Inspected actual `assist-review.png`, `assist-ready.png` and `assist-listening.png` at the same native render directory, all timestamped 2026-09-14 00:30:25. These supersede the baseline observations above for the three states. Read `CHRISNativePopup.cs` only to confirm layout geometry. No source changes or image processing were performed.

Matches v5: rounded charcoal shell, neutral list icon, wider body face, stable header Stop and footer speech/Close, Confirm beside microphone Retry, a single centered Record/Finish action, and the exact left-joystick hint. Removed typing/edit controls and removed review helper prose are absent. The five command texts and values appear complete in this example; no single-page Previous/Next clutter is shown. The static left label does not establish live handedness switching.

Concrete changes before visual sign-off:

1. **Increase text size independently of the material fix.** At the same panel-relative size, body text and button labels are roughly half the visual size of the approved board. The review's five lines are crowded into the top of a large mostly empty card. Make exact-command text and primary labels substantially larger (a 1.5–2x rendered-size trial is appropriate), then wrap/paginate as needed. Enlarge the joystick hint too; it is currently much smaller than the transcript. These are screenshot-relative design targets, not proven VR angular-size thresholds. Do not fit five commands on every page by reducing font size.
2. **Use per-command grouping for review.** Keep the single recessed surface for transcripts. For review, distinguish each logical command with its own padded row, aligned number gutter and a visible inter-row gap or subtle divider. The existing outer card may remain; separate native backplates are optional. Row grouping should follow complete command boundaries, not each visual wrapped line. Long commands take taller rows; preserve verbatim text, values and order and paginate before overflow. This delivers v5's grouping without requiring a new UI architecture.
3. **Separate device selection from Record/Finish.** The device strip and primary action visually overlap. Source geometry confirms the device spans y -0.905 to -0.655 while Record/Finish spans -1.355 to -0.865: a 0.04-unit overlap. The strip also intrudes 0.05 units into the transcript surface's bottom extent. Give the transcript card, device selector and primary button distinct gaps; a visible 0.08–0.12-unit trial gap is reasonable at the current scale. Inspect resulting hit areas rather than inferring safe targets from color alone.
4. **Avoid an overflow-menu glyph for panel movement.** The header currently uses `more_solid` (ellipsis), which conventionally suggests a menu. Prefer a suitable native move/grab glyph or a quiet CHRIS drag header with a “Move CHRIS” hover description; do not add another menu. This is a secondary affordance refinement after readability and spacing.

Known material issues, already owned by Pixel: dark Roboto text on dark surfaces, black/rectangular icon backgrounds, and the dark Confirm checkmark on cyan. These remain visually unacceptable in the current renders, but are not additional unexplained layout defects. Reinspect white/warm-white body and enabled control labels, clean icon silhouettes, and enabled/disabled distinction after that fix. Do not compensate for dark materials only by increasing font size.

Next evidence: corrected review/listening renders, a long wrapped or multipage review at the larger type size, right-hand hint plus actual handedness-change behavior, and the Labs launcher in context. Labs serialization and the reported 20 regression passes come from Pixel's handover; Iris has not independently verified those behaviors. No headset/build acceptance or final visual sign-off yet.

### Updated WIP render QA — 2026-09-14 00:44:39

Inspected the new actual `assist-review.png`, `assist-listening.png`, `assist-finalizing.png`, `assist-proposing.png` and `assist-executing.png`, all timestamped 00:44:39. These supersede the earlier dark-render findings for these states. No new source inspection or edits were needed for this pass.

Resolved: body and button text is now opaque white and substantially larger; the Confirm checkmark is white; the opaque black icon rectangles are gone. The five-command example wraps the fifth command visibly without truncating the value or units. The larger body size is suitable for the next desktop layout pass; do not keep enlarging it globally to compensate for unrelated spacing. Actual headset readability is still untested. Heading softness/glow remains visible and is already being tuned by Pixel.

Remaining actionable layout/copy points:

- Review remains one dense text block. Add per-command padding/separation and a hanging indent or number gutter: the wrapped word “units.” on command 5 currently starts at the far-left number margin, rather than under its command text. Use variable-height logical rows; keep full values and paginate when required. A single surrounding recessed card is acceptable, but it should contain clearly separated command rows.
- In Listening the transcript card, microphone device strip and Finish recording button still touch/overlap visually. The earlier spacing finding remains open; make the three surfaces visibly distinct. No new collider behavior claim is inferred from this image.
- The joystick hint and processing status remain much smaller than the body and button labels. Increase those secondary text sizes enough to read without leaning in, and use nearby unused space. Keep exact approved joystick wording and line wrapping. This is a targeted hint/status change, not another global body-size increase.
- Finalizing correctly says “Provisional transcript” and is distinct from Preparing commands / Transcript complete. However, it still displays an ordinary microphone glyph without visible off-state information. Add “Microphone off” to that state (for example “Microphone off · Provisional transcript”) or a clear microphone-off glyph with a readable off label. This restores the approved indication that capture ended while final text is still pending.

Copy checks passed in the shown states: Retry correction label, Confirm, Speech On, Close, specific left joystick hint, removed review helper sentences, and no typing/edit controls. The executing example says Awaiting verification and makes no false completion claim; it does not yet demonstrate nonzero verified progress or partial recovery.

Pending evidence after remaining fixes: corrected grouped review with a wrapped row and multipage case, separated listening controls, sharper headings, right-hand hint/handedness switch, Speech Off during trigger-release wait, and Labs after Pixel's ongoing inactive-slot reflow fix. The Labs issue was reported by Pixel and was not inspected in these five images. No final visual or headset sign-off yet.

A persistent local Stop outside the panel, if already present, remains a safety access path; the one-Stop rule here concerns avoiding duplicate cancellation controls within each panel.

### Updated render QA — 2026-09-14 00:53:25

Inspected actual `assist-review.png`, `assist-review-2.png`, `assist-listening.png`, `assist-finalizing.png`, `assist-proposing.png`, `assist-executing.png`, `assist-speech-toggle.png` and `assist-menu.png` in Pixel's `Build/CHRISNativeUI` directory, all timestamped 00:53:25. This pass supersedes the earlier unresolved layout findings for the shown states.

Resolved: review now has padded numbered command rows and an aligned number gutter. Page 1 contains commands 1–4; page 2 preserves command 5's full values and wraps “native units.” beneath the command text with a hanging indent. Confirm is visibly disabled on page 1 and enabled on page 2. Listening now visibly separates the transcript card, device selector and Finish recording button. Secondary hints/status are larger, headings are crisp, and Finalizing explicitly displays “Microphone off. Finishing transcription...” while retaining the provisional-text warning. Preparing remains a distinct state. Speech Off is visible during trigger-release wait, with Stop and Close still shown.

The requested copy is present in the reviewed states: the specific left-joystick instruction and Retry, with the redundant hold and review prose removed. The right-hand variant and live handedness switching still need behavior evidence. Executing correctly says Awaiting verification; this example does not establish nonzero verified progress or partial recovery.

Only the already-planned navigation text enlargement remains visible in these panel layouts. Include the page count as well as Previous and Next in that adjustment. No further global body-size increase is requested. The shown panel states otherwise pass this desktop screenshot design review; actual headset readability and runtime behavior remain separate acceptance items.

`assist-menu.png` shows the More menu with its retained local STOP and removed CHRIS launcher. It is not a Labs view and cannot establish the new Labs icon placement or inactive-slot reflow. A Labs screenshot remains outstanding.

Pixel reports 28 passing regression checks and worker-gated microphone-off status. The read-only `checks.txt` records 28 passes and explicitly excludes Play mode, HTTP, microphone recording, speech/model inference and sketch changes. Iris did not rerun those checks; screenshot inspection establishes appearance only, not collider or callback behavior.

### Screenshot QA accepted — 2026-09-14 00:57:59

Inspected all four final requested renders at their confirmed 00:57:59 file timestamps: `assist-review.png`, `assist-review-2.png`, `assist-listening.png` and `assist-finalizing.png`. Screenshot QA is accepted with no remaining concrete visual blocker in these views. This closes the pending navigation-size finding from the 00:53:25 pass: Previous, Next and the page count are readable and separated from the main actions and footer.

Both review pages preserve padded command rows, the aligned number gutter and the fifth command's hanging indent without clipping values or units. Listening retains visible gaps between the transcript card, device strip and Finish recording. Headings are sharp, secondary hints are readable, and Finalizing clearly shows microphone-off and provisional-transcript status. Exact revision 5 wording remains intact. No further layout or copy revision is requested before the scoped Windows build on screenshot grounds.

At this screenshot inspection, headset readability, runtime handedness detection/switching and actual input/audio behavior remained unverified by Iris. The separate Labs prefab preview has uninitialized native atlas/tray visuals; Atlas's acceptance of source/geometry checks pending headset runtime is coordination evidence, not an Iris visual verification of Labs. Neither limitation reopens the accepted panel screenshot fixes.

Iris's recorded design QA did not change application code, prefabs, scenes, settings, dependencies, builds or conductor stores. Iris did not test headset usability, provider behavior or runtime state transitions; the later user-reported functional acceptance is recorded separately above.
