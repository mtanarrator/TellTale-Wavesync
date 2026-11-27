# TellTale WaveSync — Change Summary

## v2.0.0 — Global matching & trustworthy metadata

- **Global matching is now the default:** clips are matched pairwise through a sparse audio graph and solved together as relative positions. No single master anchor is required; the selected master only pins the overall gauge.
- **Automatic legacy fallback:** sparse/sequence-only graphs, fewer than two analyzable events, more than 400 unique media paths, or roughly 512 MB of retained audio fall back to the unchanged master-anchor pipeline with a clear warning.
- **Time source and clip order controls:** **Advanced** gains Time source (Auto / Recorded date / Timecode / Filename timestamp / File modified time / Ignore) and Clip order (Auto / Strict / Timecode / Preserve timeline order / No ordering). Both persist in settings; **Preserve timeline order** keeps the legacy DST path.
- **Clock validation:** device clocks more than 30 days from the project median are treated as untrusted — their epoch is not used for sequence offsets and the warning names the device. Container timecode is handled as seconds-of-day and unwrapped across midnight.
- **Honest placement labels:** clips without an audio edge are placed by clock/order where possible and marked `clock-placed`; prior-only components keep their position instead of guessing. B-roll (clips under 15 s) never anchors a sequence and may receive at most one audio edge.
- **Destructive placements are refused:** when a device's timestamps are physically impossible (more media than their span can hold) the current timeline spacing is kept, and when a solved placement would make a device's own clips overlap heavily that device is left at its original position with an explicit warning. Camera sync is preserved; ambiguous recorder blocks are not scattered.
- **DeviceIdentity organizer:** cameras resolve to model names (`Sony A7 III`, `Sony FX3`), while recorders keep folder identity (`Tascam_wesele (audio1)` / `(audio2)`). The preview can show Camera / Recorder / B-roll roles.
- **No audio duplication:** camera video and its existing companion audio live on paired `CAM n — Name (V)` / `CAM n — Name (A)` tracks; a camera with no companion audio gets only `(V)`. Each recorder folder gets one audio track. Nothing is copied or muted. Duplicate tracks created by older builds are not recreated and should be cleaned manually.
- **Responsive metadata probing:** organizer and sync take a fast COM snapshot on VEGAS's UI thread, then collect ffprobe metadata in parallel off-thread. The host stays responsive and Stop can be handled during the metadata pass; the project-generation guard still discards results if the project changes.
- **Grouped A/V lane handling:** crossfade protection now relocates overlapping grouped camera units as whole units to adjacent Sync Lanes below the source track; single-track stacks stay on lane 0. New lanes are inserted next to their source track instead of at index 0, so upper video tracks and titles are not displaced.
- **Group primary, drift and splits:** grouped A/V analysis prefers the audio event for stream, mute and trim data; relative drift is anchored per audio component and propagated across the edge graph; split points detected on a reverse edge are remapped for the anchor case; already-aligned groups no longer dirty the undo history or count as moved.
- **Project-aware options:** the project frame rate and B-roll threshold now come from VEGAS/request options instead of hardcoded 25 fps and 15 s; sub-sample mode lowers the audio move deadzone so small corrections are applied.
- **Tests and rollback:** Rust tests cover the synthetic global chain, sparse fallback, untrusted clocks, B-roll anchoring, and legacy parity. The pre-v2 build was archived before the change (`Backups\WaveSync_legacy_20261127_123741.zip`).

## v1.5.0 — Post-v1.4.3 audit fixes

- **Dropped-frame slicing works on its own:** discontinuity detection no longer requires **Fix gradual sync drift**. The measured step jump is carried to the applier, the post-drop segment is shifted, and the duplicated tail is trimmed so the repair stays on one track.
- **Playback-rate round-trip:** an existing `PlaybackRate` is sent to the engine and its length is scaled to preserve the media span. The engine reports an absolute target (it re-measures the source media each run), so re-runs are idempotent. WaveSync records the rates it writes per project/event, so its own earlier stretch is updated or cleared while a deliberate manual retime — even inside 0.9–1.1 — is preserved.
- **Project-switch safety:** opening or closing a project during analysis cancels the run and discards the manifest; lifecycle events also refresh the track list.
- **Copyable, throttled log:** the activity log is a read-only text box with a **Copy Log** button and batched updates instead of a full rebuild per line.
- **Defaults match the docs:** **Include video clips** and **Keep overlapping clips on separate tracks** now start **ON** (explicit saved `false` still wins).
- **Organize Tracks by Device/Folder:** a new preview-first action groups timeline media by camera/recorder using ffprobe metadata, filename prefixes, and folders. Cameras get a `CAM n — Name (V)` track plus a paired `CAM n — Name (A)` track for existing companion audio (no duplication or muting); a camera without companion audio gets only `(V)`. Recorders get `REC n — Name`. Preview supports rename/include/merge/split-by-folder, the pass is one Undo step, source tracks are kept, and re-runs reuse tracks.
- **Friendlier panel:** quick-setup presets (Multicam, Long recording, Damaged footage, Reset defaults), options grouped by intent with **Advanced** collapsed, a post-run result summary with an undo hint, and a **Warnings only** log filter. The master dropdown now reads **Auto (recommended)**.
- **Envelope automation:** new Sync Lanes create missing envelopes and copy points plus base Volume/Pan/Composite levels.
- **Engine decoders:** partial FFmpeg decodes are reported as warnings instead of silent success; the Symphonia resampler flushes its tail.
- **Installer:** numeric VEGAS version ordering (`2027.0` beats `22.0`), relative build paths, uninstall cleanup for **all VEGAS versions** copies, and no `HKCU` access from the SYSTEM-context MSI action.
- **Compatibility:** manifests remain backward compatible (`split_points_seconds` is still emitted; missing `playback_rate` reads as 1.0).
- **Known limitation (v1.5.0):** grouped A/V lane relocation was implemented but disabled pending VEGAS host validation; the MSI target remains VEGAS 2027 only. v2.0.0 enables grouped lane relocation.

## v1.4.3 — Deferred audit items

- **Persistent settings:** all processing toggles and the master anchor are restored across VEGAS sessions from `%LOCALAPPDATA%\TellTale\WaveSync\settings.json`.
- **Engine health check:** the status line reports extension and native-engine versions; a missing engine disables **Sync** with a reinstall message, and an unexpected engine family is flagged as a mismatch instead of showing **Ready**.
- **Consistent naming:** remaining "Sync in Place" user-facing strings now say **Ignore Camera Clock Jumps (DST)**.
- **Honest cancellation:** cancellation is honored until the apply begins; the **Stop** button is disabled during the UndoBlock so it no longer promises to interrupt a committed pass. Closing the dock cancels a running analysis.
- **Group move rollback:** if a COM call fails partway through a group move, members already moved are restored (start and rate) and the group is reported as skipped. An exception after the move no longer aborts the remaining groups.
- **Docs:** README now cross-links the recorded-order note to the DST mode.
- **Cleanup:** old v1.4.0/v1.4.1 installers removed.
- **Installers:** `TellTale_WaveSync_Setup_v1.4.3.exe` / `.msi`.

## v1.4.2 — Audit fixes: honest results, edit-safe apply, DST detection

- **Edit-safe apply:** the request now carries each clip's persistent VEGAS `EventID`; the applier re-matches by that ID and rejects any result whose clip moved since analysis. A timeline edit during a long analysis can no longer apply a result to a different clip.
- **Honest completion status:** `ApplyManifest` returns moved and skipped counts. Partial applies show `Completed with warnings: N moved, M skipped`; clean applies report unaligned clips instead of silently calling them success.
- **DST clock-jump detection:** when two anchor clusters imply clocks about one hour apart (3300–3900 s), the run log warns that the source may have crossed a daylight-saving boundary and suggests the DST mode.
- **Tests:** 39/39 Rust tests pass, including three new clock-jump tests.
- **Installers:** `TellTale_WaveSync_Setup_v1.4.2.exe` / `.msi`.

## v1.4.1 — DST clarity + toggle interlocks

- **Renamed the mode** from "Sync in Place (Lock Order)" to **"Ignore Camera Clock Jumps (DST)"**. The old label described the mechanism; the new one names the problem. `preserve_timeline_order` is unchanged and still defaults **OFF**.
- **DST behaviour, stated plainly:** placement comes from audio correlation, not recorded timestamps. A camera clock that jumps ±1 h at a daylight-saving boundary therefore cannot move, reorder, or misplace clips; each source gets one rigid audio-derived shift.
- **Toggle interlocks:** while the DST mode is ON, **Clock Drift Correction**, **Dropped-Frame Auto-Slicing**, and **Prevent Crossfades (Auto-Add Tracks)** are switched off and locked out (dimmed). They cannot take effect in rigid-shift mode: drift/rate is forced to zero, split points are discarded, and no tracks are added.
- **Override dialog:** clicking a locked option (mouse or keyboard) explains why the combination is not advised. Choosing **Yes** turns the DST mode off and enables the requested option; choosing **No** leaves the lock in place.
- Docs and engine log strings updated to describe the DST behaviour instead of track-order mechanics.
- Installers rebuilt as `TellTale_WaveSync_Setup_v1.4.1.exe` / `.msi`.

## v1.4.0 — Sync in Place + Grouped Atomic Sync

- **Sync in Place (`preserve_timeline_order`):** one rigid shift per source; recorded timestamps are never used to reorder events; no tracks are added; overlaps are reported. Default **OFF**.
- **Grouped primary reference:** grouped multi-channel audio and A/V sets are analyzed through the lowest-`Track.Index` audio-capable primary only; companions are not decoded or correlated twice.
- **Atomic group move:** after the host gate confirmed VEGAS does **not** auto-move/split/rate companions, `EnableAtomicGroupMove = true` and `AutoMoveCompanionsInApplier = true` move, split, and rate-match every member inside the single `UndoBlock`, preserving internal offsets. Pre-validation prevents partial group commits.
- **Host verification script:** `TestHarness/verify_group_behavior.cs` (C# 5, valid 1 s probe WAV, project-bound `Media.CreateInstance`, `TrackEventGroups.Add` before members).
- **Branded binaries:** `TellTale_Audiosync.dll` + `telltale_sync_core.dll`; FFI symbols remain `vegas_sync_*`.
- **Installers:** Inno `TellTale_WaveSync_Setup_v1.4.exe` and WiX `TellTale_WaveSync_Setup_v1.4.msi`; legacy `TellTaleWaveSync.dll*` / `vegas_sync_core.dll` are removed on install.

## Capacity & Performance

- **Built for 1,000+ camera clips on one timeline.** Events are analyzed in small parallel batches (max 4 workers), and every decoded buffer is released as soon as its event is resolved, so memory stays flat instead of growing with the project size.
- **Validated on a real wedding project:** 174 video events (167 Sony FX3 + 7 Sony AX100) plus 3 audio recorders, aligned against a **9 h 21 m** master recording in about **30 seconds**.
- **Long takes never sit in RAM.** Anything over 5 minutes is streamed into a compact 125 Hz energy envelope (~4 MB per 2 hours of audio); only 30–600 s windows are ever materialized for sample-accurate refinement.
- **Crossfade lane planner stress-tested** with 400 overlapping intervals, correctly producing the minimum possible number of lanes.

## Stability & Progress

- Fixed the **hard halt** on long takes: bounded FFT windows, streaming decode, drained FFmpeg stderr, flat memory, and cancellation.
- FFmpeg child processes are launched with `CREATE_NO_WINDOW`; **no more console windows** (previously 1,000+ flashes on a large project).
- Added a **Stop** button; cancellation aborts the native pipeline in well under a second.
- Real progress for long decodes, per-event warnings, and a manifest that reports every skipped/unaligned event instead of aborting the run.
- Missing FFmpeg is detected once per run instead of being retried per event.

## Sync Engine

- **Match validation (NCC):** every alignment is gated on normalized envelope correlation, so short/unrelated audio can no longer produce a sharp but meaningless GCC peak.
- **Source-clock sync:** clips are grouped by media folder; continuous recording runs are reconstructed from file timestamps; a dominant camera clock is estimated from the longest reliable clips; every short clip is positioned from its recorded timestamp.
- **Outlier correction:** a strong-but-wrong clip inside a continuous run is overridden so footage stays continuous.
- **Recorded-time fallbacks:** clips without timestamps inherit a rigid group shift; weak matches are left unaligned (do-no-harm).
- **Manual Master Anchor:** the dropdown lists every audio **and video** track with clip count and longest take; the selection survives Refresh; picking a video track auto-enables video-clip analysis.
- **Negative timeline protection:** if sync would push content before 0, the whole synced block shifts right (logged).
- **Idempotent re-runs:** identical results produce no movement, and existing “Sync Lane” tracks are reused instead of duplicated.

## Timeline Editing

- **Crossfade protection:** overlapping synced events are spread onto extra tracks of the correct type — **video lanes for video events** (which keep their own audio), **audio lanes for audio-only events** — so VEGAS never inserts unintended crossfades.
- Untouched clips are **pinned** to their original track, preserving intentional edits.
- New lanes copy the source track's name, mute/solo, bus, effect chain, and envelope automation.
- Lane moves are verified (`ev.Track.Index`), capped, and happen inside the same **UndoBlock** — one Ctrl+Z reverts the moves and the added lanes.
- Video-track clips are now analyzed through their embedded scratch audio (optional toggle).

## Long-Take / Wedding Workflow

- Whole-take handling: long recorder takes are moved as a single event and keep their full length.
- Master analysis ceiling raised to 12 hours (a 9 h 21 m take is now fully covered).
- Source groups and recorded timestamps handle 2–5 second clips that are impossible to align in isolation.
- Master anchor is logged as `Master reference (selected track | auto): Event / file / duration`, so the operator can verify exactly what was used.

## UI

- Modern dark panel retained; added **Include Video Clip Audio (Multicam)**, **Prevent Crossfades (Auto-Add Tracks)**, and **Stop**.
- Master Anchor now shows `[A]/[V] track — clip count — longest take` with full tooltips.
- Per-event log lines include method, drift, confidence and NCC score.
- Updated installer, README, handover notes, and showcase screenshot.

## Packaging

- `TellTale_WaveSync_Setup_v1.4.exe`
- Native engine `telltale_sync_core.dll` v0.2.0; extension `TellTale_Audiosync.dll` v1.4.0.
- 30 Rust tests + harness checks for long takes, source clocks, crossfade lanes, and cancellation.
