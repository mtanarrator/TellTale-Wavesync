using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace VegasAudioSync.UI
{
    /// <summary>
    /// Operator choices persisted between VEGAS sessions. All optional processing
    /// toggles default to off; the master anchor defaults to Auto-Detect.
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
        public bool IncludeVideo { get; set; }

        [DataMember(Name = "prevent_crossfades")]
        public bool PreventCrossfades { get; set; }

        [DataMember(Name = "master_track_id")]
        public string MasterTrackId { get; set; }

        [DataMember(Name = "master_track_name")]
        public string MasterTrackName { get; set; }

        [DataMember(Name = "master_track_is_video")]
        public bool MasterTrackIsVideo { get; set; }
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
