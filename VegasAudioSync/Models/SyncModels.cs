using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace VegasAudioSync.Models
{
    [DataContract]
    public class SyncOptions
    {
        [DataMember(Name = "detect_drift")]
        public bool DetectDrift { get; set; } = true;

        [DataMember(Name = "max_drift_ppm")]
        public double MaxDriftPpm { get; set; } = 500.0;

        [DataMember(Name = "discontinuity_threshold_ms")]
        public double DiscontinuityThresholdMs { get; set; } = 30.0;

        [DataMember(Name = "sub_sample_accuracy")]
        public bool SubSampleAccuracy { get; set; } = true;

        [DataMember(Name = "detect_discontinuities")]
        public bool DetectDiscontinuities { get; set; } = true;

        [DataMember(Name = "speed_of_sound_compensation")]
        public bool SpeedOfSoundCompensation { get; set; } = false;

        [DataMember(Name = "voice_activity_filter")]
        public bool VoiceActivityFilter { get; set; } = false;

        /// <summary>
        /// When true, recorded timestamps are never used to place or reorder events.
        /// Each source group is shifted by one rigid delta and track layout is left alone.
        /// </summary>
        [DataMember(Name = "preserve_timeline_order")]
        public bool PreserveTimelineOrder { get; set; } = false;

        [DataMember(Name = "internal_sample_rate")]
        public uint InternalSampleRate { get; set; } = 16000;

        [DataMember(Name = "ffmpeg_path")]
        public string FfmpegPath { get; set; }

        /// <summary>
        /// Preferred clock for ordering/placement: auto, recorded, timecode,
        /// filename, file_time or ignore. Additive option; legacy engines ignore it.
        /// </summary>
        [DataMember(Name = "time_source")]
        public string TimeSource { get; set; } = "auto";

        /// <summary>
        /// Clip ordering strategy: auto, strict, timecode, preserve or none.
        /// </summary>
        [DataMember(Name = "clip_order")]
        public string ClipOrder { get; set; } = "auto";

        /// <summary>
        /// Reserved for the global solver. Global matching is on by default; the
        /// engine falls back automatically for sparse or very large projects.
        /// </summary>
        [DataMember(Name = "global_matching")]
        public bool GlobalMatching { get; set; } = true;

        /// <summary>Clips shorter than this are tagged as b-roll.</summary>
        [DataMember(Name = "broll_max_seconds")]
        public double BrollMaxSeconds { get; set; } = 15.0;

        /// <summary>Nominal project frame rate used by timecode parsing.</summary>
        [DataMember(Name = "project_frame_rate")]
        public double ProjectFrameRate { get; set; } = 25.0;
    }

    [DataContract]
    public class EventDescriptor
    {
        [DataMember(Name = "event_id")]
        public string EventId { get; set; }

        /// <summary>
        /// VEGAS' persistent EventID for this event. Used at apply time to find the
        /// same event again, so a timeline edit during analysis cannot cause results
        /// to be applied to a different clip that now occupies the old index.
        /// </summary>
        [DataMember(Name = "vegas_event_id")]
        public long VegasEventId { get; set; }

        [DataMember(Name = "track_id")]
        public string TrackId { get; set; }

        [DataMember(Name = "file_path")]
        public string FilePath { get; set; }

        [DataMember(Name = "stream_index")]
        public uint StreamIndex { get; set; }

        [DataMember(Name = "timeline_start_seconds")]
        public double TimelineStartSeconds { get; set; }

        [DataMember(Name = "timeline_length_seconds")]
        public double TimelineLengthSeconds { get; set; }

        [DataMember(Name = "media_start_seconds")]
        public double MediaStartSeconds { get; set; }

        [DataMember(Name = "media_length_seconds")]
        public double MediaLengthSeconds { get; set; }

        /// <summary>
        /// Timeline playback rate of the event as it was when analysis started.
        /// The applier uses it to preserve the media span when it changes the rate.
        /// </summary>
        [DataMember(Name = "playback_rate")]
        public double PlaybackRate { get; set; } = 1.0;

        [DataMember(Name = "is_muted")]
        public bool IsMuted { get; set; }

        /// <summary>
        /// Folder/media collection the file belongs to. Clips from the same camera or
        /// recorder share a key and are synchronized as one rigid group.
        /// </summary>
        [DataMember(Name = "source_key")]
        public string SourceKey { get; set; }

        /// <summary>
        /// Wall-clock recording time (seconds since the Unix epoch) when the container
        /// exposes it. Used for diagnostics and recorded-order checks.
        /// </summary>
        [DataMember(Name = "recorded_time_seconds")]
        public double? RecordedTimeSeconds { get; set; }

        /// <summary>
        /// Container timecode as seconds of day (frames ignored). Null when the media
        /// does not carry timecode or ffprobe is unavailable.
        /// </summary>
        [DataMember(Name = "timecode_seconds", EmitDefaultValue = false)]
        public double? TimecodeSeconds
        {
            get { return _timecodeSeconds; }
            set { _timecodeSeconds = SanitizeSeconds(value); }
        }
        private double? _timecodeSeconds;

        /// <summary>
        /// Epoch seconds parsed from a timestamp embedded in the filename
        /// (e.g. 260815_081034). Interpreted as local time.
        /// </summary>
        [DataMember(Name = "filename_time_seconds", EmitDefaultValue = false)]
        public double? FilenameTimeSeconds
        {
            get { return _filenameTimeSeconds; }
            set { _filenameTimeSeconds = SanitizeSeconds(value); }
        }
        private double? _filenameTimeSeconds;

        /// <summary>Epoch seconds of FileInfo.LastWriteTimeUtc.</summary>
        [DataMember(Name = "file_time_seconds", EmitDefaultValue = false)]
        public double? FileTimeSeconds
        {
            get { return _fileTimeSeconds; }
            set { _fileTimeSeconds = SanitizeSeconds(value); }
        }
        private double? _fileTimeSeconds;

        /// <summary>normal for regular clips, broll for clips shorter than the b-roll threshold.</summary>
        [DataMember(Name = "sync_role")]
        public string SyncRole { get; set; } = "normal";

        private static double? SanitizeSeconds(double? value)
        {
            if (!value.HasValue) return null;
            double seconds = value.Value;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return null;
            return seconds;
        }
    }

    [DataContract]
    public class TrackDescriptor
    {
        [DataMember(Name = "track_id")]
        public string TrackId { get; set; }

        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "is_audio")]
        public bool IsAudio { get; set; } = true;

        [DataMember(Name = "events")]
        public List<EventDescriptor> Events { get; set; } = new List<EventDescriptor>();
    }

    [DataContract]
    public class EventGroupMember
    {
        [DataMember(Name = "event_id")]
        public string EventId { get; set; }

        /// <summary>VEGAS' persistent EventID for this group member.</summary>
        [DataMember(Name = "vegas_event_id")]
        public long VegasEventId { get; set; }

        [DataMember(Name = "track_id")]
        public string TrackId { get; set; }

        [DataMember(Name = "timeline_start_seconds")]
        public double TimelineStartSeconds { get; set; }

        [DataMember(Name = "timeline_length_seconds")]
        public double TimelineLengthSeconds { get; set; }

        [DataMember(Name = "media_start_seconds")]
        public double MediaStartSeconds { get; set; }

        [DataMember(Name = "stream_index")]
        public uint StreamIndex { get; set; }

        [DataMember(Name = "is_video")]
        public bool IsVideo { get; set; }
    }

    [DataContract]
    public class EventGroupDescriptor
    {
        [DataMember(Name = "group_id")]
        public string GroupId { get; set; }

        [DataMember(Name = "kind")]
        public string Kind { get; set; }

        [DataMember(Name = "primary_event_id")]
        public string PrimaryEventId { get; set; }

        [DataMember(Name = "members")]
        public List<EventGroupMember> Members { get; set; } = new List<EventGroupMember>();
    }

    [DataContract]
    public class SyncTaskRequest
    {
        [DataMember(Name = "project_sample_rate")]
        public uint ProjectSampleRate { get; set; } = 48000;

        [DataMember(Name = "master_track_id")]
        public string MasterTrackId { get; set; }

        [DataMember(Name = "tracks")]
        public List<TrackDescriptor> Tracks { get; set; } = new List<TrackDescriptor>();

        [DataMember(Name = "groups")]
        public List<EventGroupDescriptor> Groups { get; set; } = new List<EventGroupDescriptor>();

        [DataMember(Name = "options")]
        public SyncOptions Options { get; set; } = new SyncOptions();

        public string ToJson()
        {
            var settings = new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            };
            var serializer = new DataContractJsonSerializer(typeof(SyncTaskRequest), settings);
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, this);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }

    [DataContract]
    public class SplitPoint
    {
        /// <summary>Discontinuity position in seconds, event-relative.</summary>
        [DataMember(Name = "timestamp_seconds")]
        public double TimestampSeconds { get; set; }

        /// <summary>
        /// Delay after minus delay before the discontinuity. The right-hand segment
        /// is shifted by the negative of this value in timeline seconds.
        /// </summary>
        [DataMember(Name = "step_jump_seconds")]
        public double StepJumpSeconds { get; set; }
    }

    [DataContract]
    public class EventSyncResult
    {
        [DataMember(Name = "event_id")]
        public string EventId { get; set; }

        [DataMember(Name = "track_id")]
        public string TrackId { get; set; }

        [DataMember(Name = "original_start_seconds")]
        public double OriginalStartSeconds { get; set; }

        [DataMember(Name = "resolved_start_seconds")]
        public double ResolvedStartSeconds { get; set; }

        [DataMember(Name = "delta_seconds")]
        public double DeltaSeconds { get; set; }

        [DataMember(Name = "playback_rate")]
        public double PlaybackRate { get; set; } = 1.0;

        [DataMember(Name = "drift_ppm")]
        public double DriftPpm { get; set; }

        [DataMember(Name = "confidence")]
        public double Confidence { get; set; }

        [DataMember(Name = "split_points_seconds")]
        public List<double> SplitPointsSeconds { get; set; } = new List<double>();

        /// <summary>
        /// Split points with their step jump. When present this is preferred over
        /// <see cref="SplitPointsSeconds"/> (which carries timestamps only).
        /// </summary>
        [DataMember(Name = "split_points")]
        public List<SplitPoint> SplitPoints { get; set; } = new List<SplitPoint>();

        [DataMember(Name = "is_aligned")]
        public bool IsAligned { get; set; }

        [DataMember(Name = "notes")]
        public string Notes { get; set; }
    }

    [DataContract]
    public class SyncManifest
    {
        [DataMember(Name = "session_id")]
        public string SessionId { get; set; }

        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "master_track_id")]
        public string MasterTrackId { get; set; }

        [DataMember(Name = "events")]
        public List<EventSyncResult> Events { get; set; } = new List<EventSyncResult>();

        [DataMember(Name = "warnings")]
        public List<string> Warnings { get; set; } = new List<string>();

        [DataMember(Name = "processing_time_ms")]
        public ulong ProcessingTimeMs { get; set; }

        public static SyncManifest FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            var settings = new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            };
            var serializer = new DataContractJsonSerializer(typeof(SyncManifest), settings);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (SyncManifest)serializer.ReadObject(ms);
            }
        }
    }
}
