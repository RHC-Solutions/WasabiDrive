using System.Windows;
using WasabiDrive.App.ViewModels;

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
        DialogResult = true;
    }
}
