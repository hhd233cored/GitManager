using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GitManager.App;
using GitManager.App.ViewModels;

internal static class MenuPlacementChecks
{
    public static async Task RunAsync(MainWindow window, string output)
    {
        foreach (var name in new[] { "RepositorySelectorButton", "BranchSelectorButton" })
        {
            var button = (Button)window.FindName(name);
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var menu = button.ContextMenu ?? throw new Exception("Selector did not create its menu.");
            await Below(window, button, menu, button.ActualHeight, Path.Combine(output, name + "-anchored.png"));
            // Exercise WPF's service path too: it replaces the menu's own placement with MousePoint.
            menu.IsOpen = false;
            var service = typeof(ContextMenu).Assembly.GetType("System.Windows.Controls.PopupControlService")!;
            var current = service.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            var open = service.GetMethod("RaiseContextMenuOpeningEvent", BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(IInputElement), typeof(double), typeof(double), typeof(bool)], null)!;
            open.Invoke(current, [button, button.ActualWidth * .7, button.ActualHeight * .4, false]);
            await Below(window, button, menu, button.ActualHeight, Path.Combine(output, name + "-right-click.png"));
            menu.IsOpen = false;
        }
        var vm = window.ViewModel;
        var commit = vm.SelectedCommit!;
        await vm.EnsureCommitFilesAsync(commit);
        commit.Details.IsExpanded = true;
        var list = (ListBox)window.FindName("CommitList");
        list.ScrollIntoView(commit);
        window.UpdateLayout();
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(commit);
        var text = Descendants<TextBlock>(row).Single(block => block.Name == "CommitSummaryText");
        var mouseX = Math.Clamp(Mouse.GetPosition(row).X, 0, row.ActualWidth);
        var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        { RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent, Source = text };
        list.RaiseEvent(rightClick);
        var history = row.ContextMenu ?? throw new Exception("History did not create its context menu.");
        if (row.ActualHeight <= CommitItem.RowHeight) throw new Exception("History placement check needs an expanded row.");
        await Below(window, row, history, CommitItem.RowHeight, Path.Combine(output, "history-menu-anchored.png"), mouseX);
        history.IsOpen = false;
        Console.WriteLine("PASS actual popup bounds below repository/branch buttons and an expanded History header, including WPF service reopening.");
    }

    private static async Task Below(MainWindow window, FrameworkElement target, ContextMenu menu, double height, string path, double x = 0)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        await Task.Delay(120);
        if (!menu.IsOpen || PresentationSource.FromVisual(menu) is null) throw new Exception("Context menu closed before placement could be measured.");
        var expected = target.PointToScreen(new Point(x, height));
        var actual = menu.PointToScreen(new Point());
        if (Math.Abs(actual.X - expected.X) > 2 || Math.Abs(actual.Y - expected.Y) > 2)
            throw new Exception($"Menu should start below its owner at {expected}, but opened at {actual}.");
        var content = (FrameworkElement)window.Content;
        var origin = content.PointToScreen(new Point());
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var overlay = new DrawingVisual();
        using (var dc = overlay.RenderOpen())
            dc.DrawRectangle(new VisualBrush(menu), null, new Rect((actual.X - origin.X) / dpi.DpiScaleX, (actual.Y - origin.Y) / dpi.DpiScaleY, menu.ActualWidth, menu.ActualHeight));
        bitmap.Render(overlay);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
