using System.Windows;
using System.Windows.Threading;
using Launchpad.Services;

namespace Launchpad;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private Hub? _hub;
    private MainWindow? _window;
    private Tray? _tray;
    private readonly DispatcherTimer _checks = new() { Interval = TimeSpan.FromHours(6) };

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--selftest")) { Shutdown(SelfTest.Run()); return; }
        if (e.Args is ["--shots", var shots]) { Dispatcher.InvokeAsync(async () => { await SelfTest.Shots(shots); Shutdown(); }); return; }
        // the elevated copy started by Installer.Elevate: installs the batch and leaves, no window
        if (e.Args is ["--install", var batch]) { Shutdown(Installer.RunElevated(batch)); return; }

        // One Launchpad per user: a second start (the Start menu while it sits in the tray) brings the first one's window up.
        _singleInstance = new Mutex(true, @"Local\ProtagonistLabs.Launchpad", out bool first);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ProtagonistLabs.Launchpad.Show");
        if (!first) { show.Set(); Shutdown(); return; }

        AppLog.Write($"Launchpad {Updater.Current} started");
        DispatcherUnhandledException += (_, args) => AppLog.Write($"Crash: {args.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Write($"Crash: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) => AppLog.Write($"Error in background work: {args.Exception}");
        base.OnStartup(e);

        _hub = new Hub { ShowWindow = ShowWindow };
        _tray = new Tray(_hub, ShowWindow, Quit);
        _hub.Scan();
        if (!e.Args.Contains("--tray")) ShowWindow();
        WhatsNewWindow.OpenOnceAfterUpdate();

        new Thread(() => { while (show.WaitOne()) Dispatcher.Invoke(ShowWindow); }) { IsBackground = true }.Start();

        // a check 30 s after start (logon is busy enough), then every 6 hours
        var firstCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        firstCheck.Tick += async (_, _) => { firstCheck.Stop(); await CheckAll(); };
        firstCheck.Start();
        _checks.Tick += async (_, _) => await CheckAll();
        _checks.Start();

        // only an installed copy writes its card, so a build run from bin\ never tells the sisters where Launchpad is
        if (Updater.Installed) new Thread(() =>
        {
            try { Ecosystem.WriteCard(Environment.ProcessPath ?? "", Updater.Current); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { AppLog.Write($"Couldn't write the app card: {ex.Message}"); }
        }) { IsBackground = true, Priority = ThreadPriority.Lowest }.Start();
    }

    private async Task CheckAll()
    {
        await _hub!.Check();
        await Updater.CheckAndOffer(Quit);
    }

    public Task CheckNow() => CheckAll();

    private void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(_hub!, this);
            _window.Closed += (_, _) => _window = null;
        }
        _hub!.Scan();
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Quit()
    {
        _tray?.Dispose();
        Shutdown();
    }
}
