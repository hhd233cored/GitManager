using System.Windows;
using System.Windows.Controls;

namespace GitManager.App.Dialogs;

public sealed class TextPromptWindow : Window
{
    private readonly TextBox _input;
    public string Value => _input.Text.Trim();
    public TextPromptWindow(Window owner, string title, string prompt, string initial = "")
    {
        Owner = owner;
        Title = title;
        Width = 510;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        stack.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        _input = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 20) };
        stack.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 75, Margin = new Thickness(0, 0, 9, 0) };
        var submit = new Button { Content = "确定", IsDefault = true, MinWidth = 75, Style = (Style)FindResource("PrimaryButton") };
        submit.Click += (_, _) => { if (Value.Length > 0) DialogResult = true; else _input.Focus(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(submit);
        stack.Children.Add(buttons);
        Content = stack;
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }
}
