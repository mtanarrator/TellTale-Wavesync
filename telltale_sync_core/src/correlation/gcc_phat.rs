use rustfft::num_complex::Complex;
use realfft::RealFftPlanner;
use std::sync::Mutex;

use super::peak::{CorrelationPeak, PeakDetector};

/// Hard upper bound for a single GCC-PHAT call (sum of both input lengths).
///
/// Keeping this bounded is what prevents multi-hour takes from allocating
/// multi-gigabyte FFT buffers (which is what made long wedding recordings
/// "hang" the extension). Callers must window long material first.
pub const MAX_CORRELATION_INPUT_SAMPLES: usize = 6_000_000;

pub struct GccPhat {
    planner: Mutex<RealFftPlanner<f32>>,
}

impl Default for GccPhat {
    fn default() -> Self {
        Self::new()
    }
}

impl GccPhat {
    pub fn new() -> Self {
        Self {
            planner: Mutex::new(RealFftPlanner::<f32>::new()),
        }
    }

    /// Computes GCC-PHAT cross correlation between signals `x` (master) and `y` (slave)
    /// and finds the peak delay with sub-sample interpolation.
    /// Returns the exact lag in samples (`lag > 0` means `y` started after `x` on timeline;
    /// `resolved_slave_start = master_start + (lag / sample_rate)`).
    pub fn correlate(&self, x: &[f32], y: &[f32]) -> Result<CorrelationPeak, String> {
        self.correlate_with_options(x, y, true, None)
    }

    /// Computes GCC-PHAT cross-correlation with optional sub-sample interpolation
    /// and optional symmetric lag search radius (`±search_radius` samples around lag 0).
    pub fn correlate_with_options(
        &self,
        x: &[f32],
        y: &[f32],
        sub_sample: bool,
        search_radius: Option<usize>,
    ) -> Result<CorrelationPeak, String> {
        if x.is_empty() || y.is_empty() {
            return Err("Input signals must not be empty".into());
        }

        if x.len() + y.len() > MAX_CORRELATION_INPUT_SAMPLES {
            return Err(format!(
                "Correlation window too large ({} + {} samples > {}); window long media before correlating",
                x.len(),
                y.len(),
                MAX_CORRELATION_INPUT_SAMPLES
            ));
        }

        // Determine FFT size (next power of 2 of x.len() + y.len())
        let min_len = x.len() + y.len();
        let fft_len = min_len.next_power_of_two();

        let (r2c, c2r) = {
            let mut planner = self.planner.lock().map_err(|e| format!("Lock error: {}", e))?;
            (planner.plan_fft_forward(fft_len), planner.plan_fft_inverse(fft_len))
        };

        let mut in_x = vec![0.0f32; fft_len];
        let mut in_y = vec![0.0f32; fft_len];

        in_x[..x.len()].copy_from_slice(x);
        in_y[..y.len()].copy_from_slice(y);

        let mut out_x = r2c.make_output_vec();
        let mut out_y = r2c.make_output_vec();

        r2c.process(&mut in_x, &mut out_x)
            .map_err(|e| format!("FFT forward error for x: {:?}", e))?;
        r2c.process(&mut in_y, &mut out_y)
            .map_err(|e| format!("FFT forward error for y: {:?}", e))?;

        // Cross-spectral phase transform: G_xy = (X * Y*) / |X * Y*|
        // If x has feature at tm and y has feature at ts, peak occurs at tau = tm - ts.
        // Therefore, y_start = x_start + tau.
        let mut g_xy = vec![Complex::new(0.0f32, 0.0f32); out_x.len()];
        let eps = 1e-12f32;

        for i in 0..out_x.len() {
            let prod = out_x[i] * out_y[i].conj();
            let norm = prod.norm() + eps;
            g_xy[i] = prod / norm;
        }

        // Inverse FFT
        let mut correlation_raw = c2r.make_output_vec();
        c2r.process(&mut g_xy, &mut correlation_raw)
            .map_err(|e| format!("FFT inverse error: {:?}", e))?;

        // Rearrange circular correlation to linear centered buffer:
        // Positive lags [0 .. x.len()]: correlation_raw[lag]
        // Negative lags [-y.len() .. -1]: correlation_raw[fft_len - lag]
        let max_lag = x.len().max(y.len());
        let center = max_lag;
        let mut centered_corr = vec![0.0f32; 2 * max_lag + 1];

        // Positive lags [0 .. x.len()] (Master tm >= Slave ts, Slave started after Master)
        for lag in 0..=x.len() {
            centered_corr[center + lag] = correlation_raw[lag];
        }

        // Negative lags [-y.len() .. -1] (Master tm < Slave ts, Slave started before Master)
        for lag in 1..=y.len() {
            let circ_idx = fft_len - lag;
            centered_corr[center - lag] = correlation_raw[circ_idx];
        }

        PeakDetector::find_peak_with_options(&centered_corr, center as isize, sub_sample, search_radius)
            .ok_or_else(|| "Failed to detect correlation peak".to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_gcc_phat_zero_delay() {
        let n = 2048;
        let mut x = vec![0.0f32; n];
        for i in 0..n {
            let t = i as f32 / n as f32;
            x[i] = (2.0 * std::f32::consts::PI * (200.0 + 800.0 * t) * t).sin();
        }

        let gcc = GccPhat::new();
        let peak = gcc.correlate(&x, &x).unwrap();

        assert_eq!(peak.integer_lag, 0);
        assert!(peak.exact_lag.abs() < 0.05);
        assert!(peak.confidence > 0.8);
    }

    #[test]
    fn test_gcc_phat_slave_starts_after_master() {
        // Master (x) has chirp at sample 42, Slave (y) has chirp at sample 0.
        // Therefore Slave started 42 samples AFTER Master. Lag should be +42.
        let n = 4096;
        let delay_samples = 42;
        let mut sound = vec![0.0f32; n];
        for i in 0..n {
            let t = i as f32 / n as f32;
            sound[i] = (2.0 * std::f32::consts::PI * (100.0 + 1500.0 * t) * t).sin();
        }

        let mut x = vec![0.0f32; n];
        for i in delay_samples..n {
            x[i] = sound[i - delay_samples];
        }
        let y = sound;

        let gcc = GccPhat::new();
        let peak = gcc.correlate(&x, &y).unwrap();

        assert_eq!(peak.integer_lag, delay_samples as isize);
        assert!((peak.exact_lag - delay_samples as f64).abs() < 0.05);
        assert!(peak.confidence > 0.8);
    }

    #[test]
    fn test_gcc_phat_slave_starts_before_master() {
        // Master (x) has chirp at sample 0, Slave (y) has chirp at sample 42.
        // Therefore Slave started 42 samples BEFORE Master. Lag should be -42.
        let n = 4096;
        let delay_samples = 42;
        let mut sound = vec![0.0f32; n];
        for i in 0..n {
            let t = i as f32 / n as f32;
            sound[i] = (2.0 * std::f32::consts::PI * (100.0 + 1500.0 * t) * t).sin();
        }

        let x = sound.clone();
        let mut y = vec![0.0f32; n];
        for i in delay_samples..n {
            y[i] = sound[i - delay_samples];
        }

        let gcc = GccPhat::new();
        let peak = gcc.correlate(&x, &y).unwrap();

        assert_eq!(peak.integer_lag, -(delay_samples as isize));
        assert!((peak.exact_lag - (-(delay_samples as f64))).abs() < 0.05);
        assert!(peak.confidence > 0.8);
    }
}
