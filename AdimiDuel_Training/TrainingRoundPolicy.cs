using System;

namespace AdimiDuel_Training
{
    internal struct TrainingRoundOutcome
    {
        internal bool Complete;
        internal int? WinningSide;
        internal string Reason;
        internal bool Interrupted;
    }

    // Only a confirmed, attributable removal and a living opponent can win.
    // Unknown removals, simultaneous deaths and timeouts remain audit records.
    internal static class TrainingRoundPolicy
    {
        internal static TrainingRoundOutcome Resolve(bool removedA, bool removedB, bool invalidRemoval,
            bool aliveA, bool aliveB, double elapsed, double timeout)
        {
            if (invalidRemoval) return Abort("unattributed_removal");
            if (removedA && removedB) return Abort("draw");
            if (removedA) return aliveB ? Win(1) : Abort("unobserved_removal");
            if (removedB) return aliveA ? Win(0) : Abort("unobserved_removal");
            if (!aliveA || !aliveB) return Abort("unobserved_removal");
            if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0d
                || double.IsNaN(timeout) || double.IsInfinity(timeout) || timeout <= 0d) return Abort("invalid_time");
            if (elapsed >= timeout) return Abort("timeout");
            return new TrainingRoundOutcome { Complete = false };
        }

        private static TrainingRoundOutcome Win(int side) => new TrainingRoundOutcome
        { Complete = true, WinningSide = side, Reason = "side_win", Interrupted = false };

        private static TrainingRoundOutcome Abort(string reason) => new TrainingRoundOutcome
        { Complete = true, WinningSide = null, Reason = reason, Interrupted = true };
    }
}
