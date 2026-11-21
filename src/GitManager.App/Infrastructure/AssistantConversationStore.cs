using System.IO;
using GitManager.Agent;
using GitManager.Services;
using Microsoft.Data.Sqlite;

namespace GitManager.App.Infrastructure;

internal sealed record AssistantConversation(string Id, string RepositoryPath, string RepositoryName, string Branch,
    string Title, DateTimeOffset UpdatedAt)
{
    public string Scope => string.IsNullOrEmpty(Branch) ? RepositoryName : $"{RepositoryName} · {Branch}";
    public DateTime LocalUpdatedAt => UpdatedAt.LocalDateTime;
}

internal sealed class AssistantConversationStore(string? path = null)
{
    private readonly string _path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitManager", "chats.db"));

    public void Append(string id, RepositoryContext repository, string title, string question, string answer)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var session = connection.CreateCommand();
        session.Transaction = transaction;
        session.CommandText = """
            INSERT INTO conversations (id, repository_path, repository_name, branch, title, created_at, updated_at)
            VALUES ($id, $path, $name, $branch, $title, $now, $now)
            ON CONFLICT (id) DO UPDATE SET updated_at = MAX(updated_at, excluded.updated_at);
            """;
        session.Parameters.AddWithValue("$id", id);
        session.Parameters.AddWithValue("$path", repository.Path);
        session.Parameters.AddWithValue("$name", repository.Name);
        session.Parameters.AddWithValue("$branch", repository.Branch);
        session.Parameters.AddWithValue("$title", title);
        session.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        session.ExecuteNonQuery();

        using var messages = connection.CreateCommand();
        messages.Transaction = transaction;
        messages.CommandText = """
            INSERT INTO messages (conversation_id, role, content)
            VALUES ($id, 'user', $question), ($id, 'assistant', $answer);
            """;
        messages.Parameters.AddWithValue("$id", id);
        messages.Parameters.AddWithValue("$question", question);
        messages.Parameters.AddWithValue("$answer", answer);
        messages.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<AssistantConversation> List()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, repository_path, repository_name, branch, title, updated_at
            FROM conversations ORDER BY updated_at DESC, rowid DESC;
            """;
        using var reader = command.ExecuteReader();
        var results = new List<AssistantConversation>();
        while (reader.Read())
            results.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5))));
        return results;
    }

    public IReadOnlyList<AssistantTurn> Read(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT role, content FROM messages WHERE conversation_id = $id ORDER BY id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var messages = new List<AssistantTurn>();
        while (reader.Read()) messages.Add(new(reader.GetString(0), reader.GetString(1)));
        return messages;
    }

    public void Delete(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM conversations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public static bool IsStorageError(Exception exception) => exception is SqliteException or IOException or UnauthorizedAccessException;

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            ForeignKeys = true,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS conversations (
                    id TEXT PRIMARY KEY NOT NULL,
                    repository_path TEXT NOT NULL,
                    repository_name TEXT NOT NULL,
                    branch TEXT NOT NULL,
                    title TEXT NOT NULL,
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS messages (
                    id INTEGER PRIMARY KEY,
                    conversation_id TEXT NOT NULL REFERENCES conversations (id) ON DELETE CASCADE,
                    role TEXT NOT NULL CHECK (role IN ('user', 'assistant')),
                    content TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_messages_conversation ON messages (conversation_id, id);
                """;
            schema.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static string CreateTitle(string question)
    {
        var line = question.AsSpan().Trim();
        var lineBreak = line.IndexOfAny('\r', '\n');
        if (lineBreak >= 0) line = line[..lineBreak].TrimEnd();
        if (line.IsEmpty) return "对话";
        if (line.Length <= 64) return line.ToString();
        return line[..(char.IsHighSurrogate(line[63]) ? 63 : 64)].ToString() + "…";
    }
}
