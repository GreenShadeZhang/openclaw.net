using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent;

internal sealed record AgentPreparedTurn(
    List<ChatMessage> Messages,
    ChatOptions ChatOptions,
    SessionExecutionCheckpoint? ResumeCheckpoint);
