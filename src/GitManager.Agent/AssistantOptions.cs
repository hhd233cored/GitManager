using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace GitManager.Agent;

public sealed record AssistantOptions(string BaseUrl = "https://api.openai.com/v1", string Model = "", bool IncludeDiff = true,
    bool EnableMcp = false, bool UseStreaming = true, int MaxOutputTokens = 2000)
{
    public Uri Endpoint
    {
        get
        {
            if (!Uri.TryCreate(BaseUrl.TrimEnd('/') + '/', UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
                || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                throw new ArgumentException("服务地址应为 HTTPS API 基础地址；本机服务也可使用 http://localhost 或 http://127.0.0.1。");
            return uri;
        }
    }
    public void Validate()
    {
        _ = Endpoint;
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 200) throw new ArgumentException("请填写模型名称。");
    }
}

public static class AssistantClientFactory
{
    public static IChatClient Create(AssistantOptions options, string? apiKey)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(apiKey) && !options.Endpoint.IsLoopback) throw new ArgumentException("请在助手设置中填写此服务的 API Key。");
        return new OpenAI.Chat.ChatClient(options.Model.Trim(), new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey),
            new OpenAIClientOptions { Endpoint = options.Endpoint }).AsIChatClient();
    }
}
