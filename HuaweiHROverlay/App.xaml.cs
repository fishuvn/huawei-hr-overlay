using System.Windows;

namespace HuaweiHROverlay;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Global exception handler for unhandled exceptions
        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show(
                $"Unexpected error: {args.Exception.Message}\n\n{args.Exception.StackTrace}",
                "HuaweiHROverlay Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
