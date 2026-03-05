using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

/// <summary>
/// Optional store capabilities used by background memory retention services.
/// This interface is additive and does not change <see cref="IMemoryStore"/>.
/// </summary>
public interface IMemoryRetentionStore
{
    ValueTask<RetentionSweepResult> SweepAsync(
        RetentionSweepRequest request,
        IReadOnlySet<string> protectedSessionIds,
        CancellationToken ct);

    ValueTask<RetentionStoreStats> GetRetentionStatsAsync(CancellationToken ct);
}
