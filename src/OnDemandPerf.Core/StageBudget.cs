namespace OnDemandPerf.Core;

/// <summary>
/// A stage stops at its own safety budget or at the time left in the run, whichever comes first. A run that is going long
/// then ends the stage as TimedOut with its completed measurements, instead of the run deadline cutting it off mid-stage.
/// </summary>
public static class StageBudget
{
    /// <param name="budgetSeconds">The stage's own safety budget.</param>
    /// <param name="available">Time left for this stage in the run; null when the caller imposes no run deadline.</param>
    public static TimeSpan Limit(int budgetSeconds, TimeSpan? available)
    {
        var limit = TimeSpan.FromSeconds(budgetSeconds);
        if (available is not { } left || left >= limit) return limit;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}
