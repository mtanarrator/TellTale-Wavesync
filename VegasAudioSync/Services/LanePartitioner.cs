using System;
using System.Collections.Generic;

namespace VegasAudioSync.Services
{
    /// <summary>
    /// Pure interval-partitioning helper used to keep events from overlapping on one
    /// track. Overlaps in VEGAS create automatic crossfades, so synchronized events
    /// that land on top of each other are spread across additional lanes.
    /// </summary>
    public static class LanePartitioner
    {
        /// <summary>
        /// Assigns each item a lane such that no two items in the same lane overlap.
        /// Returns the lane index per input item and reports the number of lanes used.
        /// </summary>
        /// <param name="startsMs">Start time of each item in milliseconds.</param>
        /// <param name="endsMs">End time of each item in milliseconds.</param>
        /// <param name="laneCount">Number of lanes required (at least 1 for non-empty input).</param>
        /// <param name="toleranceMs">
        /// Items closer than this are treated as adjacent, not overlapping. VEGAS events
        /// that butt up against each other share an exact boundary.
        /// </param>
        /// <param name="pinned">
        /// Optional flags marking items that must stay on lane 0 (for example clips the
        /// sync pass did not touch, or deliberate pre-existing crossfades). Pinned items
        /// are never relocated; unpinned items are routed around them.
        /// </param>
        public static int[] Assign(
            IReadOnlyList<double> startsMs,
            IReadOnlyList<double> endsMs,
            out int laneCount,
            double toleranceMs = 1.0,
            IReadOnlyList<bool> pinned = null)
        {
            if (startsMs == null || endsMs == null)
                throw new ArgumentNullException(nameof(startsMs));

            int n = Math.Min(startsMs.Count, endsMs.Count);
            var lanes = new int[n];
            laneCount = 0;

            if (n == 0)
                return lanes;

            var laneIntervals = new List<List<Interval>>
            {
                new List<Interval>() // lane 0: the original track
            };

            // 1. Pinned items never move.
            for (int i = 0; i < n; i++)
            {
                if (pinned != null && i < pinned.Count && pinned[i])
                {
                    lanes[i] = 0;
                    laneIntervals[0].Add(new Interval(startsMs[i], endsMs[i]));
                }
            }

            // 2. Remaining items are packed in start order into the first lane where
            //    they do not overlap anything already placed there.
            var order = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                if (pinned != null && i < pinned.Count && pinned[i]) continue;
                order.Add(i);
            }

            order.Sort((a, b) =>
            {
                int cmp = startsMs[a].CompareTo(startsMs[b]);
                if (cmp != 0) return cmp;
                cmp = endsMs[a].CompareTo(endsMs[b]);
                return cmp != 0 ? cmp : a.CompareTo(b);
            });

            foreach (int idx in order)
            {
                double start = startsMs[idx];
                double end = endsMs[idx];

                int lane = -1;
                for (int i = 0; i < laneIntervals.Count; i++)
                {
                    if (!OverlapsAny(laneIntervals[i], start, end, toleranceMs))
                    {
                        lane = i;
                        break;
                    }
                }

                if (lane < 0)
                {
                    lane = laneIntervals.Count;
                    laneIntervals.Add(new List<Interval>());
                }

                laneIntervals[lane].Add(new Interval(start, end));
                lanes[idx] = lane;
            }

            laneCount = laneIntervals.Count;
            return lanes;
        }

        private static bool OverlapsAny(List<Interval> intervals, double start, double end, double toleranceMs)
        {
            foreach (var iv in intervals)
            {
                // Overlap when one interval starts strictly before the other ends
                // (with a small tolerance for shared VEGAS boundaries).
                if (start < iv.End - toleranceMs && end > iv.Start + toleranceMs)
                {
                    return true;
                }
            }
            return false;
        }

        private readonly struct Interval
        {
            public readonly double Start;
            public readonly double End;

            public Interval(double start, double end)
            {
                Start = start;
                End = end;
            }
        }
    }
}
