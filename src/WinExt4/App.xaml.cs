using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinExt4.UI;

namespace WinExt4;

public partial class App : Application
{
    // One instance per user session: a second launch hands its arguments to
    // the running one (often the background instance started at logon).
    private const string MutexName = @"Local\WinExt4.SingleInstance";
    private const string ShowEventName = @"Local\WinExt4.Show";
    private static readonly string PendingFile = Path.Combine(Path.GetTempPath(), "WinExt4", "pending-open.txt");

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private TrayIcon? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        bool tray = e.Args.Contains("--tray");
        var files = e.Args.Where(File.Exists).Select(Path.GetFullPath).ToList();

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            if (files.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PendingFile)!);
                File.AppendAllLines(PendingFile, files);
            }
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
                ev.Set();
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(OnShowRequested), null, -1, false);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _window = new MainWindow();
        MainWindow = _window;
        _window.Closed += (_, _) => ExitApp();

        if (tray)
        {
            _tray = new TrayIcon(ShowWindow, () => _window.ExitFromTray());
            _window.EnterBackgroundMode(_tray);
        }
        else
        {
            _window.Show();
        }

        foreach (var f in files)
            _window.OpenImage(f);
    }

    private void OnShowRequested()
    {
        ShowWindow();
        if (!File.Exists(PendingFile)) return;
        try
        {
            var files = File.ReadAllLines(PendingFile);
            File.Delete(PendingFile);
            foreach (var f in files.Where(File.Exists))
                _window?.OpenImage(f);
        }
        catch (IOException)
        {
        }
    }

    private void ShowWindow() => _window?.ShowFromBackground();

    private void ExitApp()
    {
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _showEvent?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "WinExt4", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
