using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Boukensha.Core.Mcp;

public sealed class McpClient(
    string name,
    string command,
    IReadOnlyList<string>? args = null,
    IReadOnlyDictionary<string, string>? env = null) : IAsyncDisposable
{
    public sealed class McpException(string message) : Exception(message);

    private const string ProtocolVersion = "2024-11-05";

    public string Name { get; } = name;

    private Process? _process;
    private int _nextId;
    private readonly StringBuilder _stderrBuffer = new();
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args ?? []) startInfo.ArgumentList.Add(arg);
        foreach (var (key, value) in env ?? new Dictionary<string, string>()) startInfo.Environment[key] = value;

        try
        {
            _process = Process.Start(startInfo) ?? throw new McpException($"failed to start MCP server '{Name}' ({command})");
        }
        catch (Exception e) when (e is not McpException)
        {
            throw new McpException($"failed to start MCP server '{Name}' ({command}): {e.Message}");
        }

        _ = Task.Run(DrainStderrAsync, cancellationToken);

        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "boukensha", ["version"] = "0.1.0" },
        }, cancellationToken);
        await WriteAsync(JsonRpc.BuildNotification("notifications/initialized", new JsonObject()), cancellationToken);
    }

    public async Task<JsonArray> ToolsListAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("tools/list", new JsonObject(), cancellationToken);
        return result?["tools"] as JsonArray ?? [];
    }

    public async Task<string> ToolsCallAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = JsonUtil.ToJsonNode(arguments),
        }, cancellationToken) ?? new JsonObject();

        var text = JsonRpc.ExtractToolText(result);
        if (JsonRpc.IsToolError(result))
        {
            throw new McpException($"tool '{toolName}' on '{Name}' failed: {text}");
        }
        return text;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null) return;
        try { _process.StandardInput.Close(); } catch { /* already closed */ }
        try { _process.StandardOutput.Close(); } catch { /* already closed */ }
        try
        {
            if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
        }
        catch { /* best-effort shutdown */ }
        _process.Dispose();
        _process = null;
    }

    private async Task<JsonObject?> RequestAsync(string method, JsonNode @params, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        await WriteAsync(JsonRpc.BuildRequest(id, method, @params), cancellationToken);
        var response = await ReadResponseAsync(id, cancellationToken);
        if (response["error"] is JsonObject error)
        {
            throw new McpException($"{Name}: {error["message"]}");
        }
        return response["result"] as JsonObject;
    }

    private async Task WriteAsync(string line, CancellationToken cancellationToken)
    {
        if (_process is null) throw new McpException($"MCP server '{Name}' has not been started");
        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(line);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new McpException($"MCP server '{Name}' closed its input unexpectedly: {e.Message}");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task<JsonObject> ReadResponseAsync(int expectedId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await _process!.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new McpException($"MCP server '{Name}' closed its output unexpectedly (stderr: {_stderrBuffer})");
            }
            if (JsonRpc.TryParseResponse(line, expectedId, out var message))
            {
                return message!;
            }
        }
    }

    private async Task DrainStderrAsync()
    {
        if (_process is null) return;
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync()) is not null)
            {
                lock (_stderrBuffer) _stderrBuffer.AppendLine(line);
            }
        }
        catch { /* process ended */ }
    }
}
