using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway;

internal sealed class GatewayCronJobSource : ICronJobSource
{
    private readonly GatewayConfig _config;
    private readonly HeartbeatService _heartbeat;

    public GatewayCronJobSource(GatewayConfig config, HeartbeatService heartbeat)
    {
        _config = config;
        _heartbeat = heartbeat;
    }

    public IReadOnlyList<CronJobConfig> GetJobs()
    {
        var jobs = new List<CronJobConfig>();
        if (_config.Cron.Enabled && _config.Cron.Jobs is { Count: > 0 })
            jobs.AddRange(_config.Cron.Jobs);

        var managedHeartbeatJob = _heartbeat.BuildManagedJob();
        if (managedHeartbeatJob is not null)
            jobs.Add(managedHeartbeatJob);

        return jobs;
    }
}
