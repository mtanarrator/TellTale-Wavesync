using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace VegasAudioSync.Services
{
    /// <summary>Metadata read from one media file. All values are optional.</summary>
    public sealed class MediaMetadata
    {
        /// <summary>Container timecode as seconds of day (frames ignored).</summary>
        public double? TimecodeSeconds { get; set; }

        /// <summary>Container creation_time as epoch seconds (UTC when the tag is UTC).</summary>
        public double? CreationTimeSeconds { get; set; }

        /// <summary>Epoch seconds parsed from a timestamp in the filename (local time).</summary>
        public double? FilenameTimeSeconds { get; set; }
    }

    /// <summary>
    /// Per-file media metadata cache backed by ffprobe.
    /// Cache: %LOCALAPPDATA%\TellTale\WaveSync\media_meta.json, keyed by the
    /// normalized full path and validated with file size + last-write ticks.
    /// Every failure path is non-fatal: filename timestamps keep working even
    /// when ffprobe is missing.
    /// </summary>
    public static class MediaMetadataCache
    {
        private const int ProbeTimeoutMilliseconds = 8000;

        private static readonly object SyncRoot = new object();

        private static Dictionary<string, CacheEntry> _cache;
        private static bool _loaded;
        private static bool _dirty;

        // Files whose probe already failed in this run (cacheKey|size|mtime) so a
        // second Lookup never spawns ffprobe again for the same failed file.
        private static readonly HashSet<string> FailedProbes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool _probeResolved;
        private static string _probeArgument;
        private static string _probePath;

        private static readonly Regex FilenameTimestamp8 = new Regex(
            @"(?<!\d)(\d{8})[ _\-](\d{6}|\d{4})(?!\d)",
            RegexOptions.CultureInvariant);

        private static readonly Regex FilenameTimestamp6 = new Regex(
            @"(?<!\d)(\d{6})[ _\-](\d{6}|\d{4})(?!\d)",
            RegexOptions.CultureInvariant);

        public static string CachePath
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "TellTale", "WaveSync", "media_meta.json");
            }
        }

        /// <summary>
        /// Returns cached/ffprobe timecode and creation time plus the filename
        /// timestamp for <paramref name="path"/>. Never throws and never returns null.
        /// </summary>
        /// <param name="path">Media file path.</param>
        /// <param name="ffprobePath">
        /// ffprobe.exe, ffmpeg.exe or a folder containing ffprobe. When null,
        /// <see cref="DeviceOrganizer.FindFfprobe"/> discovers it from PATH/WinGet.
        /// </param>
        public static MediaMetadata Lookup(string path, string ffprobePath)
        {
            var metadata = new MediaMetadata();
            if (string.IsNullOrEmpty(path)) return metadata;

            try { metadata.FilenameTimeSeconds = ParseFilenameTimestamp(path); }
            catch { metadata.FilenameTimeSeconds = null; }

            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return metadata; }

            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists) return metadata;

                long size = info.Length;
                long mtime = info.LastWriteTimeUtc.Ticks;
                string cacheKey = NormalizePath(fullPath);

                CacheEntry entry = null;
                bool needsProbe = false;
                string probeKey = cacheKey + "|"
                    + size.ToString(CultureInfo.InvariantCulture) + "|"
                    + mtime.ToString(CultureInfo.InvariantCulture);

                lock (SyncRoot)
                {
                    EnsureLoadedLocked();

                    bool valid = _cache.TryGetValue(cacheKey, out entry)
                        && entry != null
                        && entry.Size == size
                        && entry.Mtime == mtime;

                    if (!valid)
                    {
                        entry = null;
                        if (_cache.Remove(cacheKey)) _dirty = true;
                        needsProbe = !FailedProbes.Contains(probeKey);
                    }
                }

                // Run ffprobe outside SyncRoot so background workers can probe
                // different media files concurrently without serializing on the lock.
                if (needsProbe)
                {
                    CacheEntry probed = Probe(fullPath, ffprobePath);
                    lock (SyncRoot)
                    {
                        if (probed != null)
                        {
                            probed.Path = fullPath;
                            probed.Size = size;
                            probed.Mtime = mtime;
                            _cache[cacheKey] = probed;
                            _dirty = true;   // flushed once at the end of extraction
                            entry = probed;
                        }
                        else
                        {
                            FailedProbes.Add(probeKey);
                        }
                    }
                }

                if (entry != null)
                {
                    metadata.TimecodeSeconds = Sanitize(entry.TimecodeSeconds);
                    metadata.CreationTimeSeconds = Sanitize(entry.CreationTimeSeconds);
                }
            }
            catch
            {
                // A probe/cache failure must never break timeline extraction.
            }

            return metadata;
        }

        /// <summary>
        /// Persists pending cache changes once per extraction run. Lookup only
        /// marks the cache dirty; the extractor calls this from a finally block.
        /// Never throws.
        /// </summary>
        public static void Flush()
        {
            try
            {
                lock (SyncRoot)
                {
                    EnsureLoadedLocked();
                    if (_dirty) SaveLocked();
                }
            }
            catch
            {
                // Caching is an optimization; failures are non-fatal.
            }
        }

        /// <summary>
        /// Parses a container timecode to seconds of day. Accepts 2 to 4
        /// colon-separated fields; frames are ignored:
        /// 2 fields = HH:MM, 3 = HH:MM:SS, 4 = HH:MM:SS:FF.
        /// Drop-frame semicolons and decimal dots are treated as colons.
        /// Returns null for impossible clocks (hours &gt;= 24, minutes/seconds &gt;= 60).
        /// </summary>
        public static double? ParseTimecodeSeconds(string rawTimecode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rawTimecode)) return null;

                string value = rawTimecode.Trim().Replace(';', ':').Replace('.', ':');
                string[] parts = value.Split(new[] { ':' });
                if (parts.Length < 2 || parts.Length > 4) return null;

                var numbers = new int[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    int number;
                    if (!int.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out number))
                        return null;
                    if (number < 0) return null;
                    numbers[i] = number;
                }

                int hours = numbers[0];
                int minutes = numbers[1];
                if (hours >= 24 || minutes >= 60) return null;

                int secondsField = 0;
                if (parts.Length >= 3)
                {
                    secondsField = numbers[2];
                    if (secondsField >= 60) return null;
                }

                // Frames (4th field) are validated as non-negative but otherwise
                // ignored: timecode is seconds-of-day at nominal frame rate.
                return hours * 3600.0 + minutes * 60.0 + secondsField;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Parses YYMMDD_HHMMSS, YYMMDD_HHMM, YYYYMMDD_HHMMSS or YYYYMMDD_HHMM
        /// from a file name/path. The camera clock is assumed to be local time,
        /// so the result is the epoch of that local wall clock. Returns null on
        /// anything that is not a valid timestamp.
        /// </summary>
        public static double? ParseFilenameTimestamp(string pathOrFileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pathOrFileName)) return null;

                string name;
                try { name = Path.GetFileName(pathOrFileName); }
                catch { name = pathOrFileName; }
                try { name = Path.GetFileNameWithoutExtension(name); }
                catch { /* keep the original name */ }

                Match match = FilenameTimestamp8.Match(name);
                if (!match.Success) match = FilenameTimestamp6.Match(name);
                if (!match.Success) return null;

                string date = match.Groups[1].Value;
                string time = match.Groups[2].Value;

                int year;
                int month;
                int day;

                if (date.Length == 8)
                {
                    year = int.Parse(date.Substring(0, 4), CultureInfo.InvariantCulture);
                    month = int.Parse(date.Substring(4, 2), CultureInfo.InvariantCulture);
                    day = int.Parse(date.Substring(6, 2), CultureInfo.InvariantCulture);
                }
                else
                {
                    int shortYear = int.Parse(date.Substring(0, 2), CultureInfo.InvariantCulture);
                    year = shortYear <= 69 ? 2000 + shortYear : 1900 + shortYear;
                    month = int.Parse(date.Substring(2, 2), CultureInfo.InvariantCulture);
                    day = int.Parse(date.Substring(4, 2), CultureInfo.InvariantCulture);
                }

                int hour = int.Parse(time.Substring(0, 2), CultureInfo.InvariantCulture);
                int minute = int.Parse(time.Substring(2, 2), CultureInfo.InvariantCulture);
                int second = time.Length >= 6
                    ? int.Parse(time.Substring(4, 2), CultureInfo.InvariantCulture)
                    : 0;

                var localTime = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
                return ToEpochSeconds(localTime.ToUniversalTime());
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return Path.GetFullPath(path).ToLowerInvariant(); }
            catch { return path.ToLowerInvariant(); }
        }

        private static void EnsureLoadedLocked()
        {
            if (_loaded) return;
            _loaded = true;
            _dirty = false;
            _cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string path = CachePath;
                if (!File.Exists(path)) return;

                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return;

                var serializer = new DataContractJsonSerializer(typeof(CacheDocument));
                CacheDocument document;
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    document = serializer.ReadObject(stream) as CacheDocument;
                }

                if (document == null || document.Entries == null) return;
                foreach (CacheEntry entry in document.Entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Path)) continue;
                    _cache[NormalizePath(entry.Path)] = entry;
                }
            }
            catch
            {
                // Corrupt or unreadable cache: start fresh.
                _cache.Clear();
            }
        }

        private static void SaveLocked()
        {
            try
            {
                var document = new CacheDocument
                {
                    Entries = new List<CacheEntry>(_cache.Values)
                };

                var serializer = new DataContractJsonSerializer(typeof(CacheDocument));
                string json;
                using (var stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, document);
                    json = Encoding.UTF8.GetString(stream.ToArray());
                }

                string path = CachePath;
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                string temp = path + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                _dirty = false;
            }
            catch
            {
                // Caching is an optimization; failures are non-fatal.
            }
        }

        private static string ResolveFfprobe(string ffprobePathOrFfmpeg)
        {
            string argument = ffprobePathOrFfmpeg ?? string.Empty;
            lock (SyncRoot)
            {
                if (_probeResolved && string.Equals(_probeArgument, argument, StringComparison.OrdinalIgnoreCase))
                    return _probePath;
            }

            string resolved = DeviceOrganizer.FindFfprobe(argument);
            lock (SyncRoot)
            {
                _probeArgument = argument;
                _probePath = resolved;
                _probeResolved = true;
                return _probePath;
            }
        }

        private static CacheEntry Probe(string path, string ffprobePathOrFfmpeg)
        {
            try
            {
                string ffprobe = ResolveFfprobe(ffprobePathOrFfmpeg);
                if (string.IsNullOrEmpty(ffprobe)) return null;

                string stdout;
                int exitCode;
                if (!RunFfprobe(ffprobe, path, out stdout, out exitCode)) return null;
                if (exitCode != 0 || string.IsNullOrEmpty(stdout)) return null;

                ProbeResponse response = ParseProbeResponse(stdout);
                if (response == null) return new CacheEntry();

                string timecode = GetTag(response.Format != null ? response.Format.Tags : null, "timecode");
                if (string.IsNullOrEmpty(timecode) && response.Streams != null)
                {
                    foreach (ProbeStream stream in response.Streams)
                    {
                        string streamTimecode = GetTag(stream != null ? stream.Tags : null, "timecode");
                        if (!string.IsNullOrEmpty(streamTimecode))
                        {
                            timecode = streamTimecode;
                            break;
                        }
                    }
                }

                string creationTime = GetTag(response.Format != null ? response.Format.Tags : null, "creation_time");

                return new CacheEntry
                {
                    TimecodeSeconds = ParseTimecodeSeconds(timecode),
                    CreationTimeSeconds = ParseCreationTime(creationTime)
                };
            }
            catch
            {
                return null;
            }
        }

        private static bool RunFfprobe(string ffprobe, string path, out string stdout, out int exitCode)
        {
            stdout = null;
            exitCode = -1;

            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = "-v error -show_entries format_tags=creation_time,timecode "
                    + "-show_entries stream_tags=timecode -of json -- \""
                    + path.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (var process = new Process { StartInfo = startInfo })
                {
                    var output = new StringBuilder();

                    process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs args)
                    {
                        if (args.Data != null) output.AppendLine(args.Data);
                    };
                    process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs args)
                    {
                        // stderr is drained so ffprobe can never block on a full pipe.
                    };

                    if (!process.Start()) return false;

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(ProbeTimeoutMilliseconds))
                    {
                        try { process.Kill(); } catch { }
                        try { process.WaitForExit(1000); } catch { }
                        return false;
                    }

                    process.WaitForExit();
                    exitCode = process.ExitCode;
                    stdout = output.ToString();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static ProbeResponse ParseProbeResponse(string json)
        {
            try
            {
                var settings = new DataContractJsonSerializerSettings
                {
                    UseSimpleDictionaryFormat = true
                };
                var serializer = new DataContractJsonSerializer(typeof(ProbeResponse), settings);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return serializer.ReadObject(stream) as ProbeResponse;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string GetTag(Dictionary<string, string> tags, string key)
        {
            if (tags == null) return null;

            string value;
            if (tags.TryGetValue(key, out value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();

            foreach (KeyValuePair<string, string> pair in tags)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    return pair.Value.Trim();
                }
            }

            return null;
        }

        private static double? ParseCreationTime(string raw)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(raw)) return null;

                DateTime parsed;
                if (!DateTime.TryParse(
                    raw.Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out parsed))
                {
                    return null;
                }

                return ToEpochSeconds(parsed);
            }
            catch
            {
                return null;
            }
        }

        private static double ToEpochSeconds(DateTime utc)
        {
            return (utc.ToUniversalTime()
                - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static double? Sanitize(double? value)
        {
            if (!value.HasValue) return null;
            double seconds = value.Value;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return null;
            return seconds;
        }

        [DataContract]
        private sealed class CacheDocument
        {
            [DataMember(Name = "entries")]
            public List<CacheEntry> Entries { get; set; }
        }

        [DataContract]
        private sealed class CacheEntry
        {
            [DataMember(Name = "path")]
            public string Path { get; set; }

            [DataMember(Name = "size")]
            public long Size { get; set; }

            /// <summary>Last-write time in UTC ticks.</summary>
            [DataMember(Name = "mtime")]
            public long Mtime { get; set; }

            [DataMember(Name = "timecode_seconds")]
            public double? TimecodeSeconds { get; set; }

            [DataMember(Name = "creation_time_seconds")]
            public double? CreationTimeSeconds { get; set; }
        }

        [DataContract]
        private sealed class ProbeResponse
        {
            [DataMember(Name = "format")]
            public ProbeFormat Format { get; set; }

            [DataMember(Name = "streams")]
            public List<ProbeStream> Streams { get; set; }
        }

        [DataContract]
        private sealed class ProbeFormat
        {
            [DataMember(Name = "tags")]
            public Dictionary<string, string> Tags { get; set; }
        }

        [DataContract]
        private sealed class ProbeStream
        {
            [DataMember(Name = "tags")]
            public Dictionary<string, string> Tags { get; set; }
        }
    }
}
