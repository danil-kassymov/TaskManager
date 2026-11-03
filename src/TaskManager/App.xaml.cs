using System.Windows;
using System.Windows.Threading;
using TaskManager.Services;
using TaskManager.ViewModels;
using TaskManager.Views;

namespace TaskManager;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        Services = new AppServices(AppPaths.Default());
        var vm = new MainViewModel(Services);
        MainWindow = new MainWindow(vm);
        MainWindow.Show();
        Services.GDrive.StartPeriodic(TimeSpan.FromMinutes(1)); // pull changes made on other devices
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services?.Dispose();
        base.OnExit(e);
    }

    static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep working after unexpected errors; the data is already persisted per change.
        try
        {
            File.AppendAllText(Path.Combine(Services?.Paths.Root ?? Path.GetTempPath(), "error.log"),
                $"[{DateTime.Now:O}] {e.Exception}\n\n");
        }
        catch (IOException) { }
        MessageBox.Show(e.Exception.Message, "TaskManager — ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
