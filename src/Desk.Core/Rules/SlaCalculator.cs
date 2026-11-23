namespace Desk.Core.Rules;

public readonly record struct SlaDeadlines(DateTimeOffset ResponseDueAt, DateTimeOffset ResolutionDueAt);

/// <summary>Pure SLA deadline arithmetic. Deadlines are counted from creation; time spent in Pending is added back.</summary>
public static class SlaCalculator
{
    public static SlaDeadlines Compute(
        DateTimeOffset createdAt, int firstResponseMinutes, int resolutionMinutes, TimeSpan pausedTotal = default) =>
        new(createdAt.AddMinutes(firstResponseMinutes) + pausedTotal,
            createdAt.AddMinutes(resolutionMinutes) + pausedTotal);

    /// <summary>Duration spent in Pending; never negative (clock skew).</summary>
    public static TimeSpan PausedDuration(DateTimeOffset pendingSince, DateTimeOffset now) =>
        now > pendingSince ? now - pendingSince : TimeSpan.Zero;

    public static SlaDeadlines Shift(SlaDeadlines deadlines, TimeSpan paused) =>
        new(deadlines.ResponseDueAt + paused, deadlines.ResolutionDueAt + paused);
}
