# TellTale WaveSync v1.5.0

**High-precision audio and multi-camera synchronization for VEGAS Pro.**

Website: [www.telltale.video](https://www.telltale.video)  
Authored by **Mohamed Anis** (**TellTale FX**)

---

## Highlights

### Dropped-frame slicing now works on its own
**Repair dropped frames** no longer requires **Fix gradual sync drift**. The measured step jump is carried through the engine, the post-drop segment is shifted back into sync, and the duplicated tail is trimmed so the repair stays on a single track instead of creating a new lane. Inserted-frame jumps are clamped so they cannot collide with the next clip.

### Playback rate survives re-runs
An event's existing `PlaybackRate` is sent to the engine and its length is scaled to preserve the media span. The engine reports an absolute target, so running twice does not compound the correction. WaveSync records the rates it writes per project and event: its own earlier stretch can be refined or cleared, while a deliberate manual retime is preserved. Placement and split timestamps are rate-aware.

### Project-switch safety
Opening or closing a project during analysis now cancels the run and discards the manifest, so results can never be applied to the wrong project. Project and track-count changes also refresh the track list automatically.

### Copyable activity log
The activity log is a read-only text box with a **Copy Log** button. Updates are batched several times per second instead of rebuilding the whole buffer for every line, so 1,000-clip runs stay responsive.

### Honest engine diagnostics
Engine warnings — including partial FFmpeg decodes and unreadable chunks — now appear in the run log instead of being silently dropped. A failed native-library load reports the Win32 error and path.

### Correct defaults and lanes
**Include video clips** and **Keep overlapping clips on separate tracks** now start **ON**, matching the documentation; an explicitly saved `false` still wins. New Sync Lanes create missing automation envelopes and copy points plus base Volume/Pan/Composite levels without duplicating the time-zero anchor.

### Organize Tracks by Device
A new **Organize Tracks by Device…** action scans timeline media, groups clips by recording device, and opens a preview before touching the timeline.

- Device identity uses camera metadata first (make/model), then filename prefix plus folder, then folder; confidence is shown per group.
- The preview lets you rename, include/exclude, merge selected groups, and split by folder.
- Cameras get exactly one `CAM n — Name (V)` track. A camera clip is a single video event with embedded audio — even if the camera mic is silent — so no camera audio track is created, copied, or muted. Recorders get `REC n — Name`. Nothing is duplicated, so Sync never analyzes the same camera audio twice.
- The pass is one Undo step and original tracks are kept. Re-runs reuse tracks.
- The operation ignores **Include video clips** and **Selected clips only**. Recommended order: **organize first, then synchronize**.

### A friendlier panel
- **Quick setup presets:** Multicam, Long recording, Damaged footage, and Reset defaults set the whole option matrix in one click; individual toggles still work as before.
- **Options grouped by intent:** What to sync, Handle problems, Result layout, and a collapsed **Advanced** section, with plain-language labels and a tooltip on every toggle.
- **Result summary:** after a run, see moved/skipped/unaligned counts, new Sync Lanes, the reference track actually used, and a Ctrl+Z undo reminder.
- **Warnings-only log filter:** keep the full history but show only warnings, errors, and skipped-clip notes when you need signal over noise.
- **Better use of space:** the decorative banner is gone, the log keeps a usable fixed height, and the Sync/Stop buttons stay pinned while the panel scrolls in small docks.

### Installer fixes
The EXE installer now ranks VEGAS version folders numerically (`2027.0` beats `22.0`, `10.0` beats `9.0`), builds from any checkout location, cleans obsolete binaries in sibling version folders, and removes those untracked copies on uninstall. The MSI no longer probes `HKCU` from its SYSTEM-context action.

> **MSI scope:** the MSI targets **VEGAS Pro 2027** only. Use the EXE installer for VEGAS 19–22 as well.

---

## Compatibility

- Existing manifests and settings files still load. `split_points_seconds` is still emitted; a missing `playback_rate` reads as `1.0`.
- The native engine ABI is unchanged (`telltale_sync_core v0.2.x`).

## Known limitation

- Grouped A/V lane relocation is implemented but disabled pending VEGAS host validation. Overlapping grouped clips remain on their original track and the run log says so. `TestHarness\verify_group_track_move.cs` is the probe for that validation.
