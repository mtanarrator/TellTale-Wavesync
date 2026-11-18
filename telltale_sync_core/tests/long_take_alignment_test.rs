use std::fs::File;
use std::io::Write;
use std::path::Path;

use vegas_sync_core::engine::run_sync_pipeline;
use vegas_sync_core::ffi::ProgressReporter;
use vegas_sync_core::models::*;

const SAMPLE_RATE: u32 = 16000;

fn write_wav_i16_mono(path: &Path, sample_rate: u32, samples: &[f32]) {
    let num_channels: u16 = 1;
    let bits_per_sample: u16 = 16;
    let byte_rate = sample_rate * num_channels as u32 * (bits_per_sample as u32 / 8);
    let block_align = num_channels * (bits_per_sample / 8);
    let data_len = samples.len() * block_align as usize;
    let chunk_size = 36 + data_len as u32;

    let mut data = Vec::with_capacity(data_len);
    for &s in samples {
        let v = (s.clamp(-1.0, 1.0) * 32000.0) as i16;
        data.extend_from_slice(&v.to_le_bytes());
    }

    let mut file = File::create(path).expect("create wav");
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
    file.write_all(&data).unwrap();
}

fn next_noise(state: &mut u64) -> f32 {
    *state = state
        .wrapping_mul(6364136223846793005)
        .wrapping_add(1442695040888963407);
    ((*state >> 33) as f32 / 2147483648.0) - 0.5
}

/// Simulates a long continuous recorder take: irregular speech-like noise bursts
/// every few seconds, with low-level room tone in between.
fn build_long_recording(seconds: f32) -> Vec<f32> {
    let n = (seconds * SAMPLE_RATE as f32) as usize;
    let mut samples = vec![0.0f32; n];
    let mut state: u64 = 0xDEADBEEFCAFEBABE;

    // Deterministic burst schedule: start time + duration + gain per burst.
    let mut schedule = Vec::new();
    let mut t = 0.35f32;
    let mut k: u32 = 0;
    while t < seconds {
        let dur = 0.25 + ((k * 37) % 23) as f32 * 0.09;
        let gain = 0.25 + ((k * 53) % 31) as f32 / 40.0;
        if t + dur < seconds {
            schedule.push((t, dur, gain));
        }
        t += 2.5 + ((k * 29) % 17) as f32 * 0.21;
        k += 1;
    }

    for i in 0..n {
        let time = i as f32 / SAMPLE_RATE as f32;
        let mut v = next_noise(&mut state) * 0.002; // room tone
        for &(start, dur, gain) in &schedule {
            if time >= start && time < start + dur {
                // Per-burst amplitude modulation keeps the envelope distinctive.
                let local = (time - start) / dur;
                v += next_noise(&mut state) * gain * (0.4 + 0.6 * (local * 3.1).sin().abs());
            }
        }
        samples[i] = v.clamp(-0.99, 0.99);
    }

    samples
}

fn make_event(
    event_id: &str,
    track_id: &str,
    path: &Path,
    timeline_start: f64,
    timeline_len: f64,
    media_start: f64,
    media_len: f64,
) -> EventDescriptor {
    EventDescriptor {
        event_id: event_id.to_string(),
        track_id: track_id.to_string(),
        file_path: path.to_string_lossy().to_string(),
        stream_index: 0,
        timeline_start_seconds: timeline_start,
        timeline_length_seconds: timeline_len,
        media_start_seconds: media_start,
        media_length_seconds: media_len,
        is_muted: false,
        source_key: None,
        recorded_time_seconds: None,
    }
}

#[test]
fn test_short_clips_inherit_source_group_shift() {
    // One reliable 60 s anchor and several noise-only 3 s clips from the same
    // "camera" folder. The short clips cannot be matched on their own; they must
    // inherit the anchor's rigid group shift.
    let seconds = 600.0f32;
    let long = build_long_recording(seconds);

    let temp_dir = std::env::temp_dir();
    let master_path = temp_dir.join("wavesync_group_master.wav");
    write_wav_i16_mono(&master_path, SAMPLE_RATE, &long);

    let true_anchor_pos = 200.0f64;
    let anchor_len = 60.0f64;
    let group_delta = -300.0f64;
    let anchor_timeline = true_anchor_pos - group_delta; // 500 s

    let anchor_start = (true_anchor_pos * SAMPLE_RATE as f64) as usize;
    let anchor_end = ((true_anchor_pos + anchor_len) * SAMPLE_RATE as f64) as usize;
    let anchor_path = temp_dir.join("wavesync_group_anchor.wav");
    write_wav_i16_mono(&anchor_path, SAMPLE_RATE, &long[anchor_start..anchor_end]);

    let short_positions = vec![210.0f64, 300.0, 400.0, 450.0, 550.0];
    let mut short_paths = Vec::new();

    for (i, _) in short_positions.iter().enumerate() {
        let mut state = 0x1234_5678_9ABC_DEF0u64 + i as u64;
        let noise: Vec<f32> = (0..(3.0 * SAMPLE_RATE as f32) as usize)
            .map(|_| next_noise(&mut state) * 0.5)
            .collect();
        let path = temp_dir.join(format!("wavesync_group_short_{}.wav", i));
        write_wav_i16_mono(&path, SAMPLE_RATE, &noise);
        short_paths.push(path);
    }

    // A noise clip with no source key must not be aligned on its own.
    let orphan_path = temp_dir.join("wavesync_group_orphan.wav");
    let mut state = 0xDEAD_BEEF_1234_5678u64;
    let orphan: Vec<f32> = (0..(3.0 * SAMPLE_RATE as f32) as usize)
        .map(|_| next_noise(&mut state) * 0.5)
        .collect();
    write_wav_i16_mono(&orphan_path, SAMPLE_RATE, &orphan);

    let mut cam_events = vec![make_event(
        "Ev_Anchor",
        "Track_Cam",
        &anchor_path,
        anchor_timeline,
        anchor_len,
        0.0,
        anchor_len,
    )];
    cam_events[0].source_key = Some("F:\\wedding\\camera".into());

    for (i, pos) in short_positions.iter().enumerate() {
        let mut ev = make_event(
            &format!("Ev_Short_{}", i),
            "Track_Cam",
            &short_paths[i],
            pos - group_delta,
            3.0,
            0.0,
            3.0,
        );
        ev.source_key = Some("F:\\wedding\\camera".into());
        cam_events.push(ev);
    }

    let orphan_event = make_event(
        "Ev_Orphan",
        "Track_Orphan",
        &orphan_path,
        50.0,
        3.0,
        0.0,
        3.0,
    );

    let request = SyncTaskRequest {
        project_sample_rate: 48000,
        master_track_id: Some("Track_Boom".into()),
        tracks: vec![
            TrackDescriptor {
                track_id: "Track_Boom".into(),
                name: "Recorder".into(),
                is_audio: true,
                events: vec![make_event(
                    "Ev_Master",
                    "Track_Boom",
                    &master_path,
                    0.0,
                    seconds as f64,
                    0.0,
                    seconds as f64,
                )],
            },
            TrackDescriptor {
                track_id: "Track_Cam".into(),
                name: "Camera".into(),
                is_audio: true,
                events: cam_events,
            },
            TrackDescriptor {
                track_id: "Track_Orphan".into(),
                name: "Unrelated".into(),
                is_audio: true,
                events: vec![orphan_event],
            },
        ],
        options: SyncOptions {
            internal_sample_rate: SAMPLE_RATE,
            detect_drift: false,
            detect_discontinuities: false,
            max_drift_ppm: 500.0,
            sub_sample_accuracy: true,
            speed_of_sound_compensation: false,
            voice_activity_filter: false,
            discontinuity_threshold_ms: 50.0,
            ffmpeg_path: None,
        },
    };

    let reporter = ProgressReporter::new(None, std::ptr::null_mut());
    let result = run_sync_pipeline(&request, &reporter);

    let _ = std::fs::remove_file(&master_path);
    let _ = std::fs::remove_file(&anchor_path);
    let _ = std::fs::remove_file(&orphan_path);
    for p in &short_paths {
        let _ = std::fs::remove_file(p);
    }

    assert!(result.is_ok(), "Pipeline failed: {:?}", result.err());
    let manifest = result.unwrap();

    let anchor = manifest
        .events
        .iter()
        .find(|e| e.event_id == "Ev_Anchor")
        .unwrap();
    assert!(anchor.is_aligned);
    assert!(
        (anchor.resolved_start_seconds - true_anchor_pos).abs() < 0.05,
        "anchor resolved to {:.3}s, expected {:.3}s",
        anchor.resolved_start_seconds,
        true_anchor_pos
    );

    for (i, expected) in short_positions.iter().enumerate() {
        let res = manifest
            .events
            .iter()
            .find(|e| e.event_id == format!("Ev_Short_{}", i))
            .unwrap();
        assert!(res.is_aligned, "Ev_Short_{} should inherit the group shift", i);
        assert!(
            (res.resolved_start_seconds - expected).abs() < 0.05,
            "Ev_Short_{} resolved to {:.3}s, expected {:.3}s",
            i,
            res.resolved_start_seconds,
            expected
        );
    }

    let orphan = manifest
        .events
        .iter()
        .find(|e| e.event_id == "Ev_Orphan")
        .unwrap();
    assert!(
        !orphan.is_aligned,
        "an unrelated short clip must stay unaligned"
    );

}

#[test]
fn test_recorded_timestamps_position_short_clips_absolutely() {
    // A reliable anchor establishes the camera clock; short clips are then placed
    // purely from their recorded timestamps, so the timeline arrangement does not
    // matter at all.
    let seconds = 600.0f32;
    let long = build_long_recording(seconds);

    let temp_dir = std::env::temp_dir();
    let master_path = temp_dir.join("wavesync_clock_master.wav");
    let anchor_path = temp_dir.join("wavesync_clock_anchor.wav");
    let short_a = temp_dir.join("wavesync_clock_short_a.wav");
    let short_b = temp_dir.join("wavesync_clock_short_b.wav");

    write_wav_i16_mono(&master_path, SAMPLE_RATE, &long);

    let anchor_pos = 200.0f64;
    let anchor_len = 60.0f64;
    let anchor_start = (anchor_pos * SAMPLE_RATE as f64) as usize;
    let anchor_end = ((anchor_pos + anchor_len) * SAMPLE_RATE as f64) as usize;
    write_wav_i16_mono(&anchor_path, SAMPLE_RATE, &long[anchor_start..anchor_end]);

    let mut state: u64 = 0xABCD_1234_5678_9EF0;
    let noise: Vec<f32> = (0..(3.0 * SAMPLE_RATE as f32) as usize)
        .map(|_| next_noise(&mut state))
        .collect();
    write_wav_i16_mono(&short_a, SAMPLE_RATE, &noise);
    write_wav_i16_mono(&short_b, SAMPLE_RATE, &noise);

    // Anchor recorded at wall-clock 1_000_000 and resolves to 200 s, so the camera
    // clock offset is 999_800 s relative to the master timeline.
    let anchor_recorded = 1_000_000.0f64;
    let clock = anchor_recorded - anchor_pos;

    let expected_a = 300.0f64;
    let expected_b = 450.0f64;

    let mut cam_events = vec![make_event(
        "Ev_Anchor",
        "Track_Cam",
        &anchor_path,
        500.0,
        anchor_len,
        0.0,
        anchor_len,
    )];
    cam_events[0].source_key = Some("cam".into());
    cam_events[0].recorded_time_seconds = Some(anchor_recorded);

    let mut ev_a = make_event("Ev_A", "Track_Cam", &short_a, 0.0, 3.0, 0.0, 3.0);
    ev_a.source_key = Some("cam".into());
    ev_a.recorded_time_seconds = Some(clock + expected_a);

    let mut ev_b = make_event("Ev_B", "Track_Cam", &short_b, 40.0, 3.0, 0.0, 3.0);
    ev_b.source_key = Some("cam".into());
    ev_b.recorded_time_seconds = Some(clock + expected_b);

    cam_events.push(ev_a);
    cam_events.push(ev_b);

    let request = SyncTaskRequest {
        project_sample_rate: 48000,
        master_track_id: Some("Track_Boom".into()),
        tracks: vec![
            TrackDescriptor {
                track_id: "Track_Boom".into(),
                name: "Recorder".into(),
                is_audio: true,
                events: vec![make_event(
                    "Ev_Master",
                    "Track_Boom",
                    &master_path,
                    0.0,
                    seconds as f64,
                    0.0,
                    seconds as f64,
                )],
            },
            TrackDescriptor {
                track_id: "Track_Cam".into(),
                name: "Camera".into(),
                is_audio: true,
                events: cam_events,
            },
        ],
        options: SyncOptions {
            internal_sample_rate: SAMPLE_RATE,
            detect_drift: false,
            detect_discontinuities: false,
            max_drift_ppm: 500.0,
            sub_sample_accuracy: true,
            speed_of_sound_compensation: false,
            voice_activity_filter: false,
            discontinuity_threshold_ms: 50.0,
            ffmpeg_path: None,
        },
    };

    let reporter = ProgressReporter::new(None, std::ptr::null_mut());
    let result = run_sync_pipeline(&request, &reporter);

    let _ = std::fs::remove_file(&master_path);
    let _ = std::fs::remove_file(&anchor_path);
    let _ = std::fs::remove_file(&short_a);
    let _ = std::fs::remove_file(&short_b);

    assert!(result.is_ok(), "Pipeline failed: {:?}", result.err());
    let manifest = result.unwrap();

    for (id, expected) in [("Ev_A", expected_a), ("Ev_B", expected_b)] {
        let res = manifest.events.iter().find(|e| e.event_id == id).unwrap();
        assert!(res.is_aligned, "{} should be aligned", id);
        assert!(
            (res.resolved_start_seconds - expected).abs() < 0.05,
            "{} resolved to {:.3}s, expected {:.3}s",
            id,
            res.resolved_start_seconds,
            expected
        );
    }
}

#[test]
fn test_long_take_against_long_take_and_negative_position_shift() {
    // Two multi-hour-style recorders, both above the retain threshold. The slave
    // started 10 s before the master, which places it at a negative timeline
    // position; the engine must shift the whole synced block into positive time.
    let seconds = 400.0f32;
    let long = build_long_recording(seconds);

    let master_samples = long.clone();
    let mut slave_samples = vec![0.0f32; long.len()];
    let lead = (10.0f32 * SAMPLE_RATE as f32) as usize;
    let copy_len = long.len().saturating_sub(lead);
    slave_samples[lead..lead + copy_len].copy_from_slice(&long[..copy_len]);

    let temp_dir = std::env::temp_dir();
    let master_path = temp_dir.join("wavesync_long_master_pair.wav");
    let slave_path = temp_dir.join("wavesync_long_slave_pair.wav");
    write_wav_i16_mono(&master_path, SAMPLE_RATE, &master_samples);
    write_wav_i16_mono(&slave_path, SAMPLE_RATE, &slave_samples);

    let request = SyncTaskRequest {
        project_sample_rate: 48000,
        master_track_id: Some("Track_A".into()),
        tracks: vec![
            TrackDescriptor {
                track_id: "Track_A".into(),
                name: "Recorder A".into(),
                is_audio: true,
                events: vec![make_event(
                    "Ev_A",
                    "Track_A",
                    &master_path,
                    3.0,
                    seconds as f64,
                    0.0,
                    seconds as f64,
                )],
            },
            TrackDescriptor {
                track_id: "Track_B".into(),
                name: "Recorder B".into(),
                is_audio: true,
                events: vec![make_event(
                    "Ev_B",
                    "Track_B",
                    &slave_path,
                    200.0,
                    (seconds as f64) - 10.0,
                    0.0,
                    (seconds as f64) - 10.0,
                )],
            },
        ],
        options: SyncOptions {
            internal_sample_rate: SAMPLE_RATE,
            detect_drift: true,
            detect_discontinuities: true,
            max_drift_ppm: 500.0,
            sub_sample_accuracy: true,
            speed_of_sound_compensation: false,
            voice_activity_filter: false,
            discontinuity_threshold_ms: 50.0,
            ffmpeg_path: None,
        },
    };

    let reporter = ProgressReporter::new(None, std::ptr::null_mut());
    let result = run_sync_pipeline(&request, &reporter);

    let _ = std::fs::remove_file(&master_path);
    let _ = std::fs::remove_file(&slave_path);

    assert!(result.is_ok(), "Pipeline failed: {:?}", result.err());
    let manifest = result.unwrap();

    let a = manifest.events.iter().find(|e| e.event_id == "Ev_A").unwrap();
    let b = manifest.events.iter().find(|e| e.event_id == "Ev_B").unwrap();

    // Unshifted positions would be A=3.0 and B=-7.0; the whole block is pushed +7 s.
    assert!(
        (a.resolved_start_seconds - 10.0).abs() < 0.02,
        "master resolved to {:.3}s, expected 10.000s",
        a.resolved_start_seconds
    );
    assert!(b.is_aligned, "slave should be aligned");
    assert!(
        (b.resolved_start_seconds - 0.0).abs() < 0.02,
        "slave resolved to {:.3}s, expected 0.000s",
        b.resolved_start_seconds
    );
    assert!(
        b.drift_ppm.abs() < 30.0,
        "unexpected drift {:.1} PPM for identical clocks",
        b.drift_ppm
    );
    assert!(
        manifest.warnings.iter().any(|w| w.contains("shifted")),
        "expected a negative-position warning, got {:?}",
        manifest.warnings
    );
}

#[test]
fn test_short_camera_clips_align_inside_long_audio_take() {
    // 7 minutes of "ceremony" audio. The master take is longer than the retain
    // threshold (300 s), so the streaming envelope + windowed refinement path is
    // exercised end to end.
    let long_seconds = 420.0f32;
    let long = build_long_recording(long_seconds);

    let temp_dir = std::env::temp_dir();
    let master_path = temp_dir.join("wavesync_long_master.wav");
    write_wav_i16_mono(&master_path, SAMPLE_RATE, &long);

    // Three short "camera clips" cut from different points in the ceremony.
    let clip_specs = [(31.5f64, 24.0f64), (205.25, 30.0), (389.0, 22.0)];
    let mut clip_paths = Vec::new();
    let mut slave_events = Vec::new();

    for (idx, &(clip_at, clip_len)) in clip_specs.iter().enumerate() {
        let s = (clip_at * SAMPLE_RATE as f64) as usize;
        let e = ((clip_at + clip_len) * SAMPLE_RATE as f64) as usize;
        let clip = long[s..e].to_vec();

        let path = temp_dir.join(format!("wavesync_camera_clip_{}.wav", idx));
        write_wav_i16_mono(&path, SAMPLE_RATE, &clip);

        // Deliberately wrong initial timeline placement (as if freshly dropped).
        slave_events.push(make_event(
            &format!("Ev_Cam_{}", idx),
            "Track_Cam",
            &path,
            600.0 + idx as f64 * 90.0,
            clip_len,
            0.0,
            clip_len,
        ));
        clip_paths.push(path);
    }

    let request = SyncTaskRequest {
        project_sample_rate: 48000,
        master_track_id: Some("Track_Boom".into()),
        tracks: vec![
            TrackDescriptor {
                track_id: "Track_Boom".into(),
                name: "Ceremony Recorder (long take)".into(),
                is_audio: true,
                events: vec![make_event(
                    "Ev_Boom_0",
                    "Track_Boom",
                    &master_path,
                    0.0,
                    long_seconds as f64,
                    0.0,
                    long_seconds as f64,
                )],
            },
            TrackDescriptor {
                track_id: "Track_Cam".into(),
                name: "Camera Clips".into(),
                is_audio: true,
                events: slave_events,
            },
        ],
        options: SyncOptions {
            internal_sample_rate: SAMPLE_RATE,
            detect_drift: false,
            detect_discontinuities: false,
            max_drift_ppm: 500.0,
            sub_sample_accuracy: true,
            speed_of_sound_compensation: false,
            voice_activity_filter: false,
            discontinuity_threshold_ms: 50.0,
            ffmpeg_path: None,
        },
    };

    let reporter = ProgressReporter::new(None, std::ptr::null_mut());
    let result = run_sync_pipeline(&request, &reporter);

    let _ = std::fs::remove_file(&master_path);
    for p in &clip_paths {
        let _ = std::fs::remove_file(p);
    }

    assert!(result.is_ok(), "Pipeline failed: {:?}", result.err());
    let manifest = result.unwrap();
    assert!(manifest.success);

    for (idx, &(clip_at, _)) in clip_specs.iter().enumerate() {
        let event_id = format!("Ev_Cam_{}", idx);
        let res = manifest
            .events
            .iter()
            .find(|e| e.event_id == event_id)
            .unwrap_or_else(|| panic!("missing result for {}", event_id));

        assert!(res.is_aligned, "{} should be aligned", event_id);
        assert!(
            (res.resolved_start_seconds - clip_at).abs() < 0.02,
            "{} resolved to {:.3}s, expected {:.3}s",
            event_id,
            res.resolved_start_seconds,
            clip_at
        );
        assert!(
            res.confidence > 0.5,
            "{} confidence too low: {:.2}",
            event_id,
            res.confidence
        );
    }
}

#[test]
fn test_short_clips_only_folder_still_aligns_individually() {
    // Regression test for C1: a folder containing ONLY clips shorter than
    // MIN_ANCHOR_SPAN (15s) must still align clips with strong individual audio.
    let long_seconds = 120.0f32;
    let long = build_long_recording(long_seconds);

    let temp_dir = std::env::temp_dir();
    let master_path = temp_dir.join("wavesync_shortfolder_master.wav");
    write_wav_i16_mono(&master_path, SAMPLE_RATE, &long);

    let clip_specs = [(18.0f64, 6.0f64), (72.5, 8.0)];
    let mut clip_paths = Vec::new();
    let mut slave_events = Vec::new();

    for (idx, &(clip_at, clip_len)) in clip_specs.iter().enumerate() {
        let s = (clip_at * SAMPLE_RATE as f64) as usize;
        let e = ((clip_at + clip_len) * SAMPLE_RATE as f64) as usize;
        let path = temp_dir.join(format!("wavesync_shortfolder_clip_{}.wav", idx));
        write_wav_i16_mono(&path, SAMPLE_RATE, &long[s..e]);

        let mut ev = make_event(
            &format!("Ev_ShortOnly_{}", idx),
            "Track_Cam",
            &path,
            300.0 + idx as f64 * 20.0,
            clip_len,
            0.0,
            clip_len,
        );
        ev.source_key = Some("F:\\wedding\\short_broll_folder".into());
        slave_events.push(ev);
        clip_paths.push(path);
    }

    let request = SyncTaskRequest {
        project_sample_rate: 48000,
        master_track_id: Some("Track_Boom".into()),
        tracks: vec![
            TrackDescriptor {
                track_id: "Track_Boom".into(),
                name: "Recorder".into(),
                is_audio: true,
                events: vec![make_event(
                    "Ev_Master",
                    "Track_Boom",
                    &master_path,
                    0.0,
                    long_seconds as f64,
                    0.0,
                    long_seconds as f64,
                )],
            },
            TrackDescriptor {
                track_id: "Track_Cam".into(),
                name: "B-Roll".into(),
                is_audio: true,
                events: slave_events,
            },
        ],
        options: SyncOptions {
            internal_sample_rate: SAMPLE_RATE,
            detect_drift: false,
            detect_discontinuities: false,
            ..SyncOptions::default()
        },
    };

    let reporter = ProgressReporter::new(None, std::ptr::null_mut());
    let result = run_sync_pipeline(&request, &reporter);

    let _ = std::fs::remove_file(&master_path);
    for p in &clip_paths {
        let _ = std::fs::remove_file(p);
    }

    let manifest = result.expect("Pipeline failed");
    for (idx, &(clip_at, _)) in clip_specs.iter().enumerate() {
        let id = format!("Ev_ShortOnly_{}", idx);
        let res = manifest.events.iter().find(|e| e.event_id == id).unwrap();
        assert!(res.is_aligned, "{} should align individually", id);
        assert!(
            (res.resolved_start_seconds - clip_at).abs() < 0.03,
            "{} resolved to {:.3}s, expected {:.3}s",
            id,
            res.resolved_start_seconds,
            clip_at
        );
    }
}

#[test]
fn test_misclocked_cameras_in_same_folder_keep_strong_audio_sync() {
    // Two cameras copied into the same folder with clocks 40s apart (within the
    // 60s segment gap): strong acoustic matches on Camera B must not be forced
    // onto Camera A's clock.
    let long_seconds = 160.0f32;
    let long = build_long_recording(long_seconds);

    let temp_dir = std::env::temp_dir();
    let master_path = temp_dir.join("wavesync_dualclock_master.wav");
    write_wav_i16_mono(&master_path, SAMPLE_RATE, &long);

    let specs = [
        ("CamA_0", 10.0f64, 20.0f64, 1_000_010.0f64),
        ("CamA_1", 40.0, 20.0, 1_000_040.0),
        ("CamA_2", 70.0, 20.0, 1_000_070.0),
        ("CamB_0", 110.0, 22.0, 1_000_085.0), // Camera B RTC is 25s slow relative to Cam A
    ];

    let mut paths = Vec::new();
    let mut events = Vec::new();
    for (i, &(id, start, len, rec)) in specs.iter().enumerate() {
        let s = (start * SAMPLE_RATE as f64) as usize;
        let e = ((start + len) * SAMPLE_RATE as f64) as usize;
        let path = temp_dir.join(format!("wavesync_dualclock_{}.wav", i));
        write_wav_i16_mono(&path, SAMPLE_RATE, &long[s..e]);

        let mut ev = make_event(id, "Track_Cams", &path, 400.0 + i as f64 * 30.0, len, 0.0, len);
        ev.source_key = Some("F:\\wedding\\shared_footage".into());
        ev.recorded_time_seconds = Some(rec);
        events.push(ev);
        paths.push(path);
    }

    let request = SyncTaskRequest {
        project_sample_rate: 48000,
        master_track_id: Some("Track_Boom".into()),
        tracks: vec![
            TrackDescriptor {
                track_id: "Track_Boom".into(),
                name: "Master".into(),
                is_audio: true,
                events: vec![make_event(
                    "Ev_Master",
                    "Track_Boom",
                    &master_path,
                    0.0,
                    long_seconds as f64,
                    0.0,
                    long_seconds as f64,
                )],
            },
            TrackDescriptor {
                track_id: "Track_Cams".into(),
                name: "Shared Folder".into(),
                is_audio: true,
                events,
            },
        ],
        options: SyncOptions {
            internal_sample_rate: SAMPLE_RATE,
            detect_drift: false,
            detect_discontinuities: false,
            ..SyncOptions::default()
        },
    };

    let reporter = ProgressReporter::new(None, std::ptr::null_mut());
    let result = run_sync_pipeline(&request, &reporter);

    let _ = std::fs::remove_file(&master_path);
    for p in &paths {
        let _ = std::fs::remove_file(p);
    }

    let manifest = result.expect("Pipeline failed");
    let cam_b = manifest.events.iter().find(|e| e.event_id == "CamB_0").unwrap();
    assert!(cam_b.is_aligned);
    assert!(
        (cam_b.resolved_start_seconds - 110.0).abs() < 0.03,
        "CamB_0 should keep its strong audio match at 110.0s, got {:.3}s",
        cam_b.resolved_start_seconds
    );
}

#[test]
fn test_multi_take_master_track_aligns_secondary_chapter() {
    // Master track has Ceremony (100s at t=0) and Reception (60s at t=500s) with
    // completely distinct audio. A camera clip recorded during Reception must sync
    // against the Reception take instead of failing against Ceremony.
    let ceremony = build_long_recording(100.0);
    let mut reception = vec![0.0f32; (60.0 * SAMPLE_RATE as f32) as usize];
    let mut state: u64 = 0xCAFE_BABE_1234_5678;
    for (i, sample) in reception.iter_mut().enumerate() {
        let t = i as f32 / SAMPLE_RATE as f32;
        let pulse = ((t * 1.7).sin().abs() > 0.7) as i32 as f32;
        *sample = next_noise(&mut state) * (0.01 + 0.7 * pulse);
    }

    let temp_dir = std::env::temp_dir();
    let cer_path = temp_dir.join("wavesync_multitake_cer.wav");
    let rec_path = temp_dir.join("wavesync_multitake_rec.wav");
    let cam_rec_path = temp_dir.join("wavesync_multitake_cam.wav");
    write_wav_i16_mono(&cer_path, SAMPLE_RATE, &ceremony);
    write_wav_i16_mono(&rec_path, SAMPLE_RATE, &reception);

    let clip_off = 22.0f64;
    let clip_len = 18.0f64;
    let s = (clip_off * SAMPLE_RATE as f64) as usize;
    let e = ((clip_off + clip_len) * SAMPLE_RATE as f64) as usize;
    write_wav_i16_mono(&cam_rec_path, SAMPLE_RATE, &reception[s..e]);

    let request = SyncTaskRequest {
        project_sample_rate: 48000,
        master_track_id: Some("Track_Master".into()),
        tracks: vec![
            TrackDescriptor {
                track_id: "Track_Master".into(),
                name: "Master Recorder".into(),
                is_audio: true,
                events: vec![
                    make_event("Ev_Ceremony", "Track_Master", &cer_path, 0.0, 100.0, 0.0, 100.0),
                    make_event("Ev_Reception", "Track_Master", &rec_path, 500.0, 60.0, 0.0, 60.0),
                ],
            },
            TrackDescriptor {
                track_id: "Track_Cam".into(),
                name: "Reception Cam".into(),
                is_audio: true,
                events: vec![make_event(
                    "Ev_CamRec",
                    "Track_Cam",
                    &cam_rec_path,
                    900.0,
                    clip_len,
                    0.0,
                    clip_len,
                )],
            },
        ],
        options: SyncOptions {
            internal_sample_rate: SAMPLE_RATE,
            detect_drift: false,
            detect_discontinuities: false,
            ..SyncOptions::default()
        },
    };

    let reporter = ProgressReporter::new(None, std::ptr::null_mut());
    let result = run_sync_pipeline(&request, &reporter);

    let _ = std::fs::remove_file(&cer_path);
    let _ = std::fs::remove_file(&rec_path);
    let _ = std::fs::remove_file(&cam_rec_path);

    let manifest = result.expect("Pipeline failed");
    let cam = manifest.events.iter().find(|e| e.event_id == "Ev_CamRec").unwrap();
    let expected = 500.0 + clip_off;
    assert!(cam.is_aligned, "Ev_CamRec should align to secondary master take");
    assert!(
        (cam.resolved_start_seconds - expected).abs() < 0.05,
        "Ev_CamRec resolved to {:.3}s, expected {:.3}s",
        cam.resolved_start_seconds,
        expected
    );
}
