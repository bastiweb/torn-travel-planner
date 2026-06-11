using System.Windows;
using System.Windows.Threading;
using TornStockTravel.Services;

namespace TornStockTravel;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        AppLogService.Info("Application started.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogService.Info("Application exited.");

        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;

        base.OnExit(e);
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogService.Error("Unhandled UI exception.", e.Exception);
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        string message = e.ExceptionObject is Exception exception
            ? exception.ToString()
            : e.ExceptionObject?.ToString() ?? "Unknown exception.";
        AppLogService.Error($"Unhandled application exception. Terminating: {e.IsTerminating}{Environment.NewLine}{message}");
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogService.Error("Unobserved task exception.", e.Exception);
    }
}
