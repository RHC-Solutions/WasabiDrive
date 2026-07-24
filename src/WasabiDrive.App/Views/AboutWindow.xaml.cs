using System.Windows;
using WasabiDrive.App.Services;
using WasabiDrive.Core;

namespace WasabiDrive.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.CurrentVersionString}";
        CopyrightText.Text = AppInfo.Copyright;
    }

    private void OnOpenWebsite(object sender, RoutedEventArgs e) =>
        UpdateCoordinator.OpenUrl(AppInfo.PublisherUrl);

    private void OnOpenGitHub(object sender, RoutedEventArgs e) =>
        UpdateCoordinator.OpenUrl(AppInfo.GitHubUrl);

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        try { await UpdateCoordinator.CheckAsync(this, userInitiated: true); }
        finally { CheckButton.IsEnabled = true; }
    }
}
