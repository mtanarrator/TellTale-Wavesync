use realfft::RealFftPlanner;
use rustfft::num_complex::Complex;
use vegas_sync_core::correlation::GccPhat;

/// Applies an exact fractional-sample delay delta in the frequency domain.
fn apply_fractional_delay(samples: &[f32], delta_samples: f32) -> Vec<f32> {
    let fft_len = samples.len().next_power_of_two();
    let mut planner = RealFftPlanner::<f32>::new();
    let r2c = planner.plan_fft_forward(fft_len);
    let c2r = planner.plan_fft_inverse(fft_len);

    let mut in_buf = vec![0.0f32; fft_len];
    in_buf[..samples.len()].copy_from_slice(samples);

    let mut spectrum = r2c.make_output_vec();
    r2c.process(&mut in_buf, &mut spectrum).unwrap();

    let num_bins = spectrum.len();
    for k in 1..num_bins - 1 {
        // Phase shift: exp(-j * 2 * pi * k * delta / N)
        let phase = -2.0 * std::f32::consts::PI * (k as f32) * delta_samples / (fft_len as f32);
        let rotator = Complex::from_polar(1.0, phase);
        spectrum[k] = spectrum[k] * rotator;
    }
    // DC and Nyquist components must remain purely real for C2R
    spectrum[0].im = 0.0;
    spectrum[num_bins - 1].im = 0.0;

    let mut out_buf = c2r.make_output_vec();
    c2r.process(&mut spectrum, &mut out_buf).unwrap();

    // Normalization factor for IFFT
    let norm = 1.0 / fft_len as f32;
    for s in out_buf.iter_mut() {
        *s *= norm;
    }

    out_buf[..samples.len()].to_vec()
}

#[test]
fn test_sub_sample_super_resolution_alignment() {
    let n = 4096;
    // Broadband chirp signal
    let mut x = vec![0.0f32; n];
    for i in 0..n {
        let t = i as f32 / n as f32;
        x[i] = (2.0 * std::f32::consts::PI * (150.0 + 1200.0 * t) * t).sin();
    }

    // Apply exact fractional delay of 15.35 samples
    let target_integer_delay = 15;
    let target_fractional_delay = 0.35f32;
    let total_delay = target_integer_delay as f32 + target_fractional_delay;

    let y = apply_fractional_delay(&x, total_delay);

    // Reference y (Master at t=15.35s), Target x (Slave at t=0s):
    // Slave started after Master on timeline -> positive delay (+15.35)
    let gcc = GccPhat::new();
    let peak_pos = gcc.correlate(&y, &x).expect("GCC-PHAT failed");

    println!("Detected Peak: integer_lag={}, fractional_offset={:.4}, exact_lag={:.4}",
             peak_pos.integer_lag, peak_pos.fractional_offset, peak_pos.exact_lag);

    assert_eq!(peak_pos.integer_lag, target_integer_delay as isize);
    assert!(
        (peak_pos.exact_lag - total_delay as f64).abs() < 0.15,
        "Super-resolution error: detected {}, expected {}",
        peak_pos.exact_lag,
        total_delay
    );

    // Reference x (Master at t=0s), Target y (Slave at t=15.35s):
    // Slave started before Master on timeline -> negative delay (-15.35)
    let peak_neg = gcc.correlate(&x, &y).expect("GCC-PHAT failed");
    assert_eq!(peak_neg.integer_lag, -(target_integer_delay as isize));
    assert!(
        (peak_neg.exact_lag - (-(total_delay as f64))).abs() < 0.15,
        "Super-resolution negative error: detected {}, expected {}",
        peak_neg.exact_lag,
        -total_delay
    );
    assert!(peak_pos.confidence > 0.85);
}
