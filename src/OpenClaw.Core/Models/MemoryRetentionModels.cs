namespace OpenClaw.Core.Models;

/// <summary>
/// Sweep request passed to retention-capable memory stores.
/// All timestamps are UTC.
/// </summary>
public sealed class RetentionSweepRequest
{
    public DateTimeOffset NowUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset SessionExpiresBeforeUtc { get; init; }
    public DateTimeOffset BranchExpiresBeforeUtc { get; init; }
    public bool ArchiveEnabled { get; init; } = true;
    public string ArchivePath { get; init; } = "./memory/archive";
    public int ArchiveRetentionDays { get; init; } = 30;
    public int MaxItems { get; init; } = 1000;
    public bool DryRun { get; init; } = false;
}

/// <summary>
/// Result payload produced by one retention sweep run.
/// </summary>
public sealed class RetentionSweepResult
{
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CompletedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool DryRun { get; init; } = false;
    public bool MaxItemsLimitReached { get; set; }

    public int EligibleSessions { get; set; }
    public int EligibleBranches { get; set; }

    public int ArchivedSessions { get; set; }
    public int ArchivedBranches { get; set; }

    public int DeletedSessions { get; set; }
    public int DeletedBranches { get; set; }

    public int SkippedProtectedSessions { get; set; }
    public int SkippedCorruptSessionItems { get; set; }
    public int SkippedCorruptBranchItems { get; set; }

    public int ArchivePurgedFiles { get; set; }
    public int ArchivePurgeErrors { get; set; }

    public List<string> Errors { get; } = [];

    public int TotalArchived => ArchivedSessions + ArchivedBranches;
    public int TotalDeleted => DeletedSessions + DeletedBranches;
    public int TotalEligible => EligibleSessions + EligibleBranches;
    public long DurationMs => (long)Math.Max(0, (CompletedAtUtc - StartedAtUtc).TotalMilliseconds);
}

/// <summary>
/// Aggregate counts for persisted retention scope entities.
/// </summary>
public sealed class RetentionStoreStats
{
    public string Backend { get; init; } = "";
    public long PersistedSessions { get; init; }
    public long PersistedBranches { get; init; }
}

/// <summary>
/// Runtime status of retention orchestration for doctor and admin endpoints.
/// </summary>
public sealed class RetentionRunStatus
{
    public bool Enabled { get; set; }
    public bool StoreSupportsRetention { get; set; }
    public bool IsRunning { get; set; }
    public DateTimeOffset? LastRunStartedAtUtc { get; set; }
    public DateTimeOffset? LastRunCompletedAtUtc { get; set; }
    public long LastRunDurationMs { get; set; }
    public bool LastRunSucceeded { get; set; }
    public string? LastError { get; set; }
    public long TotalRuns { get; set; }
    public long TotalSweepErrors { get; set; }
    public long TotalArchivedItems { get; set; }
    public long TotalDeletedItems { get; set; }
    public RetentionSweepResult? LastResult { get; set; }
    public RetentionStoreStats? StoreStats { get; set; }
}
