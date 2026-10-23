using System.Diagnostics;
using System.Text;

namespace GitManager.Core;

public sealed class GitRunner
{
    public async Task<GitResult> RunAsync(string directory, IEnumerable<string> arguments,
        CancellationToken cancellationToken = default, bool throwOnError = true, string? standardInput = null)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=false");
        start.ArgumentList.Add("--literal-pathspecs");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "auto";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new GitException("未找到 Git。请安装 Git for Windows，并确保 git 已加入 PATH。");
        }
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var result = new GitResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        if (throwOnError && result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim();
            throw new GitException(message.Length > 0 ? message : $"Git 操作失败（退出代码 {result.ExitCode}）。", result.ExitCode);
        }
        return result;
    }
}
