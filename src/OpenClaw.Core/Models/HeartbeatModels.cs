namespace OpenClaw.Core.Models;

public sealed class HeartbeatConfigDto
{
    public bool Enabled { get; init; }
    public string CronExpression { get; init; } = "@hourly";
    public string? Timezone { get; init; }
    public string DeliveryChannelId { get; init; } = "cron";
    public string? DeliveryRecipientId { get; init; }
    public string? DeliverySubject { get; init; }
    public string? ModelId { get; init; }
    public IReadOnlyList<HeartbeatTaskDto> Tasks { get; init; } = [];
}

public sealed class HeartbeatTaskDto
{
    public string Id { get; init; } = "";
    public string TemplateKey { get; init; } = "custom";
    public string Title { get; init; } = "";
    public string? Target { get; init; }
    public string? Instruction { get; init; }
    public string Priority { get; init; } = "normal";
    public bool Enabled { get; init; } = true;
    public string ConditionMode { get; init; } = "and";
    public IReadOnlyList<HeartbeatConditionDto> Conditions { get; init; } = [];
}

public sealed class HeartbeatConditionDto
{
    public string Field { get; init; } = "";
    public string Operator { get; init; } = "";
    public IReadOnlyList<string> Values { get; init; } = [];
}

public sealed class HeartbeatTemplateDto
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Available { get; init; }
    public string? Reason { get; init; }
}

public sealed class HeartbeatSuggestionDto
{
    public string TemplateKey { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Target { get; init; }
    public string Reason { get; init; } = "";
    public int EvidenceCount { get; init; }
}

public sealed class HeartbeatValidationIssueDto
{
    public string Severity { get; init; } = "error";
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string? TaskId { get; init; }
}

public sealed class HeartbeatCostEstimateDto
{
    public string ProviderId { get; init; } = "";
    public string ModelId { get; init; } = "";
    public int EstimatedSkillPromptChars { get; init; }
    public int EstimatedInputTokensPerRun { get; init; }
    public int EstimatedOkOutputTokensPerRun { get; init; }
    public int EstimatedAlertOutputTokensPerRun { get; init; }
    public int EstimatedRunsPerMonth { get; init; }
    public decimal EstimatedOkCostUsdPerRun { get; init; }
    public decimal EstimatedAlertCostUsdPerRun { get; init; }
    public decimal EstimatedOkCostUsdPerMonth { get; init; }
    public decimal EstimatedAlertCostUsdPerMonth { get; init; }
}

public sealed class HeartbeatRunStatusDto
{
    public string Outcome { get; init; } = "never";
    public DateTimeOffset? LastRunAtUtc { get; init; }
    public DateTimeOffset? LastDeliveredAtUtc { get; init; }
    public bool DeliverySuppressed { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public string? SessionId { get; init; }
    public string? MessagePreview { get; init; }
}

public sealed class HeartbeatPreviewResponse
{
    public required HeartbeatConfigDto Config { get; init; }
    public string ConfigPath { get; init; } = "";
    public string HeartbeatPath { get; init; } = "";
    public string MemoryMarkdownPath { get; init; } = "";
    public string HeartbeatMarkdown { get; init; } = "";
    public string PromptPreview { get; init; } = "";
    public bool DriftDetected { get; init; }
    public bool ManagedJobActive { get; init; }
    public IReadOnlyList<HeartbeatValidationIssueDto> Issues { get; init; } = [];
    public IReadOnlyList<HeartbeatTemplateDto> AvailableTemplates { get; init; } = [];
    public IReadOnlyList<HeartbeatSuggestionDto> Suggestions { get; init; } = [];
    public required HeartbeatCostEstimateDto CostEstimate { get; init; }
}

public sealed class HeartbeatStatusResponse
{
    public required HeartbeatConfigDto Config { get; init; }
    public string ConfigPath { get; init; } = "";
    public string HeartbeatPath { get; init; } = "";
    public string MemoryMarkdownPath { get; init; } = "";
    public bool ConfigExists { get; init; }
    public bool HeartbeatExists { get; init; }
    public bool DriftDetected { get; init; }
    public HeartbeatRunStatusDto? LastRun { get; init; }
    public IReadOnlyList<HeartbeatValidationIssueDto> Issues { get; init; } = [];
    public IReadOnlyList<HeartbeatTemplateDto> AvailableTemplates { get; init; } = [];
    public IReadOnlyList<HeartbeatSuggestionDto> Suggestions { get; init; } = [];
    public required HeartbeatCostEstimateDto CostEstimate { get; init; }
}
