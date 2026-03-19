using System.Collections.Concurrent;

namespace OpenClaw.Core.Observability;

public sealed class ToolUsageTracker
{
    private readonly ConcurrentDictionary<string, ToolUsageCounter> _usage = new(StringComparer.Ordinal);

    public void RecordToolCall(string toolName, TimeSpan duration, bool failed, bool timedOut)
    {
        var counter = _usage.GetOrAdd(toolName, static _ => new ToolUsageCounter());
        Interlocked.Increment(ref counter.Calls);
        if (failed) Interlocked.Increment(ref counter.Failures);
        if (timedOut) Interlocked.Increment(ref counter.Timeouts);

        // Use Interlocked for thread-safe double addition via long bit pattern
        long rawDuration;
        long newRaw;
        do
        {
            rawDuration = Interlocked.Read(ref counter.TotalDurationMs);
            var current = BitConverter.Int64BitsToDouble(rawDuration);
            var updated = current + duration.TotalMilliseconds;
            newRaw = BitConverter.DoubleToInt64Bits(updated);
        } while (Interlocked.CompareExchange(ref counter.TotalDurationMs, newRaw, rawDuration) != rawDuration);
    }

    public IReadOnlyList<ToolUsageSnapshot> Snapshot()
        => _usage
            .Select(static kvp => new ToolUsageSnapshot
            {
                ToolName = kvp.Key,
                Calls = Interlocked.Read(ref kvp.Value.Calls),
                Failures = Interlocked.Read(ref kvp.Value.Failures),
                Timeouts = Interlocked.Read(ref kvp.Value.Timeouts),
                TotalDurationMs = BitConverter.Int64BitsToDouble(Interlocked.Read(ref kvp.Value.TotalDurationMs))
            })
            .OrderByDescending(static s => s.Calls)
            .ThenBy(static s => s.ToolName, StringComparer.Ordinal)
            .ToArray();

    private sealed class ToolUsageCounter
    {
        public long Calls;
        public long Failures;
        public long Timeouts;
        public long TotalDurationMs; // stored as bit pattern of double
    }
}

public sealed class ToolUsageSnapshot
{
    public required string ToolName { get; init; }
    public long Calls { get; init; }
    public long Failures { get; init; }
    public long Timeouts { get; init; }
    public double TotalDurationMs { get; init; }
}
