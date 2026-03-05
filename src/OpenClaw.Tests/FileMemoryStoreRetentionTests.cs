using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FileMemoryStoreRetentionTests
{
    [Fact]
    public async Task SweepAsync_ArchivesAndDeletesExpiredSessionAndBranch()
    {
        var root = CreateTempDir();
        var archive = Path.Combine(root, "archive");
        var now = new DateTimeOffset(2026, 03, 04, 12, 0, 0, TimeSpan.Zero);
        var store = new FileMemoryStore(root, maxCachedSessions: 8);

        var expiredSession = new Session
        {
            Id = "session-expired",
            ChannelId = "websocket",
            SenderId = "alice",
            LastActiveAt = now.AddDays(-45)
        };
        var freshSession = new Session
        {
            Id = "session-fresh",
            ChannelId = "websocket",
            SenderId = "bob",
            LastActiveAt = now.AddDays(-1)
        };

        var expiredBranch = new SessionBranch
        {
            BranchId = "branch-expired",
            SessionId = "session-expired",
            Name = "old",
            CreatedAt = now.AddDays(-20),
            History = []
        };
        var freshBranch = new SessionBranch
        {
            BranchId = "branch-fresh",
            SessionId = "session-fresh",
            Name = "new",
            CreatedAt = now.AddDays(-1),
            History = []
        };

        await store.SaveSessionAsync(expiredSession, CancellationToken.None);
        await store.SaveSessionAsync(freshSession, CancellationToken.None);
        await store.SaveBranchAsync(expiredBranch, CancellationToken.None);
        await store.SaveBranchAsync(freshBranch, CancellationToken.None);

        var result = await store.SweepAsync(
            new RetentionSweepRequest
            {
                NowUtc = now,
                SessionExpiresBeforeUtc = now.AddDays(-30),
                BranchExpiresBeforeUtc = now.AddDays(-14),
                ArchiveEnabled = true,
                ArchivePath = archive,
                ArchiveRetentionDays = 30,
                MaxItems = 1000
            },
            protectedSessionIds: new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None);

        Assert.Equal(1, result.DeletedSessions);
        Assert.Equal(1, result.DeletedBranches);
        Assert.Equal(1, result.ArchivedSessions);
        Assert.Equal(1, result.ArchivedBranches);

        Assert.Null(await store.GetSessionAsync("session-expired", CancellationToken.None));
        Assert.NotNull(await store.GetSessionAsync("session-fresh", CancellationToken.None));
        Assert.Null(await store.LoadBranchAsync("branch-expired", CancellationToken.None));
        Assert.NotNull(await store.LoadBranchAsync("branch-fresh", CancellationToken.None));

        Assert.NotEmpty(Directory.EnumerateFiles(archive, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SweepAsync_ProtectedSessionIsSkipped()
    {
        var root = CreateTempDir();
        var store = new FileMemoryStore(root, maxCachedSessions: 8);
        var now = new DateTimeOffset(2026, 03, 04, 12, 0, 0, TimeSpan.Zero);

        await store.SaveSessionAsync(new Session
        {
            Id = "session-protected",
            ChannelId = "websocket",
            SenderId = "alice",
            LastActiveAt = now.AddDays(-90)
        }, CancellationToken.None);

        var result = await store.SweepAsync(
            new RetentionSweepRequest
            {
                NowUtc = now,
                SessionExpiresBeforeUtc = now.AddDays(-30),
                BranchExpiresBeforeUtc = now.AddDays(-14),
                ArchiveEnabled = false,
                ArchivePath = Path.Combine(root, "archive"),
                ArchiveRetentionDays = 30,
                MaxItems = 1000
            },
            protectedSessionIds: new HashSet<string>(StringComparer.Ordinal)
            {
                "session-protected"
            },
            CancellationToken.None);

        Assert.Equal(1, result.SkippedProtectedSessions);
        Assert.Equal(0, result.DeletedSessions);
        Assert.NotNull(await store.GetSessionAsync("session-protected", CancellationToken.None));
    }

    [Fact]
    public async Task SweepAsync_ArchiveFailurePreventsDelete()
    {
        var root = CreateTempDir();
        var store = new FileMemoryStore(root, maxCachedSessions: 8);
        var now = new DateTimeOffset(2026, 03, 04, 12, 0, 0, TimeSpan.Zero);
        var invalidArchiveRoot = Path.Combine(root, "archive-blocker");
        await File.WriteAllTextAsync(invalidArchiveRoot, "not-a-directory");

        await store.SaveSessionAsync(new Session
        {
            Id = "session-expired",
            ChannelId = "websocket",
            SenderId = "alice",
            LastActiveAt = now.AddDays(-90)
        }, CancellationToken.None);

        var result = await store.SweepAsync(
            new RetentionSweepRequest
            {
                NowUtc = now,
                SessionExpiresBeforeUtc = now.AddDays(-30),
                BranchExpiresBeforeUtc = now.AddDays(-14),
                ArchiveEnabled = true,
                ArchivePath = invalidArchiveRoot,
                ArchiveRetentionDays = 30,
                MaxItems = 1000
            },
            protectedSessionIds: new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None);

        Assert.Equal(0, result.DeletedSessions);
        Assert.NotEmpty(result.Errors);
        Assert.NotNull(await store.GetSessionAsync("session-expired", CancellationToken.None));
    }

    [Fact]
    public async Task SweepAsync_PurgesExpiredArchiveFiles()
    {
        var root = CreateTempDir();
        var archive = Path.Combine(root, "archive");
        Directory.CreateDirectory(archive);
        var oldFile = Path.Combine(archive, "old.json");
        var newFile = Path.Combine(archive, "new.json");
        await File.WriteAllTextAsync(oldFile, "{}");
        await File.WriteAllTextAsync(newFile, "{}");

        var now = new DateTimeOffset(2026, 03, 04, 12, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(oldFile, now.UtcDateTime.AddDays(-60));
        File.SetLastWriteTimeUtc(newFile, now.UtcDateTime.AddDays(-1));

        var store = new FileMemoryStore(root, maxCachedSessions: 8);
        var result = await store.SweepAsync(
            new RetentionSweepRequest
            {
                NowUtc = now,
                SessionExpiresBeforeUtc = now.AddDays(-30),
                BranchExpiresBeforeUtc = now.AddDays(-14),
                ArchiveEnabled = true,
                ArchivePath = archive,
                ArchiveRetentionDays = 30,
                MaxItems = 1000
            },
            protectedSessionIds: new HashSet<string>(StringComparer.Ordinal),
            CancellationToken.None);

        Assert.Equal(1, result.ArchivePurgedFiles);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}
