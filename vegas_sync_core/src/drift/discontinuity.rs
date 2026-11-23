use super::ransac::DriftObservation;

#[derive(Debug, Clone)]
pub struct Discontinuity {
    pub timestamp_seconds: f64,
    pub step_jump_seconds: f64,
}

pub struct DiscontinuityDetector;

impl DiscontinuityDetector {
    /// Scans consecutive inlier observations for step jumps in delay that exceed the threshold.
    /// Returns recommended split timestamps.
    pub fn detect_step_jumps(
        observations: &[DriftObservation],
        threshold_seconds: f64,
    ) -> Vec<Discontinuity> {
        if observations.len() < 2 {
            return Vec::new();
        }

        let mut jumps = Vec::new();
        let mut i = 1usize;

        while i < observations.len() {
            let prev = &observations[i - 1];
            let curr = &observations[i];

            let delta_tau = curr.delay_seconds - prev.delay_seconds;
            let dt = (curr.time_seconds - prev.time_seconds).max(0.0);

            // Approximate natural clock drift contribution over this dt (< 1000 ppm)
            let max_natural_drift = dt * 0.001;
            let excess_jump = delta_tau.abs() - max_natural_drift;

            if excess_jump > threshold_seconds {
                // Reject isolated single-point spikes: a real VFR frame drop persists
                // into the following observation instead of snapping right back.
                if i + 1 < observations.len() {
                    let next = &observations[i + 1];
                    let span_dt = (next.time_seconds - prev.time_seconds).max(0.0);
                    let net_excess =
                        (next.delay_seconds - prev.delay_seconds).abs() - span_dt * 0.001;
                    if net_excess <= threshold_seconds {
                        i += 2;
                        continue;
                    }
                }

                let split_time = (prev.time_seconds + curr.time_seconds) / 2.0;
                jumps.push(Discontinuity {
                    timestamp_seconds: split_time,
                    step_jump_seconds: delta_tau,
                });
            }

            i += 1;
        }

        jumps
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_detect_frame_drop_step_jump() {
        let obs = vec![
            DriftObservation {
                time_seconds: 0.0,
                delay_seconds: 1.000,
                confidence: 0.9,
            },
            DriftObservation {
                time_seconds: 30.0,
                delay_seconds: 1.001,
                confidence: 0.9,
            },
            DriftObservation {
                time_seconds: 60.0,
                delay_seconds: 1.002,
                confidence: 0.9,
            },
            // Dropped 120ms chunk of frames between t=60 and t=90!
            DriftObservation {
                time_seconds: 90.0,
                delay_seconds: 1.122,
                confidence: 0.9,
            },
            DriftObservation {
                time_seconds: 120.0,
                delay_seconds: 1.123,
                confidence: 0.9,
            },
        ];

        let discontinuities = DiscontinuityDetector::detect_step_jumps(&obs, 0.030); // 30ms threshold
        assert_eq!(discontinuities.len(), 1);
        assert!((discontinuities[0].timestamp_seconds - 75.0).abs() < 1e-4);
        assert!((discontinuities[0].step_jump_seconds - 0.120).abs() < 0.005);
    }

    #[test]
    fn test_isolated_outlier_does_not_trigger_false_split() {
        let obs = vec![
            DriftObservation { time_seconds: 0.0, delay_seconds: 1.000, confidence: 0.9 },
            DriftObservation { time_seconds: 15.0, delay_seconds: 1.001, confidence: 0.9 },
            // Single noisy 15s window spike that immediately reverts at t=45s
            DriftObservation { time_seconds: 30.0, delay_seconds: 2.500, confidence: 0.35 },
            DriftObservation { time_seconds: 45.0, delay_seconds: 1.002, confidence: 0.9 },
            DriftObservation { time_seconds: 60.0, delay_seconds: 1.003, confidence: 0.9 },
        ];
        let jumps = DiscontinuityDetector::detect_step_jumps(&obs, 0.030);
        assert!(jumps.is_empty(), "isolated outlier should not produce splits: {:?}", jumps);
    }
}
