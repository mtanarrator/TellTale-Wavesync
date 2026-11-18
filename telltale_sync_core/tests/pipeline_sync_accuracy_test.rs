use std::fs::File;
use std::io::Write;
use vegas_sync_core::engine::run_sync_pipeline;
use vegas_sync_core::ffi::ProgressReporter;
use vegas_sync_core::models::*;

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

    for &s in samples {
        let val = (s.max(-1.0).min(1.0) * 32000.0) as i16;
        file.write_all(&val.to_le_bytes()).unwrap();
    }
}

#[test]
fn test_end_to_end_pipeline_sync_accuracy() {
    let sample_rate = 16000;
    let duration_sec = 6.0;
    let total_samples = (sample_rate as f32 * duration_sec) as usize;

    // Generate reference sound with distinct transient acoustic events
    let mut sound = vec![0.0f32; total_samples];
    for i in 0..total_samples {
        let t = i as f32 / sample_rate as f32;
        let is_burst = (t > 1.2 && t < 1.5) || (t > 2.8 && t < 3.1) || (t > 4.2 && t < 4.6);
        if is_burst {
            sound[i] = (2.0 * std::f32::consts::PI * (300.0 + 800.0 * t) * t).sin();
        }
    }

    // Master has sound starting at sample 0 (t_m = t)
    let master_samples = sound.clone();

    // Slave has sound delayed by 1.500 seconds (24,000 samples)
    // t_s = t + 1.5s
    // Since slave heard sound 1.5s later in its file, Slave started 1.5s BEFORE Master!
    let slave_delay_sec = 1.5;
    let delay_samples = (slave_delay_sec * sample_rate as f64) as usize;
    let mut slave_samples = vec![0.0f32; total_samples];
    for i in delay_samples..total_samples {
        slave_samples[i] = sound[i - delay_samples];
    }

    let temp_dir = std::env::temp_dir();
    let master_path = temp_dir.join("test_pipeline_master.wav");
    let slave_path = temp_dir.join("test_pipeline_slave.wav");

    write_wav_file(master_path.to_str().unwrap(), sample_rate, &master_samples);
    write_wav_file(slave_path.to_str().unwrap(), sample_rate, &slave_samples);

    // Timeline setup:
    // Master is placed at timeline start 10.0s (MediaStartSeconds = 0.0)
    // So the acoustic event at t=1.2s in sound is at timeline 11.2s.
    // Slave is currently placed at timeline start 20.0s (unsynced).
    // In Slave file, that event is at 1.2 + 1.5 = 2.7s into slave file.
    // To match timeline 11.2s, Slave file sample 0 must be placed at 11.2 - 2.7 = 8.5s!
    // That is: MasterStart (10.0s) - slave_delay_sec (1.5s) = 8.5s!
    let master_start_tc = 10.0;
    let slave_initial_tc = 20.0;
    let expected_slave_resolved_tc = master_start_tc - slave_delay_sec; // 8.5s

    let request = SyncTaskRequest {
        project_sample_rate: sample_rate,
        master_track_id: Some("Track_Master".into()),
        tracks: vec![
            TrackDescriptor {
                track_id: "Track_Master".into(),
                name: "Master Boom".into(),
                is_audio: true,
                events: vec![EventDescriptor {
                    event_id: "Ev_Master_0".into(),
                    track_id: "Track_Master".into(),
                    file_path: master_path.to_str().unwrap().into(),
                    stream_index: 0,
                    timeline_start_seconds: master_start_tc,
                    timeline_length_seconds: duration_sec as f64,
                    media_start_seconds: 0.0,
                    media_length_seconds: duration_sec as f64,
                    is_muted: false,
                    source_key: None,
                    recorded_time_seconds: None,
                }],
            },
            TrackDescriptor {
                track_id: "Track_Slave".into(),
                name: "Cam Scratch Mic".into(),
                is_audio: true,
                events: vec![EventDescriptor {
                    event_id: "Ev_Slave_0".into(),
                    track_id: "Track_Slave".into(),
                    file_path: slave_path.to_str().unwrap().into(),
                    stream_index: 0,
                    timeline_start_seconds: slave_initial_tc,
                    timeline_length_seconds: duration_sec as f64,
                    media_start_seconds: 0.0,
                    media_length_seconds: duration_sec as f64,
                    is_muted: false,
                    source_key: None,
                    recorded_time_seconds: None,
                }],
            },
        ],
        options: SyncOptions {
            internal_sample_rate: sample_rate,
            voice_activity_filter: false,
            detect_drift: false,
            max_drift_ppm: 200.0,
            sub_sample_accuracy: true,
            detect_discontinuities: true,
            speed_of_sound_compensation: false,
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
    assert!(manifest.success);

    let slave_res = manifest.events.iter().find(|e| e.event_id == "Ev_Slave_0").unwrap();
    assert!(slave_res.is_aligned, "Slave should be marked aligned");
    assert!(slave_res.confidence > 0.8, "Confidence should be high, was {}", slave_res.confidence);

    println!(
        "Master start: {}, Slave original: {}, Slave resolved: {}, Expected: {}",
        master_start_tc, slave_initial_tc, slave_res.resolved_start_seconds, expected_slave_resolved_tc
    );

    assert!(
        (slave_res.resolved_start_seconds - expected_slave_resolved_tc).abs() < 0.005,
        "Resolved start {} was not close to expected {}",
        slave_res.resolved_start_seconds,
        expected_slave_resolved_tc
    );
}

#[test]
fn test_end_to_end_pipeline_sync_accuracy_slave_after_master() {
    let sample_rate = 16000;
    let duration_sec = 6.0;
    let total_samples = (sample_rate as f32 * duration_sec) as usize;

    let mut sound = vec![0.0f32; total_samples];
    for i in 0..total_samples {
        let t = i as f32 / sample_rate as f32;
        let is_burst = (t > 1.2 && t < 1.5) || (t > 2.8 && t < 3.1) || (t > 4.2 && t < 4.6);
        if is_burst {
            sound[i] = (2.0 * std::f32::consts::PI * (300.0 + 800.0 * t) * t).sin();
        }
    }

    // Master has sound delayed by 1.500 seconds (24,000 samples) (t_m = t + 1.5s)
    let shift_sec = 1.5;
    let shift_samples = (shift_sec * sample_rate as f64) as usize;
    let mut master_samples = vec![0.0f32; total_samples];
    for i in shift_samples..total_samples {
        master_samples[i] = sound[i - shift_samples];
    }

    // Slave has sound starting at sample 0 (t_s = t)
    // Master heard sound 1.5s later in its file, so Slave started 1.5s AFTER Master!
    let slave_samples = sound;

    let temp_dir = std::env::temp_dir();
    let master_path = temp_dir.join("test_pipeline_master_after.wav");
    let slave_path = temp_dir.join("test_pipeline_slave_after.wav");

    write_wav_file(master_path.to_str().unwrap(), sample_rate, &master_samples);
    write_wav_file(slave_path.to_str().unwrap(), sample_rate, &slave_samples);

    let master_start_tc = 10.0;
    let slave_initial_tc = 5.0;
    let expected_slave_resolved_tc = master_start_tc + shift_sec; // 11.5s

    let request = SyncTaskRequest {
        project_sample_rate: sample_rate,
        master_track_id: Some("Track_Master".into()),
        tracks: vec![
            TrackDescriptor {
                track_id: "Track_Master".into(),
                name: "Master Boom".into(),
                is_audio: true,
                events: vec![EventDescriptor {
                    event_id: "Ev_Master_0".into(),
                    track_id: "Track_Master".into(),
                    file_path: master_path.to_str().unwrap().into(),
                    stream_index: 0,
                    timeline_start_seconds: master_start_tc,
                    timeline_length_seconds: duration_sec as f64,
                    media_start_seconds: 0.0,
                    media_length_seconds: duration_sec as f64,
                    is_muted: false,
                    source_key: None,
                    recorded_time_seconds: None,
                }],
            },
            TrackDescriptor {
                track_id: "Track_Slave".into(),
                name: "Cam Scratch Mic".into(),
                is_audio: true,
                events: vec![EventDescriptor {
                    event_id: "Ev_Slave_0".into(),
                    track_id: "Track_Slave".into(),
                    file_path: slave_path.to_str().unwrap().into(),
                    stream_index: 0,
                    timeline_start_seconds: slave_initial_tc,
                    timeline_length_seconds: duration_sec as f64,
                    media_start_seconds: 0.0,
                    media_length_seconds: duration_sec as f64,
                    is_muted: false,
                    source_key: None,
                    recorded_time_seconds: None,
                }],
            },
        ],
        options: SyncOptions {
            internal_sample_rate: sample_rate,
            voice_activity_filter: false,
            detect_drift: false,
            max_drift_ppm: 200.0,
            sub_sample_accuracy: true,
            detect_discontinuities: true,
            speed_of_sound_compensation: false,
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
    assert!(manifest.success);

    let slave_res = manifest.events.iter().find(|e| e.event_id == "Ev_Slave_0").unwrap();
    assert!(slave_res.is_aligned, "Slave should be marked aligned");
    assert!(slave_res.confidence > 0.8, "Confidence should be high, was {}", slave_res.confidence);

    println!(
        "Master start: {}, Slave original: {}, Slave resolved: {}, Expected: {}",
        master_start_tc, slave_initial_tc, slave_res.resolved_start_seconds, expected_slave_resolved_tc
    );

    assert!(
        (slave_res.resolved_start_seconds - expected_slave_resolved_tc).abs() < 0.005,
        "Resolved start {} was not close to expected {}",
        slave_res.resolved_start_seconds,
        expected_slave_resolved_tc
    );
}
