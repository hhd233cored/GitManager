using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitManager.Services;

public static class LocalToolBridge
{
    public static string DefaultPipeName => "gitmanager-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..20].ToLowerInvariant();
    private const int MaximumMessageBytes = 512 * 1024;
    public static async Task<string> ReadAsync(Stream stream, CancellationToken ct)
    {
        using var message = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("连接已关闭。");
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            var length = newline < 0 ? read : newline;
            if (message.Length + length > MaximumMessageBytes) throw new IOException("工具消息超过大小限制。");
            message.Write(buffer, 0, length);
            if (newline >= 0) return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
        }
    }
    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(ToolJson.Serialize(message) + '\n');
        if (bytes.Length > MaximumMessageBytes) throw new IOException("工具结果超过大小限制，请缩小查询范围。");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

public sealed class LocalToolBridgeServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    public event Action<string>? Failed;
    public LocalToolBridgeServer(Func<ToolCall, CancellationToken, Task<string>> invoke, string? pipeName = null)
    {
        _loop = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await using var server = new NamedPipeServerStream(pipeName ?? LocalToolBridge.DefaultPipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(3));
                    try
                    {
                        var json = await LocalToolBridge.ReadAsync(server, timeout.Token).ConfigureAwait(false);
                        var call = JsonSerializer.Deserialize<ToolCall>(json, ToolJson.Options) ?? throw new ArgumentException("工具请求为空。");
                        ToolResponse response;
                        try { response = new(Result: await invoke(call, timeout.Token).ConfigureAwait(false)); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { response = new(Error: ToolErrors.Message(ex)); }
                        await LocalToolBridge.WriteAsync(server, response, timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or ArgumentException) { }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { Failed?.Invoke(ex.Message); }
        });
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}

public sealed class LocalToolBridgeClient(string? pipeName = null)
{
    public async Task<string> InvokeAsync(ToolCall call, CancellationToken ct = default)
    {
        await using var client = new NamedPipeClientStream(".", pipeName ?? LocalToolBridge.DefaultPipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await client.ConnectAsync(5000, ct).ConfigureAwait(false); }
        catch (TimeoutException) { throw new InvalidOperationException("请先启动 GitManager，在助手设置中启用 MCP，并打开允许访问的仓库。"); }
        await LocalToolBridge.WriteAsync(client, call, ct).ConfigureAwait(false);
        var json = await LocalToolBridge.ReadAsync(client, ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<ToolResponse>(json, ToolJson.Options) ?? throw new IOException("工具响应为空。");
        if (response.Error is not null) throw new InvalidOperationException(response.Error);
        return response.Result ?? "null";
    }
}
