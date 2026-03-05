using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// A native interactive headless browser tool leveraging Microsoft.Playwright.
/// Allows agents to query dynamic SPAs, fill forms, and click elements.
/// </summary>
public sealed class BrowserTool : ITool, IAsyncDisposable
{
    private readonly ToolingConfig _config;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public BrowserTool(ToolingConfig config)
    {
        _config = config;
    }

    public string Name => "browser";
    
    public string Description => 
        "An interactive headless browser. Enables navigation to JS-heavy sites, " +
        "clicking elements, filling inputs, taking screenshots, and extracting text or DOM data.";
    
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "action": { 
              "type": "string", 
              "enum": ["goto", "click", "fill", "get_text", "evaluate", "screenshot"],
              "description": "The browser action to perform."
            },
            "url": { "type": "string", "description": "URL for goto action." },
            "selector": { "type": "string", "description": "CSS/XPath selector for click, fill, or get_text." },
            "value": { "type": "string", "description": "Text to type for fill action." },
            "script": { "type": "string", "description": "JS script to evaluate. Returns string result." }
          },
          "required": ["action"]
        }
        """;

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        
        await _lock.WaitAsync(ct);
        try
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BrowserTool));

            if (_initialized) return;

            // Ensure Chromium is installed locally before proceeding
            var exitCode = await Task.Run(() => Microsoft.Playwright.Program.Main(["install", "chromium"]), ct);
            if (exitCode != 0)
                throw new InvalidOperationException($"Playwright CLI install failed with exit code {exitCode}");

            ct.ThrowIfCancellationRequested();

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _config.BrowserHeadless,
                Timeout = _config.BrowserTimeoutSeconds * 1000
            });
            _page = await _browser.NewPageAsync();
            
            _initialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        // Setup playwright lazily
        await EnsureInitializedAsync(ct);

        await _lock.WaitAsync(ct);
        try
        {
            if (_disposed)
                return "Error: Browser tool is disposed.";

            if (_page is null)
                return "Error: Browser not initialized.";

            using var args = JsonDocument.Parse(argumentsJson);
            var action = args.RootElement.GetProperty("action").GetString();

            using var cancellationRegistration = ct.Register(() =>
            {
                var page = _page;
                if (page is not null)
                {
                    var ignoredTask = ClosePageBestEffortAsync(page);
                }
            });

            switch (action)
            {
                case "goto":
                {
                    var url = args.RootElement.GetProperty("url").GetString()!;
                    await WithCancellationAsync(
                        _page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.Load }), ct);
                    var title = await WithCancellationAsync(_page.TitleAsync(), ct);
                    return $"Navigated to {url}. Title: '{title}'";
                }

                case "click":
                {
                    var cSelector = args.RootElement.GetProperty("selector").GetString()!;
                    await WithCancellationAsync(_page.ClickAsync(cSelector), ct);
                    return $"Clicked selector: {cSelector}";
                }

                case "fill":
                {
                    var fSelector = args.RootElement.GetProperty("selector").GetString()!;
                    var value = args.RootElement.GetProperty("value").GetString()!;
                    await WithCancellationAsync(_page.FillAsync(fSelector, value), ct);
                    return $"Filled {fSelector} with provided value.";
                }

                case "get_text":
                {
                    if (args.RootElement.TryGetProperty("selector", out var textSel) && !string.IsNullOrWhiteSpace(textSel.GetString()))
                    {
                        var content = await WithCancellationAsync(_page.TextContentAsync(textSel.GetString()!), ct);
                        return content ?? "No text found for selector.";
                    }
                    
                    var body = await WithCancellationAsync(_page.TextContentAsync("body"), ct);
                    return body ?? "Body is empty.";
                }

                case "evaluate":
                {
                    if (!_config.AllowBrowserEvaluate)
                        return "Error: Browser evaluate is disabled by configuration (Tooling.AllowBrowserEvaluate=false).";

                    var script = args.RootElement.GetProperty("script").GetString()!;
                    // Run script and serialize string
                    var resultElement = await WithCancellationAsync(_page.EvaluateAsync<JsonElement>(script), ct);
                    return resultElement.ToString();
                }

                case "screenshot":
                {
                    var bytes = await WithCancellationAsync(
                        _page.ScreenshotAsync(new PageScreenshotOptions { FullPage = true }), ct);
                    return $"Screenshot taken. Base64: {Convert.ToBase64String(bytes)}";
                }

                default:
                    return $"Error: Unknown action '{action}'";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "Browser action cancelled.";
        }
        catch (Exception ex)
        {
            return $"Browser action failed: {ex.Message}";
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_page != null)
                await _page.CloseAsync();
            if (_browser != null)
                await _browser.CloseAsync();

            _playwright?.Dispose();
            _page = null;
            _browser = null;
            _playwright = null;
            _initialized = false;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task ClosePageBestEffortAsync(IPage page)
    {
        try { await page.CloseAsync(); } catch { }
    }

    private static async Task WithCancellationAsync(Task task, CancellationToken ct)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => gate.TrySetResult());

        if (task == await Task.WhenAny(task, gate.Task))
        {
            await task;
            return;
        }

        throw new OperationCanceledException(ct);
    }

    private static async Task<T> WithCancellationAsync<T>(Task<T> task, CancellationToken ct)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => gate.TrySetResult());

        if (task == await Task.WhenAny(task, gate.Task))
            return await task;

        throw new OperationCanceledException(ct);
    }
}
