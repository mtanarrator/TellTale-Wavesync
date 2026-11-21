# TellTale WaveSync

**100% Free, High-Precision Audio & Multi-Camera Synchronizer for VEGAS Pro**

TellTale WaveSync is a native VEGAS Pro extension powered by a SIMD-accelerated Rust audio analysis engine (`telltale_sync_core.dll`) and a modern WPF interface (`TellTale_Audiosync.dll`). It automatically aligns multi-camera footage, external audio recorders, and podcasts directly on your VEGAS Pro timeline.

---

## Key Features

- **1-Click Multi-Cam & Audio Sync:** Automatically aligns selected events or the entire project timeline.
- **Recorded-Time Source Sync (v1.3):** Cameras that record many short clips are grouped by media folder. A few long clips establish the camera's clock, and every short clip is then positioned from its embedded recorded timestamp — so 2–4 second clips no longer produce random high-confidence matches.
- **Long-Take / Wedding Support:** Handles recorder takes that are hours long and longer than the footage. Long media is streamed into a compact energy envelope and only small windows are ever loaded into RAM — so a 2-hour ceremony take no longer stalls or exhausts memory.
- **Match Validation (NCC):** Envelope correlation scores gate every alignment. Weak or ambiguous matches are left untouched instead of being moved to a wrong position.
- **Multicam Video Clip Audio:** Camera clips on video tracks are analyzed through their embedded scratch audio, so footage can be aligned directly (toggleable).
- **Clock Drift Correction:** Uses RANSAC linear regression and dynamic PPM audio resampling to eliminate gradual desync between different recording devices.
- **Sub-Sample Super-Resolution:** Parabolic peak interpolation for fractional-sample accuracy (eliminating phase/echo issues).
- **Dropped-Frame Auto-Slicing:** Automatically splits timeline events at Variable Frame Rate (VFR) camera drop discontinuities (e.g., phone footage).
- **Voice Activity Noise Gate:** Optional `-40 dB` noise gate to attenuate quiet room ambience before matching.
- **Master Anchor Selection:** Auto-detects the optimal reference (longest continuous take), or you can pin the master to any audio **or video** track. The dropdown shows every track with its clip count and longest take; the engine uses the longest take on the selected track, and also supports secondary takes on the master track for multi-chapter timelines.
- **Crossfade Protection:** If aligned events would overlap on a track (which makes VEGAS insert crossfades), enable **Prevent Crossfades (Auto-Add Tracks)** to automatically add extra tracks of the correct type — video lanes for video clips (which keep their own audio), audio lanes for audio-only events — so nothing intersects.
- **Cancellable & Memory-Bounded:** The **Stop** button aborts a running analysis, and memory use stays flat even with hundreds of long clips. All optional processing toggles start **OFF** by default so you can enable only what your project needs.

---

## Installation

### Option 1: Windows MSI Installer (Recommended)
Run **`TellTale_WaveSync_Setup_v1.3.1.msi`**. The installer places all required binaries into `%ProgramData%\Vegas Pro\2027.0\Application Extensions` and automatically removes any prior Inno Setup WaveSync installation and registry entries.

### Option 2: Manual Installation
Copy the compiled extension DLLs (`TellTale_Audiosync.dll`, `telltale_sync_core.dll`, and dependencies) into:
```text
C:\ProgramData\Vegas Pro\<version>\Application Extensions\
```

### FFmpeg (recommended)
FFmpeg is used for fast, seekable decoding of long takes and video containers. TellTale WaveSync looks for `ffmpeg.exe` in the WinGet packages folder, common install paths, and `PATH`. If FFmpeg is missing, the extension still works, but multi-hour takes are decoded with the slower built-in decoder.

---

## Long Takes & Wedding Workflows

Wedding/event projects typically contain one long continuous recorder take (often 1–4 hours) plus many short camera clips. TellTale WaveSync is built for that shape:

1. **Master anchor:** Auto-detect picks the event with the longest continuous take, so every short clip is located *inside* the long recording.
2. **Streaming analysis:** Long takes are decoded in chunks into a 125 Hz log-energy envelope. Only the small window needed for sample-accurate refinement is materialized.
3. **Bounded FFTs:** Correlation is always performed on windows of 30–600 seconds, never on multi-hour buffers.
4. **Flat memory:** Clips are processed in small parallel batches (max 4 workers); buffers are released as soon as each clip is resolved.
5. **Whole-take movement:** The long take is moved as a single event and keeps its full length — trim it manually afterwards if needed.
6. **Negative positions:** If sync would push content before timeline zero, the whole synced block is shifted right (logged as a warning) so relative sync is preserved.
7. **No crossfades:** After alignment, tracks that received moved events are checked for overlaps. Overlapping sync results are spread onto extra lanes of the matching type (video tracks for video clips, audio tracks for audio-only events). Clips the sync did not touch stay pinned in place, so intentional pre-existing edits are preserved. Disable with **Prevent Crossfades (Auto-Add Tracks)** if you prefer manual track management.
8. **Short-clip source sync:** Clips from one camera folder are treated as one source. Continuous recording runs are detected from the media creation timestamps; a dominant clock cluster is estimated from the longer, reliable clips; every other clip in the run is placed from its recorded timestamp. This is what makes 2–4 second clips reliable, and it keeps all clips of one camera moving together.

---

## How sync decisions are made

- Each event gets a first-pass draft alignment using normalized envelope cross-correlation + GCC-PHAT.
- Matches with an envelope NCC below ~0.35 are discarded (do-no-harm), because short/unrelated audio can still produce a sharp GCC peak.
- For each media folder, contiguous recording runs are reconstructed from file timestamps. A 2-second clock cluster is preferred; a single disagreeing strong match is overridden so continuous footage stays continuous.
- Clips without timestamps fall back to a single rigid group shift derived from the reliable clips, which preserves the editor's relative arrangement.
- Timestamps come from VEGAS' own `Media.RecordedDateTime` (the camera's creation time). If a file has no timestamp, audio matching is used instead.

**Note:** recorded-time positioning places clips at their true chronological position. If you intentionally reordered clips for storytelling, expect the sync to restore the recorded order; always review before saving (one Ctrl+Z reverts the whole pass).

---

## Repository Structure

| Path | Description |
| :--- | :--- |
| `vegas_sync_core/` | Rust native audio synchronization engine (`telltale_sync_core.dll`) |
| `TellTale_Audiosync.dll` / `VegasAudioSync/` | C# (.NET 4.8) VEGAS Pro extension & WPF UI (`TellTale_Audiosync.dll`) |
| `Installer/` | WiX v4 MSI source (`TellTaleWaveSync.wxs`, `License.rtf`) & legacy Inno script |
| `TestHarness/` | Standalone verifier — run with `--longtest` for the long-take wedding scenario |
| `TellTale_WaveSync_Setup_v1.3.1.msi` | Compiled Windows MSI installer |
| `TellTale_WaveSync_Presentation.html` | Edge-compatible offline fullscreen presentation deck |
| `telltale_wavesync_screenshot.png` | Clean offscreen-rendered UI screenshot |
| `TellTale_WaveSync_YouTube_Thumbnail.png` | 1280×720 YouTube showcase thumbnail |

---

## Building from Source

### 1. Rust Core Engine (`vegas_sync_core`)
```powershell
cd "D:\VEGAS Extensions\VEGAS Audio Sync\vegas_sync_core"
cargo test
cargo build --release
```

### 2. C# VEGAS Pro Extension (`VegasAudioSync`)
```powershell
dotnet build "VegasAudioSync\VegasAudioSync.csproj" -c Release
```
Targets **.NET Framework 4.8** (x64). Ensure `telltale_sync_core.dll` is copied alongside `TellTale_Audiosync.dll` (the build does this automatically when the Rust release DLL exists).

### 3. Windows Installer (`Installer`)
Compile `Installer\TellTaleWaveSync_Installer.iss` using **Inno Setup 6** (`ISCC.exe`).

---

## Verification

```powershell
cd vegas_sync_core
cargo test                      # 30 tests, including long-take, source-clock and master-anchor suites

cd ..
dotnet build TestHarness\TestHarness.csproj -c Release
TestHarness\bin\Release\net48\TestHarness.exe            # FFI smoke test
TestHarness\bin\Release\net48\TestHarness.exe --longtest # 7-minute take + 3 camera clips
TestHarness\bin\Release\net48\TestHarness.exe --canceltest # verifies the Stop button path
```
