using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenClaw.Cli;

internal static class ClawHubCommand
{
    private const string EnvWorkspace = "OPENCLAW_WORKSPACE";
    private const string EnvClawHubWorkdir = "CLAWHUB_WORKDIR";
    private const string EnvClawHubDisableTelemetry = "CLAWHUB_DISABLE_TELEMETRY";

    internal enum TelemetryMode
    {
        Off,
        On
    }

    internal sealed record WrapperArgs(
        bool ShowHelp,
        string? Workdir,
        bool UseManaged,
        TelemetryMode Telemetry,
        string[] ForwardArgs);

    internal sealed record InvocationSpec(
        string FileName,
        string[] Arguments,
        string Workdir,
        TelemetryMode Telemetry);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var parsed = ParseArgs(args);
            if (parsed.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var workspaceEnv = Environment.GetEnvironmentVariable(EnvWorkspace);
            var workdir = ResolveWorkdir(parsed, workspaceEnv, home);
            EnsureDirectoryExists(workdir);

            var spec = BuildInvocationSpec(parsed, workdir);
            return await RunClawHubAsync(spec);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Run: openclaw clawhub --help");
            return 2;
        }
    }

    internal static WrapperArgs ParseArgs(string[] args)
    {
        var showHelp = false;
        string? workdir = null;
        var useManaged = false;
        var telemetry = TelemetryMode.Off;

        var forward = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];

            if (a is "-h" or "--help")
            {
                showHelp = true;
                continue;
            }

            if (a == "--")
            {
                forward.AddRange(args[(i + 1)..]);
                break;
            }

            if (a == "--workdir")
            {
                if (i + 1 >= args.Length)
                    throw new UsageException("Missing value for --workdir");
                workdir = args[++i];
                continue;
            }

            if (a == "--managed")
            {
                useManaged = true;
                continue;
            }

            if (a == "--telemetry")
            {
                if (i + 1 >= args.Length)
                    throw new UsageException("Missing value for --telemetry (expected: on|off)");
                var v = args[++i].Trim().ToLowerInvariant();
                telemetry = v switch
                {
                    "on" => TelemetryMode.On,
                    "off" => TelemetryMode.Off,
                    _ => throw new UsageException("Invalid value for --telemetry (expected: on|off)")
                };
                continue;
            }

            if (a.StartsWith("--", StringComparison.Ordinal))
                throw new UsageException($"Unknown option: {a} (use -- to forward flags to ClawHub)");

            // First non-wrapper token => start forwarding, including this one.
            forward.AddRange(args[i..]);
            break;
        }

        if (useManaged && !string.IsNullOrWhiteSpace(workdir))
            throw new UsageException("Cannot use --managed together with --workdir");

        return new WrapperArgs(showHelp, workdir, useManaged, telemetry, forward.ToArray());
    }

    internal static string ResolveWorkdir(WrapperArgs args, string? openclawWorkspace, string userProfilePath)
    {
        if (!string.IsNullOrWhiteSpace(args.Workdir))
            return Path.GetFullPath(args.Workdir);

        if (args.UseManaged)
        {
            return Path.Combine(userProfilePath, ".openclaw");
        }

        if (!string.IsNullOrWhiteSpace(openclawWorkspace))
            return Path.GetFullPath(openclawWorkspace);

        throw new UsageException($"Missing {EnvWorkspace}. Set {EnvWorkspace} or pass --workdir or --managed.");
    }

    internal static InvocationSpec BuildInvocationSpec(WrapperArgs args, string resolvedWorkdir)
    {
        if (args.ForwardArgs.Length == 0)
            throw new UsageException("Missing ClawHub args. Example: openclaw clawhub search \"calendar\"");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Prefer cmd.exe so npm-installed clawhub.cmd shims work.
            var cmdArgs = new List<string> { "/d", "/s", "/c", "clawhub" };
            cmdArgs.AddRange(args.ForwardArgs);
            return new InvocationSpec("cmd.exe", cmdArgs.ToArray(), resolvedWorkdir, args.Telemetry);
        }

        return new InvocationSpec("clawhub", args.ForwardArgs, resolvedWorkdir, args.Telemetry);
    }

    private static async Task<int> RunClawHubAsync(InvocationSpec spec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            UseShellExecute = false
        };

        foreach (var a in spec.Arguments)
            startInfo.ArgumentList.Add(a);

        var childEnv = BuildChildEnvironment(spec.Workdir, spec.Telemetry);
        foreach (var (k, v) in childEnv)
        {
            if (v is null)
                startInfo.Environment.Remove(k);
            else
                startInfo.Environment[k] = v;
        }

        try
        {
            using var p = Process.Start(startInfo);
            if (p is null)
                throw new UsageException("Failed to start ClawHub CLI.");

            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (IsCommandNotFound(ex))
        {
            PrintMissingClawHub(spec.FileName);
            return 127;
        }
    }

    internal static Dictionary<string, string?> BuildChildEnvironment(string workdir, TelemetryMode telemetry)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [EnvClawHubWorkdir] = workdir
        };

        if (telemetry == TelemetryMode.Off)
        {
            env[EnvClawHubDisableTelemetry] = "1";
        }
        else
        {
            // Remove from child env even if it's set in the parent shell.
            env[EnvClawHubDisableTelemetry] = null;
        }

        return env;
    }

    private static bool IsCommandNotFound(Win32Exception ex)
    {
        // Cross-platform "file not found" is typically NativeErrorCode 2.
        return ex.NativeErrorCode == 2;
    }

    private static void EnsureDirectoryExists(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            throw new UsageException($"Failed to create workdir '{path}': {ex.Message}");
        }
    }

    private static void PrintMissingClawHub(string attemptedExecutable)
    {
        Console.Error.WriteLine($"ClawHub CLI not found on PATH (tried '{attemptedExecutable}').");
        Console.Error.WriteLine("Install it with one of:");
        Console.Error.WriteLine("  npm i -g clawhub");
        Console.Error.WriteLine("  pnpm add -g clawhub");
        Console.Error.WriteLine("Then re-run: openclaw clawhub -- --help");
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            $"""
            openclaw clawhub — ClawHub CLI wrapper

            Usage:
              openclaw clawhub [wrapper options] [--] <clawhub args...>

            Wrapper options:
              --help, -h          Show this help.
              --workdir <path>    Set {EnvClawHubWorkdir} for the child process.
              --managed           Use ~/.openclaw as workdir (installs into ~/.openclaw/skills).
              --telemetry on|off  Default: off. When off, sets {EnvClawHubDisableTelemetry}=1 for the child.
              --                 Forward all remaining args verbatim to ClawHub.

            Workdir resolution (when no --workdir/--managed):
              - Uses {EnvWorkspace} as the workdir.

            Examples:
              # Forward --help to ClawHub itself:
              openclaw clawhub -- --help

              # Search and install into $OPENCLAW_WORKSPACE/skills:
              openclaw clawhub search "calendar"
              openclaw clawhub install <skill-slug>

              # Install into ~/.openclaw/skills:
              openclaw clawhub --managed install <skill-slug>
            """);
    }

    private sealed class UsageException(string message) : Exception(message);
}
