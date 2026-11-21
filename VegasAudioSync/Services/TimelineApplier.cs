using System;
using System.Collections.Generic;
using ScriptPortal.Vegas;
using VegasAudioSync.Models;

namespace VegasAudioSync.Services
{
    public class TimelineApplier
    {
        /// <summary>
        /// Applies a synchronization manifest to the timeline.
        /// </summary>
        /// <param name="preventCrossfades">
        /// When true, any track that received synchronized events is checked for
        /// overlapping events; overlapping items are spread onto additional tracks of
        /// the same media type (video lanes for video events, audio lanes for audio
        /// events) so VEGAS never inserts unintended crossfades.
        /// </param>
        public static int ApplyManifest(
            Vegas vegas,
            SyncManifest manifest,
            bool preventCrossfades = true,
            Action<string> logCallback = null)
        {
            if (vegas == null || vegas.Project == null)
                throw new InvalidOperationException("No active VEGAS project loaded.");

            if (manifest == null || manifest.Events == null || manifest.Events.Count == 0)
                return 0;

            var resultMap = new Dictionary<string, EventSyncResult>();
            foreach (var res in manifest.Events)
            {
                resultMap[res.EventId] = res;
            }

            // Snapshot the tracks before we add any lanes: adding tracks mutates the
            // project collection, and all result ids are based on the original indices.
            var trackSnapshot = new List<Track>();
            foreach (Track track in vegas.Project.Tracks)
            {
                if (track.IsAudio() || track.IsVideo())
                {
                    trackSnapshot.Add(track);
                }
            }

            // Pre-compute which VEGAS events were aligned by the engine. This uses the
            // persistent VEGAS EventID so companion lookups never depend on object identity.
            var syncedVegasEventIds = new HashSet<long>();
            foreach (Track track in trackSnapshot)
            {
                uint eventIndex = 0;
                foreach (TrackEvent ev in track.Events)
                {
                    string eventId = $"Event_{track.Index}_{eventIndex}";
                    eventIndex++;

                    EventSyncResult res;
                    if (resultMap.TryGetValue(eventId, out res) && res.IsAligned)
                    {
                        syncedVegasEventIds.Add(ev.EventID);
                    }
                }
            }

            // VEGAS events whose start/length/rate was touched by this run. Only tracks
            // containing these events are considered for crossfade relief, so unrelated
            // tracks and intentional edits elsewhere are left alone.
            var changedVegasEventIds = new HashSet<long>();
            int movedEventsCount = 0;

            using (var undo = new UndoBlock("Synchronize Audio & Video Events (TellTale WaveSync)"))
            {
                foreach (Track track in trackSnapshot)
                {
                    // Snapshot events and their original Event_<track>_<idx> ids before any
                    // Split() mutates track.Events during iteration.
                    var queued = new List<Tuple<TrackEvent, string, EventSyncResult>>();
                    uint eventIndex = 0;
                    foreach (TrackEvent ev in track.Events)
                    {
                        string eventId = $"Event_{track.Index}_{eventIndex}";
                        eventIndex++;

                        EventSyncResult syncResult;
                        if (resultMap.TryGetValue(eventId, out syncResult))
                        {
                            queued.Add(Tuple.Create(ev, eventId, syncResult));
                        }
                    }

                    foreach (var item in queued)
                    {
                        TrackEvent ev = item.Item1;
                        string eventId = item.Item2;
                        EventSyncResult syncResult = item.Item3;

                        if (!syncResult.IsAligned)
                        {
                            logCallback?.Invoke($"  Skipping [{eventId}] (marked unaligned by engine).");
                            continue;
                        }

                        try
                        {
                            bool changed;
                            movedEventsCount += ApplyEventResult(
                                vegas,
                                ev,
                                eventId,
                                syncResult,
                                syncedVegasEventIds,
                                changedVegasEventIds,
                                logCallback,
                                out changed);

                            if (changed)
                            {
                                changedVegasEventIds.Add(ev.EventID);
                            }
                        }
                        catch (Exception ex)
                        {
                            logCallback?.Invoke($"  WARNING: could not move [{eventId}]: {ex.Message}");
                        }
                    }
                }

                if (preventCrossfades && changedVegasEventIds.Count > 0)
                {
                    SeparateOverlappingEvents(vegas, trackSnapshot, changedVegasEventIds, logCallback);
                }

                vegas.UpdateUI();
            }

            return movedEventsCount;
        }

        private static int ApplyEventResult(
            Vegas vegas,
            TrackEvent ev,
            string eventId,
            EventSyncResult syncResult,
            HashSet<long> syncedVegasEventIds,
            HashSet<long> changedVegasEventIds,
            Action<string> logCallback,
            out bool changed)
        {
            changed = false;
            int moved = 0;
            Timecode originalStart = ev.Start;
            Timecode originalLength = ev.Length;

            Timecode newStart = Timecode.FromMilliseconds(syncResult.ResolvedStartSeconds * 1000.0);
            Timecode deltaTc = newStart - originalStart;

            // 1. Apply resolved start position
            if (Math.Abs(deltaTc.ToMilliseconds()) > 1.0)
            {
                ev.AdjustStartLength(newStart, ev.Length, false);
                moved++;
                changed = true;
                logCallback?.Invoke($"  Moved [{eventId}]: {originalStart} -> {newStart} (Shift: {deltaTc})");
            }
            else
            {
                logCallback?.Invoke($"  [{eventId}] already at resolved position ({newStart}).");
            }

            // 2. Apply clock drift playback rate (clamped within safe bounds)
            double safeRate = Math.Max(0.90, Math.Min(1.10, syncResult.PlaybackRate));
            bool rateChanged = Math.Abs(safeRate - 1.0) > 1e-6;
            if (rateChanged)
            {
                ev.PlaybackRate = safeRate;
                changed = true;
            }

            // 3. Move linked companion media that has no synchronization result of its own.
            SynchronizeCompanionEvents(
                vegas,
                ev,
                originalStart,
                originalLength,
                deltaTc,
                safeRate,
                rateChanged,
                syncedVegasEventIds,
                changedVegasEventIds,
                logCallback);

            // 4. Handle discontinuities / splits in descending time order after positioning
            //    so both left and right pieces keep their aligned position and playback rate.
            if (syncResult.SplitPointsSeconds != null && syncResult.SplitPointsSeconds.Count > 0 && ev.Track != null)
            {
                var preSplitIds = new HashSet<long>();
                foreach (TrackEvent existing in ev.Track.Events)
                {
                    preSplitIds.Add(existing.EventID);
                }

                var sortedSplits = new List<double>(syncResult.SplitPointsSeconds);
                sortedSplits.Sort((a, b) => b.CompareTo(a));

                foreach (double splitSec in sortedSplits)
                {
                    Timecode splitTc = Timecode.FromMilliseconds(splitSec * 1000.0) + deltaTc;
                    if (splitTc > ev.Start && splitTc < ev.End)
                    {
                        Timecode splitOffset = splitTc - ev.Start;
                        ev.Split(splitOffset);
                        SplitCompanionVideoEvents(ev, splitTc);
                        changed = true;
                        logCallback?.Invoke($"  Split [{eventId}] at {splitTc}");
                    }
                }

                foreach (TrackEvent postEv in ev.Track.Events)
                {
                    if (!preSplitIds.Contains(postEv.EventID))
                    {
                        changedVegasEventIds.Add(postEv.EventID);
                    }
                }
            }

            return moved;
        }

        private static void SplitCompanionVideoEvents(TrackEvent audioEvent, Timecode globalSplitTc)
        {
            if (audioEvent.IsGrouped && audioEvent.Group != null)
            {
                foreach (TrackEvent comp in audioEvent.Group)
                {
                    if (comp != audioEvent && globalSplitTc > comp.Start && globalSplitTc < comp.End)
                    {
                        comp.Split(globalSplitTc - comp.Start);
                    }
                }
            }
        }

        /// <summary>
        /// Shifts video/audio companions that share the same media but did not receive
        /// their own synchronization result (for example a linked video event whose
        /// audio was analyzed on a separate audio track).
        ///
        /// Companions that *did* get a result are skipped, otherwise they would be
        /// moved twice. Only events overlapping the source event in time are touched,
        /// so re-used media elsewhere on the timeline stays put.
        /// </summary>
        private static void SynchronizeCompanionEvents(
            Vegas vegas,
            TrackEvent sourceEvent,
            Timecode originalStart,
            Timecode originalLength,
            Timecode deltaTc,
            double playbackRate,
            bool rateChanged,
            HashSet<long> syncedVegasEventIds,
            HashSet<long> changedVegasEventIds,
            Action<string> logCallback)
        {
            if (Math.Abs(deltaTc.ToMilliseconds()) < 1.0 && !rateChanged)
            {
                return;
            }

            // A: Already part of a VEGAS event group (e.g. standard A/V take).
            if (sourceEvent.IsGrouped && sourceEvent.Group != null)
            {
                foreach (TrackEvent companion in sourceEvent.Group)
                {
                    if (companion == sourceEvent) continue;
                    if (syncedVegasEventIds.Contains(companion.EventID)) continue;

                    companion.AdjustStartLength(companion.Start + deltaTc, companion.Length, false);
                    if (rateChanged)
                    {
                        companion.PlaybackRate = playbackRate;
                    }
                    changedVegasEventIds.Add(companion.EventID);
                    logCallback?.Invoke("    Shifted grouped companion event.");
                }
                return;
            }

            // B: Same media path, not yet grouped.
            string mediaPath = GetMediaPath(sourceEvent);
            if (string.IsNullOrEmpty(mediaPath))
            {
                return;
            }

            var newlyGrouped = new List<TrackEvent> { sourceEvent };
            Timecode sourceEnd = originalStart + originalLength;

            foreach (Track track in vegas.Project.Tracks)
            {
                if (!track.IsVideo() && !track.IsAudio()) continue;

                foreach (TrackEvent vEv in track.Events)
                {
                    if (vEv.EventID == sourceEvent.EventID) continue;
                    string vPath = GetMediaPath(vEv);
                    if (string.IsNullOrEmpty(vPath) ||
                        !string.Equals(vPath, mediaPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (syncedVegasEventIds.Contains(vEv.EventID))
                    {
                        // This event was aligned on its own; do not double-move it.
                        continue;
                    }

                    // Only companions that actually overlap the source event are linked.
                    bool overlaps = vEv.Start < sourceEnd && vEv.End > originalStart;
                    if (!overlaps) continue;

                    vEv.AdjustStartLength(vEv.Start + deltaTc, vEv.Length, false);
                    if (rateChanged)
                    {
                        vEv.PlaybackRate = playbackRate;
                    }
                    changedVegasEventIds.Add(vEv.EventID);
                    newlyGrouped.Add(vEv);
                    logCallback?.Invoke($"    Linked & shifted companion video on Track {track.Index + 1} by {deltaTc}.");
                }
            }

            if (newlyGrouped.Count > 1)
            {
                try
                {
                    var group = new TrackEventGroup(vegas.Project);
                    foreach (var ev in newlyGrouped)
                    {
                        group.Add(ev);
                    }
                    vegas.Project.Groups.Add(group);
                }
                catch
                {
                    // Ignore if grouping is restricted.
                }
            }
        }

        /// <summary>
        /// Detects overlapping events on every track touched by this sync pass and
        /// spreads them across additional tracks so no two events share the same
        /// time range on one track (i.e. no unintended crossfades).
        ///
        /// New lanes always match the source media type:
        ///   - video tracks for video events (they keep their own embedded audio)
        ///   - audio tracks for audio-only events
        /// </summary>
        private static void SeparateOverlappingEvents(
            Vegas vegas,
            List<Track> trackSnapshot,
            HashSet<long> changedVegasEventIds,
            Action<string> logCallback)
        {
            int lanesCreated = 0;
            int eventsRelocated = 0;

            foreach (Track track in trackSnapshot)
            {
                if (!track.IsValid()) continue;

                // Snapshot the track's events after all position changes.
                var events = new List<TrackEvent>();
                bool affected = false;
                foreach (TrackEvent ev in track.Events)
                {
                    if (!ev.IsValid()) continue;
                    events.Add(ev);
                    if (changedVegasEventIds.Contains(ev.EventID))
                    {
                        affected = true;
                    }
                }

                if (!affected || events.Count < 2)
                    continue;

                // Standard interval partitioning: greedy earliest-available lane.
                // Clips the sync pass did not touch stay pinned to the original track,
                // so deliberate pre-existing edits are preserved and only the events
                // this run moved can be relocated.
                var starts = new List<double>(events.Count);
                var ends = new List<double>(events.Count);
                var pinned = new List<bool>(events.Count);
                foreach (var ev in events)
                {
                    starts.Add(ev.Start.ToMilliseconds());
                    ends.Add(ev.End.ToMilliseconds());
                    pinned.Add(!changedVegasEventIds.Contains(ev.EventID));
                }

                int laneCount;
                var laneByItem = LanePartitioner.Assign(starts, ends, out laneCount, 1.0, pinned);

                if (laneCount <= 1)
                    continue;

                var lanes = new List<List<TrackEvent>>(laneCount);
                for (int i = 0; i < laneCount; i++)
                {
                    lanes.Add(new List<TrackEvent>());
                }

                for (int i = 0; i < events.Count; i++)
                {
                    lanes[laneByItem[i]].Add(events[i]);
                }

                logCallback?.Invoke(
                    $"  Track {track.Index + 1} '{track.Name}' has overlapping events; creating {laneCount - 1} extra {(track.IsVideo() ? "video" : "audio")} lane(s).");

                string baseName = string.IsNullOrEmpty(track.Name)
                    ? (track.IsVideo() ? "Video" : "Audio")
                    : track.Name;

                int lanesForThisTrack = 0;
                for (int laneIndex = 1; laneIndex < laneCount; laneIndex++)
                {
                    if (lanesForThisTrack >= MaxLanesPerTrack)
                    {
                        logCallback?.Invoke($"  WARNING: lane limit reached on '{track.Name}'; some overlaps remain.");
                        break;
                    }

                    // Reuse a lane created by an earlier run when it is still free.
                    Track newTrack = FindReusableLane(vegas, track, baseName, laneIndex + 1, lanes[laneIndex]);
                    if (newTrack == null)
                    {
                        try
                        {
                            newTrack = track.IsVideo()
                                ? (Track)vegas.Project.AddVideoTrack()
                                : (Track)vegas.Project.AddAudioTrack();
                        }
                        catch (Exception ex)
                        {
                            logCallback?.Invoke($"  WARNING: could not create an extra track: {ex.Message}");
                            break;
                        }

                        lanesForThisTrack++;
                        lanesCreated++;
                        TryCopyTrackSettings(track, newTrack, laneIndex + 1);
                    }

                    foreach (var ev in lanes[laneIndex])
                    {
                        try
                        {
                            ev.Track = newTrack;

                            // Verify VEGAS actually accepted the move.
                            if (ev.Track == null || ev.Track.Index != newTrack.Index)
                            {
                                throw new InvalidOperationException("VEGAS rejected the event move to the new lane.");
                            }

                            eventsRelocated++;
                        }
                        catch (Exception ex)
                        {
                            logCallback?.Invoke($"  WARNING: could not move an event to a new lane: {ex.Message}");
                        }
                    }
                }
            }

            if (lanesCreated > 0)
            {
                logCallback?.Invoke(
                    $"Crossfade protection: added {lanesCreated} track(s) and relocated {eventsRelocated} event(s).");
            }
        }

        private const int MaxLanesPerTrack = 64;

        /// <summary>
        /// Looks for a track created by an earlier sync run that can host the given
        /// events without overlapping anything already on it. Reusing lanes keeps
        /// repeated synchronizations from accumulating duplicate tracks.
        /// </summary>
        private static Track FindReusableLane(
            Vegas vegas,
            Track sourceTrack,
            string baseName,
            int laneNumber,
            List<TrackEvent> events)
        {
            string expectedName = $"{baseName} (Sync Lane {laneNumber})";

            foreach (Track candidate in vegas.Project.Tracks)
            {
                if (candidate.Index == sourceTrack.Index) continue;
                if (candidate.IsVideo() != sourceTrack.IsVideo()) continue;
                if (!string.Equals(candidate.Name, expectedName, StringComparison.OrdinalIgnoreCase)) continue;

                bool free = true;
                foreach (TrackEvent existing in candidate.Events)
                {
                    double exStart = existing.Start.ToMilliseconds();
                    double exEnd = existing.End.ToMilliseconds();
                    foreach (var moving in events)
                    {
                        double mvStart = moving.Start.ToMilliseconds();
                        double mvEnd = moving.End.ToMilliseconds();
                        if (mvStart < exEnd - 1.0 && mvEnd > exStart + 1.0)
                        {
                            free = false;
                            break;
                        }
                    }
                    if (!free) break;
                }

                if (free) return candidate;
            }

            return null;
        }

        private static void TryCopyTrackSettings(Track source, Track target, int laneNumber)
        {
            string baseName = string.IsNullOrEmpty(source.Name)
                ? (source.IsVideo() ? "Video" : "Audio")
                : source.Name;

            try { target.Name = $"{baseName} (Sync Lane {laneNumber})"; } catch { }
            try { target.Mute = source.Mute; } catch { }
            try { target.Solo = source.Solo; } catch { }
            try
            {
                var bus = source.BusTrack;
                if (bus != null)
                {
                    target.BusTrack = bus;
                }
            }
            catch
            {
                // Some track types do not expose a bus.
            }

            // Best-effort copy of the source track's effect chain so the new lane
            // sounds/looks the same as the track it was split from.
            try
            {
                foreach (Effect fx in source.Effects)
                {
                    if (fx == null || !fx.IsValid() || fx.PlugIn == null) continue;
                    try
                    {
                        Effect added = target.Effects.AddEffect(fx.PlugIn);
                        if (added == null) continue;

                        try
                        {
                            byte[] state = fx.GetNativeState();
                            if (state != null && state.Length > 0)
                            {
                                added.SetNativeState(state);
                            }
                        }
                        catch
                        {
                            // Parameter state could not be transferred; keep defaults.
                        }

                        try { added.Bypass = fx.Bypass; } catch { }
                    }
                    catch
                    {
                        // Plugin may not allow another instance on this track type.
                    }
                }
            }
            catch
            {
                // Effects collection unavailable; continue without them.
            }

            // Best-effort copy of track automation (volume, pan, opacity, ...) so the
            // new lane follows the same mix as the track it was split from.
            try
            {
                foreach (Envelope sourceEnvelope in source.Envelopes)
                {
                    if (sourceEnvelope == null || !sourceEnvelope.IsValid()) continue;
                    try
                    {
                        Envelope targetEnvelope = target.Envelopes.FindByType(sourceEnvelope.Type);
                        if (targetEnvelope == null) continue;

                        foreach (EnvelopePoint point in sourceEnvelope.Points)
                        {
                            targetEnvelope.Points.Add(new EnvelopePoint(point.X, point.Y, point.Curve));
                        }
                    }
                    catch
                    {
                        // Envelope type not available on the new track.
                    }
                }
            }
            catch
            {
                // Envelopes collection unavailable; continue without automation.
            }
        }

        private static string GetMediaPath(TrackEvent ev)
        {
            if (ev == null || ev.ActiveTake == null) return null;
            string path = ev.ActiveTake.MediaPath;
            if (string.IsNullOrEmpty(path) && ev.ActiveTake.Media != null)
            {
                path = ev.ActiveTake.Media.FilePath;
            }
            return path;
        }
    }
}
