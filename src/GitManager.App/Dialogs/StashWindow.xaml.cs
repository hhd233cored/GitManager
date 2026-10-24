using System.Windows;
using System.Windows.Controls;
using GitManager.App.ViewModels;
using GitManager.Core;

namespace GitManager.App.Dialogs;

public partial class StashWindow : Window
{
    private readonly MainViewModel _vm;
    public StashWindow(Window owner, MainViewModel vm)
    {
        InitializeComponent(); Owner = owner; _vm = vm; DataContext = vm;
        Left = owner.Left + 22; Top = owner.Top + 130;
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await OperationUi.SaveStashAsync(this, _vm);
    private async void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StashesList.SelectedItem is StashEntry entry) await _vm.ViewStashAsync(entry);
    }
    private async void View_Click(object sender, RoutedEventArgs e)
    {
        if (StashesList.SelectedItem is StashEntry entry) await _vm.ViewStashAsync(entry);
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (StashesList.SelectedItem is StashEntry entry) await _vm.ApplyStashAsync(entry, false, RestoreIndexCheckBox.IsChecked == true);
    }
    private async void Pop_Click(object sender, RoutedEventArgs e)
    {
        if (StashesList.SelectedItem is StashEntry entry) await _vm.ApplyStashAsync(entry, true, RestoreIndexCheckBox.IsChecked == true);
    }
    private async void Drop_Click(object sender, RoutedEventArgs e)
    {
        if (StashesList.SelectedItem is not StashEntry entry) return;
        var dialog = new OperationDialog(this, "删除 Stash", entry.Message, entry.Id,
            [new ActionChoice(true, "删除此 Stash", "这会移除所选 Stash。已由恢复点引用的自动快照会继续保留。", true)]);
        if (dialog.ShowDialog() == true) await _vm.DeleteStashAsync(entry);
    }
}
