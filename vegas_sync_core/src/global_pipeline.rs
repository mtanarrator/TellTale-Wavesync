//! Global matching pipeline (Milestone B).
//!
//! Instead of anchoring every clip to one master take, this module measures a
//! sparse set of audio links between temporally plausible event pairs, adds
//! ordering links between consecutive clips of the same device, and solves all
//! positions at once with [`crate::global_solver`].
//!
//! # Deliberate limits
//!
//! * At most [`MAX_GLOBAL_EVENTS`] unique media paths and roughly
//!   [`MAX_GLOBAL_MEMORY_BYTES`] of retained PCM. Beyond that the caller falls
//!   back to the legacy pipeline.
//! * Only temporally plausible pairs are scored (timeline proximity, clock
//!   proximity or consecutive clips of one device), and at most
//!   [`MAX_CANDIDATES_PER_EVENT`] survive per event, so the expensive
//!   correlation stage never runs on the full N² set.
//! * A global result needs a genuinely covered audio graph (see
//!   [`graph_is_usable`]). Sequence-only or sparse graphs fall back to the
//!   legacy pipeline.
//!
//! # Coordinate convention
//!
//! Every solver node is the **timeline start** of one event (the value the host
//! applies). An audio constraint `from -> to` stores the ideal value
//! `start_to - start_from`, converted from the correlation lag with the same T0
//! math the legacy engine uses:
//!
//! ```text
//! offset = (span_start_from - media_start_from) / rate_from
//!        - (span_start_to   - media_start_to)   / rate_to
//!        + lag_seconds / rate_to
//! ```
//!
//! The pure constraint/output helpers ([`build_plan`], [`solve_positions`],
//! [`build_results`], [`graph_is_usable`]) are split out from media I/O so the
//! graph behaviour can be unit-tested without decoding audio. That is deliberate:
//! `mock://` media all decode to the same synthetic tone, so a mock-path chain
//! cannot express "A↔B and B↔C match but A↔C does not"; the WAV integration test
//! covers the real correlation path instead.
//!
//! # Why the solve runs twice
//!
//! The final solve uses the contract weights (priors 0.02, Huber delta 0.25 s).
//! With raw current timeline starts as priors, a component whose clips are still
//! hundreds of seconds out of place would have its internal spacing pulled by
//! seconds; the robust pass would then treat the correct audio edges as outliers
//! and collapse the component. A first, prior-free solve therefore recovers the
//! relative layout, and the priors are centred on that layout (one translation
//! per component: the pin's current position, or the median current position).
//! The final solve keeps the specified weights and has zero edge residual, so
//! the robust pass is stable and the block still lands where the editor left it.

use std::cmp::Ordering;
use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use rayon::prelude::*;

use crate::audio::decoder::CANCELLED;
use crate::clock::{self, TimeInfo};
use crate::correlation::coarse::ENVELOPE_HOP_SIZE;
use crate::correlation::{CoarseAligner, GccPhat};
use crate::engine::{
    analysis_span, analyze_pair, choose_master, normalize_path, playback_rate_or_one,
    prepare_event, PreparedEvent, RETAIN_SAMPLES_SECONDS,
};
use crate::ffi::ProgressReporter;
use crate::global_solver::{self, Constraint, ConstraintKind, Node, SolveResult};
use crate::models::{EventDescriptor, EventSyncResult, SplitPoint, SyncTaskRequest};

/// Hard ceiling on the number of unique media paths the global path attempts.
pub(crate) const MAX_GLOBAL_EVENTS: usize = 400;

/// Hard ceiling on the estimated retained PCM (~512 MB).
pub(crate) const MAX_GLOBAL_MEMORY_BYTES: usize = 512 * 1024 * 1024;

/// Per-event candidate cap after cheap envelope scoring.
const MAX_CANDIDATES_PER_EVENT: usize = 8;

/// Per-event cap before the cheap envelope scan, ranked by time proximity.
const PRESCAN_PER_EVENT: usize = 16;

/// Number of longest takes reserved as candidate hubs for every event.
const HUB_SLOTS: usize = 2;

/// Current timeline spans closer than this are temporally plausible.
const TIMELINE_WINDOW_SECONDS: f64 = 5.0 * 60.0;

/// Recorded/order times closer than this are temporally plausible.
const CLOCK_WINDOW_SECONDS: f64 = 30.0 * 60.0;

/// Minimum cheap envelope NCC before the expensive refinement runs.
const COARSE_ACCEPT: f64 = 0.15;

/// Minimum refined confidence for an audio constraint.
const EDGE_MIN_CONFIDENCE: f64 = 0.30;

/// Minimum refined NCC for an audio constraint.
const EDGE_MIN_NCC: f64 = 0.25;

/// Weak prior that keeps the solved block near the current edit.
const PRIOR_WEIGHT: f64 = 0.02;

/// Stronger prior for a user-selected master track: it pins the gauge only.
const MASTER_PIN_WEIGHT: f64 = 2.0;

const SEQUENCE_TRUSTED_WEIGHT: f64 = 3.0;
const SEQUENCE_UNTRUSTED_WEIGHT: f64 = 0.5;

/// Sequence offsets beyond this are treated as bad metadata, not as a real gap.
const MAX_ABS_SEQUENCE_OFFSET: f64 = 12.0 * 3600.0;

/// Robust Huber delta passed to the solver.
const HUBER_DELTA: f64 = 0.25;

/// A placement this small is rounding noise, not a real move.
const MOVED_EPSILON: f64 = 0.001;

/// Same-file pairs below these magnitudes carry no information (duplicates).
const DUPLICATE_LAG_EPSILON: f64 = 0.05;
const DUPLICATE_OFFSET_EPSILON: f64 = 0.05;

/// One accepted audio measurement between two events.
#[derive(Debug, Clone)]
pub(crate) struct AudioEvidence {
    from: usize,
    to: usize,
    offset_seconds: f64,
    confidence: f64,
    ncc: f64,
    drift_ppm: f64,
    split_points: Vec<SplitPoint>,
}

impl AudioEvidence {
    fn weight(&self) -> f64 {
        self.confidence * self.ncc
    }
}

/// One sequence link between consecutive clips of the same device.
#[derive(Debug, Clone, Copy)]
pub(crate) struct SequenceLink {
    from: usize,
    to: usize,
    offset_seconds: f64,
    weight: f64,
}

/// How same-source clips should be ordered.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum SequenceMode {
    None,
    Preserve,
    Timecode,
    Strict,
    Auto,
}

/// Graph assembled from evidence, ready for the solver.
pub(crate) struct Plan {
    pub(crate) nodes: Vec<Node>,
    pub(crate) constraints: Vec<Constraint>,
    /// Accepted audio edges in deterministic (weight desc, index asc) order.
    audio: Vec<AudioEvidence>,
    /// Whether an accepted sequence constraint touches each node.
    has_sequence: Vec<bool>,
    pub(crate) audio_edges: usize,
    pub(crate) sequence_edges: usize,
    pub(crate) pin: Option<usize>,
}

fn is_broll(ev: &EventDescriptor) -> bool {
    ev.sync_role.eq_ignore_ascii_case("broll")
}

fn normalized_path(path: &str) -> String {
    normalize_path(path).to_lowercase()
}

fn display_source(key: &str) -> String {
    let trimmed = key.strip_prefix("src:").unwrap_or(key);
    let trimmed = trimmed.strip_prefix("event:").unwrap_or(trimmed);
    trimmed
        .replace('/', "\\")
        .rsplit('\\')
        .next()
        .unwrap_or(trimmed)
        .to_string()
}

fn format_seconds(seconds: f64) -> String {
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

fn valid_timecode(value: Option<f64>) -> Option<f64> {
    value.filter(|value| value.is_finite() && (0.0..86_400.0).contains(value))
}

/// True when the project is too large for the global path.
///
/// The event ceiling counts unique normalized paths (duplicate camera-audio
/// events must not push a normal project over the limit), while retained memory
/// is estimated per unique preparation key so duplicated media is not
/// double-counted.
pub(crate) fn exceeds_limits(events: &[EventDescriptor], target_rate: u32) -> bool {
    let rate = target_rate.max(1000) as f64;
    let hop = ENVELOPE_HOP_SIZE.max(1) as f64;
    let sample_bytes = std::mem::size_of::<f32>() as f64;

    let mut paths: HashSet<String> = HashSet::new();
    let mut keys: HashMap<(String, u32, u64, u64, u64), f64> = HashMap::new();

    for event in events {
        let path = normalized_path(&event.file_path);
        paths.insert(path.clone());

        let (_, span_len) = analysis_span(event, false);
        let span = span_len.max(0.0);
        let mut bytes = (span * rate / hop).ceil() * sample_bytes;
        if span <= RETAIN_SAMPLES_SECONDS {
            bytes += span * rate * sample_bytes;
        }

        let key = (
            path,
            event.stream_index,
            event.media_start_seconds.to_bits(),
            event.media_length_seconds.to_bits(),
            playback_rate_or_one(event).to_bits(),
        );
        let entry = keys.entry(key).or_insert(0.0);
        *entry = entry.max(bytes);
    }

    if paths.len() > MAX_GLOBAL_EVENTS {
        return true;
    }

    keys.values().sum::<f64>() > MAX_GLOBAL_MEMORY_BYTES as f64
}

/// Coverage decision. At least one audio edge is always required; a 2–3 event
/// project is allowed as a smoke test, larger projects need three accepted
/// edges or audio covering at least a quarter of the events.
pub(crate) fn graph_is_usable(
    event_count: usize,
    audio_edges: usize,
    audio_connected: usize,
) -> bool {
    if event_count < 2 || audio_edges < 1 {
        return false;
    }
    if event_count <= 3 {
        return true;
    }
    audio_edges >= 3 || audio_connected.saturating_mul(4) >= event_count
}

/// Counts events that sit in a component containing at least one audio edge.
fn audio_connected_events(event_count: usize, constraints: &[Constraint]) -> usize {
    if event_count == 0 {
        return 0;
    }
    let mut parent: Vec<usize> = (0..event_count).collect();

    fn find(parent: &mut [usize], node: usize) -> usize {
        let mut root = node;
        while parent[root] != root {
            root = parent[root];
        }
        let mut current = node;
        while parent[current] != current {
            let next = parent[current];
            parent[current] = root;
            current = next;
        }
        root
    }

    for constraint in constraints {
        if constraint.kind != ConstraintKind::Audio
            || constraint.from >= event_count
            || constraint.to >= event_count
            || constraint.from == constraint.to
        {
            continue;
        }
        let root_from = find(&mut parent, constraint.from);
        let root_to = find(&mut parent, constraint.to);
        if root_from != root_to {
            parent[root_to] = root_from;
        }
    }

    let mut audio_roots: HashSet<usize> = HashSet::new();
    for constraint in constraints {
        if constraint.kind == ConstraintKind::Audio && constraint.from < event_count {
            audio_roots.insert(find(&mut parent, constraint.from));
        }
    }

    (0..event_count)
        .filter(|&node| audio_roots.contains(&find(&mut parent, node)))
        .count()
}

/// Parses the UI clip-order policy. Unsupported values downgrade to `auto` with
/// a warning instead of being silently ignored.
fn parse_clip_order(raw: &str, warnings: &mut Vec<String>) -> SequenceMode {
    let value = raw.trim();
    if value.is_empty() || value.eq_ignore_ascii_case("auto") {
        return SequenceMode::Auto;
    }
    if value.eq_ignore_ascii_case("none") {
        return SequenceMode::None;
    }
    if value.eq_ignore_ascii_case("preserve") {
        return SequenceMode::Preserve;
    }
    if value.eq_ignore_ascii_case("timecode") {
        return SequenceMode::Timecode;
    }
    if value.eq_ignore_ascii_case("strict") {
        return SequenceMode::Strict;
    }

    warnings.push(format!(
        "Global matching: unsupported clip order '{}'; using auto ordering.",
        value
    ));
    SequenceMode::Auto
}

/// Builds the solver graph. Pure except for the clip-order warning, so it is
/// unit-tested directly (see the tests module).
pub(crate) fn build_plan(
    events: &[EventDescriptor],
    infos: &[TimeInfo],
    audio: &[AudioEvidence],
    pin: Option<usize>,
    clip_order: &str,
    warnings: &mut Vec<String>,
) -> Plan {
    let n = events.len();
    let nodes: Vec<Node> = events
        .iter()
        .enumerate()
        .map(|(index, event)| Node {
            id: event.event_id.clone(),
            prior_offset: Some(event.timeline_start_seconds),
            prior_weight: if Some(index) == pin {
                MASTER_PIN_WEIGHT
            } else {
                PRIOR_WEIGHT
            },
        })
        .collect();

    // Strongest audio measurements first so the B-roll single-edge rule keeps
    // the best available link rather than an arbitrary one.
    let mut sorted_audio = audio.to_vec();
    sorted_audio.sort_by(|a, b| {
        b.weight()
            .partial_cmp(&a.weight())
            .unwrap_or(Ordering::Equal)
            .then_with(|| a.from.cmp(&b.from))
            .then_with(|| a.to.cmp(&b.to))
    });

    let mut constraints: Vec<Constraint> = Vec::new();
    let mut accepted_audio: Vec<AudioEvidence> = Vec::new();
    let mut broll_incoming = vec![0usize; n];

    for mut edge in sorted_audio {
        if edge.from == edge.to || edge.from >= n || edge.to >= n {
            continue;
        }

        let from_broll = is_broll(&events[edge.from]);
        let to_broll = is_broll(&events[edge.to]);
        if from_broll && to_broll {
            // Two B-roll clips carry no anchor evidence.
            continue;
        }
        if from_broll {
            // Pairs arrive oriented by event index. A B-roll clip may still
            // receive a link from a normal clip, so flip the edge instead of
            // dropping it. Split points were measured for the original target
            // and no longer belong to the new target.
            std::mem::swap(&mut edge.from, &mut edge.to);
            edge.offset_seconds = -edge.offset_seconds;
            edge.split_points.clear();
        }
        if is_broll(&events[edge.to]) {
            // B-roll may receive exactly one edge.
            if broll_incoming[edge.to] >= 1 {
                continue;
            }
            broll_incoming[edge.to] += 1;
        }

        constraints.push(Constraint {
            from: edge.from,
            to: edge.to,
            offset_seconds: edge.offset_seconds,
            weight: edge.weight(),
            kind: ConstraintKind::Audio,
        });
        accepted_audio.push(edge);
    }

    let audio_edges = accepted_audio.len();
    let mut sequence_edges = 0usize;
    let mut has_sequence = vec![false; n];

    let paths: Vec<String> = events
        .iter()
        .map(|event| normalized_path(&event.file_path))
        .collect();

    for link in sequence_links(events, infos, clip_order, &paths, warnings) {
        if link.from >= n || link.to >= n || link.from == link.to {
            continue;
        }
        // B-roll can never anchor a sequence link; it may receive one edge.
        if is_broll(&events[link.from]) {
            continue;
        }
        if is_broll(&events[link.to]) {
            if broll_incoming[link.to] >= 1 {
                continue;
            }
            broll_incoming[link.to] += 1;
        }

        constraints.push(Constraint {
            from: link.from,
            to: link.to,
            offset_seconds: link.offset_seconds,
            weight: link.weight,
            kind: ConstraintKind::Sequence,
        });
        has_sequence[link.from] = true;
        has_sequence[link.to] = true;
        sequence_edges += 1;
    }

    debug_assert_eq!(nodes.len(), n);

    Plan {
        nodes,
        constraints,
        audio: accepted_audio,
        has_sequence,
        audio_edges,
        sequence_edges,
        pin,
    }
}

/// Solves the graph twice so the specified weak priors behave as a block gauge
/// instead of distorting internal spacing (see the module docs).
pub(crate) fn solve_positions(events: &[EventDescriptor], plan: &mut Plan) -> SolveResult {
    let mut probe_nodes = plan.nodes.clone();
    for node in &mut probe_nodes {
        node.prior_weight = 0.0;
    }
    let probe = global_solver::solve(&probe_nodes, &plan.constraints, HUBER_DELTA);

    let n = plan.nodes.len();
    let mut sizes = vec![0usize; n];
    for &component in &probe.component {
        if component < n {
            sizes[component] += 1;
        }
    }

    let pin = plan.pin;
    let mut translations: Vec<Option<f64>> = vec![None; n];

    for (index, node) in plan.nodes.iter_mut().enumerate() {
        let component = probe.component.get(index).copied().unwrap_or(index);
        if sizes.get(component).copied().unwrap_or(0) <= 1 {
            // Isolated node: its current timeline start is the best evidence.
            continue;
        }

        let translation = match translations.get(component).copied().flatten() {
            Some(value) => value,
            None => {
                let members: Vec<usize> = (0..n)
                    .filter(|&other| probe.component.get(other).copied() == Some(component))
                    .collect();
                let value = match pin.filter(|pinned| members.contains(pinned)) {
                    Some(pinned) => {
                        events
                            .get(pinned)
                            .map(|ev| ev.timeline_start_seconds)
                            .unwrap_or(0.0)
                            - probe.positions.get(pinned).copied().unwrap_or(0.0)
                    }
                    None => {
                        let deltas: Vec<f64> = members
                            .iter()
                            .map(|&other| {
                                events
                                    .get(other)
                                    .map(|ev| ev.timeline_start_seconds)
                                    .unwrap_or(0.0)
                                    - probe.positions.get(other).copied().unwrap_or(0.0)
                            })
                            .collect();
                        median(&deltas)
                    }
                };
                if component < translations.len() {
                    translations[component] = Some(value);
                }
                value
            }
        };

        node.prior_offset = Some(probe.positions.get(index).copied().unwrap_or(0.0) + translation);
    }

    global_solver::solve(&plan.nodes, &plan.constraints, HUBER_DELTA)
}

fn median(values: &[f64]) -> f64 {
    let mut sorted: Vec<f64> = values
        .iter()
        .copied()
        .filter(|value| value.is_finite())
        .collect();
    if sorted.is_empty() {
        return 0.0;
    }
    sorted.sort_by(|a, b| a.partial_cmp(b).unwrap_or(Ordering::Equal));
    let middle = sorted.len() / 2;
    if sorted.len().is_multiple_of(2) {
        (sorted[middle - 1] + sorted[middle]) * 0.5
    } else {
        sorted[middle]
    }
}

/// Same-source event pairs used as cheap candidate hints, independent of the
/// clip-order policy (a policy of `none` disables sequence edges, not the
/// possibility of matching two clips from one device acoustically).
fn same_source_pairs(events: &[EventDescriptor]) -> HashSet<(usize, usize)> {
    let mut groups: HashMap<&str, Vec<usize>> = HashMap::new();
    for (index, event) in events.iter().enumerate() {
        if let Some(key) = event.source_key.as_deref() {
            let key = key.trim();
            if !key.is_empty() {
                groups.entry(key).or_default().push(index);
            }
        }
    }

    let mut pairs = HashSet::new();
    for members in groups.values_mut() {
        members.sort_by(|&a, &b| {
            events[a]
                .timeline_start_seconds
                .partial_cmp(&events[b].timeline_start_seconds)
                .unwrap_or(Ordering::Equal)
                .then_with(|| a.cmp(&b))
        });
        for window in members.windows(2) {
            pairs.insert((window[0].min(window[1]), window[0].max(window[1])));
        }
    }
    pairs
}

/// Unwraps seconds-of-day timecodes for one device into elapsed seconds.
///
/// Sorting raw values is ambiguous around midnight, so the cycle is rotated to
/// start after its largest gap (the quiet overnight break) before handing the
/// sequence to [`clock::unwrap_timecode_order`]. 23:59 → 00:01 therefore becomes
/// +120 s rather than an almost full-day jump.
fn unwrap_timecode_pairs(pairs: &[(usize, f64)]) -> Vec<(usize, f64)> {
    if pairs.len() <= 1 {
        return pairs.to_vec();
    }

    let mut sorted = pairs.to_vec();
    sorted.sort_by(|a, b| {
        a.1.partial_cmp(&b.1)
            .unwrap_or(Ordering::Equal)
            .then_with(|| a.0.cmp(&b.0))
    });

    let mut best_gap = f64::NEG_INFINITY;
    let mut best_index = 0usize;
    for index in 0..sorted.len() {
        let next = (index + 1) % sorted.len();
        let gap = if next == 0 {
            sorted[0].1 + 86_400.0 - sorted[index].1
        } else {
            sorted[next].1 - sorted[index].1
        };
        if gap > best_gap {
            best_gap = gap;
            best_index = next;
        }
    }

    let mut rotation: Vec<(usize, f64)> = Vec::with_capacity(sorted.len());
    for offset in 0..sorted.len() {
        rotation.push(sorted[(best_index + offset) % sorted.len()]);
    }

    let raw: Vec<f64> = rotation.iter().map(|pair| pair.1).collect();
    let unwrapped = clock::unwrap_timecode_order(&raw);
    rotation
        .into_iter()
        .map(|pair| pair.0)
        .zip(unwrapped)
        .collect()
}

/// One entry per event: `(index, ordering value, basis label)`.
fn order_group(
    events: &[EventDescriptor],
    infos: &[TimeInfo],
    mode: SequenceMode,
    members: &[usize],
) -> Vec<(usize, f64, &'static str)> {
    let mut entries: Vec<(usize, f64, &'static str)> = Vec::with_capacity(members.len());

    for &index in members {
        let info = infos.get(index).copied().unwrap_or(TimeInfo::NONE);
        let entry = match mode {
            SequenceMode::None => unreachable!("caller handles `none`"),
            SequenceMode::Preserve => (index, events[index].timeline_start_seconds, "timeline"),
            SequenceMode::Timecode => match valid_timecode(events[index].timecode_seconds) {
                Some(value) => (index, value, "timecode"),
                None => (index, events[index].timeline_start_seconds, "timeline"),
            },
            SequenceMode::Strict => match (info.trusted, info.epoch_seconds) {
                (true, Some(value)) if value.is_finite() => (index, value, info.epoch_source),
                _ => (index, events[index].timeline_start_seconds, "timeline"),
            },
            SequenceMode::Auto => match (info.order_seconds, info.order_source) {
                (Some(value), source)
                    if value.is_finite()
                        && source != "none"
                        && (info.trusted || source == "timecode") =>
                {
                    (index, value, source)
                }
                _ => (index, events[index].timeline_start_seconds, "timeline"),
            },
        };
        entries.push(entry);
    }

    let bases: HashSet<&str> = entries.iter().map(|entry| entry.2).collect();

    // All-timecode groups wrap at midnight; unwrap before ordering.
    if bases.len() == 1 && bases.contains("timecode") {
        let pairs: Vec<(usize, f64)> = entries.iter().map(|entry| (entry.0, entry.1)).collect();
        let unwrapped: HashMap<usize, f64> = unwrap_timecode_pairs(&pairs).into_iter().collect();
        for entry in &mut entries {
            if let Some(value) = unwrapped.get(&entry.0) {
                entry.1 = *value;
            }
        }
    } else if bases.len() > 1 {
        // Mixed bases (seconds-of-day vs epoch vs timeline) are not comparable.
        for entry in &mut entries {
            entry.1 = events[entry.0].timeline_start_seconds;
            entry.2 = "timeline";
        }
    }

    entries.sort_by(|a, b| {
        a.1.partial_cmp(&b.1)
            .unwrap_or(Ordering::Equal)
            .then_with(|| a.0.cmp(&b.0))
    });
    entries
}

/// Builds consecutive same-source links according to the clip-order policy.
fn sequence_links(
    events: &[EventDescriptor],
    infos: &[TimeInfo],
    clip_order: &str,
    paths: &[String],
    warnings: &mut Vec<String>,
) -> Vec<SequenceLink> {
    let mode = parse_clip_order(clip_order, warnings);
    if mode == SequenceMode::None {
        return Vec::new();
    }

    let mut groups: HashMap<&str, Vec<usize>> = HashMap::new();
    for (index, event) in events.iter().enumerate() {
        if let Some(key) = event.source_key.as_deref() {
            let key = key.trim();
            if !key.is_empty() {
                groups.entry(key).or_default().push(index);
            }
        }
    }

    let mut keys: Vec<&str> = groups.keys().copied().collect();
    keys.sort_unstable();

    let mut links = Vec::new();
    let mut absurd_warnings = 0usize;

    for key in keys {
        let members = match groups.get(key) {
            Some(members) => members,
            None => continue,
        };
        let ordered = order_group(events, infos, mode, members);

        // Collapse duplicate camera-audio events (same media path/stream/start)
        // to one representative so 0 s, weight-3 sequence edges cannot stack.
        let mut deduped: Vec<(usize, f64, &'static str)> = Vec::with_capacity(ordered.len());
        for entry in ordered {
            let duplicate = deduped.iter().any(|kept| {
                same_sequence_media(
                    &events[kept.0],
                    &events[entry.0],
                    &paths[kept.0],
                    &paths[entry.0],
                )
            });
            if !duplicate {
                deduped.push(entry);
            }
        }

        for window in deduped.windows(2) {
            let (from, from_value, from_basis) = window[0];
            let (to, to_value, to_basis) = window[1];
            let timeline_offset =
                events[to].timeline_start_seconds - events[from].timeline_start_seconds;

            let mut offset = timeline_offset;
            let mut weight = SEQUENCE_UNTRUSTED_WEIGHT;

            let same_basis = from_basis == to_basis && from_basis != "timeline";
            let order_allowed = same_basis
                && (from_basis == "timecode"
                    || (infos.get(from).map(|i| i.trusted).unwrap_or(false)
                        && infos.get(to).map(|i| i.trusted).unwrap_or(false)));

            if order_allowed {
                // Order values are media timestamps (t0); solver nodes are
                // event starts, so the trimmed media lead-in must be added.
                let from_lead =
                    events[from].media_start_seconds / playback_rate_or_one(&events[from]);
                let to_lead = events[to].media_start_seconds / playback_rate_or_one(&events[to]);
                offset = (to_value - from_value) + to_lead - from_lead;
                weight = SEQUENCE_TRUSTED_WEIGHT;
            }

            if offset.abs() > MAX_ABS_SEQUENCE_OFFSET {
                offset = timeline_offset;
                weight = SEQUENCE_UNTRUSTED_WEIGHT;
                if absurd_warnings < 3 {
                    warnings.push(format!(
                        "Global matching: implausible {} gap ({:.0}s) between '{}' and '{}'; using the current timeline gap.",
                        from_basis, to_value - from_value, events[from].event_id, events[to].event_id
                    ));
                    absurd_warnings += 1;
                }
            }

            links.push(SequenceLink {
                from,
                to,
                offset_seconds: offset,
                weight,
            });
        }
    }

    links
}

/// Builds the manifest rows from solved positions. Pure; returns the results and
/// the right-shift applied to the synced block (0.0 when no shift was needed).
pub(crate) fn build_results(
    events: &[EventDescriptor],
    infos: &[TimeInfo],
    solved: &SolveResult,
    plan: &Plan,
    detect_drift: bool,
) -> (Vec<EventSyncResult>, f64) {
    let n = events.len();

    let mut best_audio: Vec<Option<&AudioEvidence>> = vec![None; n];
    let mut best_incoming: Vec<Option<&AudioEvidence>> = vec![None; n];
    for edge in &plan.audio {
        if edge.from >= n || edge.to >= n || edge.from == edge.to {
            continue;
        }
        for node in [edge.from, edge.to] {
            let better = best_audio
                .get(node)
                .and_then(|current| current.map(|cur| edge.weight() > cur.weight()))
                .unwrap_or(true);
            if better {
                best_audio[node] = Some(edge);
            }
        }
        let better_incoming = best_incoming
            .get(edge.to)
            .and_then(|current| current.map(|cur| edge.weight() > cur.weight()))
            .unwrap_or(true);
        if better_incoming {
            best_incoming[edge.to] = Some(edge);
        }
    }

    // Nodes connected to an audio component only through sequence edges share
    // the component's best confidence instead of reporting 0.
    let mut component_confidence: Vec<Option<f64>> = vec![None; n];
    let mut component_drift: Vec<f64> = vec![0.0; n];
    for edge in &plan.audio {
        let component = solved.component.get(edge.to).copied().unwrap_or(0);
        if component >= n {
            continue;
        }
        let better = component_confidence[component]
            .map(|current| edge.confidence > current)
            .unwrap_or(true);
        if better {
            component_confidence[component] = Some(edge.confidence);
            component_drift[component] = edge.drift_ppm;
        }
    }

    let positioned: Vec<bool> = (0..n)
        .map(|index| {
            solved
                .component_has_audio
                .get(index)
                .copied()
                .unwrap_or(false)
                || plan.has_sequence.get(index).copied().unwrap_or(false)
        })
        .collect();

    // VEGAS cannot place events before zero. Only the solved block is shifted;
    // untouched nodes keep their current (non-negative) timeline position.
    let mut min_position = f64::INFINITY;
    for (index, &is_positioned) in positioned.iter().enumerate() {
        if is_positioned {
            if let Some(position) = solved.positions.get(index).copied() {
                if position.is_finite() && position < min_position {
                    min_position = position;
                }
            }
        }
    }
    let shift = if min_position.is_finite() && min_position < -MOVED_EPSILON {
        -min_position
    } else {
        0.0
    };

    let mut results = Vec::with_capacity(n);
    for (index, event) in events.iter().enumerate() {
        let original = event.timeline_start_seconds;
        let info = infos.get(index).copied().unwrap_or(TimeInfo::NONE);
        let has_usable_time = info.order_seconds.is_some() || info.epoch_seconds.is_some();
        let broll = is_broll(event);

        let solved_position = solved
            .positions
            .get(index)
            .copied()
            .filter(|value| value.is_finite())
            .unwrap_or(original);
        let resolved = if positioned[index] {
            solved_position + shift
        } else {
            solved_position
        };
        let delta = resolved - original;
        let in_audio_component = solved
            .component_has_audio
            .get(index)
            .copied()
            .unwrap_or(false);

        let incoming = best_incoming[index];
        let split_points: Vec<SplitPoint> = incoming
            .map(|edge| edge.split_points.clone())
            .unwrap_or_default();
        let split_points_seconds: Vec<f64> =
            split_points.iter().map(|sp| sp.timestamp_seconds).collect();

        let (is_aligned, confidence, drift_ppm, playback_rate, notes) = if in_audio_component {
            let incident = best_audio[index];
            let component = solved.component.get(index).copied().unwrap_or(0);
            let confidence = incident
                .map(|edge| edge.confidence)
                .or_else(|| component_confidence.get(component).copied().flatten())
                .unwrap_or(0.0);
            let drift = if detect_drift {
                incident
                    .map(|edge| edge.drift_ppm)
                    .unwrap_or_else(|| component_drift.get(component).copied().unwrap_or(0.0))
            } else {
                0.0
            };
            let rate = 1.0 + drift / 1_000_000.0;
            let playback_rate = if rate.is_finite() && rate > 0.0 {
                rate
            } else {
                1.0
            };
            let ncc = incident.map(|edge| edge.ncc).unwrap_or(0.0);
            let mut note = format!(
                "Aligned via global audio graph (Conf: {:.0}%, NCC: {:.2})",
                confidence * 100.0,
                ncc
            );
            if broll {
                note.push_str(" (B-roll)");
            }
            (true, confidence, drift, playback_rate, note)
        } else if has_usable_time || plan.has_sequence.get(index).copied().unwrap_or(false) {
            let sequence_placed = plan.has_sequence.get(index).copied().unwrap_or(false);
            // There is no `method` field on the manifest row, so the strategy is
            // carried in the note; the required phrase stays verbatim for the
            // host/UI counters.
            let note = if broll {
                "B-roll; clock-placed (no audio edge)".to_string()
            } else if sequence_placed {
                "sequence: clock-placed (no audio edge)".to_string()
            } else {
                "clock-placed (no audio edge)".to_string()
            };
            let confidence = if sequence_placed { 0.30 } else { 0.20 };
            // A prior-only or sequence-placed node is only "aligned" when it
            // actually moved; otherwise the host would see a no-op result.
            (delta.abs() > MOVED_EPSILON, confidence, 0.0, 1.0, note)
        } else {
            let note = if broll {
                "B-roll; no audio edge and no usable clock".to_string()
            } else {
                "no audio edge and no usable clock".to_string()
            };
            (false, 0.0, 0.0, 1.0, note)
        };

        results.push(EventSyncResult {
            event_id: event.event_id.clone(),
            track_id: event.track_id.clone(),
            original_start_seconds: original,
            resolved_start_seconds: resolved,
            delta_seconds: delta,
            playback_rate,
            drift_ppm,
            confidence,
            split_points_seconds,
            split_points,
            is_aligned,
            notes: Some(notes),
        });
    }

    (results, shift)
}

/// `(Some(results), warnings)` on success; `(None, warnings)` requests the
/// legacy fallback.
pub(crate) type GlobalRun = (Option<Vec<EventSyncResult>>, Vec<String>);

/// Runs the global strategy. `Ok((None, warnings))` means "not enough evidence,
/// use legacy"; the warnings still explain why.
pub(crate) fn run_global(
    request: &SyncTaskRequest,
    all_events: &[EventDescriptor],
    target_rate: u32,
    reporter: &ProgressReporter,
) -> Result<GlobalRun, String> {
    let mut warnings: Vec<String> = Vec::new();
    let infos = clock::select_all(all_events, &request.options);

    let untrusted = clock::untrusted_sources(all_events, &request.options);
    if !untrusted.is_empty() {
        let mut keys: Vec<String> = untrusted.iter().map(|key| display_source(key)).collect();
        keys.sort();
        keys.dedup();
        warnings.push(format!(
            "Global matching: clock not trusted for {}; epoch-based sequence offsets were not used for those devices.",
            keys.join(", ")
        ));
    }

    if all_events.len() < 2 {
        warnings.push("Global matching: fewer than two events; falling back.".to_string());
        return Ok((None, warnings));
    }
    if exceeds_limits(all_events, target_rate) {
        warnings.push(format!(
            "Global matching: project exceeds the global limits ({} unique media paths or {} MB retained audio); falling back.",
            MAX_GLOBAL_EVENTS,
            MAX_GLOBAL_MEMORY_BYTES / (1024 * 1024)
        ));
        return Ok((None, warnings));
    }

    if !reporter.report(0.04, "Global matching: reading device clocks...") {
        return Err(CANCELLED.into());
    }

    let prepared = prepare_all(request, all_events, target_rate, reporter, &mut warnings)?;
    let prepared_count = prepared.iter().filter(|p| p.is_some()).count();
    if prepared_count < 2 {
        warnings.push(
            "Global matching: fewer than two events could be analyzed; falling back.".to_string(),
        );
        return Ok((None, warnings));
    }

    let same_source = same_source_pairs(all_events);

    let paths: Vec<String> = all_events
        .iter()
        .map(|event| normalized_path(&event.file_path))
        .collect();
    let hubs = longest_take_indices(all_events, HUB_SLOTS);
    let prescan = prescan_pairs(all_events, &infos, &same_source, &hubs, &paths);

    if !reporter.report(0.36, "Global matching: scoring candidate pairs...") {
        return Err(CANCELLED.into());
    }

    let mut scored: Vec<(usize, usize, f64)> = Vec::with_capacity(prescan.len());
    for (step, &(from, to)) in prescan.iter().enumerate() {
        if step % 32 == 0
            && !reporter.report(
                0.36 + 0.08 * (step as f32 / prescan.len().max(1) as f32),
                "Global matching: scoring candidate pairs...",
            )
        {
            return Err(CANCELLED.into());
        }
        let (Some(a), Some(b)) = (prepared.get(from), prepared.get(to)) else {
            continue;
        };
        let (Some(a), Some(b)) = (a.as_ref(), b.as_ref()) else {
            continue;
        };
        let Some(coarse) =
            CoarseAligner::find_envelope_lag(&a.envelope, &b.envelope, ENVELOPE_HOP_SIZE)
        else {
            continue;
        };
        if coarse.score.is_finite() {
            scored.push((from, to, coarse.score));
        }
    }

    // At most MAX_CANDIDATES_PER_EVENT survive, ranked by envelope score, then
    // by timeline proximity, with reserved slots for same-source neighbours and
    // the longest takes (the recorder hub must not be squeezed out).
    let n = all_events.len();
    let mut incident: Vec<Vec<(f64, f64, usize, f64)>> = vec![Vec::new(); n];
    for &(from, to, coarse) in &scored {
        let bonus = if same_source.contains(&(from, to)) {
            1.0
        } else if hubs.contains(&from) || hubs.contains(&to) {
            0.5
        } else {
            0.0
        };
        let start_delta =
            (all_events[from].timeline_start_seconds - all_events[to].timeline_start_seconds).abs();
        incident[from].push((coarse + bonus, start_delta, to, coarse));
        incident[to].push((coarse + bonus, start_delta, from, coarse));
    }

    let mut refine: Vec<(usize, usize, f64)> = Vec::new();
    for (index, candidates) in incident.iter_mut().enumerate() {
        candidates.sort_by(|a, b| {
            b.0.partial_cmp(&a.0)
                .unwrap_or(Ordering::Equal)
                .then_with(|| a.1.partial_cmp(&b.1).unwrap_or(Ordering::Equal))
                .then_with(|| a.2.cmp(&b.2))
        });

        let mut chosen: Vec<(usize, f64)> = Vec::new();
        let mut seen: HashSet<usize> = HashSet::new();

        for &(_, _, other, coarse) in candidates.iter() {
            if chosen.len() >= MAX_CANDIDATES_PER_EVENT {
                break;
            }
            let pair = (index.min(other), index.max(other));
            if (same_source.contains(&pair) || hubs.contains(&other)) && seen.insert(other) {
                chosen.push((other, coarse));
            }
        }
        for &(_, _, other, coarse) in candidates.iter() {
            if chosen.len() >= MAX_CANDIDATES_PER_EVENT {
                break;
            }
            if seen.insert(other) {
                chosen.push((other, coarse));
            }
        }

        for (other, coarse) in chosen {
            let pair = if index < other {
                (index, other, coarse)
            } else {
                (other, index, coarse)
            };
            refine.push(pair);
        }
    }
    refine.sort_by(|a, b| {
        a.0.cmp(&b.0)
            .then_with(|| a.1.cmp(&b.1))
            .then_with(|| a.2.partial_cmp(&b.2).unwrap_or(Ordering::Equal))
    });
    refine.dedup_by(|a, b| a.0 == b.0 && a.1 == b.1);

    let gcc = GccPhat::new();
    let workers = rayon::current_num_threads().clamp(1, 4);
    let total_pairs = refine.len().max(1);
    let mut audio: Vec<AudioEvidence> = Vec::new();
    let mut done = 0usize;

    for chunk in refine.chunks(workers) {
        let outcomes: Vec<AudioEvidence> = chunk
            .par_iter()
            .filter_map(|&(from, to, coarse)| {
                let forced =
                    same_source.contains(&(from, to)) || hubs.contains(&from) || hubs.contains(&to);
                if coarse < COARSE_ACCEPT && !forced {
                    return None;
                }
                let a = prepared.get(from)?.as_ref()?;
                let b = prepared.get(to)?.as_ref()?;
                if a.envelope.len() < 2 || b.envelope.len() < 2 {
                    return None;
                }

                let pair = analyze_pair(a, b, request, target_rate, &gcc).ok()?;
                if !pair.confidence.is_finite() || pair.confidence < EDGE_MIN_CONFIDENCE {
                    return None;
                }
                let ncc = if a.is_synthetic || b.is_synthetic {
                    1.0
                } else {
                    pair.ncc_score
                };
                if !ncc.is_finite() || ncc < EDGE_MIN_NCC || !pair.lag_seconds.is_finite() {
                    return None;
                }

                // Same T0 conversion as `engine::analyze_event`, expressed in
                // event-start coordinates (the solver's node variable).
                let rate_from = playback_rate_or_one(&a.event);
                let rate_to = playback_rate_or_one(&b.event);
                let lead_from = (a.span_start_media - a.event.media_start_seconds) / rate_from;
                let lead_to = (b.span_start_media - b.event.media_start_seconds) / rate_to;
                let offset = lead_from - lead_to + pair.lag_seconds / rate_to;
                if !offset.is_finite() {
                    return None;
                }

                // Duplicate camera-audio events (same file, same span) would
                // otherwise stack zero-offset edges and eat candidate slots.
                if paths[from] == paths[to]
                    && pair.lag_seconds.abs() < DUPLICATE_LAG_EPSILON
                    && offset.abs() < DUPLICATE_OFFSET_EPSILON
                {
                    return None;
                }

                Some(AudioEvidence {
                    from,
                    to,
                    offset_seconds: offset,
                    confidence: pair.confidence,
                    ncc,
                    drift_ppm: pair.drift_ppm,
                    split_points: pair.split_points,
                })
            })
            .collect();

        audio.extend(outcomes);
        done += chunk.len();
        if !reporter.report(
            0.45 + 0.40 * (done as f32 / total_pairs as f32),
            &format!(
                "Global matching: matched {} of {} candidate pair(s)...",
                done,
                refine.len()
            ),
        ) {
            return Err(CANCELLED.into());
        }
    }

    if !reporter.report(0.88, "Global matching: solving the audio graph...") {
        return Err(CANCELLED.into());
    }

    let pin = request.master_track_id.as_deref().and_then(|track| {
        if all_events
            .iter()
            .any(|e| e.track_id == track && !e.is_muted)
        {
            let chosen = choose_master(request, all_events);
            all_events
                .iter()
                .position(|e| e.event_id == chosen.event_id)
        } else {
            None
        }
    });

    let mut plan = build_plan(
        all_events,
        &infos,
        &audio,
        pin,
        &request.options.clip_order,
        &mut warnings,
    );
    let connected = audio_connected_events(all_events.len(), &plan.constraints);

    // Zero audio edges after B-roll/duplicate filtering must never produce a
    // "global" manifest: there is nothing audio-verified to report.
    if plan.audio_edges == 0 {
        warnings.push(
            "Global matching: no usable audio edge after filtering; falling back.".to_string(),
        );
        return Ok((None, warnings));
    }
    if !graph_is_usable(all_events.len(), plan.audio_edges, connected) {
        warnings.push(format!(
            "Global matching: only {} audio edge(s) covering {} of {} event(s); falling back to the legacy pipeline.",
            plan.audio_edges,
            connected,
            all_events.len()
        ));
        return Ok((None, warnings));
    }

    let solved = solve_positions(all_events, &mut plan);
    let (results, shift) = build_results(
        all_events,
        &infos,
        &solved,
        &plan,
        request.options.detect_drift,
    );

    // --- Reporting -----------------------------------------------------------------
    let mut audio_components = 0usize;
    let mut seen_components: HashSet<usize> = HashSet::new();
    let mut with_audio = 0usize;
    let mut clock_placed = 0usize;
    let mut no_time = 0usize;

    for (index, _event) in all_events.iter().enumerate() {
        let in_audio = solved
            .component_has_audio
            .get(index)
            .copied()
            .unwrap_or(false);
        if in_audio {
            with_audio += 1;
            if let Some(component) = solved.component.get(index).copied() {
                if seen_components.insert(component) {
                    audio_components += 1;
                }
            }
        } else {
            let info = infos.get(index).copied().unwrap_or(TimeInfo::NONE);
            let has_sequence = plan.has_sequence.get(index).copied().unwrap_or(false);
            if info.order_seconds.is_some() || info.epoch_seconds.is_some() || has_sequence {
                clock_placed += 1;
            } else {
                no_time += 1;
            }
        }
    }

    warnings.push(format!(
        "Global matching: {} event(s), {} audio edge(s), {} sequence edge(s) in {} audio-connected component(s); {} clip(s) clock-placed, {} without an audio edge, {} with no usable clock.",
        all_events.len(),
        plan.audio_edges,
        plan.sequence_edges,
        audio_components,
        clock_placed,
        all_events.len() - with_audio,
        no_time
    ));
    if shift > MOVED_EPSILON {
        warnings.push(format!(
            "Global matching: synchronized block shifted right by {} to keep every event on the positive timeline.",
            format_seconds(shift)
        ));
    }

    Ok((Some(results), warnings))
}

fn prepare_all(
    request: &SyncTaskRequest,
    events: &[EventDescriptor],
    target_rate: u32,
    reporter: &ProgressReporter,
    warnings: &mut Vec<String>,
) -> Result<Vec<Option<Arc<PreparedEvent>>>, String> {
    type CacheKey = (String, u32, u64, u64, u64, u64);

    let mut prepared: Vec<Option<Arc<PreparedEvent>>> = Vec::with_capacity(events.len());
    let mut cache: HashMap<CacheKey, Option<Arc<PreparedEvent>>> = HashMap::new();
    let total = events.len().max(1);

    for (index, event) in events.iter().enumerate() {
        if !reporter.report(
            0.05 + 0.30 * (index as f32 / total as f32),
            &format!(
                "Global matching: analyzing {} ({}/{})",
                event.event_id,
                index + 1,
                events.len()
            ),
        ) {
            return Err(CANCELLED.into());
        }

        let key: CacheKey = (
            normalized_path(&event.file_path),
            event.stream_index,
            event.media_start_seconds.to_bits(),
            event.media_length_seconds.to_bits(),
            event.timeline_length_seconds.to_bits(),
            playback_rate_or_one(event).to_bits(),
        );

        if let Some(cached) = cache.get(&key) {
            prepared.push(cached.clone());
            continue;
        }

        let result = match prepare_event(event, false, request, target_rate, None) {
            Ok(p) => Some(Arc::new(p)),
            Err(e) if e == CANCELLED => return Err(CANCELLED.into()),
            Err(e) => {
                warnings.push(format!(
                    "Global matching: skipped '{}': {}",
                    event.event_id, e
                ));
                None
            }
        };
        cache.insert(key, result.clone());
        prepared.push(result);
    }

    Ok(prepared)
}

fn longest_take_indices(events: &[EventDescriptor], limit: usize) -> Vec<usize> {
    let mut order: Vec<usize> = (0..events.len()).collect();
    order.sort_by(|&a, &b| {
        let span_a = analysis_span(&events[a], false).1;
        let span_b = analysis_span(&events[b], false).1;
        span_b
            .partial_cmp(&span_a)
            .unwrap_or(Ordering::Equal)
            .then_with(|| a.cmp(&b))
    });
    order.truncate(limit);
    order
}

fn same_media_duplicate(
    a: &EventDescriptor,
    b: &EventDescriptor,
    path_a: &str,
    path_b: &str,
) -> bool {
    path_a == path_b
        && a.stream_index == b.stream_index
        && (a.media_start_seconds - b.media_start_seconds).abs() < 1e-6
        && (a.media_length_seconds - b.media_length_seconds).abs() < 1e-6
        && (playback_rate_or_one(a) - playback_rate_or_one(b)).abs() < 1e-9
}

/// Identity used to collapse duplicate camera-audio events in sequence
/// ordering: same file, same stream and the same media start.
fn same_sequence_media(
    a: &EventDescriptor,
    b: &EventDescriptor,
    path_a: &str,
    path_b: &str,
) -> bool {
    path_a == path_b
        && a.stream_index == b.stream_index
        && (a.media_start_seconds - b.media_start_seconds).abs() < 1e-6
}

/// Cheap temporal plausibility prescan. Keeps at most [`PRESCAN_PER_EVENT`]
/// neighbours per event, ranked by time proximity then start-position distance,
/// with reserved slots for same-source neighbours and the longest takes.
fn prescan_pairs(
    events: &[EventDescriptor],
    infos: &[TimeInfo],
    same_source: &HashSet<(usize, usize)>,
    hubs: &[usize],
    paths: &[String],
) -> Vec<(usize, usize)> {
    let n = events.len();
    let mut selected: HashSet<(usize, usize)> = HashSet::new();

    for index in 0..n {
        let mut neighbours: Vec<(f64, f64, usize)> = Vec::new();
        for other in 0..n {
            if index == other {
                continue;
            }
            if same_media_duplicate(&events[index], &events[other], &paths[index], &paths[other]) {
                continue;
            }
            let pair = (index.min(other), index.max(other));
            if !same_source.contains(&pair) && !temporally_plausible(index, other, events, infos) {
                continue;
            }
            let proximity = 1.0 / (1.0 + timeline_gap(&events[index], &events[other]));
            let start_delta =
                (events[index].timeline_start_seconds - events[other].timeline_start_seconds).abs();
            neighbours.push((proximity, start_delta, other));
        }

        neighbours.sort_by(|a, b| {
            b.0.partial_cmp(&a.0)
                .unwrap_or(Ordering::Equal)
                .then_with(|| a.1.partial_cmp(&b.1).unwrap_or(Ordering::Equal))
                .then_with(|| a.2.cmp(&b.2))
        });

        let mut chosen: Vec<usize> = Vec::new();
        let mut seen: HashSet<usize> = HashSet::new();

        // Reserve same-source neighbours.
        for &(_, _, other) in neighbours.iter() {
            if chosen.len() >= PRESCAN_PER_EVENT {
                break;
            }
            let pair = (index.min(other), index.max(other));
            if same_source.contains(&pair) && seen.insert(other) {
                chosen.push(other);
            }
        }
        // Reserve the longest takes (recorder hub).
        for &hub in hubs {
            if chosen.len() >= PRESCAN_PER_EVENT {
                break;
            }
            if hub != index
                && neighbours.iter().any(|&(_, _, other)| other == hub)
                && seen.insert(hub)
            {
                chosen.push(hub);
            }
        }
        // Fill the rest by rank.
        for &(_, _, other) in neighbours.iter() {
            if chosen.len() >= PRESCAN_PER_EVENT {
                break;
            }
            if seen.insert(other) {
                chosen.push(other);
            }
        }

        for other in chosen {
            selected.insert((index.min(other), index.max(other)));
        }
    }

    let mut pairs: Vec<(usize, usize)> = selected.into_iter().collect();
    pairs.sort_unstable();
    pairs
}

fn temporally_plausible(
    a: usize,
    b: usize,
    events: &[EventDescriptor],
    infos: &[TimeInfo],
) -> bool {
    if timeline_gap(&events[a], &events[b]) <= TIMELINE_WINDOW_SECONDS {
        return true;
    }

    let (Some(ia), Some(ib)) = (infos.get(a), infos.get(b)) else {
        return false;
    };

    if let (Some(ea), Some(eb)) = (ia.epoch_seconds, ib.epoch_seconds) {
        if (ea - eb).abs() <= CLOCK_WINDOW_SECONDS {
            return true;
        }
    }

    // Seconds-of-day and epoch values are different bases; only compare order
    // times that came from the same source kind.
    if ia.order_source == ib.order_source {
        if let (Some(oa), Some(ob)) = (ia.order_seconds, ib.order_seconds) {
            if (oa - ob).abs() <= CLOCK_WINDOW_SECONDS {
                return true;
            }
        }
    }

    false
}

fn timeline_gap(a: &EventDescriptor, b: &EventDescriptor) -> f64 {
    let a_start = a.timeline_start_seconds;
    let a_end = a_start + a.timeline_length_seconds.max(0.0);
    let b_start = b.timeline_start_seconds;
    let b_end = b_start + b.timeline_length_seconds.max(0.0);

    if a_end < b_start {
        b_start - a_end
    } else if b_end < a_start {
        a_start - b_end
    } else {
        0.0
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const RATE: u32 = 16_000;

    fn event(
        id: &str,
        start: f64,
        length: f64,
        source: Option<&str>,
        role: &str,
    ) -> EventDescriptor {
        EventDescriptor {
            event_id: id.to_string(),
            track_id: format!("track-{id}"),
            file_path: format!("mock://{id}"),
            timeline_start_seconds: start,
            timeline_length_seconds: length,
            media_length_seconds: length,
            source_key: source.map(str::to_string),
            sync_role: role.to_string(),
            ..EventDescriptor::default()
        }
    }

    fn time_info(order: Option<f64>, epoch: Option<f64>, trusted: bool) -> TimeInfo {
        TimeInfo {
            order_seconds: order,
            epoch_seconds: epoch,
            order_source: if order.is_some() { "recorded" } else { "none" },
            epoch_source: if epoch.is_some() { "recorded" } else { "none" },
            source: if epoch.is_some() { "recorded" } else { "none" },
            trusted,
        }
    }

    fn audio(from: usize, to: usize, offset: f64, confidence: f64, ncc: f64) -> AudioEvidence {
        AudioEvidence {
            from,
            to,
            offset_seconds: offset,
            confidence,
            ncc,
            drift_ppm: 0.0,
            split_points: Vec::new(),
        }
    }

    fn plan_with(
        events: &[EventDescriptor],
        infos: &[TimeInfo],
        audio: &[AudioEvidence],
        pin: Option<usize>,
        clip_order: &str,
    ) -> (Plan, Vec<String>) {
        let mut warnings = Vec::new();
        let plan = build_plan(events, infos, audio, pin, clip_order, &mut warnings);
        (plan, warnings)
    }

    fn approx(actual: f64, expected: f64, tolerance: f64) {
        assert!(
            (actual - expected).abs() <= tolerance,
            "expected {expected}, got {actual} (tolerance {tolerance})"
        );
    }

    /// A↔B and B↔C have audio edges; A↔C does not. The global solve must place
    /// C consistently relative to A through B.
    #[test]
    fn chain_aligns_transitively_through_the_middle_event() {
        let events = vec![
            event("A", 0.0, 30.0, None, "normal"),
            event("B", 100.0, 30.0, None, "normal"),
            event("C", 200.0, 30.0, None, "normal"),
        ];
        let infos = vec![TimeInfo::NONE; 3];
        let mut plan = build_plan(
            &events,
            &infos,
            &[audio(0, 1, 4.0, 0.9, 0.8), audio(1, 2, 4.0, 0.9, 0.8)],
            None,
            "auto",
            &mut Vec::new(),
        );

        assert_eq!(plan.audio_edges, 2);
        let solved = solve_positions(&events, &mut plan);
        assert!(solved.component_has_audio.iter().all(|&has| has));

        let (results, shift) = build_results(&events, &infos, &solved, &plan, false);
        assert_eq!(shift, 0.0);

        approx(
            results[1].resolved_start_seconds - results[0].resolved_start_seconds,
            4.0,
            1e-6,
        );
        approx(
            results[2].resolved_start_seconds - results[1].resolved_start_seconds,
            4.0,
            1e-6,
        );
        assert!(results.iter().all(|r| r.is_aligned));
        assert!(results.iter().all(|r| r
            .notes
            .as_deref()
            .unwrap_or("")
            .contains("global audio graph")));
    }

    /// An isolated node with a usable clock keeps its position and is reported
    /// as clock-placed (not aligned).
    #[test]
    fn prior_only_node_keeps_position_and_is_clock_placed() {
        let events = vec![
            event("A", 0.0, 30.0, None, "normal"),
            event("B", 4.0, 30.0, None, "normal"),
            event("lonely", 900.0, 20.0, None, "normal"),
        ];
        let infos = vec![
            time_info(Some(1_800_000_000.0), Some(1_800_000_000.0), true),
            time_info(Some(1_800_000_010.0), Some(1_800_000_010.0), true),
            time_info(Some(1_800_000_100.0), Some(1_800_000_100.0), true),
        ];
        let mut plan = build_plan(
            &events,
            &infos,
            &[audio(0, 1, 4.0, 0.9, 0.9)],
            None,
            "auto",
            &mut Vec::new(),
        );
        let solved = solve_positions(&events, &mut plan);
        let (results, _) = build_results(&events, &infos, &solved, &plan, false);

        let lonely = &results[2];
        approx(lonely.resolved_start_seconds, 900.0, 1e-9);
        approx(lonely.delta_seconds, 0.0, 1e-9);
        assert!(!lonely.is_aligned);
        assert_eq!(
            lonely.notes.as_deref(),
            Some("clock-placed (no audio edge)")
        );
    }

    #[test]
    fn coverage_requires_three_edges_or_a_quarter_of_the_events() {
        assert!(!graph_is_usable(3, 0, 0));
        assert!(!graph_is_usable(1, 0, 0));
        assert!(!graph_is_usable(0, 0, 0));
        // 2–3 event smoke tests are allowed with a single edge.
        assert!(graph_is_usable(2, 1, 2));
        assert!(graph_is_usable(3, 1, 2));
        // Larger projects need three edges or ≥25% coverage.
        assert!(!graph_is_usable(10, 1, 1));
        assert!(graph_is_usable(10, 3, 0));
        assert!(graph_is_usable(10, 2, 3));
    }

    #[test]
    fn broll_is_never_a_sequence_anchor() {
        let events = vec![
            event("broll", 0.0, 5.0, Some("cam"), "broll"),
            event("main", 300.0, 30.0, Some("cam"), "normal"),
        ];
        let infos = vec![
            time_info(Some(1_800_000_000.0), Some(1_800_000_000.0), true),
            time_info(Some(1_800_000_300.0), Some(1_800_000_300.0), true),
        ];
        let (plan, _) = plan_with(&events, &infos, &[], None, "auto");

        assert_eq!(plan.sequence_edges, 0);
        assert!(plan.constraints.is_empty());
    }

    #[test]
    fn broll_edge_is_reoriented_instead_of_dropped() {
        let events = vec![
            event("broll", 0.0, 5.0, None, "broll"),
            event("main", 30.0, 30.0, None, "normal"),
        ];
        let infos = vec![TimeInfo::NONE; 2];

        let (plan, _) = plan_with(&events, &infos, &[audio(0, 1, 5.0, 0.9, 0.9)], None, "auto");
        assert_eq!(plan.audio_edges, 1);
        let constraint = plan.constraints[0];
        assert_eq!(constraint.kind, ConstraintKind::Audio);
        assert_eq!(constraint.from, 1, "normal clip must become the anchor");
        assert_eq!(constraint.to, 0, "B-roll must be the receiver");
        approx(constraint.offset_seconds, -5.0, 1e-9);

        // B-roll ↔ B-roll is never usable.
        let both = vec![
            event("broll-a", 0.0, 5.0, None, "broll"),
            event("broll-b", 10.0, 5.0, None, "broll"),
        ];
        let (plan, _) = plan_with(
            &both,
            &[TimeInfo::NONE; 2],
            &[audio(0, 1, 2.0, 0.9, 0.9)],
            None,
            "auto",
        );
        assert_eq!(plan.audio_edges, 0);
    }

    #[test]
    fn sequence_edge_uses_order_for_trusted_clocks_and_gaps_for_untrusted() {
        let events = vec![
            event("a", 0.0, 10.0, Some("cam"), "normal"),
            event("b", 100.0, 10.0, Some("cam"), "normal"),
        ];
        let trusted = vec![
            time_info(Some(1_800_000_000.0), Some(1_800_000_000.0), true),
            time_info(Some(1_800_000_012.0), Some(1_800_000_012.0), true),
        ];
        let (plan, _) = plan_with(&events, &trusted, &[], None, "auto");
        assert_eq!(plan.sequence_edges, 1);
        approx(plan.constraints[0].offset_seconds, 12.0, 1e-9);
        approx(plan.constraints[0].weight, SEQUENCE_TRUSTED_WEIGHT, 1e-9);

        let untrusted = vec![
            time_info(Some(1_800_000_000.0), None, false),
            time_info(Some(1_800_000_012.0), None, false),
        ];
        let (plan, _) = plan_with(&events, &untrusted, &[], None, "auto");
        assert_eq!(plan.sequence_edges, 1);
        // Untrusted clocks use the current timeline gap (100 s), not the clock.
        approx(plan.constraints[0].offset_seconds, 100.0, 1e-9);
        approx(plan.constraints[0].weight, SEQUENCE_UNTRUSTED_WEIGHT, 1e-9);
    }

    #[test]
    fn sequence_offset_includes_trimmed_media_start() {
        let mut a = event("a", 0.0, 10.0, Some("cam"), "normal");
        a.media_start_seconds = 1.0;
        let mut b = event("b", 100.0, 10.0, Some("cam"), "normal");
        b.media_start_seconds = 2.5;
        let events = vec![a, b];
        let infos = vec![
            time_info(Some(1_800_000_000.0), Some(1_800_000_000.0), true),
            time_info(Some(1_800_000_012.0), Some(1_800_000_012.0), true),
        ];

        let mut plan = build_plan(&events, &infos, &[], None, "auto", &mut Vec::new());
        assert_eq!(plan.sequence_edges, 1);
        // 12 s of media order plus (2.5 s − 1.0 s) of trimmed lead-in.
        approx(plan.constraints[0].offset_seconds, 13.5, 1e-9);

        let solved = solve_positions(&events, &mut plan);
        let (results, _) = build_results(&events, &infos, &solved, &plan, false);
        approx(
            results[1].resolved_start_seconds - results[0].resolved_start_seconds,
            13.5,
            1e-6,
        );
    }

    #[test]
    fn duplicate_media_events_are_collapsed_in_sequence_links() {
        let mut first = event("dup-1", 0.0, 10.0, Some("cam"), "normal");
        first.file_path = "C:/media/take.wav".to_string();
        let mut twin = event("dup-2", 5.0, 10.0, Some("cam"), "normal");
        twin.file_path = "C:/media/take.wav".to_string();
        let third = event("other", 100.0, 10.0, Some("cam"), "normal");
        let events = vec![first, twin, third];
        let infos = vec![
            time_info(Some(1_800_000_000.0), Some(1_800_000_000.0), true),
            time_info(Some(1_800_000_000.0), Some(1_800_000_000.0), true),
            time_info(Some(1_800_000_012.0), Some(1_800_000_012.0), true),
        ];

        let (plan, _) = plan_with(&events, &infos, &[], None, "auto");
        assert_eq!(plan.sequence_edges, 1);
        // Only the first duplicate participates; the twin creates no 0 s edge.
        assert_eq!(plan.constraints[0].from, 0);
        assert_eq!(plan.constraints[0].to, 2);
        approx(plan.constraints[0].offset_seconds, 12.0, 1e-9);
    }

    #[test]
    fn mixed_order_bases_fall_back_to_the_timeline_gap() {
        let events = vec![
            event("a", 0.0, 10.0, Some("cam"), "normal"),
            event("b", 100.0, 10.0, Some("cam"), "normal"),
        ];
        let infos = vec![
            TimeInfo {
                order_seconds: Some(1_800_000_000.0),
                epoch_seconds: Some(1_800_000_000.0),
                order_source: "recorded",
                epoch_source: "recorded",
                source: "recorded",
                trusted: true,
            },
            TimeInfo {
                order_seconds: Some(1_800_000_012.0),
                epoch_seconds: Some(1_800_000_012.0),
                order_source: "filename",
                epoch_source: "filename",
                source: "filename",
                trusted: true,
            },
        ];
        let (plan, _) = plan_with(&events, &infos, &[], None, "auto");
        assert_eq!(plan.sequence_edges, 1);
        approx(plan.constraints[0].offset_seconds, 100.0, 1e-9);
        approx(plan.constraints[0].weight, SEQUENCE_UNTRUSTED_WEIGHT, 1e-9);
    }

    #[test]
    fn midnight_timecode_is_unwrapped() {
        let mut late = event("late", 0.0, 10.0, Some("cam"), "normal");
        late.timecode_seconds = Some(86_340.0);
        let mut early = event("early", 30.0, 10.0, Some("cam"), "normal");
        early.timecode_seconds = Some(60.0);
        let events = vec![late, early];
        let infos = vec![
            TimeInfo {
                order_seconds: Some(86_340.0),
                epoch_seconds: None,
                order_source: "timecode",
                epoch_source: "none",
                source: "timecode",
                trusted: true,
            },
            TimeInfo {
                order_seconds: Some(60.0),
                epoch_seconds: None,
                order_source: "timecode",
                epoch_source: "none",
                source: "timecode",
                trusted: true,
            },
        ];
        let (plan, _) = plan_with(&events, &infos, &[], None, "timecode");
        assert_eq!(plan.sequence_edges, 1);
        // 23:59 → 00:01 is +120 s, not +86_100 s.
        approx(plan.constraints[0].offset_seconds, 120.0, 1e-9);
        approx(plan.constraints[0].weight, SEQUENCE_TRUSTED_WEIGHT, 1e-9);
        assert_eq!(plan.constraints[0].from, 0);
        assert_eq!(plan.constraints[0].to, 1);
    }

    #[test]
    fn absurd_order_offsets_fall_back_with_a_warning() {
        let events = vec![
            event("a", 0.0, 10.0, Some("cam"), "normal"),
            event("b", 100.0, 10.0, Some("cam"), "normal"),
        ];
        let infos = vec![
            time_info(Some(1_800_000_000.0), Some(1_800_000_000.0), true),
            time_info(
                Some(1_800_000_000.0 + 2.0 * 86_400.0),
                Some(1_800_000_000.0 + 2.0 * 86_400.0),
                true,
            ),
        ];
        let (plan, warnings) = plan_with(&events, &infos, &[], None, "auto");
        assert_eq!(plan.sequence_edges, 1);
        approx(plan.constraints[0].offset_seconds, 100.0, 1e-9);
        approx(plan.constraints[0].weight, SEQUENCE_UNTRUSTED_WEIGHT, 1e-9);
        assert!(warnings
            .iter()
            .any(|warning| warning.contains("implausible")));
    }

    #[test]
    fn clip_order_policies_are_honored() {
        let events = vec![
            event("a", 0.0, 10.0, Some("cam"), "normal"),
            event("b", 100.0, 10.0, Some("cam"), "normal"),
        ];
        let infos = vec![
            time_info(Some(1_800_000_000.0), Some(1_800_000_000.0), true),
            time_info(Some(1_800_000_012.0), Some(1_800_000_012.0), true),
        ];

        let (plan, _) = plan_with(&events, &infos, &[], None, "none");
        assert_eq!(plan.sequence_edges, 0);

        let (plan, _) = plan_with(&events, &infos, &[], None, "preserve");
        approx(plan.constraints[0].offset_seconds, 100.0, 1e-9);
        approx(plan.constraints[0].weight, SEQUENCE_UNTRUSTED_WEIGHT, 1e-9);

        let (plan, warnings) = plan_with(&events, &infos, &[], None, "bogus");
        assert_eq!(plan.sequence_edges, 1);
        approx(plan.constraints[0].offset_seconds, 12.0, 1e-9);
        assert!(warnings
            .iter()
            .any(|warning| warning.contains("unsupported clip order")));
    }

    #[test]
    fn duplicate_media_guard_counts_unique_paths() {
        let duplicates: Vec<EventDescriptor> = (0..1_000)
            .map(|index| {
                let mut duplicate = event(&format!("dup-{index}"), 0.0, 5.0, None, "normal");
                duplicate.file_path = "C:/media/same_take.wav".to_string();
                duplicate
            })
            .collect();
        assert!(!exceeds_limits(&duplicates, RATE));

        let unique: Vec<EventDescriptor> = (0..=MAX_GLOBAL_EVENTS)
            .map(|index| event(&format!("e{index}"), index as f64, 5.0, None, "normal"))
            .collect();
        assert!(exceeds_limits(&unique, RATE));

        let heavy: Vec<EventDescriptor> = (0..30)
            .map(|index| event(&format!("h{index}"), index as f64, 300.0, None, "normal"))
            .collect();
        assert!(exceeds_limits(&heavy, RATE));
    }

    #[test]
    fn prescan_skips_identical_media_duplicates() {
        let mut first = event("dup-a", 0.0, 10.0, None, "normal");
        first.file_path = "C:/media/same.wav".to_string();
        let mut second = event("dup-b", 1.0, 10.0, None, "normal");
        second.file_path = "C:/media/same.wav".to_string();
        let third = event("other", 2.0, 10.0, None, "normal");
        let events = vec![first, second, third];
        let infos = vec![TimeInfo::NONE; 3];
        let paths = vec![
            normalized_path("C:/media/same.wav"),
            normalized_path("C:/media/same.wav"),
            normalized_path("mock://other"),
        ];
        let pairs = prescan_pairs(&events, &infos, &HashSet::new(), &[], &paths);
        assert!(!pairs.contains(&(0, 1)), "duplicate media must not pair");
        assert!(pairs.contains(&(0, 2)));
        assert!(pairs.contains(&(1, 2)));
    }

    #[test]
    fn longest_take_hub_is_reserved_in_the_prescan() {
        let events = vec![
            event("short-a", 0.0, 5.0, None, "normal"),
            event("short-b", 10.0, 5.0, None, "normal"),
            event("short-c", 20.0, 5.0, None, "normal"),
            event("recorder", 30.0, 300.0, None, "normal"),
        ];
        let infos = vec![TimeInfo::NONE; 4];
        let paths: Vec<String> = events
            .iter()
            .map(|event| normalized_path(&event.file_path))
            .collect();
        let hubs = longest_take_indices(&events, HUB_SLOTS);
        assert_eq!(hubs.first().copied(), Some(3));

        let pairs = prescan_pairs(&events, &infos, &HashSet::new(), &hubs, &paths);
        for index in 0..3 {
            assert!(pairs.contains(&(index.min(3), index.max(3))));
        }
    }

    #[test]
    fn timeline_gap_handles_overlap_and_separation() {
        let a = event("a", 10.0, 10.0, None, "normal");
        let b = event("b", 15.0, 10.0, None, "normal");
        let c = event("c", 40.0, 5.0, None, "normal");
        approx(timeline_gap(&a, &b), 0.0, 1e-9);
        approx(timeline_gap(&b, &c), 15.0, 1e-9);
    }

    #[test]
    fn same_source_links_follow_trusted_order() {
        let events = vec![
            event("late", 0.0, 5.0, Some("cam"), "normal"),
            event("early", 50.0, 5.0, Some("cam"), "normal"),
        ];
        let infos = vec![
            time_info(Some(2_000.0), Some(2_000.0), true),
            time_info(Some(1_000.0), Some(1_000.0), true),
        ];
        let mut warnings = Vec::new();
        let paths: Vec<String> = events
            .iter()
            .map(|event| normalized_path(&event.file_path))
            .collect();
        let links = sequence_links(&events, &infos, "auto", &paths, &mut warnings);
        assert_eq!(links.len(), 1);
        assert_eq!(links[0].from, 1);
        assert_eq!(links[0].to, 0);
        approx(links[0].offset_seconds, 1000.0, 1e-9);
    }

    #[test]
    fn build_plan_is_deterministic_for_repeated_calls() {
        let events = vec![
            event("A", 0.0, 30.0, None, "normal"),
            event("B", 100.0, 30.0, None, "normal"),
            event("C", 200.0, 30.0, None, "normal"),
        ];
        let infos = vec![TimeInfo::NONE; 3];
        let evidence = vec![audio(0, 1, 4.0, 0.9, 0.9), audio(1, 2, 4.0, 0.8, 0.9)];
        let first = build_plan(&events, &infos, &evidence, None, "auto", &mut Vec::new());
        let second = build_plan(&events, &infos, &evidence, None, "auto", &mut Vec::new());
        assert_eq!(first.constraints, second.constraints);
        assert_eq!(first.audio_edges, second.audio_edges);
        assert_eq!(first.nodes, second.nodes);
    }
}
