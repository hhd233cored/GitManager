using System.Windows;

namespace GitManager.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var repository = e.Args.Length >= 2 && e.Args[0] == "--repository" ? e.Args[1] : e.Args.FirstOrDefault();
        var window = new MainWindow(repository);
        MainWindow = window;
        window.Show();
    }
}
