using System.Windows;

namespace WideLong;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            // Show what went wrong (the message only, never data or a stack trace). Nothing is logged or sent anywhere.
            var ex = args.Exception is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : args.Exception;
            string detail = ex is OutOfMemoryException ? "There was not enough memory. Select fewer columns or smaller files." : $"{ex.Message} ({ex.GetType().Name})";
            MessageBox.Show("The operation could not be completed.\n\n" + detail + "\n\nNo recovery file or error report was saved. Your original files are unchanged; Undo is still available.", "WideLong", MessageBoxButton.OK, MessageBoxImage.Error);
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
