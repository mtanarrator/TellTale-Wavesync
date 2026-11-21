# Agent Handover — TellTale WaveSync

## 1. Project Overview
**TellTale WaveSync** is a 100% free, native audio & multi-camera synchronization extension for **VEGAS Pro** (a PluralEyes alternative).
- **Architecture:**
  - **Rust Native Engine:** `D:\VEGAS Extensions\VEGAS Audio Sync\vegas_sync_core` → compiles to `telltale_sync_core.dll` (30 tests passing, including long-take, source-clock, master-anchor and crossfade-lane suites).
  - **C# .NET 4.8 Extension & WPF UI:** `D:\VEGAS Extensions\VEGAS Audio Sync\VegasAudioSync` → compiles to `TellTale_Audiosync.dll` (v1.4.0).
  - **Installer:** `D:\VEGAS Extensions\VEGAS Audio Sync\Installer\TellTaleWaveSync_Installer.iss` → compiles via Inno Setup 6 (`ISCC.exe`) to `TellTale_WaveSync_Setup_v1.4.exe`.

---

## 2. Current State & Completed Deliverables

### v1.1 - Long-take / wedding support
The extension previously **halted** on wedding-style projects (one long recorder take, many short clips). Root causes and fixes:

1. **Monolithic decoding.** Every event was fully decoded, all at once (Rayon fan-out), and every buffer was retained. 177 events × long takes exhausted RAM.
   - Now: events ≤ 300 s keep PCM in memory; longer takes stream through `AudioDecoder::scan_energy_envelope` into a 125 Hz log-energy envelope (≈4 MB per 2 hours) with the PCM discarded.
   - Slaves are processed in small parallel batches (max 4 workers); buffers are freed after each correlation.
2. **Unbounded FFTs.** `GccPhat::correlate` built FFT buffers sized by the full media length (hundreds of millions of samples → multi-GB allocations).
   - Now: `MAX_CORRELATION_INPUT_SAMPLES` (6 M) hard-guards the call, and the pipeline always correlates 30–600 s windows.
3. **FFmpeg stderr deadlock.** `stderr` was piped but never drained, so a chatty decoder could fill the OS pipe and block forever.
   - Now: a dedicated thread drains stderr; `-nostdin` is set; output is read in 1 MB chunks into f32 with progress reporting and kill-on-cancel.
4. **Master anchor picked the wrong event.** It used "track with most events"; a long recorder take was not guaranteed to be the reference.
   - Now: the master is the event with the longest continuous media span (`media_length_seconds`, else timeline length). Slaves analyze only the on-timeline portion.
5. **Short clip inside a long take.** Plain envelope cross-correlation is diluted when one signal is 100× longer.
   - Now: `CoarseAligner::find_envelope_lag` uses overlap-normalized cross-correlation (prefix-sum energy), then a 30 s GCC-PHAT refinement, then an acoustic-fingerprint fallback.
6. **Multicam video clips were ignored** (`track.IsAudio()` only).
   - Now: video-track events are extracted and aligned through their embedded audio (UI toggle **Include Video Clip Audio (Multicam)**, default on). Generator/still events are skipped.
7. **Double-moving linked video.** Companion shifting ran for every matching media path and could move events twice.
   - Now: `TimelineApplier` pre-computes aligned VEGAS `EventID`s and skips companions with their own result; only time-overlapping companions are linked.
8. **No cancellation and poor feedback.** `ProgressReporter::report` return values were ignored and the C# callback always returned 0.
   - Now: the pipeline aborts on cancellation (`VEGAS_SYNC_ERR_CANCELLED = -5`), the UI has a **Stop** button, and long decodes report progress.
9. **Negative resolved positions.** If sync pushed content before timeline zero, `AdjustStartLength` could fail.
   - Now: the engine shifts the whole synced block right and emits a warning.
10. **One bad file aborted the whole run.** Decode failures are now per-event warnings; the rest of the project still syncs.
11. **Hundreds of console windows.** VEGAS is a GUI process, so every `ffmpeg.exe` child got its own console window (1100+ on a large wedding timeline). FFmpeg is now spawned with `CREATE_NO_WINDOW` (`std::os::windows::process::CommandExt`), `-nostdin -nostats -loglevel error`, so decoding is completely invisible. A missing FFmpeg is also detected once per run instead of being re-probed (and re-spawned) for every event.

### v1.3 - Source-clock sync for short clips (critical fix)
The v1.2 behavior still failed on the real "Sync Wedding" project: 2–4 s camera clips produced **high-confidence but wrong** correlations, so clips were scattered, crossfaded, and every re-run added more lanes.

**Root cause found by analysing the real media** (`F:\My Edits\MOHAMMED\MOHAMMMED`):
- 174 video events (7 AX100 + 167 FX3) + 3 audio events, mastered against a **9 h 21 m** Olympus recorder.
- Most FX3 files are 1.4–5 s. GCC-PHAT returns a sharp peak (confidence 100%) for *any* 3 s pair, so per-clip confidence was meaningless. Envelope correlation (NCC) is the reliable discriminator (true matches 0.6–0.9, false ones 0.3–0.5).
- The FX3 folder contains **two camera clock bodies ~30.5 s apart**; naive nearest-anchor assignment created 30 s jumps in otherwise continuous footage.

**Implemented:**
- `EventDescriptor` now carries `source_key` (media folder) and `recorded_time_seconds` (from `Media.RecordedDateTime`).
- Per-event drafts keep their NCC score (also shown in the UI notes).
- `finalize_edges()` groups clips by folder, splits each group into **continuous recording runs** (recorded gap > 60 s), clusters implied camera clocks (2 s tolerance), and picks the dominant clock for the run. Every clip in the run is then placed absolutely from its recorded timestamp; strong clips that disagree are corrected as outliers.
- Clips without timestamps fall back to a rigid group shift derived from reliable anchors.
- Weak matches (NCC < 0.35 or < 1.5 s of audio) are left unaligned instead of being moved.
- `TimelineApplier` verifies each lane move (`ev.Track.Index`), reuses existing "Sync Lane N" tracks on re-runs, and caps lanes per track.

**Validation on the real files:** all 167 FX3 clips align against the 9 h master in ~30 s; 166/167 transitions match the recorded timeline exactly (the single 30.5 s step is the boundary between the two camera clock bodies). The AX100 group (7 clips) also aligns consistently. `TestHarness --realtest <master> <off> <len> <clip:|...>` reproduces this.

### v1.4.0 - Sync in Place + atomic grouped sync (activation)
Host gate (`TestHarness/verify_group_behavior.cs`) confirmed on VEGAS Pro 2027:
- `AdjustStartLength` on primary → companion auto-move: **NO**
- `Split` on primary → companion auto-split: **NO**
- `PlaybackRate` on primary → companion auto-rate: **NO**

Activated:
- `TimelineApplier.EnableAtomicGroupMove = true`
- `TimelineApplier.AutoMoveCompanionsInApplier = true`
- `preserve_timeline_order` remains default **false**; UI toggle **Sync in Place (Lock Order)**.
- Grouped A/V and multi-channel audio are analyzed through the lowest-`Track.Index` audio-capable primary and moved/split/rate-matched atomically inside the single `UndoBlock`, after pre-validating every member.
- Release installers: `TellTale_WaveSync_Setup_v1.4.exe` (Inno) and `TellTale_WaveSync_Setup_v1.4.msi` (WiX); both remove legacy `TellTaleWaveSync.dll*` / `vegas_sync_core.dll`.


### v1.3.1 - Manual master anchor
The Master Anchor dropdown previously listed **audio tracks only** and reset to Auto on every refresh, so a video/camera track could never be chosen.

- The dropdown now lists **every audio and video track** with `[Audio]/[Video]`, index, name, clip count and longest take, e.g. `[Video] 3: FX3 — 167 clips, longest 14m22s`.
- Selection is preserved across **Refresh** (matched by track id).
- Picking a video track automatically enables **Include Video Clip Audio** (otherwise that track would not be analyzed).
- The engine logs `Master reference (selected track): <event> (<file>, <duration>)`; if the chosen track has no analyzable events it warns and falls back to Auto instead of silently doing something else.
- `choose_master` unit tests cover "selected track uses its longest take" and "unknown track falls back to auto".

### v1.2 - Crossfade protection

12. **Overlapping synced events inserted unintended crossfades.** After alignment, events that used to live apart can land on top of one another on the same track.
    - `TimelineApplier` now runs `LanePartitioner` over every track that received a changed event.
    - Overlapping *moved* events are relocated onto new lanes of the correct media type: `Project.AddVideoTrack()` for video events (which carry their own audio), `Project.AddAudioTrack()` for audio-only events.
    - Unchanged clips are **pinned** to lane 0 and never moved, so pre-existing intentional overlaps are preserved. The partitioner only routes moved events around them.
    - New lanes copy the source track's name, mute/solo state, bus assignment, and a best-effort copy of the effect chain (`AddEffect` + `GetNativeState`/`SetNativeState`).
    - Everything happens inside the same `UndoBlock`, so one Ctrl+Z reverts the moves **and** the added lanes.
    - UI toggle: **Prevent Crossfades (Auto-Add Tracks)** (default on).
    - Pure algorithm unit test: `TestHarness.exe --lanetest` (400-interval stress test asserts no lane contains overlapping items and the lane count equals the maximum concurrent overlap).

### Earlier deliverables
- Event selection filter and sync calculation bugs resolved.
- UI modernized to CyberStudio dark theme, branded as **TellTale WaveSync**.
- Unnecessary subtitle under WaveSync and "SIMD PRO" badge removed.
- "Master Anchor" dropdown text set to black for readability.
- Inno Setup 6.7.3 at `C:\Users\criss\AppData\Local\Programs\Inno Setup 6\ISCC.exe`.
- Marketing & visual assets in `D:\VEGAS Extensions\VEGAS Audio Sync\`.

---

## 3. Environment & Gotchas
- **VEGAS Pro DLL Locking:** If `vegas2027.exe` is running, Windows locks `TellTale_Audiosync.dll` in `C:\ProgramData\Vegas Pro\2027.0\Application Extensions`. Close VEGAS first, or rename the DLL, before deploying.
- **Building:** `cargo build --release`, then `dotnet build VegasAudioSync\VegasAudioSync.csproj -c Release`. The csproj deploys `TellTale_Audiosync.dll` + `telltale_sync_core.dll` to `C:\ProgramData\Vegas Pro\2027.0\Application Extensions` automatically (`ContinueOnError=WarnAndContinue`).
- **WPF Offscreen Rendering:** Must run on an STA thread (`powershell -ExecutionPolicy Bypass -STA -File ...`) with an active `System.Windows.Application` context.
- **WPF XAML Quirks:** Standard WPF `TextBlock` does not support `LetterSpacing`. When writing XAML inside PowerShell scripts, use XML character entities (e.g., `&#x2022;`, `&#x2714;`, `&#x26A1;`) instead of raw UTF-8 symbols to avoid PowerShell 5.1 Windows-1252 encoding glitches.
- **Edge Local File Security:** Microsoft Edge blocks external CDN scripts on `file:///` URLs. Keep all HTML/CSS/JS self-contained inline.
- **FFmpeg:** Not bundled. Located via WinGet packages, common paths, or `PATH`. Without it the engine falls back to Symphonia (correct, but multi-hour takes are slower and momentarily memory-heavy during the fallback decode).
- **Long-take thresholds (Rust `engine.rs`):** `RETAIN_SAMPLES_SECONDS = 300`, `MAX_ANALYSIS_SECONDS = 6 h`, `DRIFT_SPAN_SECONDS = 600`, `DRIFT_MAX_OBSERVATIONS = 64`. Increasing the retain threshold increases RAM linearly.
- **No visible FFmpeg windows:** `AudioDecoder::stream_ffmpeg_audio` sets `CREATE_NO_WINDOW` on Windows. Do not remove it — without it VEGAS (a GUI process) flashes one console window per FFmpeg invocation.

---

## 4. Testing
```powershell
cd "D:\VEGAS Extensions\VEGAS Audio Sync\vegas_sync_core"
cargo test        # unit + integration, incl. tests\long_take_alignment_test.rs

cd ..
dotnet build "TestHarness\TestHarness.csproj" -c Release
& "TestHarness\bin\Release\net48\TestHarness.exe"            # FFI smoke test
& "TestHarness\bin\Release\net48\TestHarness.exe" --longtest # 7-min take + 3 clips (expect PASS)
& "TestHarness\bin\Release\net48\TestHarness.exe" --canceltest # cancellation via FFI
& "TestHarness\bin\Release\net48\TestHarness.exe" --lanetest   # crossfade lane partitioner
```
The long-take tests build synthetic "ceremony" recordings (noise bursts), cut short clips from known offsets, and assert sub-20 ms placement.

---

## 5. Constraints & Branding
- Always use the **TellTale WaveSync** brand name.
- Emphasize that the extension is **100% FREE** (no paywalls or subscriptions).
- Mirror any user-facing deliverables (installers, screenshots, thumbnails, presentations) to `D:\VEGAS Extensions\VEGAS Audio Sync\`.
