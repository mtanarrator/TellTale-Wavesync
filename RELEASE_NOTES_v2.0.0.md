# TellTale WaveSync v2.0.0

**Global matching, trustworthy device metadata, and honest placement for VEGAS Pro.**

Website: [www.telltale.video](https://www.telltale.video)  
Authored by **Mohamed Anis** (**TellTale FX**)

---

## Highlights

### Global matching is the new default

WaveSync no longer needs one master recording to locate everything else. Clips are compared pairwise with temporally plausible neighbours, the accepted audio links form a graph, and all positions are solved together as relative offsets. That makes multi-camera projects without a single continuous reference — and projects where the best reference changes from scene to scene — solvable in one pass.

- **On by default.** The toggle lives in **Advanced → Global matching (recommended)** and persists between sessions. Turning it off restores the legacy master-anchor behavior.
- **No single master required.** The selected master anchor only pins the overall position; it is not a precondition for matching.
- **Same-device order is protected.** Consecutive clips from one camera or recorder are linked by sequence constraints, so continuous runs stay in recording order even where audio alone is ambiguous.
- **Clear run reporting.** The summary distinguishes audio-aligned, clock-placed, B-roll, and unaligned clips, and the log states whether the global or legacy path ran.

### Automatic fallback keeps large and awkward projects safe

Global matching is not forced onto inputs it cannot support. WaveSync logs a warning and runs the unchanged legacy pipeline when:

- there are more than **400 unique media paths** or roughly **512 MB** of retained audio;
- fewer than two events can be analyzed;
- the audio graph is sparse or sequence-only, with no genuine connected evidence.

The fallback is automatic and non-destructive. Legacy behavior is also used when **Preserve timeline order** is selected.

### Time source and clip order are now explicit controls

Two new **Advanced** controls make the engine's assumptions visible and adjustable:

| Control | Options |
| :--- | :--- |
| **Time source** | Auto (recommended), Recorded date, Timecode, Filename timestamp, File modified time, Ignore |
| **Clip order** | Auto (recommended), Strict file/recorded order, Timecode order, Preserve timeline order, No ordering |

**Auto** time source prefers recorded date → container timecode → filename timestamp → file modified time. These controls decide how clips are ordered and how unsynced clips are placed when audio cannot decide. Both settings are restored with the rest of the panel.

### Bad clocks are detected instead of trusted

A device clock that is more than **30 days** away from the project median is treated as untrusted — for example, a camera whose container date says 2018 in a 2026 project. Its timestamp is not used for epoch-based sequence offsets. Ordering falls back to timecode/filename order or timeline gaps, and the warning names the affected device.

Container timecode is handled as seconds-of-day rather than a date, and is unwrapped across midnight so a run that crosses 23:59 does not jump backwards.

### Camera audio is paired, not duplicated

The organizer builds a **`CAM n — Name (V)`** track per camera and, when the camera already has companion audio events, a paired **`CAM n — Name (A)`** track. Existing audio is moved onto the paired track; nothing is copied, duplicated, or muted. A camera with no companion audio gets only its `(V)` track. Recorders still get one `REC n — Name` audio track per folder.

### Metadata probing stays off the VEGAS UI thread

Organizer and sync take a fast COM snapshot on VEGAS's UI thread, then collect ffprobe metadata in parallel on background workers. Scanning no longer blocks the panel, **Stop** remains available during the metadata pass, and the project-generation guard still discards results if the project changes. Crossfade protection now relocates overlapping grouped camera units as whole units to adjacent Sync Lanes below their source track.

Recorder clocks can also be *compressed* rather than simply wrong: 137 minutes of takes stamped inside a 9-minute span. When that happens the engine keeps the device's current timeline spacing instead of squeezing the takes together. And if a solved placement would make a device's own clips overlap heavily, that device is left at its original position and the run log names the refusal. Camera sync is preserved; an ambiguous recorder block is never scattered.

### B-roll and unsynced clips are labelled honestly

- Clips without an audio edge are placed from their clock/order when that evidence exists and marked **`clock-placed`** in the manifest and run log.
- Clips with neither audio evidence nor a usable clock keep their current position instead of being moved on a guess.
- **B-roll** (clips shorter than 15 s, typically short camera cutaways) never anchors a sequence. It may receive one audio edge; otherwise it is placed by clock/order and labelled **B-roll**.
- These labels are advisory. They do not by themselves change track assignment.

### Device identity is consistent everywhere

The organizer and the sync engine now share one device-identity rule:

- Cameras are named from camera metadata or the model token in the filename — **Sony A7 III** and **Sony FX3**, not two generic folder groups.
- Recorders keep their folder identity, so **Tascam_wesele (audio1)** and **Tascam_wesele (audio2)** remain separate sources and separate audio tracks.
- The organizer preview reports Camera / Recorder / B-roll roles where applicable.

**Recommended order remains: Organize Tracks by Device/Folder first, then Sync.** A camera's video stays on its `(V)` track; if companion audio exists, the organizer places it on the paired `(A)` track. Audio is never duplicated, copied, or muted, and the two recorder folders are never merged into one source.

### Tests and rollback

The v2 work was validated against synthetic global chains (A↔B↔C where A and C share no direct edge), sparse-fallback decisions, untrusted-clock selection, compressed-clock guards, B-roll anchoring, and a legacy-parity check with global matching off. Rust tests now cover 117 cases. The pre-v2 build was archived before the change as `Backups\WaveSync_legacy_20261127_123741.zip`.

---

## Compatibility

- **Global matching defaults to ON.** An explicitly saved `false` is honored and restores the legacy path.
- Existing settings files load unchanged; missing v2 fields default to **Global matching on**, **Time source Auto**, and **Clip order Auto**.
- Existing manifests still load. The new placement notes are additive; split-point and playback-rate fields keep their previous meaning.
- **Preserve timeline order** and the DST rigid-shift mode continue to use the legacy pipeline.
- Installation, FFmpeg discovery, and VEGAS version support are unchanged from v1.5.0.

---

## Known limitations

- **Not a Syncaila parity claim.** v2 improves relative matching and metadata handling; it does not reproduce every Syncaila workflow or guarantee identical results.
- **Cross-device unsynced clips are not guaranteed sub-second.** When two devices share no usable audio, position comes from clocks and order. Sample-accurate accuracy is only claimed where audio actually overlaps.
- **Untrusted clocks reduce ordering evidence.** A device with a bad clock can still be placed by timecode, filename, timeline order, or audio edges — but its epoch is deliberately ignored, so its order may differ from the camera's real shooting order.
- **Global mode falls back for scale or sparsity.** Above 400 unique media paths, roughly 512 MB retained audio, fewer than two analyzable events, or a sparse/sequence-only graph, the legacy pipeline runs instead.
- **Duplicate audio from older builds is not removed automatically.** Clean up duplicate camera-audio or recorder tracks created by earlier versions before organizing and syncing.
- **B-roll classification is informational.** The 15-second threshold can label a genuinely short normal clip as B-roll; it does not change which track the clip belongs to.
- **Clock-placed clips should be reviewed.** They are not audio-verified, and the log says so.
- **Impossible or destructive device layouts are refused.** If a device's timestamps are physically impossible, v2 keeps its current timeline spacing. If placing a device would overlap its own clips heavily, v2 leaves that device at its original position and reports it as unaligned. This is deliberate: camera sync is preserved and the recorder block is never scattered into dozens of lanes.

---

## What to test

Use the **Regression Case** fixture — 112 timeline events over 66 unique media files (46 grouped camera A/V primaries + 20 recorder WAVs):

1. **Organization:** scanning reports camera and recorder groups — `CAM 1 — Sony A7 III (V)` and `(A)`, `CAM 2 — Sony FX3 (V)` and `(A)`, `REC 1 — Tascam_wesele (audio1)`, `REC 2 — Tascam_wesele (audio2)`. The paired `(A)` tracks contain the existing camera audio events; no new audio events are created.
2. **Untrusted clock:** the A7 III's 2018 container date produces an untrusted-clock warning that names **Sony A7 III**, and its epoch is not used for sequence offsets.
3. **B-roll:** 17 short clips (under 15 s) are counted as B-roll; no normal 15 s+ clip is misclassified.
4. **Dual recorders:** the two Tascam folders stay separate, each receives its own audio track, and no audio is duplicated or muted.
5. **Global run:** a default run completes and the summary/log contains `Path: Global matching` with counts where audio-aligned + clock-placed + unaligned = 66 analyzed primaries (112 events with grouped companions).
6. **Recorder guard:** because the Tascam filenames fit 137 minutes of media into 9 minutes, the log warns that timeline spacing is kept; any recorder block that would overlap is left at its original position, reported as unaligned, with **no new sync lanes**.
7. **Legacy baseline:** a copy of the project with Global matching **off** logs `Path: Legacy master anchor` and completes coherently; compare moved/unaligned counts and clip-level differences.
8. **Fallback:** a 1–2 clip project or an ambiguous subset logs the global fallback warning and completes through the legacy path.
9. **Persistence:** the toggle and both clocks survive closing/reopening VEGAS; deleting `settings.json` returns to global on / Auto / Auto; a saved `global_matching: false` stays off.
10. **No duplicate audio:** after organizing, each camera's video is on `(V)`, existing companion audio on the paired `(A)`, and each recorder folder has exactly one audio track; nothing is duplicated or muted.
11. **Responsiveness:** on a cold or larger project, scanning keeps VEGAS responsive and **Stop** cancels during the metadata pass without applying partial results.
