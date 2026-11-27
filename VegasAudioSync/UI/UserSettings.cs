using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace VegasAudioSync.UI
{
    /// <summary>
    /// Operator choices persisted between VEGAS sessions. Optional processing
    /// toggles default to off, except Include video clips, Keep overlapping
    /// clips on separate tracks and Global matching which default to on; the
    /// master anchor defaults to Auto (recommended).
    /// </summary>
    [DataContract]
    public sealed class UserSettings
    {
        [DataMember(Name = "preserve_timeline_order")]
        public bool PreserveTimelineOrder { get; set; }

        [DataMember(Name = "detect_drift")]
        public bool DetectDrift { get; set; }

        [DataMember(Name = "sub_sample_accuracy")]
        public bool SubSampleAccuracy { get; set; }

        [DataMember(Name = "detect_discontinuities")]
        public bool DetectDiscontinuities { get; set; }

        [DataMember(Name = "voice_activity_filter")]
        public bool VoiceActivityFilter { get; set; }

        [DataMember(Name = "selected_only")]
        public bool SelectedOnly { get; set; }

        [DataMember(Name = "include_video")]
        public bool IncludeVideo { get; set; } = true;

        [DataMember(Name = "prevent_crossfades")]
        public bool PreventCrossfades { get; set; } = true;

        [DataMember(Name = "global_matching")]
        public bool GlobalMatching { get; set; } = true;

        [DataMember(Name = "master_track_id")]
        public string MasterTrackId { get; set; }

        [DataMember(Name = "master_track_name")]
        public string MasterTrackName { get; set; }

        [DataMember(Name = "master_track_is_video")]
        public bool MasterTrackIsVideo { get; set; }

        /// <summary>Preferred clock: auto, recorded, timecode, filename, file_time or ignore.</summary>
        [DataMember(Name = "time_source")]
        public string TimeSource { get; set; } = "auto";

        /// <summary>Clip ordering: auto, strict, timecode, preserve or none.</summary>
        [DataMember(Name = "clip_order")]
        public string ClipOrder { get; set; } = "auto";

        [OnDeserializing]
        private void ApplyDocumentedDefaults(StreamingContext context)
        {
            // DataContractJsonSerializer skips constructors and property
            // initializers, so a settings file written before a field existed would
            // otherwise load it as false. Seed the documented defaults; values
            // present in the file still overwrite them.
            IncludeVideo = true;
            PreventCrossfades = true;
            GlobalMatching = true;
            TimeSource = "auto";
            ClipOrder = "auto";
        }
    }

    /// <summary>
    /// Loads and saves <see cref="UserSettings"/> under
    /// %LOCALAPPDATA%\TellTale\WaveSync\settings.json. Failures are non-fatal:
    /// the extension keeps working with defaults.
    /// </summary>
    public static class UserSettingsStore
    {
        private static readonly object SyncRoot = new object();

        public static string SettingsPath
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "TellTale", "WaveSync", "settings.json");
            }
        }

        public static UserSettings Load()
        {
            lock (SyncRoot)
            {
                try
                {
                    string path = SettingsPath;
                    if (!File.Exists(path)) return new UserSettings();

                    var serializer = new DataContractJsonSerializer(typeof(UserSettings));
                    using (var stream = File.OpenRead(path))
                    {
                        var settings = serializer.ReadObject(stream) as UserSettings;
                        return settings ?? new UserSettings();
                    }
                }
                catch
                {
                    return new UserSettings();
                }
            }
        }

        public static void Save(UserSettings settings)
        {
            if (settings == null) return;

            lock (SyncRoot)
            {
                try
                {
                    string path = SettingsPath;
                    string directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    var serializer = new DataContractJsonSerializer(typeof(UserSettings));
                    using (var stream = new MemoryStream())
                    {
                        serializer.WriteObject(stream, settings);
                        File.WriteAllText(path, Encoding.UTF8.GetString(stream.ToArray()), new UTF8Encoding(false));
                    }
                }
                catch
                {
                    // Settings are a convenience; never let a read-only profile break sync.
                }
            }
        }
    }
}
