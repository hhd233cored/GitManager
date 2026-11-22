using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GitManager.Server;

internal sealed class CloudDbContext(DbContextOptions<CloudDbContext> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<ConversationBackup> ConversationBackups => Set<ConversationBackup>();
    public DbSet<BackupMessageEntity> BackupMessages => Set<BackupMessageEntity>();
    public DbSet<UserPreference> UserPreferences => Set<UserPreference>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<ConversationBackup>().HasIndex(backup => new { backup.UserId, backup.ClientConversationId }).IsUnique();
        model.Entity<ConversationBackup>().HasOne<IdentityUser>().WithMany().HasForeignKey(backup => backup.UserId);
        model.Entity<BackupMessageEntity>().HasKey(message => new { message.BackupId, message.Position });
        model.Entity<BackupMessageEntity>().HasOne<ConversationBackup>().WithMany(backup => backup.Messages)
            .HasForeignKey(message => message.BackupId);
        model.Entity<UserPreference>().HasKey(preference => preference.UserId);
        model.Entity<UserPreference>().HasOne<IdentityUser>().WithOne().HasForeignKey<UserPreference>(preference => preference.UserId);
    }
}

internal sealed class ConversationBackup
{
    public long Id { get; set; }
    public required string UserId { get; init; }
    public required string ClientConversationId { get; init; }
    public string Title { get; set; } = "";
    public string RepositoryName { get; set; } = "";
    public string Branch { get; set; } = "";
    public long BackedUpAt { get; set; }
    public List<BackupMessageEntity> Messages { get; set; } = [];
}

internal sealed class BackupMessageEntity
{
    public long BackupId { get; set; }
    public int Position { get; set; }
    public required string Role { get; init; }
    public required string Content { get; init; }
}

internal sealed class UserPreference
{
    public required string UserId { get; init; }
    public string Model { get; set; } = "";
    public bool UseStreaming { get; set; }
    public int MaxOutputTokens { get; set; }
    public long UpdatedAt { get; set; }
}
