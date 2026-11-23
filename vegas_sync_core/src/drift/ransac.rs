#[derive(Debug, Clone, Copy)]
pub struct DriftObservation {
    pub time_seconds: f64,
    pub delay_seconds: f64,
    pub confidence: f64,
}

#[derive(Debug, Clone)]
pub struct RansacResult {
    pub slope: f64,          // m (drift rate, unitless)
    pub intercept: f64,      // c (start offset in seconds)
    pub drift_ppm: f64,      // m * 1,000,000
    pub playback_rate: f64,  // 1.0 + m
    pub inliers: Vec<usize>,
    pub r_squared: f64,
}

pub struct RansacRegression;

impl RansacRegression {
    /// Fits a robust linear model tau(t) = m * t + c using RANSAC.
    /// Rejects false correlation peaks caused by silence or background noise.
    pub fn fit(
        observations: &[DriftObservation],
        max_iterations: usize,
        residual_threshold_seconds: f64,
        min_inlier_ratio: f64,
    ) -> Option<RansacResult> {
        let n = observations.len();
        if n < 2 {
            return None;
        }

        if n == 2 {
            let p1 = &observations[0];
            let p2 = &observations[1];
            let dt = p2.time_seconds - p1.time_seconds;
            if dt.abs() < 1e-6 {
                return None;
            }
            let m = (p2.delay_seconds - p1.delay_seconds) / dt;
            let c = p1.delay_seconds - m * p1.time_seconds;
            return Some(RansacResult {
                slope: m,
                intercept: c,
                drift_ppm: m * 1_000_000.0,
                playback_rate: 1.0 + m,
                inliers: vec![0, 1],
                r_squared: 1.0,
            });
        }

        let mut best_inliers = Vec::new();

        // Simple deterministic pseudo-random sequence for reproducible RANSAC
        let mut seed: u64 = 0x9E3779B97F4A7C15;

        for _ in 0..max_iterations {
            // Pick two distinct points
            seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1);
            let idx1 = (seed >> 32) as usize % n;
            seed = seed.wrapping_mul(6364136223846793005).wrapping_add(1);
            let mut idx2 = (seed >> 32) as usize % n;
            if idx1 == idx2 {
                idx2 = (idx1 + 1) % n;
            }

            let p1 = &observations[idx1];
            let p2 = &observations[idx2];
            let dt = p2.time_seconds - p1.time_seconds;
            if dt.abs() < 1e-3 {
                continue;
            }

            let candidate_m = (p2.delay_seconds - p1.delay_seconds) / dt;
            // Reject unphysically extreme clock drift (> 1000 PPM) early
            if (candidate_m * 1_000_000.0).abs() > 2000.0 {
                continue;
            }

            let candidate_c = p1.delay_seconds - candidate_m * p1.time_seconds;

            // Count inliers
            let mut current_inliers = Vec::new();
            for (i, obs) in observations.iter().enumerate() {
                let predicted = candidate_m * obs.time_seconds + candidate_c;
                let residual = (obs.delay_seconds - predicted).abs();
                if residual <= residual_threshold_seconds {
                    current_inliers.push(i);
                }
            }

            if current_inliers.len() > best_inliers.len() {
                best_inliers = current_inliers;
            }
        }

        let min_required = ((n as f64) * min_inlier_ratio).max(2.0) as usize;
        if best_inliers.len() < min_required {
            return None;
        }

        // Refine model with Ordinary Least Squares on all consensus inliers
        let (ref_m, ref_c, r2) = Self::refine_ols(observations, &best_inliers);

        Some(RansacResult {
            slope: ref_m,
            intercept: ref_c,
            drift_ppm: ref_m * 1_000_000.0,
            playback_rate: 1.0 + ref_m,
            inliers: best_inliers,
            r_squared: r2,
        })
    }

    fn refine_ols(
        observations: &[DriftObservation],
        inliers: &[usize],
    ) -> (f64, f64, f64) {
        let count = inliers.len() as f64;
        let mut sum_t = 0.0f64;
        let mut sum_y = 0.0f64;
        let mut sum_tt = 0.0f64;
        let mut sum_ty = 0.0f64;

        for &idx in inliers {
            let obs = &observations[idx];
            sum_t += obs.time_seconds;
            sum_y += obs.delay_seconds;
            sum_tt += obs.time_seconds * obs.time_seconds;
            sum_ty += obs.time_seconds * obs.delay_seconds;
        }

        let denom = count * sum_tt - sum_t * sum_t;
        if denom.abs() < 1e-12 {
            return (0.0, sum_y / count, 0.0);
        }

        let m = (count * sum_ty - sum_t * sum_y) / denom;
        let c = (sum_y - m * sum_t) / count;

        // Compute R^2
        let mean_y = sum_y / count;
        let mut ss_tot = 0.0f64;
        let mut ss_res = 0.0f64;
        for &idx in inliers {
            let obs = &observations[idx];
            let pred = m * obs.time_seconds + c;
            ss_tot += (obs.delay_seconds - mean_y).powi(2);
            ss_res += (obs.delay_seconds - pred).powi(2);
        }

        let r2 = if ss_tot > 1e-12 {
            (1.0 - ss_res / ss_tot).max(0.0).min(1.0)
        } else {
            1.0
        };

        (m, c, r2)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_ransac_drift_recovery_with_outliers() {
        // True model: tau(t) = 0.000035 * t + 1.250 (35 PPM drift, 1.250s offset)
        let true_m = 0.000035;
        let true_c = 1.250;

        let mut observations = Vec::new();
        // 20 valid points
        for i in 0..20 {
            let t = i as f64 * 30.0; // every 30 seconds
            let delay = true_m * t + true_c;
            observations.push(DriftObservation {
                time_seconds: t,
                delay_seconds: delay,
                confidence: 0.95,
            });
        }

        // Add 5 severe outliers (false peaks / room thumps)
        observations.push(DriftObservation {
            time_seconds: 60.0,
            delay_seconds: -4.5,
            confidence: 0.3,
        });
        observations.push(DriftObservation {
            time_seconds: 180.0,
            delay_seconds: 12.0,
            confidence: 0.2,
        });
        observations.push(DriftObservation {
            time_seconds: 300.0,
            delay_seconds: -0.2,
            confidence: 0.4,
        });

        let result = RansacRegression::fit(&observations, 200, 0.010, 0.6).unwrap();

        assert!((result.intercept - true_c).abs() < 1e-4);
        assert!((result.slope - true_m).abs() < 1e-6);
        assert!((result.drift_ppm - 35.0).abs() < 1.0);
        assert!(result.r_squared > 0.99);
        assert_eq!(result.inliers.len(), 20); // Successfully rejected all 3 outliers!
    }
}
