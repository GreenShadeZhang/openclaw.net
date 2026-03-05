using OpenClaw.Cli;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ClawHubWrapperTests
{
    [Fact]
    public void ParseArgs_Help_SetsShowHelp()
    {
        var parsed = ClawHubCommand.ParseArgs(["--help"]);
        Assert.True(parsed.ShowHelp);
    }

    [Fact]
    public void ParseArgs_WorkdirAndForwarding_Works()
    {
        var parsed = ClawHubCommand.ParseArgs(["--workdir", "/tmp", "--", "search", "calendar"]);
        Assert.Equal("/tmp", parsed.Workdir);
        Assert.False(parsed.UseManaged);
        Assert.Equal(ClawHubCommand.TelemetryMode.Off, parsed.Telemetry);
        Assert.Equal(["search", "calendar"], parsed.ForwardArgs);
    }

    [Fact]
    public void ParseArgs_DoubleDash_ForwardsHelpToClawhub()
    {
        var parsed = ClawHubCommand.ParseArgs(["--", "--help"]);
        Assert.False(parsed.ShowHelp);
        Assert.Equal(["--help"], parsed.ForwardArgs);
    }

    [Fact]
    public void ParseArgs_UnknownWrapperOption_Errors()
    {
        var ex = Assert.ThrowsAny<Exception>(() => ClawHubCommand.ParseArgs(["--nope"]));
        Assert.Contains("Unknown option", ex.Message);
    }

    [Fact]
    public void ResolveWorkdir_UsesWorkspaceEnv_WhenSet()
    {
        var parsed = ClawHubCommand.ParseArgs(["search", "calendar"]);
        var resolved = ClawHubCommand.ResolveWorkdir(parsed, "/a/workspace", "/home/user");
        Assert.EndsWith("/a/workspace", resolved.Replace('\\', '/'));
    }

    [Fact]
    public void ResolveWorkdir_RequiresWorkspaceEnv_WhenNotProvided()
    {
        var parsed = ClawHubCommand.ParseArgs(["search", "calendar"]);
        var ex = Assert.ThrowsAny<Exception>(() => ClawHubCommand.ResolveWorkdir(parsed, null, "/home/user"));
        Assert.Contains("OPENCLAW_WORKSPACE", ex.Message);
    }

    [Fact]
    public void ResolveWorkdir_Managed_UsesDotOpenclaw()
    {
        var parsed = ClawHubCommand.ParseArgs(["--managed", "search", "calendar"]);
        var resolved = ClawHubCommand.ResolveWorkdir(parsed, null, "/home/user");
        Assert.EndsWith("/home/user/.openclaw", resolved.Replace('\\', '/'));
    }

    [Fact]
    public void BuildChildEnvironment_Default_DisablesTelemetry()
    {
        var env = ClawHubCommand.BuildChildEnvironment("/a/workspace", ClawHubCommand.TelemetryMode.Off);
        Assert.Equal("/a/workspace", env["CLAWHUB_WORKDIR"]);
        Assert.Equal("1", env["CLAWHUB_DISABLE_TELEMETRY"]);
    }

    [Fact]
    public void BuildChildEnvironment_TelemetryOn_RemovesDisableVar()
    {
        var env = ClawHubCommand.BuildChildEnvironment("/a/workspace", ClawHubCommand.TelemetryMode.On);
        Assert.Equal("/a/workspace", env["CLAWHUB_WORKDIR"]);
        Assert.True(env.ContainsKey("CLAWHUB_DISABLE_TELEMETRY"));
        Assert.Null(env["CLAWHUB_DISABLE_TELEMETRY"]);
    }
}
