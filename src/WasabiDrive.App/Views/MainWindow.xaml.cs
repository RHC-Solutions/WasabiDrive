using System.ComponentModel;
using System.Windows;
using WasabiDrive.App.Services;
using WasabiDrive.App.ViewModels;

namespace WasabiDrive.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppController _controller;

    /// <summary>Set by the tray "Exit" command so closing really exits instead of hiding.</summary>
    public bool AllowClose { get; set; }

    public MainWindow(MainViewModel vm, AppController controller)
    {
        InitializeComponent();
        _vm = vm;
        _controller = controller;
        DataContext = vm;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var editVm = new MappingEditViewModel(null, null, _controller.Settings);
        if (ShowEditor(editVm))
            _controller.SaveMapping(editVm.BuildMapping(), editVm.BuildCredentials());
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMapping is not { } selected) return;
        var creds = _controller.GetCredentials(selected.Model.Id);
        var editVm = new MappingEditViewModel(selected.Model, creds, _controller.Settings);
        if (ShowEditor(editVm))
            _controller.SaveMapping(editVm.BuildMapping(), editVm.BuildCredentials());
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMapping is not { } selected) return;
        var confirm = MessageBox.Show(
            $"Delete mapping '{selected.Name}' ({selected.DriveTarget})? This will unmount it and remove its saved credentials.",
            "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes)
            await _controller.DeleteMappingAsync(selected);
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var settingsVm = new SettingsViewModel(_controller.Settings);
        var win = new SettingsWindow(settingsVm, _controller) { Owner = this };
        if (win.ShowDialog() == true)
        {
            settingsVm.ApplyTo(_controller.Settings);
            _controller.SaveSettings();
            App.ApplyAutoMountSetting(_controller.Settings.StartAtLogin);
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    private bool ShowEditor(MappingEditViewModel editVm)
    {
        var win = new MappingEditWindow(editVm) { Owner = this };
        return win.ShowDialog() == true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window hides to tray unless the user chose Exit.
        if (!AllowClose && _controller.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
