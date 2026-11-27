# TellTale WaveSync

**100% Free, Open-Source (Apache 2.0), High-Precision Audio & Multi-Camera Synchronizer for VEGAS Pro**  
Website: [www.telltale.video](https://www.telltale.video) · Authored by **Mohamed Anis** (**TellTale FX**)

TellTale WaveSync is a native VEGAS Pro extension powered by a SIMD-accelerated Rust audio analysis engine (`telltale_sync_core.dll`) and a modern WPF interface (`TellTale_Audiosync.dll`). It automatically aligns multi-camera footage, external audio recorders, and podcasts directly on your VEGAS Pro timeline.

---

## Download

| | |
| :--- | :--- |
| **Latest release** | [v2.0.0](https://github.com/mtanarrator/TellTale-Wavesync/releases/latest) |
| **Installer (recommended)** | [TellTale_WaveSync_Setup_v2.0.0.exe](https://github.com/mtanarrator/TellTale-Wavesync/releases/download/v2.0.0/TellTale_WaveSync_Setup_v2.0.0.exe) |
| **MSI alternative** | [TellTale_WaveSync_Setup_v2.0.0.msi](https://github.com/mtanarrator/TellTale-Wavesync/releases/download/v2.0.0/TellTale_WaveSync_Setup_v2.0.0.msi) |
| **Checksums** | [SHA256SUMS.txt](SHA256SUMS.txt) |
| **Release notes** | [RELEASE_NOTES_v2.0.0.md](RELEASE_NOTES_v2.0.0.md) |

> The v2.0.0 installer is not yet code-signed. Windows SmartScreen may show **Unknown publisher**; verify the SHA-256 in `SHA256SUMS.txt` before running.

---

## Key Features

- **1-Click Multi-Cam & Audio Sync:** Automatically aligns selected events or the entire project timeline.
- **Recorded-Time Source Sync (v1.3):** Cameras that record many short clips are grouped by media folder. A few long clips establish the camera's clock, and every short clip is then positioned from its embedded recorded timestamp — so 2–4 second clips no longer produce random high-confidence matches.
- **Global matching (v2.0):** Matches clips pairwise and solves the whole project as one relative graph, so no single master take is required. On by default, with automatic fallback to the legacy master-anchor pipeline. See [Global matching (v2)](#global-matching-v2).
- **Force audio-only placement / DST (v1.4.1):** Placement comes from audio correlation, not recorded timestamps. Each source receives one rigid shift, so a camera clock that jumps an hour forward or backward (a daylight-saving change) cannot move, reorder, or misplace clips. No tracks are added; overlaps are reported instead of auto-laned. While this mode is ON, **Fix gradual sync drift**, **Repair dropped frames**, and **Keep overlapping clips on separate tracks** are switched off and locked - they cannot take effect in this mode. Clicking a locked option explains the trade-off and lets you override it (which turns this mode off).
- **Grouped Atomic Sync (v1.4):** Grouped multi-channel audio and grouped A/V events are analyzed through a single audio-capable primary (the audio member when one exists), then moved, split and rate-matched as one unit while preserving every member's internal offset. Host-verified against VEGAS Pro 2027: VEGAS does not auto-move/split/rate companions, so the applier does it atomically inside one UndoBlock.
- **Edit-Safe Apply (v1.4.2):** Results are matched back to clips by VEGAS' persistent `EventID` and checked against the position captured at analysis. If you move, trim, split, or delete a clip while a long sync is running, that result is skipped - it is never applied to a different clip that drifted into the old index.
- **Honest Completion Status (v1.4.2):** The status line reports moved, skipped, and unaligned counts. Partial results say **Completed with warnings** instead of **Success**.
- **DST Clock-Jump Detection (v1.4.2):** If a source's implied camera clocks differ by about one hour, the run log flags a possible daylight-saving change and points you to **Force audio-only placement (DST)**.
- **Persistent Settings (v1.4.3):** Your processing toggles and master anchor are restored on the next VEGAS session (stored under `%LOCALAPPDATA%\TellTale\WaveSync\settings.json`).
- **Engine Health Check (v1.4.3):** The status line shows the extension and native-engine versions. A missing native engine disables **Sync** with a clear reinstall message; a version mismatch is flagged instead of silently reporting **Ready**.
- **Long-Take / Wedding Support:** Handles recorder takes that are hours long and longer than the footage. Long media is streamed into a compact energy envelope and only small windows are ever loaded into RAM — so a 2-hour ceremony take no longer stalls or exhausts memory.
- **Match Validation (NCC):** Envelope correlation scores gate every alignment. Weak or ambiguous matches are left untouched instead of being moved to a wrong position.
- **Include video clips:** Camera clips on video tracks are analyzed through their embedded scratch audio, so footage can be aligned directly (toggleable).
- **Fix gradual sync drift:** Uses RANSAC linear regression and dynamic PPM audio resampling to eliminate gradual desync between different recording devices.
- **Maximum precision (slower):** Parabolic peak interpolation for fractional-sample accuracy (eliminating phase/echo issues). Advanced.
- **Repair dropped frames:** Automatically splits timeline events at Variable Frame Rate (VFR) camera drop discontinuities (e.g., phone footage).
- **Ignore background noise:** Optional `-40 dB` noise gate to attenuate quiet room ambience before matching. Advanced.
- **Master Anchor Selection:** **Auto (recommended)** picks the longest continuous take, or you can pin the master to any audio **or video** track. The dropdown shows every track with its clip count and longest take; the engine uses the longest take on the selected track, and also supports secondary takes on the master track for multi-chapter timelines.
- **Crossfade Protection:** If aligned events would overlap on a track (which makes VEGAS insert crossfades), enable **Keep overlapping clips on separate tracks** to automatically add extra tracks of the correct type — video/audio lanes for grouped A/V clips (which move as one unit), audio lanes for audio-only events — so nothing intersects.
- **Organize Tracks by Device/Folder:** scan timeline media, group clips by recording device or media folder, preview the grouping, then build **one video track per camera** (`CAM n — Name (V)`) plus a **paired audio track** (`CAM n — Name (A)`) when that camera already has a companion audio event, and **one audio track per recorder** (`REC n — Name`). Existing camera audio is moved onto the paired track — never duplicated, copied, or muted. A camera with no companion audio gets only its `(V)` track. The preview lets you rename, include/exclude, merge, or split by folder; the whole pass is one Undo step and original tracks are kept. This operation ignores the **Include video clips** and **Selected clips only** scope toggles. Recommended order: **organize first, then Sync**.
- **Cancellable & Memory-Bounded:** The **Stop** button aborts a running analysis, and memory use stays flat even with hundreds of long clips. Video clip audio and crossfade prevention start **ON**; the remaining optional processing toggles start **OFF** so you can enable only what your project needs.

---

## Installation

### Option 1: Windows Installer (Recommended)
Download and run **`TellTale_WaveSync_Setup_v2.0.0.exe`** from the Releases page. The installer places all required binaries into the newest detected VEGAS Pro version folder under `%ProgramData%\Vegas Pro\` or `%ProgramData%\VEGAS Pro\`, and removes legacy WaveSync files. It can also install to every detected VEGAS version in one pass.

### Option 2: Windows MSI
Download and run **`TellTale_WaveSync_Setup_v2.0.0.msi`**. It installs the same binaries and removes any prior Inno Setup WaveSync installation and registry entries.

> **Note:** the MSI currently targets **VEGAS Pro 2027** only. Use the EXE installer if you need VEGAS 19–22 as well.

### Option 3: Manual Installation
Copy the compiled extension DLLs (`TellTale_Audiosync.dll`, `telltale_sync_core.dll`, and dependencies) into:
```text
C:\ProgramData\Vegas Pro\<version>\Application Extensions\
```

### FFmpeg (recommended)
FFmpeg is used for fast, seekable decoding of long takes and video containers. TellTale WaveSync looks for `ffmpeg.exe` in the WinGet packages folder, common install paths, and `PATH`. If FFmpeg is missing, the extension still works, but multi-hour takes are decoded with the slower built-in decoder.

---

## Long Takes & Wedding Workflows

Wedding/event projects typically contain one long continuous recorder take (often 1–4 hours) plus many short camera clips. TellTale WaveSync is built for that shape:

1. **Master anchor:** **Auto (recommended)** picks the event with the longest continuous take, so every short clip is located *inside* the long recording.
2. **Streaming analysis:** Long takes are decoded in chunks into a 125 Hz log-energy envelope. Only the small window needed for sample-accurate refinement is materialized.
3. **Bounded FFTs:** Correlation is always performed on windows of 30–600 seconds, never on multi-hour buffers.
4. **Flat memory:** Clips are processed in small parallel batches (max 4 workers); buffers are released as soon as each clip is resolved.
5. **Whole-take movement:** The long take is moved as a single event and keeps its full length — trim it manually afterwards if needed.
6. **Negative positions:** If sync would push content before timeline zero, the whole synced block is shifted right (logged as a warning) so relative sync is preserved.
7. **No crossfades:** After alignment, tracks that received moved events are checked for overlaps. Overlapping sync results are spread onto extra lanes of the matching type (video tracks for video clips, audio tracks for audio-only events). Clips the sync did not touch stay pinned in place, so intentional pre-existing edits are preserved. Disable with **Keep overlapping clips on separate tracks** if you prefer manual track management.
8. **Short-clip source sync:** Clips from one camera folder are treated as one source. Continuous recording runs are detected from the media creation timestamps; a dominant clock cluster is estimated from the longer, reliable clips; every other clip in the run is placed from its recorded timestamp. This is what makes 2–4 second clips reliable, and it keeps all clips of one camera moving together.

---

## How sync decisions are made

> With **Global matching** on (the v2 default), placement comes from the global graph described in the next section. With it off — or after an automatic fallback — the master-anchor pipeline below runs unchanged.

- Each event gets a first-pass draft alignment using normalized envelope cross-correlation + GCC-PHAT.
- Matches with an envelope NCC below ~0.35 are discarded (do-no-harm), because short/unrelated audio can still produce a sharp GCC peak.
- For each media folder, contiguous recording runs are reconstructed from file timestamps. A 2-second clock cluster is preferred; a single disagreeing strong match is overridden so continuous footage stays continuous.
- Clips without timestamps fall back to a single rigid group shift derived from the reliable clips, which preserves the editor's relative arrangement.
- Timestamps come from VEGAS' own `Media.RecordedDateTime` (the camera's creation time). If a file has no timestamp, audio matching is used instead.

**Note:** recorded-time positioning places clips at their true chronological position. If you intentionally reordered clips for storytelling, expect the sync to restore the recorded order; always review before saving (one Ctrl+Z reverts the whole pass). If the camera crossed a daylight-saving boundary - or its clock otherwise jumped by an hour - enable **Force audio-only placement (DST)**: recorded timestamps are then ignored for placement and each source is shifted rigidly from audio.

---

## Global matching (v2)

Starting with **v2.0.0**, TellTale WaveSync can match clips through a **global audio graph** instead of anchoring every clip to one master take. It is **on by default** and can be turned off in **Advanced → Global matching (recommended)**. If the graph is too sparse or the project is too large, WaveSync logs a warning and automatically runs the legacy master-anchor pipeline, so existing projects keep working.

### How the global graph works

- Clips are compared pairwise with temporally plausible neighbours, and the accepted links are solved together. No single master recording is required; a selected master only pins the overall position gauge.
- Clips from the same device are connected by **sequence/order constraints**, so a continuous camera or recorder run stays in recording order where audio alone is ambiguous.
- Clips with no audio edge are placed from their clock/order when that evidence is usable and marked **`clock-placed`** in the manifest and run log. They are not reported as audio-verified.
- **B-roll** (clips shorter than 15 s, typically short camera cutaways) never acts as an anchor. It may receive one audio edge; otherwise it is placed by clock/order and labelled **B-roll**.
- Components with no audio evidence and no usable clock keep their current timeline position instead of being moved on a guess. Each run reports audio-aligned, clock-placed, B-roll, and unaligned counts.

### Time source and clip order

Both controls are in **Advanced** and persist between sessions:

| Control | Options | What it decides |
| :--- | :--- | :--- |
| **Time source** | Auto (recommended), Recorded date, Timecode, Filename timestamp, File modified time, Ignore | Which clock is trusted for ordering and placing clips without an audio edge. **Auto** prefers recorded date → container timecode → filename timestamp → file modified time. |
| **Clip order** | Auto (recommended), Strict file/recorded order, Timecode order, Preserve timeline order, No ordering | How consecutive clips from the same device are ordered when audio cannot decide. |

`Preserve timeline order` keeps the existing rigid-shift/DST behavior and therefore runs the legacy path rather than the global graph.

### Device identity

- Cameras are named from camera metadata or the model token in their filenames — for example **Sony A7 III** and **Sony FX3** — instead of relying only on the folder name.
- Recorders keep their folder identity, so two Tascam folders stay separate: **Tascam_wesele (audio1)** and **Tascam_wesele (audio2)**.
- **Organize Tracks by Device/Folder before Sync** is recommended. Each device gets its own correctly named track, and Sync sees one source per device.
- A camera's video stays on its `(V)` track. If the project already has companion camera audio, the organizer places it on the paired `(A)` track. Audio is never duplicated, copied, or muted; cameras without companion audio get only `(V)`.

### Usage note

Run **Organize Tracks by Device/Folder** first, then **Sync**. Keep Global matching on for normal multi-camera and multi-recorder work, and review any clip the run marks `clock-placed`. If you want to compare against the previous behavior, turn Global matching off (or choose **Preserve timeline order**) and run the same project copy through the legacy path. Always review before saving; one Ctrl+Z reverts the whole pass.

### Limitations, stated plainly

- **Bad clocks are not trusted.** A device whose clock is more than 30 days away from the project median — for example a camera stamped 2018 in a 2026 project — is marked untrusted. Its epoch is not used for sequence placement; ordering falls back to timecode/filename order or timeline gaps, and the run log names the device.
- **Cross-device unsynced clips are not guaranteed sub-second.** When two devices share no usable audio, placement depends on clocks and order. Sample-accurate sync is only claimed where audio actually overlaps.
- **Global mode falls back automatically** when there are more than 400 unique media paths, roughly 512 MB of retained audio, fewer than two analyzable events, or a sparse/sequence-only audio graph. The run log says which path ran.
- **Duplicate audio from older builds should be cleaned up manually.** v2 does not duplicate camera audio or create extra recorder tracks, but it does not delete duplicate tracks that earlier versions already added.
- **Impossible device clocks and destructive placements are refused.** If a device's timestamps pack more media into their span than the files can physically contain (for example 137 minutes of recorder takes stamped inside 9 minutes), v2 keeps that device's existing timeline spacing instead of compressing it. If a solved placement would make a device's own clips overlap heavily, that device is left at its original position and the log says so. Those clips are reported as unaligned rather than moved into a guessing layout.

---

## Repository Structure

| Path | Description |
| :--- | :--- |
| `vegas_sync_core/` | Rust native audio synchronization engine (`telltale_sync_core.dll`) — crate name is kept for FFI/ABI stability |
| `VegasAudioSync/` | C# (.NET 4.8) VEGAS Pro extension and WPF UI (`TellTale_Audiosync.dll`) |
| `Installer/` | Inno Setup (`TellTaleWaveSync_Installer.iss`) and WiX v4 MSI (`TellTaleWaveSync.wxs`) sources |
| `TestHarness/` | Standalone verifier — `--longtest`, `--ordertest`, `--grouptest`, `--lanetest`, `--canceltest` |
| `LICENSE` / `NOTICE` | Apache License 2.0 and attribution |
| `CHANGELOG.md` | Full release history (v1.0 → v2.0.0) |
| `RELEASE_NOTES_v2.0.0.md` | GitHub release body for global matching and trustworthy metadata |
| `RELEASE_NOTES_v1.4.3.md` | GitHub release body and install notes |
| `SHA256SUMS.txt` | Checksums for the v2.0.0 installers |
| `TellTale_WaveSync_Presentation.html` | Offline fullscreen presentation deck |
| `telltale_wavesync_screenshot.png` | Offscreen-rendered UI screenshot |
| `TellTale_WaveSync_YouTube_Thumbnail.png` | 1280×720 showcase thumbnail |

---

## Building from Source

### 1. Rust Core Engine (`vegas_sync_core`)
```powershell
cd vegas_sync_core
cargo test
cargo build --release
```
The output is `vegas_sync_core\target\release\telltale_sync_core.dll`.

### 2. C# VEGAS Pro Extension (`VegasAudioSync`)
```powershell
dotnet build VegasAudioSync\VegasAudioSync.csproj -c Release
```
Targets **.NET Framework 4.8** (x64). The build copies `telltale_sync_core.dll` next to `TellTale_Audiosync.dll` when the Rust release DLL exists.

### 3. Windows Installers (`Installer`)
```powershell
# Inno Setup 6
ISCC.exe Installer\TellTaleWaveSync_Installer.iss

# WiX v4
wix build Installer\TellTaleWaveSync.wxs -arch x64 -ext WixToolset.UI.wixext -o Installer\Output\TellTale_WaveSync_Setup_v2.0.0.msi
```

---

## Verification

```powershell
cd vegas_sync_core
cargo test                      # 117 tests: global graph/fallback, clocks, long-take, source-clock, preserve-order

cd ..
dotnet build TestHarness\TestHarness.csproj -c Release
TestHarness\bin\Release\net48\TestHarness.exe              # FFI smoke test
TestHarness\bin\Release\net48\TestHarness.exe --longtest   # long take + camera clips
TestHarness\bin\Release\net48\TestHarness.exe --ordertest  # DST / order preservation
TestHarness\bin\Release\net48\TestHarness.exe --grouptest  # atomic group planner
TestHarness\bin\Release\net48\TestHarness.exe --lanetest   # crossfade lane planner
TestHarness\bin\Release\net48\TestHarness.exe --canceltest # Stop button path
```

---

## License

Apache License 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
