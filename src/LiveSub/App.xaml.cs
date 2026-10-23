using System.Windows;
using LiveSub.Services;

namespace LiveSub;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            AppPaths.Log("UNHANDLED: " + args.Exception);
            MessageBox.Show(args.Exception.Message, "LiveSub", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppPaths.Log("DOMAIN UNHANDLED: " + args.ExceptionObject);
    }
}
