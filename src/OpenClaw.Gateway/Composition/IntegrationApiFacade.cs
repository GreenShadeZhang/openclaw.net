using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;

namespace OpenClaw.Gateway.Composition;

internal sealed class IntegrationApiFacade
{
    private readonly GatewayStartupContext _startup;
    private readonly GatewayAppRuntime _runtime;
    private readonly ISessionAdminStore _sessionAdminStore;

    public IntegrationApiFacade(
        GatewayStartupContext startup,
        GatewayAppRuntime runtime,
        ISessionAdminStore sessionAdminStore)
    {
        _startup = startup;
        _runtime = runtime;
        _sessionAdminStore = sessionAdminStore;
    }

    public IntegrationStatusResponse BuildStatusResponse()
    {
        _runtime.RuntimeMetrics.SetActiveSessions(_runtime.SessionManager.ActiveCount);
        _runtime.RuntimeMetrics.SetCircuitBreakerState((int)_runtime.AgentRuntime.CircuitBreakerState);

        return new IntegrationStatusResponse
        {
            Health = new HealthResponse { Status = "ok", Uptime = Environment.TickCount64 },
            Runtime = _startup.RuntimeState,
            Metrics = _runtime.RuntimeMetrics.Snapshot(),
            ActiveSessions = _runtime.SessionManager.ActiveCount,
            PendingApprovals = _runtime.ToolApprovalService.ListPending().Count,
            ActiveApprovalGrants = _runtime.Operations.ApprovalGrants.List().Count
        };
    }

    public async Task<IntegrationSessionsResponse> ListSessionsAsync(int page, int pageSize, SessionListQuery query, CancellationToken cancellationToken)
    {
        var metadataById = _runtime.Operations.SessionMetadata.GetAll();
        var persisted = await _sessionAdminStore.ListSessionsAsync(page, pageSize, query, cancellationToken);
        var active = (await _runtime.SessionManager.ListActiveAsync(cancellationToken))
            .Where(session => MatchesSessionQuery(session, query, metadataById))
            .OrderByDescending(static session => session.LastActiveAt)
            .Select(static session => new SessionSummary
            {
                Id = session.Id,
                ChannelId = session.ChannelId,
                SenderId = session.SenderId,
                CreatedAt = session.CreatedAt,
                LastActiveAt = session.LastActiveAt,
                State = session.State,
                HistoryTurns = session.History.Count,
                TotalInputTokens = session.TotalInputTokens,
                TotalOutputTokens = session.TotalOutputTokens,
                IsActive = true
            })
            .ToArray();

        var filteredPersisted = new PagedSessionList
        {
            Page = persisted.Page,
            PageSize = persisted.PageSize,
            HasMore = persisted.HasMore,
            Items = persisted.Items
                .Where(item => MatchesSummaryQuery(item, query, metadataById))
                .ToArray()
        };

        return new IntegrationSessionsResponse
        {
            Filters = query,
            Active = active,
            Persisted = filteredPersisted
        };
    }

    public async Task<IntegrationSessionDetailResponse?> GetSessionAsync(string id, CancellationToken cancellationToken)
    {
        var session = await _runtime.SessionManager.LoadAsync(id, cancellationToken);
        if (session is null)
            return null;

        var branches = await _runtime.SessionManager.ListBranchesAsync(id, cancellationToken);

        return new IntegrationSessionDetailResponse
        {
            Session = session,
            IsActive = _runtime.SessionManager.IsActive(id),
            BranchCount = branches.Count,
            Metadata = _runtime.Operations.SessionMetadata.Get(id)
        };
    }

    public async Task<IntegrationSessionTimelineResponse?> GetSessionTimelineAsync(string id, int limit, CancellationToken cancellationToken)
    {
        var session = await _runtime.SessionManager.LoadAsync(id, cancellationToken);
        if (session is null)
            return null;

        return new IntegrationSessionTimelineResponse
        {
            SessionId = id,
            Events = _runtime.Operations.RuntimeEvents.Query(new RuntimeEventQuery { SessionId = id, Limit = limit }),
            ProviderTurns = _runtime.ProviderUsage.RecentTurns(id, limit)
        };
    }

    public IntegrationRuntimeEventsResponse QueryRuntimeEvents(RuntimeEventQuery query)
        => new()
        {
            Query = query,
            Items = _runtime.Operations.RuntimeEvents.Query(query)
        };

    public IntegrationApprovalsResponse GetApprovals(string? channelId, string? senderId)
        => new()
        {
            ChannelId = string.IsNullOrWhiteSpace(channelId) ? null : channelId,
            SenderId = string.IsNullOrWhiteSpace(senderId) ? null : senderId,
            Items = _runtime.ToolApprovalService.ListPending(channelId, senderId)
        };

    public IntegrationApprovalHistoryResponse GetApprovalHistory(ApprovalHistoryQuery query)
        => new()
        {
            Query = query,
            Items = _runtime.ApprovalAuditStore.Query(query)
        };

    public IntegrationProvidersResponse GetProviders(int recentTurnsLimit)
        => new()
        {
            Routes = _runtime.Operations.LlmExecution.SnapshotRoutes(),
            Usage = _runtime.ProviderUsage.Snapshot(),
            Policies = _runtime.Operations.ProviderPolicies.List(),
            RecentTurns = _runtime.ProviderUsage.RecentTurns(limit: recentTurnsLimit)
        };

    public IntegrationPluginsResponse GetPlugins()
        => new()
        {
            Items = _runtime.Operations.PluginHealth.ListSnapshots()
        };

    public IntegrationOperatorAuditResponse GetOperatorAudit(OperatorAuditQuery query)
        => new()
        {
            Query = query,
            Items = _runtime.Operations.OperatorAudit.Query(query)
        };

    public async Task<IntegrationDashboardResponse> GetDashboardAsync(CancellationToken cancellationToken)
    {
        return new IntegrationDashboardResponse
        {
            Status = BuildStatusResponse(),
            Approvals = GetApprovals(channelId: null, senderId: null),
            ApprovalHistory = GetApprovalHistory(new ApprovalHistoryQuery { Limit = 12 }),
            Providers = GetProviders(recentTurnsLimit: 20),
            Plugins = GetPlugins(),
            Events = QueryRuntimeEvents(new RuntimeEventQuery { Limit = 20 })
        };
    }

    public async Task<IntegrationMessageResponse> QueueMessageAsync(IntegrationMessageRequest request, CancellationToken cancellationToken)
    {
        var effectiveChannelId = string.IsNullOrWhiteSpace(request.ChannelId) ? "integration-api" : request.ChannelId.Trim();
        var effectiveSenderId = string.IsNullOrWhiteSpace(request.SenderId) ? "http-client" : request.SenderId.Trim();
        var effectiveSessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? $"{effectiveChannelId}:{effectiveSenderId}"
            : request.SessionId.Trim();

        await _runtime.RecentSenders.RecordAsync(effectiveChannelId, effectiveSenderId, senderName: null, cancellationToken);

        var message = new InboundMessage
        {
            ChannelId = effectiveChannelId,
            SenderId = effectiveSenderId,
            SessionId = effectiveSessionId,
            Type = "user_message",
            Text = request.Text,
            MessageId = request.MessageId,
            ReplyToMessageId = request.ReplyToMessageId
        };

        if (!_runtime.Pipeline.InboundWriter.TryWrite(message))
            await _runtime.Pipeline.InboundWriter.WriteAsync(message, cancellationToken);

        return new IntegrationMessageResponse
        {
            Accepted = true,
            ChannelId = effectiveChannelId,
            SenderId = effectiveSenderId,
            SessionId = effectiveSessionId,
            MessageId = request.MessageId
        };
    }

    public static SessionListQuery BuildSessionQuery(
        string? search,
        string? channelId,
        string? senderId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string? state,
        bool? starred,
        string? tag)
    {
        return new SessionListQuery
        {
            Search = string.IsNullOrWhiteSpace(search) ? null : search,
            ChannelId = string.IsNullOrWhiteSpace(channelId) ? null : channelId,
            SenderId = string.IsNullOrWhiteSpace(senderId) ? null : senderId,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            State = ParseSessionState(state),
            Starred = starred,
            Tag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim()
        };
    }

    public static SessionState? ParseSessionState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Enum.TryParse<SessionState>(value, ignoreCase: true, out var state)
            ? state
            : null;
    }

    private static bool MatchesSessionQuery(
        Session session,
        SessionListQuery query,
        IReadOnlyDictionary<string, SessionMetadataSnapshot> metadataById)
    {
        if (!string.IsNullOrWhiteSpace(query.ChannelId) &&
            !string.Equals(session.ChannelId, query.ChannelId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.SenderId) &&
            !string.Equals(session.SenderId, query.SenderId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (query.FromUtc is { } fromUtc && session.LastActiveAt < fromUtc)
            return false;

        if (query.ToUtc is { } toUtc && session.LastActiveAt > toUtc)
            return false;

        if (query.State is { } state && session.State != state)
            return false;

        var metadata = metadataById.TryGetValue(session.Id, out var storedMetadata)
            ? storedMetadata
            : new SessionMetadataSnapshot { SessionId = session.Id, Starred = false, Tags = [] };

        if (query.Starred is { } starred && metadata.Starred != starred)
            return false;

        if (!string.IsNullOrWhiteSpace(query.Tag) &&
            !metadata.Tags.Contains(query.Tag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query.Search))
            return true;

        return session.Id.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || session.ChannelId.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || session.SenderId.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || metadata.Tags.Any(tag => tag.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesSummaryQuery(
        SessionSummary summary,
        SessionListQuery query,
        IReadOnlyDictionary<string, SessionMetadataSnapshot> metadataById)
    {
        if (!string.IsNullOrWhiteSpace(query.ChannelId) &&
            !string.Equals(summary.ChannelId, query.ChannelId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.SenderId) &&
            !string.Equals(summary.SenderId, query.SenderId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (query.FromUtc is { } fromUtc && summary.LastActiveAt < fromUtc)
            return false;

        if (query.ToUtc is { } toUtc && summary.LastActiveAt > toUtc)
            return false;

        if (query.State is { } state && summary.State != state)
            return false;

        var metadata = metadataById.TryGetValue(summary.Id, out var storedMetadata)
            ? storedMetadata
            : new SessionMetadataSnapshot { SessionId = summary.Id, Starred = false, Tags = [] };

        if (query.Starred is { } starred && metadata.Starred != starred)
            return false;

        if (!string.IsNullOrWhiteSpace(query.Tag) &&
            !metadata.Tags.Contains(query.Tag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query.Search))
            return true;

        return summary.Id.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || summary.ChannelId.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || summary.SenderId.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || metadata.Tags.Any(tag => tag.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
    }
}
