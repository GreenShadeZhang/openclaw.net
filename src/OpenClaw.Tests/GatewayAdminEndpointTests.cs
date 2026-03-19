using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Client;
using OpenClaw.Agent;
using OpenClaw.Agent.Plugins;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Middleware;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;
using OpenClaw.Core.Sessions;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class GatewayAdminEndpointTests
{
    [Fact]
    public async Task AuthSession_BearerAndBrowserSessionFlow_Works()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var anonymousResponse = await harness.Client.GetAsync("/auth/session");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var bearerRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/session");
        bearerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var bearerResponse = await harness.Client.SendAsync(bearerRequest);
        Assert.Equal(HttpStatusCode.OK, bearerResponse.StatusCode);
        var bearerPayload = await ReadJsonAsync(bearerResponse);
        Assert.Equal("bearer", bearerPayload.RootElement.GetProperty("authMode").GetString());

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/session")
        {
            Content = JsonContent("""{"remember":true}""")
        };
        loginRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var loginResponse = await harness.Client.SendAsync(loginRequest);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginPayload = await ReadJsonAsync(loginResponse);
        Assert.Equal("browser-session", loginPayload.RootElement.GetProperty("authMode").GetString());
        var csrfToken = loginPayload.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrfToken));
        var cookie = Assert.Single(loginResponse.Headers.GetValues("Set-Cookie"));

        using var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/session");
        sessionRequest.Headers.Add("Cookie", cookie);
        var sessionResponse = await harness.Client.SendAsync(sessionRequest);
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        var sessionPayload = await ReadJsonAsync(sessionResponse);
        Assert.Equal("browser-session", sessionPayload.RootElement.GetProperty("authMode").GetString());

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/auth/session");
        deleteRequest.Headers.Add("Cookie", cookie);
        deleteRequest.Headers.Add(BrowserSessionAuthService.CsrfHeaderName, csrfToken);
        var deleteResponse = await harness.Client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task AdminSettings_BrowserSessionMutation_RequiresCsrf()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        var (cookie, csrfToken) = await LoginAsync(harness.Client, harness.AuthToken);

        using var currentSettingsRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/settings");
        currentSettingsRequest.Headers.Add("Cookie", cookie);
        var currentSettingsResponse = await harness.Client.SendAsync(currentSettingsRequest);
        currentSettingsResponse.EnsureSuccessStatusCode();
        using var currentSettings = await ReadJsonAsync(currentSettingsResponse);
        var settingsPayload = currentSettings.RootElement.GetProperty("settings").Clone();
        var settingsDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settingsPayload.GetRawText(), CoreJsonContext.Default.BridgeDictionaryStringJsonElement)!;
        settingsDict["usageFooter"] = JsonSerializer.SerializeToElement("tokens");

        using var forbiddenRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/settings")
        {
            Content = JsonContent(JsonSerializer.Serialize(settingsDict, CoreJsonContext.Default.BridgeDictionaryStringJsonElement))
        };
        forbiddenRequest.Headers.Add("Cookie", cookie);
        var forbiddenResponse = await harness.Client.SendAsync(forbiddenRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, forbiddenResponse.StatusCode);

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/settings")
        {
            Content = JsonContent(JsonSerializer.Serialize(settingsDict, CoreJsonContext.Default.BridgeDictionaryStringJsonElement))
        };
        allowedRequest.Headers.Add("Cookie", cookie);
        allowedRequest.Headers.Add(BrowserSessionAuthService.CsrfHeaderName, csrfToken);
        var allowedResponse = await harness.Client.SendAsync(allowedRequest);
        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        var payload = await ReadJsonAsync(allowedResponse);
        Assert.Equal("tokens", payload.RootElement.GetProperty("settings").GetProperty("usageFooter").GetString());
    }

    [Fact]
    public async Task HeartbeatEndpoints_PreviewSaveAndStatus_Work()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        await File.WriteAllTextAsync(Path.Combine(harness.StoragePath, "memory.md"), "Prefer concise summaries.");

        var config = new HeartbeatConfigDto
        {
            Enabled = true,
            CronExpression = "@hourly",
            Timezone = "UTC",
            DeliveryChannelId = "cron",
            DeliverySubject = "Ops heartbeat",
            ModelId = "gpt-4o-mini",
            Tasks =
            [
                new HeartbeatTaskDto
                {
                    Id = "watch-critical-alerts",
                    TemplateKey = "custom",
                    Title = "Watch critical alerts",
                    Instruction = "Only report urgent findings."
                }
            ]
        };

        using var previewRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/heartbeat/preview")
        {
            Content = JsonContent(JsonSerializer.Serialize(config, CoreJsonContext.Default.HeartbeatConfigDto))
        };
        previewRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var previewResponse = await harness.Client.SendAsync(previewRequest);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);

        using var previewPayload = await ReadJsonAsync(previewResponse);
        Assert.True(Path.IsPathRooted(previewPayload.RootElement.GetProperty("configPath").GetString()!));
        Assert.True(Path.IsPathRooted(previewPayload.RootElement.GetProperty("heartbeatPath").GetString()!));
        Assert.True(Path.IsPathRooted(previewPayload.RootElement.GetProperty("memoryMarkdownPath").GetString()!));
        Assert.Equal("gpt-4o-mini", previewPayload.RootElement.GetProperty("costEstimate").GetProperty("modelId").GetString());
        Assert.Equal(0, previewPayload.RootElement.GetProperty("issues").GetArrayLength());

        using var saveRequest = new HttpRequestMessage(HttpMethod.Put, "/admin/heartbeat")
        {
            Content = JsonContent(JsonSerializer.Serialize(config, CoreJsonContext.Default.HeartbeatConfigDto))
        };
        saveRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var saveResponse = await harness.Client.SendAsync(saveRequest);
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);

        using var savePayload = await ReadJsonAsync(saveResponse);
        var configPath = savePayload.RootElement.GetProperty("configPath").GetString()!;
        var heartbeatPath = savePayload.RootElement.GetProperty("heartbeatPath").GetString()!;
        Assert.True(File.Exists(configPath));
        Assert.True(File.Exists(heartbeatPath));

        var heartbeatMarkdown = await File.ReadAllTextAsync(heartbeatPath);
        Assert.Contains("managed_by: openclaw_heartbeat_wizard", heartbeatMarkdown, StringComparison.Ordinal);
        Assert.Contains("source_hash:", heartbeatMarkdown, StringComparison.Ordinal);

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/heartbeat/status");
        statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var statusResponse = await harness.Client.SendAsync(statusRequest);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        using var statusPayload = await ReadJsonAsync(statusResponse);
        Assert.True(statusPayload.RootElement.GetProperty("configExists").GetBoolean());
        Assert.True(statusPayload.RootElement.GetProperty("heartbeatExists").GetBoolean());
        Assert.Equal(Path.Combine(harness.StoragePath, "memory.md"), statusPayload.RootElement.GetProperty("memoryMarkdownPath").GetString());
        Assert.Equal("cron", statusPayload.RootElement.GetProperty("config").GetProperty("deliveryChannelId").GetString());
    }

    [Fact]
    public async Task HeartbeatPreview_UsesSuggestionsAndCostEstimateVariesBySchedule()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        await harness.MemoryStore.SaveNoteAsync("competitor-watch", "Check https://example.com/status for outages.", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(harness.StoragePath, "memory.md"), "Please keep checking https://example.com/status for major changes.");
        var session = await harness.Runtime.SessionManager.GetOrCreateAsync("websocket", "tester", CancellationToken.None);
        session.History.Add(new ChatTurn
        {
            Role = "user",
            Content = "Please monitor https://example.com/status and /tmp/competitor-alerts for changes."
        });
        await harness.Runtime.SessionManager.PersistAsync(session, CancellationToken.None);

        var dailyConfig = new HeartbeatConfigDto
        {
            Enabled = true,
            CronExpression = "0 9 * * *",
            Timezone = "UTC",
            DeliveryChannelId = "cron",
            ModelId = "gpt-4o-mini",
            Tasks =
            [
                new HeartbeatTaskDto
                {
                    Id = "watch-site",
                    TemplateKey = "website_monitoring",
                    Title = "Watch competitor status page",
                    Target = "https://example.com/status"
                }
            ]
        };

        using var dailyPreviewRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/heartbeat/preview")
        {
            Content = JsonContent(JsonSerializer.Serialize(dailyConfig, CoreJsonContext.Default.HeartbeatConfigDto))
        };
        dailyPreviewRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var dailyPreviewResponse = await harness.Client.SendAsync(dailyPreviewRequest);
        Assert.Equal(HttpStatusCode.OK, dailyPreviewResponse.StatusCode);

        using var dailyPayload = await ReadJsonAsync(dailyPreviewResponse);
        Assert.Contains(
            dailyPayload.RootElement.GetProperty("suggestions").EnumerateArray(),
            item => string.Equals(item.GetProperty("target").GetString(), "https://example.com/status", StringComparison.Ordinal));
        Assert.Contains(
            dailyPayload.RootElement.GetProperty("suggestions").EnumerateArray(),
            item => item.GetProperty("reason").GetString()!.Contains("memory.md", StringComparison.Ordinal));

        var dailyRuns = dailyPayload.RootElement.GetProperty("costEstimate").GetProperty("estimatedRunsPerMonth").GetInt32();

        var hourlyConfig = new HeartbeatConfigDto
        {
            Enabled = true,
            CronExpression = "@hourly",
            Timezone = "UTC",
            DeliveryChannelId = "cron",
            ModelId = "gpt-4o-mini",
            Tasks = dailyConfig.Tasks
        };

        using var hourlyPreviewRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/heartbeat/preview")
        {
            Content = JsonContent(JsonSerializer.Serialize(hourlyConfig, CoreJsonContext.Default.HeartbeatConfigDto))
        };
        hourlyPreviewRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var hourlyPreviewResponse = await harness.Client.SendAsync(hourlyPreviewRequest);
        Assert.Equal(HttpStatusCode.OK, hourlyPreviewResponse.StatusCode);

        using var hourlyPayload = await ReadJsonAsync(hourlyPreviewResponse);
        var hourlyRuns = hourlyPayload.RootElement.GetProperty("costEstimate").GetProperty("estimatedRunsPerMonth").GetInt32();

        Assert.True(hourlyRuns > dailyRuns);
    }

    [Fact]
    public async Task HeartbeatSave_InvalidConfig_ReturnsBadRequest()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var invalidConfig = new HeartbeatConfigDto
        {
            Enabled = true,
            CronExpression = "not-a-cron",
            Timezone = "Mars/Phobos",
            DeliveryChannelId = "telegram",
            Tasks = []
        };

        using var saveRequest = new HttpRequestMessage(HttpMethod.Put, "/admin/heartbeat")
        {
            Content = JsonContent(JsonSerializer.Serialize(invalidConfig, CoreJsonContext.Default.HeartbeatConfigDto))
        };
        saveRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var saveResponse = await harness.Client.SendAsync(saveRequest);

        Assert.Equal(HttpStatusCode.BadRequest, saveResponse.StatusCode);
        using var payload = await ReadJsonAsync(saveResponse);
        Assert.NotEqual(0, payload.RootElement.GetProperty("issues").GetArrayLength());
    }

    [Fact]
    public async Task AdminSettings_Mutation_RejectsOversizedPayload()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var oversizedFooter = new string('x', 300_000);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/settings")
        {
            Content = JsonContent($$"""{"usageFooter":"{{oversizedFooter}}"}""")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);

        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task ToolsApprovals_AndHistory_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);
        var approval = harness.Runtime.ToolApprovalService.Create("sess1", "telegram", "sender1", "shell", """{"cmd":"ls"}""", TimeSpan.FromMinutes(5));
        harness.Runtime.ApprovalAuditStore.RecordCreated(approval);

        using var approvalsRequest = new HttpRequestMessage(HttpMethod.Get, "/tools/approvals");
        approvalsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var approvalsResponse = await harness.Client.SendAsync(approvalsRequest);
        Assert.Equal(HttpStatusCode.OK, approvalsResponse.StatusCode);
        var approvalsPayload = await ReadJsonAsync(approvalsResponse);
        Assert.Equal(1, approvalsPayload.RootElement.GetProperty("items").GetArrayLength());

        using var historyRequest = new HttpRequestMessage(HttpMethod.Get, "/tools/approvals/history?limit=10");
        historyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var historyResponse = await harness.Client.SendAsync(historyRequest);
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var historyPayload = await ReadJsonAsync(historyResponse);
        Assert.Equal(1, historyPayload.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("created", historyPayload.RootElement.GetProperty("items")[0].GetProperty("eventType").GetString());
    }

    [Fact]
    public async Task ProviderPolicies_Audit_AndRateLimits_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        using var createPolicy = new HttpRequestMessage(HttpMethod.Post, "/admin/providers/policies")
        {
            Content = JsonContent("""
                {
                  "id": "pp_test",
                  "priority": 10,
                  "providerId": "openai",
                  "modelId": "gpt-4o-mini",
                  "enabled": true,
                  "fallbackModels": ["gpt-4o"]
                }
                """)
        };
        createPolicy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var createPolicyResponse = await harness.Client.SendAsync(createPolicy);
        Assert.Equal(HttpStatusCode.OK, createPolicyResponse.StatusCode);

        using var listPolicies = new HttpRequestMessage(HttpMethod.Get, "/admin/providers/policies");
        listPolicies.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var listPoliciesResponse = await harness.Client.SendAsync(listPolicies);
        Assert.Equal(HttpStatusCode.OK, listPoliciesResponse.StatusCode);
        using var policiesPayload = await ReadJsonAsync(listPoliciesResponse);
        Assert.Equal(1, policiesPayload.RootElement.GetProperty("items").GetArrayLength());

        using var resetCircuit = new HttpRequestMessage(HttpMethod.Post, "/admin/providers/openai/circuit/reset");
        resetCircuit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var resetCircuitResponse = await harness.Client.SendAsync(resetCircuit);
        Assert.Equal(HttpStatusCode.OK, resetCircuitResponse.StatusCode);

        using var createRateLimit = new HttpRequestMessage(HttpMethod.Post, "/admin/rate-limits")
        {
            Content = JsonContent("""
                {
                  "id": "rl_test",
                  "actorType": "ip",
                  "endpointScope": "openai_http",
                  "burstLimit": 5,
                  "burstWindowSeconds": 60,
                  "sustainedLimit": 10,
                  "sustainedWindowSeconds": 300,
                  "enabled": true
                }
                """)
        };
        createRateLimit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var createRateLimitResponse = await harness.Client.SendAsync(createRateLimit);
        Assert.Equal(HttpStatusCode.OK, createRateLimitResponse.StatusCode);

        using var auditRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/audit?limit=10");
        auditRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var auditResponse = await harness.Client.SendAsync(auditRequest);
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        using var auditPayload = await ReadJsonAsync(auditResponse);
        var actions = auditPayload.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("actionType").GetString())
            .ToArray();
        Assert.Contains("provider_policy_upsert", actions);
        Assert.Contains("provider_circuit_reset", actions);
        Assert.Contains("rate_limit_policy_upsert", actions);
    }

    [Fact]
    public async Task PluginState_ApprovalPolicies_AndTimeline_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        using var disablePlugin = new HttpRequestMessage(HttpMethod.Post, "/admin/plugins/test-plugin/disable")
        {
            Content = JsonContent("""{"reason":"maintenance"}""")
        };
        disablePlugin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var disableResponse = await harness.Client.SendAsync(disablePlugin);
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);

        using var pluginRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/plugins/test-plugin");
        pluginRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var pluginResponse = await harness.Client.SendAsync(pluginRequest);
        Assert.Equal(HttpStatusCode.OK, pluginResponse.StatusCode);
        using var pluginPayload = await ReadJsonAsync(pluginResponse);
        Assert.True(pluginPayload.RootElement.GetProperty("disabled").GetBoolean());

        using var createGrant = new HttpRequestMessage(HttpMethod.Post, "/tools/approval-policies")
        {
            Content = JsonContent("""
                {
                  "id": "grant_test",
                  "scope": "sender_tool_window",
                  "channelId": "telegram",
                  "senderId": "user1",
                  "toolName": "shell",
                  "grantedBy": "tester",
                  "grantSource": "test",
                  "remainingUses": 1
                }
                """)
        };
        createGrant.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var createGrantResponse = await harness.Client.SendAsync(createGrant);
        Assert.Equal(HttpStatusCode.OK, createGrantResponse.StatusCode);

        using var listGrantRequest = new HttpRequestMessage(HttpMethod.Get, "/tools/approval-policies");
        listGrantRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var listGrantResponse = await harness.Client.SendAsync(listGrantRequest);
        Assert.Equal(HttpStatusCode.OK, listGrantResponse.StatusCode);
        using var grantPayload = await ReadJsonAsync(listGrantResponse);
        Assert.Equal(1, grantPayload.RootElement.GetProperty("items").GetArrayLength());

        var session = await harness.Runtime.SessionManager.GetOrCreateByIdAsync("sess-timeline", "telegram", "user1", CancellationToken.None);
        session.History.Add(new ChatTurn { Role = "user", Content = "hello" });
        await harness.Runtime.SessionManager.PersistAsync(session, CancellationToken.None);
        harness.Runtime.Operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = "evt_timeline",
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            Component = "test",
            Action = "seeded",
            Severity = "info",
            Summary = "seeded"
        });

        using var timelineRequest = new HttpRequestMessage(HttpMethod.Get, $"/admin/sessions/{Uri.EscapeDataString(session.Id)}/timeline");
        timelineRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var timelineResponse = await harness.Client.SendAsync(timelineRequest);
        Assert.Equal(HttpStatusCode.OK, timelineResponse.StatusCode);
        using var timelinePayload = await ReadJsonAsync(timelineResponse);
        Assert.Equal(session.Id, timelinePayload.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(1, timelinePayload.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task AdminSummary_IncludesRuntimeOrchestrator()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: false);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/summary");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = await ReadJsonAsync(response);
        Assert.Equal(
            OpenClaw.Core.Models.RuntimeOrchestrator.Native,
            payload.RootElement.GetProperty("runtime").GetProperty("orchestrator").GetString());
    }

    [Fact]
    public async Task IntegrationApi_Status_Sessions_Events_AndMessageQueue_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var session = await harness.Runtime.SessionManager.GetOrCreateByIdAsync("sess-integration", "api", "user1", CancellationToken.None);
        session.History.Add(new ChatTurn { Role = "user", Content = "hello" });
        await harness.Runtime.SessionManager.PersistAsync(session, CancellationToken.None);
        harness.Runtime.Operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = "evt_integration",
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            Component = "integration-test",
            Action = "seeded",
            Severity = "info",
            Summary = "seeded"
        });

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/status");
        statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var statusResponse = await harness.Client.SendAsync(statusRequest);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        using var statusPayload = await ReadJsonAsync(statusResponse);
        Assert.Equal("ok", statusPayload.RootElement.GetProperty("health").GetProperty("status").GetString());
        Assert.True(statusPayload.RootElement.GetProperty("activeSessions").GetInt32() >= 1);

        using var sessionsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/sessions?page=1&pageSize=10&channelId=api");
        sessionsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var sessionsResponse = await harness.Client.SendAsync(sessionsRequest);
        Assert.Equal(HttpStatusCode.OK, sessionsResponse.StatusCode);
        using var sessionsPayload = await ReadJsonAsync(sessionsResponse);
        Assert.Equal(1, sessionsPayload.RootElement.GetProperty("active").GetArrayLength());

        using var detailRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/integration/sessions/{Uri.EscapeDataString(session.Id)}");
        detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var detailResponse = await harness.Client.SendAsync(detailRequest);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detailPayload = await ReadJsonAsync(detailResponse);
        Assert.Equal(session.Id, detailPayload.RootElement.GetProperty("session").GetProperty("id").GetString());
        Assert.True(detailPayload.RootElement.GetProperty("isActive").GetBoolean());

        using var eventsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/runtime-events?limit=10&component=integration-test");
        eventsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var eventsResponse = await harness.Client.SendAsync(eventsRequest);
        Assert.Equal(HttpStatusCode.OK, eventsResponse.StatusCode);
        using var eventsPayload = await ReadJsonAsync(eventsResponse);
        Assert.Equal(1, eventsPayload.RootElement.GetProperty("items").GetArrayLength());

        using var enqueueRequest = new HttpRequestMessage(HttpMethod.Post, "/api/integration/messages")
        {
            Content = JsonContent("""
                {
                  "channelId": "api",
                  "senderId": "client-1",
                  "text": "queued message"
                }
                """)
        };
        enqueueRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var enqueueResponse = await harness.Client.SendAsync(enqueueRequest);
        Assert.Equal(HttpStatusCode.Accepted, enqueueResponse.StatusCode);
        using var enqueuePayload = await ReadJsonAsync(enqueueResponse);
        Assert.True(enqueuePayload.RootElement.GetProperty("accepted").GetBoolean());

        var queued = await harness.Runtime.Pipeline.InboundReader.ReadAsync(CancellationToken.None);
        Assert.Equal("api", queued.ChannelId);
        Assert.Equal("client-1", queued.SenderId);
        Assert.Equal("queued message", queued.Text);
    }

    [Fact]
    public async Task IntegrationApi_Dashboard_Approvals_Providers_Plugins_Audit_AndTimeline_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var session = await harness.Runtime.SessionManager.GetOrCreateByIdAsync("sess-dashboard", "api", "user-dashboard", CancellationToken.None);
        session.History.Add(new ChatTurn { Role = "user", Content = "inspect me" });
        await harness.Runtime.SessionManager.PersistAsync(session, CancellationToken.None);

        var approval = harness.Runtime.ToolApprovalService.Create("sess-dashboard", "api", "user-dashboard", "shell", "{\"cmd\":\"pwd\"}", TimeSpan.FromMinutes(5));
        harness.Runtime.ApprovalAuditStore.RecordCreated(approval);
        harness.Runtime.Operations.OperatorAudit.Append(new OperatorAuditEntry
        {
            Id = "audit_dashboard_1",
            ActorId = "tester",
            AuthMode = "bearer",
            ActionType = "dashboard_test",
            TargetId = session.Id,
            Summary = "seeded",
            Success = true
        });
        harness.Runtime.Operations.RuntimeEvents.Append(new RuntimeEventEntry
        {
            Id = "evt_dashboard",
            SessionId = session.Id,
            ChannelId = session.ChannelId,
            SenderId = session.SenderId,
            Component = "dashboard-test",
            Action = "seeded",
            Severity = "info",
            Summary = "seeded"
        });

        using var dashboardRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/dashboard");
        dashboardRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var dashboardResponse = await harness.Client.SendAsync(dashboardRequest);
        Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);
        using var dashboardPayload = await ReadJsonAsync(dashboardResponse);
        Assert.Equal("ok", dashboardPayload.RootElement.GetProperty("status").GetProperty("health").GetProperty("status").GetString());
        Assert.Equal(1, dashboardPayload.RootElement.GetProperty("approvals").GetProperty("items").GetArrayLength());

        using var approvalsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/approvals?channelId=api&senderId=user-dashboard");
        approvalsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var approvalsResponse = await harness.Client.SendAsync(approvalsRequest);
        Assert.Equal(HttpStatusCode.OK, approvalsResponse.StatusCode);
        using var approvalsPayload = await ReadJsonAsync(approvalsResponse);
        Assert.Equal(1, approvalsPayload.RootElement.GetProperty("items").GetArrayLength());

        using var approvalHistoryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/approval-history?limit=10&channelId=api");
        approvalHistoryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var approvalHistoryResponse = await harness.Client.SendAsync(approvalHistoryRequest);
        Assert.Equal(HttpStatusCode.OK, approvalHistoryResponse.StatusCode);
        using var approvalHistoryPayload = await ReadJsonAsync(approvalHistoryResponse);
        Assert.Equal("created", approvalHistoryPayload.RootElement.GetProperty("items")[0].GetProperty("eventType").GetString());

        using var providersRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/providers?recentTurnsLimit=5");
        providersRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var providersResponse = await harness.Client.SendAsync(providersRequest);
        Assert.Equal(HttpStatusCode.OK, providersResponse.StatusCode);
        using var providersPayload = await ReadJsonAsync(providersResponse);
        Assert.True(providersPayload.RootElement.TryGetProperty("routes", out _));

        using var pluginsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/plugins");
        pluginsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var pluginsResponse = await harness.Client.SendAsync(pluginsRequest);
        Assert.Equal(HttpStatusCode.OK, pluginsResponse.StatusCode);
        using var pluginsPayload = await ReadJsonAsync(pluginsResponse);
        Assert.True(pluginsPayload.RootElement.TryGetProperty("items", out _));

        using var auditRequest = new HttpRequestMessage(HttpMethod.Get, "/api/integration/operator-audit?limit=10&actionType=dashboard_test");
        auditRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var auditResponse = await harness.Client.SendAsync(auditRequest);
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        using var auditPayload = await ReadJsonAsync(auditResponse);
        Assert.Equal(1, auditPayload.RootElement.GetProperty("items").GetArrayLength());

        using var detailRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/integration/sessions/{Uri.EscapeDataString(session.Id)}");
        detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var detailResponse = await harness.Client.SendAsync(detailRequest);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detailPayload = await ReadJsonAsync(detailResponse);
        Assert.Equal(0, detailPayload.RootElement.GetProperty("branchCount").GetInt32());

        using var timelineRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/integration/sessions/{Uri.EscapeDataString(session.Id)}/timeline?limit=10");
        timelineRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var timelineResponse = await harness.Client.SendAsync(timelineRequest);
        Assert.Equal(HttpStatusCode.OK, timelineResponse.StatusCode);
        using var timelinePayload = await ReadJsonAsync(timelineResponse);
        Assert.Equal(session.Id, timelinePayload.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(1, timelinePayload.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task Mcp_Initialize_List_And_Call_AreServed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var anonymousResponse = await harness.Client.PostAsync("/mcp", JsonContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var initializeRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent("""
                {
                  "jsonrpc": "2.0",
                  "id": 1,
                  "method": "initialize",
                  "params": { "protocolVersion": "2025-03-26" }
                }
                """)
        };
        initializeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var initializeResponse = await harness.Client.SendAsync(initializeRequest);
        Assert.Equal(HttpStatusCode.OK, initializeResponse.StatusCode);
        using var initializePayload = await ReadJsonAsync(initializeResponse);
        Assert.Equal("OpenClaw Gateway MCP", initializePayload.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(initializePayload.RootElement.GetProperty("result").GetProperty("capabilities").GetProperty("resources").GetProperty("supportsTemplates").GetBoolean());

        using var toolsListRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent("""
                {
                  "jsonrpc": "2.0",
                  "id": 2,
                  "method": "tools/list",
                  "params": {}
                }
                """)
        };
        toolsListRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var toolsListResponse = await harness.Client.SendAsync(toolsListRequest);
        Assert.Equal(HttpStatusCode.OK, toolsListResponse.StatusCode);
        using var toolsListPayload = await ReadJsonAsync(toolsListResponse);
        Assert.Contains(toolsListPayload.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(item => item.GetProperty("name").GetString()), name => name == "openclaw.get_dashboard");

        using var templatesListRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent("""
                {
                  "jsonrpc": "2.0",
                  "id": 22,
                  "method": "resources/templates/list",
                  "params": {}
                }
                """)
        };
        templatesListRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var templatesListResponse = await harness.Client.SendAsync(templatesListRequest);
        Assert.Equal(HttpStatusCode.OK, templatesListResponse.StatusCode);
        using var templatesListPayload = await ReadJsonAsync(templatesListResponse);
        Assert.Contains(templatesListPayload.RootElement.GetProperty("result").GetProperty("resourceTemplates").EnumerateArray().Select(item => item.GetProperty("uriTemplate").GetString()), template => template == "openclaw://sessions/{sessionId}");

        using var callToolRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent("""
                {
                  "jsonrpc": "2.0",
                  "id": 3,
                  "method": "tools/call",
                  "params": {
                    "name": "openclaw.get_status",
                    "arguments": {}
                  }
                }
                """)
        };
        callToolRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var callToolResponse = await harness.Client.SendAsync(callToolRequest);
        Assert.Equal(HttpStatusCode.OK, callToolResponse.StatusCode);
        using var callToolPayload = await ReadJsonAsync(callToolResponse);
        var statusText = callToolPayload.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("activeSessions", statusText);

        using var resourceReadRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent("""
                {
                  "jsonrpc": "2.0",
                  "id": 4,
                  "method": "resources/read",
                  "params": {
                    "uri": "openclaw://dashboard"
                  }
                }
                """)
        };
        resourceReadRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var resourceReadResponse = await harness.Client.SendAsync(resourceReadRequest);
        Assert.Equal(HttpStatusCode.OK, resourceReadResponse.StatusCode);
        using var resourceReadPayload = await ReadJsonAsync(resourceReadResponse);
        var dashboardText = resourceReadPayload.RootElement.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString();
        Assert.Contains("status", dashboardText);

        using var promptGetRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent("""
                {
                  "jsonrpc": "2.0",
                  "id": 23,
                  "method": "prompts/get",
                  "params": {
                    "name": "openclaw_session_summary",
                    "arguments": {
                      "sessionId": "sess-dashboard"
                    }
                  }
                }
                """)
        };
        promptGetRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
        var promptGetResponse = await harness.Client.SendAsync(promptGetRequest);
        Assert.Equal(HttpStatusCode.OK, promptGetResponse.StatusCode);
        using var promptGetPayload = await ReadJsonAsync(promptGetResponse);
        var promptText = promptGetPayload.RootElement.GetProperty("result").GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("sess-dashboard", promptText);
    }

    [Fact]
    public async Task OpenClawHttpClient_McpSurface_Works()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

        var session = await harness.Runtime.SessionManager.GetOrCreateByIdAsync("sess-client-mcp", "api", "sdk-user", CancellationToken.None);
        session.History.Add(new ChatTurn { Role = "user", Content = "hello from sdk" });
        await harness.Runtime.SessionManager.PersistAsync(session, CancellationToken.None);

        using var client = new OpenClawHttpClient(harness.Client.BaseAddress!.ToString(), harness.AuthToken, harness.Client);

        var initialize = await client.InitializeMcpAsync(new McpInitializeRequest { ProtocolVersion = "2025-03-26" }, CancellationToken.None);
        Assert.True(initialize.Capabilities.Resources.SupportsTemplates);

        var tools = await client.ListMcpToolsAsync(CancellationToken.None);
        Assert.Contains(tools.Tools, item => item.Name == "openclaw.get_dashboard");

        var templates = await client.ListMcpResourceTemplatesAsync(CancellationToken.None);
        Assert.Contains(templates.ResourceTemplates, item => item.UriTemplate == "openclaw://sessions/{sessionId}");

        var prompt = await client.GetMcpPromptAsync(
            "openclaw_session_summary",
            new Dictionary<string, string> { ["sessionId"] = session.Id },
            CancellationToken.None);
        Assert.Contains(session.Id, prompt.Messages[0].Content[0].Text);

        var sessionResource = await client.ReadMcpResourceAsync($"openclaw://sessions/{Uri.EscapeDataString(session.Id)}", CancellationToken.None);
        Assert.Contains(session.Id, sessionResource.Contents[0].Text);

        using var emptyArguments = JsonDocument.Parse("{}");
        var toolResult = await client.CallMcpToolAsync("openclaw.get_status", emptyArguments.RootElement.Clone(), CancellationToken.None);
        Assert.False(toolResult.IsError);
        Assert.Contains("activeSessions", toolResult.Content[0].Text);
    }

    [Fact]
    public async Task OpenApi_Document_IsExposed()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: false);

        var response = await harness.Client.GetAsync("/openapi/openclaw-integration.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var openApiVersion = payload.RootElement.GetProperty("openapi").GetString();
        Assert.StartsWith("3.", openApiVersion);
        Assert.True(payload.RootElement.GetProperty("paths").TryGetProperty("/api/integration/dashboard", out _));
    }

    [Fact]
    public async Task AdminUiContract_ReferencedRoutes_AreMapped()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: false);
        var dataSource = harness.App.Services.GetRequiredService<EndpointDataSource>();
        var routePatterns = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToHashSet(StringComparer.Ordinal);

        var expectedRoutes = new[]
        {
            "/auth/session",
            "/openapi/{documentName}.json",
            "/api/integration/dashboard",
            "/api/integration/status",
            "/api/integration/approvals",
            "/api/integration/approval-history",
            "/api/integration/providers",
            "/api/integration/plugins",
            "/api/integration/operator-audit",
            "/api/integration/sessions",
            "/api/integration/sessions/{id}",
            "/api/integration/sessions/{id}/timeline",
            "/api/integration/runtime-events",
            "/api/integration/messages",
            "/mcp",
            "/admin",
            "/admin/summary",
            "/admin/providers",
            "/admin/providers/policies",
            "/admin/providers/{providerId}/circuit/reset",
            "/admin/events",
            "/admin/sessions",
            "/admin/sessions/{id}",
            "/admin/sessions/{id}/branches",
            "/admin/sessions/{id}/timeline",
            "/admin/sessions/{id}/diff",
            "/admin/sessions/{id}/metadata",
            "/admin/sessions/export",
            "/admin/sessions/{id}/export",
            "/admin/branches/{id}/restore",
            "/admin/plugins",
            "/admin/plugins/{id}",
            "/admin/plugins/{id}/disable",
            "/admin/plugins/{id}/enable",
            "/admin/plugins/{id}/quarantine",
            "/admin/plugins/{id}/clear-quarantine",
            "/admin/audit",
            "/admin/webhooks/dead-letter",
            "/admin/webhooks/dead-letter/{id}/replay",
            "/admin/webhooks/dead-letter/{id}/discard",
            "/admin/rate-limits",
            "/admin/rate-limits/{id}",
            "/admin/settings",
            "/admin/heartbeat",
            "/admin/heartbeat/preview",
            "/admin/heartbeat/status",
            "/tools/approvals",
            "/tools/approvals/history",
            "/tools/approval-policies",
            "/tools/approval-policies/{id}",
            "/pairing/list",
            "/allowlists/{channelId}",
            "/allowlists/{channelId}/add_latest",
            "/allowlists/{channelId}/tighten",
            "/memory/retention/status",
            "/memory/retention/sweep",
            "/doctor/text"
        };

        foreach (var route in expectedRoutes)
            Assert.Contains(route, routePatterns);
    }

    [Fact]
    public async Task AdminUi_StaticApiTargets_MapToKnownRoutes()
    {
        await using var harness = await CreateHarnessAsync(nonLoopbackBind: false);
        var dataSource = harness.App.Services.GetRequiredService<EndpointDataSource>();
        var routePatterns = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(static endpoint => endpoint.RoutePattern.RawText)
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .ToHashSet(StringComparer.Ordinal);

        var adminHtmlPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/OpenClaw.Gateway/wwwroot/admin.html"));
        var html = await File.ReadAllTextAsync(adminHtmlPath);
        var matches = Regex.Matches(html, @"(?:api|mutate)\('(?<route>/[^']+)'");
        var staticRoutes = matches
            .Select(match => match.Groups["route"].Value.Split('?', 2)[0])
            .Where(static route => !route.Contains('{', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var route in staticRoutes)
            Assert.Contains(route, routePatterns);
    }

    private static async Task<(string Cookie, string CsrfToken)> LoginAsync(HttpClient client, string authToken)
    {
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/session")
        {
            Content = JsonContent("""{"remember":false}""")
        };
        loginRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        var response = await client.SendAsync(loginRequest);
        response.EnsureSuccessStatusCode();
        var payload = await ReadJsonAsync(response);
        return (
            Assert.Single(response.Headers.GetValues("Set-Cookie")),
            payload.RootElement.GetProperty("csrfToken").GetString()!);
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(payload);
    }

    private static async Task<GatewayTestHarness> CreateHarnessAsync(bool nonLoopbackBind)
    {
        var storagePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openclaw-admin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        var config = new GatewayConfig
        {
            BindAddress = nonLoopbackBind ? "0.0.0.0" : "127.0.0.1",
            AuthToken = "test-admin-token",
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            },
            Llm = new LlmProviderConfig
            {
                Provider = "openai",
                ApiKey = "test-key",
                Model = "gpt-4o",
                RetryCount = 0,
                TimeoutSeconds = 0
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false,
                AllowBrowserEvaluate = false
            },
            Plugins = new OpenClaw.Core.Plugins.PluginsConfig
            {
                Enabled = false
            }
        };

        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
            IsNonLoopbackBind = nonLoopbackBind,
            WorkspacePath = null
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOpenApi("openclaw-integration");
        builder.Services.ConfigureHttpJsonOptions(opts => opts.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default));
        var memoryStore = new FileMemoryStore(storagePath, maxCachedSessions: 8);
        var sessionManager = new SessionManager(memoryStore, config, NullLogger.Instance);
        var heartbeatService = new HeartbeatService(config, memoryStore, sessionManager, NullLogger<HeartbeatService>.Instance);
        builder.Services.AddSingleton<IMemoryStore>(memoryStore);
        builder.Services.AddSingleton(sessionManager);
        builder.Services.AddSingleton(heartbeatService);
        builder.Services.AddSingleton(new BrowserSessionAuthService(config));
        builder.Services.AddSingleton(new AdminSettingsService(
            config,
            AdminSettingsService.CreateSnapshot(config),
            AdminSettingsService.GetSettingsPath(config),
            NullLogger<AdminSettingsService>.Instance));
        builder.Services.AddSingleton(new ProviderUsageTracker());
        builder.Services.AddSingleton(new ToolUsageTracker());
        builder.Services.AddSingleton(new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance));
        builder.Services.AddSingleton(new ContractStore(storagePath, NullLogger<ContractStore>.Instance));
        builder.Services.AddSingleton(sp =>
        {
            var contractStartup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
                IsNonLoopbackBind = nonLoopbackBind,
                WorkspacePath = null
            };
            return new ContractGovernanceService(
                contractStartup,
                sp.GetRequiredService<ContractStore>(),
                sp.GetRequiredService<RuntimeEventStore>(),
                sp.GetRequiredService<ProviderUsageTracker>(),
                NullLogger<ContractGovernanceService>.Instance);
        });

        var app = builder.Build();
        var runtime = CreateRuntime(config, storagePath, memoryStore, sessionManager, heartbeatService);
        app.MapOpenApi("/openapi/{documentName}.json");
        app.MapOpenClawEndpoints(startup, runtime);
        await app.StartAsync();

        return new GatewayTestHarness(app, app.GetTestClient(), runtime, config.AuthToken!, storagePath, memoryStore);
    }

    private static GatewayAppRuntime CreateRuntime(
        GatewayConfig config,
        string storagePath,
        IMemoryStore memoryStore,
        SessionManager sessionManager,
        HeartbeatService heartbeatService)
    {
        var allowlistSemantics = AllowlistPolicy.ParseSemantics(config.Channels.AllowlistSemantics);
        var allowlists = new AllowlistManager(storagePath, NullLogger<AllowlistManager>.Instance);
        var recentSenders = new RecentSendersStore(storagePath, NullLogger<RecentSendersStore>.Instance);
        var commandProcessor = new ChatCommandProcessor(sessionManager);
        var toolApprovalService = new ToolApprovalService();
        var approvalAuditStore = new ApprovalAuditStore(storagePath, NullLogger<ApprovalAuditStore>.Instance);
        var runtimeMetrics = new RuntimeMetrics();
        var providerUsage = new ProviderUsageTracker();
        var providerRegistry = new LlmProviderRegistry();
        var providerPolicies = new ProviderPolicyService(storagePath, NullLogger<ProviderPolicyService>.Instance);
        var runtimeEvents = new RuntimeEventStore(storagePath, NullLogger<RuntimeEventStore>.Instance);
        var operatorAudit = new OperatorAuditStore(storagePath, NullLogger<OperatorAuditStore>.Instance);
        var approvalGrants = new ToolApprovalGrantStore(storagePath, NullLogger<ToolApprovalGrantStore>.Instance);
        var webhookDeliveries = new WebhookDeliveryStore(storagePath, NullLogger<WebhookDeliveryStore>.Instance);
        var actorRateLimits = new ActorRateLimitService(storagePath, NullLogger<ActorRateLimitService>.Instance);
        var sessionMetadata = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance);
        var pluginHealth = new PluginHealthService(storagePath, NullLogger<PluginHealthService>.Instance);
        var llmExecution = new GatewayLlmExecutionService(
            config,
            providerRegistry,
            providerPolicies,
            runtimeEvents,
            runtimeMetrics,
            providerUsage,
            NullLogger<GatewayLlmExecutionService>.Instance);
        var retentionCoordinator = Substitute.For<IMemoryRetentionCoordinator>();
        retentionCoordinator.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new RetentionRunStatus { Enabled = false, StoreSupportsRetention = false }));
        retentionCoordinator.SweepNowAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(new RetentionSweepResult()));

        var agentRuntime = Substitute.For<IAgentRuntime>();
        agentRuntime.CircuitBreakerState.Returns(CircuitState.Closed);
        agentRuntime.LoadedSkillNames.Returns(Array.Empty<string>());
        agentRuntime.ReloadSkillsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var pipeline = new MessagePipeline();
        var middleware = new MiddlewarePipeline([]);
        var wsChannel = new WebSocketChannel(config.WebSocket);
        var nativeRegistry = new NativePluginRegistry(config.Plugins.Native, NullLogger.Instance, config.Tooling);
        var skillWatcher = new SkillWatcherService(config.Skills, null, [], agentRuntime, NullLogger<SkillWatcherService>.Instance);

        return new GatewayAppRuntime
        {
            AgentRuntime = agentRuntime,
            OrchestratorId = RuntimeOrchestrator.Native,
            Pipeline = pipeline,
            MiddlewarePipeline = middleware,
            WebSocketChannel = wsChannel,
            ChannelAdapters = new Dictionary<string, OpenClaw.Core.Abstractions.IChannelAdapter>(StringComparer.Ordinal)
            {
                ["websocket"] = wsChannel
            },
            SessionManager = sessionManager,
            RetentionCoordinator = retentionCoordinator,
            PairingManager = new PairingManager(storagePath, NullLogger<PairingManager>.Instance),
            Allowlists = allowlists,
            AllowlistSemantics = allowlistSemantics,
            RecentSenders = recentSenders,
            CommandProcessor = commandProcessor,
            ToolApprovalService = toolApprovalService,
            ApprovalAuditStore = approvalAuditStore,
            RuntimeMetrics = runtimeMetrics,
            ProviderUsage = providerUsage,
            Heartbeat = heartbeatService,
            SkillWatcher = skillWatcher,
            PluginReports = Array.Empty<PluginLoadReport>(),
            Operations = new RuntimeOperationsState
            {
                ProviderPolicies = providerPolicies,
                ProviderRegistry = providerRegistry,
                LlmExecution = llmExecution,
                PluginHealth = pluginHealth,
                ApprovalGrants = approvalGrants,
                RuntimeEvents = runtimeEvents,
                OperatorAudit = operatorAudit,
                WebhookDeliveries = webhookDeliveries,
                ActorRateLimits = actorRateLimits,
                SessionMetadata = sessionMetadata
            },
            EffectiveRequireToolApproval = false,
            EffectiveApprovalRequiredTools = Array.Empty<string>(),
            NativeRegistry = nativeRegistry,
            SessionLocks = new ConcurrentDictionary<string, SemaphoreSlim>(),
            LockLastUsed = new ConcurrentDictionary<string, DateTimeOffset>(),
            AllowedOriginsSet = null,
            DynamicProviderOwners = Array.Empty<string>(),
            EstimatedSkillPromptChars = 0,
            CronTask = null,
            TwilioSmsWebhookHandler = null,
            PluginHost = null,
            NativeDynamicPluginHost = null,
            RegisteredToolNames = System.Collections.Frozen.FrozenSet<string>.Empty
        };
    }

    private sealed record GatewayTestHarness(
        WebApplication App,
        HttpClient Client,
        GatewayAppRuntime Runtime,
        string AuthToken,
        string StoragePath,
        IMemoryStore MemoryStore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}
