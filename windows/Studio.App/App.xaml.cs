using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Lernheft.Studio.App;

/// <summary>
/// Start der App: eine einzige laufende Instanz, Daten laden, Farben setzen, Hauptfenster zeigen.
/// <c>--data &lt;Ordner&gt;</c> nimmt einen anderen Datenordner, <c>--shots &lt;Ordner&gt;</c> erzeugt Bildschirmfotos
/// aller Ansichten mit Beispieldaten (für die automatische Prüfung).
/// </summary>
public partial class App : Application
{
    private const string InstanceName = "Lernheft.Studio.SingleInstance";
    private Mutex? _mutex;
    private EventWaitHandle? _wake;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? data = null;
        string? shots = null;
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--data" && i + 1 < e.Args.Length) data = e.Args[++i];
            else if (e.Args[i] == "--shots" && i + 1 < e.Args.Length) shots = e.Args[++i];
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Services.Log("Absturz: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Services.Log("Hintergrundfehler: " + args.Exception);
            args.SetObserved();
        };

        if (shots is not null)
        {
            Harness.Run(this, Path.GetFullPath(shots));
            return;
        }

        if (data is null && !ClaimInstance())
        {
            Shutdown();
            return;
        }

        try
        {
            Services.Init(data);
        }
        catch (Exception error)
        {
            MessageBox.Show("Lernheft Studio kann seine Daten nicht öffnen:\n\n" + error.Message, "Lernheft Studio",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        Theme.Restore();
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>Läuft die App schon, wird sie nach vorne geholt und diese zweite Instanz beendet sich.</summary>
    private bool ClaimInstance()
    {
        _mutex = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            try
            {
                using var other = EventWaitHandle.OpenExisting(InstanceName + ".Wake");
                other.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return false;
        }
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Wake");
        var thread = new Thread(() =>
        {
            while (_wake.WaitOne())
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (MainWindow is not { } window) return;
                    if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                    window.Show();
                    window.Activate();
                    window.Topmost = true;
                    window.Topmost = false;
                });
            }
        }) { IsBackground = true, Name = "Wake" };
        thread.Start();
        return true;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Services.Log("Fehler: " + e.Exception);
        e.Handled = true;
        if (e.Exception is OutOfMemoryException) return;
        MainWindow?.Dispatcher.BeginInvoke(() =>
        {
            if (global::Lernheft.Studio.App.MainWindow.Current is { } main)
                main.ShowBanner("Da ist etwas schiefgelaufen: " + e.Exception.Message + " – deine Notizen sind gespeichert.");
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Services.Pad?.Dispose(); }
        catch (Exception) { }
        _mutex?.Dispose();
        _wake?.Dispose();
        base.OnExit(e);
    }
}
