using System;
using System.Collections;
using System.Reflection;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;

namespace OpenClaw.Gateway.Extensions;

public static class GatewaySecurityExtensions
{
    public static void EnforcePublicBindHardening(GatewayConfig config, bool isNonLoopbackBind)
    {
        if (!isNonLoopbackBind)
            return;

        var toolingUnsafe =
            config.Tooling.AllowShell ||
            config.Tooling.AllowedReadRoots.Contains("*", StringComparer.Ordinal) ||
            config.Tooling.AllowedWriteRoots.Contains("*", StringComparer.Ordinal);

        if (toolingUnsafe && !config.Security.AllowUnsafeToolingOnPublicBind)
        {
            throw new InvalidOperationException(
                "Refusing to start with unsafe tooling settings on a non-loopback bind. " +
                "Set OpenClaw:Tooling:AllowShell=false and restrict AllowedReadRoots/AllowedWriteRoots, " +
                "or explicitly opt in via OpenClaw:Security:AllowUnsafeToolingOnPublicBind=true.");
        }

        if (config.Plugins.Enabled && !config.Security.AllowPluginBridgeOnPublicBind)
        {
            throw new InvalidOperationException(
                "Refusing to start with the JS plugin bridge enabled on a non-loopback bind. " +
                "Disable OpenClaw:Plugins:Enabled, or explicitly opt in via OpenClaw:Security:AllowPluginBridgeOnPublicBind=true.");
        }

        if (config.Channels.WhatsApp.Enabled)
        {
            if (string.Equals(config.Channels.WhatsApp.Type, "official", StringComparison.OrdinalIgnoreCase))
            {
                if (!config.Channels.WhatsApp.ValidateSignature)
                {
                    throw new InvalidOperationException(
                        "Refusing to start with WhatsApp official webhooks on a non-loopback bind without signature validation. " +
                        "Set OpenClaw:Channels:WhatsApp:ValidateSignature=true and configure WebhookAppSecretRef.");
                }

                var appSecret = SecretResolver.Resolve(config.Channels.WhatsApp.WebhookAppSecretRef)
                    ?? config.Channels.WhatsApp.WebhookAppSecret;
                if (string.IsNullOrWhiteSpace(appSecret))
                {
                    throw new InvalidOperationException(
                        "Refusing to start with WhatsApp official webhooks on a non-loopback bind without a webhook app secret. " +
                        "Set OpenClaw:Channels:WhatsApp:WebhookAppSecretRef (recommended) or WebhookAppSecret.");
                }
            }
            else if (string.Equals(config.Channels.WhatsApp.Type, "bridge", StringComparison.OrdinalIgnoreCase))
            {
                var bridgeToken = SecretResolver.Resolve(config.Channels.WhatsApp.BridgeTokenRef)
                    ?? config.Channels.WhatsApp.BridgeToken;
                if (string.IsNullOrWhiteSpace(bridgeToken))
                {
                    throw new InvalidOperationException(
                        "Refusing to start with WhatsApp bridge webhooks on a non-loopback bind without inbound authentication. " +
                        "Set OpenClaw:Channels:WhatsApp:BridgeTokenRef (recommended) or BridgeToken.");
                }
            }
        }

        if (!config.Security.AllowRawSecretRefsOnPublicBind)
        {
            var rawSecretPaths = FindRawSecretRefs(config);
            if (rawSecretPaths.Count > 0)
            {
                var sample = string.Join(", ", rawSecretPaths.Take(3));
                var suffix = rawSecretPaths.Count > 3 ? ", ..." : "";
                throw new InvalidOperationException(
                    "Refusing to start with a raw: secret ref on a non-loopback bind. " +
                    $"Detected in: {sample}{suffix}. " +
                    "Use env:... / OS keychain storage, or explicitly opt in via OpenClaw:Security:AllowRawSecretRefsOnPublicBind=true.");
            }
        }
    }

    private static IReadOnlyList<string> FindRawSecretRefs(object root)
    {
        var hits = new List<string>(capacity: 8);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        VisitForRawRefs(root, "OpenClaw", hits, visited);
        return hits;
    }

    private static void VisitForRawRefs(
        object? value,
        string path,
        List<string> hits,
        HashSet<object> visited)
    {
        if (value is null || hits.Count >= 8)
            return;

        if (value is string s)
        {
            if (SecretResolver.IsRawRef(s) && LooksLikeSecretPath(path))
                hits.Add(path);
            return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid))
        {
            return;
        }

        if (!type.IsValueType && !visited.Add(value))
            return;

        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                VisitForRawRefs(entry.Value, $"{path}[{entry.Key}]", hits, visited);
                if (hits.Count >= 8)
                    return;
            }
            return;
        }

        if (value is IEnumerable sequence)
        {
            var idx = 0;
            foreach (var item in sequence)
            {
                VisitForRawRefs(item, $"{path}[{idx++}]", hits, visited);
                if (hits.Count >= 8)
                    return;
            }
            return;
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length != 0)
                continue;

            object? child;
            try
            {
                child = prop.GetValue(value);
            }
            catch
            {
                continue;
            }

            VisitForRawRefs(child, $"{path}:{prop.Name}", hits, visited);
            if (hits.Count >= 8)
                return;
        }
    }

    private static bool LooksLikeSecretPath(string path)
    {
        return path.Contains("Ref", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("ApiKey", StringComparison.OrdinalIgnoreCase);
    }
}
