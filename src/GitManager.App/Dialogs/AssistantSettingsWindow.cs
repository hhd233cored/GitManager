using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitManager.Agent;
using GitManager.App.ViewModels;
using GitManager.Contracts;
using Microsoft.Extensions.AI;

namespace GitManager.App.Dialogs;

public sealed class AssistantSettingsWindow : Window
{
    private readonly TextBox _url;
    private readonly TextBox _model;
    private readonly PasswordBox _key;
    private readonly CheckBox _deleteKey;
    private readonly CheckBox _includeDiff;
    private readonly CheckBox _includeProject;
    private readonly TextBox _excludedPaths;
    private readonly CheckBox _streaming;
    private readonly TextBox _maxOutputTokens;
    private readonly CheckBox _mcp;
    private readonly TextBlock _status;
    private readonly AssistantViewModel _viewModel;
    private readonly CancellationTokenSource _closed = new();
    public AssistantSettingsWindow(Window owner, AssistantViewModel viewModel)
    {
        _viewModel = viewModel;
        Owner = owner; Title = "助手设置"; Width = 580; MaxHeight = 820; SizeToContent = SizeToContent.Height;
        MinWidth = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(22) };
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var account = new Button { Name = "CloudAccountButton", Content = "账号与备份…", Padding = new Thickness(10, 4, 10, 4) };
        DockPanel.SetDock(account, Dock.Right);
        account.Click += (_, _) => OpenCloudAccount();
        heading.Children.Add(account);
        heading.Children.Add(new TextBlock { Text = "连接模型", FontSize = 19, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(heading);
        panel.Children.Add(Note("支持 OpenAI 兼容的 Chat Completions 接口。Ollama、LM Studio 等本机服务请使用其兼容接口地址。"));
        _url = AddField(panel, "API 基础地址（例如 https://服务地址/v1）", viewModel.Options.BaseUrl, "AssistantBaseUrl");
        _model = AddField(panel, "模型名称（需支持工具调用）", viewModel.Options.Model, "AssistantModel");
        panel.Children.Add(new TextBlock { Text = "API Key", Margin = new Thickness(0, 10, 0, 5) });
        _key = new PasswordBox { Name = "AssistantApiKey", Padding = new Thickness(7), MinHeight = 30 };
        panel.Children.Add(_key);
        panel.Children.Add(Note("密钥存放在 Windows 凭据管理器，按服务地址分别保存。留空保留该地址已保存的密钥；本机服务可不填。"));
        _deleteKey = new CheckBox { Content = "清除该服务已保存的密钥", Margin = new Thickness(0, 5, 0, 8) };
        _includeDiff = new CheckBox { Name = "AssistantIncludeDiff", Content = "允许共享仓库内容（Diff、源码及搜索片段）", IsChecked = viewModel.Options.IncludeDiff, Margin = new Thickness(0, 7, 0, 7) };
        _includeProject = new CheckBox { Name = "AssistantIncludeProject", Content = "启用项目概况、文件浏览、源码阅读与代码搜索", IsChecked = viewModel.Options.IncludeRepositoryContent, Margin = new Thickness(0, 0, 0, 7) };
        _excludedPaths = new TextBox { Name = "AssistantExcludedPaths", Text = viewModel.Options.ExcludedPaths, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Height = 56, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 8000 };
        _streaming = new CheckBox { Content = "流式显示回答（服务不支持时可关闭）", IsChecked = viewModel.Options.UseStreaming, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(_deleteKey); panel.Children.Add(_includeDiff); panel.Children.Add(_includeProject);
        panel.Children.Add(Note("读取的内容会发给所选模型服务；启用 MCP 时也受这些设置约束。默认排除常见密钥、链接、依赖与构建目录。"));
        panel.Children.Add(Note("额外排除路径：每行一个仓库相对路径、目录或 * 通配模式，例如 private、*.local.json。对 Diff、项目阅读和 MCP 同时生效。"));
        panel.Children.Add(_excludedPaths); panel.Children.Add(_streaming);
        _maxOutputTokens = AddField(panel, "最大输出 Token 数（256–8000）", Math.Clamp(viewModel.Options.MaxOutputTokens, 256, 8000).ToString(), "AssistantMaxOutputTokens");
        panel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 12) });
        _mcp = new CheckBox { Name = "AssistantEnableMcp", Content = "启用本机 MCP，开放当前选中的仓库", IsChecked = viewModel.Options.EnableMcp, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(_mcp);
        panel.Children.Add(Note("外部客户端使用下方配置连接。GitManager 需保持运行；切换仓库或分支后，客户端应重新读取当前仓库。写操作仍需在 GitManager 界面查看预览并执行。"));
        var config = new TextBox { Name = "McpConfigurationText", Text = viewModel.McpConfiguration, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 105, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 7, 0, 5), FontFamily = new FontFamily("Consolas"), FontSize = 11 };
        panel.Children.Add(config);
        var copy = new Button { Content = "复制 MCP 配置", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 0, 12) };
        copy.Click += (_, _) => Clipboard.SetText(config.Text);
        panel.Children.Add(copy);
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SteelBlue, Margin = new Thickness(0, 3, 0, 10) };
        panel.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var test = new Button { Name = "TestAssistantConnectionButton", Content = "测试连接", Margin = new Thickness(0, 0, 8, 0), MinWidth = 85 };
        test.Click += TestConnection;
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 65 };
        var save = new Button { Name = "SaveAssistantSettingsButton", Content = "保存", MinWidth = 80, Style = (Style)FindResource("PrimaryButton") };
        save.Click += (_, _) =>
        {
            try { viewModel.SaveOptions(CurrentOptions(), _key.Password, _deleteKey.IsChecked == true); DialogResult = true; }
            catch (Exception ex) { _status.Text = AssistantViewModel.SafeError(ex, _key.Password); }
        };
        buttons.Children.Add(test); buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, Background = Brushes.White, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Closed += (_, _) => { _closed.Cancel(); _closed.Dispose(); };
    }
    private AssistantOptions CurrentOptions()
    {
        if (!int.TryParse(_maxOutputTokens.Text, out var maxOutputTokens) || !SyncedPreferences.IsValidTokenLimit(maxOutputTokens))
            throw new ArgumentException("最大输出 Token 数须为 256–8000。");
        return new(_url.Text.Trim(), _model.Text.Trim(), _includeDiff.IsChecked == true,
            _mcp.IsChecked == true, _streaming.IsChecked == true, maxOutputTokens, _includeProject.IsChecked == true, _excludedPaths.Text);
    }

    private void OpenCloudAccount()
    {
        var before = _viewModel.ExportPreferences();
        new CloudAccountWindow(this, _viewModel).ShowDialog();
        var after = _viewModel.ExportPreferences();
        if (after == before) return;
        _model.Text = after.Model;
        _streaming.IsChecked = after.UseStreaming;
        _maxOutputTokens.Text = after.MaxOutputTokens.ToString();
    }
    private async void TestConnection(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        button.IsEnabled = false;
        _status.Text = "正在测试…";
        string? key = null;
        try
        {
            var options = CurrentOptions();
            key = _key.Password.Length > 0 ? _key.Password : _deleteKey.IsChecked == true ? null : _viewModel.ReadSecret(options.BaseUrl);
            using var client = AssistantClientFactory.Create(options, key);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var response = await client.GetResponseAsync("Reply with OK.", new ChatOptions { MaxOutputTokens = 128 }, timeout.Token);
            _status.Text = string.IsNullOrWhiteSpace(response.Text) ? "服务已响应，但未返回文字。请检查模型是否支持文字和工具调用。" : "连接成功。可保存设置后使用助手。";
        }
        catch (OperationCanceledException) { _status.Text = "测试已取消或超时。"; }
        catch (Exception ex) { _status.Text = AssistantViewModel.SafeError(ex, key); }
        finally { button.IsEnabled = true; }
    }
    private static TextBox AddField(Panel parent, string label, string value, string name)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 5) });
        var text = new TextBox { Name = name, Text = value, MinHeight = 30, Padding = new Thickness(7) };
        parent.Children.Add(text); return text;
    }
    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, FontSize = 11, Margin = new Thickness(0, 5, 0, 5) };
}
