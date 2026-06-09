using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent.Execution;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Security;
using OpenClaw.Core.Skills;

namespace OpenClaw.Agent;

/// <summary>
/// Delegate for interactive tool approval. Returns true to allow, false to deny.
/// </summary>
public delegate ValueTask<bool> ToolApprovalCallback(string toolName, string arguments, CancellationToken ct);

/// <summary>
/// The agent loop: receives a user message, builds context from session history + memory,
/// calls the LLM, executes tool calls, and returns the final response.
/// Uses Microsoft.Extensions.AI for provider-agnostic LLM access (thin, AOT-friendly).
/// Includes retry with exponential backoff, per-call timeout, circuit breaker,
/// streaming, parallel tool execution, context compaction, hooks, and tool approval.
/// </summary>
public sealed class AgentRuntime : IAgentRuntime
{
    private readonly OpenClawToolExecutor _toolExecutor;
    private readonly ILogger? _logger;
    private readonly int _maxTokens;
    private readonly int _maxIterations;
    private readonly float _temperature;
    private readonly int _maxHistoryTurns;
    private readonly bool _enableCompaction;
    private readonly int _compactionThreshold;
    private readonly int _compactionKeepRecent;
    private readonly bool _requireToolApproval;
    private readonly CircuitBreaker _circuitBreaker;
    private readonly ILlmExecutionService? _llmExecutionService;
    private readonly bool _estimateTokenBudgetAdmission;
    private readonly LlmProviderConfig _config;
    private readonly IUserProfileStore? _profileStore;
    private readonly ProfilesConfig? _profilesConfig;
    private readonly SkillsConfig? _skillsConfig;
    private readonly string? _skillWorkspacePath;
    private readonly IReadOnlyList<string> _pluginSkillDirs;
    private readonly IRedactionPipeline _redaction;
    private readonly FractalMemoryConfig? _fractalMemory;
    private readonly AgentPromptContextAssembler _promptContext;
    private readonly AgentCheckpointManager _checkpointManager;
    private readonly AgentTurnAccounting _accounting;
    private readonly AgentModelExecutor _modelExecutor;
    private readonly AgentToolCallLoop _toolCallLoop;

    public AgentRuntime(
        IChatClient chatClient,
        IReadOnlyList<ITool> tools,
        IMemoryStore memory,
        LlmProviderConfig config,
        int maxHistoryTurns,
        IReadOnlyList<SkillDefinition>? skills = null,
        SkillsConfig? skillsConfig = null,
        string? skillWorkspacePath = null,
        IReadOnlyList<string>? pluginSkillDirs = null,
        ILogger? logger = null,
        int toolTimeoutSeconds = 30,
        RuntimeMetrics? metrics = null,
        ProviderUsageTracker? providerUsage = null,
        ILlmExecutionService? llmExecutionService = null,
        bool parallelToolExecution = true,
        bool enableCompaction = false,
        int compactionThreshold = 40,
        int compactionKeepRecent = 10,
        bool requireToolApproval = false,
        string[]? approvalRequiredTools = null,
        int maxIterations = 10,
        IReadOnlyList<IToolHook>? hooks = null,
        long sessionTokenBudget = 0,
        MemoryRecallConfig? recall = null,
        IUserProfileStore? profileStore = null,
        ProfilesConfig? profilesConfig = null,
        IToolSandbox? toolSandbox = null,
        GatewayConfig? gatewayConfig = null,
        ToolUsageTracker? toolUsageTracker = null,
        ToolExecutionRouter? executionRouter = null,
        IToolPresetResolver? toolPresetResolver = null,
        Func<Session, bool>? isContractTokenBudgetExceeded = null,
        Func<Session, bool>? isContractRuntimeBudgetExceeded = null,
        Action<Session, string, string, long, long>? recordContractTurnUsage = null,
        Action<Session, string>? appendContractSnapshot = null,
        ToolAuditLog? toolAuditLog = null,
        IRedactionPipeline? redaction = null,
        ISentinelSubstitutionService? sentinelSubstitution = null,
        IToolGovernanceService? toolGovernance = null,
        IPlanExecuteVerifyOrchestrator? planExecuteVerify = null,
        ContextBudgetPlanner? contextBudgetPlanner = null)
    {
        _logger = logger;
        _config = config;
        _maxTokens = config.MaxTokens;
        _maxIterations = Math.Max(1, maxIterations);
        _temperature = config.Temperature;
        _maxHistoryTurns = Math.Max(1, maxHistoryTurns);
        _enableCompaction = enableCompaction;
        _compactionThreshold = Math.Max(4, compactionThreshold);
        _compactionKeepRecent = Math.Max(2, compactionKeepRecent);
        _requireToolApproval = requireToolApproval;
        var approvalRequiredSet = NormalizeApprovalRequiredTools(approvalRequiredTools);
        var effectiveHooks = hooks ?? [];
        _llmExecutionService = llmExecutionService;
        _skillsConfig = skillsConfig;
        _skillWorkspacePath = skillWorkspacePath;
        _pluginSkillDirs = pluginSkillDirs ?? [];
        _redaction = redaction ?? new NoopRedactionPipeline();
        var effectiveSentinelSubstitution = sentinelSubstitution ?? new NoopSentinelSubstitutionService();
        _circuitBreaker = new CircuitBreaker(
            config.CircuitBreakerThreshold,
            TimeSpan.FromSeconds(config.CircuitBreakerCooldownSeconds),
            logger);

        _toolExecutor = new OpenClawToolExecutor(
            tools,
            toolTimeoutSeconds,
            requireToolApproval,
            [.. approvalRequiredSet],
            effectiveHooks,
            metrics,
            logger,
            config: gatewayConfig,
            toolSandbox: toolSandbox,
            toolUsageTracker: toolUsageTracker,
            executionRouter: executionRouter,
            toolPresetResolver: toolPresetResolver,
            redaction: _redaction,
            sentinelSubstitution: effectiveSentinelSubstitution,
            toolGovernance: toolGovernance,
            planExecuteVerify: planExecuteVerify,
            auditLog: toolAuditLog);
        _estimateTokenBudgetAdmission = gatewayConfig?.EnableEstimatedTokenAdmissionControl ?? false;
        _profileStore = profileStore;
        _profilesConfig = profilesConfig;
        _fractalMemory = gatewayConfig?.Memory.Fractal;
        var projectId = gatewayConfig?.Memory.ProjectId
            ?? Environment.GetEnvironmentVariable("OPENCLAW_PROJECT");
        var memoryRecallPrefix = string.IsNullOrWhiteSpace(projectId) ? null : $"project:{projectId.Trim()}:";
        _promptContext = new AgentPromptContextAssembler(
            memory,
            requireToolApproval,
            recall,
            _profileStore,
            _profilesConfig,
            contextBudgetPlanner,
            _fractalMemory,
            metrics,
            logger,
            memoryRecallPrefix);
        _promptContext.ApplySkills(skills ?? [], _skillsConfig?.InstructionPrompt);
        _checkpointManager = new AgentCheckpointManager(memory, logger);
        _accounting = new AgentTurnAccounting(
            metrics,
            providerUsage,
            config,
            sessionTokenBudget,
            _estimateTokenBudgetAdmission,
            () => _llmExecutionService?.DefaultCircuitState ?? _circuitBreaker.State,
            isContractTokenBudgetExceeded,
            isContractRuntimeBudgetExceeded,
            recordContractTurnUsage,
            appendContractSnapshot,
            logger);
        _modelExecutor = new AgentModelExecutor(
            chatClient,
            config,
            _circuitBreaker,
            llmExecutionService,
            _accounting,
            logger);
        _toolCallLoop = new AgentToolCallLoop(_toolExecutor, parallelToolExecution);
    }

    public IReadOnlyList<string> LoadedSkillNames => _promptContext.LoadedSkillNames;

    public IReadOnlyList<SkillDefinition> LoadedSkills => _promptContext.LoadedSkills;

    public Task<IReadOnlyList<string>> ReloadSkillsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_skillsConfig is null)
            return Task.FromResult<IReadOnlyList<string>>(LoadedSkillNames);

        var logger = _logger ?? NullLogger.Instance;
        var skills = SkillLoader.LoadAll(_skillsConfig, _skillWorkspacePath, logger, _pluginSkillDirs);
        _promptContext.ApplySkills(skills, _skillsConfig.InstructionPrompt);

        if (skills.Count > 0)
            logger.LogInformation("{Summary}", SkillPromptBuilder.BuildSummary(skills));
        else
            logger.LogInformation("No skills loaded.");

        return Task.FromResult<IReadOnlyList<string>>(LoadedSkillNames);
    }

    /// <summary>
    /// Exposes the circuit breaker state for health/metrics endpoints.
    /// </summary>
    public CircuitState CircuitBreakerState => _modelExecutor.CircuitBreakerState;

    /// <summary>
    /// Run the agent loop for a single user turn. Supports multi-step tool use,
    /// parallel tool execution, hooks, and optional tool approval.
    /// </summary>
    public async Task<string> RunAsync(
        Session session, string userMessage, CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Agent.RunAsync");
        activity?.SetTag("session.id", session.Id);
        activity?.SetTag("channel.id", session.ChannelId);

        var turnCtx = new TurnContext
        {
            SessionId = session.Id,
            ChannelId = session.ChannelId
        };
        userMessage = _redaction.Redact(userMessage);

        _accounting.IncrementRequests();
        _logger?.LogInformation("[{CorrelationId}] Turn start session={SessionId} channel={ChannelId}",
            turnCtx.CorrelationId, session.Id, session.ChannelId);

        if (_accounting.TryRejectContractBudget(session, out var contractBudgetMessage))
        {
            _accounting.AppendContractSnapshot(session, "budget_exceeded");
            _accounting.LogTurnComplete(turnCtx);
            return contractBudgetMessage;
        }

        var preparedTurn = await PrepareTurnAsync(
            session,
            userMessage,
            turnCtx,
            responseSchema,
            isStreaming: false,
            ct);
        var messages = preparedTurn.Messages;
        var chatOptions = preparedTurn.ChatOptions;

        for (var i = 0; i < _maxIterations; i++)
        {
            // Mid-turn budget check: stop if token budget is exceeded
            if (_accounting.TryRejectSessionTokenBudget(session, turnCtx, out var sessionBudgetMessage))
            {
                _accounting.LogTurnComplete(turnCtx);
                return sessionBudgetMessage;
            }

            if (_accounting.TryRejectContractBudget(session, out contractBudgetMessage))
            {
                _accounting.AppendContractSnapshot(session, "budget_exceeded");
                _accounting.LogTurnComplete(turnCtx);
                return contractBudgetMessage;
            }

            LlmExecutionResult? executionResult = null;
            var llmSw = Stopwatch.StartNew();
            try
            {
                executionResult = await _modelExecutor.CallLlmWithResilienceAsync(
                    session,
                    messages,
                    chatOptions,
                    turnCtx,
                    _promptContext.SkillPromptLength,
                    ct);
            }
            catch (CircuitOpenException coe)
            {
                _logger?.LogWarning("[{CorrelationId}] Circuit breaker open — retry after {RetryAfter}s",
                    turnCtx.CorrelationId, coe.RetryAfter.TotalSeconds);
                _accounting.LogTurnComplete(turnCtx);
                return coe.Message;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (EstimatedBudgetAdmissionException ex)
            {
                _accounting.LogTurnComplete(turnCtx);
                return ex.Message;
            }
            catch (ModelSelectionException ex)
            {
                _logger?.LogWarning("[{CorrelationId}] Model selection failed: {Message}", turnCtx.CorrelationId, ex.Message);
                _accounting.LogTurnComplete(turnCtx);
                return ex.Message;
            }
            catch (Exception ex)
            {
                _accounting.IncrementLlmErrors();
                _logger?.LogError(ex, "[{CorrelationId}] LLM call failed after all retries and fallbacks", turnCtx.CorrelationId);
                _accounting.LogTurnComplete(turnCtx);
                return "Sorry, I'm having trouble reaching my AI provider right now. Please try again shortly.";
            }
            llmSw.Stop();

            if (executionResult is null)
            {
                _accounting.LogTurnComplete(turnCtx);
                return "Sorry, I'm having trouble reaching my AI provider right now. Please try again shortly.";
            }

            var response = executionResult.Response;

            _accounting.RecordLlmResultUsage(session, turnCtx, llmSw.Elapsed, messages, executionResult, _promptContext.SkillPromptLength);

            if (_accounting.TryRejectContractBudget(session, out contractBudgetMessage))
            {
                _accounting.AppendContractSnapshot(session, "budget_exceeded");
                _accounting.LogTurnComplete(turnCtx);
                return contractBudgetMessage;
            }

            // Check for tool calls
            var toolCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            if (toolCalls.Count == 0)
            {
                // Final text response
                var text = _redaction.Redact(response.Text ?? "");
                session.History.Add(new ChatTurn { Role = "assistant", Content = text });
                AgentCheckpointManager.MarkCheckpointCompleted(session, SessionCheckpointStates.Completed, "final_response");
                _accounting.AppendContractSnapshot(session, "active");
                _accounting.LogTurnComplete(turnCtx);
                return text;
            }

            // Execute tool calls (parallel or sequential based on config)
            var toolBatch = await _toolCallLoop.ExecuteToolCallsAsync(
                toolCalls, session, turnCtx, isStreaming: false, approvalCallback, ct);
            var invocations = toolBatch.Invocations;
            var toolResults = toolBatch.Results;

            // Feed all tool calls as a single assistant message, then all results as a single tool message
            messages.Add(new ChatMessage(ChatRole.Assistant, toolCalls.Cast<AIContent>().ToList()));
            messages.Add(new ChatMessage(ChatRole.Tool, toolResults.Cast<AIContent>().ToList()));

            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "[tool_use]",
                ToolCalls = invocations
            });

            // Compaction is NOT run inside the iteration loop to avoid cascading LLM calls.
            // It runs once at the start of the turn (before the loop).
            _promptContext.TrimHistory(session, _maxHistoryTurns);
            await _checkpointManager.PersistToolBatchCheckpointAsync(session, turnCtx, i, invocations, ct);
        }

        AgentCheckpointManager.MarkCheckpointCompleted(session, SessionCheckpointStates.Failed, "max_iterations");
        _accounting.AppendContractSnapshot(session, "active");
        _accounting.LogTurnComplete(turnCtx);
        return "I've reached the maximum number of tool iterations. Please try a simpler request.";
    }

    /// <summary>
    /// Run the agent loop with streaming. Yields incremental events (text deltas, tool status)
    /// for real-time delivery to WebSocket clients.
    /// </summary>
    public async IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        Session session, string userMessage,
        [EnumeratorCancellation] CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Agent.RunStreamingAsync");
        activity?.SetTag("session.id", session.Id);
        activity?.SetTag("channel.id", session.ChannelId);

        var turnCtx = new TurnContext
        {
            SessionId = session.Id,
            ChannelId = session.ChannelId
        };
        userMessage = _redaction.Redact(userMessage);

        _accounting.IncrementRequests();
        _logger?.LogInformation("[{CorrelationId}] Streaming turn start session={SessionId} channel={ChannelId}",
            turnCtx.CorrelationId, session.Id, session.ChannelId);

        if (_accounting.TryRejectContractBudget(session, out var contractBudgetMessage))
        {
            yield return AgentStreamEvent.ErrorOccurred(contractBudgetMessage, "contract_budget_exceeded");
            yield return AgentStreamEvent.Complete();
            _accounting.AppendContractSnapshot(session, "budget_exceeded");
            _accounting.LogTurnComplete(turnCtx);
            yield break;
        }

        if (_requireToolApproval && approvalCallback is null)
        {
            _logger?.LogWarning(
                "[{CorrelationId}] Streaming session has RequireToolApproval=true but no approval callback is registered — protected tools will be auto-denied. " +
                "Connect through /chat for interactive approvals, or set OpenClaw:Tooling:RequireToolApproval=false for trusted local sessions.",
                turnCtx.CorrelationId);
        }

        var preparedTurn = await PrepareTurnAsync(
            session,
            userMessage,
            turnCtx,
            responseSchema: null,
            isStreaming: true,
            ct);
        var messages = preparedTurn.Messages;
        var chatOptions = preparedTurn.ChatOptions;

        for (var i = 0; i < _maxIterations; i++)
        {
            // Mid-turn budget check: stop if token budget is exceeded
            if (_accounting.TryRejectSessionTokenBudget(session, turnCtx, out var sessionBudgetMessage))
            {
                yield return AgentStreamEvent.ErrorOccurred(
                    sessionBudgetMessage,
                    "session_token_limit");
                yield return AgentStreamEvent.Complete();
                _accounting.LogTurnComplete(turnCtx);
                yield break;
            }

            if (_accounting.TryRejectContractBudget(session, out contractBudgetMessage))
            {
                yield return AgentStreamEvent.ErrorOccurred(contractBudgetMessage, "contract_budget_exceeded");
                yield return AgentStreamEvent.Complete();
                _accounting.AppendContractSnapshot(session, "budget_exceeded");
                _accounting.LogTurnComplete(turnCtx);
                yield break;
            }

            // Stream the LLM response, collecting chunks and tool calls.
            // We buffer events because C# doesn't allow yield in try/catch.
            var streamResult = await _modelExecutor.StreamLlmCollectAsync(
                session,
                messages,
                chatOptions,
                turnCtx,
                _promptContext.SkillPromptLength,
                ct);

            // Redact the complete buffered text so secrets split across provider chunks cannot leak.
            var fullText = streamResult.FullText;
            var redactedText = _redaction.Redact(fullText);
            if (string.Equals(redactedText, fullText, StringComparison.Ordinal))
            {
                foreach (var delta in streamResult.TextDeltas)
                    yield return AgentStreamEvent.TextDelta(delta);
            }
            else if (!string.IsNullOrEmpty(redactedText))
            {
                yield return AgentStreamEvent.TextDelta(redactedText);
            }

            // If streaming failed, yield error and stop
            if (streamResult.Error is not null)
            {
                yield return AgentStreamEvent.ErrorOccurred(streamResult.Error, "provider_failure");
                yield return AgentStreamEvent.Complete();
                _accounting.LogTurnComplete(turnCtx);
                yield break;
            }

            _accounting.RecordStreamingTurnUsage(session, turnCtx, messages, streamResult, _promptContext.SkillPromptLength);

            if (_accounting.TryRejectContractBudget(session, out contractBudgetMessage))
            {
                yield return AgentStreamEvent.ErrorOccurred(contractBudgetMessage, "contract_budget_exceeded");
                yield return AgentStreamEvent.Complete();
                _accounting.AppendContractSnapshot(session, "budget_exceeded");
                _accounting.LogTurnComplete(turnCtx);
                yield break;
            }

            var toolCalls = streamResult.ToolCalls;

            if (toolCalls.Count == 0)
            {
                // Final text response
                session.History.Add(new ChatTurn { Role = "assistant", Content = redactedText });
                AgentCheckpointManager.MarkCheckpointCompleted(session, SessionCheckpointStates.Completed, "final_response");
                yield return AgentStreamEvent.Complete();
                _accounting.AppendContractSnapshot(session, "active");
                _accounting.LogTurnComplete(turnCtx);
                yield break;
            }

            AgentToolBatchExecution? toolBatch = null;
            await foreach (var update in _toolCallLoop.ExecuteStreamingToolCallsAsync(
                toolCalls,
                session,
                turnCtx,
                approvalCallback,
                ct))
            {
                if (update.StreamEvent is not null)
                    yield return update.StreamEvent.Value;
                if (update.Batch is not null)
                    toolBatch = update.Batch;
            }

            if (toolBatch is null)
                throw new InvalidOperationException(
                    $"Streaming tool call loop completed without final batch for session={session.Id} correlation={turnCtx.CorrelationId}.");

            var invocations = toolBatch.Invocations;
            var toolResults = toolBatch.Results;

            messages.Add(new ChatMessage(ChatRole.Assistant, toolCalls.Cast<AIContent>().ToList()));
            messages.Add(new ChatMessage(ChatRole.Tool, toolResults.Cast<AIContent>().ToList()));

            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "[tool_use]",
                ToolCalls = invocations
            });

            // Compaction is NOT run inside the iteration loop to avoid cascading LLM calls.
            _promptContext.TrimHistory(session, _maxHistoryTurns);
            await _checkpointManager.PersistToolBatchCheckpointAsync(session, turnCtx, i, invocations, ct);
        }

        yield return AgentStreamEvent.ErrorOccurred(
            "I've reached the maximum number of tool iterations. Please try a simpler request.",
            "max_iterations");
        yield return AgentStreamEvent.Complete();
        AgentCheckpointManager.MarkCheckpointCompleted(session, SessionCheckpointStates.Failed, "max_iterations");
        _accounting.AppendContractSnapshot(session, "active");
        _accounting.LogTurnComplete(turnCtx);
    }

    private async Task<AgentPreparedTurn> PrepareTurnAsync(
        Session session,
        string userMessage,
        TurnContext turnCtx,
        JsonElement? responseSchema,
        bool isStreaming,
        CancellationToken ct)
    {
        var resumeCheckpoint = AgentCheckpointManager.TryGetResumableCheckpoint(session);
        if (resumeCheckpoint is null)
        {
            session.History.Add(new ChatTurn { Role = "user", Content = userMessage });

            if (_enableCompaction)
                await CompactHistoryAsync(session, ct);
            else
                _promptContext.TrimHistory(session, _maxHistoryTurns);
        }
        else
        {
            resumeCheckpoint.LastResumeAttemptAtUtc = DateTimeOffset.UtcNow;
            _logger?.LogInformation(
                isStreaming
                    ? "[{CorrelationId}] Resuming streaming session={SessionId} from checkpoint {CheckpointId}"
                    : "[{CorrelationId}] Resuming session={SessionId} from checkpoint {CheckpointId}",
                turnCtx.CorrelationId,
                session.Id,
                resumeCheckpoint.CheckpointId);
        }

        var messages = _promptContext.BuildMessages(session, _maxHistoryTurns, exactLatestToolBatch: resumeCheckpoint is not null);
        if (resumeCheckpoint is not null)
        {
            messages.Insert(1, new ChatMessage(ChatRole.System, AgentCheckpointManager.BuildCheckpointResumeInstruction(resumeCheckpoint)));
            if (!AgentCheckpointManager.IsBareResumeRequest(userMessage))
                messages.Add(new ChatMessage(ChatRole.User, AgentCheckpointManager.BuildCheckpointResumeUserNote(userMessage)));
        }
        else
        {
            // Keep the existing insertion arithmetic stable across the three recall injectors.
            var memoryRecallInjected = await _promptContext.TryInjectRecallAsync(messages, userMessage, ct);
            await _promptContext.TryInjectStructuredMemoryContextAsync(messages, session, userMessage, memoryRecallInjected, ct);
            await _promptContext.TryInjectProfileRecallAsync(messages, session, ct);
        }

        var chatOptions = new ChatOptions
        {
            ModelId = session.ModelOverride ?? _config.Model,
            MaxOutputTokens = _maxTokens,
            Temperature = _temperature,
            Tools = _toolExecutor.GetToolDeclarations(session),
            ResponseFormat = responseSchema.HasValue
                ? ChatResponseFormat.ForJsonSchema(responseSchema.Value, "response")
                : null
        };

        if (!string.IsNullOrWhiteSpace(session.ReasoningEffort))
        {
            chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            chatOptions.AdditionalProperties["reasoning_effort"] = session.ReasoningEffort;
        }

        return new AgentPreparedTurn(messages, chatOptions, resumeCheckpoint);
    }

    /// <summary>
    /// Compacts session history by summarizing older turns via the LLM.
    /// Keeps the most recent turns verbatim and replaces older ones with a summary.
    /// </summary>
    public async Task CompactHistoryAsync(Session session, CancellationToken ct)
    {
        if (session.History.Count <= _compactionThreshold)
        {
            // Below threshold — just apply simple trim as fallback
            _promptContext.TrimHistory(session, _maxHistoryTurns);
            return;
        }

        var keepCount = Math.Min(_compactionKeepRecent, session.History.Count - 2);
        var toSummarizeCount = session.History.Count - keepCount;

        if (toSummarizeCount < 4)
        {
            _promptContext.TrimHistory(session, _maxHistoryTurns);
            return;
        }

        // Check if we already have a compaction summary as the first turn
        if (session.History.Count > 0 &&
            session.History[0].Role == "system" &&
            session.History[0].Content.StartsWith("[Previous conversation summary:", StringComparison.Ordinal))
        {
            // Previous summary will be included in what gets re-summarized
        }

        var turnsToSummarize = session.History.GetRange(0, toSummarizeCount);
        var conversationText = new StringBuilder();
        foreach (var turn in turnsToSummarize)
        {
            if (turn.Content == "[tool_use]" && turn.ToolCalls is { Count: > 0 })
            {
                foreach (var tc in turn.ToolCalls)
                    conversationText.AppendLine($"assistant: [called {tc.ToolName}] → {AgentPromptContextAssembler.Truncate(tc.Result ?? "", 200)}");
            }
            else
            {
                conversationText.AppendLine($"{turn.Role}: {AgentPromptContextAssembler.Truncate(turn.Content, 500)}");
            }
        }

        try
        {
            var summaryMessages = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "Summarize the following conversation turns into a concise context summary (2-3 sentences). " +
                    "Focus on key decisions, facts established, and pending tasks. Output ONLY the summary."),
                new(ChatRole.User, conversationText.ToString())
            };

            var summaryOptions = new ChatOptions { MaxOutputTokens = 256, Temperature = 0.3f };
            var compactionTurnCtx = new TurnContext
            {
                SessionId = session.Id,
                ChannelId = session.ChannelId
            };

            var summarySw = Stopwatch.StartNew();
            var response = await _modelExecutor.CallLlmWithResilienceAsync(
                session,
                summaryMessages,
                summaryOptions,
                compactionTurnCtx,
                _promptContext.SkillPromptLength,
                ct);
            summarySw.Stop();

            var summaryInputTokens = response.Response.Usage?.InputTokenCount ?? 0;
            var summaryOutputTokens = response.Response.Usage?.OutputTokenCount ?? 0;
            _accounting.RecordCompactionUsage(
                session,
                compactionTurnCtx,
                summarySw.Elapsed,
                summaryMessages,
                response,
                summaryInputTokens,
                summaryOutputTokens,
                _promptContext.SkillPromptLength);

            var summary = response.Response.Text ?? "";

            if (!string.IsNullOrWhiteSpace(summary))
            {
                _accounting.IncrementMemoryCompactions();
                session.History.RemoveRange(0, toSummarizeCount);
                session.History.Insert(0, new ChatTurn
                {
                    Role = "system",
                    Content = $"[Previous conversation summary: {summary}]"
                });
                _logger?.LogDebug("Compacted {Count} history turns into summary", toSummarizeCount);
            }
            else
            {
                // Summarization returned empty — fall back to simple trim
                _promptContext.TrimHistory(session, _maxHistoryTurns);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "History compaction failed — falling back to simple trim");
            _promptContext.TrimHistory(session, _maxHistoryTurns);
        }
    }

    private static HashSet<string> NormalizeApprovalRequiredTools(string[]? configuredTools)
    {
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        var tools = configuredTools is { Length: > 0 } ? configuredTools : ["shell", "write_file"];

        foreach (var toolName in tools)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                continue;

            normalized.Add(NormalizeApprovalToolName(toolName.Trim()));
        }

        return normalized;
    }

    private static string NormalizeApprovalToolName(string toolName) =>
        string.Equals(toolName, "file_write", StringComparison.Ordinal)
            ? "write_file"
            : toolName;


}
