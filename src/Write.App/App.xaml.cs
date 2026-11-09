using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Write.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && e.Args[0] == "--selftest")
        {
            SelfTest.Run(this, e.Args);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        var files = e.Args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (files.Count == 0)
        {
            new MainWindow().Show();
            return;
        }
        foreach (var file in files)
        {
            var window = new MainWindow();
            window.Show();
            window.OpenPath(file);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the application (and the user's document) alive; they can still save their work.
        e.Handled = true;
        try
        {
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Write", "error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTime.Now:O}\n{e.Exception}\n\n");
        }
        catch (Exception)
        {
            // Logging is best effort.
        }
        MessageBox.Show(
            "Er ging iets mis bij deze bewerking. Uw document is nog geopend; sla het voor de zekerheid op onder een nieuwe naam.\n\n" + e.Exception.Message,
            "Write", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
