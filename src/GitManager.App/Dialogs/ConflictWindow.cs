using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitManager.Core;

namespace GitManager.App.Dialogs;

public sealed class ConflictWindow : Window
{
    private readonly TextBox _result;
    public ConflictResolution Resolution { get; private set; }
    public string ResultText => _result.Text;
    public ConflictWindow(Window owner, ConflictContent file, bool rebasing)
    {
        Owner = owner; Title = "解决冲突 · " + file.Path; Width = 1050; Height = 760; MinWidth = 760; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = file.Path + "\n" + (rebasing ? "变基中：当前版本为目标分支，传入版本为正在重放的提交。" : "选择一侧版本，或编辑下方结果后保存并标记为解决。"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        grid.Children.Add(heading);
        var sides = new Grid(); sides.ColumnDefinitions.Add(new ColumnDefinition()); sides.ColumnDefinitions.Add(new ColumnDefinition()); Grid.SetRow(sides, 1);
        Grid Side(string name, string? contents)
        {
            var panel = new Grid { Margin = new Thickness(0, 0, 8, 8) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition());
            panel.Children.Add(new TextBlock { Text = name + (contents is null ? "（此侧已删除）" : ""), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
            var source = new TextBox { Text = file.IsBinary ? "二进制或非 UTF-8 内容，请选择保留哪一侧。" : contents ?? "", IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), VerticalContentAlignment = VerticalAlignment.Top };
            Grid.SetRow(source, 1); panel.Children.Add(source);
            return panel;
        }
        var current = Side("当前版本（ours）", file.Current); var incoming = Side("传入版本（theirs）", file.Incoming);
        Grid.SetColumn(incoming, 1); sides.Children.Add(current); sides.Children.Add(incoming); grid.Children.Add(sides);
        var resultPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 12) }; Grid.SetRow(resultPanel, 2);
        var resultLabel = new TextBlock { Text = "编辑结果", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) }; DockPanel.SetDock(resultLabel, Dock.Top); resultPanel.Children.Add(resultLabel);
        _result = new TextBox { Name = "ConflictResultBox", Text = file.IsBinary ? "" : file.Result, IsEnabled = !file.IsBinary, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, FontFamily = new FontFamily("Consolas") };
        resultPanel.Children.Add(_result); grid.Children.Add(resultPanel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; Grid.SetRow(buttons, 3);
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; buttons.Children.Add(cancel);
        void Add(string label, ConflictResolution choice, bool enabled = true)
        {
            var button = new Button { Content = label, IsEnabled = enabled, Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, _) => { Resolution = choice; DialogResult = true; }; buttons.Children.Add(button);
        }
        Add("采用当前版本" + (file.Current is null ? "（删除）" : ""), ConflictResolution.Current);
        Add("采用传入版本" + (file.Incoming is null ? "（删除）" : ""), ConflictResolution.Incoming);
        Add("保存编辑结果并标记解决", ConflictResolution.Edited, !file.IsBinary);
        grid.Children.Add(buttons); Content = new Border { Background = Brushes.White, Padding = new Thickness(16), Child = grid };
    }
}
