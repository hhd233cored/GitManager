using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using GitManager.App;
using GitManager.App.Controls;
using GitManager.App.ViewModels;
using GitManager.Core;

internal static class ChatPresentationChecks
{
    public static async Task RunAsync(MainWindow window, string output, Func<Window, string, Task> capture)
    {
        var original = window.ViewModel.Repository!.Path;
        var assistant = window.Assistant;
        assistant.IsOpen = true;
        var message = new AssistantMessage("助手", """
            ## 同步建议

            先检查 **本地变更**，再运行 `git fetch origin`。*保留修改*，~~不要直接覆盖~~。

            > Fetch 只更新远程信息。

            1. 查看变更
            2. 对比本地与远程
               - 保留现有提交
               - 选择合适的同步方式

            - [x] 已检查工作区
            - [ ] 等待确认

            | 操作 | 作用 |
            | --- | --- |
            | Fetch | 获取远程信息 |
            | Pull | 更新当前分支 |

            ```bash
            git fetch origin
            git status --short
            ```

            [Git 文档](https://git-scm.com/docs)
            [本地文件](file:///C:/private.txt)
            [脚本](javascript:alert(1))
            ![示例图片](https://example.test/tracker.png)

            <script>这只是文本</script>
            """);
        assistant.Messages.Add(message);
        await Task.Delay(250); window.UpdateLayout();
        var panel = (AssistantPane)window.FindName("AssistantPanel");
        var view = Descendants(panel).OfType<MarkdownView>().Single();
        var text = new TextRange(view.Document.ContentStart, view.Document.ContentEnd).Text;
        Assert(text.Contains("本地变更") && !text.Contains("**本地变更**"), "Markdown emphasis was not rendered.");
        Assert(view.Document.Blocks.OfType<Paragraph>().Any(paragraph => paragraph.FontSize >= 16), "Missing heading.");
        Assert(Logical(view.Document).OfType<Span>().Any(span => span.FontWeight == FontWeights.Bold), "Missing bold inline.");
        Assert(view.Document.Blocks.OfType<System.Windows.Documents.List>().Count() == 2 && view.Document.Blocks.OfType<Table>().Count() == 1, "Missing list/table blocks.");
        Assert(text.Contains("☑") && text.Contains("☐") && text.Contains("git status --short"), "Missing task markers or fenced code.");
        var links = Logical(view.Document).OfType<Hyperlink>().ToArray();
        Assert(links.Length == 1 && links[0].NavigateUri.Scheme == "https", "Unsafe navigation or missing web link.");
        Assert(!MarkdownView.TryWebUri("file:///C:/private.txt", out _) && !MarkdownView.TryWebUri("javascript:alert(1)", out _), "Unsafe URI accepted.");
        Assert(!Descendants(view).OfType<Image>().Any() && text.Contains("<script>"), "HTML/images must not be executed or loaded.");
        view.SelectAll();
        Assert(view.Selection.Text.Contains("git fetch origin") && !view.Selection.Text.Contains("```"), "Rendered text cannot be selected/copied.");
        view.Selection.Select(view.Document.ContentStart, view.Document.ContentStart);
        await capture(window, Path.Combine(output, "assistant-markdown.png"));

        message.Text = "生成中的代码：\n\n```text\n" + new string('a', 1600);
        await Task.Delay(200); window.Width = 1080; window.UpdateLayout();
        await Task.Delay(100);
        var code = view.Document.Blocks.OfType<Paragraph>().Single(paragraph => paragraph.FontFamily.Source.Contains("Consolas"));
        var run = code.Inlines.OfType<Run>().Single();
        var start = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var end = run.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        Assert(end.Y > start.Y + 50 && end.Right <= view.ActualWidth + 2, "Long code must wrap to the available width.");
        Assert(view.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled, "Markdown has a horizontal scrollbar.");
        message.Text += "\n```\n\n**流式回答已完成**";
        await Task.Delay(200);
        Assert(new TextRange(view.Document.ContentStart, view.Document.ContentEnd).Text.Contains("流式回答已完成"), "Streaming Markdown did not update.");
        var scroll = (ScrollViewer)panel.FindName("ConversationScroll");
        scroll.ScrollToTop(); window.UpdateLayout();
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        view.RaiseEvent(wheel); window.UpdateLayout();
        Assert(wheel.Handled && scroll.VerticalOffset > 0, "Markdown swallowed conversation scrolling.");
        await capture(window, Path.Combine(output, "assistant-markdown-wrapped.png"));
        window.Width = 1440;
        assistant.Messages.Clear();

        var git = new GitService();
        var runner = new GitRunner();
        var clone = Path.Combine(Path.GetFullPath(output), "fetch-time-" + Guid.NewGuid().ToString("N"));
        await git.CloneAsync(original, clone);
        await git.FetchAsync(clone);
        var relative = (await runner.RunAsync(clone, ["rev-parse", "--git-path", "FETCH_HEAD"])).Output.Trim();
        File.SetLastWriteTimeUtc(Path.GetFullPath(relative, clone), DateTime.UtcNow.AddMinutes(-27));
        await window.ViewModel.OpenRepositoryAsync(clone);
        Assert(window.ViewModel.Error.Length == 0, window.ViewModel.Error);
        Assert(window.ViewModel.SyncTitle == "Fetch origin" && window.ViewModel.SyncSubtitle == "Last fetched 27 minutes ago", "Fetch time missing or incorrectly marks current branch as freshly fetched.");
        await capture(window, Path.Combine(output, "toolbar-last-fetched.png"));
        using (var reopened = new MainViewModel(persistSettings: false))
        {
            await reopened.OpenRepositoryAsync(clone);
            Assert(reopened.SyncSubtitle == "Last fetched 27 minutes ago", "Fetch time did not survive reopening.");
        }
        var worktree = clone + "-worktree";
        await runner.RunAsync(clone, ["worktree", "add", "--detach", worktree, "HEAD"]);
        await git.FetchAsync(worktree);
        Assert(File.Exists(Path.Combine(worktree, ".git")) && await git.GetLastFetchTimeAsync(worktree) is not null, "Fetch timestamp does not support worktrees.");
        await window.ViewModel.OpenRepositoryAsync(original);
        assistant.IsOpen = false;
        Console.WriteLine("PASS rendered/streaming Markdown, selected code text, nested lists, tables, safe links, wrapped code, outer scrolling and persistent/worktree Fetch time.");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static IEnumerable<DependencyObject> Logical(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Logical(child)) yield return nested;
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
