using System;
using System.Collections.Generic;
using ScriptPortal.Vegas;
using VegasAudioSync.Models;

namespace VegasAudioSync.Services
{
    public class TimelineApplier
    {
        /// <summary>
        /// Host-gated Upgrade 2 switch. Verified with verify_group_behavior.cs:
        /// VEGAS does NOT auto-move, auto-split, or auto-rate grouped companions.
        /// </summary>
        public static bool EnableAtomicGroupMove = true;

        /// <summary>
        /// True while the applier is responsible for shifting grouped companions
        /// (the existing behavior). Set false if the host verification proves VEGAS
        /// moves grouped members automatically.
        /// </summary>
        public static bool AutoMoveCompanionsInApplier = true;

        /// <summary>
        /// How far an event may have moved since extraction before its result is
        /// rejected. VEGAS timecodes are frame-based, so a few milliseconds of
        /// conversion noise are tolerated; a real edit is far larger.
        /// </summary>
        private const double TimelineEditToleranceSeconds = 0.05;

        /// <summary>Outcome of one apply pass.</summary>
        public sealed class ApplyResult
        {
            /// <summary>Events whose position or rate was changed.</summary>
            public int Moved;

            /// <summary>
            /// Aligned events the applier could not apply because they were removed,
            /// moved, locked, detached, or threw during the move. These must be
            /// surfaced to the operator instead of being reported as success.
            /// </summary>
            public int Skipped;
        }

        /// <summary>
        /// Applies a synchronization manifest to the timeline.
        /// </summary>
        /// <param name="preventCrossfades">
        /// When true, any track that received synchronized events is checked for
        /// overlapping events; overlapping items are spread onto additional tracks of
        /// the same media type (video lanes for video events, audio lanes for audio
        /// events) so VEGAS never inserts unintended crossfades.
        /// </param>
        /// <param name="preserveTrackLayout">
        /// When true, no tracks are added and no event is reassigned to another track;
        /// overlapping results are logged as warnings instead.
        /// </param>
        public static ApplyResult ApplyManifest(
            Vegas vegas,
            SyncManifest manifest,
            SyncTaskRequest request,
            bool preventCrossfades = true,
            bool preserveTrackLayout = false,
            Action<string> logCallback = null)
        {
            if (vegas == null || vegas.Project == null)
                throw new InvalidOperationException("No active VEGAS project loaded.");

            var applyResult = new ApplyResult();
            if (manifest == null || manifest.Events == null || manifest.Events.Count == 0)
                return applyResult;

            var resultMap = new Dictionary<string, EventSyncResult>();
            foreach (var res in manifest.Events)
            {
                resultMap[res.EventId] = res;
            }

            var groups = request != null ? request.Groups : null;

            // Map the engine's stable analysis id back to VEGAS' persistent EventID.
            // This is what makes apply safe if the operator edits the timeline while
            // the analysis is running.
            var vegasIdBySyncId = BuildVegasIdMap(request);

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

            // Defensive fallback for callers that did not supply VEGAS EventIDs.
            if (vegasIdBySyncId.Count == 0)
            {
                foreach (Track track in trackSnapshot)
                {
                    uint idx = 0;
                    foreach (TrackEvent ev in track.Events)
                    {
                        vegasIdBySyncId[$"Event_{track.Index}_{idx}"] = ev.EventID;
                        idx++;
                    }
                }

                if (vegasIdBySyncId.Count > 0)
                {
                    logCallback?.Invoke(
                        "  NOTE: analysis manifest has no VEGAS event IDs; using index mapping (edit detection limited).");
                }
            }

            // Persistent VEGAS EventID -> live TrackEvent. Event IDs survive event
            // reordering, unlike Event_<track>_<idx> strings.
            var eventByVegasId = new Dictionary<long, TrackEvent>();
            var syncIdByVegasId = new Dictionary<long, string>();
            foreach (Track track in trackSnapshot)
            {
                foreach (TrackEvent ev in track.Events)
                {
                    eventByVegasId[ev.EventID] = ev;
                }
            }
            foreach (var pair in vegasIdBySyncId)
            {
                syncIdByVegasId[pair.Value] = pair.Key;
            }

            // Pre-compute which VEGAS events were aligned by the engine. This uses the
            // persistent VEGAS EventID so companion lookups never depend on object identity.
            var syncedVegasEventIds = new HashSet<long>();
            foreach (var res in manifest.Events)
            {
                if (!res.IsAligned) continue;

                long vegasId;
                if (vegasIdBySyncId.TryGetValue(res.EventId, out vegasId)
                    && eventByVegasId.ContainsKey(vegasId))
                {
                    syncedVegasEventIds.Add(vegasId);
                }
            }

            // VEGAS events whose start/length/rate was touched by this run. Only tracks
            // containing these events are considered for crossfade relief, so unrelated
            // tracks and intentional edits elsewhere are left alone.
            var changedVegasEventIds = new HashSet<long>();
            int movedEventsCount = 0;

            using (var undo = new UndoBlock("Synchronize Audio & Video Events (TellTale WaveSync)"))
            {
                var handledGroupEventIds = new HashSet<string>(StringComparer.Ordinal);
                var groupMemberVegasIds = new HashSet<long>();

                if (EnableAtomicGroupMove && groups != null && groups.Count > 0)
                {
                    foreach (var group in groups)
                    {
                        try
                        {
                            movedEventsCount += TryApplyGroup(
                                vegas,
                                group,
                                resultMap,
                                vegasIdBySyncId,
                                eventByVegasId,
                                changedVegasEventIds,
                                handledGroupEventIds,
                                groupMemberVegasIds,
                                logCallback,
                                ref applyResult.Skipped);
                        }
                        catch (Exception ex)
                        {
                            // A failure after the move (for example a split) must not
                            // abort the remaining groups or the per-event pass.
                            applyResult.Skipped += group != null && group.Members != null
                                ? group.Members.Count
                                : 1;
                            logCallback?.Invoke(
                                $"  Group '{group.GroupId}': {ex.Message}; remaining members treated as skipped.");
                        }
                    }
                }

                var visitedSyncIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (Track track in trackSnapshot)
                {
                    // Snapshot live events before any Split() mutates track.Events during iteration.
                    var queued = new List<Tuple<TrackEvent, string, EventSyncResult>>();
                    foreach (TrackEvent ev in track.Events)
                    {
                        string syncId;
                        EventSyncResult syncResult;
                        if (syncIdByVegasId.TryGetValue(ev.EventID, out syncId)
                            && resultMap.TryGetValue(syncId, out syncResult))
                        {
                            queued.Add(Tuple.Create(ev, syncId, syncResult));
                        }
                    }

                    foreach (var item in queued)
                    {
                        TrackEvent ev = item.Item1;
                        string eventId = item.Item2;
                        EventSyncResult syncResult = item.Item3;

                        if (handledGroupEventIds.Contains(eventId))
                            continue;

                        visitedSyncIds.Add(eventId);

                        if (!syncResult.IsAligned)
                        {
                            logCallback?.Invoke($"  Skipping [{eventId}] (marked unaligned by engine).");
                            continue;
                        }

                        TrackEvent resolved;
                        string resolveReason;
                        if (!TryResolveAnalyzedEvent(
                                eventId,
                                syncResult,
                                vegasIdBySyncId,
                                eventByVegasId,
                                out resolved,
                                out resolveReason))
                        {
                            applyResult.Skipped++;
                            logCallback?.Invoke($"  WARNING: skipped [{eventId}]: {resolveReason}.");
                            continue;
                        }

                        try
                        {
                            bool changed;
                            movedEventsCount += ApplyEventResult(
                                vegas,
                                resolved,
                                eventId,
                                syncResult,
                                syncedVegasEventIds,
                                changedVegasEventIds,
                                logCallback,
                                out changed);

                            if (changed)
                            {
                                changedVegasEventIds.Add(resolved.EventID);
                            }
                        }
                        catch (Exception ex)
                        {
                            applyResult.Skipped++;
                            logCallback?.Invoke($"  WARNING: could not move [{eventId}]: {ex.Message}");
                        }
                    }
                }

                // Aligned results whose live event vanished entirely (deleted during
                // analysis) are not visited above; report them as skipped as well.
                foreach (var res in manifest.Events)
                {
                    if (!res.IsAligned) continue;
                    if (handledGroupEventIds.Contains(res.EventId)) continue;
                    if (visitedSyncIds.Contains(res.EventId)) continue;

                    applyResult.Skipped++;
                    logCallback?.Invoke(
                        $"  WARNING: skipped [{res.EventId}]: event no longer exists on the timeline.");
                }

                if (preventCrossfades && !preserveTrackLayout && changedVegasEventIds.Count > 0)
                {
                    SeparateOverlappingEvents(vegas, trackSnapshot, changedVegasEventIds, groupMemberVegasIds, logCallback);
                }
                else if (preserveTrackLayout && changedVegasEventIds.Count > 0)
                {
                    LogOverlappingEvents(trackSnapshot, logCallback);
                }

                vegas.UpdateUI();
            }

            applyResult.Moved = movedEventsCount;
            return applyResult;
        }

        private static Dictionary<string, long> BuildVegasIdMap(SyncTaskRequest request)
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            if (request == null || request.Tracks == null) return map;

            foreach (var track in request.Tracks)
            {
                if (track == null || track.Events == null) continue;
                foreach (var ev in track.Events)
                {
                    if (ev == null || string.IsNullOrEmpty(ev.EventId) || ev.VegasEventId == 0) continue;
                    map[ev.EventId] = ev.VegasEventId;
                }
            }
            return map;
        }

        /// <summary>
        /// Resolves a manifest result to the live VEGAS event with the same persistent
        /// EventID, then verifies the event has not moved since extraction. Either
        /// failure means applying the result would touch the wrong clip.
        /// </summary>
        private static bool TryResolveAnalyzedEvent(
            string syncId,
            EventSyncResult result,
            Dictionary<string, long> vegasIdBySyncId,
            Dictionary<long, TrackEvent> eventByVegasId,
            out TrackEvent ev,
            out string reason)
        {
            ev = null;
            long vegasId;
            if (!vegasIdBySyncId.TryGetValue(syncId, out vegasId))
            {
                reason = "event was not part of the analysis snapshot";
                return false;
            }

            if (!eventByVegasId.TryGetValue(vegasId, out ev)
                || ev == null
                || !ev.IsValid()
                || ev.Track == null)
            {
                ev = null;
                reason = "event was removed or is no longer valid";
                return false;
            }

            double drift = Math.Abs(Seconds(ev.Start) - result.OriginalStartSeconds);
            if (drift > TimelineEditToleranceSeconds)
            {
                ev = null;
                reason = string.Format("event moved since analysis (start differs by {0:F3}s)", drift);
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Moves an entire VEGAS event group by one delta, after validating every
        /// member. Pre-validation is mandatory: UndoBlock commits on normal exit, so
        /// a partial group move must never start.
        /// </summary>
        private static int TryApplyGroup(
            Vegas vegas,
            EventGroupDescriptor group,
            Dictionary<string, EventSyncResult> resultMap,
            Dictionary<string, long> vegasIdBySyncId,
            Dictionary<long, TrackEvent> eventByVegasId,
            HashSet<long> changedVegasEventIds,
            HashSet<string> handledGroupEventIds,
            HashSet<long> groupMemberVegasIds,
            Action<string> logCallback,
            ref int skipped)
        {
            if (group == null || group.Members == null || group.Members.Count == 0)
                return 0;

            // Even a failed group must be excluded from the per-event pass, otherwise
            // a partial move could commit.
            foreach (var member in group.Members)
            {
                handledGroupEventIds.Add(member.EventId);
            }

            EventSyncResult primaryResult;
            if (!resultMap.TryGetValue(group.PrimaryEventId, out primaryResult) || !primaryResult.IsAligned)
            {
                logCallback?.Invoke($"  Group '{group.GroupId}': primary result missing/unaligned; no members moved.");
                return 0;
            }

            var memberEvents = new List<TrackEvent>();
            var states = new List<GroupMemberState>();
            var starts = new List<double>();
            int primaryIndex = -1;

            foreach (var member in group.Members)
            {
                TrackEvent ev;
                long vegasId = member.VegasEventId;
                if (vegasId == 0)
                {
                    // Older descriptors without a VEGAS ID: fall back to the sync-id map.
                    vegasIdBySyncId.TryGetValue(member.EventId, out vegasId);
                }

                if (vegasId == 0
                    || !eventByVegasId.TryGetValue(vegasId, out ev)
                    || ev == null
                    || !ev.IsValid()
                    || ev.Locked
                    || ev.Track == null)
                {
                    skipped += group.Members.Count;
                    logCallback?.Invoke(
                        $"  Group '{group.GroupId}': member '{member.EventId}' is missing, invalid, locked, or detached; no members moved.");
                    return 0;
                }

                // Reject the whole group if any member moved since analysis. The
                // group descriptor carries every member's start, including companions
                // that were never individually analyzed.
                double analyzedStart = member.TimelineStartSeconds;
                EventSyncResult memberResult;
                if (resultMap.TryGetValue(member.EventId, out memberResult))
                {
                    analyzedStart = memberResult.OriginalStartSeconds;
                }

                if (Math.Abs(Seconds(ev.Start) - analyzedStart) > TimelineEditToleranceSeconds)
                {
                    skipped += group.Members.Count;
                    logCallback?.Invoke(
                        $"  Group '{group.GroupId}': member '{member.EventId}' moved since analysis; no members moved.");
                    return 0;
                }

                if (string.Equals(member.EventId, group.PrimaryEventId, StringComparison.Ordinal))
                    primaryIndex = memberEvents.Count;

                memberEvents.Add(ev);
                starts.Add(Seconds(ev.Start));
                states.Add(new GroupMemberState
                {
                    EventId = ev.EventID,
                    StartSeconds = Seconds(ev.Start),
                    IsValid = true,
                    IsLocked = ev.Locked
                });
            }

            if (primaryIndex < 0)
            {
                skipped += group.Members.Count;
                logCallback?.Invoke($"  Group '{group.GroupId}': primary member not found; no members moved.");
                return 0;
            }

            string reason;
            if (!GroupPlanner.ValidateMembers(states, out reason))
            {
                skipped += group.Members.Count;
                logCallback?.Invoke($"  Group '{group.GroupId}': {reason}; no members moved.");
                return 0;
            }

            double delta;
            double[] targets;
            if (!GroupPlanner.TryPlan(starts[primaryIndex], primaryResult.ResolvedStartSeconds, starts, out delta, out targets)
                || !GroupPlanner.PreservesInternalOffsets(starts, targets))
            {
                skipped += group.Members.Count;
                logCallback?.Invoke($"  Group '{group.GroupId}': could not compute a safe rigid move; no members moved.");
                return 0;
            }

            double safeRate = Math.Max(0.90, Math.Min(1.10, primaryResult.PlaybackRate));
            bool rateChanged = Math.Abs(safeRate - 1.0) > 1e-6;

            var originalRates = new List<double>();
            foreach (var ev in memberEvents)
            {
                originalRates.Add(ev.PlaybackRate);
            }

            int moved;
            if (AutoMoveCompanionsInApplier)
            {
                var movedIndexes = new List<int>();
                try
                {
                    for (int i = 0; i < memberEvents.Count; i++)
                    {
                        TrackEvent ev = memberEvents[i];
                        ev.AdjustStartLength(Timecode.FromMilliseconds(targets[i] * 1000.0), ev.Length, false);
                        if (rateChanged) ev.PlaybackRate = safeRate;
                        movedIndexes.Add(i);
                        changedVegasEventIds.Add(ev.EventID);
                        groupMemberVegasIds.Add(ev.EventID);
                    }
                    moved = memberEvents.Count;
                }
                catch (Exception ex)
                {
                    RollBackGroupMembers(
                        memberEvents,
                        movedIndexes,
                        starts,
                        originalRates,
                        rateChanged,
                        changedVegasEventIds,
                        groupMemberVegasIds);
                    skipped += memberEvents.Count;
                    logCallback?.Invoke(
                        $"  Group '{group.GroupId}': move failed ({ex.Message}); all members were rolled back.");
                    return 0;
                }
            }
            else
            {
                TrackEvent primary = memberEvents[primaryIndex];
                try
                {
                    primary.AdjustStartLength(
                        Timecode.FromMilliseconds(targets[primaryIndex] * 1000.0),
                        primary.Length,
                        false);
                    if (rateChanged) primary.PlaybackRate = safeRate;
                    changedVegasEventIds.Add(primary.EventID);
                    moved = 1;
                }
                catch (Exception ex)
                {
                    RollBackGroupMembers(
                        memberEvents,
                        new List<int> { primaryIndex },
                        starts,
                        originalRates,
                        rateChanged,
                        changedVegasEventIds,
                        groupMemberVegasIds);
                    skipped += memberEvents.Count;
                    logCallback?.Invoke(
                        $"  Group '{group.GroupId}': primary move failed ({ex.Message}); rolled back. No companions were moved.");
                    return 0;
                }

                // The host moves companions; pin them so the lane pass cannot split the group.
                foreach (var ev in memberEvents)
                {
                    groupMemberVegasIds.Add(ev.EventID);
                }
            }

            // Apply the primary's split recommendations at the same global timeline
            // points to every member that contains them.
            if (primaryResult.SplitPointsSeconds != null && primaryResult.SplitPointsSeconds.Count > 0)
            {
                var splits = new List<double>(primaryResult.SplitPointsSeconds);
                splits.Sort((a, b) => b.CompareTo(a));

                foreach (double splitSec in splits)
                {
                    double globalSec = splitSec + delta;
                    foreach (TrackEvent ev in memberEvents)
                    {
                        Timecode splitTc = Timecode.FromMilliseconds(globalSec * 1000.0);
                        if (splitTc <= ev.Start || splitTc >= ev.End)
                            continue;

                        var before = new HashSet<long>();
                        foreach (TrackEvent existing in ev.Track.Events)
                            before.Add(existing.EventID);

                        ev.Split(splitTc - ev.Start);

                        foreach (TrackEvent existing in ev.Track.Events)
                        {
                            if (!before.Contains(existing.EventID))
                            {
                                changedVegasEventIds.Add(existing.EventID);
                                groupMemberVegasIds.Add(existing.EventID);
                            }
                        }
                    }
                }
            }

            logCallback?.Invoke(
                $"  Moved group '{group.GroupId}' ({group.Kind}) by {delta:+0.000;-0.000;0.000}s; {moved} member(s) kept their internal offsets.");
            return moved;
        }

        private static double Seconds(Timecode tc)
        {
            return tc.ToMilliseconds() / 1000.0;
        }

        /// <summary>
        /// Ignore Camera Clock Jumps (DST): report overlapping results that were left alone because
        /// track layout must not change.
        /// </summary>
        private static void LogOverlappingEvents(List<Track> trackSnapshot, Action<string> logCallback)
        {
            int overlaps = 0;
            foreach (Track track in trackSnapshot)
            {
                if (!track.IsValid()) continue;
                var events = new List<TrackEvent>();
                foreach (TrackEvent ev in track.Events)
                {
                    if (ev.IsValid()) events.Add(ev);
                }

                for (int i = 0; i < events.Count; i++)
                {
                    for (int j = i + 1; j < events.Count; j++)
                    {
                        double s1 = events[i].Start.ToMilliseconds();
                        double e1 = events[i].End.ToMilliseconds();
                        double s2 = events[j].Start.ToMilliseconds();
                        double e2 = events[j].End.ToMilliseconds();
                        if (s1 < e2 - 1.0 && e1 > s2 + 1.0)
                        {
                            overlaps++;
                        }
                    }
                }
            }

            if (overlaps > 0)
            {
                logCallback?.Invoke(
                    $"Ignore Camera Clock Jumps (DST): {overlaps} overlapping pair(s) left on their tracks (no lanes created). Review these manually.");
            }
        }

        /// <summary>
        /// Best-effort rollback for a group whose move failed partway through.
        /// Restores start and playback rate in reverse order and removes the members
        /// from the changed/pinned sets so the lane pass does not react to them.
        /// The surrounding UndoBlock remains the final safety net.
        /// </summary>
        private static void RollBackGroupMembers(
            List<TrackEvent> memberEvents,
            List<int> movedIndexes,
            List<double> originalStarts,
            List<double> originalRates,
            bool rateChanged,
            HashSet<long> changedVegasEventIds,
            HashSet<long> groupMemberVegasIds)
        {
            for (int k = movedIndexes.Count - 1; k >= 0; k--)
            {
                int i = movedIndexes[k];
                TrackEvent ev = memberEvents[i];
                try
                {
                    ev.AdjustStartLength(
                        Timecode.FromMilliseconds(originalStarts[i] * 1000.0),
                        ev.Length,
                        false);
                    if (rateChanged) ev.PlaybackRate = originalRates[i];
                    changedVegasEventIds.Remove(ev.EventID);
                    groupMemberVegasIds.Remove(ev.EventID);
                }
                catch
                {
                    // Keep restoring the other members; the UndoBlock covers the rest.
                }
            }
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
            if (!AutoMoveCompanionsInApplier)
            {
                // Host verification proved VEGAS moves grouped companions itself.
                return;
            }

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
            HashSet<long> pinnedGroupEventIds,
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
                    pinned.Add(!changedVegasEventIds.Contains(ev.EventID)
                        || (pinnedGroupEventIds != null && pinnedGroupEventIds.Contains(ev.EventID)));
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
