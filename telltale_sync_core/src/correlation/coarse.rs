use realfft::RealFftPlanner;
use rustfft::num_complex::Complex;

/// Default envelope analysis constants for 16 kHz audio:
/// frame = 256 samples (16 ms), hop = 128 samples (8 ms -> 125 Hz envelope rate).
pub const ENVELOPE_FRAME_SIZE: usize = 256;
pub const ENVELOPE_HOP_SIZE: usize = 128;

/// Result of a coarse (envelope-domain) alignment.
#[derive(Debug, Clone, Copy)]
pub struct CoarseLag {
    /// Lag in audio samples (`lag = t_master - t_slave`). Positive means the slave
    /// buffer starts *after* the master buffer.
    pub lag_samples: isize,
    /// Normalized cross-correlation score in [-1, 1] evaluated at the chosen lag.
    pub score: f64,
    /// Number of envelope frames that actually overlap at the chosen lag.
    pub overlap_frames: usize,
}

pub struct CoarseAligner;

impl CoarseAligner {
    /// Computes the log-energy envelope of a 16 kHz audio signal.
    pub fn compute_energy_envelope(samples: &[f32], frame_size: usize, hop_size: usize) -> Vec<f32> {
        let mut builder = EnergyEnvelopeBuilder::new(frame_size, hop_size);
        builder.push(samples);
        builder.finish()
    }

    /// Fast coarse alignment across long takes using FFT cross-correlation of energy envelopes.
    /// Returns the estimated coarse lag in samples:
    /// lag = t_master - t_slave
    /// (lag > 0 means slave started after master; lag < 0 means slave started before master).
    pub fn find_coarse_lag_samples(
        master: &[f32],
        slave: &[f32],
        frame_size: usize,
        hop_size: usize,
    ) -> Option<isize> {
        let env_m = Self::compute_energy_envelope(master, frame_size, hop_size);
        let env_s = Self::compute_energy_envelope(slave, frame_size, hop_size);

        if env_m.is_empty() || env_s.is_empty() {
            return None;
        }

        let raw = Self::cross_correlate_envelopes(&env_m, &env_s)?;
        let best = Self::pick_best_lag(&env_m, &env_s, &raw, false)?;
        Some(best.0 * hop_size as isize)
    }

    /// Normalized-envelope coarse alignment. Unlike plain correlation this is
    /// robust when one signal is dramatically longer than the other (e.g. a
    /// 3 minute camera clip inside a 2 hour wedding ceremony recording), because
    /// each candidate lag is scored only over its actual overlap window.
    pub fn find_envelope_lag(
        env_master: &[f32],
        env_slave: &[f32],
        hop_size: usize,
    ) -> Option<CoarseLag> {
        if env_master.len() < 2 || env_slave.len() < 2 {
            return None;
        }

        let raw = Self::cross_correlate_envelopes(env_master, env_slave)?;
        let (lag_frames, score, overlap_frames) =
            Self::pick_best_lag(env_master, env_slave, &raw, true)?;

        Some(CoarseLag {
            lag_samples: lag_frames * hop_size as isize,
            score,
            overlap_frames,
        })
    }

    /// Raw (unnormalized) circular correlation of the two envelopes.
    /// `raw[k]` corresponds to lag `k >= 0`, and `raw[fft_len + k]` to lag `k < 0`.
    fn cross_correlate_envelopes(env_m: &[f32], env_s: &[f32]) -> Option<Vec<f32>> {
        let min_len = env_m.len() + env_s.len();
        let fft_len = min_len.next_power_of_two();

        // Safety valve: never attempt an FFT we cannot afford (~4 GB of f32 buffers
        // would require fft_len > 2^28). This should never trigger for realistic
        // media (a 32-bit length envelope covers days of audio).
        if fft_len > (1usize << 27) {
            return None;
        }

        let mut planner = RealFftPlanner::<f32>::new();
        let r2c = planner.plan_fft_forward(fft_len);
        let c2r = planner.plan_fft_inverse(fft_len);

        let mut in_m = vec![0.0f32; fft_len];
        let mut in_s = vec![0.0f32; fft_len];
        in_m[..env_m.len()].copy_from_slice(env_m);
        in_s[..env_s.len()].copy_from_slice(env_s);

        let mut out_m = r2c.make_output_vec();
        let mut out_s = r2c.make_output_vec();

        r2c.process(&mut in_m, &mut out_m).ok()?;
        r2c.process(&mut in_s, &mut out_s).ok()?;

        // Cross-spectrum: M * S*  ->  peak at tau = t_m - t_s (envelope frames)
        let mut cross_spec = vec![Complex::new(0.0f32, 0.0f32); out_m.len()];
        for i in 0..out_m.len() {
            cross_spec[i] = out_m[i] * out_s[i].conj();
        }

        let mut corr = c2r.make_output_vec();
        c2r.process(&mut cross_spec, &mut corr).ok()?;

        Some(corr)
    }

    /// Scans every valid lag and returns `(lag_frames, score, overlap_frames)`.
    /// When `normalized` is set, computes exact zero-mean Pearson NCC over the
    /// overlapping window so a short clip embedded in a long recording is scored
    /// fairly against its actual overlap without quiet-section or edge bias.
    fn pick_best_lag(
        env_m: &[f32],
        env_s: &[f32],
        raw: &[f32],
        normalized: bool,
    ) -> Option<(isize, f64, usize)> {
        let len_m = env_m.len();
        let len_s = env_s.len();
        let fft_len = (len_m + len_s).next_power_of_two();
        let inv_fft = 1.0 / fft_len as f64;

        let mut prefix_m = Vec::with_capacity(len_m + 1);
        let mut prefix_m2 = Vec::with_capacity(len_m + 1);
        prefix_m.push(0.0f64);
        prefix_m2.push(0.0f64);
        let mut sum_m_acc = 0.0f64;
        let mut sum_m2_acc = 0.0f64;
        for &v in env_m {
            let vf = v as f64;
            sum_m_acc += vf;
            sum_m2_acc += vf * vf;
            prefix_m.push(sum_m_acc);
            prefix_m2.push(sum_m2_acc);
        }

        let mut prefix_s = Vec::with_capacity(len_s + 1);
        let mut prefix_s2 = Vec::with_capacity(len_s + 1);
        prefix_s.push(0.0f64);
        prefix_s2.push(0.0f64);
        let mut sum_s_acc = 0.0f64;
        let mut sum_s2_acc = 0.0f64;
        for &v in env_s {
            let vf = v as f64;
            sum_s_acc += vf;
            sum_s2_acc += vf * vf;
            prefix_s.push(sum_s_acc);
            prefix_s2.push(sum_s2_acc);
        }

        // Require at least half of the shorter envelope to overlap when computing
        // normalized correlation so tiny edge slivers cannot score ~1.0 on a single burst.
        let base_len = len_s.min(len_m);
        let min_overlap = if normalized {
            (base_len / 2).max(2)
        } else {
            1
        };

        let overlap_ncc = |dot: f64, m_start: usize, s_start: usize, overlap: usize| -> f64 {
            let k = overlap as f64;
            let sm = prefix_m[m_start + overlap] - prefix_m[m_start];
            let sm2 = prefix_m2[m_start + overlap] - prefix_m2[m_start];
            let ss = prefix_s[s_start + overlap] - prefix_s[s_start];
            let ss2 = prefix_s2[s_start + overlap] - prefix_s2[s_start];

            let cov = dot - (sm * ss) / k;
            let var_m = (sm2 - (sm * sm) / k).max(0.0);
            let var_s = (ss2 - (ss * ss) / k).max(0.0);
            let denom = (var_m * var_s).sqrt();
            if denom < 1e-12 {
                0.0
            } else {
                (cov / denom).clamp(-1.0, 1.0)
            }
        };

        let mut best_rank = f64::NEG_INFINITY;
        let mut best_val = 0.0f64;
        let mut best_lag = 0isize;
        let mut best_overlap = 0usize;

        // Positive lags [0 ..= len_m - 1]: master[lag..lag+overlap] vs slave[0..overlap]
        for lag in 0..len_m {
            let overlap = (len_m - lag).min(len_s);
            if overlap < min_overlap {
                continue;
            }
            let value = raw[lag] as f64 * inv_fft;
            let (rank, score) = if normalized {
                let ncc = overlap_ncc(value, lag, 0, overlap);
                let weight = (overlap as f64 / base_len.max(1) as f64).sqrt();
                (ncc * weight, ncc)
            } else {
                (value, value)
            };
            if rank > best_rank {
                best_rank = rank;
                best_val = score;
                best_lag = lag as isize;
                best_overlap = overlap;
            }
        }

        // Negative lags [-len_s + 1 .. -1]: master[0..overlap] vs slave[lag..lag+overlap]
        for lag in 1..len_s {
            let overlap = (len_s - lag).min(len_m);
            if overlap < min_overlap {
                continue;
            }
            let circ_idx = fft_len - lag;
            let value = raw[circ_idx] as f64 * inv_fft;
            let (rank, score) = if normalized {
                let ncc = overlap_ncc(value, 0, lag, overlap);
                let weight = (overlap as f64 / base_len.max(1) as f64).sqrt();
                (ncc * weight, ncc)
            } else {
                (value, value)
            };
            if rank > best_rank {
                best_rank = rank;
                best_val = score;
                best_lag = -(lag as isize);
                best_overlap = overlap;
            }
        }

        if best_rank == f64::NEG_INFINITY {
            None
        } else {
            Some((best_lag, best_val, best_overlap))
        }
    }
}

/// Incremental log-energy envelope builder.
///
/// Long recordings (hours) are decoded in chunks; this keeps only a few hundred
/// kilobytes of pending audio around while producing a compact 125 Hz envelope
/// that is cheap to cross-correlate.
pub struct EnergyEnvelopeBuilder {
    frame_size: usize,
    hop_size: usize,
    pending: Vec<f32>,
    start: usize,
    envelope: Vec<f32>,
}

impl EnergyEnvelopeBuilder {
    pub fn new(frame_size: usize, hop_size: usize) -> Self {
        Self {
            frame_size: frame_size.max(1),
            hop_size: hop_size.max(1),
            pending: Vec::new(),
            start: 0,
            envelope: Vec::new(),
        }
    }

    pub fn with_capacity(frame_size: usize, hop_size: usize, expected_frames: usize) -> Self {
        let mut builder = Self::new(frame_size, hop_size);
        builder.envelope.reserve(expected_frames);
        builder
    }

    pub fn push(&mut self, samples: &[f32]) {
        self.pending.extend_from_slice(samples);
        self.drain_frames();

        // Compact consumed samples regularly to keep memory bounded.
        if self.start > 0
            && (self.start >= self.hop_size * 4096 || self.start * 2 >= self.pending.len())
        {
            self.pending.drain(0..self.start);
            self.start = 0;
        }
    }

    fn drain_frames(&mut self) {
        while self.pending.len() >= self.start + self.frame_size {
            let frame = &self.pending[self.start..self.start + self.frame_size];
            let mut sum_sq = 0.0f32;
            for &s in frame {
                sum_sq += s * s;
            }
            let rms = (sum_sq / self.frame_size as f32).sqrt();
            self.envelope.push((rms + 1e-4).ln());
            self.start += self.hop_size;
        }
    }

    pub fn frames(&self) -> usize {
        self.envelope.len()
    }

    /// Finalizes the envelope, removing its mean so correlation is centred on zero.
    pub fn finish(mut self) -> Vec<f32> {
        self.drain_frames();
        if !self.envelope.is_empty() {
            let mean: f32 = self.envelope.iter().sum::<f32>() / self.envelope.len() as f32;
            for v in self.envelope.iter_mut() {
                *v -= mean;
            }
        }
        self.envelope
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn burst_signal(seconds: f32, bursts: &[(f32, f32)]) -> Vec<f32> {
        let sample_rate = 16000;
        let n = (sample_rate as f32 * seconds) as usize;
        let mut sound = vec![0.0f32; n];
        for i in 0..n {
            let t = i as f32 / sample_rate as f32;
            let active = bursts.iter().any(|&(a, b)| t > a && t < b);
            if active {
                sound[i] = (2.0 * std::f32::consts::PI * 440.0 * t).sin()
                    + 0.5 * (2.0 * std::f32::consts::PI * 880.0 * t).sin();
            }
        }
        sound
    }

    #[test]
    fn test_energy_envelope_coarse_alignment_slave_after_master() {
        let sample_rate = 16000;
        let n = (sample_rate as f32 * 8.0) as usize;
        let sound = burst_signal(8.0, &[(1.1, 1.4), (2.8, 3.2), (5.3, 5.8)]);

        let delay_samples = 20000;
        let mut master = vec![0.0f32; n];
        for i in delay_samples..n {
            master[i] = sound[i - delay_samples];
        }

        let coarse_lag =
            CoarseAligner::find_coarse_lag_samples(&master, &sound, 256, 128).unwrap();
        assert!(
            (coarse_lag - delay_samples as isize).abs() <= (128 * 2),
            "Coarse lag was {}, expected ~{}",
            coarse_lag,
            delay_samples
        );
    }

    #[test]
    fn test_energy_envelope_coarse_alignment_slave_before_master() {
        let sample_rate = 16000;
        let n = (sample_rate as f32 * 8.0) as usize;
        let sound = burst_signal(8.0, &[(1.1, 1.4), (2.8, 3.2), (5.3, 5.8)]);

        let delay_samples = 20000;
        let mut slave = vec![0.0f32; n];
        for i in delay_samples..n {
            slave[i] = sound[i - delay_samples];
        }

        let coarse_lag =
            CoarseAligner::find_coarse_lag_samples(&sound, &slave, 256, 128).unwrap();
        assert!(
            (coarse_lag - (-(delay_samples as isize))).abs() <= (128 * 2),
            "Coarse lag was {}, expected ~{}",
            coarse_lag,
            -(delay_samples as isize)
        );
    }

    #[test]
    fn test_normalized_envelope_finds_short_clip_inside_long_recording() {
        let sample_rate = 16000u32;
        let seconds = 400.0f32;
        let n = (sample_rate as f32 * seconds) as usize;

        // Long, mostly quiet "ceremony" recording with periodic speech-like bursts.
        let mut long = vec![0.0f32; n];
        for i in 0..n {
            let t = i as f32 / sample_rate as f32;
            let burst_idx = (t / 7.0) as i32;
            let phase = t - burst_idx as f32 * 7.0;
            // Irregular on/off pattern (varies per burst so the envelope is unique).
            let dur = 1.0 + ((burst_idx * 37) % 31) as f32 * 0.1;
            if phase < dur {
                long[i] = ((2.0 * std::f32::consts::PI * (180.0 + (burst_idx % 7) as f32 * 60.0) * t)
                    .sin()
                    * 0.8)
                    + 0.2 * ((2.0 * std::f32::consts::PI * 900.0 * t).sin());
            } else {
                long[i] = 0.001 * ((i % 17) as f32 / 17.0);
            }
        }

        // A 25 second camera clip recorded at t = 318.5s inside the long file.
        let clip_start = 318.5;
        let clip_len = 25.0;
        let s = (clip_start * sample_rate as f32) as usize;
        let e = ((clip_start + clip_len) * sample_rate as f32) as usize;
        let clip = long[s..e].to_vec();

        let env_master =
            CoarseAligner::compute_energy_envelope(&long, ENVELOPE_FRAME_SIZE, ENVELOPE_HOP_SIZE);
        let env_clip =
            CoarseAligner::compute_energy_envelope(&clip, ENVELOPE_FRAME_SIZE, ENVELOPE_HOP_SIZE);

        let coarse =
            CoarseAligner::find_envelope_lag(&env_master, &env_clip, ENVELOPE_HOP_SIZE).unwrap();

        let expected = (clip_start * sample_rate as f32) as isize;
        let tolerance = (ENVELOPE_HOP_SIZE as isize) * 3;
        assert!(
            (coarse.lag_samples - expected).abs() <= tolerance,
            "Coarse lag was {} samples, expected ~{} (score {})",
            coarse.lag_samples,
            expected,
            coarse.score
        );
        assert!(coarse.score > 0.1, "NCC score was too low: {}", coarse.score);
    }

    #[test]
    fn test_streaming_envelope_matches_batch_envelope() {
        let samples: Vec<f32> = (0..50000)
            .map(|i| (i as f32 * 0.01).sin() * 0.5 + 0.01)
            .collect();

        let batch = CoarseAligner::compute_energy_envelope(&samples, 256, 128);

        let mut builder = EnergyEnvelopeBuilder::new(256, 128);
        for chunk in samples.chunks(777) {
            builder.push(chunk);
        }
        let streaming = builder.finish();

        assert_eq!(batch.len(), streaming.len());
        for (a, b) in batch.iter().zip(streaming.iter()) {
            assert!((a - b).abs() < 1e-5, "envelope mismatch {} vs {}", a, b);
        }
    }
}
