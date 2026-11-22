using System.Text.Json.Serialization;

namespace GitManager.Contracts;

public sealed record BackupMessage(string Role, string Content);

public sealed record BackupContent(string Title, string RepositoryName, string Branch, BackupMessage[] Messages);

public sealed record BackupSummary(string ConversationId, string Title, string RepositoryName, string Branch, DateTimeOffset BackedUpAt)
{
    [JsonIgnore] public string Scope => string.IsNullOrEmpty(Branch) ? RepositoryName : $"{RepositoryName} · {Branch}";
    [JsonIgnore] public DateTime LocalBackedUpAt => BackedUpAt.LocalDateTime;
}

public sealed record DownloadedBackup(BackupSummary Summary, BackupMessage[] Messages);

public sealed record SyncedPreferences(string Model, bool UseStreaming, int MaxOutputTokens)
{
    public static bool IsValidTokenLimit(int value) => value is >= 256 and <= 8000;
}
