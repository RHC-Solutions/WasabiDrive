using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using WasabiDrive.App.Services;

namespace WasabiDrive.App.ViewModels;

/// <summary>Top-level view model for the main window.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogChars = 20_000;
    private readonly StringBuilder _log = new();

    public MainViewModel(AppController controller)
    {
        Controller = controller;
        controller.LogAppended += AppendLog;
        if (controller.StartupWarning is { } warn)
            AppendLog(warn);
    }

    public AppController Controller { get; }
    public ObservableCollection<MappingViewModel> Mappings => Controller.Mappings;

    [ObservableProperty] private MappingViewModel? _selectedMapping;
    [ObservableProperty] private string _logText = string.Empty;

    public string? RcloneWarning => Controller.StartupWarning;
    public bool HasRcloneWarning => Controller.StartupWarning is not null;

    private void AppendLog(string line)
    {
        _log.AppendLine(line);
        if (_log.Length > MaxLogChars)
            _log.Remove(0, _log.Length - MaxLogChars);
        LogText = _log.ToString();
    }
}
