using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitManager.Services;

namespace GitManager.App.Dialogs;

internal static class SourcePreviewWindow
{
    // Citation links never open a local file through the shell; revalidate the current service policy.
    public static async Task ShowAsync(Window owner, RepositoryTools tools, Uri uri)
    {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0]).ToDictionary(group => group.Key, group => Uri.UnescapeDataString(group.First()[1]));
        if (!query.TryGetValue("path", out var file) || !query.TryGetValue("line", out var number)
            || !int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line < 1 || line > 1000000)
            throw new ArgumentException("源码引用格式无效。");
        tools.RequireContext(uri.Host);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var json = await tools.InvokeAsync(new("read_file", uri.Host, file, StartLine: Math.Max(1, line - 20), EndLine: line + 80), timeout.Token);
        tools.RequireContext(uri.Host);
        using var document = JsonDocument.Parse(json);
        var source = document.RootElement;
        var hash = source.GetProperty("hash").GetString();
        var changed = query.TryGetValue("hash", out var previous) && hash != previous;
        var panel = new DockPanel { Margin = new Thickness(14), Background = Brushes.White };
        var heading = new TextBlock { Text = file + $" : {line}\n" + (changed ? "文件已变化，以下为当前工作区内容。" : "当前工作区内容") + " · SHA256 " + hash,
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var text = string.Join(Environment.NewLine, source.GetProperty("lines").EnumerateArray().Select(item =>
            $"{item.GetProperty("line").GetInt32(),5}  {item.GetProperty("text").GetString()}"));
        var code = new TextBox { Text = text, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13,
            FontWeight = FontWeights.Normal, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8),
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        panel.Children.Add(code);
        var window = new Window { Owner = owner, Title = "源码引用 · " + file, Width = 900, Height = 640, MinWidth = 400, MinHeight = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel, Background = Brushes.White };
        var startLine = source.GetProperty("startLine").GetInt32();
        window.Loaded += (_, _) => code.ScrollToLine(Math.Max(0, line - startLine));
        // Extract all values before disposing the JSON document (ShowDialog keeps it alive until close).
        window.ShowDialog();
    }
}
