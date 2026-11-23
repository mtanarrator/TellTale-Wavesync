use std::collections::{HashMap, HashSet, VecDeque};

#[derive(Debug, Clone)]
pub struct SyncEdge {
    pub from_event_id: String,
    pub to_event_id: String,
    pub offset_seconds: f64, // to_time = from_time + offset_seconds
    pub drift_ppm: f64,
    pub confidence: f64,
    pub split_points_seconds: Vec<f64>,
}

#[derive(Debug, Clone)]
pub struct ResolvedNode {
    pub event_id: String,
    pub resolved_start_seconds: f64,
    pub playback_rate: f64,
    pub drift_ppm: f64,
    pub confidence: f64,
    pub split_points_seconds: Vec<f64>,
    pub is_aligned: bool,
}

pub struct MultiTrackGraphSolver;

impl MultiTrackGraphSolver {
    /// Resolves global timeline positioning for all events across multiple tracks
    /// using a spanning tree rooted at the master event.
    pub fn solve(
        all_event_ids: &[String],
        edges: &[SyncEdge],
        master_event_id: &str,
        master_start_seconds: f64,
    ) -> HashMap<String, ResolvedNode> {
        Self::solve_with_secondary_anchors(
            all_event_ids,
            edges,
            master_event_id,
            master_start_seconds,
            &[],
        )
    }

    /// Resolves global timeline positioning from `master_event_id`, and then
    /// resolves any disconnected components reachable from `secondary_anchors`
    /// (e.g., separate non-overlapping takes on the master track across a day).
    pub fn solve_with_secondary_anchors(
        all_event_ids: &[String],
        edges: &[SyncEdge],
        master_event_id: &str,
        master_start_seconds: f64,
        secondary_anchors: &[(String, f64)],
    ) -> HashMap<String, ResolvedNode> {
        let mut adj: HashMap<String, Vec<SyncEdge>> = HashMap::new();
        for id in all_event_ids {
            adj.entry(id.clone()).or_default();
        }

        for edge in edges {
            // Forward edge
            adj.entry(edge.from_event_id.clone())
                .or_default()
                .push(edge.clone());

            // Reverse edge
            adj.entry(edge.to_event_id.clone())
                .or_default()
                .push(SyncEdge {
                    from_event_id: edge.to_event_id.clone(),
                    to_event_id: edge.from_event_id.clone(),
                    offset_seconds: -edge.offset_seconds,
                    drift_ppm: -edge.drift_ppm,
                    confidence: edge.confidence,
                    split_points_seconds: Vec::new(),
                });
        }

        let mut resolved: HashMap<String, ResolvedNode> = HashMap::new();
        let mut visited: HashSet<String> = HashSet::new();

        let mut run_bfs = |root_id: &str, root_start: f64| {
            if visited.contains(root_id) {
                return;
            }
            resolved.insert(
                root_id.to_string(),
                ResolvedNode {
                    event_id: root_id.to_string(),
                    resolved_start_seconds: root_start,
                    playback_rate: 1.0,
                    drift_ppm: 0.0,
                    confidence: 1.0,
                    split_points_seconds: Vec::new(),
                    is_aligned: true,
                },
            );
            visited.insert(root_id.to_string());

            let mut queue = VecDeque::new();
            queue.push_back(root_id.to_string());

            while let Some(curr_id) = queue.pop_front() {
                let curr_node = match resolved.get(&curr_id).cloned() {
                    Some(n) => n,
                    None => continue,
                };

                if let Some(neighbors) = adj.get(&curr_id) {
                    // Sort by highest confidence first
                    let mut sorted_edges = neighbors.clone();
                    sorted_edges.sort_by(|a, b| {
                        b.confidence
                            .partial_cmp(&a.confidence)
                            .unwrap_or(std::cmp::Ordering::Equal)
                    });

                    for edge in sorted_edges {
                        if !visited.contains(&edge.to_event_id) && edge.confidence >= 0.35 {
                            visited.insert(edge.to_event_id.clone());

                            let target_start = curr_node.resolved_start_seconds + edge.offset_seconds;
                            let target_ppm = curr_node.drift_ppm + edge.drift_ppm;
                            let target_rate = 1.0 + (target_ppm / 1_000_000.0);
                            let target_conf = curr_node.confidence * edge.confidence;

                            resolved.insert(
                                edge.to_event_id.clone(),
                                ResolvedNode {
                                    event_id: edge.to_event_id.clone(),
                                    resolved_start_seconds: target_start,
                                    playback_rate: target_rate,
                                    drift_ppm: target_ppm,
                                    confidence: target_conf,
                                    split_points_seconds: edge.split_points_seconds.clone(),
                                    is_aligned: true,
                                },
                            );

                            queue.push_back(edge.to_event_id.clone());
                        }
                    }
                }
            }
        };

        // 1. Primary master root
        run_bfs(master_event_id, master_start_seconds);

        // 2. Secondary chapter roots (only if unreachable from primary master)
        for (anchor_id, anchor_t0) in secondary_anchors {
            run_bfs(anchor_id, *anchor_t0);
        }

        // 3. Any unvisited nodes (islands) remain unaligned
        for id in all_event_ids {
            if !resolved.contains_key(id) {
                let max_edge_conf = adj
                    .get(id)
                    .and_then(|edges| {
                        edges
                            .iter()
                            .map(|e| e.confidence)
                            .max_by(|a, b| a.partial_cmp(b).unwrap_or(std::cmp::Ordering::Equal))
                    })
                    .unwrap_or(0.0);

                resolved.insert(
                    id.clone(),
                    ResolvedNode {
                        event_id: id.clone(),
                        resolved_start_seconds: 0.0,
                        playback_rate: 1.0,
                        drift_ppm: 0.0,
                        confidence: max_edge_conf,
                        split_points_seconds: Vec::new(),
                        is_aligned: false,
                    },
                );
            }
        }

        resolved
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_graph_transitive_multi_track_resolution() {
        // Master -> Cam A (+15s)
        // Cam A -> Cam B (+5s)
        // Therefore Master -> Cam B should resolve to +20s!
        let all_ids = vec![
            "Evt_Master".to_string(),
            "Evt_CamA".to_string(),
            "Evt_CamB".to_string(),
        ];

        let edges = vec![
            SyncEdge {
                from_event_id: "Evt_Master".to_string(),
                to_event_id: "Evt_CamA".to_string(),
                offset_seconds: 15.0,
                drift_ppm: 10.0,
                confidence: 0.95,
                split_points_seconds: Vec::new(),
            },
            SyncEdge {
                from_event_id: "Evt_CamA".to_string(),
                to_event_id: "Evt_CamB".to_string(),
                offset_seconds: 5.0,
                drift_ppm: 5.0,
                confidence: 0.90,
                split_points_seconds: Vec::new(),
            },
        ];

        let resolved = MultiTrackGraphSolver::solve(&all_ids, &edges, "Evt_Master", 0.0);

        assert!(resolved.get("Evt_Master").unwrap().is_aligned);
        assert_eq!(
            resolved.get("Evt_Master").unwrap().resolved_start_seconds,
            0.0
        );

        let cam_a = resolved.get("Evt_CamA").unwrap();
        assert!(cam_a.is_aligned);
        assert!((cam_a.resolved_start_seconds - 15.0).abs() < 1e-4);

        let cam_b = resolved.get("Evt_CamB").unwrap();
        assert!(cam_b.is_aligned);
        assert!((cam_b.resolved_start_seconds - 20.0).abs() < 1e-4);
        assert!((cam_b.drift_ppm - 15.0).abs() < 1e-4);
    }
}
