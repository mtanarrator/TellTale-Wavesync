using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using ScriptPortal.Vegas;

namespace VegasAudioSync.Services
{
    /// <summary>
    /// Remembers the playback rate WaveSync itself wrote for each event, so a later
    /// run can tell "our drift stretch" from a deliberate manual retime. Without this,
    /// the 0.9-1.1 drift band would overwrite a user's 0.95x slow motion.
    /// </summary>
    internal sealed class RateHistorySession
    {
        private const int MaxRecords = 5000;
        private const double RateTolerance = 1e-4;
        private static readonly object SyncRoot = new object();

        [DataContract]
        private sealed class RateRecord
        {
            [DataMember(Name = "project")] public string Project { get; set; }
            [DataMember(Name = "event_id")] public long EventId { get; set; }
            [DataMember(Name = "media")] public string Media { get; set; }
            [DataMember(Name = "rate")] public double Rate { get; set; }
            [DataMember(Name = "updated_utc")] public long UpdatedUtc { get; set; }
        }

        [DataContract]
        private sealed class Document
        {
            [DataMember(Name = "records")] public List<RateRecord> Records { get; set; }
        }

        private readonly Dictionary<string, RateRecord> _records;
        private readonly string _projectKey;
        private bool _dirty;

        private RateHistorySession(string projectKey, Dictionary<string, RateRecord> records)
        {
            _projectKey = projectKey;
            _records = records;
        }

        public static string HistoryPath
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "TellTale", "WaveSync", "rate_history.json");
            }
        }

        public static RateHistorySession Open(Vegas vegas)
        {
            string projectKey = null;
            try
            {
                string path = vegas != null && vegas.Project != null ? vegas.Project.FilePath : null;
                if (!string.IsNullOrEmpty(path))
                {
                    projectKey = CleanProjectPath(path);
                }
            }
            catch
            {
                // Unsaved or unavailable project: history is disabled for this run.
            }

            return new RateHistorySession(projectKey, LoadRecords());
        }

        /// <summary>
        /// True when WaveSync may change this event's rate: either the clip is still at
        /// 1.0, or the live rate matches the rate WaveSync recorded last time. A
        /// mismatch is treated as a manual edit, the stale record is dropped, and the
        /// rate is left alone.
        /// </summary>
        public bool IsEligible(long eventId, string mediaPath, double currentRate, Action<string> log)
        {
            if (!IsFinite(currentRate)) currentRate = 1.0;

            if (_projectKey == null)
            {
                // No project path: never guess that a non-1.0 rate is ours.
                return Math.Abs(currentRate - 1.0) <= RateTolerance;
            }

            string key = Key(_projectKey, eventId);
            RateRecord record;
            if (_records.TryGetValue(key, out record) && record != null)
            {
                bool mediaMatches = string.IsNullOrEmpty(mediaPath)
                    || string.IsNullOrEmpty(record.Media)
                    || string.Equals(mediaPath, record.Media, StringComparison.OrdinalIgnoreCase);

                if (mediaMatches && Math.Abs(currentRate - record.Rate) <= RateTolerance)
                {
                    return true;
                }

                _records.Remove(key);
                _dirty = true;
                log?.Invoke(
                    $"  Kept manual playback rate {currentRate:F4} for [{eventId}] (WaveSync had recorded {record.Rate:F4}).");
                return false;
            }

            if (Math.Abs(currentRate - 1.0) > RateTolerance)
            {
                log?.Invoke(
                    $"  Kept unrecorded playback rate {currentRate:F4} for [{eventId}] (treated as a manual retime).");
                return false;
            }

            return true;
        }

        public void Record(long eventId, string mediaPath, double rate)
        {
            if (_projectKey == null) return;

            if (Math.Abs(rate - 1.0) <= RateTolerance)
            {
                Forget(eventId);
                return;
            }

            _records[Key(_projectKey, eventId)] = new RateRecord
            {
                Project = _projectKey,
                EventId = eventId,
                Media = mediaPath,
                Rate = rate,
                UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };
            _dirty = true;
        }

        public void Forget(long eventId)
        {
            if (_projectKey == null) return;
            if (_records.Remove(Key(_projectKey, eventId)))
            {
                _dirty = true;
            }
        }

        public void Save()
        {
            if (!_dirty) return;

            lock (SyncRoot)
            {
                try
                {
                    var document = new Document
                    {
                        Records = _records.Values
                            .OrderByDescending(r => r.UpdatedUtc)
                            .Take(MaxRecords)
                            .ToList()
                    };

                    string directory = Path.GetDirectoryName(HistoryPath);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    var serializer = new DataContractJsonSerializer(typeof(Document));
                    using (var stream = File.Create(HistoryPath))
                    {
                        serializer.WriteObject(stream, document);
                    }

                    _dirty = false;
                }
                catch
                {
                    // Diagnostics only; failing to persist must not affect the apply.
                }
            }
        }

        private static Dictionary<string, RateRecord> LoadRecords()
        {
            lock (SyncRoot)
            {
                var records = new Dictionary<string, RateRecord>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (!File.Exists(HistoryPath)) return records;

                    var serializer = new DataContractJsonSerializer(typeof(Document));
                    using (var stream = File.OpenRead(HistoryPath))
                    {
                        var document = serializer.ReadObject(stream) as Document;
                        if (document != null && document.Records != null)
                        {
                            foreach (var record in document.Records)
                            {
                                if (record == null || string.IsNullOrEmpty(record.Project)) continue;
                                records[Key(record.Project, record.EventId)] = record;
                            }
                        }
                    }
                }
                catch
                {
                    // Corrupt history is ignored; it only affects reset detection.
                }

                return records;
            }
        }

        private static string CleanProjectPath(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant();
            }
            catch
            {
                return path.Trim().ToLowerInvariant();
            }
        }

        private static string Key(string project, long eventId)
        {
            return project + "|" + eventId.ToString();
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
