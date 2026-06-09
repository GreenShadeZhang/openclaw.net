using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent;

internal sealed class AgentToolBatchExecution(
    List<ToolInvocation> invocations,
    List<FunctionResultContent> results)
{
    public List<ToolInvocation> Invocations { get; } = invocations;
    public List<FunctionResultContent> Results { get; } = results;
}
