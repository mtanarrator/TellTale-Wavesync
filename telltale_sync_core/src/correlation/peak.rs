#[derive(Debug, Clone, Copy)]
pub struct CorrelationPeak {
    pub integer_lag: isize,
    pub fractional_offset: f64,
    pub exact_lag: f64,
    pub peak_value: f64,
    pub confidence: f64,
}

pub struct PeakDetector;

impl PeakDetector {
    /// Detects the highest correlation peak and applies parabolic sub-sample peak interpolation.
    pub fn find_peak_with_subsample(
        correlation: &[f32],
        center_lag: isize,
    ) -> Option<CorrelationPeak> {
        Self::find_peak_with_options(correlation, center_lag, true, None)
    }

    /// Detects the highest correlation peak, optionally disabling sub-sample
    /// interpolation or constraining the peak search within `±search_radius`
    /// samples around `center_lag`.
    pub fn find_peak_with_options(
        correlation: &[f32],
        center_lag: isize,
        sub_sample: bool,
        search_radius: Option<usize>,
    ) -> Option<CorrelationPeak> {
        if correlation.len() < 3 {
            return None;
        }

        let (search_start, search_end) = match search_radius {
            Some(r) if center_lag >= 0 => {
                let c = center_lag as usize;
                let lo = c.saturating_sub(r).min(correlation.len() - 1);
                let hi = c.saturating_add(r).min(correlation.len() - 1);
                (lo, hi)
            }
            _ => (0, correlation.len() - 1),
        };

        // Find max integer index within search bounds
        let mut max_idx = search_start;
        let mut max_val = f32::NEG_INFINITY;

        for (i, &val) in correlation
            .iter()
            .enumerate()
            .take(search_end + 1)
            .skip(search_start)
        {
            if val > max_val {
                max_val = val;
                max_idx = i;
            }
        }

        // Parabolic interpolation around max_idx
        let delta = if sub_sample && max_idx > 0 && max_idx < correlation.len() - 1 {
            let y_prev = correlation[max_idx - 1] as f64;
            let y_curr = correlation[max_idx] as f64;
            let y_next = correlation[max_idx + 1] as f64;

            let denom = 2.0 * (2.0 * y_curr - y_prev - y_next);
            if denom.abs() > 1e-12 {
                (y_next - y_prev) / denom
            } else {
                0.0
            }
        } else {
            0.0
        };

        let delta_clamped = delta.max(-0.5).min(0.5);
        let integer_lag = max_idx as isize - center_lag;
        let exact_lag = integer_lag as f64 + delta_clamped;

        // Compute noise floor statistics excluding immediate peak neighbors
        let mut noise_sum = 0.0f64;
        let mut noise_sum_sq = 0.0f64;
        let mut noise_count = 0.0f64;

        let peak_margin = 1isize;
        for (i, &val) in correlation.iter().enumerate() {
            if (i as isize - max_idx as isize).abs() > peak_margin {
                let v = val as f64;
                noise_sum += v;
                noise_sum_sq += v * v;
                noise_count += 1.0;
            }
        }

        let confidence = if noise_count > 0.0 {
            let noise_mean = noise_sum / noise_count;
            let noise_var = (noise_sum_sq / noise_count - noise_mean * noise_mean).max(1e-12);
            let noise_std = noise_var.sqrt();
            let z_score = ((max_val as f64) - noise_mean) / noise_std;
            (1.0 - (-z_score / 3.0).exp()).max(0.0).min(1.0)
        } else {
            0.85
        };

        Some(CorrelationPeak {
            integer_lag,
            fractional_offset: delta_clamped,
            exact_lag,
            peak_value: max_val as f64,
            confidence,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_exact_integer_peak() {
        let corr = vec![0.1f32, 0.5, 1.0, 0.5, 0.1];
        let peak = PeakDetector::find_peak_with_subsample(&corr, 2).unwrap();
        assert_eq!(peak.integer_lag, 0);
        assert!(peak.fractional_offset.abs() < 1e-4);
        assert!(peak.exact_lag.abs() < 1e-4);
        assert!(peak.confidence > 0.5);
    }

    #[test]
    fn test_fractional_peak_shift() {
        let corr = vec![0.1f32, 0.4, 0.9, 0.7, 0.1];
        let peak = PeakDetector::find_peak_with_subsample(&corr, 2).unwrap();
        assert_eq!(peak.integer_lag, 0);
        assert!(peak.fractional_offset > 0.0);
        assert!(peak.fractional_offset <= 0.5);
    }
}
