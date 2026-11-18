use vegas_sync_core::graph::{MultiTrackGraphSolver, SyncEdge};

#[test]
fn test_complex_multi_cam_topology_and_island_handling() {
    // 4 Tracks:
    // - Master: Sound Devices (continuous, anchored at 0.0s)
    // - CamA: Overlaps with Master (+12.4s)
    // - CamB: Overlaps with CamA (+8.2s relative to CamA -> 20.6s relative to Master)
    // - CamC: Overlaps with CamB (+14.0s relative to CamB -> 34.6s relative to Master)
    // - Island_Take: No overlap with any track (confidence < threshold)

    let all_event_ids = vec![
        "Master_Take".to_string(),
        "CamA_Take1".to_string(),
        "CamB_Take1".to_string(),
        "CamC_Take1".to_string(),
        "Island_Take".to_string(),
    ];

    let edges = vec![
        // Master -> CamA
        SyncEdge {
            from_event_id: "Master_Take".to_string(),
            to_event_id: "CamA_Take1".to_string(),
            offset_seconds: 12.400,
            drift_ppm: 12.0,
            confidence: 0.96,
            split_points_seconds: Vec::new(),
        },
        // CamA -> CamB
        SyncEdge {
            from_event_id: "CamA_Take1".to_string(),
            to_event_id: "CamB_Take1".to_string(),
            offset_seconds: 8.200,
            drift_ppm: -5.0,
            confidence: 0.92,
            split_points_seconds: Vec::new(),
        },
        // CamB -> CamC
        SyncEdge {
            from_event_id: "CamB_Take1".to_string(),
            to_event_id: "CamC_Take1".to_string(),
            offset_seconds: 14.000,
            drift_ppm: 8.0,
            confidence: 0.88,
            split_points_seconds: Vec::new(),
        },
        // Spurious low-confidence edge to island take (should be rejected)
        SyncEdge {
            from_event_id: "CamC_Take1".to_string(),
            to_event_id: "Island_Take".to_string(),
            offset_seconds: 50.000,
            drift_ppm: 0.0,
            confidence: 0.15, // Below 0.40 threshold!
            split_points_seconds: Vec::new(),
        },
    ];

    let resolved = MultiTrackGraphSolver::solve(&all_event_ids, &edges, "Master_Take", 0.0);

    // 1. Verify Master is anchored
    let master = resolved.get("Master_Take").unwrap();
    assert!(master.is_aligned);
    assert_eq!(master.resolved_start_seconds, 0.0);
    assert_eq!(master.playback_rate, 1.0);

    // 2. Verify CamA directly aligned
    let cam_a = resolved.get("CamA_Take1").unwrap();
    assert!(cam_a.is_aligned);
    assert!((cam_a.resolved_start_seconds - 12.400).abs() < 1e-4);
    assert!((cam_a.drift_ppm - 12.0).abs() < 1e-4);

    // 3. Verify CamB transitively aligned (12.4 + 8.2 = 20.6s)
    let cam_b = resolved.get("CamB_Take1").unwrap();
    assert!(cam_b.is_aligned);
    assert!((cam_b.resolved_start_seconds - 20.600).abs() < 1e-4);
    assert!((cam_b.drift_ppm - (12.0 - 5.0)).abs() < 1e-4);

    // 4. Verify CamC transitively aligned across 3 hops (20.6 + 14.0 = 34.6s)
    let cam_c = resolved.get("CamC_Take1").unwrap();
    assert!(cam_c.is_aligned);
    assert!((cam_c.resolved_start_seconds - 34.600).abs() < 1e-4);
    assert!((cam_c.drift_ppm - (12.0 - 5.0 + 8.0)).abs() < 1e-4);

    // 5. Verify Island take remains unaligned (not falsely placed)
    let island = resolved.get("Island_Take").unwrap();
    assert!(!island.is_aligned, "Low-confidence island take should not be aligned");
    assert!(island.confidence < 0.35);
}
