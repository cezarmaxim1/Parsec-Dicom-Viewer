using System.Windows;

namespace Parsec.Uninstaller;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var win = new MainWindow();
        win.Show();
    }
}
