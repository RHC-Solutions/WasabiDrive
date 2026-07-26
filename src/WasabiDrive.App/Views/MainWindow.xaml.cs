using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WasabiDrive.App.Services;
using WasabiDrive.App.ViewModels;
using WasabiDrive.Core.Models;

namespace WasabiDrive.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppController _controller;

    /// <summary>True while the log view is pinned to the newest line (the default).</summary>
    private bool _logFollowTail = true;

    /// <summary>Set while we reposition the log ourselves, so it isn't mistaken for a user scroll.</summary>
    private bool _logAutoScrolling;

    /// <summary>Last offset the user was reading at, restored after a log append.</summary>
    private double _logUserOffset;

    /// <summary>Set by the tray "Exit" command so closing really exits instead of hiding.</summary>
    public bool AllowClose { get; set; }

    public MainWindow(MainViewModel vm, AppController controller)
    {
        InitializeComponent();
        _vm = vm;
        _controller = controller;
        DataContext = vm;
    }

    /// <summary>True when a row is selected, so menus can grey out Edit/Delete like the buttons do.</summary>
    public bool HasSelection => _vm.SelectedMapping is not null;

    // Public entry points so the tray menu can invoke the same actions as the header buttons
    // instead of duplicating the logic.
    public void InvokeAdd() => OnAdd(this, new RoutedEventArgs());
    public void InvokeEdit() => OnEdit(this, new RoutedEventArgs());
    public void InvokeDelete() => OnDelete(this, new RoutedEventArgs());
    public void InvokeSettings() => OnSettings(this, new RoutedEventArgs());
    public void InvokeAbout() => OnAbout(this, new RoutedEventArgs());

    /// <summary>
    /// Right-clicking a row selects it first, so the row menu's Edit/Delete (and the header
    /// buttons) act on the row the user actually pointed at.
    /// </summary>
    private void OnListPreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement((ListView)sender, (DependencyObject)e.OriginalSource)
            as ListViewItem;
        if (item is not null)
            item.IsSelected = true;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var editVm = new MappingEditViewModel(null, null, _controller.Settings, OtherOnDemandFolders(null));
        if (ShowEditor(editVm))
            _controller.SaveMapping(editVm.BuildMapping(), editVm.BuildCredentials());
    }

    private void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMapping is not { } selected) return;
        var creds = _controller.GetCredentials(selected.Model.Id);
        var editVm = new MappingEditViewModel(selected.Model, creds, _controller.Settings,
            OtherOnDemandFolders(selected.Model.Id));
        if (ShowEditor(editVm))
            _controller.SaveMapping(editVm.BuildMapping(), editVm.BuildCredentials());
    }

    /// <summary>
    /// Folders already used by other on-demand mappings, so the editor can reject an overlapping
    /// choice — two Cloud Files sync roots must not contain one another.
    /// </summary>
    private IReadOnlyList<string> OtherOnDemandFolders(Guid? excludeId) =>
        _controller.Mappings
            .Where(m => m.Model.Mode == MappingMode.OnDemandFolder && m.Model.Id != excludeId)
            .Select(m => _controller.GetOnDemandFolder(m.Model))
            .ToList();

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
        var oldCacheDir = _controller.Settings.DefaultCache.CacheDir;
        var win = new SettingsWindow(settingsVm, _controller) { Owner = this };
        if (win.ShowDialog() == true)
        {
            settingsVm.ApplyTo(_controller.Settings);
            _controller.SaveSettings();
            App.ApplyAutoMountSetting(_controller.Settings.StartAtLogin);

            // The default cache location only affects NEW mappings; offer to apply it to existing ones.
            var newCacheDir = _controller.Settings.DefaultCache.CacheDir;
            if (!string.Equals(newCacheDir, oldCacheDir, StringComparison.OrdinalIgnoreCase)
                && _controller.Mappings.Count > 0)
            {
                var shown = string.IsNullOrWhiteSpace(newCacheDir) ? "rclone default" : newCacheDir;
                var choice = MessageBox.Show(this,
                    $"Apply the cache location ({shown}) to all {_controller.Mappings.Count} existing mapping(s)?\n\n" +
                    "They'll use it the next time you unmount and remount.",
                    "Apply cache location", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Yes)
                    _controller.ApplyDefaultCacheDirToAllMappings();
            }
        }
    }

    private void OnAbout(object sender, RoutedEventArgs e) =>
        new AboutWindow { Owner = this }.ShowDialog();

    // The activity log binds to a single string that the view model rebuilds on every appended
    // line. Assigning TextBox.Text resets the scroll offset to 0, which yanked the view back to
    // the top on each new log line. These two handlers keep the log where the reader wants it:
    // pinned to the newest line by default, or held at the line they scrolled up to.

    private void OnLogTextChanged(object sender, TextChangedEventArgs e)
    {
        _logAutoScrolling = true;
        if (_logFollowTail)
            LogBox.ScrollToEnd();
        else
            LogBox.ScrollToVerticalOffset(_logUserOffset);
        _logAutoScrolling = false;
    }

    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Ignore our own repositioning and the offset shifts caused by the content growing;
        // only a deliberate user scroll changes whether we follow the tail.
        if (_logAutoScrolling || e.ExtentHeightChange != 0d || e.VerticalChange == 0d)
            return;

        _logUserOffset = e.VerticalOffset;
        // Within a line of the bottom counts as "following", so scrolling back down re-arms it.
        _logFollowTail = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 1d;
    }

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
