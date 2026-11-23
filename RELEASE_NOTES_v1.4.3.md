# TellTale WaveSync v1.4.3

**High-precision audio and multi-camera synchronization for VEGAS Pro.**

Website: [www.telltale.video](https://www.telltale.video)  
Authored by **Mohamed Anis** (**TellTale FX**)

---

## Highlights

### Ignore Camera Clock Jumps (DST)
Placement comes from audio correlation, not recorded timestamps. Each source receives one rigid shift, so a camera clock that jumps an hour forward or backward — a daylight-saving change — cannot move, reorder, or misplace clips.

While this mode is ON, **Clock Drift Correction**, **Dropped-Frame Auto-Slicing**, and **Prevent Crossfades (Auto-Add Tracks)** are switched off and locked, because they cannot take effect in rigid-shift mode. Clicking a locked option explains the trade-off and lets you override it.

### Grouped atomic sync
Grouped multi-channel audio and grouped A/V events are analyzed through the top-most (`lowest Track.Index`) audio-capable primary only. Every member is then moved, split, and rate-matched atomically inside one `UndoBlock`, preserving internal offsets. Members are pre-validated, and a failed group is rolled back rather than partially moved.

### Edit-safe apply
Results are matched back to clips by VEGAS' persistent `EventID`, not by timeline index. If you move, trim, split, or delete a clip while a long analysis is running, that result is skipped — it is never applied to a different clip that drifted into the old index.

### Honest completion status
The status line reports moved, skipped, and unaligned counts. Partial results say **Completed with warnings** instead of **Success**.

### DST clock-jump detection
When two anchor clusters imply source clocks about one hour apart (3300–3900 s), the run log warns about a possible daylight-saving change and points to **Ignore Camera Clock Jumps (DST)**.

### Persistent settings
Processing toggles and the master anchor are restored on the next VEGAS session. Settings live at:

```text
%LOCALAPPDATA%\TellTale\WaveSync\settings.json
```

A restored master anchor is only accepted when its ID, name, and audio/video kind still match, so opening a different project safely falls back to Auto.

### Engine health check
The status line shows the extension and native-engine versions. A missing engine disables **Sync** with a reinstall message; an unexpected engine family is flagged instead of silently reporting **Ready**.

### Cancellation and recovery
Cancellation is honored until apply begins. During the `UndoBlock`, Stop is disabled so it does not promise to interrupt a committed pass. Closing the dock cancels a running analysis.

---

## Install

1. Close VEGAS Pro.
2. Download `TellTale_WaveSync_Setup_v1.4.3.exe`.
3. Run the installer. It installs to:

   ```text
   %ProgramData%\Vegas Pro\2027.0\Application Extensions
   ```

   and removes legacy `TellTaleWaveSync.dll*` / `vegas_sync_core.dll` files.
4. Start VEGAS Pro and open the TellTale WaveSync panel.

**MSI alternative:** `TellTale_WaveSync_Setup_v1.4.3.msi`

---

## Requirements

- VEGAS Pro 2027 (x64)
- Windows 10 or 11 (x64)
- .NET Framework 4.8
- FFmpeg recommended for fast long-take and video-container decoding (optional)

---

## Checksums

```text
1C978ACFBEEA917A3DECF1070ACCC597331FFC65AE42D08EB4EFD52923ABBCCD  TellTale_WaveSync_Setup_v1.4.3.exe
AA3B798B9CD62494CFD640745D1A8FD84ED2D0094B45DBD7245FD170E9760BFF  TellTale_WaveSync_Setup_v1.4.3.msi
```

Verify on Windows:

```powershell
Get-FileHash .\TellTale_WaveSync_Setup_v1.4.3.exe -Algorithm SHA256
```

---

## Notes

- **The v1.4.3 installer is not yet code-signed.** Windows SmartScreen may show **Unknown publisher**; verify the SHA-256 above before running.
- Full release history: [CHANGELOG.md](CHANGELOG.md).
- License: Apache 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).
