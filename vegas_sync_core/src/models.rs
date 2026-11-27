use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SyncOptions {
    #[serde(default = "default_true")]
    pub detect_drift: bool,
    #[serde(default = "default_max_drift_ppm")]
    pub max_drift_ppm: f64,
    #[serde(default = "default_discontinuity_threshold_ms")]
    pub discontinuity_threshold_ms: f64,
    #[serde(default = "default_true")]
    pub sub_sample_accuracy: bool,
    #[serde(default = "default_true")]
    pub detect_discontinuities: bool,
    #[serde(default)]
    pub speed_of_sound_compensation: bool,
    #[serde(default)]
    pub voice_activity_filter: bool,
    /// When true, recorded timestamps are never used to place or reorder events.
    /// Each source group is shifted by one rigid delta and track layout is left alone.
    #[serde(default)]
    pub preserve_timeline_order: bool,
    #[serde(default = "default_target_sample_rate")]
    pub internal_sample_rate: u32,
    pub ffmpeg_path: Option<String>,
    /// Which timestamp source drives ordering/placement:
    /// `auto | recorded | timecode | filename | file_time | ignore`.
    #[serde(default = "default_time_source")]
    pub time_source: String,
    /// Ordering policy for clips: `auto | strict | timecode | preserve | none`.
    #[serde(default = "default_clip_order")]
    pub clip_order: String,
    /// Enables the global constraint solver. On by default; the engine falls back
    /// automatically for sparse or very large projects.
    #[serde(default = "default_true")]
    pub global_matching: bool,
    /// Clips shorter than this are treated as b-roll.
    #[serde(default = "default_broll_max_seconds")]
    pub broll_max_seconds: f64,
    /// Timeline frame rate used by timecode helpers.
    #[serde(default = "default_project_frame_rate")]
    pub project_frame_rate: f64,
}

fn default_true() -> bool {
    true
}

fn default_max_drift_ppm() -> f64 {
    500.0
}

fn default_discontinuity_threshold_ms() -> f64 {
    30.0
}

fn default_target_sample_rate() -> u32 {
    16_000
}

fn default_time_source() -> String {
    "auto".to_string()
}

fn default_clip_order() -> String {
    "auto".to_string()
}

fn default_broll_max_seconds() -> f64 {
    15.0
}

fn default_project_frame_rate() -> f64 {
    25.0
}

impl Default for SyncOptions {
    fn default() -> Self {
        Self {
            detect_drift: true,
            max_drift_ppm: 500.0,
            discontinuity_threshold_ms: 30.0,
            sub_sample_accuracy: true,
            detect_discontinuities: true,
            speed_of_sound_compensation: false,
            voice_activity_filter: false,
            preserve_timeline_order: false,
            internal_sample_rate: 16_000,
            ffmpeg_path: None,
            time_source: default_time_source(),
            clip_order: default_clip_order(),
            global_matching: true,
            broll_max_seconds: default_broll_max_seconds(),
            project_frame_rate: default_project_frame_rate(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EventDescriptor {
    pub event_id: String,
    pub track_id: String,
    pub file_path: String,
    #[serde(default)]
    pub stream_index: u32,
    pub timeline_start_seconds: f64,
    pub timeline_length_seconds: f64,
    #[serde(default)]
    pub media_start_seconds: f64,
    #[serde(default)]
    pub media_length_seconds: f64,
    #[serde(default)]
    pub is_muted: bool,
    /// Folder/collection the media belongs to. Clips that came from the same
    /// camera/source share a key and are synchronized as one rigid group.
    #[serde(default)]
    pub source_key: Option<String>,
    /// Wall-clock recording time reported by the media container (seconds since
    /// the Unix epoch), when available. Used for diagnostics and ordering.
    #[serde(default)]
    pub recorded_time_seconds: Option<f64>,
    /// Seconds-of-day reported by the container timecode track (frames dropped,
    /// nominal project frame rate). Ordering only: never an epoch timestamp.
    #[serde(default)]
    pub timecode_seconds: Option<f64>,
    /// Epoch seconds parsed from a filename timestamp (local time).
    #[serde(default)]
    pub filename_time_seconds: Option<f64>,
    /// Epoch seconds from the media file's last-write time (`LastWriteTimeUtc`).
    #[serde(default)]
    pub file_time_seconds: Option<f64>,
    /// Epoch seconds from the file system creation time (`CreationTimeUtc`).
    /// Recorder WAVs often stamp their real start here when container metadata
    /// and filename timestamps are missing or wrong.
    #[serde(default)]
    pub file_created_time_seconds: Option<f64>,
    /// Semantic role of the clip: `normal | broll`.
    #[serde(default = "default_sync_role")]
    pub sync_role: String,
    /// Source-media seconds consumed per timeline second (VEGAS event playback
    /// rate). 1.0 for unstretched media. Non-finite/<=0 values are treated as 1.0
    /// at the conversion sites.
    #[serde(default = "default_playback_rate")]
    pub playback_rate: f64,
}

fn default_sync_role() -> String {
    "normal".to_string()
}

impl Default for EventDescriptor {
    fn default() -> Self {
        Self {
            event_id: String::new(),
            track_id: String::new(),
            file_path: String::new(),
            stream_index: 0,
            timeline_start_seconds: 0.0,
            timeline_length_seconds: 0.0,
            media_start_seconds: 0.0,
            media_length_seconds: 0.0,
            is_muted: false,
            source_key: None,
            recorded_time_seconds: None,
            timecode_seconds: None,
            filename_time_seconds: None,
            file_time_seconds: None,
            file_created_time_seconds: None,
            sync_role: default_sync_role(),
            playback_rate: default_playback_rate(),
        }
    }
}

fn default_playback_rate() -> f64 {
    1.0
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TrackDescriptor {
    pub track_id: String,
    pub name: String,
    #[serde(default = "default_true")]
    pub is_audio: bool,
    #[serde(default)]
    pub events: Vec<EventDescriptor>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EventGroupMember {
    pub event_id: String,
    pub track_id: String,
    pub timeline_start_seconds: f64,
    pub timeline_length_seconds: f64,
    #[serde(default)]
    pub media_start_seconds: f64,
    #[serde(default)]
    pub stream_index: u32,
    #[serde(default)]
    pub is_video: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EventGroupDescriptor {
    pub group_id: String,
    #[serde(default)]
    pub kind: String,
    pub primary_event_id: String,
    #[serde(default)]
    pub members: Vec<EventGroupMember>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SyncTaskRequest {
    #[serde(default = "default_project_sample_rate")]
    pub project_sample_rate: u32,
    pub master_track_id: Option<String>,
    pub tracks: Vec<TrackDescriptor>,
    #[serde(default)]
    pub groups: Vec<EventGroupDescriptor>,
    #[serde(default)]
    pub options: SyncOptions,
}

fn default_project_sample_rate() -> u32 {
    48_000
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SplitPoint {
    /// Event-relative timeline timestamp of the split.
    pub timestamp_seconds: f64,
    /// Step jump across the split (`delay_after - delay_before`). The right-hand
    /// (post-split) segment shifts by minus this value in timeline seconds.
    pub step_jump_seconds: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EventSyncResult {
    pub event_id: String,
    pub track_id: String,
    pub original_start_seconds: f64,
    pub resolved_start_seconds: f64,
    pub delta_seconds: f64,
    pub playback_rate: f64,
    pub drift_ppm: f64,
    pub confidence: f64,
    #[serde(default)]
    pub split_points_seconds: Vec<f64>,
    /// Structured split points carrying the step-jump shift. Additive; legacy
    /// consumers keep using `split_points_seconds` (timestamps only).
    #[serde(default)]
    pub split_points: Vec<SplitPoint>,
    pub is_aligned: bool,
    pub notes: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SyncManifest {
    pub session_id: String,
    pub success: bool,
    pub master_track_id: String,
    pub events: Vec<EventSyncResult>,
    pub warnings: Vec<String>,
    pub processing_time_ms: u64,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn event_descriptor_defaults_playback_rate_to_one() {
        let event: EventDescriptor = serde_json::from_value(serde_json::json!({
            "event_id": "ev-legacy",
            "track_id": "track-1",
            "file_path": "C:/media/take.wav",
            "timeline_start_seconds": 1.0,
            "timeline_length_seconds": 2.0
        }))
        .expect("legacy event must deserialize");
        assert_eq!(event.playback_rate, 1.0);
    }

    #[test]
    fn legacy_manifest_without_split_points_parses() {
        let manifest: SyncManifest = serde_json::from_value(serde_json::json!({
            "session_id": "session-1",
            "success": true,
            "master_track_id": "track-master",
            "events": [{
                "event_id": "ev-1",
                "track_id": "track-1",
                "original_start_seconds": 10.0,
                "resolved_start_seconds": 12.5,
                "delta_seconds": 2.5,
                "playback_rate": 1.0,
                "drift_ppm": 0.0,
                "confidence": 0.9,
                "split_points_seconds": [75.0],
                "is_aligned": true,
                "notes": null
            }],
            "warnings": [],
            "processing_time_ms": 1234
        }))
        .expect("legacy manifest must deserialize");
        assert!(manifest.events[0].split_points.is_empty());
        assert_eq!(manifest.events[0].split_points_seconds, vec![75.0]);
    }

    #[test]
    fn legacy_options_get_new_defaults() {
        let options: SyncOptions = serde_json::from_value(serde_json::json!({
            "ffmpeg_path": null
        }))
        .expect("legacy options must deserialize");
        assert_eq!(options.time_source, "auto");
        assert_eq!(options.clip_order, "auto");
        assert!(options.global_matching);
        assert_eq!(options.broll_max_seconds, 15.0);
        assert_eq!(options.project_frame_rate, 25.0);
    }

    #[test]
    fn event_descriptor_new_time_fields_default_to_none() {
        let event: EventDescriptor = serde_json::from_value(serde_json::json!({
            "event_id": "ev-legacy",
            "track_id": "track-1",
            "file_path": "C:/media/take.wav",
            "timeline_start_seconds": 1.0,
            "timeline_length_seconds": 2.0
        }))
        .expect("legacy event must deserialize");
        assert!(event.timecode_seconds.is_none());
        assert!(event.filename_time_seconds.is_none());
        assert!(event.file_time_seconds.is_none());
        assert_eq!(event.sync_role, "normal");
    }

    #[test]
    fn new_time_fields_round_trip_as_additive_json() {
        let event: EventDescriptor = serde_json::from_value(serde_json::json!({
            "event_id": "ev-1",
            "track_id": "track-1",
            "file_path": "C:/media/take.mp4",
            "timeline_start_seconds": 0.0,
            "timeline_length_seconds": 5.0,
            "timecode_seconds": 3600.5,
            "filename_time_seconds": 1_800_000_000.0,
            "file_time_seconds": 1_800_000_100.0,
            "sync_role": "broll"
        }))
        .expect("event with time fields must deserialize");
        assert_eq!(event.timecode_seconds, Some(3600.5));
        assert_eq!(event.filename_time_seconds, Some(1_800_000_000.0));
        assert_eq!(event.file_time_seconds, Some(1_800_000_100.0));
        assert_eq!(event.sync_role, "broll");

        let json = serde_json::to_value(&event).expect("event must serialize");
        assert_eq!(json["timecode_seconds"], 3600.5);
        assert_eq!(json["sync_role"], "broll");
    }
}
