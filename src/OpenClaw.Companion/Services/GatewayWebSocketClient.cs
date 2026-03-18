namespace OpenClaw.Companion.Services;

public sealed class GatewayWebSocketClient : IAsyncDisposable
{
    private readonly OpenClaw.Client.OpenClawWebSocketClient _inner;

    public GatewayWebSocketClient(int maxMessageBytes = 256 * 1024)
    {
        _inner = new OpenClaw.Client.OpenClawWebSocketClient(maxMessageBytes);
        _inner.OnTextMessage += text => OnTextMessage?.Invoke(text);
        _inner.OnError += error => OnError?.Invoke(error);
    }

    public bool IsConnected
        => _inner.IsConnected;

    public event Action<string>? OnTextMessage;
    public event Action<string>? OnError;

    public async Task ConnectAsync(Uri wsUri, string? bearerToken, CancellationToken ct)
        => await _inner.ConnectAsync(wsUri, bearerToken, ct);

    public async Task DisconnectAsync(CancellationToken ct)
        => await _inner.DisconnectAsync(ct);

    public async Task SendUserMessageAsync(string text, string? messageId, string? replyToMessageId, CancellationToken ct)
        => await _inner.SendUserMessageAsync(text, messageId, replyToMessageId, ct);

    public async ValueTask DisposeAsync()
    {
        try { await _inner.DisposeAsync(); } catch { }
    }
}
