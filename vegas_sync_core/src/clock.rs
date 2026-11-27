//! Clock selection and validation for media events.
//!
//! Cameras and recorders disagree about what "now" is: a Sony A7 III with a dead
//! backup battery can stamp 2018 on footage shot in 2026. This module turns the
//! raw metadata on [`EventDescriptor`] into a single [`TimeInfo`] per event and
//! cross-checks each device against the project-wide median epoch.
//!
//! # Units and bases
//!
//! - `recorded_time_seconds`, `filename_time_seconds` and `file_time_seconds`
//!   are **Unix epoch seconds** (UTC) — absolute wall-clock instants.
//! - `timecode_seconds` is **seconds-of-day** (`0.0 <= t < 86_400.0`). It never
//!   carries a date, so it can order clips from one device but cannot place
//!   them on a project timeline.
//! - [`TimeInfo::order_seconds`] and [`TimeInfo::epoch_seconds`] therefore live
//!   on **different bases**. Read [`TimeInfo::order_source`] and
//!   [`TimeInfo::epoch_source`] before comparing values from different events;
//!   seconds-of-day and epoch seconds are not comparable without those labels.
//! - Sequence builders must unwrap timecodes across midnight before building
//!   sequence edges; see [`unwrap_timecode_order`]. This module intentionally
//!   builds no sequence edges.
//!
//! # Selection and trust
//!
//! - `auto` ordering prefers `recorded` → `timecode` → `filename` → `file_time`.
//! - `auto` epoch prefers the date-bearing sources `recorded` → `filename` →
//!   `file_time`; `timecode` never supplies an epoch. A single event can thus
//!   order by timecode and carry the epoch from its filename.
//! - Explicit `time_source` modes force exactly one source and never mix bases.
//! - A timecode outside `[0, 86_400)` is rejected.
//! - Trust needs at least two devices with epoch evidence. A lone device epoch
//!   is kept as a weak prior but flagged `trusted = false`. With two or more
//!   device epochs, a median more than 30 days from the upper-median project
//!   reference is rejected and its epoch is dropped.
//!
//! Everything here is deterministic and allocation-bounded; no panics on
//! non-finite or missing metadata.

use std::collections::{HashMap, HashSet};

use crate::models::{EventDescriptor, SyncOptions};

/// Devices whose median epoch is further than this from the project median are
/// treated as having a broken clock.
pub const UNTRUSTED_EPOCH_WINDOW_SECONDS: f64 = 30.0 * SECONDS_PER_DAY;

const SECONDS_PER_DAY: f64 = 86_400.0;
/// 2000-01-01T00:00:00Z — earlier epoch values are treated as garbage.
const MIN_PLAUSIBLE_EPOCH_SECONDS: f64 = 946_684_800.0;
/// 2100-01-01T00:00:00Z — later epoch values are treated as garbage.
const MAX_PLAUSIBLE_EPOCH_SECONDS: f64 = 4_102_444_800.0;

/// Resolved timing information for one event.
///
/// `order_seconds` and `epoch_seconds` may come from different fields (for
/// example timecode for ordering and filename for the epoch). Their bases are
/// labelled by `order_source` and `epoch_source`; never compare the two numeric
/// fields directly without checking the labels.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TimeInfo {
    /// Seconds used for ordering clips from the same device. Its base is given
    /// by [`Self::order_source`]: epoch seconds for the date-bearing sources and
    /// seconds-of-day for `timecode`.
    pub order_seconds: Option<f64>,
    /// Unix epoch seconds when a date-bearing source supplied one. `None` for
    /// timecode-only sources.
    pub epoch_seconds: Option<f64>,
    /// Field that produced [`Self::order_seconds`]: `recorded`, `timecode`,
    /// `filename`, `file_time` or `none`.
    pub order_source: &'static str,
    /// Field that produced [`Self::epoch_seconds`]: `recorded`, `filename`,
    /// `file_time` or `none`.
    pub epoch_source: &'static str,
    /// Convenience summary: `epoch_source` when an epoch is present, otherwise
    /// `order_source`. Kept for callers that only want one label; use the two
    /// specific labels when the bases matter.
    pub source: &'static str,
    /// `false` when the device clock was rejected or could not be verified.
    pub trusted: bool,
}

impl TimeInfo {
    pub const NONE: Self = Self {
        order_seconds: None,
        epoch_seconds: None,
        order_source: "none",
        epoch_source: "none",
        source: "none",
        trusted: true,
    };

    fn none_untrusted() -> Self {
        Self {
            trusted: false,
            ..Self::NONE
        }
    }

    fn from_parts(
        order_seconds: Option<f64>,
        order_source: &'static str,
        epoch_seconds: Option<f64>,
        epoch_source: &'static str,
    ) -> Self {
        let source = if epoch_seconds.is_some() {
            epoch_source
        } else {
            order_source
        };
        Self {
            order_seconds,
            epoch_seconds,
            order_source,
            epoch_source,
            source,
            trusted: true,
        }
    }

    fn from_epoch(epoch_seconds: f64, source: &'static str) -> Self {
        Self::from_parts(Some(epoch_seconds), source, Some(epoch_seconds), source)
    }

    fn from_timecode(order_seconds: f64) -> Self {
        Self::from_parts(Some(order_seconds), "timecode", None, "none")
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum TimeSourceMode {
    Auto,
    Recorded,
    Timecode,
    Filename,
    FileTime,
    Ignore,
}

fn time_source_mode(raw: &str) -> TimeSourceMode {
    let raw = raw.trim();
    if raw.eq_ignore_ascii_case("recorded") {
        TimeSourceMode::Recorded
    } else if raw.eq_ignore_ascii_case("timecode") {
        TimeSourceMode::Timecode
    } else if raw.eq_ignore_ascii_case("filename") {
        TimeSourceMode::Filename
    } else if raw.eq_ignore_ascii_case("file_time") || raw.eq_ignore_ascii_case("filetime") {
        TimeSourceMode::FileTime
    } else if raw.eq_ignore_ascii_case("ignore") || raw.eq_ignore_ascii_case("none") {
        TimeSourceMode::Ignore
    } else {
        // Unknown values (including the explicit "auto") fall back to auto.
        TimeSourceMode::Auto
    }
}

fn finite(value: Option<f64>) -> Option<f64> {
    value.filter(|value| value.is_finite())
}

/// Accept only date-bearing epoch values inside a plausible calendar window.
fn plausible_epoch(value: Option<f64>) -> Option<f64> {
    finite(value)
        .filter(|value| (MIN_PLAUSIBLE_EPOCH_SECONDS..=MAX_PLAUSIBLE_EPOCH_SECONDS).contains(value))
}

/// Validate a container timecode: seconds-of-day in `[0, 86_400)`.
///
/// Out-of-range values are rejected rather than wrapped: a timecode that does
/// not fit a single day is corrupt metadata, and wrapping it would silently
/// move the clip to the wrong end of the day.
fn timecode_order(value: Option<f64>) -> Option<f64> {
    finite(value).filter(|value| (0.0..SECONDS_PER_DAY).contains(value))
}

/// Unwrap seconds-of-day timecodes into elapsed seconds across midnight.
///
/// Feed values in playback order. Every time the value decreases relative to
/// the previous one, a full day is added so the result is non-decreasing. This
/// is the primitive sequence builders need before they create edges; the
/// function itself builds no edges and does not decide clip order.
///
/// Values outside `[0, 86_400)` are skipped, matching [`select_time`].
///
/// ```
/// # use telltale_sync_core::clock::unwrap_timecode_order;
/// let unwrapped = unwrap_timecode_order(&[86_300.0, 100.0, 200.0]);
/// assert_eq!(unwrapped, vec![86_300.0, 86_500.0, 86_600.0]);
/// ```
pub fn unwrap_timecode_order(values: &[f64]) -> Vec<f64> {
    let mut day_offset = 0.0;
    let mut unwrapped: Vec<f64> = Vec::with_capacity(values.len());
    for value in values.iter().copied() {
        let Some(seconds_of_day) = timecode_order(Some(value)) else {
            continue;
        };
        let mut absolute = seconds_of_day + day_offset;
        let crossed_midnight = unwrapped
            .last()
            .is_some_and(|previous| absolute < *previous);
        if crossed_midnight {
            day_offset += SECONDS_PER_DAY;
            absolute += SECONDS_PER_DAY;
        }
        unwrapped.push(absolute);
    }
    unwrapped
}

/// Resolve the time metadata for a single event according to `options.time_source`.
///
/// `auto` keeps ordering and epoch selection independent: ordering prefers
/// `recorded` → `timecode` → `filename` → `file_time`, while the epoch prefers
/// `recorded` → `filename` → `file_time`. Explicit modes force one source.
///
/// This is the per-event view; it cannot detect a stale device clock because it
/// has no project context. Use [`select_all`] when the whole event list is known.
pub fn select_time(ev: &EventDescriptor, options: &SyncOptions) -> TimeInfo {
    match time_source_mode(&options.time_source) {
        TimeSourceMode::Ignore => TimeInfo::NONE,
        TimeSourceMode::Recorded => plausible_epoch(ev.recorded_time_seconds)
            .map_or(TimeInfo::NONE, |value| {
                TimeInfo::from_epoch(value, "recorded")
            }),
        TimeSourceMode::Timecode => {
            timecode_order(ev.timecode_seconds).map_or(TimeInfo::NONE, TimeInfo::from_timecode)
        }
        TimeSourceMode::Filename => plausible_epoch(ev.filename_time_seconds)
            .map_or(TimeInfo::NONE, |value| {
                TimeInfo::from_epoch(value, "filename")
            }),
        TimeSourceMode::FileTime => plausible_epoch(ev.file_time_seconds)
            .map_or(TimeInfo::NONE, |value| {
                TimeInfo::from_epoch(value, "file_time")
            }),
        TimeSourceMode::Auto => auto_time(ev),
    }
}

fn auto_time(ev: &EventDescriptor) -> TimeInfo {
    let recorded = plausible_epoch(ev.recorded_time_seconds);
    let timecode = timecode_order(ev.timecode_seconds);
    let filename = plausible_epoch(ev.filename_time_seconds);
    let file_time = plausible_epoch(ev.file_time_seconds);

    let (order_seconds, order_source) = if let Some(value) = recorded {
        (Some(value), "recorded")
    } else if let Some(value) = timecode {
        (Some(value), "timecode")
    } else if let Some(value) = filename {
        (Some(value), "filename")
    } else if let Some(value) = file_time {
        (Some(value), "file_time")
    } else {
        (None, "none")
    };

    // Timecode is deliberately absent here: seconds-of-day has no date.
    let (epoch_seconds, epoch_source) = if let Some(value) = recorded {
        (Some(value), "recorded")
    } else if let Some(value) = filename {
        (Some(value), "filename")
    } else if let Some(value) = file_time {
        (Some(value), "file_time")
    } else {
        (None, "none")
    };

    TimeInfo::from_parts(order_seconds, order_source, epoch_seconds, epoch_source)
}

/// Resolve timing for every event and reject or flag device clocks.
///
/// Device trust is decided once, from the per-event selections:
///
/// - Fewer than two devices with epoch evidence means the lone epoch cannot be
///   checked. Its events keep the epoch as a weak prior but are flagged
///   `trusted = false`.
/// - With two or more device epochs, the project reference is the upper median
///   of device medians, so one prolific camera cannot outvote the rest and a
///   two-device tie resolves toward the later clock (stale clocks are far more
///   common than clocks set in the future). A device more than
///   [`UNTRUSTED_EPOCH_WINDOW_SECONDS`] away is rejected and its epoch dropped.
pub fn select_all(events: &[EventDescriptor], options: &SyncOptions) -> Vec<TimeInfo> {
    let mut infos = base_infos(events, options);
    if time_source_mode(&options.time_source) == TimeSourceMode::Ignore {
        return infos;
    }

    let report = trust_analysis(events, &infos);
    if report.insufficient_evidence {
        for (index, event) in events.iter().enumerate() {
            if report.device_medians.contains_key(&device_key(event)) {
                infos[index].trusted = false;
            }
        }
        return infos;
    }

    if report.rejected.is_empty() {
        return infos;
    }

    for (index, event) in events.iter().enumerate() {
        if report.rejected.contains(&device_key(event)) {
            infos[index] = untrusted_fallback(event);
        }
    }
    infos
}

/// Per-device median epoch, keyed by `source_key` (events without a usable key
/// get a private per-event key). Devices that made no epoch claim are absent.
///
/// See [`select_all`] for the trust rules applied to these medians.
pub fn validate_devices(events: &[EventDescriptor], options: &SyncOptions) -> HashMap<String, f64> {
    let infos = base_infos(events, options);
    trust_analysis(events, &infos).device_medians
}

/// Device keys that are not trusted: either rejected by the 30-day check or,
/// with fewer than two epoch-bearing devices, flagged for insufficient
/// evidence (those events keep their epoch but must not be treated as trusted).
pub fn untrusted_sources(events: &[EventDescriptor], options: &SyncOptions) -> HashSet<String> {
    let infos = base_infos(events, options);
    let report = trust_analysis(events, &infos);
    if report.insufficient_evidence {
        return report.device_medians.into_keys().collect();
    }
    report.rejected
}

fn base_infos(events: &[EventDescriptor], options: &SyncOptions) -> Vec<TimeInfo> {
    events
        .iter()
        .map(|event| select_time(event, options))
        .collect()
}

struct TrustReport {
    device_medians: HashMap<String, f64>,
    /// Devices rejected by the 30-day window; their epoch must be dropped.
    rejected: HashSet<String>,
    /// Exactly one device claimed an epoch, so there is nothing to cross-check.
    insufficient_evidence: bool,
}

fn trust_analysis(events: &[EventDescriptor], infos: &[TimeInfo]) -> TrustReport {
    let mut groups: HashMap<String, Vec<usize>> = HashMap::new();
    for (index, event) in events.iter().enumerate() {
        groups.entry(device_key(event)).or_default().push(index);
    }

    let mut device_medians: HashMap<String, f64> = HashMap::new();
    for (key, indices) in &groups {
        let epochs: Vec<f64> = indices
            .iter()
            .filter_map(|&index| infos[index].epoch_seconds)
            .collect();
        if let Some(median) = median(&epochs) {
            device_medians.insert(key.clone(), median);
        }
    }

    let insufficient_evidence = device_medians.len() == 1;
    let project_median = project_reference(&device_medians);

    let mut rejected: HashSet<String> = HashSet::new();
    if !insufficient_evidence {
        if let Some(reference) = project_median {
            for (key, device_median) in &device_medians {
                if (device_median - reference).abs() > UNTRUSTED_EPOCH_WINDOW_SECONDS {
                    rejected.insert(key.clone());
                }
            }
        }
    }

    TrustReport {
        device_medians,
        rejected,
        insufficient_evidence,
    }
}

/// A rejected device keeps ordering if its container timecode or filename
/// timestamp still provides one, but never keeps an epoch value.
fn untrusted_fallback(event: &EventDescriptor) -> TimeInfo {
    if let Some(order_seconds) = timecode_order(event.timecode_seconds) {
        return TimeInfo {
            trusted: false,
            ..TimeInfo::from_timecode(order_seconds)
        };
    }
    if let Some(order_seconds) = plausible_epoch(event.filename_time_seconds) {
        return TimeInfo {
            trusted: false,
            ..TimeInfo::from_parts(Some(order_seconds), "filename", None, "none")
        };
    }
    TimeInfo::none_untrusted()
}

fn device_key(event: &EventDescriptor) -> String {
    match event.source_key.as_deref() {
        Some(key) if !key.trim().is_empty() => format!("src:{}", key.trim()),
        _ => format!("event:{}", event.event_id),
    }
}

fn median(values: &[f64]) -> Option<f64> {
    let mut sorted: Vec<f64> = values
        .iter()
        .copied()
        .filter(|value| value.is_finite())
        .collect();
    if sorted.is_empty() {
        return None;
    }
    sorted.sort_by(f64::total_cmp);
    let middle = sorted.len() / 2;
    Some(if sorted.len().is_multiple_of(2) {
        (sorted[middle - 1] + sorted[middle]) * 0.5
    } else {
        sorted[middle]
    })
}

/// Upper median of per-device medians: with an even number of devices the later
/// central value wins. This keeps a two-device project (one stale clock) from
/// sitting exactly between the two clocks and distrusting both devices.
fn project_reference(device_medians: &HashMap<String, f64>) -> Option<f64> {
    let mut values: Vec<f64> = device_medians
        .values()
        .copied()
        .filter(|value| value.is_finite())
        .collect();
    if values.is_empty() {
        return None;
    }
    values.sort_by(f64::total_cmp);
    // For an odd count this is the middle element; for an even count it is the
    // upper of the two central elements, as documented above.
    Some(values[values.len() / 2])
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 2026-06-15T00:00:00Z-ish.
    const EPOCH_2026: f64 = 1_781_481_600.0;
    /// 2018-03-01T00:00:00Z-ish (stale A7 III clock).
    const EPOCH_2018: f64 = 1_519_869_600.0;

    fn event(id: &str, source_key: Option<&str>) -> EventDescriptor {
        EventDescriptor {
            event_id: id.to_string(),
            track_id: "track-1".to_string(),
            file_path: format!("C:/media/{id}.mp4"),
            stream_index: 0,
            timeline_start_seconds: 0.0,
            timeline_length_seconds: 10.0,
            media_start_seconds: 0.0,
            media_length_seconds: 10.0,
            is_muted: false,
            source_key: source_key.map(str::to_string),
            recorded_time_seconds: None,
            timecode_seconds: None,
            filename_time_seconds: None,
            file_time_seconds: None,
            sync_role: "normal".to_string(),
            playback_rate: 1.0,
        }
    }

    fn with_recorded(mut event: EventDescriptor, epoch: f64) -> EventDescriptor {
        event.recorded_time_seconds = Some(epoch);
        event
    }

    #[test]
    fn stale_2018_device_is_untrusted_and_loses_its_epoch() {
        let options = SyncOptions::default();
        let mut stale = with_recorded(event("a7-1", Some("sony a7 iii|03_wesele")), EPOCH_2018);
        stale.timecode_seconds = Some(12.5);
        let fx3_a = with_recorded(event("fx3-1", Some("sony fx3|03_wesele")), EPOCH_2026);
        let mut fx3_b = with_recorded(
            event("fx3-2", Some("sony fx3|03_wesele")),
            EPOCH_2026 + 60.0,
        );
        fx3_b.timecode_seconds = Some(13.0);

        let events = vec![stale, fx3_a, fx3_b];
        let infos = select_all(&events, &options);

        assert!(!infos[0].trusted, "2018 clock must be rejected");
        assert_eq!(
            infos[0].epoch_seconds, None,
            "rejected epoch must be dropped"
        );
        assert_eq!(
            infos[0].order_seconds,
            Some(12.5),
            "timecode keeps ordering"
        );
        assert_eq!(infos[0].order_source, "timecode");
        assert_eq!(infos[0].epoch_source, "none");
        assert_eq!(infos[0].source, "timecode");

        assert!(infos[1].trusted);
        assert_eq!(infos[1].epoch_seconds, Some(EPOCH_2026));
        assert_eq!(infos[1].source, "recorded");

        let untrusted = untrusted_sources(&events, &options);
        assert_eq!(untrusted.len(), 1);
        assert!(untrusted.contains("src:sony a7 iii|03_wesele"));
    }

    #[test]
    fn two_device_epochs_reject_the_2018_a7() {
        let options = SyncOptions::default();
        let stale = with_recorded(event("a7-1", Some("sony a7 iii")), EPOCH_2018);
        let good = with_recorded(event("fx3-1", Some("sony fx3")), EPOCH_2026);

        let infos = select_all(&[stale, good], &options);
        assert!(!infos[0].trusted, "2018 clock must be rejected");
        assert_eq!(infos[0].epoch_seconds, None);
        assert!(infos[1].trusted);
        assert_eq!(infos[1].epoch_seconds, Some(EPOCH_2026));
    }

    #[test]
    fn stale_device_without_fallback_yields_no_time() {
        let options = SyncOptions::default();
        let stale = with_recorded(event("a7-1", Some("sony a7 iii")), EPOCH_2018);
        let good_a = with_recorded(event("fx3-1", Some("sony fx3")), EPOCH_2026);
        let good_b = with_recorded(event("fx3-2", Some("sony fx3")), EPOCH_2026 + 30.0);

        let infos = select_all(&[stale, good_a, good_b], &options);
        assert!(!infos[0].trusted);
        assert_eq!(infos[0].order_seconds, None);
        assert_eq!(infos[0].epoch_seconds, None);
        assert_eq!(infos[0].source, "none");
    }

    #[test]
    fn auto_timecode_only_orders_without_epoch() {
        let options = SyncOptions::default();
        let mut tc = event("fx3-tc", Some("sony fx3"));
        tc.timecode_seconds = Some(3_600.0);

        let info = select_time(&tc, &options);
        assert_eq!(info.order_seconds, Some(3_600.0));
        assert_eq!(info.order_source, "timecode");
        assert_eq!(info.epoch_seconds, None);
        assert_eq!(info.epoch_source, "none");
        assert_eq!(info.source, "timecode");
        assert!(info.trusted);

        let all = select_all(&[tc], &options);
        assert_eq!(all[0].order_seconds, Some(3_600.0));
        assert_eq!(all[0].order_source, "timecode");
        assert_eq!(all[0].epoch_seconds, None);
        assert_eq!(all[0].epoch_source, "none");
        assert!(all[0].trusted, "a timecode-only project has no epoch claim");
    }

    #[test]
    fn auto_timecode_order_uses_filename_epoch_and_labels_both_bases() {
        let options = SyncOptions::default();
        let mut mixed = event("fx3-mixed", Some("sony fx3"));
        mixed.timecode_seconds = Some(7_200.0);
        mixed.filename_time_seconds = Some(EPOCH_2026);

        let info = select_time(&mixed, &options);
        assert_eq!(info.order_seconds, Some(7_200.0));
        assert_eq!(info.order_source, "timecode");
        assert_eq!(info.epoch_seconds, Some(EPOCH_2026));
        assert_eq!(info.epoch_source, "filename");
        assert_ne!(
            info.order_source, info.epoch_source,
            "bases must stay distinguishable"
        );
        assert_ne!(
            info.order_seconds, info.epoch_seconds,
            "seconds-of-day must not be mistaken for epoch seconds"
        );
    }

    #[test]
    fn auto_prefers_recorded_over_timecode_for_both_roles() {
        let options = SyncOptions::default();
        let mut rich = with_recorded(event("clip", Some("cam")), EPOCH_2026);
        rich.timecode_seconds = Some(600.0);
        rich.filename_time_seconds = Some(EPOCH_2026 + 10.0);

        let info = select_time(&rich, &options);
        assert_eq!(info.order_seconds, Some(EPOCH_2026));
        assert_eq!(info.order_source, "recorded");
        assert_eq!(info.epoch_seconds, Some(EPOCH_2026));
        assert_eq!(info.epoch_source, "recorded");
    }

    #[test]
    fn auto_falls_back_from_recorded_to_filename_to_file_time() {
        let options = SyncOptions::default();

        let mut filename_only = event("clip-file", Some("cam"));
        filename_only.filename_time_seconds = Some(EPOCH_2026);
        let info = select_time(&filename_only, &options);
        assert_eq!(info.order_source, "filename");
        assert_eq!(info.epoch_source, "filename");
        assert_eq!(info.epoch_seconds, Some(EPOCH_2026));

        let mut file_time_only = event("clip-mtime", Some("cam"));
        file_time_only.file_time_seconds = Some(EPOCH_2026 + 5.0);
        let info = select_time(&file_time_only, &options);
        assert_eq!(info.order_source, "file_time");
        assert_eq!(info.epoch_source, "file_time");
        assert_eq!(info.epoch_seconds, Some(EPOCH_2026 + 5.0));

        // A recorded value outside the plausible calendar window is ignored.
        let mut garbage_recorded = event("clip-garbage", Some("cam"));
        garbage_recorded.recorded_time_seconds = Some(0.0);
        garbage_recorded.filename_time_seconds = Some(EPOCH_2026);
        let info = select_time(&garbage_recorded, &options);
        assert_eq!(info.order_source, "filename");
        assert_eq!(info.epoch_source, "filename");
        assert_eq!(info.epoch_seconds, Some(EPOCH_2026));

        // Nothing but a timecode still yields ordering, never an epoch.
        let mut tc_only = event("clip-tc", Some("cam"));
        tc_only.timecode_seconds = Some(4_200.0);
        let info = select_time(&tc_only, &options);
        assert_eq!(info.order_source, "timecode");
        assert_eq!(info.epoch_source, "none");
        assert_eq!(info.epoch_seconds, None);
    }

    #[test]
    fn single_epoch_device_is_flagged_untrusted_but_keeps_epoch() {
        let options = SyncOptions::default();
        let recorded = with_recorded(event("a7-1", Some("sony a7 iii")), EPOCH_2026);
        let mut tc_peer = event("fx3-tc", Some("sony fx3"));
        tc_peer.timecode_seconds = Some(1_200.0);

        let events = vec![recorded, tc_peer];
        let infos = select_all(&events, &options);

        assert!(
            !infos[0].trusted,
            "a lone epoch cannot be cross-checked and must be flagged"
        );
        assert_eq!(
            infos[0].epoch_seconds,
            Some(EPOCH_2026),
            "the lone epoch is kept as a weak prior"
        );
        assert_eq!(infos[0].epoch_source, "recorded");

        assert!(
            infos[1].trusted,
            "a timecode-only peer makes no epoch claim, so it stays trusted"
        );

        let untrusted = untrusted_sources(&events, &options);
        assert!(untrusted.contains("src:sony a7 iii"));
        assert!(!untrusted.contains("src:sony fx3"));
    }

    #[test]
    fn a7_2018_with_timecode_only_fx3_is_untrusted() {
        let options = SyncOptions::default();
        let stale = with_recorded(event("a7-1", Some("sony a7 iii")), EPOCH_2018);
        let mut tc_peer = event("fx3-tc", Some("sony fx3"));
        tc_peer.timecode_seconds = Some(1_200.0);

        let infos = select_all(&[stale, tc_peer], &options);
        assert!(
            !infos[0].trusted,
            "the only epoch belongs to the stale A7 and cannot be verified"
        );
        assert_eq!(
            infos[0].epoch_seconds,
            Some(EPOCH_2018),
            "insufficient evidence keeps the epoch as a weak prior"
        );
        assert_eq!(infos[0].epoch_source, "recorded");
        assert!(infos[1].trusted);
    }

    #[test]
    fn explicit_ignore_yields_no_time_information() {
        let options = SyncOptions {
            time_source: "ignore".to_string(),
            ..SyncOptions::default()
        };

        let mut rich = event("clip", Some("cam"));
        rich.recorded_time_seconds = Some(EPOCH_2026);
        rich.filename_time_seconds = Some(EPOCH_2026);
        rich.file_time_seconds = Some(EPOCH_2026);
        rich.timecode_seconds = Some(1_000.0);

        let info = select_time(&rich, &options);
        assert_eq!(info.order_seconds, None);
        assert_eq!(info.epoch_seconds, None);
        assert_eq!(info.order_source, "none");
        assert_eq!(info.epoch_source, "none");
        assert_eq!(info.source, "none");

        let all = select_all(&[rich], &options);
        assert_eq!(all[0].order_seconds, None);
        assert_eq!(all[0].epoch_seconds, None);
        assert_eq!(all[0].order_source, "none");
        assert_eq!(all[0].epoch_source, "none");
    }

    #[test]
    fn explicit_source_modes_are_honored() {
        let mut rich = event("clip", Some("cam"));
        rich.recorded_time_seconds = Some(EPOCH_2026);
        rich.filename_time_seconds = Some(EPOCH_2026 + 10.0);
        rich.file_time_seconds = Some(EPOCH_2026 + 20.0);
        rich.timecode_seconds = Some(600.0);

        let mut options = SyncOptions {
            time_source: "filename".to_string(),
            ..SyncOptions::default()
        };
        let info = select_time(&rich, &options);
        assert_eq!(info.source, "filename");
        assert_eq!(info.order_source, "filename");
        assert_eq!(info.epoch_source, "filename");
        assert_eq!(info.epoch_seconds, Some(EPOCH_2026 + 10.0));

        options.time_source = "file_time".to_string();
        let info = select_time(&rich, &options);
        assert_eq!(info.source, "file_time");
        assert_eq!(info.order_source, "file_time");
        assert_eq!(info.epoch_source, "file_time");

        options.time_source = "timecode".to_string();
        let info = select_time(&rich, &options);
        assert_eq!(info.source, "timecode");
        assert_eq!(info.order_source, "timecode");
        assert_eq!(info.epoch_source, "none");
        assert_eq!(info.order_seconds, Some(600.0));
        assert_eq!(info.epoch_seconds, None);
    }

    #[test]
    fn invalid_timecodes_are_ignored() {
        let options = SyncOptions::default();

        for bad in [86_400.0, -1.0, 90_000.0, f64::NAN, f64::INFINITY] {
            let mut invalid = event("clip", Some("cam"));
            invalid.timecode_seconds = Some(bad);
            let info = select_time(&invalid, &options);
            assert_eq!(info.order_seconds, None, "timecode {bad} must be rejected");
            assert_eq!(info.epoch_seconds, None);
            assert_eq!(info.order_source, "none");
            assert_eq!(info.epoch_source, "none");
        }

        // A valid timecode is still accepted at both day edges.
        for good in [0.0, 86_399.999] {
            let mut valid = event("clip", Some("cam"));
            valid.timecode_seconds = Some(good);
            assert_eq!(select_time(&valid, &options).order_seconds, Some(good));
        }
    }

    #[test]
    fn timecode_unwrap_crosses_midnight_and_skips_invalid_values() {
        let unwrapped = unwrap_timecode_order(&[86_300.0, 100.0, 200.0]);
        assert_eq!(unwrapped, vec![86_300.0, 86_500.0, 86_600.0]);

        let with_garbage = unwrap_timecode_order(&[86_300.0, 90_000.0, 100.0]);
        assert_eq!(with_garbage, vec![86_300.0, 86_500.0]);
    }

    #[test]
    fn project_median_is_weighted_per_device_not_per_event() {
        let options = SyncOptions::default();
        let mut events = Vec::new();
        // One prolific camera with a stale clock ...
        for index in 0..9 {
            events.push(with_recorded(
                event(&format!("a7-{index}"), Some("sony a7 iii")),
                EPOCH_2018,
            ));
        }
        // ... and two healthy cameras with a single clip each.
        events.push(with_recorded(event("fx3-1", Some("sony fx3")), EPOCH_2026));
        events.push(with_recorded(
            event("go-1", Some("gopro")),
            EPOCH_2026 + 120.0,
        ));

        let infos = select_all(&events, &options);
        assert!(
            infos[..9].iter().all(|info| !info.trusted),
            "stale device must be rejected even when it has most of the clips"
        );
        assert!(infos[9].trusted);
        assert!(infos[10].trusted);
    }
}
