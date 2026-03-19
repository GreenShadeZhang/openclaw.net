using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Agent.Tools;

namespace OpenClaw.Agent;

/// <summary>
/// Enforces contract-scoped tool restrictions at tool-call time.
/// Checks path-scoped capabilities and tool call count limits.
/// </summary>
public sealed class ContractScopeHook : IToolHookWithContext
{
    private readonly Func<string, ContractPolicy?> _contractResolver;
    private readonly Func<string, int> _toolCallCounter;
    private readonly ILogger _logger;

    public string Name => "ContractScope";

    /// <param name="contractResolver">Resolves a session ID to its contract policy (or null).</param>
    /// <param name="toolCallCounter">Returns the current tool call count for a session ID.</param>
    /// <param name="logger">Logger instance.</param>
    public ContractScopeHook(
        Func<string, ContractPolicy?> contractResolver,
        Func<string, int> toolCallCounter,
        ILogger logger)
    {
        _contractResolver = contractResolver;
        _toolCallCounter = toolCallCounter;
        _logger = logger;
    }

    public ValueTask<bool> BeforeExecuteAsync(string toolName, string arguments, CancellationToken ct)
        => ValueTask.FromResult(true); // No-op for non-context path; context variant handles enforcement.

    public ValueTask<bool> BeforeExecuteAsync(ToolHookContext context, CancellationToken ct)
    {
        var policy = _contractResolver(context.SessionId);
        if (policy is null)
            return ValueTask.FromResult(true);

        // Check MaxToolCalls
        if (policy.MaxToolCalls > 0)
        {
            var count = _toolCallCounter(context.SessionId);
            if (count >= policy.MaxToolCalls)
            {
                _logger.LogInformation(
                    "ContractScope: denied tool {Tool} for session {Session} — MaxToolCalls ({Max}) reached",
                    context.ToolName, context.SessionId, policy.MaxToolCalls);
                return ValueTask.FromResult(false);
            }
        }

        // Check scoped capabilities (path restrictions)
        var scope = FindScope(policy, context.ToolName);
        if (scope is not null && scope.AllowedPaths.Length > 0)
        {
            if (TryExtractPathArgument(context.ToolName, context.ArgumentsJson, out var path) &&
                !string.IsNullOrWhiteSpace(path))
            {
                if (!IsPathAllowed(path!, scope.AllowedPaths))
                {
                    _logger.LogInformation(
                        "ContractScope: denied tool {Tool} path {Path} for session {Session} — outside scoped paths",
                        context.ToolName, path, context.SessionId);
                    return ValueTask.FromResult(false);
                }
            }
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask AfterExecuteAsync(string toolName, string arguments, string result, TimeSpan duration, bool failed, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask AfterExecuteAsync(ToolHookContext context, string result, TimeSpan duration, bool failed, CancellationToken ct)
        => ValueTask.CompletedTask;

    private static ScopedCapability? FindScope(ContractPolicy policy, string toolName)
    {
        foreach (var scope in policy.ScopedCapabilities)
        {
            if (string.Equals(scope.ToolName, toolName, StringComparison.Ordinal))
                return scope;
        }
        return null;
    }

    private static bool IsPathAllowed(string path, string[] allowedPaths)
    {
        var expanded = ExpandTilde(path);
        var full = ToolPathPolicy.ResolveRealPath(expanded);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var allowed in allowedPaths)
        {
            var allowedExpanded = ExpandTilde(allowed.Trim());
            var allowedFull = Path.GetFullPath(allowedExpanded);

            if (string.Equals(full, allowedFull, comparison))
                return true;

            var root = allowedFull.EndsWith(Path.DirectorySeparatorChar)
                ? allowedFull
                : allowedFull + Path.DirectorySeparatorChar;

            if (full.StartsWith(root, comparison))
                return true;
        }

        return false;
    }

    private static bool TryExtractPathArgument(string toolName, string arguments, out string? path)
    {
        path = null;
        var prop = toolName switch
        {
            "git" => "cwd",
            _ => "path"
        };

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            if (doc.RootElement.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String)
            {
                path = p.GetString();
                return !string.IsNullOrWhiteSpace(path);
            }
        }
        catch { }

        return false;
    }

    private static string ExpandTilde(string path)
    {
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path.Length == 1 ? home : Path.Combine(home, path[2..]);
        }
        return path;
    }
}
