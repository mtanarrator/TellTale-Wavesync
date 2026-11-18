use vegas_sync_core::drift::{DiscontinuityDetector, DriftObservation, RansacRegression};

#[test]
fn test_rolling_drift_and_discontinuity_end_to_end() {
    // Ground truth:
    // Base delay: 2.500s
    // Clock drift: +45.0 PPM (m = 0.000045)
    // Frame drop at t = 60s: sudden +150ms step jump in delay
    let true_c = 2.500;
    let true_m = 0.000045; // 45 PPM
    let frame_drop_t = 60.0;
    let step_jump = 0.150;

    let mut observations = Vec::new();
    // Simulate 30-second rolling window observations from t = 0 to t = 180s
    for i in 0..13 {
        let t = i as f64 * 15.0; // 0, 15, 30, 45, 60, 75, ...
        let jump = if t >= frame_drop_t { step_jump } else { 0.0 };
        let delay = true_c + true_m * t + jump;
        observations.push(DriftObservation {
            time_seconds: t,
            delay_seconds: delay,
            confidence: 0.95,
        });
    }

    // 1. Verify discontinuity detection catches the step jump at t = 60s
    let jumps = DiscontinuityDetector::detect_step_jumps(&observations, 0.050); // 50ms threshold
    assert_eq!(jumps.len(), 1, "Expected exactly 1 frame-drop discontinuity");
    assert!(
        (jumps[0].timestamp_seconds - 52.5).abs() < 10.0,
        "Discontinuity timestamp {} was not near transition",
        jumps[0].timestamp_seconds
    );
    assert!(
        (jumps[0].step_jump_seconds - step_jump).abs() < 0.010,
        "Step jump size {} did not match true 150ms drop",
        jumps[0].step_jump_seconds
    );

    // 2. Verify RANSAC fits continuous segment with accurate PPM recovery
    let first_segment: Vec<DriftObservation> = observations
        .iter()
        .filter(|o| o.time_seconds < frame_drop_t)
        .cloned()
        .collect();

    let ransac_result = RansacRegression::fit(&first_segment, 100, 0.005, 0.6)
        .expect("RANSAC fitting failed");

    assert!(
        (ransac_result.intercept - true_c).abs() < 0.002,
        "Intercept {} did not match true offset 2.500s",
        ransac_result.intercept
    );
    assert!(
        (ransac_result.drift_ppm - 45.0).abs() < 2.0,
        "Recovered PPM {} did not match true 45.0 PPM",
        ransac_result.drift_ppm
    );
    assert!(ransac_result.r_squared > 0.98);
}
