using OpenClaw.Core.Models;

namespace OpenClaw.Core.Pipeline;

public interface ICronJobSource
{
    IReadOnlyList<CronJobConfig> GetJobs();
}
