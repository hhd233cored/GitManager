using System.Text.RegularExpressions;

namespace GitManager.Services;

public static partial class ToolErrors
{
    public static string Message(Exception error) => Redact(error.Message);
    public static string Redact(string message)
    {
        message = UrlCredentials().Replace(message, "$1***@");
        message = QueryCredentials().Replace(message, "$1***");
        return BearerCredentials().Replace(message, "$1***");
    }
    [GeneratedRegex(@"(?i)(https?://)[^\s/@]+@")]
    private static partial Regex UrlCredentials();
    [GeneratedRegex("(?i)([?&](?:api[_-]?key|access[_-]?token|token|key)=)[^&\\s\"']+")]
    private static partial Regex QueryCredentials();
    [GeneratedRegex(@"(?i)(Bearer\s+)[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex BearerCredentials();
}
