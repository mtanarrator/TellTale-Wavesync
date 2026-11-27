//! Global timeline position solver.
//!
//! Minimizes the weighted least-squares cost
//!
//! ```text
//! Σ w_k (p_to - p_from - offset_k)^2  +  Σ w_i (p_i - prior_i)^2
//! ```
//!
//! over one scalar position per [`Node`], using 3 IRLS/Huber passes for
//! robustness against outlier constraints.
//!
//! # Conventions
//!
//! * An edge means `p_to = p_from + offset_seconds` in the ideal case.
//! * `ConstraintKind::Prior` is a unary constraint that pins `to` at
//!   `offset_seconds` (`from` is ignored). Node priors are usually simpler.
//! * Every connected component (edges of kind `Audio`/`Sequence`, plus any
//!   `Prior` constraint whose endpoints differ) is solved independently.
//!   `component[i]` is a dense label assigned in ascending node order, so the
//!   label of the component containing node 0 is `0`.
//! * A component without any prior is fixed by a tiny gauge prior at 0 on its
//!   lowest-index node, making the otherwise singular system solvable.
//!
//! # Return values
//!
//! `positions` has one entry per node, in node order. `residuals` has one entry
//! per *input constraint*, in constraint order, holding the signed value
//! `p_to - p_from - offset` for relative constraints (`p_to - offset` for
//! [`ConstraintKind::Prior`]) and `0.0` for constraints that were ignored
//! (invalid index, non-finite offset, non-positive or non-finite weight).
//!
//! `component_has_audio` has one entry per node too: it is `true` when that
//! node's component contains at least one accepted `Audio` constraint, so
//! sequence-only and prior-only components are flagged `false`. It lets the
//! caller separate components with real cross-correlation evidence from those
//! placed purely by ordering or priors.
//!
//! `node_residuals` sums, per node, the absolute residuals of the accepted
//! constraints incident to it (both endpoints of a relative constraint, the
//! pinned node of a `Prior` constraint). It is a cheap per-node confidence
//! signal: near zero means the node agrees with all its measurements. Ignored
//! constraints, synthetic gauge priors and [`Node::prior_offset`] priors are
//! excluded, so the total equals twice the sum of absolute `residuals` for
//! relative constraints only.
//!
//! # Limits
//!
//! Dense normal equations with Gaussian elimination and partial pivoting:
//! `O(m^3)` time and `O(m^2)` memory per component, where `m` is the node count
//! of the largest component. This is intended for a few hundred nodes; it is
//! not a sparse solver and should not be used for many thousands of nodes.
//! The solver is float-based and tolerates duplicate edges, self edges,
//! weight outliers (clamped) and offsets up to ±1e12 seconds.

use std::collections::VecDeque;

/// Largest accepted weight magnitude; larger values are clamped so the normal
/// equations cannot overflow.
const MAX_WEIGHT: f64 = 1e9;

/// Largest accepted offset magnitude in seconds (~31,000 years); larger
/// magnitudes are treated as invalid input.
const MAX_ABS_OFFSET: f64 = 1e12;

/// Weight of the pseudo-prior that fixes the gauge of an unanchored component.
/// It is tiny relative to typical normalized weights, so it only removes the
/// translation ambiguity without biasing the solution.
const GAUGE_WEIGHT: f64 = 1e-6;

/// Number of linear solves performed per component (one per IRLS pass).
const IRLS_ITERATIONS: usize = 3;

/// Pivot magnitude below which a column is treated as rank deficient.
const PIVOT_EPS: f64 = 1e-12;

/// Role of a constraint in the global solve.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConstraintKind {
    /// Offset measured by audio cross-correlation.
    Audio,
    /// Offset derived from sequence/time ordering.
    Sequence,
    /// Unary constraint pinning `to` to `offset_seconds`.
    Prior,
}

/// A timeline element whose absolute position is estimated.
#[derive(Debug, Clone, PartialEq)]
pub struct Node {
    /// Stable identifier, carried through for the caller's bookkeeping.
    pub id: String,
    /// Absolute prior for this node's position, if known.
    pub prior_offset: Option<f64>,
    /// Weight of [`Node::prior_offset`]; `<= 0` disables the prior.
    pub prior_weight: f64,
}

/// A relative measurement between two nodes, or an absolute prior.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Constraint {
    /// Index of the source node in the `nodes` slice.
    pub from: usize,
    /// Index of the destination node in the `nodes` slice.
    pub to: usize,
    /// Ideal value of `p_to - p_from` (for [`ConstraintKind::Prior`]: the
    /// target position of `to`).
    pub offset_seconds: f64,
    /// Non-negative confidence weight; `<= 0` or non-finite disables the row.
    pub weight: f64,
    /// Measurement family, recorded by the caller for later filtering.
    pub kind: ConstraintKind,
}

/// Result of [`solve`].
#[derive(Debug, Clone, PartialEq)]
pub struct SolveResult {
    /// Estimated positions, one per input node, in node order.
    pub positions: Vec<f64>,
    /// Signed residuals, one per input constraint, in constraint order.
    pub residuals: Vec<f64>,
    /// Component label per input node.
    pub component: Vec<usize>,
    /// Whether the node's component contains an accepted [`ConstraintKind::Audio`]
    /// constraint; one entry per input node, in node order.
    pub component_has_audio: Vec<bool>,
    /// Sum of absolute residuals of the accepted constraints incident to each
    /// node; one entry per input node, in node order. See the module docs for
    /// the exact accounting.
    pub node_residuals: Vec<f64>,
}

/// A validated, normalized edge constraint.
struct Edge {
    from: usize,
    to: usize,
    offset: f64,
    weight: f64,
}

/// A validated, normalized absolute prior.
struct PriorRow {
    node: usize,
    offset: f64,
    weight: f64,
}

/// Edge with component-local node indices.
struct LocalEdge {
    from: usize,
    to: usize,
    offset: f64,
    base_weight: f64,
}

/// Prior with a component-local node index.
struct LocalPrior {
    node: usize,
    offset: f64,
    weight: f64,
}

/// Accepted equation behind an input constraint, kept for residual reporting.
#[derive(Clone, Copy)]
enum Equation {
    /// `p_to - p_from - offset`
    Edge(usize, usize, f64),
    /// `p_node - offset`
    Prior(usize, f64),
}

/// Returns the offset when it is finite and within the supported range.
fn valid_offset(value: f64) -> Option<f64> {
    if value.is_finite() && value.abs() <= MAX_ABS_OFFSET {
        Some(value)
    } else {
        None
    }
}

/// Returns the weight when it is finite and positive, clamped to [`MAX_WEIGHT`].
fn valid_weight(value: f64) -> Option<f64> {
    if value.is_finite() && value > 0.0 {
        Some(value.min(MAX_WEIGHT))
    } else {
        None
    }
}

/// Solves the global positioning problem for all nodes at once.
///
/// The solve is independent per connected component, so disconnected takes do
/// not share a gauge. See the module documentation for conventions, residual
/// ordering and complexity limits.
pub fn solve(nodes: &[Node], constraints: &[Constraint], huber_delta: f64) -> SolveResult {
    let n = nodes.len();
    let mut positions = vec![0.0; n];
    let mut residuals = vec![0.0; constraints.len()];
    let mut component = vec![0usize; n];

    if n == 0 {
        return SolveResult {
            positions,
            residuals,
            component,
            component_has_audio: Vec::new(),
            node_residuals: Vec::new(),
        };
    }

    // Validate and collect all rows. `eqs` mirrors the input constraint list so
    // residuals can be reported in the original order.
    let mut edges: Vec<Edge> = Vec::new();
    let mut priors: Vec<PriorRow> = Vec::new();
    let mut eqs: Vec<Option<Equation>> = vec![None; constraints.len()];

    for (ci, constraint) in constraints.iter().enumerate() {
        if constraint.from >= n || constraint.to >= n {
            continue;
        }
        let Some(offset) = valid_offset(constraint.offset_seconds) else {
            continue;
        };
        let Some(weight) = valid_weight(constraint.weight) else {
            continue;
        };
        if constraint.kind == ConstraintKind::Prior {
            priors.push(PriorRow {
                node: constraint.to,
                offset,
                weight,
            });
            eqs[ci] = Some(Equation::Prior(constraint.to, offset));
        } else {
            edges.push(Edge {
                from: constraint.from,
                to: constraint.to,
                offset,
                weight,
            });
            eqs[ci] = Some(Equation::Edge(constraint.from, constraint.to, offset));
        }
    }

    for (index, node) in nodes.iter().enumerate() {
        let Some(offset) = node.prior_offset.and_then(valid_offset) else {
            continue;
        };
        let Some(weight) = valid_weight(node.prior_weight) else {
            continue;
        };
        priors.push(PriorRow {
            node: index,
            offset,
            weight,
        });
    }

    // Rescale all weights by a common factor. The minimizer is unchanged, but
    // the normal equations stay in a numerically safe range.
    let mut max_weight = 0.0_f64;
    for edge in &edges {
        max_weight = max_weight.max(edge.weight);
    }
    for prior in &priors {
        max_weight = max_weight.max(prior.weight);
    }
    if max_weight > 0.0 {
        for edge in &mut edges {
            edge.weight /= max_weight;
        }
        for prior in &mut priors {
            prior.weight /= max_weight;
        }
    }

    // Connected components over accepted edges (self edges connect nothing).
    let mut adjacency: Vec<Vec<usize>> = vec![Vec::new(); n];
    for edge in &edges {
        if edge.from != edge.to {
            adjacency[edge.from].push(edge.to);
            adjacency[edge.to].push(edge.from);
        }
    }

    let mut comp_of = vec![usize::MAX; n];
    let mut comp_members: Vec<Vec<usize>> = Vec::new();
    for start in 0..n {
        if comp_of[start] != usize::MAX {
            continue;
        }
        let cid = comp_members.len();
        let mut members = Vec::new();
        let mut queue = VecDeque::new();
        comp_of[start] = cid;
        queue.push_back(start);
        while let Some(current) = queue.pop_front() {
            members.push(current);
            for &next in &adjacency[current] {
                if comp_of[next] == usize::MAX {
                    comp_of[next] = cid;
                    queue.push_back(next);
                }
            }
        }
        // Ascending order keeps the gauge anchor deterministic (members[0] is
        // the lowest global node index in this component).
        members.sort_unstable();
        comp_members.push(members);
    }

    // Bucket rows by component.
    let comp_count = comp_members.len();
    let mut comp_edges: Vec<Vec<usize>> = vec![Vec::new(); comp_count];
    for (index, edge) in edges.iter().enumerate() {
        comp_edges[comp_of[edge.from]].push(index);
    }
    let mut comp_priors: Vec<Vec<usize>> = vec![Vec::new(); comp_count];
    for (index, prior) in priors.iter().enumerate() {
        comp_priors[comp_of[prior.node]].push(index);
    }

    let use_huber = huber_delta.is_finite() && huber_delta > 0.0;
    let delta = huber_delta;
    let mut local_of = vec![usize::MAX; n];

    for (cid, members) in comp_members.iter().enumerate() {
        let m = members.len();
        for (local, &global) in members.iter().enumerate() {
            local_of[global] = local;
        }

        let local_edges: Vec<LocalEdge> = comp_edges[cid]
            .iter()
            .map(|&index| {
                let edge = &edges[index];
                LocalEdge {
                    from: local_of[edge.from],
                    to: local_of[edge.to],
                    offset: edge.offset,
                    base_weight: edge.weight,
                }
            })
            .collect();

        let mut local_priors: Vec<LocalPrior> = comp_priors[cid]
            .iter()
            .map(|&index| {
                let prior = &priors[index];
                LocalPrior {
                    node: local_of[prior.node],
                    offset: prior.offset,
                    weight: prior.weight,
                }
            })
            .collect();

        if local_priors.is_empty() {
            // Gauge fix: without any absolute information the component is only
            // defined up to translation, so anchor its lowest index at 0.
            local_priors.push(LocalPrior {
                node: 0,
                offset: 0.0,
                weight: GAUGE_WEIGHT,
            });
        }

        let mut weights: Vec<f64> = local_edges.iter().map(|edge| edge.base_weight).collect();
        let mut local_positions = solve_component(m, &local_edges, &local_priors, &weights);

        for _ in 1..IRLS_ITERATIONS {
            if use_huber {
                for (index, edge) in local_edges.iter().enumerate() {
                    let residual =
                        (local_positions[edge.to] - local_positions[edge.from] - edge.offset).abs();
                    weights[index] = if residual > delta {
                        // Huber: cap the influence of outlier measurements.
                        edge.base_weight * (delta / residual)
                    } else {
                        edge.base_weight
                    };
                }
            }
            local_positions = solve_component(m, &local_edges, &local_priors, &weights);
        }

        for (local, &global) in members.iter().enumerate() {
            let mut position = local_positions[local];
            if !position.is_finite() {
                // Defensive fallback: never leak NaN/inf into the caller.
                position = nodes[global]
                    .prior_offset
                    .filter(|value| value.is_finite())
                    .unwrap_or(0.0);
            }
            positions[global] = position;
            component[global] = cid;
        }
    }

    // Per-node confidence: accumulate the absolute residual of every accepted
    // row incident to the node (see the module docs for the accounting).
    let mut node_residuals = vec![0.0; n];
    for (index, eq) in eqs.iter().enumerate() {
        let residual = match *eq {
            Some(Equation::Edge(from, to, offset)) => positions[to] - positions[from] - offset,
            Some(Equation::Prior(node, offset)) => positions[node] - offset,
            None => 0.0,
        };
        // Defensive fallback: never leak NaN/inf into the caller.
        let residual = if residual.is_finite() { residual } else { 0.0 };
        residuals[index] = residual;
        let magnitude = residual.abs();
        match *eq {
            Some(Equation::Edge(from, to, _)) => {
                node_residuals[from] += magnitude;
                node_residuals[to] += magnitude;
            }
            Some(Equation::Prior(node, _)) => node_residuals[node] += magnitude,
            None => {}
        }
    }

    // Audio evidence is a property of the whole component; mirror it onto each
    // member node. Only accepted Audio constraints count, so components placed
    // purely by ordering or priors stay `false`.
    let mut audio_components = vec![false; comp_count];
    for (index, constraint) in constraints.iter().enumerate() {
        if eqs[index].is_some() && constraint.kind == ConstraintKind::Audio {
            audio_components[comp_of[constraint.to]] = true;
        }
    }
    let component_has_audio: Vec<bool> =
        component.iter().map(|&cid| audio_components[cid]).collect();

    SolveResult {
        positions,
        residuals,
        component,
        component_has_audio,
        node_residuals,
    }
}

/// Assembles and solves the dense normal equations of one component.
fn solve_component(
    m: usize,
    edges: &[LocalEdge],
    priors: &[LocalPrior],
    weights: &[f64],
) -> Vec<f64> {
    let mut matrix = vec![vec![0.0; m]; m];
    let mut rhs = vec![0.0; m];

    for (edge, &weight) in edges.iter().zip(weights.iter()) {
        if weight <= 0.0 || edge.from == edge.to {
            // Self edges only contribute a constant to the cost.
            continue;
        }
        matrix[edge.from][edge.from] += weight;
        matrix[edge.to][edge.to] += weight;
        matrix[edge.from][edge.to] -= weight;
        matrix[edge.to][edge.from] -= weight;
        rhs[edge.to] += weight * edge.offset;
        rhs[edge.from] -= weight * edge.offset;
    }

    for prior in priors {
        if prior.weight <= 0.0 {
            continue;
        }
        matrix[prior.node][prior.node] += prior.weight;
        rhs[prior.node] += prior.weight * prior.offset;
    }

    gaussian_solve(&mut matrix, &mut rhs)
}

/// Gaussian elimination with partial pivoting on a dense square system.
///
/// Rank-deficient columns keep their variable at 0 instead of producing NaN.
fn gaussian_solve(matrix: &mut [Vec<f64>], rhs: &mut [f64]) -> Vec<f64> {
    let m = rhs.len();
    let mut solution = vec![0.0; m];
    if m == 0 {
        return solution;
    }

    let mut pivot_columns = vec![usize::MAX; m];
    let mut pivot_row = 0;

    for column in 0..m {
        let mut best_row = pivot_row;
        let mut best_value = 0.0_f64;
        for (row, values) in matrix.iter().enumerate().skip(pivot_row) {
            let candidate = values[column].abs();
            if candidate > best_value {
                best_value = candidate;
                best_row = row;
            }
        }
        if best_value <= PIVOT_EPS {
            continue;
        }

        matrix.swap(pivot_row, best_row);
        rhs.swap(pivot_row, best_row);

        let inverse_pivot = 1.0 / matrix[pivot_row][column];
        for row in (pivot_row + 1)..m {
            let factor = matrix[row][column] * inverse_pivot;
            if factor == 0.0 {
                continue;
            }
            let (head, tail) = matrix.split_at_mut(row);
            let source = &head[pivot_row];
            let target = &mut tail[0];
            for (target_value, source_value) in
                target.iter_mut().zip(source.iter()).skip(column + 1)
            {
                *target_value -= factor * source_value;
            }
            rhs[row] -= factor * rhs[pivot_row];
        }

        pivot_columns[pivot_row] = column;
        pivot_row += 1;
        if pivot_row == m {
            break;
        }
    }

    for row in (0..pivot_row).rev() {
        let column = pivot_columns[row];
        let mut value = rhs[row];
        for (index, coefficient) in matrix[row].iter().enumerate().skip(column + 1) {
            value -= coefficient * solution[index];
        }
        let diagonal = matrix[row][column];
        if diagonal.abs() > PIVOT_EPS {
            solution[column] = value / diagonal;
        }
    }

    solution
}

#[cfg(test)]
mod tests {
    use super::*;

    fn node(id: &str) -> Node {
        Node {
            id: id.to_string(),
            prior_offset: None,
            prior_weight: 0.0,
        }
    }

    fn node_with_prior(id: &str, offset: f64, weight: f64) -> Node {
        Node {
            id: id.to_string(),
            prior_offset: Some(offset),
            prior_weight: weight,
        }
    }

    fn edge(from: usize, to: usize, offset: f64, weight: f64, kind: ConstraintKind) -> Constraint {
        Constraint {
            from,
            to,
            offset_seconds: offset,
            weight,
            kind,
        }
    }

    fn approx(a: f64, b: f64, tolerance: f64) {
        assert!(
            (a - b).abs() <= tolerance,
            "expected {b}, got {a} (tolerance {tolerance})"
        );
    }

    #[test]
    fn two_nodes_single_edge() {
        let nodes = vec![node("a"), node("b")];
        let constraints = vec![edge(0, 1, 2.5, 1.0, ConstraintKind::Audio)];

        let result = solve(&nodes, &constraints, 0.1);

        approx(result.positions[1] - result.positions[0], 2.5, 1e-9);
        approx(result.residuals[0], 0.0, 1e-9);
        assert_eq!(result.component, vec![0, 0]);
    }

    #[test]
    fn chain_offsets_accumulate() {
        let nodes = vec![node("a"), node("b"), node("c")];
        let constraints = vec![
            edge(0, 1, 1.0, 1.0, ConstraintKind::Audio),
            edge(1, 2, 2.0, 1.0, ConstraintKind::Audio),
        ];

        let result = solve(&nodes, &constraints, 0.1);

        approx(result.positions[2] - result.positions[0], 3.0, 1e-9);
        assert!(result.residuals.iter().all(|value| value.abs() < 1e-9));
    }

    #[test]
    fn inconsistent_cycle_keeps_residual() {
        let nodes = vec![node("a"), node("b"), node("c")];
        let constraints = vec![
            edge(0, 1, 1.0, 1.0, ConstraintKind::Audio),
            edge(1, 2, 1.0, 1.0, ConstraintKind::Audio),
            edge(0, 2, 1.0, 1.0, ConstraintKind::Audio),
        ];

        let result = solve(&nodes, &constraints, 0.1);

        // The cycle is over-determined by 1s; the least-squares fit spreads it
        // into 1/3s residuals, which the robust pass must not hide.
        let worst = result
            .residuals
            .iter()
            .fold(0.0_f64, |worst, value| worst.max(value.abs()));
        assert!(worst > 0.1, "worst residual was {worst}");
        assert!(result.residuals.iter().all(|value| value.is_finite()));
    }

    #[test]
    fn prior_only_nodes_honor_priors() {
        let nodes = vec![
            node_with_prior("a", 10.0, 1.0),
            node_with_prior("b", -5.0, 2.0),
            node_with_prior("c", 3.5, 1.0),
        ];

        let result = solve(&nodes, &[], 0.1);

        approx(result.positions[0], 10.0, 1e-9);
        approx(result.positions[1], -5.0, 1e-9);
        approx(result.positions[2], 3.5, 1e-9);
        assert!(result.residuals.is_empty());
    }

    #[test]
    fn disconnected_components_are_labelled_and_positioned() {
        let nodes = vec![
            node("a"),
            node("b"),
            node_with_prior("c", 100.0, 1.0),
            node("d"),
        ];
        let constraints = vec![
            edge(0, 1, 0.5, 1.0, ConstraintKind::Audio),
            edge(2, 3, 2.0, 1.0, ConstraintKind::Audio),
        ];

        let result = solve(&nodes, &constraints, 0.1);

        assert_eq!(result.component[0], result.component[1]);
        assert_eq!(result.component[2], result.component[3]);
        assert_ne!(result.component[0], result.component[2]);

        approx(result.positions[1] - result.positions[0], 0.5, 1e-9);
        approx(result.positions[2], 100.0, 1e-9);
        approx(result.positions[3] - result.positions[2], 2.0, 1e-9);
    }

    #[test]
    fn sequence_constraint_matches_audio_constraint() {
        let nodes = vec![node("a"), node("b")];
        let audio = vec![edge(0, 1, 2.5, 1.0, ConstraintKind::Audio)];
        let sequence = vec![edge(0, 1, 2.5, 1.0, ConstraintKind::Sequence)];

        let audio_result = solve(&nodes, &audio, 0.1);
        let sequence_result = solve(&nodes, &sequence, 0.1);

        assert_eq!(audio_result.positions, sequence_result.positions);
        assert_eq!(audio_result.residuals, sequence_result.residuals);
        approx(
            sequence_result.positions[1] - sequence_result.positions[0],
            2.5,
            1e-9,
        );
    }

    #[test]
    fn empty_and_isolated_nodes_are_safe() {
        let empty = solve(&[], &[], 0.1);
        assert!(empty.positions.is_empty());
        assert!(empty.residuals.is_empty());
        assert!(empty.component.is_empty());

        let isolated = solve(&[node("a"), node("b")], &[], 0.1);
        assert_eq!(isolated.positions, vec![0.0, 0.0]);
        assert_eq!(isolated.component, vec![0, 1]);
    }

    #[test]
    fn invalid_rows_are_ignored() {
        let nodes = vec![node("a"), node("b")];
        let constraints = vec![
            edge(0, 5, 1.0, 1.0, ConstraintKind::Audio), // index out of range
            edge(0, 1, f64::NAN, 1.0, ConstraintKind::Audio), // non-finite offset
            edge(0, 1, 1.0, 0.0, ConstraintKind::Audio), // zero weight
            edge(0, 1, 1.0, -2.0, ConstraintKind::Audio), // negative weight
            edge(0, 1, 1.0, f64::INFINITY, ConstraintKind::Audio), // bad weight
        ];

        let result = solve(&nodes, &constraints, 0.1);

        assert_eq!(result.positions, vec![0.0, 0.0]);
        assert_eq!(result.residuals, vec![0.0; 5]);
        assert_eq!(result.component, vec![0, 1]);
    }

    #[test]
    fn duplicate_constraints_do_not_break_the_solve() {
        let nodes = vec![node("a"), node("b")];
        let constraints = vec![
            edge(0, 1, 2.5, 1.0, ConstraintKind::Audio),
            edge(0, 1, 2.5, 1.0, ConstraintKind::Audio),
        ];

        let result = solve(&nodes, &constraints, 0.1);

        approx(result.positions[1] - result.positions[0], 2.5, 1e-9);
        assert!(result.residuals.iter().all(|value| value.abs() < 1e-9));
    }

    #[test]
    fn huge_weights_are_clamped() {
        let nodes = vec![node("a"), node("b")];
        let constraints = vec![edge(0, 1, 2.5, 1e300, ConstraintKind::Audio)];

        let result = solve(&nodes, &constraints, 0.1);

        assert!(result.positions.iter().all(|value| value.is_finite()));
        approx(result.positions[1] - result.positions[0], 2.5, 1e-9);
    }

    #[test]
    fn prior_kind_constraint_pins_a_node() {
        let nodes = vec![node("a"), node("b")];
        let constraints = vec![edge(0, 1, 4.0, 1.0, ConstraintKind::Prior)];

        let result = solve(&nodes, &constraints, 0.1);

        approx(result.positions[0], 0.0, 1e-9);
        approx(result.positions[1], 4.0, 1e-9);
        approx(result.residuals[0], 0.0, 1e-9);
    }

    #[test]
    fn component_audio_flag_distinguishes_audio_from_prior_and_sequence() {
        let nodes = vec![
            node("a"),
            node("b"),
            node_with_prior("c", 5.0, 1.0),
            node("d"),
            node("e"),
        ];
        let constraints = vec![
            // Component 0: audio evidence.
            edge(0, 1, 1.0, 1.0, ConstraintKind::Audio),
            // Component 1: prior plus a sequence edge, no audio.
            edge(2, 3, 2.0, 1.0, ConstraintKind::Sequence),
            // Component 2: a single node pinned only by a Prior constraint.
            edge(0, 4, 7.0, 1.0, ConstraintKind::Prior),
        ];

        let result = solve(&nodes, &constraints, 0.1);

        assert_eq!(result.component, vec![0, 0, 1, 1, 2]);
        assert_eq!(
            result.component_has_audio,
            vec![true, true, false, false, false]
        );
        assert_eq!(result.component_has_audio.len(), result.positions.len());
    }

    #[test]
    fn node_residuals_vanish_on_a_clean_chain() {
        let nodes = vec![node("a"), node("b"), node("c")];
        let constraints = vec![
            edge(0, 1, 1.0, 1.0, ConstraintKind::Audio),
            edge(1, 2, 2.0, 1.0, ConstraintKind::Audio),
        ];

        let result = solve(&nodes, &constraints, 0.1);

        assert_eq!(result.node_residuals.len(), nodes.len());
        assert!(
            result.node_residuals.iter().all(|value| value.abs() < 1e-9),
            "node residuals were {:?}",
            result.node_residuals
        );
    }

    #[test]
    fn node_residuals_expose_an_inconsistent_cycle() {
        let nodes = vec![node("a"), node("b"), node("c")];
        let constraints = vec![
            edge(0, 1, 1.0, 1.0, ConstraintKind::Audio),
            edge(1, 2, 1.0, 1.0, ConstraintKind::Audio),
            edge(0, 2, 1.0, 1.0, ConstraintKind::Audio),
        ];

        let result = solve(&nodes, &constraints, 0.1);

        // Every node touches two edges whose residuals cannot both be zero.
        let worst = result
            .node_residuals
            .iter()
            .fold(0.0_f64, |worst, value| worst.max(value.abs()));
        assert!(worst > 0.1, "worst node residual was {worst}");

        // Each accepted relative constraint contributes its absolute residual
        // to both endpoints.
        let edge_sum: f64 = result.residuals.iter().map(|value| value.abs()).sum();
        approx(
            result.node_residuals.iter().sum::<f64>(),
            2.0 * edge_sum,
            1e-9,
        );
    }
}
