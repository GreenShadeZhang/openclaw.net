using Microsoft.AspNetCore.Http;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class BrowserSessionAuthServiceTests
{
    [Fact]
    public void TryAuthorize_WithCookieAndCsrf_ReturnsTrue()
    {
        var service = new BrowserSessionAuthService(new GatewayConfig());
        var ticket = service.Create(remember: true);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{BrowserSessionAuthService.CookieName}={ticket.SessionId}";
        ctx.Request.Headers[BrowserSessionAuthService.CsrfHeaderName] = ticket.CsrfToken;

        var ok = service.TryAuthorize(ctx, requireCsrf: true, out var validated);

        Assert.True(ok);
        Assert.NotNull(validated);
        Assert.Equal(ticket.SessionId, validated!.SessionId);
    }

    [Fact]
    public void TryAuthorize_WithoutCsrfForMutation_ReturnsFalse()
    {
        var service = new BrowserSessionAuthService(new GatewayConfig());
        var ticket = service.Create(remember: false);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{BrowserSessionAuthService.CookieName}={ticket.SessionId}";

        var ok = service.TryAuthorize(ctx, requireCsrf: true, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Create_UsesUpdatedConfigLifetimes()
    {
        var config = new GatewayConfig();
        config.Security.BrowserRememberDays = 1;
        var service = new BrowserSessionAuthService(config);

        var shortTicket = service.Create(remember: true);
        config.Security.BrowserRememberDays = 10;
        var longTicket = service.Create(remember: true);

        Assert.True(longTicket.ExpiresAtUtc > shortTicket.ExpiresAtUtc.AddDays(5));
    }

    [Fact]
    public void Create_GeneratesHexCsrfTokens()
    {
        var service = new BrowserSessionAuthService(new GatewayConfig());

        var first = service.Create(remember: false);
        var second = service.Create(remember: false);

        Assert.Matches("^[0-9A-F]{64}$", first.CsrfToken);
        Assert.Matches("^[0-9A-F]{64}$", second.CsrfToken);
        Assert.NotEqual(first.CsrfToken, second.CsrfToken);
    }
}
