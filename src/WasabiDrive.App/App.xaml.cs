using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using WasabiDrive.App.Services;
using WasabiDrive.App.ViewModels;
using WasabiDrive.App.Views;
using WasabiDrive.Core;

namespace WasabiDrive.App;

public partial class App : Application
{
    private const string MutexName = "WasabiDrive.SingleInstance.v1";

    private Mutex? _singleInstanceMutex;
    private AppController? _controller;
    private MainWindow? _mainWindow;
    private TaskbarIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Explorer right-click verb: run the action standalone (no UI, no single-instance lock) and exit.
        var shellIdx = Array.FindIndex(e.Args, a => a.Equals("--shell", StringComparison.OrdinalIgnoreCase));
        if (shellIdx >= 0 && e.Args.Length >= shellIdx + 3)
        {
            ShellCommand.Run(e.Args[shellIdx + 1], e.Args[shellIdx + 2]);
            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isNew);
        if (!isNew)
        {
            MessageBox.Show("WasabiDrive is already running (see the system tray).",
                "WasabiDrive", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var silentAutoMount = e.Args.Contains("--silent-automount", StringComparer.OrdinalIgnoreCase);

        _controller = new AppController();
        _controller.Initialize();

        var vm = new MainViewModel(_controller);
        _mainWindow = new MainWindow(vm, _controller);

        _trayIcon = TrayIconFactory.Create(
            onOpen: ShowMainWindow,
            onMountAuto: () => _ = _controller.MountAutoAsync(),
            onUnmountAll: () => _ = _controller.ShutdownAsync(),
            onExit: ExitApp);

        // Register the scoped Explorer right-click menu, and keep it in sync with the mappings.
        ShellMenu.Register(Environment.ProcessPath, _controller.GetShellRoots());
        _controller.MappingsChanged += () =>
            ShellMenu.Register(Environment.ProcessPath, _controller.GetShellRoots());

        if (!silentAutoMount)
            ShowMainWindow();

        // Auto-mount flagged drives on every launch.
        _ = _controller.MountAutoAsync();

        // Offer updates on interactive launches when enabled (silent on failure / when current).
        if (!silentAutoMount && _controller.Settings.AutoCheckForUpdates)
            _ = UpdateCoordinator.CheckAsync(_mainWindow, userInitiated: false);
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ExitApp()
    {
        if (_controller is not null)
        {
            try { _controller.ShutdownAsync().GetAwaiter().GetResult(); }
            catch { /* best-effort unmount */ }
        }
        if (_mainWindow is not null)
            _mainWindow.AllowClose = true;
        _trayIcon?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Registers/removes the logon Scheduled Task; called from the Settings dialog.</summary>
    public static void ApplyAutoMountSetting(bool enabled)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
            return;
        try { new AutoMountManager(exePath).SetEnabled(enabled); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not update the login task: {ex.Message}",
                "WasabiDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
