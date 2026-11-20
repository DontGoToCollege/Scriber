using System.IO;
using System.Windows;

namespace Scriber;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);
        DispatcherUnhandledException += (_, args) =>
        {
            PortablePaths.Log("unhandled: " + args.Exception);
            args.Handled = true;
            if (smoke)
            {
                File.WriteAllText(Path.Combine(PortablePaths.DataDir, "smoke.txt"), "FAIL unhandled: " + args.Exception);
                Shutdown(1);
                return;
            }
            MessageBox.Show(args.Exception.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
        };
        // a file passed on the command line (Open with, or dropped on the exe) opens at start
        var file = e.Args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
        var window = new MainWindow(smoke, file);
        if (smoke)
        {
            window.RunSmoke();
            Shutdown();
            return;
        }
        window.Show();
    }
}
