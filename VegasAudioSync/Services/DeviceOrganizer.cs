using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ScriptPortal.Vegas;
using VegasAudioSync.Models;

namespace VegasAudioSync.Services
{
    /// <summary>
    /// Scans the timeline for recording devices and organizes their events onto
    /// per-device camera/recorder tracks. Scan never mutates the project; Apply is
    /// one UndoBlock and keeps the original source tracks.
    /// </summary>
    public static class DeviceOrganizer
    {
        private const int ProbeTimeoutMilliseconds = 8000;

        private const string UndoLabel = "Organize Tracks by Device";

        /// <summary>
        /// Camera clips shorter than this preview as "B-roll". The value mirrors
        /// SyncOptions.BrollMaxSeconds (and TimelineExtractor's fallback) because
        /// Scan keeps its contract signature and does not receive sync options.
        /// </summary>
        public const double DefaultBrollMaxSeconds = 15.0;

        private sealed class ProbeInfo
        {
            public bool HasVideo;
            public bool HasAudio;
            public string Make;
            public string Model;
        }

        private sealed class GroupCandidate
        {
            public string Key;
            public DeviceConfidence Confidence;
            public string DisplayName;
            public string Evidence;
            public string Folder;
            public bool AnyVideo;
            public List<DeviceEventRef> Events = new List<DeviceEventRef>();
        }

        private sealed class LiveRef
        {
            public DeviceEventRef Ref;
            public TrackEvent Event;
        }

        // =====================================================================
        // Scan
        // =====================================================================

        public static DeviceScanResult Scan(Vegas vegas, string ffprobePath, Action<string> log)
        {
            if (vegas == null || vegas.Project == null)
                throw new InvalidOperationException("No active VEGAS project loaded.");

            var result = new DeviceScanResult();
            string projectDir = TryGetProjectDirectory(vegas);

            bool probeAvailable = !string.IsNullOrEmpty(ffprobePath) && File.Exists(ffprobePath);
            if (!probeAvailable)
            {
                AddWarning(result, log, "ffprobe was not found; grouping uses filename prefixes and media folders only.");
            }

            var probeCache = new Dictionary<string, ProbeInfo>(StringComparer.OrdinalIgnoreCase);
            var candidates = new Dictionary<string, GroupCandidate>(StringComparer.OrdinalIgnoreCase);
            int mediaEventCount = 0;
            int probedFiles = 0;
            int probeFailures = 0;
            int skippedWithoutTake = 0;
            int skippedWithoutMedia = 0;

            foreach (Track track in vegas.Project.Tracks)
            {
                bool trackIsVideo = SafeIsVideo(track);
                foreach (TrackEvent ev in track.Events)
                {
                    Take take = null;
                    try { take = ev.ActiveTake; }
                    catch { take = null; }
                    if (take == null) { skippedWithoutTake++; continue; }

                    string mediaPath = ResolveTakeFilePath(take, projectDir);
                    if (string.IsNullOrEmpty(mediaPath)) { skippedWithoutMedia++; continue; }

                    string normalizedPath = NormalizePathKey(mediaPath);
                    string folder = null;
                    try { folder = Path.GetDirectoryName(mediaPath); }
                    catch { folder = null; }

                    double startSeconds = 0.0;
                    double lengthSeconds = 0.0;
                    double offsetSeconds = 0.0;
                    try { startSeconds = ev.Start.ToMilliseconds() / 1000.0; } catch { }
                    try { lengthSeconds = ev.Length.ToMilliseconds() / 1000.0; } catch { }
                    try { offsetSeconds = take.Offset.ToMilliseconds() / 1000.0; } catch { }

                    bool isGrouped = false;
                    try { isGrouped = ev.IsGrouped; } catch { isGrouped = false; }

                    var eventRef = new DeviceEventRef
                    {
                        VegasEventId = ev.EventID,
                        TrackIndex = track.Index,
                        IsVideoEvent = trackIsVideo,
                        IsGrouped = isGrouped,
                        MediaPath = mediaPath,
                        MediaFolder = folder,
                        StartSeconds = startSeconds,
                        LengthSeconds = lengthSeconds,
                        MediaOffsetSeconds = offsetSeconds,
                        PlaybackRate = SanitizePlaybackRate(SafePlaybackRate(ev))
                    };
                    mediaEventCount++;

                    ProbeInfo probe = null;
                    if (probeAvailable)
                    {
                        if (!probeCache.TryGetValue(normalizedPath, out probe))
                        {
                            string error;
                            probe = ProbeMedia(ffprobePath, mediaPath, out error);
                            probeCache[normalizedPath] = probe;
                            probedFiles++;
                            if (!string.IsNullOrEmpty(error)) probeFailures++;
                        }
                    }

                    string make = probe != null ? CleanValue(probe.Make) : null;
                    string model = probe != null ? CleanValue(probe.Model) : null;

                    // DeviceIdentity is the single source of truth for the grouping
                    // key (it matches TimelineExtractor's SourceKey). Metadata only
                    // enriches evidence; it never replaces the key or display name.
                    string identityKey = DeviceIdentity.Key(mediaPath, folder);
                    string displayName = DeviceIdentity.DisplayName(mediaPath, folder);
                    string evidence = DeviceIdentity.Evidence(mediaPath, folder);
                    DeviceConfidence confidence = MapConfidence(DeviceIdentity.Confidence(mediaPath, folder));

                    bool hasMetadata = probe != null
                        && (!string.IsNullOrEmpty(make) || !string.IsNullOrEmpty(model));
                    string metadataEvidence = hasMetadata
                        ? ("metadata " + ((make ?? string.Empty) + " " + (model ?? string.Empty)).Trim()).Trim()
                        : null;

                    string key;
                    if (!string.IsNullOrEmpty(identityKey))
                    {
                        key = identityKey;
                        if (!string.IsNullOrEmpty(metadataEvidence))
                        {
                            evidence = string.IsNullOrEmpty(evidence)
                                ? metadataEvidence
                                : evidence + "; " + metadataEvidence;
                        }
                    }
                    else if (hasMetadata)
                    {
                        // Last resort: no path-based identity, but ffprobe knows the device.
                        key = "meta:" + (make ?? string.Empty).ToLowerInvariant()
                            + "|" + (model ?? string.Empty).ToLowerInvariant();
                        displayName = BuildMetadataName(make, model);
                        evidence = metadataEvidence;
                        confidence = DeviceConfidence.High;
                    }
                    else
                    {
                        // No model/prefix/folder evidence: keep the events visible
                        // instead of dropping them, but never merge them silently.
                        string folderKey = string.IsNullOrEmpty(folder)
                            ? string.Empty
                            : folder.TrimEnd('\\', '/').ToLowerInvariant();
                        key = "folder:" + folderKey;
                        confidence = DeviceConfidence.Low;
                        if (string.IsNullOrEmpty(displayName)
                            || string.Equals(displayName, "Unknown", StringComparison.OrdinalIgnoreCase))
                        {
                            displayName = FolderLeaf(folder) ?? "Unknown device";
                        }
                        if (string.IsNullOrEmpty(evidence)) evidence = "folder leaf fallback";
                    }

                    GroupCandidate candidate;
                    if (!candidates.TryGetValue(key, out candidate))
                    {
                        candidate = new GroupCandidate
                        {
                            Key = key,
                            Confidence = confidence,
                            DisplayName = displayName,
                            Evidence = evidence,
                            Folder = folder
                        };
                        candidates[key] = candidate;
                    }
                    else if (string.IsNullOrEmpty(candidate.DisplayName))
                    {
                        candidate.DisplayName = displayName;
                    }

                    candidate.Events.Add(eventRef);
                    bool mediaHasVideo = probe != null && probe.HasVideo;
                    if (trackIsVideo || mediaHasVideo)
                    {
                        candidate.AnyVideo = true;
                    }
                }
            }

            if (probeFailures > 0)
            {
                AddWarning(result, log,
                    $"ffprobe failed for {probeFailures} media file(s); those clips were grouped by prefix/folder heuristics.");
            }

            if (skippedWithoutTake > 0 || skippedWithoutMedia > 0)
            {
                AddWarning(result, log,
                    $"{skippedWithoutTake + skippedWithoutMedia} timeline event(s) were skipped because they have no take or no resolvable media file (generators/offline media).");
            }

            foreach (GroupCandidate candidate in candidates.Values)
            {
                DeviceKind kind = candidate.AnyVideo ? DeviceKind.Camera : DeviceKind.Recorder;
                string name = SafeTrackName(candidate.DisplayName);
                string evidence = string.IsNullOrEmpty(candidate.Evidence)
                    ? "no device hint"
                    : candidate.Evidence;

                result.Groups.Add(new DeviceGroup
                {
                    Id = BuildStableId(candidate.Key, kind),
                    Name = name,
                    Kind = kind,
                    Confidence = candidate.Confidence,
                    Evidence = evidence,
                    Folder = candidate.Folder,
                    Include = true,
                    Events = candidate.Events
                });
            }

            // Cameras first, then display name, then folder for deterministic output.
            result.Groups.Sort(delegate (DeviceGroup a, DeviceGroup b)
            {
                int aKind = a.Kind == DeviceKind.Camera ? 0 : 1;
                int bKind = b.Kind == DeviceKind.Camera ? 0 : 1;
                if (aKind != bKind) return aKind.CompareTo(bKind);

                int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                if (byName != 0) return byName;

                int byFolder = string.Compare(a.Folder ?? string.Empty, b.Folder ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
                if (byFolder != 0) return byFolder;

                return string.Compare(a.Id, b.Id, StringComparison.Ordinal);
            });

            if (log != null)
            {
                log(string.Format(CultureInfo.InvariantCulture,
                    "Device scan: {0} device(s) from {1} media event(s), {2} file(s) probed.",
                    result.Groups.Count, mediaEventCount, probedFiles));
            }

            return result;
        }

        private static string BuildMetadataName(string make, string model)
        {
            if (!string.IsNullOrEmpty(model)) return model;
            if (!string.IsNullOrEmpty(make)) return make;
            return "Unknown device";
        }

        private static DeviceConfidence MapConfidence(int confidence)
        {
            if (confidence >= 2) return DeviceConfidence.High;
            if (confidence == 1) return DeviceConfidence.Medium;
            return DeviceConfidence.Low;
        }

        /// <summary>
        /// Preview-only role: cameras, recorders, or a camera group made entirely of
        /// clips shorter than <paramref name="brollMaxSeconds"/>. B-roll never changes
        /// track assignment; it exists to explain short pickup clips in the dialog.
        /// </summary>
        public static string RoleLabel(DeviceGroup group, double brollMaxSeconds)
        {
            if (group == null) return string.Empty;
            if (group.Kind == DeviceKind.Recorder) return "Recorder";

            double threshold = brollMaxSeconds > 0.0 ? brollMaxSeconds : DefaultBrollMaxSeconds;
            bool anyClip = false;
            if (group.Events != null)
            {
                foreach (DeviceEventRef ev in group.Events)
                {
                    if (ev == null) continue;
                    anyClip = true;
                    // Unknown (0) or long clips make this a normal camera group.
                    if (ev.LengthSeconds <= 0.0 || ev.LengthSeconds >= threshold) return "Camera";
                }
            }

            return anyClip ? "B-roll" : "Camera";
        }

        private static string BuildStableId(string key, DeviceKind kind)
        {
            string input = (kind == DeviceKind.Camera ? "cam|" : "rec|") + (key ?? string.Empty);
            unchecked
            {
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                ulong hash = offset;
                foreach (char c in input)
                {
                    hash ^= (byte)(c & 0xFF);
                    hash *= prime;
                    hash ^= (byte)((c >> 8) & 0xFF);
                    hash *= prime;
                }
                return "dev-" + hash.ToString("x16", CultureInfo.InvariantCulture);
            }
        }

        private static string FolderLeaf(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            try
            {
                string trimmed = folder.TrimEnd('\\', '/');
                if (trimmed.Length == 0) return folder;
                string leaf = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
            }
            catch
            {
                return null;
            }
        }

        // =====================================================================
        // Apply
        // =====================================================================

        public static DeviceApplyResult Apply(
            Vegas vegas,
            DeviceScanResult scan,
            Action<string> log)
        {
            if (vegas == null || vegas.Project == null)
                throw new InvalidOperationException("No active VEGAS project loaded.");

            var result = new DeviceApplyResult();

            // Persistent VEGAS EventIDs survive event reordering, so they are the
            // only safe way to find the clips the scan saw.
            var liveEvents = new Dictionary<long, TrackEvent>();
            foreach (Track track in vegas.Project.Tracks)
            {
                foreach (TrackEvent ev in track.Events)
                {
                    liveEvents[ev.EventID] = ev;
                }
            }

            // Number devices from the full scan order (including excluded groups), so
            // toggling an include checkbox never renumbers the remaining devices.
            var cameraNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
            var recorderNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
            int cameraNo = 0;
            int recorderNo = 0;
            if (scan != null && scan.Groups != null)
            {
                foreach (DeviceGroup group in scan.Groups)
                {
                    if (group == null || string.IsNullOrEmpty(group.Id)) continue;
                    if (group.Kind == DeviceKind.Camera) cameraNumbers[group.Id] = ++cameraNo;
                    else recorderNumbers[group.Id] = ++recorderNo;
                }
            }

            var targetTrackNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var undo = new UndoBlock(UndoLabel))
            {
                if (scan != null && scan.Groups != null)
                {
                    foreach (DeviceGroup group in scan.Groups)
                    {
                        if (group == null || !group.Include) continue;

                        try
                        {
                            List<LiveRef> live = ResolveLiveEvents(group, liveEvents, result, log);
                            if (live.Count == 0)
                            {
                                result.GroupsSkipped++;
                                AddWarning(result, log,
                                    $"Device '{SafeTrackName(group.Name)}': no timeline events remain; group skipped.");
                                continue;
                            }

                            int number = group.Kind == DeviceKind.Camera
                                ? GetNumber(cameraNumbers, group.Id)
                                : GetNumber(recorderNumbers, group.Id);
                            int movedBefore = result.EventsMoved;

                            string deviceName = SafeTrackName(group.Name);
                            string videoName = string.Format("CAM {0} \u2014 {1} (V)", number, deviceName);
                            string recorderName = string.Format("REC {0} \u2014 {1}", number, deviceName);
                            bool alreadyInPlace = group.Kind == DeviceKind.Camera
                                ? AllVideoEventsOnTrack(live, videoName)
                                : AllEventsOnTracks(live, recorderName, null);

                            if (group.Kind == DeviceKind.Camera)
                            {
                                ApplyCameraGroup(vegas, group, live, number,
                                    targetTrackNames, result, log);
                            }
                            else
                            {
                                ApplyRecorderGroup(vegas, group, live, number,
                                    targetTrackNames, result, log);
                            }

                            if (result.EventsMoved > movedBefore || alreadyInPlace)
                            {
                                result.GroupsApplied++;
                            }
                            else
                            {
                                result.GroupsSkipped++;
                                AddWarning(result, log,
                                    $"Device '{SafeTrackName(group.Name)}': no events could be moved; group skipped.");
                            }
                        }
                        catch (Exception ex)
                        {
                            result.GroupsSkipped++;
                            AddWarning(result, log,
                                $"Device '{SafeTrackName(group.Name)}': {ex.Message}; group skipped.");
                        }
                    }
                }

                if (log != null)
                {
                    log(string.Format(CultureInfo.InvariantCulture,
                        "Organize by device: {0} applied, {1} skipped, {2} track(s) created, {3} event(s) moved.",
                        result.GroupsApplied, result.GroupsSkipped, result.TracksCreated,
                        result.EventsMoved));
                }

                vegas.UpdateUI();
            }

            return result;
        }

        private static void ApplyCameraGroup(
            Vegas vegas,
            DeviceGroup group,
            List<LiveRef> live,
            int number,
            HashSet<string> targetNames,
            DeviceApplyResult result,
            Action<string> log)
        {
            string deviceName = SafeTrackName(group.Name);
            string videoName = string.Format("CAM {0} \u2014 {1} (V)", number, deviceName);

            Track videoTrack = FindOrCreateTrack(vegas, videoName, true, result, log);
            if (videoTrack == null)
            {
                throw new InvalidOperationException("VEGAS did not provide the camera video track.");
            }

            // A camera clip is one video event with embedded audio (even if the mic
            // was muted). Cameras always map to exactly one video track; any rare
            // separate audio-only clips are left where they are.
            targetNames.Add(videoName);
            int audioOnlyLeft = 0;
            foreach (LiveRef item in live)
            {
                if (!item.Ref.IsVideoEvent) audioOnlyLeft++;
            }

            MoveLiveEvents(live, videoTrack, null, result, log);
            VerifyGroupedTracks(live, videoTrack, null, result, log);

            if (audioOnlyLeft > 0)
            {
                log?.Invoke($"  Camera '{deviceName}': left {audioOnlyLeft} separate audio clip(s) on their existing track(s).");
            }
        }

        private static void ApplyRecorderGroup(
            Vegas vegas,
            DeviceGroup group,
            List<LiveRef> live,
            int number,
            HashSet<string> targetNames,
            DeviceApplyResult result,
            Action<string> log)
        {
            string trackName = string.Format("REC {0} \u2014 {1}", number, SafeTrackName(group.Name));
            Track audioTrack = FindOrCreateTrack(vegas, trackName, false, result, log);
            if (audioTrack == null)
            {
                throw new InvalidOperationException("VEGAS did not provide the recorder audio track.");
            }

            targetNames.Add(trackName);
            MoveLiveEvents(live, null, audioTrack, result, log);
            VerifyGroupedTracks(live, null, audioTrack, result, log);
        }

        private static int GetNumber(Dictionary<string, int> numbers, string id)
        {
            int number;
            if (numbers != null && !string.IsNullOrEmpty(id) && numbers.TryGetValue(id, out number))
            {
                return number;
            }
            return 1;
        }

        private static bool AllVideoEventsOnTrack(List<LiveRef> live, string videoName)
        {
            if (live == null || live.Count == 0) return false;
            bool anyVideo = false;
            foreach (LiveRef item in live)
            {
                if (!item.Ref.IsVideoEvent) continue;
                anyVideo = true;
                Track actual = null;
                try { actual = item.Event.Track; } catch { actual = null; }
                if (actual == null) return false;
                if (!string.Equals(actual.Name, videoName, StringComparison.Ordinal)) return false;
            }
            return anyVideo;
        }

        private static bool AllEventsOnTracks(List<LiveRef> live, string primaryName, string secondaryName)
        {
            if (live == null || live.Count == 0) return false;
            foreach (LiveRef item in live)
            {
                Track actual = null;
                try { actual = item.Event.Track; } catch { actual = null; }
                if (actual == null) return false;

                string expected = item.Ref.IsVideoEvent ? primaryName : (secondaryName ?? primaryName);
                if (string.IsNullOrEmpty(expected)) return false;
                if (!string.Equals(actual.Name, expected, StringComparison.Ordinal)) return false;
            }
            return true;
        }



        private static Track FindOrCreateTrack(
            Vegas vegas,
            string name,
            bool video,
            DeviceApplyResult result,
            Action<string> log)
        {
            foreach (Track candidate in vegas.Project.Tracks)
            {
                if (!SafeIsValid(candidate)) continue;
                if (video ? !SafeIsVideo(candidate) : !SafeIsAudio(candidate)) continue;
                if (!string.Equals(candidate.Name, name, StringComparison.Ordinal)) continue;
                return candidate;
            }

            Track created = video
                ? (Track)vegas.Project.AddVideoTrack()
                : (Track)vegas.Project.AddAudioTrack();

            if (created == null)
            {
                throw new InvalidOperationException("VEGAS returned no track for '" + name + "'.");
            }

            created.Name = name;
            result.TracksCreated++;
            log?.Invoke("  Created " + (video ? "video" : "audio") + " track '" + name + "'.");
            return created;
        }

        private static List<LiveRef> ResolveLiveEvents(
            DeviceGroup group,
            Dictionary<long, TrackEvent> liveEvents,
            DeviceApplyResult result,
            Action<string> log)
        {
            var live = new List<LiveRef>();
            var seen = new HashSet<long>();
            int missing = 0;

            if (group.Events != null)
            {
                foreach (DeviceEventRef eventRef in group.Events)
                {
                    if (eventRef == null) continue;
                    if (!seen.Add(eventRef.VegasEventId)) continue;

                    TrackEvent ev;
                    if (!liveEvents.TryGetValue(eventRef.VegasEventId, out ev)
                        || ev == null
                        || !SafeIsValid(ev))
                    {
                        missing++;
                        continue;
                    }

                    live.Add(new LiveRef { Ref = eventRef, Event = ev });
                }
            }

            if (missing > 0)
            {
                AddWarning(result, log,
                    $"Device '{SafeTrackName(group.Name)}': {missing} event(s) no longer exist and were skipped.");
            }

            return live;
        }

        private static void MoveLiveEvents(
            List<LiveRef> live,
            Track videoTrack,
            Track audioTrack,
            DeviceApplyResult result,
            Action<string> log)
        {
            foreach (LiveRef item in live)
            {
                Track target = item.Ref.IsVideoEvent ? videoTrack : audioTrack;
                if (target == null) continue;

                try
                {
                    Track original = item.Event.Track;
                    int originalIndex = original != null ? original.Index : -1;

                    if (originalIndex != target.Index)
                    {
                        item.Event.Track = target;
                    }

                    Track actual = item.Event.Track;
                    if (actual == null || actual.Index != target.Index)
                    {
                        AddWarning(result, log,
                            $"Event {item.Event.EventID} remained on track "
                            + (actual != null ? (actual.Index + 1).ToString(CultureInfo.InvariantCulture) : "?")
                            + " instead of '" + target.Name + "'.");
                        continue;
                    }

                    if (originalIndex != target.Index)
                    {
                        result.EventsMoved++;
                    }
                }
                catch (Exception ex)
                {
                    AddWarning(result, log,
                        $"Event {item.Event.EventID} could not move to '" + target.Name + "': " + ex.Message);
                }
            }
        }

        private static void VerifyGroupedTracks(
            List<LiveRef> live,
            Track videoTrack,
            Track audioTrack,
            DeviceApplyResult result,
            Action<string> log)
        {
            foreach (LiveRef item in live)
            {
                if (!item.Ref.IsGrouped) continue;

                Track expected = item.Ref.IsVideoEvent ? videoTrack : audioTrack;
                if (expected == null) continue;

                try
                {
                    Track actual = item.Event.Track;
                    if (actual != null && actual.Index == expected.Index) continue;

                    // A grouped companion can be dragged by the host when its partner
                    // moves. Put it back on its own track once so audio cannot be
                    // stranded on the video track.
                    try { item.Event.Track = expected; } catch { }
                    actual = item.Event.Track;
                    if (actual == null || actual.Index != expected.Index)
                    {
                        AddWarning(result, log,
                            $"Grouped event {item.Event.EventID} ended on '"
                            + (actual != null ? actual.Name : "?")
                            + "' instead of '" + expected.Name
                            + "'; review the group.");
                    }
                }
                catch (Exception ex)
                {
                    AddWarning(result, log,
                        $"Could not verify the final track of grouped event {item.Event.EventID}: " + ex.Message);
                }
            }
        }

        // =====================================================================
        // ffprobe discovery / execution
        // =====================================================================

        public static string FindFfprobe(string ffmpegPathOrNull)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(ffmpegPathOrNull))
                {
                    string supplied = ffmpegPathOrNull.Trim().Trim('"');
                    string directory = null;
                    try
                    {
                        if (File.Exists(supplied)) directory = Path.GetDirectoryName(supplied);
                        else if (Directory.Exists(supplied)) directory = supplied;
                    }
                    catch { directory = null; }

                    if (!string.IsNullOrEmpty(directory))
                    {
                        string nextToFfmpeg = Path.Combine(directory, "ffprobe.exe");
                        if (File.Exists(nextToFfmpeg)) return nextToFfmpeg;
                    }
                }

                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (string entry in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(entry)) continue;
                    string candidate;
                    try { candidate = Path.Combine(entry.Trim().Trim('"'), "ffprobe.exe"); }
                    catch { continue; }
                    if (File.Exists(candidate)) return candidate;
                }

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    string wingetRoot = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
                    string found = SearchForFileBounded(wingetRoot, "ffprobe.exe", 6, 2000);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
            }
            catch
            {
                // Discovery failures fall through to null; Scan then uses heuristics.
            }

            return null;
        }

        private static string SearchForFileBounded(string root, string fileName, int maxDepth, int maxDirectories)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;

            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(root, 0));
            int visited = 0;

            while (queue.Count > 0 && visited < maxDirectories)
            {
                KeyValuePair<string, int> current = queue.Dequeue();
                visited++;

                string candidate;
                try { candidate = Path.Combine(current.Key, fileName); }
                catch { continue; }

                if (File.Exists(candidate)) return candidate;

                if (current.Value >= maxDepth) continue;

                string[] childDirectories;
                try { childDirectories = Directory.GetDirectories(current.Key); }
                catch { continue; }

                foreach (string child in childDirectories)
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(child);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch
                    {
                        continue;
                    }

                    queue.Enqueue(new KeyValuePair<string, int>(child, current.Value + 1));
                }
            }

            return null;
        }

        private static ProbeInfo ProbeMedia(string ffprobePath, string mediaPath, out string error)
        {
            var info = new ProbeInfo();
            error = null;

            var startInfo = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = "-v error -show_entries stream=codec_type -show_entries format_tags "
                    + "-of default=noprint_wrappers=1 \""
                    + mediaPath.Replace("\"", "\\\"") + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            try
            {
                using (var process = new Process { StartInfo = startInfo })
                {
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();

                    process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs args)
                    {
                        if (args.Data != null) stdout.AppendLine(args.Data);
                    };
                    process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs args)
                    {
                        if (args.Data != null) stderr.AppendLine(args.Data);
                    };

                    if (!process.Start())
                    {
                        error = "ffprobe did not start";
                        return info;
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(ProbeTimeoutMilliseconds))
                    {
                        try { process.Kill(); } catch { }
                        error = "ffprobe timed out";
                        return info;
                    }

                    // Flush the asynchronous output handlers before parsing.
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        error = stderr.Length > 0
                            ? stderr.ToString().Trim()
                            : "ffprobe exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture);
                    }

                    ParseProbeOutput(stdout.ToString(), info);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return info;
        }

        private static void ParseProbeOutput(string output, ProbeInfo info)
        {
            if (string.IsNullOrEmpty(output) || info == null) return;

            string genericMake = null;
            string genericModel = null;
            string appleMake = null;
            string appleModel = null;

            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;

                string key = line.Substring(0, separator).Trim().ToLowerInvariant();
                string value = line.Substring(separator + 1).Trim();
                if (value.Length == 0) continue;

                if (key == "codec_type")
                {
                    if (string.Equals(value, "video", StringComparison.OrdinalIgnoreCase)) info.HasVideo = true;
                    else if (string.Equals(value, "audio", StringComparison.OrdinalIgnoreCase)) info.HasAudio = true;
                }
                else if (key == "tag:make")
                {
                    if (string.IsNullOrEmpty(genericMake)) genericMake = value;
                }
                else if (key == "tag:model")
                {
                    if (string.IsNullOrEmpty(genericModel)) genericModel = value;
                }
                else if (key == "tag:com.apple.quicktime.make")
                {
                    if (string.IsNullOrEmpty(appleMake)) appleMake = value;
                }
                else if (key == "tag:com.apple.quicktime.model")
                {
                    if (string.IsNullOrEmpty(appleModel)) appleModel = value;
                }
            }

            info.Make = !string.IsNullOrEmpty(genericMake) ? genericMake : appleMake;
            info.Model = !string.IsNullOrEmpty(genericModel) ? genericModel : appleModel;
        }

        // =====================================================================
        // Shared helpers
        // =====================================================================

        private static string TryGetProjectDirectory(Vegas vegas)
        {
            try
            {
                if (!string.IsNullOrEmpty(vegas.Project.FilePath))
                {
                    return Path.GetDirectoryName(vegas.Project.FilePath);
                }
            }
            catch
            {
                // A new unsaved project has no directory.
            }
            return null;
        }

        private static string ResolveTakeFilePath(Take take, string projectDir)
        {
            if (take == null) return null;

            try
            {
                if (take.Media != null && !string.IsNullOrEmpty(take.Media.FilePath))
                {
                    string path = CleanPath(take.Media.FilePath);
                    if (File.Exists(path)) return path;
                    if (!string.IsNullOrEmpty(projectDir))
                    {
                        string combined = Path.Combine(projectDir, path);
                        if (File.Exists(combined)) return combined;
                    }
                }
            }
            catch { }

            try
            {
                if (!string.IsNullOrEmpty(take.MediaPath))
                {
                    string path = CleanPath(take.MediaPath);
                    if (File.Exists(path)) return path;
                    if (!string.IsNullOrEmpty(projectDir))
                    {
                        string combined = Path.Combine(projectDir, path);
                        if (File.Exists(combined)) return combined;
                    }
                }
            }
            catch { }

            try
            {
                if (take.Media != null && !string.IsNullOrEmpty(take.Media.KeyString))
                {
                    string path = CleanPath(take.Media.KeyString);
                    if (File.Exists(path)) return path;
                }
            }
            catch { }

            return null;
        }

        private static string CleanPath(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string trimmed = raw.Trim('\"', '\'', ' ', '\t', '\r', '\n');
            int pipeIndex = trimmed.IndexOf('|');
            if (pipeIndex > 0) trimmed = trimmed.Substring(0, pipeIndex);
            return trimmed;
        }

        private static string NormalizePathKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }


        private static bool SafeIsVideo(Track track)
        {
            try { return track != null && track.IsVideo(); }
            catch { return false; }
        }

        private static bool SafeIsAudio(Track track)
        {
            try { return track != null && track.IsAudio(); }
            catch { return false; }
        }

        private static bool SafeIsValid(Track track)
        {
            try { return track != null && track.IsValid(); }
            catch { return false; }
        }

        private static bool SafeIsValid(TrackEvent ev)
        {
            try { return ev != null && ev.IsValid(); }
            catch { return false; }
        }

        private static double SafePlaybackRate(TrackEvent ev)
        {
            try { return ev.PlaybackRate; }
            catch { return 1.0; }
        }

        private static double SanitizePlaybackRate(double rate)
        {
            if (!IsFinite(rate) || rate <= 0.0001 || rate > 100.0) return 1.0;
            return rate;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static string CleanValue(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value.Trim();
        }

        private static string SafeTrackName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Unknown device";

            // Track names must stay single-line and readable: control characters
            // become spaces, runs of spaces collapse, and edges are trimmed.
            var builder = new StringBuilder(name.Length);
            bool previousWasSpace = false;
            foreach (char c in name.Trim())
            {
                char value = char.IsControl(c) ? ' ' : c;
                if (value == ' ')
                {
                    if (previousWasSpace) continue;
                    previousWasSpace = true;
                }
                else
                {
                    previousWasSpace = false;
                }
                builder.Append(value);
            }

            string cleaned = builder.ToString().Trim();
            return cleaned.Length > 0 ? cleaned : "Unknown device";
        }

        private static void AddWarning(DeviceScanResult result, Action<string> log, string message)
        {
            if (result != null) result.Warnings.Add(message);
            log?.Invoke("WARNING: " + message);
        }

        private static void AddWarning(DeviceApplyResult result, Action<string> log, string message)
        {
            if (result != null) result.Warnings.Add(message);
            log?.Invoke("WARNING: " + message);
        }
    }
}
