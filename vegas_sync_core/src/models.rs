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
