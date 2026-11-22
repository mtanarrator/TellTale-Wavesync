using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ScriptPortal.Vegas;
using VegasAudioSync.Models;

namespace VegasAudioSync.Services
{
    public class TimelineExtractor
    {
        /// <summary>
        /// Builds the synchronization request from the current VEGAS timeline.
        /// </summary>
        /// <param name="includeVideoEvents">
        /// When true, events on video tracks are analyzed too so multicam camera
        /// clips can be aligned using their embedded scratch audio.
        /// </param>
        public static SyncTaskRequest ExtractFromVegas(
            Vegas vegas,
            bool selectedOnly = false,
            bool includeVideoEvents = true,
            Action<string> logCallback = null,
            string masterTrackId = null)
        {
            if (vegas == null || vegas.Project == null)
                throw new InvalidOperationException("No active VEGAS project loaded.");

            string projectDir = string.IsNullOrEmpty(vegas.Project.FilePath)
                ? null
                : Path.GetDirectoryName(vegas.Project.FilePath);

            var request = new SyncTaskRequest
            {
                ProjectSampleRate = (uint)vegas.Project.Audio.SampleRate,
                Tracks = new List<TrackDescriptor>(),
                Groups = new List<EventGroupDescriptor>()
            };

            logCallback?.Invoke($"Inspecting {vegas.Project.Tracks.Count} project tracks (selectedOnly={selectedOnly}, includeVideoEvents={includeVideoEvents})...");

            int totalSelectedEventsAcrossProject = 0;
            bool masterHasSelectedEvent = false;
            if (selectedOnly)
            {
                foreach (Track t in vegas.Project.Tracks)
                {
                    if (!CanProvideAudio(t, includeVideoEvents)) continue;
                    string tid = $"Track_{t.Index}";
                    foreach (TrackEvent ev in t.Events)
                    {
                        if (ev.Selected)
                        {
                            totalSelectedEventsAcrossProject++;
                            if (tid == masterTrackId) masterHasSelectedEvent = true;
                        }
                    }
                }
                logCallback?.Invoke($"Selection filter active: found {totalSelectedEventsAcrossProject} selected event(s) across project.");
            }

            // === Stable event index and group planning ===============================
            // Our sync ids are Track.Index based; build them once over every event so
            // group descriptors and the applier always agree.
            var eventByVegasId = new Dictionary<long, TrackEvent>();
            var trackByVegasId = new Dictionary<long, Track>();
            var syncIdByVegasId = new Dictionary<long, string>();

            foreach (Track t in vegas.Project.Tracks)
            {
                uint idx = 0;
                foreach (TrackEvent ev in t.Events)
                {
                    eventByVegasId[ev.EventID] = ev;
                    trackByVegasId[ev.EventID] = t;
                    syncIdByVegasId[ev.EventID] = $"Event_{t.Index}_{idx}";
                    idx++;
                }
            }

            List<GroupPlan> allGroups = BuildGroupPlans(
                includeVideoEvents,
                eventByVegasId,
                trackByVegasId,
                syncIdByVegasId,
                projectDir,
                logCallback);

            var groupByMemberVegasId = new Dictionary<long, GroupPlan>();
            var includedGroupIds = new HashSet<string>(StringComparer.Ordinal);
            var includedPrimaryTrackIndexes = new HashSet<int>();

            foreach (var gp in allGroups)
            {
                bool included = !selectedOnly
                    || gp.Members.Any(m => m.Selected || (m.Track != null && m.Track.Selected))
                    || (!string.IsNullOrEmpty(masterTrackId)
                        && string.Equals(gp.PrimaryTrackId, masterTrackId, StringComparison.Ordinal)
                        && !masterHasSelectedEvent);

                foreach (var m in gp.Members)
                {
                    groupByMemberVegasId[m.VegasEventId] = gp;
                }

                if (included)
                {
                    includedGroupIds.Add(gp.GroupId);
                    includedPrimaryTrackIndexes.Add(gp.PrimaryTrack.Index);
                }
            }

            if (allGroups.Count > 0)
            {
                logCallback?.Invoke(
                    $"Planned {allGroups.Count} grouped event set(s); {includedGroupIds.Count} in scope. " +
                    "Only the lowest-index audio-capable primary per group will be analyzed.");
            }

            int skippedWithoutMedia = 0;
            int skippedGroupCompanions = 0;
            var addedGroupIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (Track track in vegas.Project.Tracks)
            {
                // Audio tracks always participate. Video tracks participate so that the
                // audio embedded in camera clips can be correlated directly.
                if (!CanProvideAudio(track, includeVideoEvents))
                    continue;

                string currentTrackId = $"Track_{track.Index}";
                bool includeWholeMasterFallback = selectedOnly
                    && !string.IsNullOrEmpty(masterTrackId)
                    && currentTrackId == masterTrackId
                    && !masterHasSelectedEvent;

                bool trackHasIncludedPrimary = includedPrimaryTrackIndexes.Contains(track.Index);

                bool trackHasSelectedEvents = false;
                if (selectedOnly && !includeWholeMasterFallback)
                {
                    foreach (TrackEvent ev in track.Events)
                    {
                        if (ev.Selected)
                        {
                            trackHasSelectedEvents = true;
                            break;
                        }
                    }

                    // Only fall back to track.Selected when no individual events are selected.
                    if (!trackHasSelectedEvents
                        && !(totalSelectedEventsAcrossProject == 0 && track.Selected)
                        && !trackHasIncludedPrimary)
                        continue;
                }

                string trackName = string.IsNullOrEmpty(track.Name) ? $"Track {track.Index + 1}" : track.Name;
                string trackKind = track.IsAudio() ? "audio" : "video";

                var trackDesc = new TrackDescriptor
                {
                    TrackId = currentTrackId,
                    Name = trackName,
                    IsAudio = track.IsAudio(),
                    Events = new List<EventDescriptor>()
                };

                uint eventIndex = 0;
                foreach (TrackEvent ev in track.Events)
                {
                    // IMPORTANT: eventIndex must advance for every event on the track so
                    // that the ids used later by TimelineApplier stay stable.
                    string syncId = $"Event_{track.Index}_{eventIndex}";
                    eventIndex++;

                    bool allowBySelection = !selectedOnly
                        || includeWholeMasterFallback
                        || ev.Selected
                        || (totalSelectedEventsAcrossProject == 0 && track.Selected);

                    GroupPlan group;
                    bool isGroupMember = groupByMemberVegasId.TryGetValue(ev.EventID, out group);
                    bool isGroupPrimary = isGroupMember && group.PrimaryVegasId == ev.EventID;
                    bool isGroupCompanion = isGroupMember && !isGroupPrimary;

                    if (isGroupCompanion)
                    {
                        // The group is moved atomically by TimelineApplier; companions are
                        // never decoded or correlated a second time.
                        skippedGroupCompanions++;
                        continue;
                    }

                    bool allow = isGroupPrimary
                        ? includedGroupIds.Contains(group.GroupId)
                        : allowBySelection;

                    if (!allow)
                        continue;

                    Take activeTake = null;
                    try
                    {
                        activeTake = ev.ActiveTake;
                    }
                    catch
                    {
                        activeTake = null;
                    }

                    if (activeTake == null)
                    {
                        logCallback?.Invoke($"  [Track {track.Index + 1}] {syncId}: No active take, skipped.");
                        continue;
                    }

                    string resolvedPath = ResolveTakeFilePath(activeTake, projectDir);

                    if (string.IsNullOrEmpty(resolvedPath))
                    {
                        // Generators (titles, colour solids, ...) have no media file.
                        skippedWithoutMedia++;
                        continue;
                    }

                    if (!File.Exists(resolvedPath))
                    {
                        logCallback?.Invoke($"  [Track {track.Index + 1}] Event '{activeTake.Name}': File not found at '{resolvedPath}'.");
                        continue;
                    }

                    double startSec = ev.Start.ToMilliseconds() / 1000.0;
                    double lenSec = ev.Length.ToMilliseconds() / 1000.0;
                    double takeOffsetSec = activeTake.Offset.ToMilliseconds() / 1000.0;
                    double mediaLengthSec = ResolveAvailableMediaSeconds(ev, activeTake);

                    string sourceKey = null;
                    try
                    {
                        sourceKey = Path.GetDirectoryName(resolvedPath);
                        if (!string.IsNullOrEmpty(sourceKey))
                        {
                            sourceKey = sourceKey.TrimEnd('\\').ToLowerInvariant();
                        }
                    }
                    catch
                    {
                        sourceKey = null;
                    }

                    double? recordedTime = null;
                    try
                    {
                        DateTime recorded = activeTake.Media.RecordedDateTime;
                        if (recorded.Year > 1900)
                        {
                            recordedTime = (recorded.ToUniversalTime()
                                - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                        }
                    }
                    catch
                    {
                        recordedTime = null;
                    }

                    var eventDesc = new EventDescriptor
                    {
                        EventId = syncId,
                        VegasEventId = ev.EventID,
                        TrackId = trackDesc.TrackId,
                        FilePath = resolvedPath,
                        StreamIndex = (uint)Math.Max(0, activeTake.StreamIndex),
                        TimelineStartSeconds = startSec,
                        TimelineLengthSeconds = lenSec,
                        MediaStartSeconds = takeOffsetSec,
                        MediaLengthSeconds = mediaLengthSec,
                        IsMuted = ev.Mute,
                        SourceKey = sourceKey,
                        RecordedTimeSeconds = recordedTime
                    };

                    trackDesc.Events.Add(eventDesc);

                    if (isGroupPrimary && !addedGroupIds.Contains(group.GroupId))
                    {
                        AddGroupDescriptor(request, group);
                        addedGroupIds.Add(group.GroupId);
                        logCallback?.Invoke(
                            $"  Group '{group.GroupId}' ({group.Kind}): primary {group.PrimarySyncId} on {group.PrimaryTrackId}; " +
                            $"{group.Members.Count} member(s) will move atomically.");
                    }

                    logCallback?.Invoke(
                        $"  [{trackKind} track {track.Index + 1}] Added '{activeTake.Name}' ({Path.GetFileName(resolvedPath)}) " +
                        $"at {startSec:F2}s (on timeline {lenSec:F2}s, media offset {takeOffsetSec:F2}s, media {mediaLengthSec:F2}s)");
                }

                if (trackDesc.Events.Count > 0)
                {
                    if (includeWholeMasterFallback)
                    {
                        logCallback?.Invoke($"Included master track '{trackName}' as reference anchor for selected events.");
                    }
                    request.Tracks.Add(trackDesc);
                }
            }

            if (skippedWithoutMedia > 0)
            {
                logCallback?.Invoke($"Skipped {skippedWithoutMedia} generator/still event(s) with no media file.");
            }
            if (skippedGroupCompanions > 0)
            {
                logCallback?.Invoke($"Skipped {skippedGroupCompanions} grouped companion event(s); only the primary reference per group was analyzed.");
            }

            logCallback?.Invoke($"Extraction complete. {request.Tracks.Count} track(s), {request.Groups.Count} group(s) queued.");
            return request;
        }

        private sealed class GroupMemberPlan
        {
            public long VegasEventId;
            public string SyncId;
            public Track Track;
            public TrackEvent Event;
            public bool IsPrimary;
            public bool IsVideo;
            public bool Selected;
            public double TimelineStartSeconds;
            public double TimelineLengthSeconds;
            public double MediaStartSeconds;
            public uint StreamIndex;
        }

        private sealed class GroupPlan
        {
            public string GroupId;
            public string Kind;
            public long PrimaryVegasId;
            public string PrimarySyncId;
            public string PrimaryTrackId;
            public Track PrimaryTrack;
            public List<GroupMemberPlan> Members = new List<GroupMemberPlan>();
        }

        private static List<GroupPlan> BuildGroupPlans(
            bool includeVideoEvents,
            Dictionary<long, TrackEvent> eventByVegasId,
            Dictionary<long, Track> trackByVegasId,
            Dictionary<long, string> syncIdByVegasId,
            string projectDir,
            Action<string> logCallback)
        {
            var plans = new List<GroupPlan>();
            var seenGroupIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kv in eventByVegasId)
            {
                TrackEvent rootEvent = kv.Value;
                if (!rootEvent.IsGrouped || rootEvent.Group == null)
                    continue;

                var members = new List<GroupMemberPlan>();
                var ids = new List<long>();
                foreach (TrackEvent member in rootEvent.Group)
                {
                    ids.Add(member.EventID);
                }
                ids.Sort();
                string groupId = string.Join(",", ids);
                if (!seenGroupIds.Add(groupId))
                    continue;

                bool hasVideo = false;
                bool hasAudio = false;

                foreach (long id in ids)
                {
                    TrackEvent member;
                    if (!eventByVegasId.TryGetValue(id, out member))
                        continue;

                    Track track;
                    if (!trackByVegasId.TryGetValue(id, out track))
                    {
                        try { track = member.Track; } catch { track = null; }
                    }
                    if (track == null)
                        continue;

                    Take take = null;
                    try { take = member.ActiveTake; } catch { take = null; }

                    double start = 0.0;
                    double length = 0.0;
                    double mediaStart = 0.0;
                    uint streamIndex = 0;
                    try
                    {
                        start = member.Start.ToMilliseconds() / 1000.0;
                        length = member.Length.ToMilliseconds() / 1000.0;
                        if (take != null)
                        {
                            mediaStart = take.Offset.ToMilliseconds() / 1000.0;
                            streamIndex = (uint)Math.Max(0, take.StreamIndex);
                        }
                    }
                    catch
                    {
                        // Invalid event; the applier will skip the group via validation.
                    }

                    bool isVideo = track.IsVideo();
                    if (isVideo) hasVideo = true;
                    if (track.IsAudio()) hasAudio = true;

                    members.Add(new GroupMemberPlan
                    {
                        VegasEventId = id,
                        SyncId = syncIdByVegasId.TryGetValue(id, out var sid) ? sid : null,
                        Track = track,
                        Event = member,
                        IsVideo = isVideo,
                        Selected = member.Selected || track.Selected,
                        TimelineStartSeconds = start,
                        TimelineLengthSeconds = length,
                        MediaStartSeconds = mediaStart,
                        StreamIndex = streamIndex
                    });
                }

                if (members.Count == 0)
                    continue;

                // Primary: top-most (lowest Track.Index) audio-capable member.
                // Video is audio-capable only when the operator enabled video analysis.
                GroupMemberPlan primary = members
                    .Where(m => m.Track.IsAudio() || (includeVideoEvents && m.Track.IsVideo()))
                    .OrderBy(m => m.Track.Index)
                    .ThenBy(m => m.StreamIndex)
                    .ThenBy(m => m.VegasEventId)
                    .FirstOrDefault();

                if (primary == null)
                {
                    logCallback?.Invoke($"  Group {groupId}: no audio-capable member; skipped.");
                    continue;
                }

                foreach (var m in members)
                {
                    m.IsPrimary = m.VegasEventId == primary.VegasEventId;
                }

                plans.Add(new GroupPlan
                {
                    GroupId = groupId,
                    Kind = hasVideo && hasAudio ? "AV" : (hasAudio ? "MultiChannel" : "Video"),
                    PrimaryVegasId = primary.VegasEventId,
                    PrimarySyncId = primary.SyncId,
                    PrimaryTrackId = $"Track_{primary.Track.Index}",
                    PrimaryTrack = primary.Track,
                    Members = members
                });
            }

            return plans;
        }

        private static void AddGroupDescriptor(SyncTaskRequest request, GroupPlan group)
        {
            var descriptor = new EventGroupDescriptor
            {
                GroupId = group.GroupId,
                Kind = group.Kind,
                PrimaryEventId = group.PrimarySyncId,
                Members = new List<EventGroupMember>()
            };

            foreach (var member in group.Members)
            {
                descriptor.Members.Add(new EventGroupMember
                {
                    EventId = member.SyncId,
                    VegasEventId = member.VegasEventId,
                    TrackId = $"Track_{member.Track.Index}",
                    TimelineStartSeconds = member.TimelineStartSeconds,
                    TimelineLengthSeconds = member.TimelineLengthSeconds,
                    MediaStartSeconds = member.MediaStartSeconds,
                    StreamIndex = member.StreamIndex,
                    IsVideo = member.IsVideo
                });
            }

            request.Groups.Add(descriptor);
        }

        private static bool CanProvideAudio(Track track, bool includeVideoEvents)
        {
            if (track == null) return false;
            if (track.IsAudio()) return true;
            return includeVideoEvents && track.IsVideo();
        }

        /// <summary>
        /// Returns how much source media is available starting at the take offset.
        /// For a two hour recorder take trimmed to two minutes on the timeline this
        /// reports the full remaining take, which lets the engine anchor short
        /// camera clips anywhere inside it.
        /// </summary>
        private static double ResolveAvailableMediaSeconds(TrackEvent ev, Take take)
        {
            double timelineMediaSec = 0.0;
            try
            {
                double rate = ev.PlaybackRate;
                if (rate <= 0.0001) rate = 1.0;
                timelineMediaSec = (ev.Length.ToMilliseconds() / 1000.0) * rate;
            }
            catch
            {
                timelineMediaSec = 0.0;
            }

            try
            {
                double available = take.AvailableLength.ToMilliseconds() / 1000.0;
                if (available > 0.01) return available;
            }
            catch
            {
                // Some synthetic/generated takes do not expose AvailableLength.
            }

            try
            {
                if (take.Media != null)
                {
                    double total = take.Media.Length.ToMilliseconds() / 1000.0;
                    double offset = take.Offset.ToMilliseconds() / 1000.0;
                    double remaining = total - offset;
                    if (remaining > 0.01) return remaining;
                }
            }
            catch
            {
                // Ignore and fall through to the timeline length.
            }

            return timelineMediaSec;
        }

        private static string ResolveTakeFilePath(Take take, string projectDir)
        {
            if (take == null) return null;

            // 1. Try take.Media.FilePath (the most reliable canonical path in VEGAS Pro)
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

            // 2. Try take.MediaPath
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

            // 3. Try take.Media.KeyString
            if (take.Media != null && !string.IsNullOrEmpty(take.Media.KeyString))
            {
                string path = CleanPath(take.Media.KeyString);
                if (File.Exists(path)) return path;
            }

            return null;
        }

        private static string CleanPath(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string trimmed = raw.Trim('\"', '\'', ' ', '\t', '\r', '\n');
            // Remove any trailing stream designators like "|0"
            int pipeIdx = trimmed.IndexOf('|');
            if (pipeIdx > 0)
            {
                trimmed = trimmed.Substring(0, pipeIdx);
            }
            return trimmed;
        }
    }
}
