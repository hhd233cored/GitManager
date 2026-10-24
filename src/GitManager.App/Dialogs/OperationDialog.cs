using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GitManager.App.Dialogs;

public sealed record ActionChoice(object Value, string Label, string Explanation, bool Destructive = false);
public sealed class OperationDialog : Window
{
    private readonly ComboBox _choices;
    private readonly CheckBox _acknowledgement;
    public object SelectedValue => ((ActionChoice)_choices.SelectedItem).Value;
    public OperationDialog(Window owner, string title, string prompt, string details, IReadOnlyList<ActionChoice> choices,
        int selected = 0, string submitLabel = "执行")
    {
        Owner = owner; Title = title; Width = 670; MaxHeight = 780; SizeToContent = SizeToContent.Height;
        MinWidth = 520; ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        _choices = new ComboBox { Name = "OperationChoice", ItemsSource = choices, DisplayMemberPath = nameof(ActionChoice.Label), SelectedIndex = selected, Margin = new Thickness(0, 0, 0, 10), MinHeight = 28 };
        var explanation = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(_choices); panel.Children.Add(explanation);
        if (details.Length > 0) panel.Children.Add(new TextBox { Name = "OperationPreviewText", Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 280, Padding = new Thickness(9), Margin = new Thickness(0, 0, 0, 10) });
        _acknowledgement = new CheckBox { Name = "OperationAcknowledgement", Content = "我已查看影响，确认采用此版本并替换上述历史或文件。", Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(_acknowledgement);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var execute = new Button { Name = "ExecuteOperationButton", Content = submitLabel, MinWidth = 95, Style = (Style)FindResource("PrimaryButton") };
        void Update()
        {
            var option = (ActionChoice)_choices.SelectedItem;
            explanation.Text = option.Explanation;
            _acknowledgement.Visibility = option.Destructive ? Visibility.Visible : Visibility.Collapsed;
            execute.IsEnabled = !option.Destructive || _acknowledgement.IsChecked == true;
        }
        _choices.SelectionChanged += (_, _) => { _acknowledgement.IsChecked = false; Update(); };
        _acknowledgement.Checked += (_, _) => Update(); _acknowledgement.Unchecked += (_, _) => Update();
        execute.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(execute); panel.Children.Add(buttons);
        Content = new Border { Background = Brushes.White, Padding = new Thickness(20), Child = new ScrollViewer {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        Update();
    }
}

public sealed class SaveStashDialog : Window
{
    private readonly TextBox _message;
    private readonly CheckBox _untracked;
    private readonly CheckBox _selected;
    public string Message => _message.Text.Trim();
    public bool IncludeUntracked => _untracked.IsChecked == true;
    public bool SelectedOnly => _selected.IsChecked == true;
    public SaveStashDialog(Window owner, int total, int selected)
    {
        Owner = owner; Title = "保存到 Stash"; Width = 490; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "保存未提交的改动", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        _message = new TextBox { Text = "保存于 " + DateTime.Now.ToString("MM-dd HH:mm"), Margin = new Thickness(0, 0, 0, 12) };
        _untracked = new CheckBox { Content = "包含新增文件（忽略文件不包含在内）", IsChecked = true, Margin = new Thickness(0, 0, 0, 10) };
        _selected = new CheckBox { Content = $"只保存当前勾选的 {selected} 个文件（共 {total} 个变更）", IsEnabled = selected > 0, Margin = new Thickness(0, 0, 0, 18) };
        panel.Children.Add(_message); panel.Children.Add(_untracked); panel.Children.Add(_selected);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "保存 Stash", Style = (Style)FindResource("PrimaryButton"), IsEnabled = total > 0 };
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = new Border { Background = Brushes.White, Padding = new Thickness(20), Child = panel };
    }
}
