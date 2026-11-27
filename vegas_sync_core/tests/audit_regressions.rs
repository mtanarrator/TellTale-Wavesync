//! Audit regression tests (QA, contract v1).
//!
//! These tests exercise the *public* crate API only and encode the contract in
//! `wavesync-swarm/CONTRACT.md` — not the current implementation:
//!
//! * split points carry an additive `split_points` array of `{timestamp_seconds,
//!   step_jump_seconds}` objects while legacy `split_points_seconds` keeps working;
//! * `EventDescriptor::playback_rate` defaults to 1.0 when absent;
//! * `DiscontinuityDetector` reports the 120 ms step jump at the observation
//!   midpoint and rejects isolated outliers;
//! * the sinc resampler preserves duration and impulse alignment (no uncompensated
//!   filter delay);
//! * legacy `SyncTaskRequest` / `SyncOptions` JSON still parses with safe defaults.
//!
//! NOTE on resampling: CONTRACT rule 2 forbids renaming existing public members,
//! so `DspProcessor::resample_mono` is the stable public resampling entry point
//! covered here. If an in-flight change adds `resample_sinc` as an alias it must
//! delegate to the same delay-compensated path, so this test still applies.

use serde_json::{json, Value};
use telltale_sync_core::drift::{DiscontinuityDetector, DriftObservation};
use telltale_sync_core::{
    DspProcessor, EventDescriptor, EventSyncResult, SplitPoint, SyncManifest, SyncOptions,
    SyncTaskRequest,
};

const FLOAT_TOL: f64 = 1e-9;

fn assert_approx(actual: f64, expected: f64, tol: f64, what: &str) {
    assert!(
        (actual - expected).abs() <= tol,
        "{}: expected {} (+/- {}), got {}",
        what,
        expected,
        tol,
        actual
    );
}

/// A fully-populated event result in the *new* wire format.
fn new_style_event_result() -> EventSyncResult {
    EventSyncResult {
        event_id: "ev-cam-a-001".to_string(),
        track_id: "track-cam-a".to_string(),
        original_start_seconds: 10.0,
        resolved_start_seconds: 12.5,
        delta_seconds: 2.5,
        playback_rate: 1.0,
        drift_ppm: 0.0,
        confidence: 0.94,
        split_points_seconds: vec![75.0, 180.5],
        split_points: vec![
            SplitPoint {
                timestamp_seconds: 75.0,
                step_jump_seconds: 0.120,
            },
            SplitPoint {
                timestamp_seconds: 180.5,
                step_jump_seconds: -0.040,
            },
        ],
        is_aligned: true,
        notes: Some("test fixture".to_string()),
    }
}

/// Builds an event-result JSON object, optionally including the legacy/new split
/// point representations, so legacy and current producers can both be modelled.
fn event_result_json(with_split_points: bool, with_split_points_seconds: bool) -> Value {
    let mut obj = json!({
        "event_id": "ev-cam-a-001",
        "track_id": "track-cam-a",
        "original_start_seconds": 10.0,
        "resolved_start_seconds": 12.5,
        "delta_seconds": 2.5,
        "playback_rate": 1.0,
        "drift_ppm": 0.0,
        "confidence": 0.94,
        "is_aligned": true,
        "notes": null
    });
    let map = obj.as_object_mut().expect("json object");
    if with_split_points_seconds {
        map.insert("split_points_seconds".to_string(), json!([75.0]));
    }
    if with_split_points {
        map.insert(
            "split_points".to_string(),
            json!([{ "timestamp_seconds": 75.0, "step_jump_seconds": 0.120 }]),
        );
    }
    obj
}

fn manifest_json(event: Value) -> Value {
    json!({
        "session_id": "session-1",
        "success": true,
        "master_track_id": "track-master",
        "events": [event],
        "warnings": [],
        "processing_time_ms": 1234
    })
}

// ---------------------------------------------------------------------------
// 1. EventSyncResult / SyncManifest serde
// ---------------------------------------------------------------------------

#[test]
fn event_sync_result_parses_new_split_points_format() {
    let value = event_result_json(true, true);
    let result: EventSyncResult =
        serde_json::from_value(value).expect("new-format EventSyncResult must deserialize");

    assert_eq!(result.split_points.len(), 1, "one structured split expected");
    assert_approx(
        result.split_points[0].timestamp_seconds,
        75.0,
        FLOAT_TOL,
        "split timestamp_seconds",
    );
    assert_approx(
        result.split_points[0].step_jump_seconds,
        0.120,
        FLOAT_TOL,
        "split step_jump_seconds",
    );
    assert_eq!(
        result.split_points_seconds,
        vec![75.0],
        "legacy timestamp array must be preserved"
    );
}

#[test]
fn event_sync_result_parses_legacy_json_and_defaults_split_points_empty() {
    // Legacy producers only ever wrote timestamps.
    let value = event_result_json(false, true);
    let result: EventSyncResult =
        serde_json::from_value(value).expect("legacy EventSyncResult must deserialize");

    assert_eq!(result.split_points_seconds, vec![75.0]);
    assert!(
        result.split_points.is_empty(),
        "missing split_points must default to an empty list, got {:?}",
        result.split_points
    );
}

#[test]
fn event_sync_result_round_trip_preserves_both_split_fields() {
    let original = new_style_event_result();
    let encoded = serde_json::to_value(&original).expect("serialize EventSyncResult");

    // Both representations must survive serialization (additive JSON contract).
    let seconds = encoded
        .get("split_points_seconds")
        .expect("serialized result must contain split_points_seconds")
        .as_array()
        .expect("split_points_seconds must be an array");
    assert_eq!(seconds.len(), 2);

    let structured = encoded
        .get("split_points")
        .expect("serialized result must contain split_points")
        .as_array()
        .expect("split_points must be an array");
    assert_eq!(structured.len(), 2, "both structured splits expected");
    assert_approx(
        structured[0]["timestamp_seconds"]
            .as_f64()
            .expect("timestamp_seconds number"),
        75.0,
        FLOAT_TOL,
        "encoded timestamp_seconds",
    );
    assert_approx(
        structured[0]["step_jump_seconds"]
            .as_f64()
            .expect("step_jump_seconds number"),
        0.120,
        FLOAT_TOL,
        "encoded step_jump_seconds",
    );
    assert_approx(
        structured[1]["step_jump_seconds"]
            .as_f64()
            .expect("step_jump_seconds number"),
        -0.040,
        FLOAT_TOL,
        "encoded negative step_jump_seconds",
    );

    let decoded: EventSyncResult =
        serde_json::from_value(encoded).expect("round-tripped EventSyncResult");
    assert_eq!(decoded.split_points_seconds.len(), 2);
    assert_eq!(decoded.split_points.len(), 2);
    assert_approx(
        decoded.split_points[0].timestamp_seconds,
        75.0,
        FLOAT_TOL,
        "round-tripped timestamp",
    );
    assert_approx(
        decoded.split_points[0].step_jump_seconds,
        0.120,
        FLOAT_TOL,
        "round-tripped step jump",
    );
    assert_approx(
        decoded.split_points[1].step_jump_seconds,
        -0.040,
        FLOAT_TOL,
        "round-tripped negative step jump",
    );
}

#[test]
fn sync_manifest_parses_and_round_trips_split_points() {
    let legacy: SyncManifest = serde_json::from_value(manifest_json(event_result_json(false, true)))
        .expect("legacy SyncManifest must deserialize");
    assert_eq!(legacy.events.len(), 1);
    assert_eq!(legacy.events[0].split_points_seconds, vec![75.0]);
    assert!(
        legacy.events[0].split_points.is_empty(),
        "legacy manifest events must default split_points to empty"
    );

    let current: SyncManifest = serde_json::from_value(manifest_json(event_result_json(true, true)))
        .expect("new-format SyncManifest must deserialize");
    assert_eq!(current.events[0].split_points.len(), 1);
    assert_approx(
        current.events[0].split_points[0].step_jump_seconds,
        0.120,
        FLOAT_TOL,
        "manifest step jump",
    );

    // Manifest round-trip must keep both fields on the nested event.
    let encoded = serde_json::to_value(&current).expect("serialize SyncManifest");
    let decoded: SyncManifest = serde_json::from_value(encoded).expect("round-trip SyncManifest");
    assert_eq!(decoded.events[0].split_points_seconds, vec![75.0]);
    assert_eq!(decoded.events[0].split_points.len(), 1);
    assert_approx(
        decoded.events[0].split_points[0].timestamp_seconds,
        75.0,
        FLOAT_TOL,
        "round-tripped manifest split timestamp",
    );
}

// ---------------------------------------------------------------------------
// 2. EventDescriptor::playback_rate
// ---------------------------------------------------------------------------

#[test]
fn event_descriptor_missing_playback_rate_defaults_to_one() {
    let legacy = json!({
        "event_id": "ev-legacy",
        "track_id": "track-1",
        "file_path": "C:/media/legacy-take.wav",
        "timeline_start_seconds": 1.0,
        "timeline_length_seconds": 2.0
    });

    let event: EventDescriptor =
        serde_json::from_value(legacy).expect("legacy EventDescriptor must deserialize");
    assert_eq!(
        event.playback_rate, 1.0,
        "missing playback_rate must default to 1.0"
    );
}

#[test]
fn event_descriptor_explicit_playback_rate_round_trips() {
    let value = json!({
        "event_id": "ev-stretched",
        "track_id": "track-1",
        "file_path": "C:/media/stretched-take.mov",
        "timeline_start_seconds": 0.0,
        "timeline_length_seconds": 10.0,
        "playback_rate": 0.9655
    });

    let event: EventDescriptor =
        serde_json::from_value(value).expect("EventDescriptor with playback_rate");
    assert_approx(event.playback_rate, 0.9655, FLOAT_TOL, "parsed playback_rate");

    let encoded = serde_json::to_value(&event).expect("serialize EventDescriptor");
    assert_approx(
        encoded
            .get("playback_rate")
            .and_then(Value::as_f64)
            .expect("serialized playback_rate must be a number"),
        0.9655,
        FLOAT_TOL,
        "encoded playback_rate",
    );

    let decoded: EventDescriptor =
        serde_json::from_value(encoded).expect("round-trip EventDescriptor");
    assert_approx(
        decoded.playback_rate,
        0.9655,
        FLOAT_TOL,
        "round-tripped playback_rate",
    );
}

// ---------------------------------------------------------------------------
// 3. DiscontinuityDetector (integration view of the public drift module)
// ---------------------------------------------------------------------------

#[test]
fn detector_reports_120ms_step_jump_at_observation_midpoint() {
    let observations = vec![
        DriftObservation {
            time_seconds: 0.0,
            delay_seconds: 1.000,
            confidence: 0.9,
        },
        DriftObservation {
            time_seconds: 30.0,
            delay_seconds: 1.001,
            confidence: 0.9,
        },
        DriftObservation {
            time_seconds: 60.0,
            delay_seconds: 1.002,
            confidence: 0.9,
        },
        // 120 ms of dropped frames lands between t=60 and t=90.
        DriftObservation {
            time_seconds: 90.0,
            delay_seconds: 1.122,
            confidence: 0.9,
        },
        DriftObservation {
            time_seconds: 120.0,
            delay_seconds: 1.123,
            confidence: 0.9,
        },
    ];

    let jumps = DiscontinuityDetector::detect_step_jumps(&observations, 0.030);
    assert_eq!(jumps.len(), 1, "expected exactly one discontinuity: {:?}", jumps);
    assert_approx(
        jumps[0].timestamp_seconds,
        75.0,
        1e-4,
        "split timestamp must be the midpoint of the jump interval",
    );
    assert_approx(
        jumps[0].step_jump_seconds,
        0.120,
        0.005,
        "step jump must equal delay_after - delay_before",
    );
}

#[test]
fn detector_ignores_isolated_outlier() {
    let observations = vec![
        DriftObservation {
            time_seconds: 0.0,
            delay_seconds: 1.000,
            confidence: 0.9,
        },
        DriftObservation {
            time_seconds: 15.0,
            delay_seconds: 1.001,
            confidence: 0.9,
        },
        // Single noisy window that immediately snaps back at t=45s.
        DriftObservation {
            time_seconds: 30.0,
            delay_seconds: 2.500,
            confidence: 0.35,
        },
        DriftObservation {
            time_seconds: 45.0,
            delay_seconds: 1.002,
            confidence: 0.9,
        },
        DriftObservation {
            time_seconds: 60.0,
            delay_seconds: 1.003,
            confidence: 0.9,
        },
    ];

    let jumps = DiscontinuityDetector::detect_step_jumps(&observations, 0.030);
    assert!(
        jumps.is_empty(),
        "isolated outlier must not produce a split: {:?}",
        jumps
    );
}

// ---------------------------------------------------------------------------
// 4. DspProcessor resampling integrity
// ---------------------------------------------------------------------------

#[test]
fn resample_sinc_48k_to_16k_length_is_one_third() {
    let source_rate = 48_000u32;
    let target_rate = 16_000u32;
    let input_len = 48_000usize; // exactly one second
    let input: Vec<f32> = (0..input_len)
        .map(|i| (2.0 * std::f32::consts::PI * 440.0 * i as f32 / source_rate as f32).sin())
        .collect();

    let output = DspProcessor::resample_mono(&input, source_rate, target_rate)
        .expect("48 kHz -> 16 kHz resampling must succeed");

    let expected = input_len as f64 / 3.0;
    // Contract tolerance: ~64 input-equivalent samples. At 48 kHz -> 16 kHz that
    // is ~21.3 output samples, and it is tight enough to catch an uncompensated
    // resampler delay (which is hundreds of samples).
    let tolerance = 64.0 * target_rate as f64 / source_rate as f64;
    assert!(
        (output.len() as f64 - expected).abs() <= tolerance,
        "resampled length {} is not within {} of input_len/3 = {} (untrimmed filter delay or missing tail flush?)",
        output.len(),
        tolerance,
        expected
    );
}

#[test]
fn resample_sinc_impulse_keeps_alignment() {
    let source_rate = 48_000u32;
    let target_rate = 16_000u32;
    let input_len = 48_000usize;
    let impulse_index = input_len / 4; // 25 % into the buffer

    let mut input = vec![0.0f32; input_len];
    input[impulse_index] = 1.0;

    let output = DspProcessor::resample_mono(&input, source_rate, target_rate)
        .expect("impulse resampling must succeed");
    assert!(!output.is_empty(), "resampling an impulse must produce output");

    let (peak_index, peak_value) = output
        .iter()
        .enumerate()
        .max_by(|(_, a), (_, b)| {
            a.abs()
                .partial_cmp(&b.abs())
                .unwrap_or(std::cmp::Ordering::Equal)
        })
        .map(|(i, v)| (i, *v))
        .expect("non-empty output");
    assert!(
        peak_value.abs() > 1e-3,
        "impulse energy disappeared during resampling (peak {})",
        peak_value
    );

    let expected_index = impulse_index as f64 * target_rate as f64 / source_rate as f64;
    // 1 ms at 16 kHz = 16 output samples; an uncompensated sinc delay is far larger.
    let tolerance = (target_rate as f64 / 1000.0) + 1e-6;
    assert!(
        (peak_index as f64 - expected_index).abs() <= tolerance,
        "impulse at input #{} expected near output #{} (+/- {} samples), peak found at #{} (leading filter delay not trimmed?)",
        impulse_index,
        expected_index,
        tolerance,
        peak_index
    );
}

#[test]
fn resample_sinc_empty_input_returns_empty() {
    let output = DspProcessor::resample_mono(&[], 48_000, 16_000)
        .expect("resampling an empty buffer must succeed");
    assert!(
        output.is_empty(),
        "empty input must produce empty output, got {} samples",
        output.len()
    );
}

// ---------------------------------------------------------------------------
// 5. SyncTaskRequest / SyncOptions legacy JSON compatibility
// ---------------------------------------------------------------------------

#[test]
fn legacy_sync_request_parses_with_defaults() {
    // A pre-fix C# producer payload: no options, no groups, no playback_rate.
    let legacy = json!({
        "master_track_id": "track-master",
        "tracks": [{
            "track_id": "track-cam-a",
            "name": "Cam A",
            "is_audio": true,
            "events": [{
                "event_id": "ev-cam-a-001",
                "track_id": "track-cam-a",
                "file_path": "C:/media/cam-a.wav",
                "timeline_start_seconds": 0.0,
                "timeline_length_seconds": 30.0
            }]
        }]
    });

    let request: SyncTaskRequest =
        serde_json::from_value(legacy).expect("legacy SyncTaskRequest must deserialize");

    assert_eq!(request.project_sample_rate, 48_000);
    assert!(request.groups.is_empty(), "groups must default to empty");
    assert!(request.options.detect_drift, "detect_drift must default on");
    assert!(
        request.options.detect_discontinuities,
        "detect_discontinuities must default on"
    );
    assert!(
        !request.options.voice_activity_filter,
        "voice_activity_filter must default to false"
    );
    assert!(
        !request.options.preserve_timeline_order,
        "preserve_timeline_order must default to false"
    );
    assert_eq!(
        request.tracks[0].events[0].playback_rate, 1.0,
        "legacy events without playback_rate must default to 1.0"
    );
}

#[test]
fn sync_options_voice_activity_filter_defaults_false() {
    let from_default = SyncOptions::default();
    assert!(
        !from_default.voice_activity_filter,
        "SyncOptions::default() must leave VAD off"
    );

    let from_empty_json: SyncOptions =
        serde_json::from_str("{}").expect("empty SyncOptions JSON must deserialize");
    assert!(
        !from_empty_json.voice_activity_filter,
        "missing voice_activity_filter must deserialize to false"
    );
}
