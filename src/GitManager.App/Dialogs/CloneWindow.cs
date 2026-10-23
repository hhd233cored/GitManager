using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GitManager.App.Dialogs;

public sealed class CloneWindow : Window
{
    private readonly TextBox _url;
    private readonly TextBox _destination;
    public string RepositoryUrl => _url.Text.Trim();
    public string Destination => _destination.Text.Trim();
    public CloneWindow(Window owner)
    {
        Owner = owner;
        Title = "克隆仓库";
        Width = 570;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(25) };
        panel.Children.Add(new TextBlock { Text = "克隆远程仓库", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new TextBlock { Text = "仓库地址（HTTPS / SSH）", Margin = new Thickness(0, 0, 0, 8) });
        _url = new TextBox { Margin = new Thickness(0, 0, 0, 17) };
        panel.Children.Add(_url);
        panel.Children.Add(new TextBlock { Text = "本地目标文件夹", Margin = new Thickness(0, 0, 0, 8) });
        var destinationPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
        var browse = new Button { Content = "选择位置…", Margin = new Thickness(9, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        _destination = new TextBox { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GitHub", "repository") };
        browse.Click += (_, _) =>
        {
            var picker = new OpenFolderDialog { Title = "选择克隆位置的父文件夹" };
            if (picker.ShowDialog(this) != true) return;
            var repositoryName = RepositoryUrl.TrimEnd('/').Split('/', ':').LastOrDefault() ?? "repository";
            if (repositoryName.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repositoryName = repositoryName[..^4];
            if (repositoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || repositoryName.Length == 0) repositoryName = "repository";
            _destination.Text = Path.Combine(picker.FolderName, repositoryName);
        };
        destinationPanel.Children.Add(browse);
        destinationPanel.Children.Add(_destination);
        panel.Children.Add(destinationPanel);
        panel.Children.Add(new TextBlock { Text = "目标文件夹需不存在或为空。认证使用已有的 Git 凭据或 SSH 配置。", Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 21) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 10, 0), MinWidth = 80 };
        var clone = new Button { Content = "克隆仓库", IsDefault = true, MinWidth = 100, Style = (Style)FindResource("PrimaryButton") };
        clone.Click += (_, _) =>
        {
            if (RepositoryUrl.Length == 0) { _url.Focus(); return; }
            if (!Path.IsPathFullyQualified(Destination)) { MessageBox.Show(this, "请选择完整的本地目标路径。", "目标路径", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (!Directory.Exists(Path.GetDirectoryName(Destination))) { MessageBox.Show(this, "目标路径的父文件夹不存在，请先选择有效位置。", "目标路径", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            DialogResult = true;
        };
        actions.Children.Add(cancel);
        actions.Children.Add(clone);
        panel.Children.Add(actions);
        Content = panel;
        Loaded += (_, _) => _url.Focus();
    }
}
