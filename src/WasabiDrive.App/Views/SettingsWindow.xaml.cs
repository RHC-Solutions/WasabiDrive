using System.Windows;
using Microsoft.Win32;
using WasabiDrive.App.Services;
using WasabiDrive.App.ViewModels;
using WasabiDrive.Core;
using WasabiDrive.Core.Models;

namespace WasabiDrive.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private readonly AppController _controller;

    public SettingsWindow(SettingsViewModel vm, AppController controller)
    {
        InitializeComponent();
        _vm = vm;
        _controller = controller;
        DataContext = vm;
    }

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnBrowseCacheDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose default cache location" };
        if (!string.IsNullOrWhiteSpace(_vm.DefaultCacheDir) && System.IO.Directory.Exists(_vm.DefaultCacheDir))
            dlg.InitialDirectory = _vm.DefaultCacheDir;
        if (dlg.ShowDialog(this) == true)
            _vm.DefaultCacheDir = dlg.FolderName;
    }

    private void OnAbout(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(_controller.LogsDirectory);
        UpdateCoordinator.OpenUrl(_controller.LogsDirectory);
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export settings",
            FileName = "WasabiDrive-settings.json",
            DefaultExt = ".json",
            Filter = "JSON (*.json)|*.json|XML (*.xml)|*.xml",
        };
        if (dlg.ShowDialog(this) != true) return;

        // Snapshot the on-screen edits so the export matches what the user sees.
        var snapshot = new AppSettings();
        _vm.ApplyTo(snapshot);

        try
        {
            SettingsPortability.Export(dlg.FileName, snapshot, _controller.Mappings.Select(m => m.Model));
            MessageBox.Show(this,
                $"Exported {_controller.Mappings.Count} mapping(s) and settings to:\n{dlg.FileName}\n\n" +
                "Secret keys were not included — re-enter them after importing.",
                "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed:\n{ex.Message}",
                "WasabiDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import settings",
            Filter = "Settings files (*.json;*.xml)|*.json;*.xml|JSON (*.json)|*.json|XML (*.xml)|*.xml",
        };
        if (dlg.ShowDialog(this) != true) return;

        SettingsBundle bundle;
        try
        {
            bundle = SettingsPortability.Import(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Import failed:\n{ex.Message}",
                "WasabiDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Import {bundle.Mappings.Count} mapping(s) and app settings from:\n{dlg.FileName}\n\n" +
            "Existing mappings with the same id are updated; others are added. " +
            "Secret keys are not imported — you'll re-enter them per mapping.\n\nContinue?",
            "Confirm import", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        _controller.ApplyImportedBundle(bundle);
        App.ApplyAutoMountSetting(_controller.Settings.StartAtLogin);

        MessageBox.Show(this, "Import complete.",
            "WasabiDrive", MessageBoxButton.OK, MessageBoxImage.Information);

        // Settings were applied and persisted directly; close without the normal save path.
        DialogResult = false;
    }
}
