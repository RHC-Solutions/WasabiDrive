using System.Windows;
using WasabiDrive.App.ViewModels;
using WasabiDrive.Core.Models;

namespace WasabiDrive.App.Views;

public partial class MappingEditWindow : Window
{
    private readonly MappingEditViewModel _vm;

    public MappingEditWindow(MappingEditViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        // PasswordBox can't be data-bound, so seed and read it manually.
        SecretBox.Password = vm.SecretAccessKey;
    }

    /// <summary>
    /// Picks the *parent* location and creates a named folder inside it, as OneDrive does. Taking the
    /// chosen folder as the sync root directly was a trap: selecting a drive root would have made the
    /// entire volume one sync root.
    /// </summary>
    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where the folder should be created",
        };

        // Start at the current folder's parent so "Change…" opens somewhere recognisable.
        try
        {
            var parent = System.IO.Path.GetDirectoryName(_vm.EffectiveFolderPath);
            if (!string.IsNullOrWhiteSpace(parent) && System.IO.Directory.Exists(parent))
                dlg.InitialDirectory = parent;
        }
        catch { /* the picker's own default is fine */ }

        if (dlg.ShowDialog(this) != true) return;

        _vm.SetFolderFromParent(dlg.FolderName);

        // Surface an unusable choice immediately rather than at save time.
        if (OnDemandFolderRules.Validate(_vm.LocalFolderPath) is { } error)
        {
            MessageBox.Show(this, error, "Can't use that location",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _vm.UseDefaultFolder();
        }
    }

    private void OnUseDefaultFolder(object sender, RoutedEventArgs e) => _vm.UseDefaultFolder();

    private void OnBrowseCacheDir(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose cache location" };
        if (!string.IsNullOrWhiteSpace(_vm.CacheDir) && System.IO.Directory.Exists(_vm.CacheDir))
            dlg.InitialDirectory = _vm.CacheDir;
        if (dlg.ShowDialog(this) == true)
            _vm.CacheDir = dlg.FolderName;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm.SecretAccessKey = SecretBox.Password;

        if (_vm.Validate() is { } error)
        {
            MessageBox.Show(error, "Missing information", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_vm.IsOnDemandMode && !ConfirmFolderChoice()) return;

        DialogResult = true;
    }

    /// <summary>
    /// Confirms the two folder choices that lose data rather than merely being wrong: adopting a
    /// folder that already has files in it, and moving an existing mapping to a new folder.
    /// </summary>
    private bool ConfirmFolderChoice()
    {
        if (_vm.FolderIsMoving)
        {
            var move = MessageBox.Show(this,
                $"This mapping currently uses:\n{_vm.OriginalFolderPath}\n\n" +
                $"It will start using:\n{_vm.EffectiveFolderPath}\n\n" +
                "The old folder is left on disk and is not moved or deleted — its placeholders stop " +
                "working, so remove it yourself once you're happy. Continue?",
                "Change folder location", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (move != MessageBoxResult.Yes) return false;
        }

        if (OnDemandFolderRules.HasExistingContent(_vm.EffectiveFolderPath))
        {
            var adopt = MessageBox.Show(this,
                $"{_vm.EffectiveFolderPath}\n\nalready contains files. Using it means Windows takes it " +
                "over as a sync folder: existing files are matched against the bucket, and anything " +
                "not in the bucket may be uploaded or removed to make the two match.\n\n" +
                "An empty folder is safer. Use this folder anyway?",
                "Folder is not empty", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (adopt != MessageBoxResult.Yes) return false;
        }

        return true;
    }
}
