using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal sealed class ContractStore
{
    private const string DirectoryName = "admin";
    private const string FileName = "contracts.jsonl";

    private readonly string _path;
    private readonly object _gate = new();
    private readonly ILogger<ContractStore> _logger;

    public ContractStore(string storagePath, ILogger<ContractStore> logger)
    {
        var rootedStoragePath = Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.GetFullPath(storagePath);
        _path = Path.Combine(rootedStoragePath, DirectoryName, FileName);
        _logger = logger;
    }

    public void Append(ContractExecutionSnapshot entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var line = JsonSerializer.Serialize(entry, CoreJsonContext.Default.ContractExecutionSnapshot);
            lock (_gate)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append contract snapshot to {Path}", _path);
        }
    }

    public IReadOnlyList<ContractExecutionSnapshot> Query(string? sessionId = null, string? contractId = null, int limit = 50)
    {
        var clampedLimit = Math.Clamp(limit, 1, 500);
        return JsonlQueryBuffer.ReadLatest(
            _path,
            _gate,
            clampedLimit,
            CoreJsonContext.Default.ContractExecutionSnapshot,
            item =>
            {
                if (!string.IsNullOrWhiteSpace(sessionId) &&
                    !string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
                    return false;
                if (!string.IsNullOrWhiteSpace(contractId) &&
                    !string.Equals(item.ContractId, contractId, StringComparison.Ordinal))
                    return false;
                return true;
            },
            _logger,
            "Failed to parse contract snapshot line from {Path}");
    }
}
