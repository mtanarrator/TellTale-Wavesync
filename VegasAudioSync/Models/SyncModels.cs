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
    }

    [DataContract]
    public class EventDescriptor
    {
        [DataMember(Name = "event_id")]
        public string EventId { get; set; }

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
