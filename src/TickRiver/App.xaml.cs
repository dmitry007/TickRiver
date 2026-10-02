using System.Windows;

namespace TickRiver;

public partial class App : System.Windows.Application
{
    private SingleInstance? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, error) => CrashLog.Write(error.Exception, "UI exception");
        AppDomain.CurrentDomain.UnhandledException += (_, error) => CrashLog.Write(error.ExceptionObject, "Unhandled exception");
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--smoke-test")
        {
            try { SmokeTest.Run(e.Args.Length > 1 ? e.Args[1] : "work/smoke"); Shutdown(0); }
            catch (Exception error) { File.WriteAllText("smoke-error.txt", error.ToString()); Shutdown(1); }
            return;
        }
        _instance = new SingleInstance("Local\\TickRiver-" + Environment.UserName, () => {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.Restore());
        });
        if (!_instance.IsFirst) { Shutdown(); return; }
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TickRiver", "tickriver.sqlite");
            var window = new MainWindow(new Core.Store(path));
            window.Loaded += async (_, _) => await window.ConnectClients(false);
            MainWindow = window; window.Show();
        }
        catch (Exception error) { var log = CrashLog.Write(error, "Startup exception"); System.Windows.MessageBox.Show(error.Message + (log == null ? "" : "\n\nDiagnostic log: " + log), "TickRiver could not start", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
