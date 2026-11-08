using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace GitManager.Services;

// Bounded, read-only workspace tools. The model never supplies an executable or command arguments.
internal sealed class ProjectReader(string? cacheDirectory)
{
    private const int MaximumFileBytes = 1024 * 1024;
    private readonly SemaphoreSlim _readers = new(2);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedText> _texts = new(StringComparer.Ordinal);
    private string? _overviewKey;
    private object? _overview;
    private sealed record CachedText(string[] Lines, int Characters);
    private sealed record Inventory(string[] Files, bool Truncated);
    private sealed record Source(string Path, string Hash, string[] Lines);

    public void Invalidate()
    {
        lock (_cacheLock) { _texts.Clear(); _overviewKey = null; _overview = null; }
    }

    internal static bool IsTool(string method) => method is "get_project_overview" or "list_files" or "read_file" or "search_code";

    public async Task<string> InvokeAsync(RepositoryContext context, RepositoryContentPolicy policy, ToolCall call, CancellationToken ct)
    {
        if (!policy.AllowProject) throw new InvalidOperationException("仓库源码阅读与搜索已关闭。请在助手设置中启用内容共享与项目阅读。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        await _readers.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var inventory = await InventoryAsync(context.Path, policy, timeout.Token).ConfigureAwait(false);
            var result = call.Method switch
            {
                "list_files" => List(context, inventory, call),
                "read_file" => Describe(context, await ReadAsync(context.Path, RequireFile(inventory, call.FilePath), policy, timeout.Token).ConfigureAwait(false), call),
                "search_code" => await SearchAsync(context, inventory, policy, call, timeout.Token).ConfigureAwait(false),
                "get_project_overview" => await OverviewAsync(context, inventory, policy, timeout.Token).ConfigureAwait(false),
                _ => throw new ArgumentException("不支持的项目工具。")
            };
            if (result.Length > 200000) throw new InvalidOperationException("结果超过工具输出限制，请减少 limit 或限定更具体的目录。");
            return result;
        }
        finally { _readers.Release(); }
    }

    private static string DirectoryPath(string? path) => string.IsNullOrWhiteSpace(path) || path == "." ? "" : RepositoryTools.NormalizeFilePath(path.TrimEnd('/', '\\'));
    private static bool Within(string file, string directory) => directory.Length == 0 || file.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || file.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);
    private static string RequireFile(Inventory inventory, string? path)
    {
        var relative = RepositoryTools.NormalizeFilePath(path);
        return inventory.Files.FirstOrDefault(file => file.Equals(relative, StringComparison.Ordinal))
            ?? throw new ArgumentException("文件不存在、不在 Git 文件清单内或已被排除。");
    }

    private static async Task<Inventory> InventoryAsync(string root, RepositoryContentPolicy policy, CancellationToken ct)
    {
        CheckLinks(root, root);
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "--no-optional-locks", "-c", "core.quotepath=false", "-c", "core.fsmonitor=false", "ls-files", "--cached", "--others", "--exclude-standard", "-z" }) start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_PAGER"] = "cat";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法读取 Git 文件清单。");
        using var stop = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        // Drain both streams with a hard cap instead of using an unbounded ReadToEndAsync.
        async Task<string> ReadOutput(StreamReader stream, int maximum)
        {
            try { return await BoundedOutputAsync(stream, maximum, ct).ConfigureAwait(false); }
            catch { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
        }
        var output = ReadOutput(process.StandardOutput, 4 * 1024 * 1024);
        var error = ReadOutput(process.StandardError, 16000);
        try
        {
            await Task.WhenAll(output, error).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("无法读取当前仓库的 Git 文件清单。");
            var files = output.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)
                .Where(file => { ct.ThrowIfCancellationRequested(); return !policy.Excludes(file) && SafePath(root, file); }).Order(StringComparer.Ordinal).ToArray();
            return new(files.Take(50000).ToArray(), files.Length > 50000);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
    private static async Task<string> BoundedOutputAsync(StreamReader reader, int maximum, CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        int length;
        while ((length = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length + length > maximum) throw new InvalidOperationException("文件清单或命令输出过大，请缩小仓库范围。");
            output.Append(buffer, 0, length);
        }
        return output.ToString();
    }

    private static bool SafePath(string root, string relative)
    {
        try
        {
            relative = RepositoryTools.NormalizeFilePath(relative);
            var absolute = Path.GetFullPath(relative, root);
            CheckLinks(root, absolute);
            return File.Exists(absolute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return false; }
    }
    private static void CheckLinks(string root, string absolute)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!absolute.Equals(root, StringComparison.OrdinalIgnoreCase) && !absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("只允许读取当前仓库内的文件。");
        for (var entry = absolute; ; entry = Path.GetDirectoryName(entry)!)
        {
            if ((File.Exists(entry) || Directory.Exists(entry)) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("不读取链接文件或链接目录。");
            if (entry.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }

    private async Task<Source> ReadAsync(string root, string relative, RepositoryContentPolicy policy, CancellationToken ct)
    {
        if (policy.Excludes(relative)) throw new InvalidOperationException("该路径已被内容访问策略排除。");
        var absolute = Path.GetFullPath(relative, root);
        CheckLinks(root, absolute);
        byte[] bytes;
        await using (var input = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            ValidateHandle(input.SafeFileHandle, absolute);
            if (input.Length > MaximumFileBytes) throw new InvalidOperationException("文件超过 1 MB，请使用外部工具查看。");
            bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            CheckLinks(root, absolute);
        }
        ct.ThrowIfCancellationRequested();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        CachedText? cached;
        lock (_cacheLock) _texts.TryGetValue(hash, out cached);
        if (cached is null)
        {
            string text;
            try
            {
                if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
                else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
                else text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            }
            catch (DecoderFallbackException) { throw new InvalidOperationException("文件不是 UTF-8 或带 BOM 的 UTF-16 文本。"); }
            if (text.Any(ch => ch == '\0' || char.IsControl(ch) && ch is not ('\t' or '\r' or '\n' or '\f'))) throw new InvalidOperationException("不读取二进制文件。");
            cached = new(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'), text.Length);
            lock (_cacheLock)
            {
                // Content hashes are recomputed even when timestamps are unchanged. Cache never authorizes access.
                if (_texts.Count >= 32 || _texts.Values.Sum(item => item.Characters) + text.Length > 4 * 1024 * 1024) _texts.Clear();
                _texts[hash] = cached;
            }
        }
        return new(relative, hash, cached.Lines);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    private static void ValidateHandle(SafeFileHandle handle, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        var final = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) throw new InvalidOperationException("无法验证文件的实际路径。");
        var path = final.ToString();
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) path = "\\\\" + path[8..];
        else if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) path = path[4..];
        if (!path.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("实际文件路径与仓库路径不一致，不读取重定向的文件。");
    }

    internal static string Citation(string id, string path, int line, string hash) =>
        $"gitmanager-source://{id}/?path={Uri.EscapeDataString(path)}&line={line}&hash={hash}";

    private static string Describe(RepositoryContext context, Source source, ToolCall call)
    {
        var start = Math.Max(1, call.StartLine);
        if (start > source.Lines.Length) throw new ArgumentException($"起始行超出文件，共 {source.Lines.Length} 行。");
        var end = Math.Min(source.Lines.Length, Math.Min(Math.Max(start, call.EndLine), start + 199));
        var lines = new List<object>();
        var characters = 0;
        var next = start;
        var clipped = false;
        for (var index = start - 1; index < end; index++)
        {
            var text = RepositoryTools.Clip(source.Lines[index], 4000);
            if (characters + text.Length > 16000) break;
            characters += text.Length;
            lines.Add(new { line = index + 1, text, truncated = text.Length != source.Lines[index].Length });
            clipped |= text.Length != source.Lines[index].Length;
            next = index + 2;
        }
        return ToolJson.Serialize(new { repositoryId = context.Id, source = "working_tree", file = source.Path, hash = source.Hash, totalLines = source.Lines.Length,
            startLine = start, lines, truncated = next <= Math.Max(start, call.EndLine) && next <= source.Lines.Length || clipped,
            nextLine = next <= source.Lines.Length ? (int?)next : null, citation = Citation(context.Id, source.Path, start, source.Hash) });
    }

    private static string List(RepositoryContext context, Inventory inventory, ToolCall call)
    {
        var directory = DirectoryPath(call.FilePath);
        var depth = Math.Clamp(call.Depth, 1, 8);
        var prefix = directory.Length == 0 ? "" : directory + "/";
        var entries = inventory.Files.Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(file =>
        {
            var parts = file[prefix.Length..].Split('/');
            return (path: prefix + string.Join('/', parts.Take(depth)), directory: parts.Length > depth);
        }).Distinct().OrderBy(item => item.path, StringComparer.Ordinal).ToArray();
        var skip = Math.Clamp(call.Skip, 0, 50000);
        var page = entries.Skip(skip).Take(Math.Clamp(call.Limit, 1, 200)).ToArray();
        return ToolJson.Serialize(new { repositoryId = context.Id, source = "working_tree", directory, depth,
            entries = page.Select(item => new { item.path, kind = item.directory ? "directory" : "file" }), total = entries.Length,
            nextSkip = skip + page.Length < entries.Length ? (int?)(skip + page.Length) : null, inventoryTruncated = inventory.Truncated });
    }

    private async Task<string> SearchAsync(RepositoryContext context, Inventory inventory, RepositoryContentPolicy policy, ToolCall call, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(call.Query) || call.Query.Length > 300 || call.Query.Contains('\n') || call.Query.Contains('\r')) throw new ArgumentException("请输入 1–300 字符的单行文字查询（按字面匹配）。");
        var directory = DirectoryPath(call.FilePath);
        var comparison = call.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var limit = Math.Clamp(call.Limit, 1, 100);
        if (call.Skip < 0 || call.Skip > 10000) throw new ArgumentException("搜索分页超出范围，请缩小搜索目录。");
        var skip = call.Skip;
        var matches = new List<object>();
        var seen = 0; var scanned = 0; var unreadable = 0; long bytes = 0; var returnedCharacters = 0;
        var hasMore = false; var scanLimited = false;
        foreach (var file in inventory.Files.Where(file => Within(file, directory)))
        {
            ct.ThrowIfCancellationRequested();
            if (scanned >= 3000 || bytes >= 24 * 1024 * 1024) { scanLimited = true; break; }
            try
            {
                var source = await ReadAsync(context.Path, file, policy, ct).ConfigureAwait(false);
                scanned++; bytes += source.Lines.Sum(line => (long)line.Length);
                for (var index = 0; index < source.Lines.Length; index++)
                {
                    if (!source.Lines[index].Contains(call.Query, comparison)) continue;
                    if (seen++ < skip) continue;
                    var position = source.Lines[index].IndexOf(call.Query, comparison);
                    var text = source.Lines[index];
                    var excerptStart = Math.Max(0, position - 200);
                    var excerpt = RepositoryTools.Clip(text[excerptStart..], 1000);
                    if (matches.Count >= limit || returnedCharacters + excerpt.Length > 16000) { hasMore = true; break; }
                    returnedCharacters += excerpt.Length;
                    matches.Add(new { file, line = index + 1, text = excerpt, excerptStartColumn = excerptStart + 1, truncated = excerptStart > 0 || text.Length > 1000, hash = source.Hash,
                        citation = Citation(context.Id, file, index + 1, source.Hash) });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { unreadable++; scanned++; }
            if (hasMore) break;
        }
        if (hasMore && skip + matches.Count >= 10000) { scanLimited = true; hasMore = false; }
        return ToolJson.Serialize(new { repositoryId = context.Id, source = "working_tree", query = call.Query, literal = true, matches,
            scannedFiles = scanned, unreadableFiles = unreadable, truncated = hasMore || scanLimited || inventory.Truncated,
            scanLimited, nextSkip = hasMore ? (int?)(skip + matches.Count) : null,
            note = scanLimited ? "扫描预算已达上限，请限定 filePath 为更具体的目录或文件。" : "分页期间工作区可能变化；每个结果附带独立内容哈希。" });
    }

    private async Task<string> OverviewAsync(RepositoryContext context, Inventory inventory, RepositoryContentPolicy policy, CancellationToken ct)
    {
        var names = new[] { "package.json", "pyproject.toml", "requirements.txt", "pom.xml", "cargo.toml", "go.mod", "global.json", "directory.build.props", "cmakelists.txt", "dockerfile" };
        var manifests = inventory.Files.Where(file => names.Contains(Path.GetFileName(file).ToLowerInvariant()) || Path.GetExtension(file).ToLowerInvariant() is ".csproj" or ".sln" or ".slnx" or ".fsproj").ToArray();
        var documentation = inventory.Files.Where(file => Path.GetFileName(file).StartsWith("README", StringComparison.OrdinalIgnoreCase)).OrderBy(file => file.Count(ch => ch == '/')).ThenBy(file => file, StringComparer.Ordinal).Take(4);
        var sources = new List<Source>();
        var skipped = 0;
        foreach (var file in documentation.Concat(manifests).Distinct().Take(20))
        {
            try { sources.Add(await ReadAsync(context.Path, file, policy, ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { skipped++; }
        }
        var excerpts = new List<object>();
        var excerptCharacters = 0;
        foreach (var source in sources)
        {
            var lines = new List<object>();
            foreach (var (line, index) in source.Lines.Take(35).Select((line, index) => (line, index)))
            {
                var text = RepositoryTools.Clip(line, 250);
                if (excerptCharacters + text.Length > 16000) break;
                lines.Add(new { line = index + 1, text }); excerptCharacters += text.Length;
            }
            if (lines.Count == 0) break;
            excerpts.Add(new { file = source.Path, hash = source.Hash, lines, truncated = lines.Count < source.Lines.Length || source.Lines.Take(lines.Count).Any(line => line.Length > 250), citation = Citation(context.Id, source.Path, 1, source.Hash) });
        }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.Id + policy + string.Join('\0', inventory.Files) + string.Join('|', sources.Select(item => item.Hash)))));
        object overview; bool hit;
        lock (_cacheLock)
        {
            hit = _overviewKey == key;
            overview = hit ? _overview! : new
            {
                fileCount = inventory.Files.Length, inventoryTruncated = inventory.Truncated,
                topLevel = inventory.Files.Select(file => file.Split('/')[0]).Distinct().Take(100),
                languages = inventory.Files.GroupBy(file => Path.GetExtension(file).ToLowerInvariant()).OrderByDescending(group => group.Count()).Take(15).Select(group => new { extension = group.Key, count = group.Count() }).ToArray(),
                manifests = manifests.Take(100), manifestsTruncated = manifests.Length > 100,
                documents = excerpts.ToArray(), documentsTruncated = excerpts.Count < sources.Count || documentation.Concat(manifests).Distinct().Count() > 20,
                skippedDocuments = skipped, note = "静态项目概况；未运行项目配置或构建。目录、扩展名及清单只能作为进一步阅读的线索。"
            };
            _overviewKey = key; _overview = overview;
        }
        // Persist provenance only, never source text, model output or credentials. Every lookup still refreshes the inventory and hashes.
        if (!hit && cacheDirectory is not null)
        {
            try
            {
                Directory.CreateDirectory(cacheDirectory);
                var repositoryKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.Path.ToUpperInvariant())));
                var metadata = ToolJson.Serialize(new { branch = context.Branch, updated = DateTimeOffset.UtcNow, files = sources.Select(source => new { file = source.Path, hash = source.Hash }) });
                await File.WriteAllTextAsync(Path.Combine(cacheDirectory, repositoryKey + ".json"), metadata, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return ToolJson.Serialize(new { repositoryId = context.Id, source = "working_tree", cacheHit = hit, overview });
    }
}
