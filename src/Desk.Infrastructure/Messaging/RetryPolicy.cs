using System.Globalization;

namespace Desk.Infrastructure.Messaging;

/// <summary>What to do with a failed delivery.</summary>
public readonly record struct RetryDecision(bool Retry, int NextAttempt, TimeSpan Delay)
{
    public static RetryDecision DeadLetter { get; } = new(false, 0, TimeSpan.Zero);
}

/// <summary>
/// Retry schedule. <c>attempt</c> is the header of the delivery that just failed (0 = first delivery).
/// With the delays 5 s, 30 s, 2 min: failures of attempts 0, 1 and 2 are retried after those delays,
/// the failure of attempt 3 (the third retry) goes to the dead-letter queue.
/// </summary>
public sealed class RetryPolicy(IReadOnlyList<TimeSpan> delays)
{
    public static readonly TimeSpan[] DefaultDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    public IReadOnlyList<TimeSpan> Delays { get; } = delays;

    public int MaxRetries => Delays.Count;

    public RetryDecision Decide(int failedAttempt)
    {
        var attempt = Math.Max(failedAttempt, 0);
        return attempt < Delays.Count ? new RetryDecision(true, attempt + 1, Delays[attempt]) : RetryDecision.DeadLetter;
    }

    /// <summary>Compact, unambiguous label used in queue names: 500ms, 5s, 90s, 2m.</summary>
    public static string Label(TimeSpan delay)
    {
        var ms = (long)delay.TotalMilliseconds;
        if (ms % 1000 != 0)
        {
            return ms.ToString(CultureInfo.InvariantCulture) + "ms";
        }

        var seconds = ms / 1000;
        return seconds >= 60 && seconds % 60 == 0
            ? (seconds / 60).ToString(CultureInfo.InvariantCulture) + "m"
            : seconds.ToString(CultureInfo.InvariantCulture) + "s";
    }
}
