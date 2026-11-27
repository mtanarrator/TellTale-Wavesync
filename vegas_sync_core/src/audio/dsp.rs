use rayon::prelude::*;
use rubato::{
    Resampler, SincFixedIn, SincInterpolationParameters, SincInterpolationType, WindowFunction,
};
use std::f32::consts::PI;

pub struct DspProcessor;

impl DspProcessor {
    /// Eliminates DC offset by subtracting the mean amplitude.
    pub fn remove_dc_offset(samples: &mut [f32]) {
        if samples.is_empty() {
            return;
        }
        let sum: f64 = samples.iter().map(|&s| s as f64).sum();
        let mean = (sum / samples.len() as f64) as f32;
        for s in samples.iter_mut() {
            *s -= mean;
        }
    }

    /// Resamples a single-channel (mono) audio buffer from `source_rate` to `target_rate`
    /// using band-limited Kaiser-windowed sinc interpolation.
    pub fn resample_mono(
        samples: &[f32],
        source_rate: u32,
        target_rate: u32,
    ) -> Result<Vec<f32>, String> {
        if samples.is_empty() {
            return Ok(Vec::new());
        }
        if source_rate == target_rate {
            return Ok(samples.to_vec());
        }

        let params = SincInterpolationParameters {
            sinc_len: 128,
            f_cutoff: 0.95,
            interpolation: SincInterpolationType::Linear,
            oversampling_factor: 128,
            window: WindowFunction::BlackmanHarris2,
        };

        let mut resampler = SincFixedIn::<f32>::new(
            target_rate as f64 / source_rate as f64,
            2.0,
            params,
            samples.len(),
            1,
        )
        .map_err(|e| format!("Failed to create SincFixedIn resampler: {}", e))?;

        let waves_in = vec![samples.to_vec()];
        let ratio = target_rate as f64 / source_rate as f64;
        let expected_len = ((samples.len() as f64 * ratio).round() as usize).max(1);
        let delay = resampler.output_delay();

        let mut buffer: Vec<f32> = resampler
            .process(&waves_in, None)
            .map_err(|e| format!("Resampling failed: {}", e))?
            .into_iter()
            .next()
            .unwrap_or_default();

        // `SincFixedIn` seeds its history so that output frame 0 already corresponds
        // to input frame 0: the interpolator group delay is compensated internally
        // (confirmed by the impulse test). Trimming `output_delay()` from the head
        // would therefore shift audio ~1 ms early at 16 kHz. Instead we only use it
        // to size the tail flush, and truncate to the exact expected duration.
        let mut flushes = 0usize;
        while buffer.len() < expected_len + delay && flushes < 8 {
            let chunk = resampler
                .process_partial::<Vec<f32>>(None, None)
                .map_err(|e| format!("Resampling flush failed: {}", e))?
                .into_iter()
                .next()
                .unwrap_or_default();
            if chunk.is_empty() {
                break;
            }
            buffer.extend_from_slice(&chunk);
            flushes += 1;
        }

        let mut resampled: Vec<f32> = buffer;
        resampled.truncate(expected_len);
        Ok(resampled)
    }

    /// Generates a bandpass FIR filter kernel using the windowed sinc method (Hamming window).
    /// Low cutoff = 300 Hz, High cutoff = 4000 Hz, Sample rate = 16000 Hz.
    pub fn design_bandpass_fir(
        filter_order: usize,
        low_cutoff_hz: f32,
        high_cutoff_hz: f32,
        sample_rate: f32,
    ) -> Vec<f32> {
        let n = if filter_order % 2 == 0 {
            filter_order + 1
        } else {
            filter_order
        };
        let m = (n - 1) as f32;
        let mut kernel = vec![0.0f32; n];

        let f_low = low_cutoff_hz / sample_rate;
        let f_high = high_cutoff_hz / sample_rate;

        let center = m / 2.0;

        for i in 0..n {
            let k = i as f32 - center;
            let sinc_high = if k.abs() < 1e-7 {
                2.0 * f_high
            } else {
                (2.0 * PI * f_high * k).sin() / (PI * k)
            };

            let sinc_low = if k.abs() < 1e-7 {
                2.0 * f_low
            } else {
                (2.0 * PI * f_low * k).sin() / (PI * k)
            };

            let bandpass = sinc_high - sinc_low;
            let window = 0.54 - 0.46 * (2.0 * PI * i as f32 / m).cos();
            kernel[i] = bandpass * window;
        }

        kernel
    }

    /// Convolves the 1D signal with the FIR filter kernel (parallelised with Rayon).
    /// Long takes run many times faster than the previous single-threaded loop.
    pub fn apply_fir_filter(signal: &[f32], kernel: &[f32]) -> Vec<f32> {
        if signal.is_empty() || kernel.is_empty() {
            return signal.to_vec();
        }

        let k_len = kernel.len();
        let s_len = signal.len();
        let half_k = k_len / 2;
        let mut output = vec![0.0f32; s_len];

        const CHUNK: usize = 32 * 1024;
        output
            .par_chunks_mut(CHUNK)
            .enumerate()
            .for_each(|(chunk_idx, out)| {
                let base = chunk_idx * CHUNK;
                for (j, o) in out.iter_mut().enumerate() {
                    let i = base + j;
                    let mut acc = 0.0f32;
                    for (k, &kv) in kernel.iter().enumerate() {
                        let sig_idx = i as isize + k as isize - half_k as isize;
                        if sig_idx >= 0 && (sig_idx as usize) < s_len {
                            acc += signal[sig_idx as usize] * kv;
                        }
                    }
                    *o = acc;
                }
            });

        output
    }

    pub fn condition_audio(
        raw_samples: &[f32],
        source_rate: u32,
        target_rate: u32,
    ) -> Result<Vec<f32>, String> {
        if raw_samples.is_empty() {
            return Ok(Vec::new());
        }

        let mut samples = raw_samples.to_vec();
        Self::remove_dc_offset(&mut samples);

        let resampled = if source_rate != target_rate {
            Self::resample_mono(&samples, source_rate, target_rate)?
        } else {
            samples
        };

        let fir_kernel = Self::design_bandpass_fir(65, 300.0, 4000.0, target_rate as f32);
        let mut filtered = Self::apply_fir_filter(&resampled, &fir_kernel);

        let max_amp = filtered
            .iter()
            .map(|&s| s.abs())
            .fold(0.0f32, |a, b| a.max(b));
        if max_amp > 1e-6 {
            let inv_max = 1.0 / max_amp;
            for s in filtered.iter_mut() {
                *s *= inv_max;
            }
        }

        Ok(filtered)
    }

    /// Energy-based Voice Activity Detection (VAD) filter.
    /// Zeros out noise floor/silence below threshold_db (e.g. -40 dB)
    /// to prevent false cross-correlation peaks from room ambience or stage bleed.
    pub fn apply_voice_activity_filter(samples: &mut [f32], frame_size: usize, threshold_db: f32) {
        if samples.len() < frame_size {
            return;
        }
        let threshold_linear = 10.0f32.powf(threshold_db / 20.0);

        for chunk in samples.chunks_mut(frame_size) {
            let mut sum_sq = 0.0f32;
            for &s in chunk.iter() {
                sum_sq += s * s;
            }
            let rms = (sum_sq / chunk.len() as f32).sqrt();
            if rms < threshold_linear {
                for s in chunk.iter_mut() {
                    *s = 0.0;
                }
            }
        }
    }
}

#[derive(Debug, Clone, Copy)]
struct Biquad {
    b0: f64,
    b1: f64,
    b2: f64,
    a1: f64,
    a2: f64,
    z1: f64,
    z2: f64,
}

impl Biquad {
    fn highpass(cutoff_hz: f64, sample_rate: f64) -> Self {
        let fs = sample_rate.max(1000.0);
        let fc = cutoff_hz.clamp(10.0, fs * 0.45);
        let w0 = 2.0 * std::f64::consts::PI * fc / fs;
        let cos_w0 = w0.cos();
        let sin_w0 = w0.sin();
        let q = std::f64::consts::FRAC_1_SQRT_2;
        let alpha = sin_w0 / (2.0 * q);
        let a0 = 1.0 + alpha;
        Self {
            b0: ((1.0 + cos_w0) * 0.5) / a0,
            b1: (-(1.0 + cos_w0)) / a0,
            b2: ((1.0 + cos_w0) * 0.5) / a0,
            a1: (-2.0 * cos_w0) / a0,
            a2: (1.0 - alpha) / a0,
            z1: 0.0,
            z2: 0.0,
        }
    }

    fn lowpass(cutoff_hz: f64, sample_rate: f64) -> Self {
        let fs = sample_rate.max(1000.0);
        let fc = cutoff_hz.clamp(10.0, fs * 0.45);
        let w0 = 2.0 * std::f64::consts::PI * fc / fs;
        let cos_w0 = w0.cos();
        let sin_w0 = w0.sin();
        let q = std::f64::consts::FRAC_1_SQRT_2;
        let alpha = sin_w0 / (2.0 * q);
        let a0 = 1.0 + alpha;
        Self {
            b0: ((1.0 - cos_w0) * 0.5) / a0,
            b1: (1.0 - cos_w0) / a0,
            b2: ((1.0 - cos_w0) * 0.5) / a0,
            a1: (-2.0 * cos_w0) / a0,
            a2: (1.0 - alpha) / a0,
            z1: 0.0,
            z2: 0.0,
        }
    }

    #[inline]
    fn process(&mut self, x: f64) -> f64 {
        let y = self.b0 * x + self.z1;
        self.z1 = self.b1 * x - self.a1 * y + self.z2;
        self.z2 = self.b2 * x - self.a2 * y;
        y
    }
}

/// Streaming zero-allocation 2nd-order Butterworth bandpass filter used when
/// building envelopes on multi-hour recordings so long-take envelopes match
/// the 300–4000 Hz conditioning applied to shorter clips.
pub struct StreamingBandpassFilter {
    hp: Biquad,
    lp: Biquad,
}

impl StreamingBandpassFilter {
    pub fn new(low_hz: f64, high_hz: f64, sample_rate: f64) -> Self {
        Self {
            hp: Biquad::highpass(low_hz, sample_rate),
            lp: Biquad::lowpass(high_hz, sample_rate),
        }
    }

    pub fn process_in_place(&mut self, samples: &mut [f32]) {
        for s in samples.iter_mut() {
            let y = self.lp.process(self.hp.process(*s as f64));
            *s = y as f32;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_remove_dc_offset() {
        let mut samples = vec![10.0, 11.0, 12.0, 13.0, 14.0];
        DspProcessor::remove_dc_offset(&mut samples);
        let sum: f32 = samples.iter().sum();
        assert!(sum.abs() < 1e-5);
    }

    #[test]
    fn test_fir_filter_design() {
        let kernel = DspProcessor::design_bandpass_fir(65, 300.0, 4000.0, 16000.0);
        assert_eq!(kernel.len(), 65);
        // Symmetry check
        for i in 0..kernel.len() / 2 {
            assert!((kernel[i] - kernel[kernel.len() - 1 - i]).abs() < 1e-6);
        }
    }

    #[test]
    fn test_resampling_and_conditioning() {
        // Generate 1 second of 1000 Hz test tone at 48 kHz with DC bias
        let sample_rate = 48000;
        let mut tone = Vec::with_capacity(sample_rate);
        for i in 0..sample_rate {
            let t = i as f32 / sample_rate as f32;
            let sample = (2.0 * PI * 1000.0 * t).sin() + 2.5; // with +2.5 DC offset
            tone.push(sample);
        }

        let conditioned = DspProcessor::condition_audio(&tone, 48000, 16000).unwrap();
        // Should be approximately 16000 samples (target rate * 1s)
        assert!((conditioned.len() as isize - 16000).abs() < 200);

        // Peak normalized to <= 1.0
        let max_amp = conditioned
            .iter()
            .map(|&s| s.abs())
            .fold(0.0f32, |a, b| a.max(b));
        assert!(max_amp <= 1.0001);
    }

    #[test]
    fn test_resample_mono_delay_compensated_length() {
        let source_rate = 48_000u32;
        let target_rate = 16_000u32;
        let input_len = 48_000usize;
        let input: Vec<f32> = (0..input_len)
            .map(|i| (2.0 * PI * 440.0 * i as f32 / source_rate as f32).sin())
            .collect();

        let output = DspProcessor::resample_mono(&input, source_rate, target_rate).unwrap();
        let expected = input_len as f64 * target_rate as f64 / source_rate as f64;
        assert!(
            (output.len() as f64 - expected).abs() <= 4.0,
            "expected ~{} samples, got {}",
            expected,
            output.len()
        );
    }

    #[test]
    fn test_resample_mono_impulse_alignment() {
        let source_rate = 48_000u32;
        let target_rate = 16_000u32;
        let input_len = 48_000usize;
        let impulse_index = input_len / 4;
        let mut input = vec![0.0f32; input_len];
        input[impulse_index] = 1.0;

        let output = DspProcessor::resample_mono(&input, source_rate, target_rate).unwrap();
        let (peak_index, peak) = output
            .iter()
            .enumerate()
            .max_by(|(_, a), (_, b)| {
                a.abs()
                    .partial_cmp(&b.abs())
                    .unwrap_or(std::cmp::Ordering::Equal)
            })
            .expect("non-empty output");
        assert!(peak.abs() > 1e-3, "impulse energy lost: peak {}", peak);
        let expected_index = impulse_index as f64 * target_rate as f64 / source_rate as f64;
        let tolerance = target_rate as f64 / 1000.0;
        assert!(
            (peak_index as f64 - expected_index).abs() <= tolerance,
            "impulse expected near {}, found at {}",
            expected_index,
            peak_index
        );
    }

    #[test]
    fn test_resample_mono_empty_input() {
        let output = DspProcessor::resample_mono(&[], 48_000, 16_000).unwrap();
        assert!(output.is_empty());
    }

    #[test]
    fn test_voice_activity_filter() {
        // 512 samples of loud signal + 512 samples of quiet noise (-60 dB)
        let mut signal = vec![0.8f32; 512];
        let mut noise = vec![0.001f32; 512]; // -60 dB
        signal.append(&mut noise);

        DspProcessor::apply_voice_activity_filter(&mut signal, 512, -40.0);

        // First 512 preserved
        assert_eq!(signal[0], 0.8f32);
        // Last 512 zeroed out by VAD
        assert_eq!(signal[512], 0.0f32);
    }

    #[test]
    fn test_streaming_bandpass_filter_attenuates_dc_and_passes_voice() {
        let rate = 16000.0;
        let n = 8000;
        let mut rumble_plus_dc: Vec<f32> = (0..n)
            .map(|i| 1.5 + 0.5 * (2.0 * PI * 40.0 * i as f32 / rate as f32).sin())
            .collect();
        let mut filter = StreamingBandpassFilter::new(300.0, 4000.0, rate);
        filter.process_in_place(&mut rumble_plus_dc);
        let tail_rms = (rumble_plus_dc[n / 2..].iter().map(|s| s * s).sum::<f32>() / (n / 2) as f32).sqrt();
        assert!(tail_rms < 0.03, "expected 40Hz+DC to be strongly attenuated, got {}", tail_rms);

        let mut speech: Vec<f32> = (0..n)
            .map(|i| (2.0 * PI * 1000.0 * i as f32 / rate as f32).sin())
            .collect();
        let mut filter2 = StreamingBandpassFilter::new(300.0, 4000.0, rate);
        filter2.process_in_place(&mut speech);
        let speech_rms = (speech[n / 2..].iter().map(|s| s * s).sum::<f32>() / (n / 2) as f32).sqrt();
        assert!(speech_rms > 0.6, "expected 1kHz tone to pass, got {}", speech_rms);
    }
}
