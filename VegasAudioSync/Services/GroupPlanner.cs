using System;
using System.Collections.Generic;

namespace VegasAudioSync.Services
{
    /// <summary>
    /// Pure math/validation helper for moving a VEGAS event group as one rigid unit.
    /// Kept free of ScriptPortal.Vegas types so it can be unit-tested in TestHarness.
    /// </summary>
    public static class GroupPlanner
    {
        /// <summary>
        /// Computes one delta from the primary and applies it to every member,
        /// preserving each member's offset relative to the primary.
        /// </summary>
        public static bool TryPlan(
            double primaryCurrentStart,
            double primaryResolvedStart,
            IReadOnlyList<double> memberStarts,
            out double delta,
            out double[] targets)
        {
            delta = 0.0;
            targets = Array.Empty<double>();

            if (memberStarts == null || memberStarts.Count == 0)
                return false;
            if (!IsFinite(primaryCurrentStart) || !IsFinite(primaryResolvedStart))
                return false;

            delta = primaryResolvedStart - primaryCurrentStart;
            if (!IsFinite(delta))
                return false;

            targets = new double[memberStarts.Count];
            for (int i = 0; i < memberStarts.Count; i++)
            {
                if (!IsFinite(memberStarts[i]))
                    return false;
                targets[i] = memberStarts[i] + delta;
                if (!IsFinite(targets[i]))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Verifies that a planned move does not alter any member's offset relative to
        /// the first member (internal sync preserved).
        /// </summary>
        public static bool PreservesInternalOffsets(
            IReadOnlyList<double> currentStarts,
            IReadOnlyList<double> targetStarts,
            double toleranceSeconds = 0.0005)
        {
            if (currentStarts == null || targetStarts == null) return false;
            if (currentStarts.Count == 0 || currentStarts.Count != targetStarts.Count) return false;

            double baseCurrent = currentStarts[0];
            double baseTarget = targetStarts[0];

            for (int i = 1; i < currentStarts.Count; i++)
            {
                double currentOffset = currentStarts[i] - baseCurrent;
                double targetOffset = targetStarts[i] - baseTarget;
                if (Math.Abs(currentOffset - targetOffset) > toleranceSeconds)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Pre-validates every group member before any mutation. A partial group move
        /// must never commit inside the single UndoBlock.
        /// </summary>
        public static bool ValidateMembers(IReadOnlyList<GroupMemberState> members, out string reason)
        {
            reason = null;
            if (members == null || members.Count == 0)
            {
                reason = "group has no members";
                return false;
            }

            foreach (var member in members)
            {
                if (member == null)
                {
                    reason = "group contains a null member";
                    return false;
                }
                if (!member.IsValid)
                {
                    reason = $"event {member.EventId} is invalid";
                    return false;
                }
                if (member.IsLocked)
                {
                    reason = $"event {member.EventId} is locked";
                    return false;
                }
            }

            return true;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    /// <summary>Validation state for one group member, supplied by the applier.</summary>
    public sealed class GroupMemberState
    {
        public long EventId;
        public double StartSeconds;
        public bool IsValid = true;
        public bool IsLocked;
    }
}
