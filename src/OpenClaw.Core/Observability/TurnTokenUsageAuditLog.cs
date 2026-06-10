using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Observability;

/// <summary>
/// Thread-safe, append-only JSON-lines writer for per-turn token usage.
/// </summary>
public sealed class TurnTokenUsageAuditLog : ITurnTokenUsageObserver
{
    private readonly object _sync = new();
    private readonly string? _filePath;
    private readonly ILogger<TurnTokenUsageAuditLog>? _logger;

    public TurnTokenUsageAuditLog(string? filePath, ILogger<TurnTokenUsageAuditLog>? logger = null)
    {
        _logger = logger;
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            _filePath = fullPath;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to initialize turn token usage audit path for {Path}; file logging will be disabled", filePath);
            _filePath = null;
        }
    }

    public void RecordTurn(TurnTokenUsageRecord record)
    {
        var filePath = _filePath;
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            var json = JsonSerializer.Serialize(record, TurnTokenUsageJsonContext.Default.TurnTokenUsageRecord);
            lock (_sync)
            {
                File.AppendAllText(filePath, json + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to append turn token usage entry to {Path}", filePath);
        }
    }
}

public sealed class CompositeTurnTokenUsageObserver : ITurnTokenUsageObserver
{
    private readonly IReadOnlyList<ITurnTokenUsageObserver> _observers;
    private readonly ILogger<CompositeTurnTokenUsageObserver>? _logger;

    public CompositeTurnTokenUsageObserver(
        IReadOnlyList<ITurnTokenUsageObserver> observers,
        ILogger<CompositeTurnTokenUsageObserver>? logger = null)
    {
        _observers = observers;
        _logger = logger;
    }

    public void RecordTurn(TurnTokenUsageRecord record)
    {
        foreach (var observer in _observers)
        {
            try
            {
                observer.RecordTurn(record);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(
                    ex,
                    "Turn token usage observer {ObserverType} failed while recording session {SessionId}",
                    observer.GetType().FullName,
                    record.SessionId);
            }
        }
    }
}

[JsonSerializable(typeof(TurnTokenUsageRecord))]
internal sealed partial class TurnTokenUsageJsonContext : JsonSerializerContext;