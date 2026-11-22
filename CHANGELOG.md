# TellTale WaveSync — Change Summary

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
