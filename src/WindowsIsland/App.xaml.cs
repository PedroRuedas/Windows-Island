using System.IO;
using System.Windows;

namespace WindowsIsland;

public partial class App : Application
{
    private Mutex? _singleInstance;

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsIsland");

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\WindowsIsland.SingleInstance", out bool created);
        // After registering the package the island relaunches itself; give the old instance time to exit.
        if (!created && !(e.Args.Contains(Services.PackageRegistration.RestartArgument) && WaitForPreviousInstance(_singleInstance)))
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
        try
        {
            new MainWindow().Show();
        }
        catch (Exception ex)
        {
            // Without this the process would linger windowless (DispatcherUnhandledException swallows it).
            Log(ex);
            Shutdown(1);
        }
    }

    private static bool WaitForPreviousInstance(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (AbandonedMutexException)
        {
            return true; // The previous instance exited without releasing it: ours now.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); } catch (ApplicationException) { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    public static void Log(Exception ex) => Log(ex.ToString());

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(Path.Combine(DataDirectory, "log.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the island.
        }
    }
}
