using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TerraCompanion.App;

public partial class App : Application
{
    static bool _showingError;

    public static string ErrorLogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TerraCompanion", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        bool snapshot = e.Args.Length >= 2 && e.Args[0] == "--snapshot";
        ThemeManager.Apply(snapshot && e.Args.Length == 3 ? e.Args[2] : AppState.Settings.Theme);
        var window = new MainWindow();
        if (snapshot)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000; window.Top = 0; window.ShowActivated = false;
            window.Show();
            _ = Snapshot.Run(window, e.Args[1]);
            return;
        }
        window.Show();
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            File.AppendAllText(ErrorLogPath, $"{DateTime.Now:u}  {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
        // Never stack message boxes: a modal box pumps messages, which can raise the same error again.
        if (_showingError) return;
        _showingError = true;
        try
        {
            MessageBox.Show(e.Exception.Message + "\n\nDetails were written to " + ErrorLogPath, "TerraCompanion - something went wrong",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _showingError = false; }
    }
}
