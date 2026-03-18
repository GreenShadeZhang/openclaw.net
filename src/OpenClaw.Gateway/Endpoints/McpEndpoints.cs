using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;

namespace OpenClaw.Gateway.Endpoints;

internal static class McpEndpoints
{
    public static void MapOpenClawMcpEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        var sessionAdminStore = (ISessionAdminStore)app.Services.GetRequiredService<IMemoryStore>();
        var facade = new IntegrationApiFacade(startup, runtime, sessionAdminStore);

        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            if (!EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind))
                return await CreateErrorResponseAsync(statusCode: StatusCodes.Status401Unauthorized, id: null, code: -32001, message: "Unauthorized.");

            if (!runtime.Operations.ActorRateLimits.TryConsume("ip", EndpointHelpers.GetRemoteIpKey(ctx), "mcp_http", out var blockedByPolicyId))
            {
                return await CreateErrorResponseAsync(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    id: null,
                    code: -32029,
                    message: $"Rate limit exceeded by policy '{blockedByPolicyId}'.");
            }

            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            }
            catch
            {
                return await CreateErrorResponseAsync(statusCode: StatusCodes.Status400BadRequest, id: null, code: -32700, message: "Invalid JSON.");
            }

            using (document)
            {
                var root = document.RootElement;
                var id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : (JsonElement?)null;
                if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
                    return await CreateErrorResponseAsync(StatusCodes.Status400BadRequest, id, -32600, "method is required.");

                var method = methodElement.GetString();
                var @params = root.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : default;

                try
                {
                    return method switch
                    {
                        "initialize" => await CreateResultResponseAsync(
                            id,
                            new McpInitializeResult
                            {
                                Capabilities = new McpCapabilities
                                {
                                    Tools = new McpToolCapabilities { ListChanged = false },
                                    Resources = new McpResourceCapabilities { ListChanged = false, SupportsTemplates = true },
                                    Prompts = new McpPromptCapabilities { ListChanged = false }
                                },
                                ServerInfo = new McpServerInfo
                                {
                                    Name = "OpenClaw Gateway MCP",
                                    Version = "1.0.0"
                                }
                            },
                            CoreJsonContext.Default.McpInitializeResult),
                        "tools/list" => await CreateResultResponseAsync(id, BuildToolList(), CoreJsonContext.Default.McpToolListResult),
                        "tools/call" => await HandleToolCallAsync(id, paramsElement, facade, ctx.RequestAborted),
                        "resources/list" => await CreateResultResponseAsync(id, BuildResourceList(), CoreJsonContext.Default.McpResourceListResult),
                        "resources/templates/list" => await CreateResultResponseAsync(id, BuildResourceTemplateList(), CoreJsonContext.Default.McpResourceTemplateListResult),
                        "resources/read" => await HandleReadResourceAsync(id, paramsElement, facade, ctx.RequestAborted),
                        "prompts/list" => await CreateResultResponseAsync(id, BuildPromptList(), CoreJsonContext.Default.McpPromptListResult),
                        "prompts/get" => await HandleGetPromptAsync(id, paramsElement),
                        _ => await CreateErrorResponseAsync(StatusCodes.Status400BadRequest, id, -32601, $"Method '{method}' is not supported.")
                    };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return await CreateErrorResponseAsync(StatusCodes.Status500InternalServerError, id, -32603, ex.Message);
                }
            }
        }).WithTags("OpenClaw MCP");
    }

    private static async Task<IResult> HandleToolCallAsync(
        JsonElement? id,
        JsonElement paramsElement,
        IntegrationApiFacade facade,
        CancellationToken cancellationToken)
    {
        var request = paramsElement.ValueKind is JsonValueKind.Object
            ? paramsElement.Deserialize(CoreJsonContext.Default.McpCallToolRequest)
            : null;
        if (request is null)
            return await CreateErrorResponseAsync(StatusCodes.Status400BadRequest, id, -32602, "Tool call params are required.");

        var result = request.Name switch
        {
            "openclaw.get_dashboard" => CreateToolResult(JsonSerializer.Serialize(
                await facade.GetDashboardAsync(cancellationToken),
                CoreJsonContext.Default.IntegrationDashboardResponse)),
            "openclaw.get_status" => CreateToolResult(JsonSerializer.Serialize(facade.BuildStatusResponse(), CoreJsonContext.Default.IntegrationStatusResponse)),
            "openclaw.list_approvals" => CreateToolResult(JsonSerializer.Serialize(
                facade.GetApprovals(
                    GetStringArgument(request.Arguments, "channelId"),
                    GetStringArgument(request.Arguments, "senderId")),
                CoreJsonContext.Default.IntegrationApprovalsResponse)),
            "openclaw.get_approval_history" => CreateToolResult(JsonSerializer.Serialize(
                facade.GetApprovalHistory(new ApprovalHistoryQuery
                {
                    Limit = GetIntArgument(request.Arguments, "limit", 50),
                    ChannelId = GetStringArgument(request.Arguments, "channelId"),
                    SenderId = GetStringArgument(request.Arguments, "senderId"),
                    ToolName = GetStringArgument(request.Arguments, "toolName")
                }),
                CoreJsonContext.Default.IntegrationApprovalHistoryResponse)),
            "openclaw.get_providers" => CreateToolResult(JsonSerializer.Serialize(
                facade.GetProviders(GetIntArgument(request.Arguments, "recentTurnsLimit", 20)),
                CoreJsonContext.Default.IntegrationProvidersResponse)),
            "openclaw.get_plugins" => CreateToolResult(JsonSerializer.Serialize(
                facade.GetPlugins(),
                CoreJsonContext.Default.IntegrationPluginsResponse)),
            "openclaw.query_operator_audit" => CreateToolResult(JsonSerializer.Serialize(
                facade.GetOperatorAudit(new OperatorAuditQuery
                {
                    Limit = GetIntArgument(request.Arguments, "limit", 50),
                    ActorId = GetStringArgument(request.Arguments, "actorId"),
                    ActionType = GetStringArgument(request.Arguments, "actionType"),
                    TargetId = GetStringArgument(request.Arguments, "targetId")
                }),
                CoreJsonContext.Default.IntegrationOperatorAuditResponse)),
            "openclaw.list_sessions" => CreateToolResult(JsonSerializer.Serialize(
                await facade.ListSessionsAsync(
                    GetIntArgument(request.Arguments, "page", 1),
                    GetIntArgument(request.Arguments, "pageSize", 25),
                    IntegrationApiFacade.BuildSessionQuery(
                        GetStringArgument(request.Arguments, "search"),
                        GetStringArgument(request.Arguments, "channelId"),
                        GetStringArgument(request.Arguments, "senderId"),
                        GetDateTimeOffsetArgument(request.Arguments, "fromUtc"),
                        GetDateTimeOffsetArgument(request.Arguments, "toUtc"),
                        GetStringArgument(request.Arguments, "state"),
                        GetNullableBoolArgument(request.Arguments, "starred"),
                        GetStringArgument(request.Arguments, "tag")),
                    cancellationToken),
                CoreJsonContext.Default.IntegrationSessionsResponse)),
            "openclaw.get_session" => await BuildGetSessionToolResultAsync(request.Arguments, facade, cancellationToken),
            "openclaw.get_session_timeline" => await BuildGetTimelineToolResultAsync(request.Arguments, facade, cancellationToken),
            "openclaw.query_runtime_events" => CreateToolResult(JsonSerializer.Serialize(
                facade.QueryRuntimeEvents(new RuntimeEventQuery
                {
                    Limit = GetIntArgument(request.Arguments, "limit", 100),
                    SessionId = GetStringArgument(request.Arguments, "sessionId"),
                    ChannelId = GetStringArgument(request.Arguments, "channelId"),
                    SenderId = GetStringArgument(request.Arguments, "senderId"),
                    Component = GetStringArgument(request.Arguments, "component"),
                    Action = GetStringArgument(request.Arguments, "action")
                }),
                CoreJsonContext.Default.IntegrationRuntimeEventsResponse)),
            "openclaw.send_message" => CreateToolResult(JsonSerializer.Serialize(
                await facade.QueueMessageAsync(new IntegrationMessageRequest
                {
                    ChannelId = GetStringArgument(request.Arguments, "channelId"),
                    SenderId = GetStringArgument(request.Arguments, "senderId"),
                    SessionId = GetStringArgument(request.Arguments, "sessionId"),
                    Text = GetStringArgument(request.Arguments, "text") ?? string.Empty,
                    MessageId = GetStringArgument(request.Arguments, "messageId"),
                    ReplyToMessageId = GetStringArgument(request.Arguments, "replyToMessageId")
                }, cancellationToken),
                CoreJsonContext.Default.IntegrationMessageResponse)),
            _ => new McpCallToolResult
            {
                IsError = true,
                Content = [new McpTextContent { Text = $"Unsupported tool '{request.Name}'." }]
            }
        };

        return await CreateResultResponseAsync(id, result, CoreJsonContext.Default.McpCallToolResult);
    }

    private static async Task<McpCallToolResult> BuildGetSessionToolResultAsync(JsonElement arguments, IntegrationApiFacade facade, CancellationToken cancellationToken)
    {
        var sessionId = GetStringArgument(arguments, "sessionId");
        if (string.IsNullOrWhiteSpace(sessionId))
            return new McpCallToolResult { IsError = true, Content = [new McpTextContent { Text = "sessionId is required." }] };

        var session = await facade.GetSessionAsync(sessionId, cancellationToken);
        return session is null
            ? new McpCallToolResult { IsError = true, Content = [new McpTextContent { Text = $"Session '{sessionId}' was not found." }] }
            : CreateToolResult(JsonSerializer.Serialize(session, CoreJsonContext.Default.IntegrationSessionDetailResponse));
    }

    private static async Task<McpCallToolResult> BuildGetTimelineToolResultAsync(JsonElement arguments, IntegrationApiFacade facade, CancellationToken cancellationToken)
    {
        var sessionId = GetStringArgument(arguments, "sessionId");
        if (string.IsNullOrWhiteSpace(sessionId))
            return new McpCallToolResult { IsError = true, Content = [new McpTextContent { Text = "sessionId is required." }] };

        var timeline = await facade.GetSessionTimelineAsync(sessionId, GetIntArgument(arguments, "limit", 100), cancellationToken);
        return timeline is null
            ? new McpCallToolResult { IsError = true, Content = [new McpTextContent { Text = $"Session '{sessionId}' was not found." }] }
            : CreateToolResult(JsonSerializer.Serialize(timeline, CoreJsonContext.Default.IntegrationSessionTimelineResponse));
    }

    private static async Task<IResult> HandleReadResourceAsync(
        JsonElement? id,
        JsonElement paramsElement,
        IntegrationApiFacade facade,
        CancellationToken cancellationToken)
    {
        var request = paramsElement.ValueKind is JsonValueKind.Object
            ? paramsElement.Deserialize(CoreJsonContext.Default.McpReadResourceRequest)
            : null;
        if (request is null || string.IsNullOrWhiteSpace(request.Uri))
            return await CreateErrorResponseAsync(StatusCodes.Status400BadRequest, id, -32602, "Resource uri is required.");

        string text;
        if (TryGetSessionResourceUri(request.Uri, out var sessionId))
        {
            var session = await facade.GetSessionAsync(sessionId, cancellationToken);
            if (session is null)
                return await CreateErrorResponseAsync(StatusCodes.Status404NotFound, id, -32004, $"Session '{sessionId}' was not found.");

            text = JsonSerializer.Serialize(session, CoreJsonContext.Default.IntegrationSessionDetailResponse);
        }
        else if (TryGetSessionTimelineResourceUri(request.Uri, out var timelineSessionId))
        {
            var timeline = await facade.GetSessionTimelineAsync(timelineSessionId, limit: 100, cancellationToken);
            if (timeline is null)
                return await CreateErrorResponseAsync(StatusCodes.Status404NotFound, id, -32004, $"Session '{timelineSessionId}' was not found.");

            text = JsonSerializer.Serialize(timeline, CoreJsonContext.Default.IntegrationSessionTimelineResponse);
        }
        else
        {
            switch (request.Uri)
            {
                case "openclaw://status":
                    text = JsonSerializer.Serialize(facade.BuildStatusResponse(), CoreJsonContext.Default.IntegrationStatusResponse);
                    break;
                case "openclaw://dashboard":
                    text = JsonSerializer.Serialize(await facade.GetDashboardAsync(cancellationToken), CoreJsonContext.Default.IntegrationDashboardResponse);
                    break;
                case "openclaw://approvals":
                    text = JsonSerializer.Serialize(facade.GetApprovals(channelId: null, senderId: null), CoreJsonContext.Default.IntegrationApprovalsResponse);
                    break;
                case "openclaw://approvals/history":
                    text = JsonSerializer.Serialize(facade.GetApprovalHistory(new ApprovalHistoryQuery { Limit = 20 }), CoreJsonContext.Default.IntegrationApprovalHistoryResponse);
                    break;
                case "openclaw://providers":
                    text = JsonSerializer.Serialize(facade.GetProviders(recentTurnsLimit: 20), CoreJsonContext.Default.IntegrationProvidersResponse);
                    break;
                case "openclaw://plugins":
                    text = JsonSerializer.Serialize(facade.GetPlugins(), CoreJsonContext.Default.IntegrationPluginsResponse);
                    break;
                case "openclaw://operator-audit":
                    text = JsonSerializer.Serialize(facade.GetOperatorAudit(new OperatorAuditQuery { Limit = 20 }), CoreJsonContext.Default.IntegrationOperatorAuditResponse);
                    break;
                default:
                    return await CreateErrorResponseAsync(StatusCodes.Status404NotFound, id, -32004, $"Resource '{request.Uri}' was not found.");
            }
        }

        var result = new McpReadResourceResult
        {
            Contents = [new McpResourceTextContents { Uri = request.Uri, Text = text }]
        };

        return await CreateResultResponseAsync(id, result, CoreJsonContext.Default.McpReadResourceResult);
    }

    private static async Task<IResult> HandleGetPromptAsync(JsonElement? id, JsonElement paramsElement)
    {
        var request = paramsElement.ValueKind is JsonValueKind.Object
            ? paramsElement.Deserialize(CoreJsonContext.Default.McpGetPromptRequest)
            : null;
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return await CreateErrorResponseAsync(StatusCodes.Status400BadRequest, id, -32602, "Prompt name is required.");

        var arguments = request.Arguments ?? new Dictionary<string, string>(StringComparer.Ordinal);
        McpGetPromptResult result;
        switch (request.Name)
        {
            case "openclaw_operator_summary":
                result = BuildOperatorSummaryPrompt(arguments);
                break;
            case "openclaw_session_summary":
                if (!arguments.TryGetValue("sessionId", out var sessionId) || string.IsNullOrWhiteSpace(sessionId))
                    return await CreateErrorResponseAsync(StatusCodes.Status400BadRequest, id, -32602, "sessionId is required for openclaw_session_summary.");

                result = BuildSessionSummaryPrompt(sessionId);
                break;
            default:
                return await CreateErrorResponseAsync(StatusCodes.Status400BadRequest, id, -32601, $"Prompt '{request.Name}' is not supported.");
        }

        return await CreateResultResponseAsync(id, result, CoreJsonContext.Default.McpGetPromptResult);
    }

    private static McpToolListResult BuildToolList()
        => new()
        {
            Tools =
            [
                new McpToolDefinition { Name = "openclaw.get_dashboard", Description = "Get the aggregated operator dashboard snapshot.", InputSchema = ParseSchema("{}") },
                new McpToolDefinition { Name = "openclaw.get_status", Description = "Get the current OpenClaw gateway runtime status.", InputSchema = ParseSchema("{}") },
                new McpToolDefinition { Name = "openclaw.list_approvals", Description = "List pending tool approvals with optional channel or sender filters.", InputSchema = ParseSchema("{\"type\":\"object\",\"properties\":{\"channelId\":{\"type\":\"string\"},\"senderId\":{\"type\":\"string\"}},\"additionalProperties\":false}") },
                new McpToolDefinition { Name = "openclaw.get_approval_history", Description = "Get recent approval history entries.", InputSchema = ParseSchema("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\"},\"channelId\":{\"type\":\"string\"},\"senderId\":{\"type\":\"string\"},\"toolName\":{\"type\":\"string\"}},\"additionalProperties\":false}") },
                new McpToolDefinition { Name = "openclaw.get_providers", Description = "Get provider routing, usage, policies, and recent turns.", InputSchema = ParseSchema("{\"type\":\"object\",\"properties\":{\"recentTurnsLimit\":{\"type\":\"integer\"}},\"additionalProperties\":false}") },
                new McpToolDefinition { Name = "openclaw.get_plugins", Description = "Get the current plugin health listing.", InputSchema = ParseSchema("{}") },
                new McpToolDefinition { Name = "openclaw.query_operator_audit", Description = "Query recent operator audit entries.", InputSchema = ParseSchema("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\"},\"actorId\":{\"type\":\"string\"},\"actionType\":{\"type\":\"string\"},\"targetId\":{\"type\":\"string\"}},\"additionalProperties\":false}") },
                new McpToolDefinition { Name = "openclaw.list_sessions", Description = "List OpenClaw sessions with optional filters.", InputSchema = ParseSchema("{\"type\":\"object\",\"properties\":{\"page\":{\"type\":\"integer\"},\"pageSize\":{\"type\":\"integer\"},\"search\":{\"type\":\"string\"},\"channelId\":{\"type\":\"string\"},\"senderId\":{\"type\":\"string\"},\"state\":{\"type\":\"string\"},\"tag\":{\"type\":\"string\"}},\"additionalProperties\":false}") },
                new McpToolDefinition { Name = "openclaw.get_session", Description = "Get a session by id.", InputSchema = ParseSchema("{\"type\":\"object\",\"properties\":{\"sessionId\":{\"type\":\"string\"}},\"required\":[\"sessionId\"],\"additionalProperties\":false}") },
                new McpToolDefinition { Name = "openclaw.get_session_timeline", Description = "Get the runtime timeline for a session.", InputSchema = ParseSchema("{\"type\":\"object\",\"properties\":{\"sessionId\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}},\"required\":[\"sessionId\"],\"additionalProperties\":false}") },
                new McpToolDefinition { Name = "openclaw.query_runtime_events", Description = "Query recent runtime events.", InputSchema = ParseSchema("{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\"},\"sessionId\":{\"type\":\"string\"},\"channelId\":{\"type\":\"string\"},\"senderId\":{\"type\":\"string\"},\"component\":{\"type\":\"string\"},\"action\":{\"type\":\"string\"}},\"additionalProperties\":false}") },
                new McpToolDefinition { Name = "openclaw.send_message", Description = "Queue a message into the OpenClaw inbound pipeline.", InputSchema = ParseSchema("{\"type\":\"object\",\"properties\":{\"channelId\":{\"type\":\"string\"},\"senderId\":{\"type\":\"string\"},\"sessionId\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"},\"messageId\":{\"type\":\"string\"},\"replyToMessageId\":{\"type\":\"string\"}},\"required\":[\"text\"],\"additionalProperties\":false}") }
            ]
        };

    private static McpResourceListResult BuildResourceList()
        => new()
        {
            Resources =
            [
                new McpResourceDefinition { Uri = "openclaw://status", Name = "Gateway Status", Description = "Current gateway runtime status snapshot." },
                new McpResourceDefinition { Uri = "openclaw://approvals", Name = "Pending Approvals", Description = "Current pending tool approvals." },
                new McpResourceDefinition { Uri = "openclaw://approvals/history", Name = "Approval History", Description = "Recent approval history entries." },
                new McpResourceDefinition { Uri = "openclaw://providers", Name = "Provider Snapshot", Description = "Provider routing, usage, and recent turn summary." },
                new McpResourceDefinition { Uri = "openclaw://plugins", Name = "Plugin Snapshot", Description = "Current plugin health listing." },
                new McpResourceDefinition { Uri = "openclaw://operator-audit", Name = "Operator Audit", Description = "Recent operator audit entries." },
                new McpResourceDefinition { Uri = "openclaw://dashboard", Name = "Dashboard Snapshot", Description = "Aggregated operator dashboard snapshot from the typed integration API." }
            ]
        };

    private static McpResourceTemplateListResult BuildResourceTemplateList()
        => new()
        {
            ResourceTemplates =
            [
                new McpResourceTemplateDefinition { UriTemplate = "openclaw://sessions/{sessionId}", Name = "Session Detail", Description = "Read a session detail snapshot by session id." },
                new McpResourceTemplateDefinition { UriTemplate = "openclaw://sessions/{sessionId}/timeline", Name = "Session Timeline", Description = "Read a session timeline by session id." }
            ]
        };

    private static McpPromptListResult BuildPromptList()
        => new()
        {
            Prompts =
            [
                new McpPromptDefinition
                {
                    Name = "openclaw_operator_summary",
                    Description = "Guide a model to summarize gateway health for an operator.",
                    Arguments =
                    [
                        new McpPromptArgumentDefinition { Name = "focus", Description = "Optional area to emphasize, such as providers, approvals, or plugins.", Required = false }
                    ]
                },
                new McpPromptDefinition
                {
                    Name = "openclaw_session_summary",
                    Description = "Guide a model to summarize a specific session using MCP resources.",
                    Arguments =
                    [
                        new McpPromptArgumentDefinition { Name = "sessionId", Description = "The session id to summarize.", Required = true }
                    ]
                }
            ]
        };

    private static McpGetPromptResult BuildOperatorSummaryPrompt(IReadOnlyDictionary<string, string> arguments)
    {
        arguments.TryGetValue("focus", out var focus);
        var subject = string.IsNullOrWhiteSpace(focus) ? "overall gateway health" : focus.Trim();

        return new McpGetPromptResult
        {
            Description = "Summarize the OpenClaw gateway state for an operator.",
            Messages =
            [
                new McpPromptMessage
                {
                    Role = "user",
                    Content =
                    [
                        new McpTextContent
                        {
                            Text = $"Summarize {subject}. Start with openclaw://dashboard, then inspect openclaw://status, openclaw://approvals, openclaw://providers, openclaw://plugins, and openclaw://operator-audit as needed. Use the runtime event tools if you need more detail on recent anomalies."
                        }
                    ]
                }
            ]
        };
    }

    private static McpGetPromptResult BuildSessionSummaryPrompt(string sessionId)
        => new()
        {
            Description = "Summarize an OpenClaw session using typed MCP resources.",
            Messages =
            [
                new McpPromptMessage
                {
                    Role = "user",
                    Content =
                    [
                        new McpTextContent
                        {
                            Text = $"Summarize session '{sessionId}'. Read openclaw://sessions/{Uri.EscapeDataString(sessionId)} and openclaw://sessions/{Uri.EscapeDataString(sessionId)}/timeline, then explain the current state, recent runtime events, and any notable provider activity."
                        }
                    ]
                }
            ]
        };

    private static McpCallToolResult CreateToolResult(string text)
        => new()
        {
            Content = [new McpTextContent { Text = text }]
        };

    private static string? GetStringArgument(JsonElement arguments, string propertyName)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int GetIntArgument(JsonElement arguments, string propertyName, int fallback)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(propertyName, out var value))
            return fallback;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : fallback;
    }

    private static bool? GetNullableBoolArgument(JsonElement arguments, string propertyName)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static DateTimeOffset? GetDateTimeOffsetArgument(JsonElement arguments, string propertyName)
    {
        var value = GetStringArgument(arguments, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    private static bool TryGetSessionResourceUri(string uri, out string sessionId)
    {
        const string prefix = "openclaw://sessions/";
        const string timelineSuffix = "/timeline";

        sessionId = string.Empty;
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || uri.EndsWith(timelineSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = uri[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        sessionId = Uri.UnescapeDataString(value);
        return true;
    }

    private static bool TryGetSessionTimelineResourceUri(string uri, out string sessionId)
    {
        const string prefix = "openclaw://sessions/";
        const string timelineSuffix = "/timeline";

        sessionId = string.Empty;
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !uri.EndsWith(timelineSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = uri[prefix.Length..^timelineSuffix.Length].Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        sessionId = Uri.UnescapeDataString(value);
        return true;
    }

    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<IResult> CreateResultResponseAsync<T>(JsonElement? id, T result, JsonTypeInfo<T> typeInfo)
    {
        var json = BuildJsonRpcEnvelope(id, writer =>
        {
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, result, typeInfo);
        });

        return Results.Text(json, "application/json", Encoding.UTF8, StatusCodes.Status200OK);
    }

    private static async Task<IResult> CreateErrorResponseAsync(int statusCode, JsonElement? id, int code, string message)
    {
        var json = BuildJsonRpcEnvelope(id, writer =>
        {
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        });

        return Results.Text(json, "application/json", Encoding.UTF8, statusCode);
    }

    private static string BuildJsonRpcEnvelope(JsonElement? id, Action<Utf8JsonWriter> writeBody)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        if (id is { } value)
            value.WriteTo(writer);
        else
            writer.WriteNullValue();
        writeBody(writer);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
