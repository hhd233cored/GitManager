using System.Security.Claims;
using GitManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GitManager.Server;

internal static class CloudEndpoints
{
    public static void MapCloudEndpoints(this RouteGroupBuilder account)
    {
        var backups = account.MapGroup("/backups");
        backups.MapGet("", ListBackupsAsync);
        backups.MapGet("/{conversationId:guid}", ReadBackupAsync);
        backups.MapPut("/{conversationId:guid}", SaveBackupAsync);
        backups.MapDelete("/{conversationId:guid}", DeleteBackupAsync);
        account.MapGet("/me/preferences", ReadPreferencesAsync);
        account.MapPut("/me/preferences", SavePreferencesAsync);
    }

    private static async Task<IResult> ListBackupsAsync(ClaimsPrincipal user, CloudDbContext db, CancellationToken cancellationToken)
    {
        var backups = await OwnedBackups(db, user).OrderByDescending(backup => backup.BackedUpAt).ThenByDescending(backup => backup.Id)
            .ToListAsync(cancellationToken);
        return Results.Ok(backups.Select(ToSummary).ToArray());
    }

    private static async Task<IResult> ReadBackupAsync(Guid conversationId, ClaimsPrincipal user, CloudDbContext db, CancellationToken cancellationToken)
    {
        var backup = await OwnedBackups(db, user).Include(item => item.Messages)
            .SingleOrDefaultAsync(item => item.ClientConversationId == conversationId.ToString("N"), cancellationToken);
        if (backup is null) return Results.NotFound();
        return Results.Ok(new DownloadedBackup(ToSummary(backup), backup.Messages.OrderBy(message => message.Position)
            .Select(message => new BackupMessage(message.Role, message.Content)).ToArray()));
    }

    private static async Task<IResult> SaveBackupAsync(Guid conversationId, BackupContent content, ClaimsPrincipal user,
        CloudDbContext db, CancellationToken cancellationToken)
    {
        var error = ValidateBackup(content);
        if (error is not null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["backup"] = [error] });

        var id = conversationId.ToString("N");
        // SQLite acquires the writer lock before the lookup, including when two uploads create the same backup.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var backup = await OwnedBackups(db, user).SingleOrDefaultAsync(item => item.ClientConversationId == id, cancellationToken);
        if (backup is null)
        {
            backup = new ConversationBackup { UserId = UserId(user), ClientConversationId = id };
            db.ConversationBackups.Add(backup);
        }
        else
        {
            await db.BackupMessages.Where(message => message.BackupId == backup.Id).ExecuteDeleteAsync(cancellationToken);
        }
        backup.Title = content.Title;
        backup.RepositoryName = content.RepositoryName;
        backup.Branch = content.Branch;
        backup.BackedUpAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        backup.Messages = content.Messages.Select((message, position) => new BackupMessageEntity
        {
            Position = position,
            Role = message.Role,
            Content = message.Content
        }).ToList();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteBackupAsync(Guid conversationId, ClaimsPrincipal user, CloudDbContext db, CancellationToken cancellationToken)
    {
        var count = await OwnedBackups(db, user).Where(item => item.ClientConversationId == conversationId.ToString("N"))
            .ExecuteDeleteAsync(cancellationToken);
        return count == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> ReadPreferencesAsync(ClaimsPrincipal user, CloudDbContext db, CancellationToken cancellationToken)
    {
        var preference = await db.UserPreferences.FindAsync([UserId(user)], cancellationToken);
        return preference is null ? Results.NotFound() : Results.Ok(new SyncedPreferences(preference.Model, preference.UseStreaming, preference.MaxOutputTokens));
    }

    private static async Task<IResult> SavePreferencesAsync(SyncedPreferences preferences, ClaimsPrincipal user, CloudDbContext db,
        CancellationToken cancellationToken)
    {
        if (preferences.Model is null || preferences.Model.Length > 200 || !SyncedPreferences.IsValidTokenLimit(preferences.MaxOutputTokens))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["preferences"] = ["模型名称最多 200 字符，最大输出 Token 数须为 256–8000。"] });

        var id = UserId(user);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var preference = await db.UserPreferences.FindAsync([id], cancellationToken);
        if (preference is null)
        {
            preference = new UserPreference { UserId = id };
            db.UserPreferences.Add(preference);
        }
        preference.Model = preferences.Model;
        preference.UseStreaming = preferences.UseStreaming;
        preference.MaxOutputTokens = preferences.MaxOutputTokens;
        preference.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.NoContent();
    }

    private static string? ValidateBackup(BackupContent content)
    {
        if (string.IsNullOrWhiteSpace(content.Title) || content.Title.Length > 256 || content.RepositoryName is null
            || content.RepositoryName.Length > 512 || content.Branch is null || content.Branch.Length > 1024)
            return "会话标题、仓库名或分支无效。";
        if (content.Messages is not { Length: >= 1 and <= 4000 }) return "每份备份需包含 1–4000 条消息。";
        long textLength = 0;
        foreach (var message in content.Messages)
        {
            if (message is null || message.Role is not ("user" or "assistant") || message.Content is null) return "消息角色或正文无效。";
            textLength += message.Content.Length;
            if (textLength > 2_000_000) return "会话正文超过 200 万字符。";
        }
        return null;
    }

    private static IQueryable<ConversationBackup> OwnedBackups(CloudDbContext db, ClaimsPrincipal user)
    {
        var id = UserId(user);
        return db.ConversationBackups.Where(backup => backup.UserId == id);
    }

    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private static BackupSummary ToSummary(ConversationBackup backup) => new(backup.ClientConversationId, backup.Title,
        backup.RepositoryName, backup.Branch, DateTimeOffset.FromUnixTimeMilliseconds(backup.BackedUpAt));
}
