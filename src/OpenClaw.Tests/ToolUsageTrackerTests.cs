using OpenClaw.Core.Observability;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ToolUsageTrackerTests
{
    [Fact]
    public void RecordToolCall_TracksPerToolName()
    {
        var tracker = new ToolUsageTracker();

        tracker.RecordToolCall("web_search", TimeSpan.FromMilliseconds(100), failed: false, timedOut: false);
        tracker.RecordToolCall("file_read", TimeSpan.FromMilliseconds(50), failed: false, timedOut: false);
        tracker.RecordToolCall("web_search", TimeSpan.FromMilliseconds(200), failed: false, timedOut: false);

        var snapshot = tracker.Snapshot();
        Assert.Equal(2, snapshot.Count);

        var webSearch = snapshot.First(s => s.ToolName == "web_search");
        Assert.Equal(2, webSearch.Calls);
        Assert.Equal(0, webSearch.Failures);

        var fileRead = snapshot.First(s => s.ToolName == "file_read");
        Assert.Equal(1, fileRead.Calls);
    }

    [Fact]
    public void Snapshot_ReturnsAllTrackedTools_Sorted()
    {
        var tracker = new ToolUsageTracker();

        tracker.RecordToolCall("shell", TimeSpan.FromMilliseconds(10), false, false);
        tracker.RecordToolCall("web_search", TimeSpan.FromMilliseconds(10), false, false);
        tracker.RecordToolCall("web_search", TimeSpan.FromMilliseconds(10), false, false);
        tracker.RecordToolCall("file_read", TimeSpan.FromMilliseconds(10), false, false);

        var snapshot = tracker.Snapshot();
        // Sorted by calls descending, then name ascending
        Assert.Equal("web_search", snapshot[0].ToolName);
        Assert.Equal(2, snapshot[0].Calls);
        // file_read and shell both have 1 call, sorted alphabetically
        Assert.Equal("file_read", snapshot[1].ToolName);
        Assert.Equal("shell", snapshot[2].ToolName);
    }

    [Fact]
    public void RecordToolCall_ConcurrentSafe()
    {
        var tracker = new ToolUsageTracker();
        const int iterations = 10_000;

        Parallel.For(0, iterations, i =>
        {
            tracker.RecordToolCall("concurrent_tool", TimeSpan.FromMilliseconds(1), failed: i % 10 == 0, timedOut: false);
        });

        var snapshot = tracker.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(iterations, snapshot[0].Calls);
        Assert.Equal(iterations / 10, snapshot[0].Failures);
    }

    [Fact]
    public void RecordToolCall_TracksDurationAndFailures()
    {
        var tracker = new ToolUsageTracker();

        tracker.RecordToolCall("tool_a", TimeSpan.FromMilliseconds(100), failed: false, timedOut: false);
        tracker.RecordToolCall("tool_a", TimeSpan.FromMilliseconds(200), failed: true, timedOut: false);
        tracker.RecordToolCall("tool_a", TimeSpan.FromMilliseconds(300), failed: true, timedOut: true);

        var snapshot = tracker.Snapshot();
        var tool = snapshot.Single();
        Assert.Equal(3, tool.Calls);
        Assert.Equal(2, tool.Failures);
        Assert.Equal(1, tool.Timeouts);
        Assert.Equal(600.0, tool.TotalDurationMs, precision: 1);
    }
}
