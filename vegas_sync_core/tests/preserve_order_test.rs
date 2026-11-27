use std::fs::File;
use std::io::Write;
use std::path::Path;

use telltale_sync_core::engine::run_sync_pipeline;
use telltale_sync_core::ffi::ProgressReporter;
use telltale_sync_core::models::*;

const RATE: u32 = 16000;

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

fn build_long_recording(seconds: f32) -> Vec<f32> {
    let n = (seconds * RATE as f32) as usize;
    let mut samples = vec![0.0f32; n];
    let mut state: u64 = 0xA5A5_1234_DEAD_BEEF;
    let mut schedule = Vec::new();
    let mut t = 0.25f32;
    let mut k: u32 = 0;
    while t < seconds {
        let dur = 0.3 + ((k * 31) % 19) as f32 * 0.1;
        let gain = 0.3 + ((k * 47) % 29) as f32 / 50.0;
        if t + dur < seconds {
            schedule.push((t, dur, gain));
        }
        t += 2.2 + ((k * 23) % 13) as f32 * 0.27;
        k += 1;
    }

    for i in 0..n {
        let time = i as f32 / RATE as f32;
        let mut v = next_noise(&mut state) * 0.002;
        for &(start, dur, gain) in &schedule {
            if time >= start && time < start + dur {
                let local = (time - start) / dur;
                v += next_noise(&mut state) * gain * (0.4 + 0.6 * (local * 3.0).sin().abs());
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
    length: f64,
    source_key: Option<&str>,
    recorded_time: Option<f64>,
) -> EventDescriptor {
    EventDescriptor {
        event_id: event_id.to_string(),
        track_id: track_id.to_string(),
        file_path: path.to_string_lossy().to_string(),
        stream_index: 0,
        timeline_start_seconds: timeline_start,
        timeline_length_seconds: length,
        media_start_seconds: 0.0,
        media_length_seconds: length,
        is_muted: false,
        source_key: source_key.map(|s| s.to_string()),
        recorded_time_seconds: recorded_time,
        playback_rate: 1.0,
        ..EventDescriptor::default()
    }
}

#[test]
fn test_preserve_timeline_order_blocks_recorded_time_reordering() {
    // Master is 180 s. Three 20 s source clips come from 20/80/140 s.
    // Their timeline order is deliberately REVERSED relative to the recording:
    //   A (true 20 s) at timeline 120
    //   B (true 80 s) at timeline 70
    //   C (true 140 s) at timeline 20
    // Recorded timestamps therefore imply A,B,C, while the timeline is C,B,A.
    let master_seconds = 180.0f32;
    let clip_seconds = 20.0f64;
    let true_positions = [20.0f64, 80.0, 140.0];
    let timeline_positions = [120.0f64, 70.0, 20.0];
    let ids = ["Ev_A", "Ev_B", "Ev_C"];

    let master_samples = build_long_recording(master_seconds);
    let temp_dir = std::env::temp_dir();
    let master_path = temp_dir.join("wavesync_preserve_master.wav");
    write_wav_i16_mono(&master_path, RATE, &master_samples);

    let mut clip_paths = Vec::new();
    for (i, &pos) in true_positions.iter().enumerate() {
        let s = (pos * RATE as f64) as usize;
        let e = ((pos + clip_seconds) * RATE as f64) as usize;
        let path = temp_dir.join(format!("wavesync_preserve_clip_{}.wav", i));
        write_wav_i16_mono(&path, RATE, &master_samples[s..e]);
        clip_paths.push(path);
    }

    let recorded_base = 1_000_000.0f64;
    let build_request = |preserve: bool| {
        let mut tracks = vec![TrackDescriptor {
            track_id: "Track_Master".into(),
            name: "Master".into(),
            is_audio: true,
            events: vec![make_event(
                "Ev_Master",
                "Track_Master",
                &master_path,
                0.0,
                master_seconds as f64,
                None,
                None,
            )],
        }];

        let mut events = Vec::new();
        for i in 0..3 {
            events.push(make_event(
                ids[i],
                "Track_Cam",
                &clip_paths[i],
                timeline_positions[i],
                clip_seconds,
                Some("cam"),
                Some(recorded_base + true_positions[i]),
            ));
        }

        tracks.push(TrackDescriptor {
            track_id: "Track_Cam".into(),
            name: "Camera".into(),
            is_audio: true,
            events,
        });

        SyncTaskRequest {
            project_sample_rate: 48000,
            master_track_id: Some("Track_Master".into()),
            tracks,
            groups: Vec::new(),
            options: SyncOptions {
                global_matching: false,
                detect_drift: false,
                max_drift_ppm: 500.0,
                discontinuity_threshold_ms: 50.0,
                sub_sample_accuracy: true,
                detect_discontinuities: false,
                speed_of_sound_compensation: false,
                voice_activity_filter: false,
                preserve_timeline_order: preserve,
                internal_sample_rate: RATE,
                ffmpeg_path: None,
                ..SyncOptions::default()
            },
        }
    };

    let run = |preserve: bool| {
        let request = build_request(preserve);
        let reporter = ProgressReporter::new(None, std::ptr::null_mut());
        let manifest = run_sync_pipeline(&request, &reporter).expect("pipeline failed");
        ids.iter()
            .map(|id| {
                manifest
                    .events
                    .iter()
                    .find(|e| e.event_id == *id)
                    .map(|e| e.resolved_start_seconds)
                    .expect("missing result")
            })
            .collect::<Vec<_>>()
    };

    let preserved = run(true);
    let unrestricted = run(false);

    let _ = std::fs::remove_file(&master_path);
    for p in &clip_paths {
        let _ = std::fs::remove_file(p);
    }

    // Preserve: one rigid +10 s group delta (median of -100/+10/+120) => 130/80/30.
    assert!(
        (preserved[0] - 130.0).abs() < 0.25,
        "A preserved to {:.3}s, expected 130.0s",
        preserved[0]
    );
    assert!(
        (preserved[1] - 80.0).abs() < 0.25,
        "B preserved to {:.3}s, expected 80.0s",
        preserved[1]
    );
    assert!(
        (preserved[2] - 30.0).abs() < 0.25,
        "C preserved to {:.3}s, expected 30.0s",
        preserved[2]
    );

    // Without preservation, recorded timestamps place A/B/C at 20/80/140,
    // reversing the timeline order.
    assert!(
        (unrestricted[0] - 20.0).abs() < 0.25,
        "A unrestricted to {:.3}s, expected 20.0s",
        unrestricted[0]
    );
    assert!(
        (unrestricted[1] - 80.0).abs() < 0.25,
        "B unrestricted to {:.3}s, expected 80.0s",
        unrestricted[1]
    );
    assert!(
        (unrestricted[2] - 140.0).abs() < 0.25,
        "C unrestricted to {:.3}s, expected 140.0s",
        unrestricted[2]
    );

    // Order check: preserved keeps the original timeline order (C,B,A),
    // unrestricted follows recorded order (A,B,C).
    let preserved_order = order_of(&preserved);
    let unrestricted_order = order_of(&unrestricted);
    assert_eq!(preserved_order, vec![2usize, 1, 0]);
    assert_eq!(unrestricted_order, vec![0usize, 1, 2]);
}

fn order_of(starts: &[f64]) -> Vec<usize> {
    let mut indices: Vec<usize> = (0..starts.len()).collect();
    indices.sort_by(|&a, &b| starts[a].partial_cmp(&starts[b]).unwrap());
    indices
}
