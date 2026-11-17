using System.Windows;
using System.Windows.Threading;

namespace EverNote2OneNote.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Used by the Windows build pipeline to check the published executable end to end without a user.
        if (e.Args.Length == 3 && e.Args[0] == "--self-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await SelfTest.RunAsync(e.Args[1], e.Args[2]));
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorDialog.Show(Current?.MainWindow, "Something unexpected went wrong.", e.Exception);
        e.Handled = true;
    }
}
