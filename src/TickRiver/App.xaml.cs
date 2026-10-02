using System.Threading;
using System.Windows;

namespace TickRiver;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--smoke-test")
        {
            try { SmokeTest.Run(e.Args.Length > 1 ? e.Args[1] : "work/smoke"); Shutdown(0); }
            catch (Exception error) { File.WriteAllText("smoke-error.txt", error.ToString()); Shutdown(1); }
            return;
        }
        _instance = new Mutex(true, "Local\\TickRiver-" + Environment.UserName, out var created);
        if (!created) { System.Windows.MessageBox.Show("TickRiver is already running. Open it from the system tray.", "TickRiver"); Shutdown(); return; }
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TickRiver", "tickriver.sqlite");
            var window = new MainWindow(new Core.Store(path));
            window.Loaded += async (_, _) => await window.ConnectClients(false);
            MainWindow = window; window.Show();
        }
        catch (Exception error) { System.Windows.MessageBox.Show(error.Message, "TickRiver could not start", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
