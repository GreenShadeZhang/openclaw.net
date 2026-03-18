namespace OpenClaw.Core.Models;

public sealed class IntegrationStatusResponse
{
    public required HealthResponse Health { get; init; }
    public required GatewayRuntimeState Runtime { get; init; }
    public required OpenClaw.Core.Observability.MetricsSnapshot Metrics { get; init; }
    public int ActiveSessions { get; init; }
    public int PendingApprovals { get; init; }
    public int ActiveApprovalGrants { get; init; }
}

public sealed class IntegrationSessionsResponse
{
    public required SessionListQuery Filters { get; init; }
    public IReadOnlyList<SessionSummary> Active { get; init; } = [];
    public required PagedSessionList Persisted { get; init; }
}

public sealed class IntegrationSessionDetailResponse
{
    public Session? Session { get; init; }
    public bool IsActive { get; init; }
    public int BranchCount { get; init; }
    public SessionMetadataSnapshot? Metadata { get; init; }
}

public sealed class IntegrationMessageRequest
{
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public string? SessionId { get; init; }
    public required string Text { get; init; }
    public string? MessageId { get; init; }
    public string? ReplyToMessageId { get; init; }
}

public sealed class IntegrationMessageResponse
{
    public bool Accepted { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string SessionId { get; init; }
    public string? MessageId { get; init; }
}

public sealed class IntegrationRuntimeEventsResponse
{
    public required RuntimeEventQuery Query { get; init; }
    public IReadOnlyList<RuntimeEventEntry> Items { get; init; } = [];
}

public sealed class IntegrationApprovalsResponse
{
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public IReadOnlyList<OpenClaw.Core.Pipeline.ToolApprovalRequest> Items { get; init; } = [];
}

public sealed class IntegrationApprovalHistoryResponse
{
    public required ApprovalHistoryQuery Query { get; init; }
    public IReadOnlyList<ApprovalHistoryEntry> Items { get; init; } = [];
}

public sealed class IntegrationProvidersResponse
{
    public IReadOnlyList<ProviderRouteHealthSnapshot> Routes { get; init; } = [];
    public IReadOnlyList<OpenClaw.Core.Observability.ProviderUsageSnapshot> Usage { get; init; } = [];
    public IReadOnlyList<ProviderPolicyRule> Policies { get; init; } = [];
    public IReadOnlyList<ProviderTurnUsageEntry> RecentTurns { get; init; } = [];
}

public sealed class IntegrationPluginsResponse
{
    public IReadOnlyList<PluginHealthSnapshot> Items { get; init; } = [];
}

public sealed class IntegrationOperatorAuditResponse
{
    public required OperatorAuditQuery Query { get; init; }
    public IReadOnlyList<OperatorAuditEntry> Items { get; init; } = [];
}

public sealed class IntegrationSessionTimelineResponse
{
    public required string SessionId { get; init; }
    public IReadOnlyList<RuntimeEventEntry> Events { get; init; } = [];
    public IReadOnlyList<ProviderTurnUsageEntry> ProviderTurns { get; init; } = [];
}

public sealed class IntegrationDashboardResponse
{
    public required IntegrationStatusResponse Status { get; init; }
    public required IntegrationApprovalsResponse Approvals { get; init; }
    public required IntegrationApprovalHistoryResponse ApprovalHistory { get; init; }
    public required IntegrationProvidersResponse Providers { get; init; }
    public required IntegrationPluginsResponse Plugins { get; init; }
    public required IntegrationRuntimeEventsResponse Events { get; init; }
}
