using OpenClaw.Core.Models;

namespace OpenClaw.Agent;

internal sealed class AgentToolLoopUpdate
{
    private AgentToolLoopUpdate(AgentStreamEvent? streamEvent, AgentToolBatchExecution? batch)
    {
        StreamEvent = streamEvent;
        Batch = batch;
    }

    public AgentStreamEvent? StreamEvent { get; }
    public AgentToolBatchExecution? Batch { get; }

    public static AgentToolLoopUpdate Event(AgentStreamEvent streamEvent) => new(streamEvent, null);

    public static AgentToolLoopUpdate Completed(AgentToolBatchExecution batch) => new(null, batch);
}
