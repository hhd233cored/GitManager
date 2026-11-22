using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GitManager.Contracts;

namespace GitManager.App.Infrastructure;

internal sealed class CloudAccountException(string message) : Exception(message);

internal sealed class CloudAccountClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private Session? _session;

    public string ServerAddress { get; private set; } = Environment.GetEnvironmentVariable("GITMANAGER_SERVER_URL") ?? "http://127.0.0.1:5187";
    public string? Email => _session?.Email;

    public async Task SignInAsync(string address, string email, string password, bool register, CancellationToken cancellationToken)
    {
        var trimmedAddress = address.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmedAddress + '/', UriKind.Absolute, out var server)
            || (server.Scheme != "https" && !(server.Scheme == "http" && server.IsLoopback))
            || server.UserInfo.Length > 0 || server.Query.Length > 0 || server.Fragment.Length > 0)
            throw new CloudAccountException("请输入 HTTPS 后端地址。本机服务可使用 http://127.0.0.1:5187。");
        email = email.Trim();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            throw new CloudAccountException("请输入邮箱和密码。");

        var credentials = new Credentials(email, password);
        if (register)
        {
            using var registration = await SendAsync(HttpMethod.Post, server, "api/auth/register", credentials, null, cancellationToken);
            await VerifyAsync(registration, null, cancellationToken);
        }
        using var response = await SendAsync(HttpMethod.Post, server, "api/auth/login", credentials, null, cancellationToken);
        await VerifyAsync(response, null, cancellationToken);
        var token = await ReadAsync<LoginToken>(response, cancellationToken);
        if (string.IsNullOrWhiteSpace(token.AccessToken)) throw new CloudAccountException("后端没有返回有效的登录令牌。");
        cancellationToken.ThrowIfCancellationRequested();
        ServerAddress = trimmedAddress;
        _session = new(server, email, token.AccessToken);
    }

    public void SignOut() => _session = null;

    public async Task<BackupSummary[]> ListBackupsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, "api/backups", null, cancellationToken);
        return await ReadAsync<BackupSummary[]>(response, cancellationToken);
    }

    public async Task UploadAsync(string id, BackupContent content, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Put, BackupRoute(id), content, cancellationToken);
    }

    public async Task<DownloadedBackup> DownloadAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, BackupRoute(id), null, cancellationToken);
        var backup = await ReadAsync<DownloadedBackup>(response, cancellationToken);
        if (backup.Summary?.ConversationId != id || string.IsNullOrWhiteSpace(backup.Summary.Title)
            || backup.Summary.RepositoryName is null || backup.Summary.Branch is null
            || backup.Messages is not { Length: > 0 } || backup.Messages.Any(message =>
                message is null || message.Role is not ("user" or "assistant") || message.Content is null))
            throw new CloudAccountException("后端返回的会话备份无效。");
        return backup;
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Delete, BackupRoute(id), null, cancellationToken);
    }

    public async Task<SyncedPreferences?> DownloadPreferencesAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, "api/me/preferences", null, cancellationToken, allowMissing: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var preferences = await ReadAsync<SyncedPreferences>(response, cancellationToken);
        if (preferences.Model is null || preferences.Model.Length > 200 || !SyncedPreferences.IsValidTokenLimit(preferences.MaxOutputTokens))
            throw new CloudAccountException("后端返回的助手偏好无效。");
        return preferences;
    }

    public async Task UploadPreferencesAsync(SyncedPreferences preferences, CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Put, "api/me/preferences", preferences, cancellationToken);
    }

    private static string BackupRoute(string id) => $"api/backups/{Uri.EscapeDataString(id)}";

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string route, object? body,
        CancellationToken cancellationToken, bool allowMissing = false)
    {
        var session = _session ?? throw new CloudAccountException("请先登录账号。");
        var response = await SendAsync(method, session.Server, route, body, session.Token, cancellationToken);
        try
        {
            if (!allowMissing || response.StatusCode != HttpStatusCode.NotFound)
                await VerifyAsync(response, session, cancellationToken);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri server, string route, object? body,
        string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(server, route));
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try { return await Http.SendAsync(request, cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new CloudAccountException("连接后端超时，请重试。"); }
        catch (HttpRequestException)
        { throw new CloudAccountException("无法连接后端，请检查服务是否已启动和地址是否正确。"); }
    }

    private async Task VerifyAsync(HttpResponseMessage response, Session? session, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            if (session is null) throw new CloudAccountException("登录失败，请检查邮箱和密码后重试。");
            if (ReferenceEquals(session, _session)) SignOut();
            throw new CloudAccountException("登录已失效，请重新登录。");
        }
        if (response.StatusCode == HttpStatusCode.BadRequest && session is not null)
        {
            try
            {
                var problem = await response.Content.ReadFromJsonAsync<ValidationProblem>(cancellationToken);
                var message = problem?.Errors?.Values.SelectMany(entries => entries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(message) && message.Length <= 240) throw new CloudAccountException(message);
            }
            catch (JsonException) { }
        }
        var messageText = response.StatusCode switch
        {
            HttpStatusCode.BadRequest when session is null => "注册失败。请检查邮箱是否已注册，密码是否包含大小写字母、数字和符号。",
            HttpStatusCode.BadRequest => "后端拒绝了本次请求，请检查输入。",
            HttpStatusCode.NotFound => "后端找不到这项内容，请刷新列表或检查服务地址。",
            HttpStatusCode.RequestEntityTooLarge => "这段会话超过后端允许的备份大小。",
            _ => $"后端请求失败（HTTP {(int)response.StatusCode}）。"
        };
        throw new CloudAccountException(messageText);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) where T : class
    {
        try { return await response.Content.ReadFromJsonAsync<T>(cancellationToken) ?? throw new CloudAccountException("后端响应为空。"); }
        catch (JsonException) { throw new CloudAccountException("后端响应的格式无效。"); }
    }

    private sealed record Credentials(string Email, string Password);
    private sealed record LoginToken(string AccessToken);
    private sealed record ValidationProblem(Dictionary<string, string[]> Errors);
    private sealed record Session(Uri Server, string Email, string Token);
}
