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

        /// <summary>
        /// Grouped A/V lane relocation. DISABLED by default: reassigning the track of a
        /// grouped event is a VEGAS host behavior that cannot be validated outside the
        /// host. Enabling it also requires verifying that moving one member through
        /// <c>TrackEvent.Track</c> does not drag the other members along (which would
        /// double-apply the lane move). See FINDINGS.md #1.
        /// </summary>
        private const bool EnableGroupLaneMove = false;

        /// <summary>Jumps larger than this are treated as a bad measurement and clamped.</summary>
        private const double MaxStepJumpSeconds = 10.0;
        private const double MinLeftPieceSeconds = 0.5;

        /// <summary>
        /// VEGAS snaps splits to frame boundaries, so the right-hand piece can start a
        /// fraction of a frame away from the requested split time.
        /// </summary>
        private const double SplitSnapToleranceSeconds = 0.1;

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

            /// <summary>Extra tracks (sync lanes) created by crossfade protection.</summary>
            public int LanesCreated;
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

            // Loaded once per apply; records which rates WaveSync itself wrote so a
            // manual retime (including one inside the 0.9-1.1 band) is never clobbered.
            var rateHistory = RateHistorySession.Open(vegas);

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

                // Groups that were fully moved by this run, as live event units. Only
                // used by the (disabled) group lane relocation path.
                var movedGroupUnits = new List<List<TrackEvent>>();

                if (EnableAtomicGroupMove && groups != null && groups.Count > 0)
                {
                    foreach (var group in groups)
                    {
                        try
                        {
                            movedEventsCount += TryApplyGroup(
                                vegas,
                                rateHistory,
                                group,
                                resultMap,
                                vegasIdBySyncId,
                                eventByVegasId,
                                changedVegasEventIds,
                                handledGroupEventIds,
                                groupMemberVegasIds,
                                movedGroupUnits,
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
                                rateHistory,
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
                    applyResult.LanesCreated = SeparateOverlappingEvents(
                        vegas,
                        trackSnapshot,
                        changedVegasEventIds,
                        groupMemberVegasIds,
                        movedGroupUnits,
                        logCallback);
                }
                else if (preserveTrackLayout && changedVegasEventIds.Count > 0)
                {
                    LogOverlappingEvents(trackSnapshot, logCallback);
                }

                vegas.UpdateUI();
            }

            applyResult.Moved = movedEventsCount;
            rateHistory.Save();
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
            RateHistorySession rateHistory,
            EventGroupDescriptor group,
            Dictionary<string, EventSyncResult> resultMap,
            Dictionary<string, long> vegasIdBySyncId,
            Dictionary<long, TrackEvent> eventByVegasId,
            HashSet<long> changedVegasEventIds,
            HashSet<string> handledGroupEventIds,
            HashSet<long> groupMemberVegasIds,
            List<List<TrackEvent>> movedGroupUnits,
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

            // Each member keeps its own playback rate and length. A previous run may have
            // stretched a member, so the scale is computed from the member's live rate.
            var originalRates = new List<double>();
            var originalLengths = new List<double>();
            foreach (var ev in memberEvents)
            {
                originalRates.Add(SanitizePlaybackRate(ev.PlaybackRate));
                originalLengths.Add(Seconds(ev.Length));
            }

            // Clamp the *absolute* target, and only for rates inside the drift band.
            // The engine re-measures the source media every run (it cannot see the
            // timeline stretch), so its target is absolute: applying it as a ratio
            // would compound the correction on every run. Rate history decides whether
            // an in-band value is ours to change or a deliberate manual retime.
            double primaryOldRate = originalRates[primaryIndex];
            bool primaryEligible = rateHistory != null
                && rateHistory.IsEligible(
                    memberEvents[primaryIndex].EventID,
                    GetMediaPath(memberEvents[primaryIndex]),
                    primaryOldRate,
                    logCallback);
            double targetRate = primaryEligible
                ? ClampResolvedRate(primaryResult.PlaybackRate)
                : primaryOldRate;

            bool rateChanged = false;
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
                        bool memberEligible = primaryEligible
                            && rateHistory != null
                            && rateHistory.IsEligible(
                                ev.EventID,
                                GetMediaPath(ev),
                                originalRates[i],
                                logCallback);
                        double memberTarget = memberEligible ? targetRate : originalRates[i];
                        if (Math.Abs(memberTarget - originalRates[i]) > 1e-6)
                        {
                            if (ApplyPlaybackRate(ev, originalRates[i], memberTarget, group.GroupId, logCallback))
                            {
                                rateChanged = true;
                                rateHistory.Record(ev.EventID, GetMediaPath(ev), memberTarget);
                            }
                        }
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
                        originalLengths,
                        originalRates,
                        rateChanged,
                        changedVegasEventIds,
                        groupMemberVegasIds,
                        rateHistory);
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
                    if (Math.Abs(targetRate - originalRates[primaryIndex]) > 1e-6)
                    {
                        if (ApplyPlaybackRate(primary, originalRates[primaryIndex], targetRate, group.GroupId, logCallback))
                        {
                            rateChanged = true;
                            rateHistory.Record(primary.EventID, GetMediaPath(primary), targetRate);
                        }
                    }
                    changedVegasEventIds.Add(primary.EventID);
                    moved = 1;
                }
                catch (Exception ex)
                {
                    RollBackGroupMembers(
                        memberEvents,
                        new List<int> { primaryIndex },
                        starts,
                        originalLengths,
                        originalRates,
                        rateChanged,
                        changedVegasEventIds,
                        groupMemberVegasIds,
                        rateHistory);
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

            // Live members of this group after the move, used by the group lane path.
            var unitMembers = new List<TrackEvent>(memberEvents);

            // Apply the primary's split recommendations at the same global timeline
            // points to every member that contains them. The right-hand piece of every
            // member receives the same step-jump shift so internal offsets stay rigid.
            var splitPlan = BuildSplitPlan(primaryResult, logCallback);
            if (splitPlan.Count > 0)
            {
                foreach (var split in splitPlan)
                {
                    double globalSec = split.TimestampSeconds + delta;
                    if (!IsFinite(globalSec))
                        continue;

                    Timecode globalTc = Timecode.FromMilliseconds(globalSec * 1000.0);
                    foreach (TrackEvent ev in memberEvents)
                    {
                        if (ev.Track == null) continue;
                        if (globalTc <= ev.Start || globalTc >= ev.End)
                            continue;

                        var before = SnapshotTrackIds(ev.Track);
                        Timecode leftStart = ev.Start;
                        ev.Split(globalTc - ev.Start);

                        ShiftSplitPieces(
                            ev.Track,
                            ev,
                            before,
                            globalTc,
                            leftStart,
                            split.StepJumpSeconds,
                            group.GroupId,
                            logCallback);

                        foreach (TrackEvent existing in ev.Track.Events)
                        {
                            if (!before.Contains(existing.EventID))
                            {
                                changedVegasEventIds.Add(existing.EventID);
                                groupMemberVegasIds.Add(existing.EventID);
                                unitMembers.Add(existing);
                            }
                        }
                    }
                }
            }

            if (movedGroupUnits != null && moved > 0)
            {
                movedGroupUnits.Add(unitMembers);
            }

            logCallback?.Invoke(
                $"  Moved group '{group.GroupId}' ({group.Kind}) by {delta:+0.000;-0.000;0.000}s; {moved} member(s) kept their internal offsets.");
            return moved;
        }

        private static double Seconds(Timecode tc)
        {
            return tc.ToMilliseconds() / 1000.0;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>Normalizes a rate read from the host so bad values behave as 1.0.</summary>
        private static double SanitizePlaybackRate(double rate)
        {
            if (!IsFinite(rate) || rate <= 0.0001 || rate > 100.0) return 1.0;
            return rate;
        }

        /// <summary>
        /// Clamps an absolute drift target. DataContractJsonSerializer does not run
        /// property initializers, so an old manifest deserializes a missing rate as
        /// 0.0; that must mean 1.0, not "clamp to 0.90".
        /// </summary>
        private static double ClampResolvedRate(double rate)
        {
            if (!IsFinite(rate) || rate <= 0.0) return 1.0;
            return Math.Max(0.90, Math.Min(1.10, rate));
        }

        /// <summary>
        /// Sets a new playback rate while preserving the media span:
        /// newLength = oldLength * (oldRate / newRate). Callers only pass targets for
        /// rates inside the drift band, so a manual retime is never rescaled.
        /// </summary>
        private static bool ApplyPlaybackRate(
            TrackEvent ev,
            double oldRate,
            double newRate,
            string context,
            Action<string> logCallback)
        {
            if (ev == null) return false;

            Timecode originalLength = ev.Length;
            double oldLengthSeconds = Seconds(originalLength);
            double newLengthSeconds = oldLengthSeconds * (oldRate / newRate);

            if (!IsFinite(newLengthSeconds)
                || newLengthSeconds <= 0.0005
                || newLengthSeconds > 10000000.0)
            {
                logCallback?.Invoke(
                    $"  WARNING: [{context}] skipped rate {oldRate:F4} -> {newRate:F4} (unsafe length {newLengthSeconds:F3}s).");
                return false;
            }

            try
            {
                // Set the rate first, then pin the scaled timeline length explicitly: the
                // host may or may not adjust the length itself when the rate changes.
                ev.PlaybackRate = newRate;
                ev.AdjustStartLength(ev.Start, Timecode.FromMilliseconds(newLengthSeconds * 1000.0), false);
                logCallback?.Invoke(
                    $"  Rate [{context}]: {oldRate:F4} -> {newRate:F4} (length {oldLengthSeconds:F3}s -> {newLengthSeconds:F3}s)");
                return true;
            }
            catch (Exception ex)
            {
                // Best-effort restore; the surrounding UndoBlock is the final safety net.
                try { ev.PlaybackRate = oldRate; } catch { }
                try { ev.AdjustStartLength(ev.Start, originalLength, false); } catch { }
                logCallback?.Invoke(
                    $"  WARNING: [{context}] could not apply rate {newRate:F4} ({ex.Message}).");
                return false;
            }
        }

        /// <summary>
        /// Force audio-only placement (DST): report overlapping results that were left alone because
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
                    $"Force audio-only placement (DST): {overlaps} overlapping pair(s) left on their tracks (no lanes created). Review these manually.");
            }
        }

        /// <summary>
        /// Best-effort rollback for a group whose move failed partway through.
        /// Restores start, length and playback rate in reverse order and removes the members
        /// from the changed/pinned sets so the lane pass does not react to them.
        /// The surrounding UndoBlock remains the final safety net.
        /// </summary>
        private static void RollBackGroupMembers(
            List<TrackEvent> memberEvents,
            List<int> movedIndexes,
            List<double> originalStarts,
            List<double> originalLengths,
            List<double> originalRates,
            bool rateChanged,
            HashSet<long> changedVegasEventIds,
            HashSet<long> groupMemberVegasIds,
            RateHistorySession rateHistory)
        {
            for (int k = movedIndexes.Count - 1; k >= 0; k--)
            {
                int i = movedIndexes[k];
                TrackEvent ev = memberEvents[i];
                try
                {
                    ev.AdjustStartLength(
                        Timecode.FromMilliseconds(originalStarts[i] * 1000.0),
                        Timecode.FromMilliseconds(originalLengths[i] * 1000.0),
                        false);
                    if (rateChanged) ev.PlaybackRate = originalRates[i];
                    changedVegasEventIds.Remove(ev.EventID);
                    groupMemberVegasIds.Remove(ev.EventID);
                    rateHistory?.Forget(ev.EventID);
                }
                catch
                {
                    // Keep restoring the other members; the UndoBlock covers the rest.
                }
            }
        }

        private static int ApplyEventResult(
            Vegas vegas,
            RateHistorySession rateHistory,
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

            // 2. Apply clock drift playback rate. The engine re-measures the source
            //    media, so its target is absolute. Rate history decides whether an
            //    in-band value is WaveSync's own stretch or a deliberate manual retime;
            //    either way, changing the rate preserves the media span.
            double oldRate = SanitizePlaybackRate(ev.PlaybackRate);
            bool rateEligible = rateHistory != null
                && rateHistory.IsEligible(ev.EventID, GetMediaPath(ev), oldRate, logCallback);
            double safeRate = rateEligible
                ? ClampResolvedRate(syncResult.PlaybackRate)
                : oldRate;
            bool rateChanged = Math.Abs(safeRate - oldRate) > 1e-6;
            if (rateChanged)
            {
                if (ApplyPlaybackRate(ev, oldRate, safeRate, eventId, logCallback))
                {
                    changed = true;
                    rateHistory.Record(ev.EventID, GetMediaPath(ev), safeRate);
                }
                else
                {
                    rateChanged = false;
                }
            }

            // 3. Move linked companion media that has no synchronization result of its own.
            SynchronizeCompanionEvents(
                vegas,
                rateHistory,
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
            //    Each right-hand piece is pulled by the step jump so a dropped-frame jump is
            //    corrected instead of only being cut.
            var splitPlan = BuildSplitPlan(syncResult, logCallback);
            if (splitPlan.Count > 0 && ev.Track != null)
            {
                var preSplitIds = SnapshotTrackIds(ev.Track);

                foreach (var split in splitPlan)
                {
                    Timecode splitTc = Timecode.FromMilliseconds(split.TimestampSeconds * 1000.0) + deltaTc;
                    if (splitTc > ev.Start && splitTc < ev.End)
                    {
                        Timecode leftStart = ev.Start;
                        var beforeIds = SnapshotTrackIds(ev.Track);

                        ev.Split(splitTc - ev.Start);
                        SplitCompanionVideoEvents(
                            ev,
                            splitTc,
                            split.StepJumpSeconds,
                            eventId,
                            logCallback);
                        bool shifted = ShiftSplitPieces(
                            ev.Track,
                            ev,
                            beforeIds,
                            splitTc,
                            leftStart,
                            split.StepJumpSeconds,
                            eventId,
                            logCallback);

                        changed = true;
                        bool hasStepJump = Math.Abs(split.StepJumpSeconds) > 1e-9;
                        logCallback?.Invoke(shifted || !hasStepJump
                            ? $"  Split [{eventId}] at {splitTc} (step jump {split.StepJumpSeconds:+0.000;-0.000;0.000}s)"
                            : $"  Cut [{eventId}] at {splitTc}; step-jump correction was not applied.");
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

        /// <summary>
        /// Copies the split recommendations into a descending plan. <c>split_points</c>
        /// wins when present; the legacy timestamp-only list falls back to a zero shift.
        /// Near-equal timestamps (within 1 ms) are merged, keeping the larger jump.
        /// </summary>
        private static List<SplitPoint> BuildSplitPlan(EventSyncResult syncResult, Action<string> logCallback)
        {
            var plan = new List<SplitPoint>();
            if (syncResult == null) return plan;

            if (syncResult.SplitPoints != null && syncResult.SplitPoints.Count > 0)
            {
                foreach (var split in syncResult.SplitPoints)
                {
                    if (split == null) continue;
                    if (!IsFinite(split.TimestampSeconds))
                    {
                        logCallback?.Invoke("  WARNING: ignored a split point with a non-finite timestamp.");
                        continue;
                    }
                    plan.Add(new SplitPoint
                    {
                        TimestampSeconds = split.TimestampSeconds,
                        StepJumpSeconds = IsFinite(split.StepJumpSeconds) ? split.StepJumpSeconds : 0.0
                    });
                }
            }
            else if (syncResult.SplitPointsSeconds != null)
            {
                foreach (double timestamp in syncResult.SplitPointsSeconds)
                {
                    if (!IsFinite(timestamp))
                    {
                        logCallback?.Invoke("  WARNING: ignored a split point with a non-finite timestamp.");
                        continue;
                    }
                    plan.Add(new SplitPoint
                    {
                        TimestampSeconds = timestamp,
                        StepJumpSeconds = 0.0
                    });
                }
            }

            // Descending: splitting later points first keeps earlier ones valid.
            plan.Sort((a, b) => b.TimestampSeconds.CompareTo(a.TimestampSeconds));

            var merged = new List<SplitPoint>();
            foreach (var split in plan)
            {
                int last = merged.Count - 1;
                if (last >= 0
                    && Math.Abs(merged[last].TimestampSeconds - split.TimestampSeconds) <= 0.001)
                {
                    if (Math.Abs(split.StepJumpSeconds) > Math.Abs(merged[last].StepJumpSeconds))
                    {
                        merged[last] = split;
                    }
                    continue;
                }
                merged.Add(split);
            }

            return merged;
        }

        private static HashSet<long> SnapshotTrackIds(Track track)
        {
            var ids = new HashSet<long>();
            if (track == null) return ids;
            foreach (TrackEvent ev in track.Events)
            {
                ids.Add(ev.EventID);
            }
            return ids;
        }

        /// <summary>
        /// Repairs a dropped-frame jump at a split. A positive jump pulls the right
        /// piece left; the duplicated left tail is trimmed by the same amount so the
        /// two pieces stay adjacent instead of overlapping (an overlap would be
        /// relocated to another lane by the crossfade pass). A negative jump leaves a
        /// gap. Length and playback rate of the right piece are preserved.
        /// </summary>
        private static bool ShiftSplitPieces(
            Track track,
            TrackEvent leftPiece,
            HashSet<long> beforeIds,
            Timecode splitTc,
            Timecode leftStart,
            double stepJumpSeconds,
            string context,
            Action<string> logCallback)
        {
            if (track == null || beforeIds == null || leftPiece == null) return false;
            if (!IsFinite(stepJumpSeconds) || Math.Abs(stepJumpSeconds) < 1e-9) return false;

            if (Math.Abs(stepJumpSeconds) > MaxStepJumpSeconds)
            {
                logCallback?.Invoke(
                    $"  WARNING: [{context}] step jump {stepJumpSeconds:+0.000;-0.000;0.000}s exceeds {MaxStepJumpSeconds}s; clamping.");
                stepJumpSeconds = (stepJumpSeconds > 0 ? 1.0 : -1.0) * MaxStepJumpSeconds;
            }

            double splitSec = Seconds(splitTc);
            double leftStartSec = Seconds(leftStart);

            var pieces = new List<TrackEvent>();
            foreach (TrackEvent piece in track.Events)
            {
                if (beforeIds.Contains(piece.EventID)) continue;
                if (!piece.IsValid()) continue;

                // Only the piece that starts at the split point belongs to it. The set
                // diff already isolates the new event; the window absorbs frame snapping.
                double pieceStartSec = Seconds(piece.Start);
                if (pieceStartSec < splitSec - SplitSnapToleranceSeconds
                    || pieceStartSec > splitSec + SplitSnapToleranceSeconds)
                    continue;

                pieces.Add(piece);
            }

            if (pieces.Count == 0) return false;

            // A positive jump means the post-drop audio runs late, so the right piece
            // is pulled left. That would overlap the left piece and make the crossfade
            // pass relocate it to another lane, undoing the correction. Trim the
            // duplicated tail instead: the dropped time is removed and the cut stays
            // on one track.
            if (stepJumpSeconds > 0)
            {
                double trimmedLengthSec = Seconds(leftPiece.Length) - stepJumpSeconds;
                if (trimmedLengthSec < MinLeftPieceSeconds)
                {
                    logCallback?.Invoke(
                        $"  WARNING: [{context}] skipped the {stepJumpSeconds:+0.000;-0.000;0.000}s step-jump shift (left piece too short to trim).");
                    return false;
                }

                try
                {
                    leftPiece.AdjustStartLength(
                        leftPiece.Start,
                        Timecode.FromMilliseconds(trimmedLengthSec * 1000.0),
                        false);
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke(
                        $"  WARNING: [{context}] could not trim the left piece ({ex.Message}).");
                    return false;
                }
            }

            bool applied = false;
            foreach (TrackEvent piece in pieces)
            {
                double pieceStartSec = Seconds(piece.Start);
                double newStartSec = pieceStartSec - stepJumpSeconds;
                if (!IsFinite(newStartSec) || newStartSec < 0.0)
                {
                    logCallback?.Invoke(
                        $"  WARNING: [{context}] skipped the {stepJumpSeconds:+0.000;-0.000;0.000}s step-jump shift (invalid start).");
                    continue;
                }

                if (newStartSec < leftStartSec - 0.001)
                {
                    logCallback?.Invoke(
                        $"  WARNING: [{context}] skipped the {stepJumpSeconds:+0.000;-0.000;0.000}s step-jump shift (would cross the left piece).");
                    continue;
                }

                // An inserted-frame jump moves the right piece later and leaves a gap.
                // If the next event starts inside that gap, clamp the shift so the
                // piece does not overlap an unrelated clip (which the crossfade pass
                // would otherwise relocate to a new track).
                if (stepJumpSeconds < 0.0)
                {
                    double nextStartSec = FindNextEventStart(track, piece, splitSec);
                    if (IsFinite(nextStartSec))
                    {
                        double maxStartSec = nextStartSec - Seconds(piece.Length) - 0.001;
                        if (maxStartSec < pieceStartSec - 0.001)
                        {
                            logCallback?.Invoke(
                                $"  WARNING: [{context}] skipped the {stepJumpSeconds:+0.000;-0.000;0.000}s step-jump shift (no room before the next event).");
                            continue;
                        }

                        if (newStartSec > maxStartSec)
                        {
                            logCallback?.Invoke(
                                $"  WARNING: [{context}] clamped the {stepJumpSeconds:+0.000;-0.000;0.000}s step-jump shift to avoid overlapping the next event.");
                            newStartSec = maxStartSec;
                        }
                    }
                }

                try
                {
                    piece.AdjustStartLength(
                        Timecode.FromMilliseconds(newStartSec * 1000.0),
                        piece.Length,
                        false);
                    applied = true;
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke(
                        $"  WARNING: [{context}] could not shift the split piece ({ex.Message}).");
                }
            }

            return applied;
        }

        /// <summary>
        /// Start of the nearest valid event after <paramref name="afterSec"/> on the
        /// same track, excluding the piece being moved.
        /// </summary>
        private static double FindNextEventStart(Track track, TrackEvent moving, double afterSec)
        {
            double next = double.PositiveInfinity;
            foreach (TrackEvent other in track.Events)
            {
                if (other == moving || !other.IsValid()) continue;
                double start = Seconds(other.Start);
                if (start > afterSec + 0.001 && start < next)
                {
                    next = start;
                }
            }
            return next;
        }

        private static void SplitCompanionVideoEvents(
            TrackEvent audioEvent,
            Timecode globalSplitTc,
            double stepJumpSeconds,
            string context,
            Action<string> logCallback)
        {
            if (!audioEvent.IsGrouped || audioEvent.Group == null) return;

            // Snapshot first: splitting may add events to the group collection.
            var companions = new List<TrackEvent>();
            foreach (TrackEvent comp in audioEvent.Group)
            {
                companions.Add(comp);
            }

            foreach (TrackEvent comp in companions)
            {
                if (comp == audioEvent || comp.Track == null) continue;
                if (!(globalSplitTc > comp.Start && globalSplitTc < comp.End)) continue;

                var before = SnapshotTrackIds(comp.Track);
                Timecode leftStart = comp.Start;
                comp.Split(globalSplitTc - comp.Start);

                ShiftSplitPieces(
                    comp.Track,
                    comp,
                    before,
                    globalSplitTc,
                    leftStart,
                    stepJumpSeconds,
                    context + " companion",
                    logCallback);
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
            RateHistorySession rateHistory,
            TrackEvent sourceEvent,
            Timecode originalStart,
            Timecode originalLength,
            Timecode deltaTc,
            double targetRate,
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
                        double companionOldRate = SanitizePlaybackRate(companion.PlaybackRate);
                        bool companionEligible = rateHistory != null
                            && rateHistory.IsEligible(
                                companion.EventID,
                                GetMediaPath(companion),
                                companionOldRate,
                                logCallback);
                        double companionTarget = companionEligible ? targetRate : companionOldRate;
                        if (Math.Abs(companionTarget - companionOldRate) > 1e-6)
                        {
                            if (ApplyPlaybackRate(companion, companionOldRate, companionTarget, "companion", logCallback))
                            {
                                rateHistory.Record(companion.EventID, GetMediaPath(companion), companionTarget);
                            }
                        }
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
                        double companionOldRate = SanitizePlaybackRate(vEv.PlaybackRate);
                        bool companionEligible = rateHistory != null
                            && rateHistory.IsEligible(
                                vEv.EventID,
                                GetMediaPath(vEv),
                                companionOldRate,
                                logCallback);
                        double companionTarget = companionEligible ? targetRate : companionOldRate;
                        if (Math.Abs(companionTarget - companionOldRate) > 1e-6)
                        {
                            if (ApplyPlaybackRate(vEv, companionOldRate, companionTarget, "linked companion", logCallback))
                            {
                                rateHistory.Record(vEv.EventID, GetMediaPath(vEv), companionTarget);
                            }
                        }
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
        private static int SeparateOverlappingEvents(
            Vegas vegas,
            List<Track> trackSnapshot,
            HashSet<long> changedVegasEventIds,
            HashSet<long> pinnedGroupEventIds,
            List<List<TrackEvent>> movedGroupUnits,
            Action<string> logCallback)
        {
            int lanesCreated = 0;
            int eventsRelocated = 0;
            bool groupPinnedNotice = false;

            // Guarded group-atomic lane assignment: one lane index per fully moved
            // group, applied to each member's own source track. Off by default until
            // the VEGAS host behavior for grouped track reassignment is validated.
            if (EnableGroupLaneMove && movedGroupUnits != null && movedGroupUnits.Count > 0)
            {
                RelocateGroupUnits(
                    vegas,
                    movedGroupUnits,
                    changedVegasEventIds,
                    logCallback,
                    ref lanesCreated,
                    ref eventsRelocated);
            }

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

                // Report once per run when a moved group stays pinned on a track that
                // has to be split (the disabled group lane path).
                if (!EnableGroupLaneMove && !groupPinnedNotice
                    && pinnedGroupEventIds != null && pinnedGroupEventIds.Count > 0)
                {
                    for (int i = 0; i < events.Count && !groupPinnedNotice; i++)
                    {
                        if (!pinned[i] || !pinnedGroupEventIds.Contains(events[i].EventID))
                            continue;

                        for (int j = 0; j < events.Count; j++)
                        {
                            if (i == j) continue;
                            if (starts[i] < ends[j] - 1.0 && ends[i] > starts[j] + 1.0)
                            {
                                groupPinnedNotice = true;
                                break;
                            }
                        }
                    }
                }

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

            if (groupPinnedNotice)
            {
                logCallback?.Invoke(
                    "NOTE: overlapping grouped A/V clips stay on the original track (group lane relocation is disabled pending host validation).");
            }

            if (lanesCreated > 0)
            {
                logCallback?.Invoke(
                    $"Crossfade protection: added {lanesCreated} track(s) and relocated {eventsRelocated} event(s).");
            }

            return lanesCreated;
        }

        /// <summary>
        /// Group-atomic lane assignment for the (guarded) group lane path. Each fully
        /// moved group is one unit spanning min-start to max-end; the unit gets a single
        /// lane index and every member is relocated to that lane of its own source track
        /// (video member to a video lane, audio member to an audio lane).
        /// Groups with any non-movable member are left pinned on the original track.
        /// </summary>
        private static void RelocateGroupUnits(
            Vegas vegas,
            List<List<TrackEvent>> movedGroupUnits,
            HashSet<long> changedVegasEventIds,
            Action<string> logCallback,
            ref int lanesCreated,
            ref int eventsRelocated)
        {
            var units = new List<List<TrackEvent>>();
            foreach (var candidate in movedGroupUnits)
            {
                if (candidate == null || candidate.Count == 0) continue;

                bool movable = true;
                foreach (var ev in candidate)
                {
                    if (ev == null
                        || !ev.IsValid()
                        || ev.Track == null
                        || ev.Locked
                        || !changedVegasEventIds.Contains(ev.EventID))
                    {
                        movable = false;
                        break;
                    }
                }

                // A group that lost a member stays pinned so it cannot be torn apart.
                if (movable) units.Add(candidate);
            }

            if (units.Count == 0) return;

            var unitStarts = new List<double>(units.Count);
            var unitEnds = new List<double>(units.Count);
            foreach (var unit in units)
            {
                double minStart = double.MaxValue;
                double maxEnd = double.MinValue;
                foreach (var ev in unit)
                {
                    double start = ev.Start.ToMilliseconds();
                    double end = ev.End.ToMilliseconds();
                    if (start < minStart) minStart = start;
                    if (end > maxEnd) maxEnd = end;
                }
                unitStarts.Add(minStart);
                unitEnds.Add(maxEnd);
            }

            int unitLaneCount;
            var laneByUnit = LanePartitioner.Assign(unitStarts, unitEnds, out unitLaneCount, 1.0, null);
            if (unitLaneCount <= 1) return;

            int relocated = 0;
            for (int u = 0; u < units.Count; u++)
            {
                int laneNumber = laneByUnit[u] + 1;
                if (laneByUnit[u] <= 0) continue;
                if (laneNumber - 1 > MaxLanesPerTrack)
                {
                    logCallback?.Invoke("  WARNING: group lane limit reached; some grouped overlaps remain.");
                    continue;
                }

                foreach (var member in units[u])
                {
                    Track sourceTrack = member.Track;
                    if (sourceTrack == null) continue;

                    bool created;
                    Track lane = GetOrCreateLane(vegas, sourceTrack, laneNumber, units[u], out created, logCallback);
                    if (lane == null) continue;
                    if (created)
                    {
                        lanesCreated++;
                    }

                    try
                    {
                        member.Track = lane;
                        if (member.Track == null || member.Track.Index != lane.Index)
                        {
                            throw new InvalidOperationException("VEGAS rejected the group member move to the new lane.");
                        }
                        relocated++;
                        eventsRelocated++;
                    }
                    catch (Exception ex)
                    {
                        logCallback?.Invoke($"  WARNING: could not move a grouped event to a new lane: {ex.Message}");
                    }
                }
            }

            logCallback?.Invoke(
                $"Group lane relocation: {units.Count} group unit(s), {relocated} member(s) relocated by group index.");
        }

        /// <summary>
        /// Returns the lane track for <paramref name="laneNumber"/> on the source track,
        /// reusing a lane from an earlier run or creating and configuring a new one.
        /// </summary>
        private static Track GetOrCreateLane(
            Vegas vegas,
            Track sourceTrack,
            int laneNumber,
            List<TrackEvent> movingEvents,
            out bool created,
            Action<string> logCallback)
        {
            created = false;
            string baseName = string.IsNullOrEmpty(sourceTrack.Name)
                ? (sourceTrack.IsVideo() ? "Video" : "Audio")
                : sourceTrack.Name;

            Track lane = FindReusableLane(vegas, sourceTrack, baseName, laneNumber, movingEvents);
            if (lane != null) return lane;

            try
            {
                lane = sourceTrack.IsVideo()
                    ? (Track)vegas.Project.AddVideoTrack()
                    : (Track)vegas.Project.AddAudioTrack();
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"  WARNING: could not create an extra track: {ex.Message}");
                return null;
            }

            created = true;
            TryCopyTrackSettings(sourceTrack, lane, laneNumber);
            return lane;
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

            // Static mixer values, where the track type exposes them.
            try
            {
                var sourceAudio = source as AudioTrack;
                var targetAudio = target as AudioTrack;
                if (sourceAudio != null && targetAudio != null)
                {
                    targetAudio.Volume = sourceAudio.Volume;
                }
            }
            catch
            {
                // Volume not settable on this track type.
            }

            try
            {
                var sourceAudio = source as AudioTrack;
                var targetAudio = target as AudioTrack;
                if (sourceAudio != null && targetAudio != null)
                {
                    targetAudio.PanX = sourceAudio.PanX;
                }
            }
            catch
            {
                // Pan not settable on this track type.
            }

            try
            {
                var sourceVideo = source as VideoTrack;
                var targetVideo = target as VideoTrack;
                if (sourceVideo != null && targetVideo != null)
                {
                    targetVideo.CompositeLevel = sourceVideo.CompositeLevel;
                }
            }
            catch
            {
                // Composite level not settable on this track type.
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
                        if (targetEnvelope == null)
                        {
                            if (!target.CanAddEnvelope(sourceEnvelope.Type)) continue;
                            targetEnvelope = new Envelope(sourceEnvelope.Type);
                            target.Envelopes.Add(targetEnvelope);
                        }

                        // Always drop the implicit time-zero anchor before replaying the
                        // source points. A brand-new track usually already has a default
                        // Volume envelope, and appending a source point at X=0 on top of
                        // that default anchor produced a duplicate.
                        for (int i = targetEnvelope.Points.Count - 1; i >= 0; i--)
                        {
                            if (targetEnvelope.Points[i].X.ToMilliseconds() <= 0.5)
                            {
                                targetEnvelope.Points.RemoveAt(i);
                            }
                        }

                        // Envelope points must be added in ascending time order.
                        var points = new List<EnvelopePoint>();
                        foreach (EnvelopePoint point in sourceEnvelope.Points)
                        {
                            points.Add(point);
                        }
                        points.Sort((a, b) => a.X.CompareTo(b.X));

                        foreach (EnvelopePoint point in points)
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
