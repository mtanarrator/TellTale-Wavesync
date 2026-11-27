//! End-to-end tests for the Milestone B global matching path.
//!
//! The audio is written as real WAV files because the `mock://` fixture decodes
//! to one identical synthetic tone for every event. That makes every mock pair
//! correlate at lag 0 and cannot express a chain where A↔B and B↔C match but
//! A↔C does not. The graph logic itself (constraints, priors, output rows) is
//! covered by pure unit tests inside `src/global_pipeline.rs`.

use std::fs::File;
use std::io::Write;

use telltale_sync_core::engine::run_sync_pipeline;
use telltale_sync_core::ffi::ProgressReporter;
use telltale_sync_core::models::*;

const SAMPLE_RATE: u32 = 16_000;
const SOURCE_SECONDS: f64 = 16.0;

fn write_wav_file(path: &str, sample_rate: u32, samples: &[f32]) {
    let num_samples = samples.len();
    let num_channels: u16 = 1;
    let bits_per_sample: u16 = 16;
    let byte_rate = sample_rate * num_channels as u32 * (bits_per_sample as u32 / 8);
    let block_align = num_channels * (bits_per_sample / 8);
    let data_len = num_samples * block_align as usize;
    let chunk_size = 36 + data_len as u32;

    let mut file = File::create(path).expect("Failed to create test WAV file");
    file.write_all(b"RIFF").unwrap();
    file.write_all(&chunk_size.to_le_bytes()).unwrap();
    file.write_all(b"WAVE").unwrap();
    file.write_all(b"fmt ").unwrap();
    file.write_all(&16u32.to_le_bytes()).unwrap();
    file.write_all(&1u16.to_le_bytes()).unwrap();
    file.write_all(&num_channels.to_le_bytes()).unwrap();
    file.write_all(&sample_rate.to_le_bytes()).unwrap();
    file.write_all(&byte_rate.to_le_bytes()).unwrap();
    file.write_all(&block_align.to_le_bytes()).unwrap();
    file.write_all(&bits_per_sample.to_le_bytes()).unwrap();
    file.write_all(b"data").unwrap();
    file.write_all(&(data_len as u32).to_le_bytes()).unwrap();

    for &sample in samples {
        let value = (sample.clamp(-1.0, 1.0) * 32_000.0) as i16;
        file.write_all(&value.to_le_bytes()).unwrap();
    }
}

/// A deterministic, non-repeating chirp with a pseudo-random amplitude gate.
/// Disjoint slices have unrelated energy envelopes, while overlapping slices
/// correlate strongly through both the envelope and the waveform.
fn source_signal() -> Vec<f32> {
    let total = (SOURCE_SECONDS * SAMPLE_RATE as f64) as usize;
    let mut samples = vec![0.0f32; total];
    let mut phase = 0.0f64;
    let mut gate = 0.0f64;
    let mut rng: u64 = 0x2545_F491_4F6C_DD1D;

    for (index, sample) in samples.iter_mut().enumerate() {
        let t = index as f64 / SAMPLE_RATE as f64;

        if index % 160 == 0 {
            rng = rng
                .wrapping_mul(6_364_136_223_846_793_005)
                .wrapping_add(1_442_695_040_888_963_407);
            let draw = ((rng >> 11) as f64) / ((1u64 << 53) as f64);
            gate = if draw < 0.30 {
                0.03
            } else {
                0.35 + 0.65 * draw
            };
        }

        // Sweep 180 Hz -> 2200 Hz over the whole 16 s source.
        let frequency = 180.0 + (2_200.0 - 180.0) * (t / SOURCE_SECONDS);
        phase += 2.0 * std::f64::consts::PI * frequency / SAMPLE_RATE as f64;
        let value = (phase.sin() + 0.35 * (2.0 * phase).sin()) * gate;
        *sample = value as f32;
    }

    samples
}

fn slice(signal: &[f32], start_seconds: f64, length_seconds: f64) -> Vec<f32> {
    let start = (start_seconds * SAMPLE_RATE as f64) as usize;
    let len = (length_seconds * SAMPLE_RATE as f64) as usize;
    signal[start..start + len].to_vec()
}

fn event(
    id: &str,
    track: &str,
    path: &str,
    start: f64,
    length: f64,
    source: Option<&str>,
) -> EventDescriptor {
    EventDescriptor {
        event_id: id.to_string(),
        track_id: track.to_string(),
        file_path: path.to_string(),
        stream_index: 0,
        timeline_start_seconds: start,
        timeline_length_seconds: length,
        media_start_seconds: 0.0,
        media_length_seconds: length,
        is_muted: false,
        source_key: source.map(str::to_string),
        playback_rate: 1.0,
        ..EventDescriptor::default()
    }
}

fn request(tracks: Vec<TrackDescriptor>, global_matching: bool) -> SyncTaskRequest {
    SyncTaskRequest {
        project_sample_rate: SAMPLE_RATE,
        master_track_id: None,
        tracks,
        groups: Vec::new(),
        options: SyncOptions {
            preserve_timeline_order: false,
            internal_sample_rate: SAMPLE_RATE,
            global_matching,
            detect_drift: false,
            detect_discontinuities: false,
            voice_activity_filter: false,
            ffmpeg_path: None,
            ..SyncOptions::default()
        },
    }
}

fn track(id: &str, event: EventDescriptor) -> TrackDescriptor {
    TrackDescriptor {
        track_id: id.to_string(),
        name: id.to_string(),
        is_audio: true,
        events: vec![event],
    }
}

#[test]
fn global_chain_aligns_through_the_middle_clip() {
    let dir = std::env::temp_dir().join(format!(
        "wavesync_global_chain_{}_{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ));
    std::fs::create_dir_all(&dir).expect("create temp dir");

    let signal = source_signal();
    // A: source 0-8 s, B: 4-12 s, C: 8-16 s. A and C share no audio, but both
    // overlap B, and B knows both sides of the chain.
    let path_a = dir.join("a.wav");
    let path_b = dir.join("b.wav");
    let path_c = dir.join("c.wav");
    write_wav_file(
        path_a.to_str().unwrap(),
        SAMPLE_RATE,
        &slice(&signal, 0.0, 8.0),
    );
    write_wav_file(
        path_b.to_str().unwrap(),
        SAMPLE_RATE,
        &slice(&signal, 4.0, 8.0),
    );
    write_wav_file(
        path_c.to_str().unwrap(),
        SAMPLE_RATE,
        &slice(&signal, 8.0, 8.0),
    );

    let tracks = vec![
        track(
            "Track_A",
            event("A", "Track_A", path_a.to_str().unwrap(), 0.0, 8.0, None),
        ),
        track(
            "Track_B",
            event("B", "Track_B", path_b.to_str().unwrap(), 100.0, 8.0, None),
        ),
        track(
            "Track_C",
            event("C", "Track_C", path_c.to_str().unwrap(), 200.0, 8.0, None),
        ),
        // Disconnected b-roll-like node with no usable clock.
        track(
            "Track_D",
            event(
                "D",
                "Track_D",
                "mock://global-prior-only",
                900.0,
                5.0,
                Some("mock|broll"),
            ),
        ),
        // Disconnected node with a clock but no audio edge.
        {
            let mut clock_only = event(
                "E",
                "Track_E",
                "mock://global-clock-only",
                1_500.0,
                5.0,
                Some("mock|clock"),
            );
            clock_only.recorded_time_seconds = Some(1_800_000_000.0);
            track("Track_E", clock_only)
        },
    ];

    let reporter = ProgressReporter::new(None, std::ptr::null_mut());
    let result = run_sync_pipeline(&request(tracks, true), &reporter);
    let _ = std::fs::remove_dir_all(&dir);
    let manifest = result.expect("global pipeline must succeed");

    let find = |id: &str| {
        manifest
            .events
            .iter()
            .find(|event| event.event_id == id)
            .unwrap_or_else(|| panic!("missing result for {id}"))
    };

    // The two-hop chain was used: exactly two audio edges (A↔B, B↔C) and no A↔C.
    assert!(
        manifest
            .warnings
            .iter()
            .any(|warning| warning.contains("2 audio edge(s)")),
        "expected exactly two audio edges, warnings: {:?}",
        manifest.warnings
    );

    // Global mode must not claim a master reference when the user did not pin one.
    assert!(
        manifest.master_track_id.is_empty(),
        "global mode reported a false master: '{}'",
        manifest.master_track_id
    );

    let a = find("A");
    let b = find("B");
    let c = find("C");
    for result in [a, b, c] {
        assert!(result.is_aligned, "{} must be aligned", result.event_id);
        assert!(
            result
                .notes
                .as_deref()
                .unwrap_or("")
                .contains("global audio graph"),
            "{} notes: {:?}",
            result.event_id,
            result.notes
        );
        assert!(
            result.confidence >= 0.30,
            "{} confidence {}",
            result.event_id,
            result.confidence
        );
    }

    // B is 4 s after A, C is 4 s after B, all through the middle clip.
    let ab = b.resolved_start_seconds - a.resolved_start_seconds;
    let bc = c.resolved_start_seconds - b.resolved_start_seconds;
    assert!((ab - 4.0).abs() < 0.05, "B-A was {ab}");
    assert!((bc - 4.0).abs() < 0.05, "C-B was {bc}");

    // Disconnected nodes are never dragged into the solved block.
    let d = find("D");
    assert!((d.resolved_start_seconds - 900.0).abs() < 1e-6);
    assert!(d.delta_seconds.abs() < 1e-6);
    assert!(!d.is_aligned);
    assert!(
        d.notes
            .as_deref()
            .unwrap_or("")
            .contains("no audio edge and no usable clock"),
        "D notes: {:?}",
        d.notes
    );

    let e = find("E");
    assert!((e.resolved_start_seconds - 1_500.0).abs() < 1e-6);
    assert!(e.delta_seconds.abs() < 1e-6);
    assert!(!e.is_aligned);
    assert!(
        e.notes
            .as_deref()
            .unwrap_or("")
            .contains("clock-placed (no audio edge)"),
        "E notes: {:?}",
        e.notes
    );
    assert!(e.confidence <= 0.3);
}

#[test]
fn sparse_graph_falls_back_to_the_legacy_pipeline() {
    let dir = std::env::temp_dir().join(format!(
        "wavesync_global_sparse_{}_{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ));
    std::fs::create_dir_all(&dir).expect("create temp dir");

    let signal = source_signal();
    let path_a = dir.join("a.wav");
    let path_b = dir.join("b.wav");
    write_wav_file(
        path_a.to_str().unwrap(),
        SAMPLE_RATE,
        &slice(&signal, 0.0, 8.0),
    );
    write_wav_file(
        path_b.to_str().unwrap(),
        SAMPLE_RATE,
        &slice(&signal, 8.0, 8.0),
    );

    // Far apart on the timeline, no usable clock agreement: no temporally
    // plausible candidate pair can be formed, so the global graph is empty.
    let mut a = event("A", "Track_A", path_a.to_str().unwrap(), 0.0, 8.0, None);
    a.recorded_time_seconds = Some(1_781_481_600.0);
    let mut b = event("B", "Track_B", path_b.to_str().unwrap(), 1_000.0, 8.0, None);
    b.recorded_time_seconds = Some(1_519_869_600.0); // stale 2018 clock
    let tracks = vec![track("Track_A", a), track("Track_B", b)];

    let reporter = ProgressReporter::new(None, std::ptr::null_mut());
    let result = run_sync_pipeline(&request(tracks, true), &reporter);
    let _ = std::fs::remove_dir_all(&dir);
    let manifest = result.expect("legacy fallback must still produce a manifest");

    assert!(
        manifest
            .warnings
            .iter()
            .any(|warning| warning.contains("falling back to the legacy single-anchor pipeline")),
        "expected a fallback warning, warnings: {:?}",
        manifest.warnings
    );
    // The global diagnostics must survive a sparse fallback.
    assert!(
        manifest
            .warnings
            .iter()
            .any(|warning| warning.contains("clock not trusted")),
        "expected the untrusted-clock warning to survive, warnings: {:?}",
        manifest.warnings
    );
    assert!(
        !manifest.events.iter().any(|event| event
            .notes
            .as_deref()
            .unwrap_or("")
            .contains("global audio graph")),
        "the global path must not produce audio-graph placements"
    );
}

#[test]
fn global_matching_with_preserve_timeline_uses_the_legacy_dst_path() {
    let mut a = event("A", "Track_A", "mock://preserve-a", 0.0, 10.0, Some("cam"));
    let mut b = event("B", "Track_B", "mock://preserve-b", 40.0, 10.0, Some("cam"));
    a.recorded_time_seconds = Some(1_800_000_000.0);
    b.recorded_time_seconds = Some(1_800_000_020.0);

    let mut sync_request = request(vec![track("Track_A", a), track("Track_B", b)], true);
    sync_request.options.preserve_timeline_order = true;

    let reporter = ProgressReporter::new(None, std::ptr::null_mut());
    let manifest = run_sync_pipeline(&sync_request, &reporter).expect("legacy DST path must run");

    assert!(
        manifest
            .warnings
            .iter()
            .any(|warning| warning.contains("Force audio-only placement (DST) enabled")),
        "expected the DST warning, warnings: {:?}",
        manifest.warnings
    );
    assert!(
        !manifest
            .warnings
            .iter()
            .any(|warning| warning.contains("audio edge(s)")),
        "the global summary must not run, warnings: {:?}",
        manifest.warnings
    );
    assert!(!manifest.events.iter().any(|event| event
        .notes
        .as_deref()
        .unwrap_or("")
        .contains("global audio graph")));
}

#[test]
fn zero_usable_audio_edges_falls_back() {
    // Two B-roll clips pair acoustically, but B-roll ↔ B-roll edges are
    // discarded. The plan then has zero audio edges and must not emit a global
    // manifest.
    let mut a = event("A", "Track_A", "mock://zero-a", 0.0, 5.0, Some("broll-src"));
    let mut b = event("B", "Track_B", "mock://zero-b", 2.0, 5.0, Some("broll-src"));
    a.sync_role = "broll".to_string();
    b.sync_role = "broll".to_string();

    let reporter = ProgressReporter::new(None, std::ptr::null_mut());
    let manifest = run_sync_pipeline(
        &request(vec![track("Track_A", a), track("Track_B", b)], true),
        &reporter,
    )
    .expect("legacy fallback must produce a manifest");

    assert!(
        manifest.warnings.iter().any(|warning| warning
            .contains("no usable audio edge after filtering")
            || warning.contains("falling back to the legacy")),
        "expected the zero-edge fallback warning, warnings: {:?}",
        manifest.warnings
    );
    assert!(
        !manifest
            .warnings
            .iter()
            .any(|warning| warning.contains("audio edge(s)")),
        "the global summary must not run with zero audio edges, warnings: {:?}",
        manifest.warnings
    );
    assert!(!manifest.events.iter().any(|event| event
        .notes
        .as_deref()
        .unwrap_or("")
        .contains("global audio graph")));
}

#[test]
fn legacy_path_is_unchanged_when_global_matching_is_off() {
    let dir = std::env::temp_dir().join(format!(
        "wavesync_global_off_{}_{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ));
    std::fs::create_dir_all(&dir).expect("create temp dir");

    let signal = source_signal();
    let path_a = dir.join("a.wav");
    let path_b = dir.join("b.wav");
    write_wav_file(
        path_a.to_str().unwrap(),
        SAMPLE_RATE,
        &slice(&signal, 0.0, 8.0),
    );
    write_wav_file(
        path_b.to_str().unwrap(),
        SAMPLE_RATE,
        &slice(&signal, 4.0, 8.0),
    );

    let tracks = vec![
        track(
            "Track_A",
            event("A", "Track_A", path_a.to_str().unwrap(), 0.0, 8.0, None),
        ),
        track(
            "Track_B",
            event("B", "Track_B", path_b.to_str().unwrap(), 100.0, 8.0, None),
        ),
    ];

    let reporter = ProgressReporter::new(None, std::ptr::null_mut());
    let result = run_sync_pipeline(&request(tracks, false), &reporter);
    let _ = std::fs::remove_dir_all(&dir);
    let manifest = result.expect("legacy pipeline must succeed");

    assert!(
        !manifest
            .warnings
            .iter()
            .any(|warning| warning.contains("Global matching")),
        "global matching must be silent when disabled, warnings: {:?}",
        manifest.warnings
    );
}
