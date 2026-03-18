using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;

namespace OpenClaw.Client;

public sealed class OpenClawWebSocketClient : IAsyncDisposable
{
    private readonly int _maxMessageBytes;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _rxCts;
    private Task? _rxLoop;

    public OpenClawWebSocketClient(int maxMessageBytes = 256 * 1024)
    {
        _maxMessageBytes = maxMessageBytes;
    }

    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return _ws?.State == WebSocketState.Open;
            }
        }
    }

    public event Action<string>? OnTextMessage;
    public event Action<WsServerEnvelope>? OnEnvelopeReceived;
    public event Action<string>? OnError;

    public async Task ConnectAsync(Uri wsUri, string? bearerToken, CancellationToken ct)
    {
        await DisconnectAsync(ct);

        var ws = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(bearerToken))
            ws.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");

        await ws.ConnectAsync(wsUri, ct);

        var rxCts = new CancellationTokenSource();
        var rxLoop = Task.Run(() => ReceiveLoopAsync(ws, rxCts.Token), rxCts.Token);

        lock (_stateLock)
        {
            _ws = ws;
            _rxCts = rxCts;
            _rxLoop = rxLoop;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        ClientWebSocket? ws;
        CancellationTokenSource? rxCts;
        Task? rxLoop;

        lock (_stateLock)
        {
            ws = _ws;
            rxCts = _rxCts;
            rxLoop = _rxLoop;
            _ws = null;
            _rxCts = null;
            _rxLoop = null;
        }

        try
        {
            if (rxCts is not null)
                await rxCts.CancelAsync();
        }
        catch
        {
        }

        if (rxLoop is not null)
        {
            try { await rxLoop.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
        }

        if (rxCts is not null)
        {
            try { rxCts.Dispose(); } catch { }
        }

        if (ws is null)
            return;

        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing", ct);
        }
        catch
        {
        }
        finally
        {
            ws.Dispose();
        }
    }

    public async Task SendUserMessageAsync(string text, string? messageId, string? replyToMessageId, CancellationToken ct)
    {
        ClientWebSocket? ws;
        lock (_stateLock)
        {
            ws = _ws;
        }

        if (ws is null || ws.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected.");

        var payload = JsonSerializer.Serialize(
            new WsClientEnvelope
            {
                Type = "user_message",
                Text = text,
                MessageId = messageId,
                ReplyToMessageId = replyToMessageId
            },
            CoreJsonContext.Default.WsClientEnvelope);

        var bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length > _maxMessageBytes)
            throw new InvalidOperationException("Message too large.");

        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var writer = new ArrayBufferWriter<byte>(16 * 1024);

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                writer.Clear();
                WebSocketReceiveResult result;

                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    if (writer.WrittenCount + result.Count > _maxMessageBytes)
                        throw new InvalidOperationException("Inbound message too large.");

                    writer.Write(buffer.AsSpan(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var text = Encoding.UTF8.GetString(writer.WrittenSpan);
                OnTextMessage?.Invoke(text);

                try
                {
                    var envelope = JsonSerializer.Deserialize(text, CoreJsonContext.Default.WsServerEnvelope);
                    if (envelope is not null)
                        OnEnvelopeReceived?.Invoke(envelope);
                }
                catch
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync(CancellationToken.None); } catch { }
        _sendLock.Dispose();
    }
}
