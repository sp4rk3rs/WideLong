using System.Windows;

namespace WideLong;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("The operation could not be completed. No recovery file or error report was saved. Your original files are unchanged.", "WideLong", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
        if (e.Args.Contains("--synthetic-check"))
            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is MainWindow window) window.StartSyntheticCheck(e.Args.Contains("--compound-example"));
            });
    }
}
