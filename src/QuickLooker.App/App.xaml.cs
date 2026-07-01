using System.IO;
using System.Windows;

namespace QuickLooker.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        var initialPath = e.Args.FirstOrDefault(arg => File.Exists(arg) || Directory.Exists(arg));

        if (initialPath is not null)
        {
            window.Loaded += (_, _) =>
            {
                _ = window.Dispatcher.InvokeAsync(async () => await window.OpenPathAsync(initialPath));
            };
        }

        window.Show();
    }
}
