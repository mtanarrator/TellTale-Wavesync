use realfft::RealFftPlanner;

use super::gcc_phat::GccPhat;
use super::peak::CorrelationPeak;

pub struct AcousticFingerprinter;

impl AcousticFingerprinter {
    /// Computes a 16-band log-spectral flux fingerprint sequence for extreme noise resilience.
    /// Captures frequency-resolved energy onset transitions across time using an FFT.
    pub fn extract_spectral_flux(
        samples: &[f32],
        sample_rate: u32,
        frame_size: usize,
        hop_size: usize,
    ) -> Vec<f32> {
        if samples.len() < frame_size || frame_size < 32 || hop_size == 0 {
            return Vec::new();
        }

        let num_frames = (samples.len() - frame_size) / hop_size + 1;
        let mut flux_sequence = Vec::with_capacity(num_frames);

        let num_bands = 16usize;
        let fft_len = frame_size.next_power_of_two();
        let mut planner = RealFftPlanner::<f32>::new();
        let r2c = planner.plan_fft_forward(fft_len);

        let hann: Vec<f32> = (0..frame_size)
            .map(|i| {
                0.5 * (1.0 - (2.0 * std::f32::consts::PI * i as f32 / (frame_size - 1) as f32).cos())
            })
            .collect();

        let mut in_buf = vec![0.0f32; fft_len];
        let mut spectrum = r2c.make_output_vec();
        let num_bins = spectrum.len();

        // Map frequency bins across ~150 Hz .. min(6000 Hz, Nyquist) into 16 log-spaced bands.
        let sr = sample_rate.max(1000) as f64;
        let f_min = 150.0f64.min(sr * 0.1);
        let f_max = 6000.0f64.min(sr * 0.48).max(f_min * 2.0);
        let mut band_edges = Vec::with_capacity(num_bands + 1);
        for b in 0..=num_bands {
            let frac = b as f64 / num_bands as f64;
            let freq = f_min * (f_max / f_min).powf(frac);
            let bin = ((freq * fft_len as f64) / sr).round() as usize;
            band_edges.push(bin.clamp(1, num_bins));
        }

        let mut prev_energy = vec![0.0f32; num_bands];
        let mut current_energy = vec![0.0f32; num_bands];

        for f in 0..num_frames {
            let start = f * hop_size;
            for i in 0..frame_size {
                in_buf[i] = samples[start + i] * hann[i];
            }
            for x in in_buf[frame_size..].iter_mut() {
                *x = 0.0;
            }

            if r2c.process(&mut in_buf, &mut spectrum).is_err() {
                flux_sequence.push(0.0);
                continue;
            }

            for b in 0..num_bands {
                let lo = band_edges[b];
                let hi = band_edges[b + 1].max(lo + 1).min(num_bins);
                let mut band_pow = 0.0f32;
                for bin in &spectrum[lo..hi] {
                    band_pow += bin.re * bin.re + bin.im * bin.im;
                }
                current_energy[b] = (band_pow + 1e-7).ln();
            }

            // Half-wave rectified spectral flux
            let mut flux = 0.0f32;
            for b in 0..num_bands {
                let diff = current_energy[b] - prev_energy[b];
                if diff > 0.0 {
                    flux += diff;
                }
            }

            flux_sequence.push(flux);
            std::mem::swap(&mut prev_energy, &mut current_energy);
        }

        // Center flux around zero mean
        let mean = flux_sequence.iter().sum::<f32>() / (flux_sequence.len().max(1) as f32);
        for x in flux_sequence.iter_mut() {
            *x -= mean;
        }

        flux_sequence
    }

    /// Correlates acoustic fingerprint embeddings between master and slave using phase transforms.
    /// Used as an automatic fallback when raw waveform GCC-PHAT SNR is beneath threshold (< 0.40).
    pub fn match_fingerprints(
        master: &[f32],
        slave: &[f32],
        sample_rate: u32,
    ) -> Option<CorrelationPeak> {
        let gcc = GccPhat::new();
        Self::match_fingerprints_with_gcc(master, slave, sample_rate, &gcc, true, None)
    }

    /// Reuses an existing [`GccPhat`] planner and optionally constrains the sample search radius.
    pub fn match_fingerprints_with_gcc(
        master: &[f32],
        slave: &[f32],
        sample_rate: u32,
        gcc: &GccPhat,
        sub_sample: bool,
        search_radius_samples: Option<usize>,
    ) -> Option<CorrelationPeak> {
        let frame_size = 512; // 32 ms at 16 kHz
        let hop_size = 256;   // 16 ms hop

        let fp_master = Self::extract_spectral_flux(master, sample_rate, frame_size, hop_size);
        let fp_slave = Self::extract_spectral_flux(slave, sample_rate, frame_size, hop_size);

        if fp_master.is_empty() || fp_slave.is_empty() {
            return None;
        }

        let frame_radius = search_radius_samples.map(|r| (r / hop_size).max(2));
        let flux_peak = gcc
            .correlate_with_options(&fp_master, &fp_slave, sub_sample, frame_radius)
            .ok()?;

        let sample_lag = flux_peak.exact_lag * hop_size as f64;

        Some(CorrelationPeak {
            integer_lag: sample_lag.round() as isize,
            fractional_offset: flux_peak.fractional_offset,
            exact_lag: sample_lag,
            peak_value: flux_peak.peak_value,
            confidence: flux_peak.confidence * 0.90,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_acoustic_fingerprint_matching_under_noise() {
        let sample_rate = 16000;
        let duration = 5.0;
        let n = (sample_rate as f32 * duration) as usize;

        // Aperiodic speech-like transient bursts (non-repetitive pattern at 0.5s, 1.4s, 2.7s, 3.9s)
        let mut master = vec![0.0f32; n];
        for i in 0..n {
            let t = i as f32 / sample_rate as f32;
            let is_burst = (t > 0.5 && t < 0.6)
                || (t > 1.4 && t < 1.55)
                || (t > 2.7 && t < 2.9)
                || (t > 3.9 && t < 4.1);
            let amp = if is_burst { 1.0 } else { 0.02 };
            master[i] = amp * (2.0 * std::f32::consts::PI * (300.0 + 500.0 * t) * t).sin();
        }

        // Slave has sound delayed by 8,192 samples (t_s = t_m + 8,192).
        // That means Slave started 8,192 samples BEFORE Master (timeline offset = -8192).
        let delay_samples = 8192;
        let mut slave = vec![0.0f32; n];
        for i in delay_samples..n {
            let noise = ((i % 13) as f32 / 13.0) * 0.05;
            slave[i] = master[i - delay_samples] + noise;
        }

        let peak = AcousticFingerprinter::match_fingerprints(&master, &slave, sample_rate)
            .expect("Fingerprint matching failed");

        // Should resolve to -delay_samples within ±hop_size samples
        assert!(
            (peak.exact_lag - (-(delay_samples as f64))).abs() <= 512.0,
            "Fingerprint exact lag was {}, expected {}",
            peak.exact_lag,
            -(delay_samples as f64)
        );
        assert!(peak.confidence > 0.5);
    }
}
