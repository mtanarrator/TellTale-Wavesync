use rayon::prelude::*;
use std::collections::{HashMap, HashSet};
use std::path::Path;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Instant;

use crate::audio::decoder::{reset_ffmpeg_availability, CANCELLED};
use crate::audio::AudioDecoder;
use crate::correlation::coarse::{ENVELOPE_FRAME_SIZE, ENVELOPE_HOP_SIZE};
use crate::correlation::gcc_phat::MAX_CORRELATION_INPUT_SAMPLES;
use crate::correlation::{AcousticFingerprinter, CoarseAligner, GccPhat};
use crate::drift::{DiscontinuityDetector, DriftObservation, RansacRegression};
use crate::ffi::ProgressReporter;
use crate::graph::{MultiTrackGraphSolver, SyncEdge};
use crate::models::{EventDescriptor, EventSyncResult, SplitPoint, SyncManifest, SyncTaskRequest};

/// Events shorter than this keep their PCM in memory (cheap, and allows a single
/// high-precision full-buffer GCC-PHAT pass).
pub(crate) const RETAIN_SAMPLES_SECONDS: f64 = 300.0;

/// Absolute ceiling on how much of a single take is analyzed. Wedding/event
/// recordings are long, but there is no practical benefit beyond that.
const MAX_ANALYSIS_SECONDS: f64 = 12.0 * 3600.0;

/// Window used for the precision GCC-PHAT refinement.
const FINE_WINDOW_SECONDS: f64 = 30.0;

/// Rolling drift analysis window and the span it is allowed to cover.
const DRIFT_WINDOW_SECONDS: f64 = 15.0;
const DRIFT_SPAN_SECONDS: f64 = 600.0;
const DRIFT_MAX_OBSERVATIONS: usize = 64;

/// Minimum overlapping audio required before we trust a correlation.
const MIN_OVERLAP_SECONDS: f64 = 1.0;

/// Fingerprint fallback window (bounded like everything else).
const FINGERPRINT_WINDOW_SECONDS: f64 = 120.0;

/// A prepared (decoded + envelope-analyzed) timeline event.
#[derive(Clone)]
pub(crate) struct PreparedEvent {
    pub(crate) event: EventDescriptor,
    /// Media time (seconds) that corresponds to buffer index 0.
    pub(crate) span_start_media: f64,
    /// Analyzed duration in seconds.
    pub(crate) span_len_seconds: f64,
    /// Retained PCM (only for short spans), at the internal sample rate.
    pub(crate) samples: Option<Vec<f32>>,
    /// 125 Hz log-energy envelope (always available).
    pub(crate) envelope: Vec<f32>,
    /// True for generated/mock media where envelope similarity is meaningless.
    pub(crate) is_synthetic: bool,
}

impl PreparedEvent {
    fn buffer_len(&self, rate: u32) -> usize {
        (self.span_len_seconds * rate as f64).round().max(1.0) as usize
    }

    /// Returns `len` samples starting at buffer index `offset`, either from the
    /// retained buffer or by re-decoding just that window from disk.
    fn fetch(
        &self,
        offset: usize,
        len: usize,
        rate: u32,
        ffmpeg: Option<&str>,
    ) -> Result<Vec<f32>, String> {
        let total = match &self.samples {
            Some(buf) => buf.len(),
            None => self.buffer_len(rate),
        };
        if total == 0 {
            return Err("empty audio buffer".into());
        }
        let start = offset.min(total - 1);
        let len = len.min(total - start).max(1);

        if let Some(buf) = &self.samples {
            return Ok(buf[start..start + len].to_vec());
        }

        let media_start = self.span_start_media + start as f64 / rate as f64;
        let duration = len as f64 / rate as f64;
        AudioDecoder::load_and_condition_for_stream(
            &self.event.file_path,
            self.event.stream_index,
            Some(media_start),
            Some(duration),
            rate,
            ffmpeg,
        )
    }
}

struct DraftInfo {
    edge: SyncEdge,
    ncc: f64,
    span_seconds: f64,
    timeline_start: f64,
    // Timeline seconds from the source media start (already converted with the
    // event's playback rate at construction).
    media_start: f64,
}

#[derive(Clone)]
struct ClockCandidate {
    recorded: f64,
    clock: f64,
    delta: f64,
    ncc: f64,
    confidence: f64,
}

/// Thresholds for trusting a per-event audio match.
const NCC_ANCHOR: f64 = 0.60;
const NCC_CANDIDATE: f64 = 0.35;
const CLOCK_CLUSTER_TOLERANCE: f64 = 2.0;
const NCC_MIN: f64 = 0.35;
const MIN_ANCHOR_SPAN: f64 = 15.0;
const MIN_INDIVIDUAL_SPAN: f64 = 1.5;
const OUTLIER_TOLERANCE: f64 = 3.0;
const SOURCE_SEGMENT_GAP_SECONDS: f64 = 60.0;

pub(crate) struct PairAnalysis {
    pub(crate) lag_seconds: f64,
    pub(crate) confidence: f64,
    pub(crate) drift_ppm: f64,
    pub(crate) split_points: Vec<SplitPoint>,
    pub(crate) method: &'static str,
    pub(crate) ncc_score: f64,
}

struct EdgeOutcome {
    edge: Option<SyncEdge>,
    method: Option<String>,
    ncc_score: Option<f64>,
    split_points: Vec<SplitPoint>,
    warning: Option<String>,
    cancelled: bool,
}

impl EdgeOutcome {
    fn cancelled() -> Self {
        Self {
            edge: None,
            method: None,
            ncc_score: None,
            split_points: Vec::new(),
            warning: None,
            cancelled: true,
        }
    }
}

pub fn run_sync_pipeline(
    request: &SyncTaskRequest,
    reporter: &ProgressReporter,
) -> Result<SyncManifest, String> {
    let start_time = Instant::now();
    let target_rate = request.options.internal_sample_rate.max(1000);

    // Allow FFmpeg to be probed again on every run (the user may have installed it
    // since the previous synchronization).
    reset_ffmpeg_availability();

    if !reporter.report(0.02, "Validating timeline events...") {
        return Err(CANCELLED.into());
    }

    if request.tracks.is_empty() {
        return Err("No tracks provided in synchronization request".into());
    }

    // Collect all events
    let mut all_events: Vec<EventDescriptor> = Vec::new();
    for track in &request.tracks {
        for ev in &track.events {
            all_events.push(ev.clone());
        }
    }

    if all_events.is_empty() {
        return Err("No audio events found on timeline".into());
    }

    let mut warnings: Vec<String> = Vec::new();
    let gcc = GccPhat::new();

    // --- Global matching (Milestone B) ----------------------------------------------
    // Enabled by default from the UI. It is skipped for DST/preserve-timeline mode,
    // which must keep its rigid per-source behaviour. Any sparse/unusable input falls
    // through to the untouched legacy pipeline below.
    if request.options.global_matching && !request.options.preserve_timeline_order {
        match crate::global_pipeline::run_global(request, &all_events, target_rate, reporter) {
            Ok((Some(event_results), mut global_warnings)) => {
                warnings.append(&mut global_warnings);
                if let Some(track_id) = request.master_track_id.as_deref() {
                    if !all_events.iter().any(|e| e.track_id == track_id) {
                        warnings.push(format!(
                            "Selected master track '{}' has no analyzable events (generators/missing media, or it was excluded by the selection scope). Falling back to Auto (recommended).",
                            track_id
                        ));
                    }
                }

                // Global matching has no implicit master. Keep the user-pinned
                // track if one was selected; otherwise leave it empty so the UI
                // can report "Global matching" without a false master.
                let master_track_id = request
                    .master_track_id
                    .clone()
                    .filter(|track| all_events.iter().any(|e| &e.track_id == track))
                    .unwrap_or_default();

                let elapsed = start_time.elapsed().as_millis() as u64;
                return Ok(SyncManifest {
                    session_id: format!(
                        "sync_{}",
                        std::time::SystemTime::now()
                            .duration_since(std::time::UNIX_EPOCH)
                            .unwrap()
                            .as_secs()
                    ),
                    success: true,
                    master_track_id,
                    events: event_results,
                    warnings,
                    processing_time_ms: elapsed,
                });
            }
            Ok((None, mut global_warnings)) => {
                // Keep the global diagnostics (for example untrusted clocks)
                // even when the sparse/memory fallback runs.
                warnings.append(&mut global_warnings);
                warnings.push(
                    "Global matching not applicable for this project; falling back to the legacy single-anchor pipeline."
                        .to_string(),
                );
            }
            Err(e) if e == CANCELLED => return Err(CANCELLED.into()),
            Err(e) => warnings.push(format!(
                "Global matching failed ({}); falling back to the legacy single-anchor pipeline.",
                e
            )),
        }
    }

    // --- Master reference selection -------------------------------------------------
    // Pick the event that covers the longest continuous media span. For wedding
    // projects this is normally the long audio recorder take, which gives every
    // short camera clip an absolute position to lock onto.
    if let Some(track_id) = request.master_track_id.as_deref() {
        if !all_events.iter().any(|e| e.track_id == track_id) {
            warnings.push(format!(
                "Selected master track '{}' has no analyzable events (generators/missing media, or it was excluded by the selection scope). Falling back to Auto (recommended).",
                track_id
            ));
        }
    }

    let master_event = choose_master(request, &all_events);
    let master_track_id = master_event.track_id.clone();

    reporter.report(
        0.03,
        &format!(
            "Master reference{}: {} ({}, {})",
            if request.master_track_id.is_some() {
                " (selected track)"
            } else {
                " (auto)"
            },
            master_event.event_id,
            short_file_name(&master_event.file_path),
            format_duration(master_span_score(master_event))
        ),
    );

    // --- Decode / envelope analysis -------------------------------------------------
    let master_progress = |frac: f32| -> bool {
        let frac = frac.clamp(0.0, 1.0);
        reporter.report(
            0.03 + 0.17 * frac,
            &format!(
                "Analyzing master reference {} ({:.0}%)",
                short_file_name(&master_event.file_path),
                frac * 100.0
            ),
        )
    };

    let master = prepare_event(
        master_event,
        true,
        request,
        target_rate,
        Some(&master_progress),
    )
    .map_err(|e| {
        if e == CANCELLED {
            e
        } else {
            format!(
                "Failed to analyze master event '{}': {}",
                master_event.event_id, e
            )
        }
    })?;

    if master.span_len_seconds > RETAIN_SAMPLES_SECONDS {
        warnings.push(format!(
            "Master take is {} long; analyzed via streaming envelope with windowed sample-exact refinement.",
            format_duration(master.span_len_seconds)
        ));
    }

    // --- Slave events ---------------------------------------------------------------
    let non_master_events: Vec<&EventDescriptor> = all_events
        .iter()
        .filter(|e| e.event_id != master_event.event_id)
        .collect();

    let total_non_master = non_master_events.len().max(1);
    let completed = AtomicUsize::new(0);

    // Bound memory: each worker can hold one short clip plus up to two 10 minute
    // refinement windows. Keeping the fan-out small is what keeps RAM flat while
    // still using multiple cores.
    let workers = rayon::current_num_threads().clamp(1, 4);

    let mut drafts: Vec<DraftInfo> = Vec::new();
    let mut methods: HashMap<String, String> = HashMap::new();
    let mut ncc_scores: HashMap<String, f64> = HashMap::new();
    // Structured split points keyed by event id. The graph solver only carries the
    // legacy timestamp array, so the step jumps are re-attached at manifest time.
    let mut split_points_by_event: HashMap<String, Vec<SplitPoint>> = HashMap::new();

    let event_map: HashMap<String, &EventDescriptor> = all_events
        .iter()
        .map(|e| (e.event_id.clone(), e))
        .collect();

    for chunk in non_master_events.chunks(workers) {
        let outcomes: Vec<EdgeOutcome> = chunk
            .par_iter()
            .map(|ev| analyze_event(&master, ev, request, target_rate, &gcc, reporter))
            .collect();

        for outcome in outcomes {
            if outcome.cancelled {
                return Err(CANCELLED.into());
            }
            if let Some(edge) = outcome.edge {
                if let Some(method) = outcome.method {
                    methods.insert(edge.to_event_id.clone(), method);
                }
                if let Some(ncc) = outcome.ncc_score {
                    ncc_scores.insert(edge.to_event_id.clone(), ncc);
                }
                if !outcome.split_points.is_empty() {
                    split_points_by_event
                        .insert(edge.to_event_id.clone(), outcome.split_points);
                }

                if let Some(ev) = event_map.get(&edge.to_event_id) {
                    drafts.push(DraftInfo {
                        edge,
                        ncc: outcome.ncc_score.unwrap_or(0.0),
                        span_seconds: analysis_span(ev, false).1,
                        timeline_start: ev.timeline_start_seconds,
                        media_start: media_seconds_to_timeline(ev, ev.media_start_seconds),
                    });
                }
            }
            if let Some(w) = outcome.warning {
                warnings.push(w);
            }
        }

        let done = completed.fetch_add(chunk.len(), Ordering::Relaxed) + chunk.len();
        let progress = 0.20 + 0.65 * (done as f32 / total_non_master as f32);
        if !reporter.report(
            progress,
            &format!(
                "Aligned {} of {} event(s)...",
                done.min(total_non_master),
                total_non_master
            ),
        ) {
            return Err(CANCELLED.into());
        }
    }

    // Master file T0 is the timeline timestamp corresponding to t=0 of the source
    // media. A retimed master advances media faster than timeline, so scale the
    // media offset back to timeline seconds.
    let master_t0 = master_event.timeline_start_seconds
        - media_seconds_to_timeline(master_event, master_event.media_start_seconds);

    // --- Phase 2: Multi-take & transitive relay pass --------------------------------
    // Events outside the single longest master take (e.g. a separate Reception take
    // on the master track, or secondary cameras that only overlap another camera)
    // could not match `master_event` in Phase 1. Correlate them against secondary
    // takes on the master track and/or strong long anchors so MultiTrackGraphSolver
    // can chain across chapters and tracks.
    let mut anchor_t0_map: HashMap<String, f64> = HashMap::new();
    anchor_t0_map.insert(master_event.event_id.clone(), master_t0);

    let strong_draft_ids: HashSet<String> = drafts
        .iter()
        .filter(|d| d.ncc >= NCC_MIN && d.edge.confidence >= 0.35 && d.span_seconds >= MIN_INDIVIDUAL_SPAN)
        .map(|d| d.edge.to_event_id.clone())
        .collect();

    for d in &drafts {
        if strong_draft_ids.contains(&d.edge.to_event_id) {
            anchor_t0_map.insert(d.edge.to_event_id.clone(), master_t0 + d.edge.offset_seconds);
        }
    }

    let mut secondary_roots: Vec<(String, f64)> = Vec::new();
    let mut relay_anchors: Vec<(&EventDescriptor, f64)> = Vec::new();

    // Other takes on the chosen master track that didn't overlap `master_event` act
    // as secondary chapter roots at their existing timeline positions.
    if let Some(selected_master_track) = request.master_track_id.as_deref() {
        for ev in &all_events {
            if ev.event_id != master_event.event_id
                && ev.track_id == selected_master_track
                && !strong_draft_ids.contains(&ev.event_id)
                && analysis_span(ev, true).1 >= MIN_ANCHOR_SPAN
            {
                let t0 = ev.timeline_start_seconds
                    - media_seconds_to_timeline(ev, ev.media_start_seconds);
                anchor_t0_map.insert(ev.event_id.clone(), t0);
                secondary_roots.push((ev.event_id.clone(), t0));
                relay_anchors.push((ev, t0));
            }
        }
    }

    // Also include up to 4 strong aligned long anchors (>= 30s) from other tracks
    // for transitive CamA -> CamB bridging when CamB doesn't overlap the master.
    let mut long_aligned: Vec<&DraftInfo> = drafts
        .iter()
        .filter(|d| d.ncc >= 0.55 && d.edge.confidence >= 0.50 && d.span_seconds >= 30.0)
        .collect();
    long_aligned.sort_by(|a, b| {
        b.span_seconds
            .partial_cmp(&a.span_seconds)
            .unwrap_or(std::cmp::Ordering::Equal)
    });
    for d in long_aligned.into_iter().take(4) {
        if let Some(ev) = event_map.get(&d.edge.to_event_id) {
            if let Some(&t0) = anchor_t0_map.get(&ev.event_id) {
                relay_anchors.push((ev, t0));
            }
        }
    }

    let relay_candidates: Vec<&EventDescriptor> = non_master_events
        .iter()
        .copied()
        .filter(|ev| {
            !strong_draft_ids.contains(&ev.event_id)
                && !anchor_t0_map.contains_key(&ev.event_id)
                && analysis_span(ev, false).1 >= 3.0
        })
        .collect();

    if !relay_anchors.is_empty() && !relay_candidates.is_empty() {
        if !reporter.report(0.87, "Checking secondary takes & transitive links...") {
            return Err(CANCELLED.into());
        }

        for (anchor_ev, _) in relay_anchors {
            if !reporter.report(0.88, &format!("Relay matching against {}...", short_file_name(&anchor_ev.file_path))) {
                return Err(CANCELLED.into());
            }
            let anchor_prepared = match prepare_event(anchor_ev, true, request, target_rate, None) {
                Ok(p) => p,
                Err(_) => continue,
            };

            let pending: Vec<&EventDescriptor> = relay_candidates
                .iter()
                .copied()
                .filter(|ev| {
                    ev.event_id != anchor_ev.event_id
                        && !drafts.iter().any(|d| {
                            d.edge.to_event_id == ev.event_id
                                && d.ncc >= NCC_MIN
                                && d.edge.confidence >= 0.40
                        })
                })
                .collect();

            for chunk in pending.chunks(workers) {
                let outcomes: Vec<EdgeOutcome> = chunk
                    .par_iter()
                    .map(|ev| analyze_event(&anchor_prepared, ev, request, target_rate, &gcc, reporter))
                    .collect();

                for outcome in outcomes {
                    if outcome.cancelled {
                        return Err(CANCELLED.into());
                    }
                    if let Some(edge) = outcome.edge {
                        let ncc = outcome.ncc_score.unwrap_or(0.0);
                        if ncc >= NCC_MIN && edge.confidence >= 0.40 {
                            // Replace any weak Phase 1 draft for this event with the stronger relay match.
                            drafts.retain(|d| d.edge.to_event_id != edge.to_event_id);
                            if let Some(method) = outcome.method {
                                methods.insert(edge.to_event_id.clone(), method);
                            }
                            ncc_scores.insert(edge.to_event_id.clone(), ncc);
                            if !outcome.split_points.is_empty() {
                                split_points_by_event
                                    .insert(edge.to_event_id.clone(), outcome.split_points);
                            }
                            if let Some(ev) = event_map.get(&edge.to_event_id) {
                                drafts.push(DraftInfo {
                                    edge,
                                    ncc,
                                    span_seconds: analysis_span(ev, false).1,
                                    timeline_start: ev.timeline_start_seconds,
                                    media_start: media_seconds_to_timeline(ev, ev.media_start_seconds),
                                });
                            }
                        }
                    }
                }
            }
        }
    }

    if !reporter.report(0.93, "Resolving multi-track graph topology...") {
        return Err(CANCELLED.into());
    }

    let secondary_root_ids: HashSet<String> =
        secondary_roots.iter().map(|(id, _)| id.clone()).collect();

    // Group drafts by media source (camera folder). Short clips are too ambiguous to
    // align on their own, so a group inherits the median shift measured by its
    // longest, most trustworthy clips. This also keeps every clip of a source moving
    // together, which prevents overlap/crossfade churn between runs.
    let (edges, grouping_warnings) = finalize_edges(
        &drafts,
        &all_events,
        &master_event.event_id,
        master_t0,
        &anchor_t0_map,
        &secondary_root_ids,
        request.options.preserve_timeline_order,
        &mut methods,
        &mut ncc_scores,
    );
    warnings.extend(grouping_warnings);

    if request.options.preserve_timeline_order {
        warnings.push(
            "Force audio-only placement (DST) enabled: recorded timestamps were ignored for placement; each source was shifted by one rigid audio-derived delta, so camera clock jumps (including daylight-saving changes) cannot move, reorder, or misplace clips."
                .to_string(),
        );
    }

    let all_event_ids: Vec<String> = all_events.iter().map(|e| e.event_id.clone()).collect();
    let resolved_map = MultiTrackGraphSolver::solve_with_secondary_anchors(
        &all_event_ids,
        &edges,
        &master_event.event_id,
        master_t0,
        &secondary_roots,
    );

    // Resolve absolute starts before applying any global shift.
    let mut resolved_starts: HashMap<String, (f64, bool)> = HashMap::new();
    let mut min_start = f64::INFINITY;

    for ev in &all_events {
        let is_master = ev.event_id == master_event.event_id;
        if let Some(resolved) = resolved_map.get(&ev.event_id) {
            let resolved_event_start = if is_master {
                ev.timeline_start_seconds
            } else if resolved.is_aligned {
                resolved.resolved_start_seconds
                    + media_seconds_to_timeline(ev, ev.media_start_seconds)
            } else {
                ev.timeline_start_seconds
            };

            if resolved.is_aligned && resolved_event_start < min_start {
                min_start = resolved_event_start;
            }
            resolved_starts.insert(ev.event_id.clone(), (resolved_event_start, resolved.is_aligned));
        }
    }

    // VEGAS cannot place events at negative timeline positions. If an event needs to
    // start before zero, shift the whole synced block right so relative sync is kept.
    let global_shift = if min_start.is_finite() && min_start < -0.001 {
        let shift = -min_start;
        warnings.push(format!(
            "Synchronized block shifted right by {} to keep every event on the positive timeline.",
            format_duration(shift)
        ));
        shift
    } else {
        0.0
    };

    let mut event_results = Vec::new();
    for ev in &all_events {
        let is_master = ev.event_id == master_event.event_id;

        if let Some(resolved) = resolved_map.get(&ev.event_id) {
            let (base_start, aligned) = resolved_starts
                .get(&ev.event_id)
                .copied()
                .unwrap_or((ev.timeline_start_seconds, false));

            let resolved_event_start = if aligned { base_start + global_shift } else { base_start };
            let delta = resolved_event_start - ev.timeline_start_seconds;

            let notes = if is_master {
                Some(format!(
                    "Master reference (anchored{})",
                    if global_shift > 0.001 {
                        format!(
                            ", block shifted right by {})",
                            format_duration(global_shift)
                        )
                    } else {
                        ")".to_string()
                    }
                ))
            } else if resolved.is_aligned {
                Some(format!(
                    "Aligned via {} (Drift: {:.1} PPM, Conf: {:.0}%, NCC: {:.2})",
                    methods
                        .get(&ev.event_id)
                        .map(|s| s.as_str())
                        .unwrap_or("GCC-PHAT"),
                    resolved.drift_ppm,
                    resolved.confidence * 100.0,
                    ncc_scores.get(&ev.event_id).copied().unwrap_or(0.0)
                ))
            } else {
                Some(format!(
                    "Low confidence ({:.0}%) / unaligned (no acoustic match found)",
                    resolved.confidence * 100.0
                ))
            };

            event_results.push(EventSyncResult {
                event_id: ev.event_id.clone(),
                track_id: ev.track_id.clone(),
                original_start_seconds: ev.timeline_start_seconds,
                resolved_start_seconds: resolved_event_start,
                delta_seconds: delta,
                playback_rate: resolved.playback_rate,
                drift_ppm: resolved.drift_ppm,
                confidence: resolved.confidence,
                split_points_seconds: resolved.split_points_seconds.clone(),
                split_points: align_split_points(
                    split_points_by_event.get(&ev.event_id).map(|v| v.as_slice()),
                    &resolved.split_points_seconds,
                ),
                is_aligned: resolved.is_aligned,
                notes,
            });
        }
    }

    let elapsed = start_time.elapsed().as_millis() as u64;

    Ok(SyncManifest {
        session_id: format!(
            "sync_{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_secs()
        ),
        success: true,
        master_track_id,
        events: event_results,
        warnings,
        processing_time_ms: elapsed,
    })
}

/// Rebuilds the structured split points for one event from the timestamps the
/// graph solver resolved, re-attaching the step jump recorded for the same
/// (event-relative) timestamp. Falls back to a zero shift when the solver used
/// another edge and no jump was recorded for that timestamp.
fn align_split_points(recorded: Option<&[SplitPoint]>, timestamps: &[f64]) -> Vec<SplitPoint> {
    let recorded = recorded.unwrap_or(&[]);
    timestamps
        .iter()
        .map(|&timestamp_seconds| {
            let step_jump_seconds = recorded
                .iter()
                .find(|sp| (sp.timestamp_seconds - timestamp_seconds).abs() <= 1e-3)
                .map(|sp| sp.step_jump_seconds)
                .unwrap_or(0.0);
            SplitPoint {
                timestamp_seconds,
                step_jump_seconds,
            }
        })
        .collect()
}

fn draft_resolved_start(
    d: &DraftInfo,
    anchor_t0_map: &HashMap<String, f64>,
    master_t0: f64,
) -> f64 {
    let from_t0 = anchor_t0_map
        .get(d.edge.from_event_id.as_str())
        .copied()
        .unwrap_or(master_t0);
    from_t0 + d.edge.offset_seconds + d.media_start
}

#[allow(clippy::too_many_arguments)]
fn finalize_edges(
    drafts: &[DraftInfo],
    all_events: &[EventDescriptor],
    master_event_id: &str,
    master_t0: f64,
    anchor_t0_map: &HashMap<String, f64>,
    secondary_root_ids: &HashSet<String>,
    preserve_order: bool,
    methods: &mut HashMap<String, String>,
    ncc_scores: &mut HashMap<String, f64>,
) -> (Vec<SyncEdge>, Vec<String>) {
    let mut warnings = Vec::new();
    let mut out: Vec<SyncEdge> = Vec::new();
    let mut handled: HashSet<String> = HashSet::new();

    let draft_by_id: HashMap<&str, &DraftInfo> = drafts
        .iter()
        .map(|d| (d.edge.to_event_id.as_str(), d))
        .collect();

    let mut groups: HashMap<&str, Vec<&EventDescriptor>> = HashMap::new();
    for ev in all_events {
        if ev.event_id == master_event_id || secondary_root_ids.contains(&ev.event_id) {
            continue;
        }
        if let Some(key) = ev.source_key.as_deref() {
            if !key.is_empty() {
                groups.entry(key).or_default().push(ev);
            }
        }
    }

    for (key, members) in &groups {
        let (segments, untimed) = split_recorded_segments(members);

        // A group-wide fallback shift, used for clips without recorded timestamps.
        let mut all_anchor_deltas = Vec::new();
        for ev in members {
            if let Some(d) = draft_by_id.get(ev.event_id.as_str()) {
                if is_anchor(d) {
                    let resolved = draft_resolved_start(d, anchor_t0_map, master_t0);
                    all_anchor_deltas.push(resolved - d.timeline_start);
                }
            }
        }
        let fallback_delta = if all_anchor_deltas.is_empty() {
            None
        } else {
            Some(median(&all_anchor_deltas))
        };

        // A group-wide clock, used for runs whose own matches were all too weak.
        let mut group_clock_votes: Vec<f64> = Vec::new();
        let mut all_clusters: Vec<(Vec<ClockCandidate>, f64)> = Vec::new();
        for segment in &segments {
            let segment_clusters = clock_clusters(segment, &draft_by_id, anchor_t0_map, master_t0);
            for (cluster, clock) in &segment_clusters {
                for _ in 0..cluster.len() {
                    group_clock_votes.push(*clock);
                }
            }
            all_clusters.extend(segment_clusters);
        }
        let group_clock = if group_clock_votes.is_empty() {
            None
        } else {
            Some(median(&group_clock_votes))
        };

        // Recorded timestamps are only used for placement in the default mode.
        // Flag a daylight-saving-style clock jump so the operator can switch to the
        // mode that ignores those timestamps instead of trusting a mis-sorted run.
        if !preserve_order {
            if let Some(jump) = detect_clock_jump(&all_clusters) {
                warnings.push(format!(
                    "Source '{}': the recorded clock changes by about {:.0} s between clips (possible daylight-saving change). Enable 'Force audio-only placement (DST)' so placement does not depend on those timestamps.",
                    short_source_name(key),
                    jump
                ));
            }
        }

        for segment in &segments {
            process_segment(
                key,
                segment,
                fallback_delta,
                group_clock,
                preserve_order,
                &draft_by_id,
                master_event_id,
                master_t0,
                anchor_t0_map,
                &mut out,
                &mut handled,
                methods,
                ncc_scores,
                &mut warnings,
            );
        }

        if !untimed.is_empty() {
            process_untimed(
                key,
                &untimed,
                fallback_delta,
                preserve_order,
                &draft_by_id,
                master_event_id,
                master_t0,
                &mut out,
                &mut handled,
                methods,
                ncc_scores,
                &mut warnings,
            );
        }
    }

    // Events that are not part of a reliable group (including folders whose clips
    // are all < MIN_ANCHOR_SPAN) must stand on their own when they have a valid match.
    for d in drafts {
        if handled.contains(&d.edge.to_event_id) {
            continue;
        }

        if d.span_seconds < MIN_INDIVIDUAL_SPAN || d.ncc < NCC_MIN || d.edge.confidence < 0.35 {
            warnings.push(format!(
                "Left '{}' unaligned: audio match too weak (NCC {:.2}, Conf {:.0}%, {:.1}s of audio).",
                d.edge.to_event_id,
                d.ncc,
                d.edge.confidence * 100.0,
                d.span_seconds
            ));
            continue;
        }

        out.push(d.edge.clone());
    }

    (out, warnings)
}

fn is_anchor(d: &DraftInfo) -> bool {
    d.ncc >= NCC_ANCHOR && d.span_seconds >= MIN_ANCHOR_SPAN && d.edge.confidence >= 0.5
}

/// Splits a source into continuous recording runs. A gap longer than
/// `SOURCE_SEGMENT_GAP_SECONDS` starts a new run, which also separates two camera
/// bodies whose clocks disagree but whose files share one folder.
fn split_recorded_segments<'a>(
    members: &[&'a EventDescriptor],
) -> (Vec<Vec<&'a EventDescriptor>>, Vec<&'a EventDescriptor>) {
    let mut timed: Vec<&EventDescriptor> = members
        .iter()
        .filter(|e| e.recorded_time_seconds.is_some())
        .cloned()
        .collect();
    let untimed: Vec<&EventDescriptor> = members
        .iter()
        .filter(|e| e.recorded_time_seconds.is_none())
        .cloned()
        .collect();

    timed.sort_by(|a, b| {
        a.recorded_time_seconds
            .partial_cmp(&b.recorded_time_seconds)
            .unwrap_or(std::cmp::Ordering::Equal)
    });

    let mut segments: Vec<Vec<&EventDescriptor>> = Vec::new();
    let mut current: Vec<&EventDescriptor> = Vec::new();
    let mut last_end: Option<f64> = None;

    for ev in timed {
        let recorded = ev.recorded_time_seconds.unwrap_or(0.0);
        let span = analysis_span(ev, false).1;

        if let Some(end) = last_end {
            if recorded - end > SOURCE_SEGMENT_GAP_SECONDS {
                segments.push(std::mem::take(&mut current));
            }
        }

        current.push(ev);
        let end = recorded + span;
        last_end = Some(match last_end {
            Some(prev) if prev > end => prev,
            _ => end,
        });
    }

    if !current.is_empty() {
        segments.push(current);
    }

    (segments, untimed)
}

#[allow(clippy::too_many_arguments)]
fn clock_clusters(
    members: &[&EventDescriptor],
    draft_by_id: &HashMap<&str, &DraftInfo>,
    anchor_t0_map: &HashMap<String, f64>,
    master_t0: f64,
) -> Vec<(Vec<ClockCandidate>, f64)> {
    let mut candidates: Vec<ClockCandidate> = Vec::new();
    for ev in members {
        if let (Some(recorded), Some(d)) = (ev.recorded_time_seconds, draft_by_id.get(ev.event_id.as_str())) {
            if d.ncc >= NCC_CANDIDATE && d.span_seconds >= MIN_ANCHOR_SPAN && d.edge.confidence >= 0.5 {
                let resolved = draft_resolved_start(d, anchor_t0_map, master_t0);
                candidates.push(ClockCandidate {
                    recorded,
                    clock: recorded - (resolved - d.media_start),
                    delta: resolved - d.timeline_start,
                    ncc: d.ncc,
                    confidence: d.edge.confidence,
                });
            }
        }
    }

    candidates.sort_by(|a, b| a.clock.partial_cmp(&b.clock).unwrap_or(std::cmp::Ordering::Equal));

    let mut clusters: Vec<(Vec<ClockCandidate>, f64)> = Vec::new();
    let mut index = 0usize;
    while index < candidates.len() {
        let mut end = index + 1;
        while end < candidates.len()
            && candidates[end].clock - candidates[index].clock <= CLOCK_CLUSTER_TOLERANCE
        {
            end += 1;
        }

        let cluster: Vec<ClockCandidate> = candidates[index..end].to_vec();
        let max_ncc = cluster.iter().map(|c| c.ncc).fold(0.0f64, f64::max);
        if cluster.len() >= 2 || max_ncc >= NCC_ANCHOR {
            let clock = median(&cluster.iter().map(|c| c.clock).collect::<Vec<_>>());
            clusters.push((cluster, clock));
        }

        index = end;
    }

    clusters.sort_by(|a, b| {
        b.0.len().cmp(&a.0.len()).then_with(|| {
            let a_max = a.0.iter().map(|c| c.ncc).fold(0.0f64, f64::max);
            let b_max = b.0.iter().map(|c| c.ncc).fold(0.0f64, f64::max);
            b_max.partial_cmp(&a_max).unwrap_or(std::cmp::Ordering::Equal)
        })
    });

    clusters
}

/// Returns the separation when two implied clocks differ by roughly one hour.
///
/// A genuine daylight-saving change shifts the camera's wall clock by ~3600 s,
/// which shows up as two anchor clusters whose implied source clocks differ by
/// that amount. A normal recording gap does not change the implied clock, so it
/// does not trigger this check. Two camera bodies sharing one folder could also
/// differ by an hour; the caller only warns in that case (it does not change
/// placement).
fn detect_clock_jump(clusters: &[(Vec<ClockCandidate>, f64)]) -> Option<f64> {
    if clusters.len() < 2 {
        return None;
    }

    let mut separations = Vec::new();
    for i in 0..clusters.len() {
        for j in (i + 1)..clusters.len() {
            let separation = (clusters[i].1 - clusters[j].1).abs();
            if (3300.0..=3900.0).contains(&separation) {
                separations.push(separation);
            }
        }
    }

    if separations.is_empty() {
        None
    } else {
        Some(median(&separations))
    }
}

#[allow(clippy::too_many_arguments)]
fn process_segment(
    key: &str,
    members: &[&EventDescriptor],
    fallback_delta: Option<f64>,
    group_clock: Option<f64>,
    preserve_order: bool,
    draft_by_id: &HashMap<&str, &DraftInfo>,
    master_event_id: &str,
    master_t0: f64,
    anchor_t0_map: &HashMap<String, f64>,
    out: &mut Vec<SyncEdge>,
    handled: &mut HashSet<String>,
    methods: &mut HashMap<String, String>,
    ncc_scores: &mut HashMap<String, f64>,
    warnings: &mut Vec<String>,
) {
    if preserve_order {
        let mut shifted = 0usize;
        for ev in members {
            handled.insert(ev.event_id.clone());
            if let Some(delta) = fallback_delta {
                let target_start = ev.timeline_start_seconds + delta;
                let offset = target_start - master_t0
            - media_seconds_to_timeline(ev, ev.media_start_seconds);
                out.push(SyncEdge {
                    from_event_id: master_event_id.to_string(),
                    to_event_id: ev.event_id.clone(),
                    offset_seconds: offset,
                    drift_ppm: 0.0,
                    confidence: 0.7,
                    split_points_seconds: Vec::new(),
                });
                methods.insert(
                    ev.event_id.clone(),
                    format!("DST mode '{}'", short_source_name(key)),
                );
                ncc_scores.insert(ev.event_id.clone(), NCC_MIN);
                shifted += 1;
            }
        }

        warnings.push(format!(
            "DST mode '{}': {} clip(s) shifted rigidly by {:+.3}s; recorded clock ignored, relative order and gaps preserved.",
            short_source_name(key),
            shifted,
            fallback_delta.unwrap_or(0.0)
        ));
        return;
    }

    struct AnchorInfo {
        recorded: Option<f64>,
        clock: Option<f64>,
        delta: f64,
        ncc: f64,
        confidence: f64,
    }

    let clusters = clock_clusters(members, draft_by_id, anchor_t0_map, master_t0);

    // If one clock clearly dominates this continuous recording run, use it for
    // short/ambiguous clips in the run. Only override an existing anchor when the
    // dominant cluster has strong consensus and the disagreeing anchor is weak-to-moderate,
    // so two cameras with genuine strong audio matches sharing a folder are not conflated.
    let (segment_clock, strong_consensus): (Option<f64>, bool) = match clusters.first() {
        Some((best, clock)) if best.len() >= 2 => {
            let second = clusters.get(1).map(|c| c.0.len()).unwrap_or(0);
            if best.len() > second {
                let strong = (best.len() >= 3 && best.len() >= second * 2) || second == 0;
                (Some(*clock), strong)
            } else {
                (None, false)
            }
        }
        _ => (None, false),
    };

    let mut anchors: Vec<AnchorInfo> = Vec::new();
    for (cluster, clock) in &clusters {
        for c in cluster {
            anchors.push(AnchorInfo {
                recorded: Some(c.recorded),
                clock: Some(*clock),
                delta: c.delta,
                ncc: c.ncc,
                confidence: c.confidence,
            });
        }
    }

    // Fall back to the plain per-event anchors when no timestamps are available.
    if anchors.is_empty() {
        for ev in members {
            if let Some(d) = draft_by_id.get(ev.event_id.as_str()) {
                if is_anchor(d) {
                    let resolved = draft_resolved_start(d, anchor_t0_map, master_t0);
                    anchors.push(AnchorInfo {
                        recorded: None,
                        clock: None,
                        delta: resolved - d.timeline_start,
                        ncc: d.ncc,
                        confidence: d.edge.confidence,
                    });
                }
            }
        }
    }

    let median_delta = if anchors.is_empty() {
        fallback_delta
    } else {
        Some(median(&anchors.iter().map(|a| a.delta).collect::<Vec<_>>()))
    };
    let med_ncc = if anchors.is_empty() {
        NCC_ANCHOR
    } else {
        median(&anchors.iter().map(|a| a.ncc).collect::<Vec<_>>())
    };
    let med_conf = if anchors.is_empty() {
        0.7
    } else {
        median(&anchors.iter().map(|a| a.confidence).collect::<Vec<_>>())
    };

    let timed_anchors: Vec<&AnchorInfo> = anchors
        .iter()
        .filter(|a| a.recorded.is_some() && a.clock.is_some())
        .collect();

    let mut synthesized = 0usize;
    let mut overridden = 0usize;
    let mut absolute = 0usize;

    for ev in members.iter() {
        let predicted_absolute = match ev.recorded_time_seconds {
            Some(rec) => {
                if let Some(clock) = segment_clock {
                    Some((rec - clock) + media_seconds_to_timeline(ev, ev.media_start_seconds))
                } else if !timed_anchors.is_empty() {
                    let mut best: Option<(f64, f64)> = None;
                    for a in &timed_anchors {
                        let candidate = a.recorded.unwrap_or(0.0);
                        let distance = (candidate - rec).abs();
                        if best.map(|b| distance < b.0).unwrap_or(true) {
                            best = Some((distance, a.clock.unwrap_or(0.0)));
                        }
                    }
                    best.map(|(_, clock)| {
                        (rec - clock) + media_seconds_to_timeline(ev, ev.media_start_seconds)
                    })
                } else {
                    group_clock.map(|clock| {
                        (rec - clock) + media_seconds_to_timeline(ev, ev.media_start_seconds)
                    })
                }
            }
            None => None,
        };

        if let Some(d) = draft_by_id.get(ev.event_id.as_str()) {
            let own_resolved = draft_resolved_start(d, anchor_t0_map, master_t0);

            if is_anchor(d) {
                let agrees = match predicted_absolute {
                    Some(pred) => {
                        (own_resolved - pred).abs() <= OUTLIER_TOLERANCE
                            || !strong_consensus
                            || d.ncc >= 0.75
                    }
                    None => true,
                };

                if agrees {
                    handled.insert(ev.event_id.clone());
                    out.push(d.edge.clone());
                    continue;
                }

                overridden += 1;
            } else if predicted_absolute.is_none()
                && median_delta.is_none()
                && d.span_seconds >= MIN_INDIVIDUAL_SPAN
                && d.ncc >= NCC_MIN
                && d.edge.confidence >= 0.35
            {
                // Folder had no >=15s anchor: let strong short/medium clips align individually.
                continue;
            } else if predicted_absolute.is_some()
                && !strong_consensus
                && d.span_seconds >= MIN_INDIVIDUAL_SPAN
                && d.ncc >= NCC_ANCHOR
                && d.edge.confidence >= 0.60
            {
                handled.insert(ev.event_id.clone());
                out.push(d.edge.clone());
                continue;
            }
        }

        let target_start = predicted_absolute
            .or_else(|| median_delta.map(|d| ev.timeline_start_seconds + d));

        let target_start = match target_start {
            Some(t) => t,
            None => continue,
        };

        handled.insert(ev.event_id.clone());
        if predicted_absolute.is_some() {
            absolute += 1;
        }

        let offset = target_start - master_t0
            - media_seconds_to_timeline(ev, ev.media_start_seconds);
        out.push(SyncEdge {
            from_event_id: master_event_id.to_string(),
            to_event_id: ev.event_id.clone(),
            offset_seconds: offset,
            drift_ppm: 0.0,
            confidence: med_conf.max(0.5),
            split_points_seconds: Vec::new(),
        });
        methods.insert(
            ev.event_id.clone(),
            if predicted_absolute.is_some() {
                format!("source clock '{}'", short_source_name(key))
            } else {
                format!("source group '{}'", short_source_name(key))
            },
        );
        ncc_scores.insert(ev.event_id.clone(), med_ncc);
        synthesized += 1;
    }

    if !anchors.is_empty() {
        warnings.push(format!(
            "Source '{}': {} clip(s) in this run, {} reliable anchor(s); {} positioned by recorded time, {} inherited the group shift, {} outlier(s) corrected.",
            short_source_name(key),
            members.len(),
            anchors.len(),
            absolute,
            synthesized - absolute,
            overridden
        ));
    }
}

#[allow(clippy::too_many_arguments)]
fn process_untimed(
    key: &str,
    members: &[&EventDescriptor],
    fallback_delta: Option<f64>,
    preserve_order: bool,
    draft_by_id: &HashMap<&str, &DraftInfo>,
    master_event_id: &str,
    master_t0: f64,
    out: &mut Vec<SyncEdge>,
    handled: &mut HashSet<String>,
    methods: &mut HashMap<String, String>,
    ncc_scores: &mut HashMap<String, f64>,
    warnings: &mut Vec<String>,
) {
    if preserve_order {
        let mut shifted = 0usize;
        for ev in members {
            handled.insert(ev.event_id.clone());
            if let Some(delta) = fallback_delta {
                let target_start = ev.timeline_start_seconds + delta;
                let offset = target_start - master_t0
            - media_seconds_to_timeline(ev, ev.media_start_seconds);
                out.push(SyncEdge {
                    from_event_id: master_event_id.to_string(),
                    to_event_id: ev.event_id.clone(),
                    offset_seconds: offset,
                    drift_ppm: 0.0,
                    confidence: 0.7,
                    split_points_seconds: Vec::new(),
                });
                methods.insert(
                    ev.event_id.clone(),
                    format!("DST mode '{}'", short_source_name(key)),
                );
                ncc_scores.insert(ev.event_id.clone(), NCC_MIN);
                shifted += 1;
            }
        }

        warnings.push(format!(
            "DST mode '{}': {} untimed clip(s) shifted rigidly by {:+.3}s.",
            short_source_name(key),
            shifted,
            fallback_delta.unwrap_or(0.0)
        ));
        return;
    }

    for ev in members {
        if let Some(d) = draft_by_id.get(ev.event_id.as_str()) {
            if is_anchor(d)
                || (d.span_seconds >= MIN_INDIVIDUAL_SPAN
                    && d.ncc >= NCC_MIN
                    && d.edge.confidence >= 0.45)
            {
                handled.insert(ev.event_id.clone());
                out.push(d.edge.clone());
                continue;
            }
        }

        let target_start = match fallback_delta {
            Some(delta) => ev.timeline_start_seconds + delta,
            None => {
                // Let the standalone loop report any remaining weak draft warnings once.
                if !draft_by_id.contains_key(ev.event_id.as_str()) {
                    handled.insert(ev.event_id.clone());
                    warnings.push(format!(
                        "Left '{}' unaligned: no recorded timestamp and no reliable anchor for its source.",
                        ev.event_id
                    ));
                }
                continue;
            }
        };

        handled.insert(ev.event_id.clone());
        let offset = target_start - master_t0
            - media_seconds_to_timeline(ev, ev.media_start_seconds);
        out.push(SyncEdge {
            from_event_id: master_event_id.to_string(),
            to_event_id: ev.event_id.clone(),
            offset_seconds: offset,
            drift_ppm: 0.0,
            confidence: 0.7,
            split_points_seconds: Vec::new(),
        });
        methods.insert(ev.event_id.clone(), format!("source group '{}'", short_source_name(key)));
        ncc_scores.insert(ev.event_id.clone(), NCC_ANCHOR);
    }
}

fn median(values: &[f64]) -> f64 {
    if values.is_empty() {
        return 0.0;
    }
    let mut sorted = values.to_vec();
    sorted.sort_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal));
    let n = sorted.len();
    if n % 2 == 1 {
        sorted[n / 2]
    } else {
        (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0
    }
}

fn short_source_name(key: &str) -> String {
    key.replace('/', "\\")
        .rsplit('\\')
        .next()
        .unwrap_or(key)
        .to_string()
}

fn analyze_event(
    master: &PreparedEvent,
    ev: &EventDescriptor,
    request: &SyncTaskRequest,
    target_rate: u32,
    gcc: &GccPhat,
    reporter: &ProgressReporter,
) -> EdgeOutcome {
    let file_label = short_file_name(&ev.file_path);
    let slave_progress = |frac: f32| -> bool {
        let frac = frac.clamp(0.0, 1.0);
        reporter.report(
            0.20,
            &format!("Analyzing {} ({:.0}%)", file_label, frac * 100.0),
        )
    };

    let slave = match prepare_event(
        ev,
        false,
        request,
        target_rate,
        Some(&slave_progress),
    ) {
        Ok(p) => p,
        Err(e) if e == CANCELLED => return EdgeOutcome::cancelled(),
        Err(e) => {
            return EdgeOutcome {
                edge: None,
                method: None,
                ncc_score: None,
                split_points: Vec::new(),
                warning: Some(format!("Skipped '{}': {}", ev.event_id, e)),
                cancelled: false,
            }
        }
    };

    match analyze_pair(master, &slave, request, target_rate, gcc) {
        Ok(pair) => {
            // Buffer-space lag -> media-space T0 offset. Convert each side's media
            // position with its own playback rate so a retimed master or slave does
            // not distort the timeline delta.
            let bias = master.span_start_media / playback_rate_or_one(&master.event)
                - slave.span_start_media / playback_rate_or_one(ev);
            let offset_seconds = pair.lag_seconds / playback_rate_or_one(ev) + bias;
            let split_points_seconds = pair
                .split_points
                .iter()
                .map(|sp| sp.timestamp_seconds)
                .collect();

            EdgeOutcome {
                edge: Some(SyncEdge {
                    from_event_id: master.event.event_id.clone(),
                    to_event_id: ev.event_id.clone(),
                    offset_seconds,
                    drift_ppm: pair.drift_ppm,
                    confidence: pair.confidence,
                    split_points_seconds,
                }),
                method: Some(pair.method.to_string()),
                ncc_score: Some(if slave.is_synthetic { 1.0 } else { pair.ncc_score }),
                split_points: pair.split_points,
                warning: None,
                cancelled: false,
            }
        }
        Err(e) => EdgeOutcome {
            edge: None,
            method: None,
            ncc_score: None,
            split_points: Vec::new(),
            warning: Some(format!("Skipped '{}': {}", ev.event_id, e)),
            cancelled: false,
        },
    }
}

pub(crate) fn analyze_pair(
    master: &PreparedEvent,
    slave: &PreparedEvent,
    request: &SyncTaskRequest,
    target_rate: u32,
    gcc: &GccPhat,
) -> Result<PairAnalysis, String> {
    let rate = target_rate as f64;
    let ffmpeg = request.options.ffmpeg_path.as_deref();

    // Envelope similarity is a much more trustworthy match indicator than the
    // GCC-PHAT peak sharpness (which is high even for unrelated short clips).
    let coarse = CoarseAligner::find_envelope_lag(
        &master.envelope,
        &slave.envelope,
        ENVELOPE_HOP_SIZE,
    );
    let ncc_score = coarse.map(|c| c.score).unwrap_or(0.0);

    // 1. Either both events are short enough to correlate directly, or we go through
    //    the bounded envelope + windowed refinement path.
    if let (Some(m), Some(s)) = (master.samples.as_deref(), slave.samples.as_deref()) {
        if m.len() + s.len() <= MAX_CORRELATION_INPUT_SAMPLES {
            return correlate_direct(slave, m, s, request, target_rate, gcc, ncc_score);
        }
    }

    // 2. Coarse alignment on the compact envelopes (cheap even for hours of audio).
    let coarse_lag = match coarse {
        Some(c) => c.lag_samples,
        None => return Err("audio is too short to build an energy envelope".into()),
    };

    let m_len = master.buffer_len(target_rate) as isize;
    let s_len = slave.buffer_len(target_rate) as isize;

    // Overlap region expressed in slave buffer coordinates.
    let overlap_start = 0.max(-coarse_lag);
    let overlap_end = s_len.min(m_len - coarse_lag);
    let overlap_len = overlap_end - overlap_start;

    if overlap_len < (MIN_OVERLAP_SECONDS * rate) as isize {
        return Err("no overlapping audio between master and this event".into());
    }

    // 3. Materialize one bounded window pair that is guaranteed to overlap. Both
    //    windows are at most DRIFT_SPAN_SECONDS long, so memory stays flat.
    let window_len = overlap_len.min((DRIFT_SPAN_SECONDS * rate) as isize);
    let v0 = overlap_start + (overlap_len - window_len) / 2;
    let u0 = v0 + coarse_lag;

    let mut slave_window = slave.fetch(v0.max(0) as usize, window_len as usize, target_rate, ffmpeg)?;
    let mut master_window = master.fetch(u0.max(0) as usize, window_len as usize, target_rate, ffmpeg)?;

    if request.options.voice_activity_filter {
        crate::audio::DspProcessor::apply_voice_activity_filter(&mut slave_window, 512, -40.0);
        crate::audio::DspProcessor::apply_voice_activity_filter(&mut master_window, 512, -40.0);
    }

    let win_len = slave_window.len().min(master_window.len());
    if win_len < (MIN_OVERLAP_SECONDS * rate) as usize {
        return Err("overlapping audio window was too short".into());
    }
    let slave_window = &slave_window[..win_len];
    let master_window = &master_window[..win_len];

    // 4. Precision GCC-PHAT constrained around the coarse lag (+/- 0.75 s) so
    //    fine refinement cannot jump to an unrelated distant peak. If the center
    //    window is quiet, probe earlier and later slices of the overlap.
    let base_lag = (u0 - v0) as f64;
    let mut lag_samples = coarse_lag as f64;
    let mut confidence = 0.0f64;
    let mut method = "envelope + windowed GCC-PHAT";
    let fine_search_radius = Some((0.75 * rate) as usize);

    let fine_len = ((FINE_WINDOW_SECONDS * rate) as usize).min(win_len);
    if fine_len >= 2048 {
        let max_off = win_len - fine_len;
        let mut candidate_offsets = vec![max_off / 2];
        if max_off >= fine_len / 2 {
            candidate_offsets.push(max_off / 4);
            candidate_offsets.push((max_off * 3) / 4);
        }

        for fine_off in candidate_offsets {
            let peak = gcc.correlate_with_options(
                &master_window[fine_off..fine_off + fine_len],
                &slave_window[fine_off..fine_off + fine_len],
                request.options.sub_sample_accuracy,
                fine_search_radius,
            )?;
            if peak.confidence > confidence {
                lag_samples = base_lag + peak.exact_lag;
                confidence = peak.confidence;
            }
            if confidence >= 0.45 {
                break;
            }
        }
    }

    // 5. Fingerprint fallback for noisy/reverberant wedding audio.
    if confidence < 0.40 {
        let fp_len = ((FINGERPRINT_WINDOW_SECONDS * rate) as usize).min(win_len);
        if fp_len >= 4096 {
            let off = (win_len - fp_len) / 2;
            if let Some(fp) = AcousticFingerprinter::match_fingerprints_with_gcc(
                &master_window[off..off + fp_len],
                &slave_window[off..off + fp_len],
                target_rate,
                gcc,
                request.options.sub_sample_accuracy,
                fine_search_radius,
            ) {
                if fp.confidence > confidence {
                    lag_samples = base_lag + fp.exact_lag;
                    confidence = fp.confidence * 0.90;
                    method = "acoustic fingerprint fallback";
                }
            }
        }
    }

    // 6. Reject physically impossible placements: the refined lag must still leave
    //    a meaningful overlap between the two buffers.
    let final_lag = lag_samples.round() as isize;
    let final_overlap = s_len.min(m_len - final_lag) - 0.max(-final_lag);
    if final_overlap < (MIN_OVERLAP_SECONDS * rate) as isize {
        return Err("refined alignment fell outside the master recording".into());
    }

    // 7. Drift + discontinuity analysis over the bounded window pair.
    let (drift_ppm, split_points) = analyze_drift(
        master_window,
        slave_window,
        v0.max(0) as f64 / rate,
        lag_samples.round() as isize,
        slave,
        request,
        target_rate,
        gcc,
    );

    Ok(PairAnalysis {
        lag_seconds: lag_samples / rate,
        confidence,
        drift_ppm,
        split_points,
        method,
        ncc_score,
    })
}

fn correlate_direct(
    slave: &PreparedEvent,
    m: &[f32],
    s: &[f32],
    request: &SyncTaskRequest,
    target_rate: u32,
    gcc: &GccPhat,
    ncc_score: f64,
) -> Result<PairAnalysis, String> {
    let rate = target_rate as f64;
    let mut peak = gcc.correlate_with_options(m, s, request.options.sub_sample_accuracy, None)?;
    let mut method = "GCC-PHAT direct";

    if peak.confidence < 0.40 {
        if let Some(fp) = AcousticFingerprinter::match_fingerprints_with_gcc(
            m,
            s,
            target_rate,
            gcc,
            request.options.sub_sample_accuracy,
            None,
        ) {
            if fp.confidence > peak.confidence {
                peak = fp;
                method = "acoustic fingerprint fallback";
            }
        }
    }

    let lag_samples = peak.exact_lag;

    let (drift_ppm, split_points) = analyze_drift(
        m,
        s,
        0.0,
        peak.integer_lag,
        slave,
        request,
        target_rate,
        gcc,
    );

    Ok(PairAnalysis {
        lag_seconds: lag_samples / rate,
        confidence: peak.confidence,
        drift_ppm,
        split_points,
        method,
        ncc_score,
    })
}

/// Rolling-window drift estimation and discontinuity detection.
///
/// `base_lag` is the integer lag (in samples) between the two supplied buffers,
/// i.e. `master[i]` corresponds to `slave[i - base_lag]`. `time_base_seconds` is
/// the slave-buffer time of index 0 of the supplied window.
#[allow(clippy::too_many_arguments)]
fn analyze_drift(
    master: &[f32],
    slave: &[f32],
    time_base_seconds: f64,
    base_lag: isize,
    slave_event: &PreparedEvent,
    request: &SyncTaskRequest,
    target_rate: u32,
    gcc: &GccPhat,
) -> (f64, Vec<SplitPoint>) {
    // Discontinuity-only runs still need the observation loop; only the drift
    // regression is gated on `detect_drift`.
    if !request.options.detect_drift && !request.options.detect_discontinuities {
        return (0.0, Vec::new());
    }

    let rate = target_rate as f64;
    let window = (DRIFT_WINDOW_SECONDS * rate) as usize;
    let m_len = master.len();
    let s_len = slave.len();

    if s_len < window || m_len < window {
        return (0.0, Vec::new());
    }

    // Always bound the number of analysed windows regardless of media duration.
    let step = ((window / 2).max((s_len - window) / DRIFT_MAX_OBSERVATIONS.max(1))).max(1);

    let mut observations: Vec<DriftObservation> = Vec::new();
    let mut offset = 0usize;

    while offset + window <= s_len {
        let master_offset = offset as isize + base_lag;
        if master_offset >= 0 && (master_offset as usize) + window <= m_len {
            let m_slice = &master[master_offset as usize..master_offset as usize + window];
            let s_slice = &slave[offset..offset + window];

            if let Ok(peak) = gcc.correlate_with_options(
                m_slice,
                s_slice,
                request.options.sub_sample_accuracy,
                None,
            ) {
                if peak.confidence >= 0.30 {
                    observations.push(DriftObservation {
                        time_seconds: time_base_seconds + offset as f64 / rate,
                        delay_seconds: (base_lag as f64 + peak.exact_lag) / rate,
                        confidence: peak.confidence,
                    });
                }
            }
        }
        offset += step;
    }

    // Event-relative offset of buffer index 0 (0 for normal takes).
    let buffer_to_event = slave_event.span_start_media - slave_event.event.media_start_seconds;

    if observations.len() < 2 {
        return (0.0, Vec::new());
    }

    // Drift needs a meaningful number of observations; short camera clips are not
    // time-stretched based on three noisy samples.
    let residual_threshold = (request.options.discontinuity_threshold_ms / 1000.0).max(0.001);
    let mut drift_ppm = 0.0;
    let mut split_obs = observations.clone();
    if observations.len() >= 5 {
        if let Some(ransac) = RansacRegression::fit(&observations, 150, residual_threshold, 0.5) {
            // Only stretch the timeline when drift correction is enabled. The fit
            // still contributes its inlier set to de-noise discontinuity detection.
            if request.options.detect_drift && ransac.r_squared >= 0.5 {
                let limit = request.options.max_drift_ppm.abs().clamp(1.0, 100_000.0);
                drift_ppm = ransac.drift_ppm.clamp(-limit, limit);
            }
            if ransac.inliers.len() >= 3 {
                split_obs = ransac.inliers.iter().map(|&idx| observations[idx]).collect();
                split_obs.sort_by(|a, b| {
                    a.time_seconds
                        .partial_cmp(&b.time_seconds)
                        .unwrap_or(std::cmp::Ordering::Equal)
                });
            }
        }
    }

    let mut split_points = Vec::new();
    if request.options.detect_discontinuities {
        // Split times are measured in decoded media seconds; convert to timeline
        // seconds using the event's existing playback rate before placing them.
        let slave_rate = playback_rate_or_one(&slave_event.event);
        for d in DiscontinuityDetector::detect_step_jumps(&split_obs, residual_threshold) {
            split_points.push(SplitPoint {
                timestamp_seconds: slave_event.event.timeline_start_seconds
                    + (buffer_to_event + d.timestamp_seconds) / slave_rate,
                step_jump_seconds: d.step_jump_seconds / slave_rate,
            });
        }
    }

    (drift_ppm, split_points)
}

pub(crate) fn prepare_event(
    ev: &EventDescriptor,
    is_master: bool,
    request: &SyncTaskRequest,
    target_rate: u32,
    progress: Option<&(dyn Fn(f32) -> bool + Sync)>,
) -> Result<PreparedEvent, String> {
    let (span_start, span_len) = analysis_span(ev, is_master);

    let clean_path = normalize_path(&ev.file_path);

    let path_exists = !clean_path.is_empty() && Path::new(&clean_path).exists();

    if !path_exists {
        if is_mock_path(&clean_path) {
            let samples = generate_synthetic_buffer(span_len, target_rate);
            let envelope = CoarseAligner::compute_energy_envelope(
                &samples,
                ENVELOPE_FRAME_SIZE,
                ENVELOPE_HOP_SIZE,
            );
            return Ok(PreparedEvent {
                event: ev.clone(),
                span_start_media: span_start,
                span_len_seconds: span_len,
                samples: Some(samples),
                envelope,
                is_synthetic: true,
            });
        }

        return Err(format!(
            "media file does not exist on disk: '{}'",
            clean_path
        ));
    }

    if span_len <= 0.5 {
        return Err("event is too short to analyze".into());
    }

    if span_len <= RETAIN_SAMPLES_SECONDS {
        let mut samples = AudioDecoder::load_and_condition_with_progress_for_stream(
            &clean_path,
            ev.stream_index,
            Some(span_start),
            Some(span_len),
            target_rate,
            request.options.ffmpeg_path.as_deref(),
            progress,
        )?;

        if samples.is_empty() {
            return Err("decoded audio buffer was empty".into());
        }

        // Build the envelope from the ungated samples so short and long takes use
        // identical envelope conditioning; VAD is applied afterwards and only
        // affects the GCC-PHAT waveform correlation.
        let envelope = CoarseAligner::compute_energy_envelope(
            &samples,
            ENVELOPE_FRAME_SIZE,
            ENVELOPE_HOP_SIZE,
        );

        if request.options.voice_activity_filter {
            crate::audio::DspProcessor::apply_voice_activity_filter(&mut samples, 512, -40.0);
        }

        return Ok(PreparedEvent {
            event: ev.clone(),
            span_start_media: span_start,
            span_len_seconds: span_len,
            samples: Some(samples),
            envelope,
            is_synthetic: false,
        });
    }

    // Long take: stream the audio through an envelope builder so memory stays flat.
    let envelope = AudioDecoder::scan_energy_envelope_for_stream(
        &clean_path,
        ev.stream_index,
        Some(span_start),
        Some(span_len),
        target_rate,
        request.options.ffmpeg_path.as_deref(),
        ENVELOPE_FRAME_SIZE,
        ENVELOPE_HOP_SIZE,
        progress,
    )?;

    Ok(PreparedEvent {
        event: ev.clone(),
        span_start_media: span_start,
        span_len_seconds: span_len,
        samples: None,
        envelope,
        is_synthetic: false,
    })
}

/// Returns `(media_start_seconds, analyzed_length_seconds)` for an event.
///
/// The master reference analyzes the whole available take so that camera clips
/// anywhere inside a long recorder file can be located. Slaves only analyze the
/// portion that is actually on the timeline.
pub(crate) fn analysis_span(ev: &EventDescriptor, is_master: bool) -> (f64, f64) {
    let rate = playback_rate_or_one(ev);
    let start = ev.media_start_seconds.max(0.0);

    // `timeline_length_seconds` is in timeline seconds; decoding, envelopes and
    // correlations all work in source-media seconds, so scale by the playback
    // rate (media span = timeline span * rate).
    let timeline_span_media = ev.timeline_length_seconds * rate;

    let available = if ev.media_length_seconds > 0.5 {
        ev.media_length_seconds
    } else {
        timeline_span_media
    };

    let len = if is_master {
        available
    } else {
        timeline_span_media.max(0.5).min(available.max(0.5))
    };

    (start, len.clamp(0.5, MAX_ANALYSIS_SECONDS))
}

/// VEGAS playback rate with defensive normalization (non-finite/<=0 -> 1.0).
pub(crate) fn playback_rate_or_one(ev: &EventDescriptor) -> f64 {
    let rate = ev.playback_rate;
    if rate.is_finite() && rate > 0.0 {
        rate
    } else {
        1.0
    }
}

/// Converts a source-media duration into timeline seconds at the event's rate.
fn media_seconds_to_timeline(ev: &EventDescriptor, seconds: f64) -> f64 {
    seconds / playback_rate_or_one(ev)
}

pub(crate) fn master_span_score(ev: &EventDescriptor) -> f64 {
    if ev.media_length_seconds > 0.5 {
        ev.media_length_seconds
    } else {
        ev.timeline_length_seconds.max(0.5)
    }
}

pub(crate) fn choose_master<'a>(
    request: &SyncTaskRequest,
    all_events: &'a [EventDescriptor],
) -> &'a EventDescriptor {
    if let Some(track_id) = request.master_track_id.as_ref() {
        let in_track: Vec<&EventDescriptor> = all_events
            .iter()
            .filter(|e| &e.track_id == track_id)
            .collect();
        if let Some(best) = in_track
            .iter()
            .copied()
            .filter(|e| !e.is_muted)
            .max_by(|a, b| {
                master_span_score(a)
                    .partial_cmp(&master_span_score(b))
                    .unwrap_or(std::cmp::Ordering::Equal)
            })
            .or_else(|| {
                in_track.into_iter().max_by(|a, b| {
                    master_span_score(a)
                        .partial_cmp(&master_span_score(b))
                        .unwrap_or(std::cmp::Ordering::Equal)
                })
            })
        {
            return best;
        }
    }

    all_events
        .iter()
        .filter(|e| !e.is_muted)
        .max_by(|a, b| {
            let sa = master_span_score(a);
            let sb = master_span_score(b);
            sa.partial_cmp(&sb).unwrap_or(std::cmp::Ordering::Equal)
        })
        .or_else(|| {
            all_events.iter().max_by(|a, b| {
                let sa = master_span_score(a);
                let sb = master_span_score(b);
                sa.partial_cmp(&sb).unwrap_or(std::cmp::Ordering::Equal)
            })
        })
        .unwrap_or(&all_events[0])
}

pub(crate) fn normalize_path(path: &str) -> String {
    path.trim_matches('"').replace('/', "\\")
}

fn is_mock_path(clean_path: &str) -> bool {
    clean_path.is_empty()
        || clean_path.starts_with("test://")
        || clean_path.starts_with("mock://")
        || clean_path.starts_with("test:\\\\")
        || clean_path.starts_with("mock:\\\\")
}

fn short_file_name(path: &str) -> String {
    let normalized = path.replace('\\', "/");
    normalized
        .rsplit('/')
        .next()
        .unwrap_or(path)
        .trim_matches('"')
        .to_string()
}

fn format_duration(seconds: f64) -> String {
    let total = seconds.max(0.0) as u64;
    let h = total / 3600;
    let m = (total % 3600) / 60;
    let s = total % 60;
    if h > 0 {
        format!("{}h{:02}m", h, m)
    } else if m > 0 {
        format!("{}m{:02}s", m, s)
    } else {
        format!("{}s", s)
    }
}

fn generate_synthetic_buffer(duration_seconds: f64, sample_rate: u32) -> Vec<f32> {
    let num_samples = ((duration_seconds.max(0.1) * sample_rate as f64) as usize).max(1);
    let mut buffer = Vec::with_capacity(num_samples);
    for i in 0..num_samples {
        let t = i as f32 / sample_rate as f32;
        let sample = 0.6 * (2.0 * std::f32::consts::PI * 440.0 * t).sin()
            + 0.3 * (2.0 * std::f32::consts::PI * 880.0 * t).sin()
            + 0.1 * (2.0 * std::f32::consts::PI * 1760.0 * t).sin();
        buffer.push(sample);
    }
    buffer
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::models::SyncOptions;

    fn ev(id: &str, track: &str, timeline_len: f64, media_len: f64) -> EventDescriptor {
        EventDescriptor {
            event_id: id.into(),
            track_id: track.into(),
            file_path: "mock://media".into(),
            stream_index: 0,
            timeline_start_seconds: 0.0,
            timeline_length_seconds: timeline_len,
            media_start_seconds: 0.0,
            media_length_seconds: media_len,
            is_muted: false,
            source_key: None,
            recorded_time_seconds: None,
            ..EventDescriptor::default()
        }
    }

    fn request_with_master(track: Option<&str>) -> SyncTaskRequest {
        SyncTaskRequest {
            project_sample_rate: 48000,
            master_track_id: track.map(|s| s.to_string()),
            tracks: Vec::new(),
            groups: Vec::new(),
            options: SyncOptions::default(),
        }
    }

    #[test]
    fn test_selected_master_track_uses_longest_take_on_that_track() {
        // Auto would pick the 2 hour event on Track_A, but the operator selected
        // Track_B, whose longest take is 30 minutes.
        let events = vec![
            ev("A_long", "Track_A", 7200.0, 7200.0),
            ev("B_short", "Track_B", 60.0, 60.0),
            ev("B_long", "Track_B", 1800.0, 1800.0),
        ];

        let auto = choose_master(&request_with_master(None), &events);
        assert_eq!(auto.event_id, "A_long");

        let selected = choose_master(&request_with_master(Some("Track_B")), &events);
        assert_eq!(selected.event_id, "B_long");
    }

    #[test]
    fn test_unknown_master_track_falls_back_to_auto() {
        let events = vec![
            ev("A_long", "Track_A", 7200.0, 7200.0),
            ev("B_long", "Track_B", 1800.0, 1800.0),
        ];

        let chosen = choose_master(&request_with_master(Some("Track_Missing")), &events);
        assert_eq!(chosen.event_id, "A_long");
    }

    fn clock_candidate() -> ClockCandidate {
        ClockCandidate {
            recorded: 0.0,
            clock: 0.0,
            delta: 0.0,
            ncc: 1.0,
            confidence: 1.0,
        }
    }

    #[test]
    fn test_detect_clock_jump_flags_one_hour_separation() {
        let clusters = vec![
            (vec![clock_candidate()], 1_000.0),
            (vec![clock_candidate()], 4_600.0),
        ];
        let jump = detect_clock_jump(&clusters).expect("one-hour jump should be detected");
        assert!((jump - 3600.0).abs() < 1.0);
    }

    #[test]
    fn test_detect_clock_jump_ignores_ordinary_gaps() {
        let clusters = vec![
            (vec![clock_candidate()], 1_000.0),
            (vec![clock_candidate()], 2_800.0),
        ];
        assert!(detect_clock_jump(&clusters).is_none());
    }

    #[test]
    fn test_detect_clock_jump_needs_two_clusters() {
        let clusters = vec![(vec![clock_candidate()], 1_000.0)];
        assert!(detect_clock_jump(&clusters).is_none());
    }
}
