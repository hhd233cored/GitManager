using System.Windows;
using System.Windows.Controls;
using GitManager.App.Infrastructure;
using GitManager.App.ViewModels;
using GitManager.Contracts;

namespace GitManager.App.Dialogs;

public partial class CloudAccountWindow : Window
{
    private readonly AssistantViewModel _assistant;
    private readonly CloudAccountClient _account;
    private readonly CancellationTokenSource _closed = new();
    private bool _working;

    public CloudAccountWindow(Window owner, AssistantViewModel assistant)
    {
        _assistant = assistant;
        _account = assistant.CloudAccount;
        InitializeComponent();
        Owner = owner;
        ServerAddress.Text = _account.ServerAddress;
        AccountEmail.Text = _account.Email ?? "";
        UpdateControls();
        Loaded += async (_, _) => { if (_account.Email is not null) await RunAsync(RefreshAsync); };
        Closed += (_, _) => { _closed.Cancel(); _closed.Dispose(); };
    }

    private async void Login_Click(object sender, RoutedEventArgs args) => await SignInAsync(register: false);
    private async void Register_Click(object sender, RoutedEventArgs args) => await SignInAsync(register: true);

    private Task SignInAsync(bool register) => RunAsync(async cancellationToken =>
    {
        var password = AccountPassword.Password;
        AccountPassword.Clear();
        await _account.SignInAsync(ServerAddress.Text, AccountEmail.Text, password, register, cancellationToken);
        AccountEmail.Text = _account.Email;
        await RefreshAsync(cancellationToken);
        Status.Text = register ? "已注册并登录。" : "已登录。";
    });

    private void Logout_Click(object sender, RoutedEventArgs args)
    {
        _account.SignOut();
        BackupList.ItemsSource = null;
        Status.Text = "已退出登录。";
        UpdateControls();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args) => await RunAsync(RefreshAsync);

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var backups = await _account.ListBackupsAsync(cancellationToken);
        BackupList.ItemsSource = backups;
    }

    private void Backup_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateControls();

    private async void Restore_Click(object sender, RoutedEventArgs args)
    {
        if (BackupList.SelectedItem is not BackupSummary selection) return;
        await RunAsync(async cancellationToken =>
        {
            var backup = await _account.DownloadAsync(selection.ConversationId, cancellationToken);
            var result = await _assistant.RestoreConversationAsync(backup, replace: false, cancellationToken);
            if (result == ConversationRestore.Conflict)
            {
                if (cancellationToken.IsCancellationRequested || MessageBox.Show(this, $"本机已有不同版本的「{backup.Summary.Title}」。用云端备份替换吗？",
                    "替换本机会话", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                result = await _assistant.RestoreConversationAsync(backup, replace: true, cancellationToken);
            }
            Status.Text = result == ConversationRestore.AlreadyPresent ? "本机已有相同的会话。" : "已恢复。可在助手的历史列表中查看。";
        });
    }

    private async void Delete_Click(object sender, RoutedEventArgs args)
    {
        if (BackupList.SelectedItem is not BackupSummary selection) return;
        if (MessageBox.Show(this, $"删除「{selection.Title}」的云端备份？", "删除云端备份", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunAsync(async cancellationToken =>
        {
            await _account.DeleteAsync(selection.ConversationId, cancellationToken);
            await RefreshAsync(cancellationToken);
            Status.Text = "已删除云端备份。";
        });
    }

    private async void UploadPreferences_Click(object sender, RoutedEventArgs args) => await RunAsync(async cancellationToken =>
    {
        await _account.UploadPreferencesAsync(_assistant.ExportPreferences(), cancellationToken);
        Status.Text = "已上传当前保存的助手偏好。";
    });

    private async void ApplyPreferences_Click(object sender, RoutedEventArgs args) => await RunAsync(async cancellationToken =>
    {
        var preferences = await _account.DownloadPreferencesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (preferences is null) { Status.Text = "这个账号还没有保存偏好。"; return; }
        _assistant.ApplyPreferences(preferences);
        Status.Text = "已应用云端偏好。";
    });

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_working) return;
        var cancellationToken = _closed.Token;
        _working = true;
        Status.Text = "正在处理…";
        UpdateControls();
        try
        {
            await action(cancellationToken);
            if (Status.Text == "正在处理…") Status.Text = "";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (CloudAccountException ex) { Status.Text = ex.Message; }
        catch (Exception ex) when (AssistantConversationStore.IsStorageError(ex)) { Status.Text = "无法访问本机数据。" + AssistantViewModel.SafeError(ex, null); }
        finally
        {
            _working = false;
            if (!cancellationToken.IsCancellationRequested) UpdateControls();
        }
    }

    private void UpdateControls()
    {
        var signedIn = _account.Email is not null;
        if (!signedIn && BackupList.ItemsSource is not null) BackupList.ItemsSource = null;
        ServerAddress.IsReadOnly = AccountEmail.IsReadOnly = signedIn || _working;
        Grid.SetColumnSpan(EmailField, signedIn ? 3 : 1);
        PasswordField.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        AccountPassword.IsEnabled = LoginButton.IsEnabled = RegisterButton.IsEnabled = !signedIn && !_working;
        LoginButton.Visibility = RegisterButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        LogoutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        LogoutButton.IsEnabled = RefreshButton.IsEnabled = BackupList.IsEnabled = signedIn && !_working;
        RestoreButton.IsEnabled = DeleteButton.IsEnabled = signedIn && !_working && BackupList.SelectedItem is not null;
        UploadPreferencesButton.IsEnabled = ApplyPreferencesButton.IsEnabled = signedIn && !_working;
        EmptyBackups.Visibility = BackupList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyBackups.Text = signedIn ? "暂无云端备份" : "登录后查看备份";
    }
}
