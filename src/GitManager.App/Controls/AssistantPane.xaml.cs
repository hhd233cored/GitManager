using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GitManager.App.ViewModels;
using GitManager.Services;

namespace GitManager.App.Controls;

public partial class AssistantPane : UserControl
{
    public event Action<OperationPlan>? ReviewRequested;
    private bool _followEnd = true;
    public AssistantPane()
    {
        InitializeComponent();
        ConversationScroll.ScrollChanged += (_, args) =>
        {
            if (args.ExtentHeightChange == 0) _followEnd = ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset < 30;
            else if (_followEnd) ConversationScroll.ScrollToEnd();
        };
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
}
