using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GitManager.App.Infrastructure;
using GitManager.App.ViewModels;
using Microsoft.Win32;

namespace GitManager.App.Dialogs;

public partial class RepositoryManagerWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ListCollectionView _view;
    private RepositoryEntry? Selected => RepositoryList.SelectedItem as RepositoryEntry;

    public RepositoryManagerWindow(Window owner, MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        Owner = owner;
        DataContext = viewModel;
        _view = new ListCollectionView(viewModel.Repositories);
        _view.Filter = item => item is RepositoryEntry entry && (entry.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase)
            || entry.Path.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));
        RepositoryList.ItemsSource = _view;
        SelectPath(viewModel.Repository?.Path);
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.Repositories.CollectionChanged += Repositories_CollectionChanged;
        Closed += (_, _) =>
        {
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            viewModel.Repositories.CollectionChanged -= Repositories_CollectionChanged;
        };
        UpdateButtons();
    }

    private void SelectPath(string? path)
    {
        RepositoryList.SelectedItem = _viewModel.Repositories.FirstOrDefault(entry => RepositoryEntry.SamePath(entry.Path, path));
        if (RepositoryList.SelectedItem is null && RepositoryList.Items.Count > 0) RepositoryList.SelectedIndex = 0;
        if (RepositoryList.SelectedItem is { } selected) RepositoryList.ScrollIntoView(selected);
    }
    private void UpdateButtons()
    {
        if (OpenButton is null) return;
        var canEdit = Selected is not null && !_viewModel.IsBusy;
        RenameButton.IsEnabled = RelocateButton.IsEnabled = RemoveButton.IsEnabled = OpenButton.IsEnabled = canEdit;
        var index = Selected is null ? -1 : _viewModel.Repositories.IndexOf(Selected);
        MoveUpButton.IsEnabled = canEdit && SearchBox.Text.Length == 0 && index > 0;
        MoveDownButton.IsEnabled = canEdit && SearchBox.Text.Length == 0 && index < _viewModel.Repositories.Count - 1;
        EmptyMessage.Visibility = RepositoryList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Text = SearchBox.Text.Length > 0 ? "没有匹配的仓库。" : "列表为空，可添加本地 Git 仓库。";
    }
    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsBusy)) UpdateButtons();
    }
    private void Repositories_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateButtons();
    private void RepositoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder is not null) SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_view is null) return;
        _view.Refresh();
        UpdateButtons();
    }
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "添加 Git 仓库（可多选）", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        await _viewModel.AddRepositoriesAsync(picker.FolderNames);
        SearchBox.Text = "";
        SelectPath(_viewModel.Repository?.Path);
    }
    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        var prompt = new TextPromptWindow(this, "重命名仓库", "修改在 GitManager 中的显示名称。本地文件夹名称会保持原样。", entry.Name);
        if (prompt.ShowDialog() != true) return;
        _viewModel.RenameRepository(entry, prompt.Value);
        SelectPath(entry.Path);
    }
    private async void Relocate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        var picker = new OpenFolderDialog { Title = "选择仓库的新位置" };
        if (picker.ShowDialog(this) != true) return;
        var index = _viewModel.Repositories.IndexOf(entry);
        await _viewModel.RelocateRepositoryAsync(entry, picker.FolderName);
        SelectPath(index >= 0 && index < _viewModel.Repositories.Count ? _viewModel.Repositories[index].Path : entry.Path);
    }
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        var index = RepositoryList.SelectedIndex;
        await _viewModel.RemoveRepositoryAsync(entry);
        if (RepositoryList.Items.Count > 0) RepositoryList.SelectedIndex = Math.Min(index, RepositoryList.Items.Count - 1);
        UpdateButtons();
    }
    private void Move(int offset)
    {
        if (Selected is not { } entry) return;
        _viewModel.MoveRepository(entry, offset);
        SelectPath(entry.Path);
    }
    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        await _viewModel.OpenRepositoryAsync(entry.Path);
        if (RepositoryEntry.SamePath(_viewModel.Repository?.Path, entry.Path) && !_viewModel.HasError) Close();
    }
    private void RepositoryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsBusy && Selected is not null) Open_Click(sender, e);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
