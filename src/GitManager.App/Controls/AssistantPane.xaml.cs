using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using GitManager.App.Dialogs;
using GitManager.App.Infrastructure;
using GitManager.App.ViewModels;
using GitManager.Services;

namespace GitManager.App.Controls;

public partial class AssistantPane : UserControl
{
    public event Action<OperationPlan>? ReviewRequested;
    private readonly DispatcherTimer _conversationMenuClose = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _conversationMenuRevision;
    private bool _followEnd = true;
    public AssistantPane()
    {
        InitializeComponent();
        _conversationMenuClose.Tick += (_, _) =>
        {
            _conversationMenuClose.Stop();
            if (!AssistantHistoryButton.IsMouseOver && !ConversationMenu.IsMouseOver) CloseConversationMenu();
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseConversationMenu(); };
        DataContextChanged += (_, _) => CloseConversationMenu();
        ConversationScroll.ScrollChanged += (_, args) =>
        {
            if (DataContext is AssistantViewModel { IsViewingHistory: true }) return;
            if (args.ExtentHeightChange == 0) _followEnd = ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset < 30;
            else if (_followEnd) ConversationScroll.ScrollToEnd();
        };
    }

    private async void ConversationTitle_MouseEnter(object sender, MouseEventArgs args) => await ShowConversationMenuAsync();
    private async void ConversationTitle_Click(object sender, RoutedEventArgs args) => await ShowConversationMenuAsync();
    private void ConversationMenu_MouseEnter(object sender, MouseEventArgs args) => _conversationMenuClose.Stop();
    private void ConversationMenu_MouseLeave(object sender, MouseEventArgs args)
    {
        if (ConversationMenu.Visibility != Visibility.Visible) return;
        _conversationMenuClose.Stop();
        _conversationMenuClose.Start();
    }

    private async Task ShowConversationMenuAsync()
    {
        _conversationMenuClose.Stop();
        if (ConversationMenu.Visibility == Visibility.Visible || DataContext is not AssistantViewModel vm) return;
        ConversationMenu.Visibility = Visibility.Visible;
        await LoadConversationMenuAsync(vm);
    }

    private async Task LoadConversationMenuAsync(AssistantViewModel vm)
    {
        var revision = ++_conversationMenuRevision;
        ConversationList.ItemsSource = null;
        SetConversationMenuStatus("正在读取会话…");
        try
        {
            var conversations = await vm.ListConversationsAsync();
            if (revision != _conversationMenuRevision) return;
            ConversationList.ItemsSource = conversations;
            SetConversationMenuStatus(conversations.Count == 0 ? "暂无历史会话" : "");
        }
        catch (Exception ex) when (AssistantConversationStore.IsStorageError(ex))
        {
            if (revision == _conversationMenuRevision)
                SetConversationMenuStatus("无法读取会话列表。" + AssistantViewModel.SafeError(ex, null));
        }
    }

    private void SetConversationMenuStatus(string text)
    {
        ConversationMenuStatus.Text = text;
        ConversationMenuStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseConversationMenu()
    {
        _conversationMenuClose.Stop();
        _conversationMenuRevision++;
        ConversationMenu.Visibility = Visibility.Collapsed;
    }

    private async void OpenConversation_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: AssistantConversation conversation } || DataContext is not AssistantViewModel vm) return;
        CloseConversationMenu();
        await vm.ViewConversationAsync(conversation);
    }

    private async void DeleteConversation_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: AssistantConversation conversation } || DataContext is not AssistantViewModel vm || vm.IsBusy) return;
        var choice = MessageBox.Show(Window.GetWindow(this), $"删除本机历史中的「{conversation.Title}」？", "删除会话",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes) return;
        try
        {
            await vm.DeleteConversationAsync(conversation);
            if (ConversationMenu.Visibility == Visibility.Visible) await LoadConversationMenuAsync(vm);
        }
        catch (Exception ex) when (AssistantConversationStore.IsStorageError(ex))
        {
            await ShowConversationMenuAsync();
            SetConversationMenuStatus("无法删除这段会话。" + AssistantViewModel.SafeError(ex, null));
        }
    }

    private void Pane_PreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || ConversationMenu.Visibility != Visibility.Visible) return;
        var restoreTitleFocus = ConversationMenu.IsKeyboardFocusWithin;
        CloseConversationMenu();
        if (restoreTitleFocus) AssistantHistoryButton.Focus();
        args.Handled = true;
    }

    private void ConversationMessages_TargetUpdated(object sender, DataTransferEventArgs args)
    {
        if (args.Property != ItemsControl.ItemsSourceProperty) return;
        Dispatcher.InvokeAsync(() =>
        {
            _followEnd = DataContext is not AssistantViewModel { IsViewingHistory: true };
            if (_followEnd) ConversationScroll.ScrollToEnd();
            else ConversationScroll.ScrollToTop();
        }, DispatcherPriority.Loaded);
    }

    private void Input_KeyDown(object sender, KeyEventArgs args)
    {
        if (HandleShortcut(args.Key, Keyboard.Modifiers)) args.Handled = true;
    }
    public bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (DataContext is not AssistantViewModel vm) return false;
        if (key == Key.Enter && modifiers.HasFlag(ModifierKeys.Control))
        {
            if (vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
            return true;
        }
        if (key == Key.Escape && vm.IsBusy) { vm.CancelCommand.Execute(null); return true; }
        return false;
    }
    private void ReviewPlan_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: AssistantPlanItem item }) ReviewRequested?.Invoke(item.Plan);
    }
    private void DeclinePlan_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: AssistantPlanItem item } && DataContext is AssistantViewModel vm) vm.DeclinePlan(item.Plan);
    }
    private async void Source_Requested(object? sender, SourceNavigationEventArgs args)
    {
        if (DataContext is not AssistantViewModel { IsViewingHistory: false } vm) return;
        try { await SourcePreviewWindow.ShowAsync(Window.GetWindow(this), vm.RepositoryTools, args.Uri); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ToolErrors.Message(ex), "无法打开源码引用", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
