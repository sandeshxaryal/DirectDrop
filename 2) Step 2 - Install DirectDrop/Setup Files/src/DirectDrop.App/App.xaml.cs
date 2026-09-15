using System.Windows;
using System.Windows.Threading;
using DirectDrop.App.Services;

namespace DirectDrop.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Every unhandled exception goes to the log file before anything
        // else happens - a silent crash is exactly what the spec's "the
        // application should never silently fail" principle rules out.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("Unhandled UI exception", args.Exception);
            System.Windows.MessageBox.Show(
                $"DirectDrop hit an unexpected error and needs to close this operation.\n\n{args.Exception.Message}\n\nDetails were written to the log file.",
                "DirectDrop", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLogger.Error("Unhandled background exception", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        AppLogger.Info("DirectDrop starting up.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info("DirectDrop shutting down.");
        base.OnExit(e);
    }
}
