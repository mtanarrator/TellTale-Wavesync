using System;
using System.Collections.Generic;
using System.IO;
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
                Tracks = new List<TrackDescriptor>()
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

            // Track grouped AudioEvent media paths so we do not decode/correlate the
            // grouped VideoEvent a second time (TimelineApplier moves grouped A/V together).
            var groupedAudioKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Track t in vegas.Project.Tracks)
            {
                if (!t.IsAudio()) continue;
                foreach (TrackEvent ev in t.Events)
                {
                    if (!ev.IsGrouped || ev.Group == null) continue;
                    Take take = null;
                    try { take = ev.ActiveTake; } catch { take = null; }
                    string p = ResolveTakeFilePath(take, projectDir);
                    if (!string.IsNullOrEmpty(p))
                    {
                        groupedAudioKeys.Add($"{ev.Group.GetHashCode()}|{p}|{Math.Max(0, take.StreamIndex)}");
                    }
                }
            }

            int skippedWithoutMedia = 0;
            int skippedDuplicateGroupedVideo = 0;

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
                    if (!trackHasSelectedEvents && !(totalSelectedEventsAcrossProject == 0 && track.Selected))
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
                    bool allowBySelection = !selectedOnly
                        || includeWholeMasterFallback
                        || ev.Selected
                        || (totalSelectedEventsAcrossProject == 0 && track.Selected);

                    if (!allowBySelection)
                    {
                        eventIndex++;
                        continue;
                    }

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
                        logCallback?.Invoke($"  [Track {track.Index + 1}] Event {eventIndex}: No active take, skipped.");
                        eventIndex++;
                        continue;
                    }

                    string resolvedPath = ResolveTakeFilePath(activeTake, projectDir);

                    if (string.IsNullOrEmpty(resolvedPath))
                    {
                        // Generators (titles, colour solids, ...) have no media file.
                        skippedWithoutMedia++;
                        eventIndex++;
                        continue;
                    }

                    if (!File.Exists(resolvedPath))
                    {
                        logCallback?.Invoke($"  [Track {track.Index + 1}] Event '{activeTake.Name}': File not found at '{resolvedPath}'.");
                        eventIndex++;
                        continue;
                    }

                    // If this is a video event grouped with an already-extracted audio event
                    // for the same media & stream, skip redundant analysis (TimelineApplier shifts the group).
                    if (track.IsVideo() && ev.IsGrouped && ev.Group != null)
                    {
                        string groupKey = $"{ev.Group.GetHashCode()}|{resolvedPath}|{Math.Max(0, activeTake.StreamIndex)}";
                        bool audioCompanionIncluded = !selectedOnly || includeWholeMasterFallback;
                        if (!audioCompanionIncluded)
                        {
                            foreach (TrackEvent gEv in ev.Group)
                            {
                                if (gEv != ev && gEv.IsAudio() && (gEv.Selected || (totalSelectedEventsAcrossProject == 0 && gEv.Track != null && gEv.Track.Selected)))
                                {
                                    audioCompanionIncluded = true;
                                    break;
                                }
                            }
                        }

                        if (audioCompanionIncluded && groupedAudioKeys.Contains(groupKey))
                        {
                            skippedDuplicateGroupedVideo++;
                            eventIndex++;
                            continue;
                        }
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
                        EventId = $"Event_{track.Index}_{eventIndex}",
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
                    logCallback?.Invoke(
                        $"  [{trackKind} track {track.Index + 1}] Added '{activeTake.Name}' ({Path.GetFileName(resolvedPath)}) " +
                        $"at {startSec:F2}s (on timeline {lenSec:F2}s, media offset {takeOffsetSec:F2}s, media {mediaLengthSec:F2}s)");
                    eventIndex++;
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
            if (skippedDuplicateGroupedVideo > 0)
            {
                logCallback?.Invoke($"Deduplicated {skippedDuplicateGroupedVideo} grouped video event(s) (aligned via their grouped audio event).");
            }

            logCallback?.Invoke($"Extraction complete. {request.Tracks.Count} track(s) with events queued.");
            return request;
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
