using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal sealed class OpenClawHttpClient : IDisposable
{
    private readonly OpenClaw.Client.OpenClawHttpClient _inner;

    public OpenClawHttpClient(string baseUrl, string? authToken)
        => _inner = new OpenClaw.Client.OpenClawHttpClient(baseUrl, authToken);

    public async Task<OpenAiChatCompletionResponse> ChatCompletionAsync(
        OpenAiChatCompletionRequest request,
        CancellationToken cancellationToken)
        => await _inner.ChatCompletionAsync(request, cancellationToken);

    public async Task<string> StreamChatCompletionAsync(
        OpenAiChatCompletionRequest request,
        Action<string> onText,
        CancellationToken cancellationToken)
        => await _inner.StreamChatCompletionAsync(request, onText, cancellationToken);

    public void Dispose() => _inner.Dispose();
}
