using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WasabiDrive.App.ViewModels;
using WasabiDrive.Core;
using WasabiDrive.Core.Models;

namespace WasabiDrive.App.Services;

/// <summary>
/// Central application service: owns the stores and the <see cref="MountManager"/>, exposes the
/// live collection of mappings, and brokers mount/unmount and persistence for the view models.
/// </summary>
public sealed class AppController
{
    private readonly SettingsStore _settingsStore = new();
    private readonly MappingStore _mappingStore = new();
    private readonly CredentialStore _credentialStore = new();
    private readonly FileLogger _fileLogger = new();

    private MountManager? _mountManager;

    /// <summary>Folder that holds the daily log files.</summary>
    public string LogsDirectory => AppPaths.LogsDir;

    public AppSettings Settings { get; private set; } = new();
    public ObservableCollection<MappingViewModel> Mappings { get; } = new();

    /// <summary>Null when rclone.exe could not be located; the UI surfaces this as a warning.</summary>
    public string? RcloneError { get; private set; }

    /// <summary>Null when WinFsp is installed; otherwise a warning shown in the UI.</summary>
    public string? WinFspError { get; private set; }

    /// <summary>Combined startup warning (rclone and/or WinFsp), or null when all is well.</summary>
    public string? StartupWarning =>
        new[] { RcloneError, WinFspError }.FirstOrDefault(x => x is not null) is null
            ? null
            : string.Join(" ", new[] { RcloneError, WinFspError }.Where(x => x is not null));

    public event Action<string>? LogAppended;

    public void Initialize()
    {
        AppPaths.EnsureCreated();
        Settings = _settingsStore.Load();
        _credentialStore.Load();

        _fileLogger.Log($"--- WasabiDrive {AppInfo.CurrentVersionString} started ---");

        if (!WinFspDetector.IsInstalled())
            WinFspError = "WinFsp is not installed — mounting will fail until it is installed.";

        var rclonePath = AppPaths.ResolveRcloneExe(Settings.RcloneExePath);
        if (rclonePath is null)
        {
            RcloneError = "rclone.exe was not found. Reinstall WasabiDrive or set its path in Settings.";
        }
        else
        {
            _mountManager = new MountManager(rclonePath);
            _mountManager.StatusChanged += OnStatusChanged;
            _mountManager.LogReceived += OnLogReceived;
        }

        foreach (var mapping in _mappingStore.Load())
            Mappings.Add(new MappingViewModel(this, mapping));

        if (StartupWarning is { } warning)
            _fileLogger.Log($"Startup warning: {warning}");
    }

    public WasabiCredentials? GetCredentials(Guid mappingId) => _credentialStore.Get(mappingId);

    /// <summary>Adds a new mapping (or updates an existing one) and persists it + its credentials.</summary>
    public void SaveMapping(Mapping mapping, WasabiCredentials credentials)
    {
        var existing = Mappings.FirstOrDefault(m => m.Model.Id == mapping.Id);
        if (existing is null)
            Mappings.Add(new MappingViewModel(this, mapping));
        else
            existing.UpdateModel(mapping);

        _credentialStore.Set(mapping.Id, credentials);
        PersistMappings();
    }

    public async Task DeleteMappingAsync(MappingViewModel vm)
    {
        await UnmountAsync(vm).ConfigureAwait(true);
        _credentialStore.Remove(vm.Model.Id);
        Mappings.Remove(vm);
        PersistMappings();
    }

    public async Task MountAsync(MappingViewModel vm)
    {
        if (_mountManager is null)
            throw new InvalidOperationException(RcloneError ?? "rclone is unavailable.");

        var creds = _credentialStore.Get(vm.Model.Id)
            ?? throw new InvalidOperationException("No saved credentials for this mapping.");
        await _mountManager.MountAsync(vm.Model, creds).ConfigureAwait(true);
    }

    public async Task UnmountAsync(MappingViewModel vm)
    {
        if (_mountManager is null) return;
        await _mountManager.UnmountAsync(vm.Model.Id).ConfigureAwait(true);
    }

    /// <summary>Mounts every mapping flagged <see cref="Mapping.AutoMount"/>. Used at logon.</summary>
    public async Task MountAutoAsync()
    {
        foreach (var vm in Mappings.Where(m => m.Model.AutoMount))
        {
            try { await MountAsync(vm).ConfigureAwait(true); }
            catch (Exception ex) { AppendLog($"Auto-mount of {vm.DriveTarget} failed: {ex.Message}"); }
        }
    }

    public async Task ShutdownAsync()
    {
        if (_mountManager is not null)
            await _mountManager.UnmountAllAsync().ConfigureAwait(false);
        _fileLogger.Log("--- WasabiDrive shutting down ---");
        _fileLogger.Dispose();
    }

    /// <summary>
    /// Applies an imported <see cref="SettingsBundle"/>: replaces app settings and merges mappings
    /// (same id = update, otherwise add). Credentials are not part of the bundle. Persists both.
    /// </summary>
    public void ApplyImportedBundle(SettingsBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        Settings = bundle.Settings ?? new AppSettings();
        _settingsStore.Save(Settings);

        foreach (var mapping in bundle.Mappings)
        {
            var existing = Mappings.FirstOrDefault(m => m.Model.Id == mapping.Id);
            if (existing is null)
                Mappings.Add(new MappingViewModel(this, mapping));
            else
                existing.UpdateModel(mapping);
        }
        PersistMappings();
    }

    public void SaveSettings() => _settingsStore.Save(Settings);

    public void PersistMappings() => _mappingStore.Save(Mappings.Select(m => m.Model));

    private void OnStatusChanged(object? sender, MountStatusChangedEventArgs e) =>
        RunOnUi(() =>
        {
            var vm = Mappings.FirstOrDefault(m => m.Model.Id == e.MappingId);
            vm?.ApplyStatus(e.State, e.Message);
            if (e.Message is not null)
                AppendLog($"[{vm?.DriveTarget}] {e.State}: {e.Message}");
        });

    private void OnLogReceived(object? sender, MountLogEventArgs e) =>
        RunOnUi(() => AppendLog(e.Line));

    private void AppendLog(string line)
    {
        _fileLogger.Log(line);
        LogAppended?.Invoke(line);
    }

    private static void RunOnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app is null) { action(); return; }
        if (app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.Invoke(action);
    }
}
