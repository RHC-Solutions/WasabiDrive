using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WasabiDrive.App.Services;
using WasabiDrive.CloudFiles;
using WasabiDrive.Core.Models;

namespace WasabiDrive.App.ViewModels;

/// <summary>Row view model: one mapping plus its live mount state and per-row commands.</summary>
public sealed partial class MappingViewModel : ObservableObject
{
    private readonly AppController _controller;

    public MappingViewModel(AppController controller, Mapping model)
    {
        _controller = controller;
        Model = model;
    }

    public Mapping Model { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(IsMounted))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(MountCommand))]
    [NotifyCanExecuteChangedFor(nameof(UnmountCommand))]
    private MountState _state = MountState.Unmounted;

    [ObservableProperty]
    private string? _statusMessage;

    public string Name => string.IsNullOrWhiteSpace(Model.Name) ? Model.BucketName : Model.Name;
    public string BucketName => Model.BucketName;
    public string DriveTarget => Model.DriveTarget;
    public string RegionCode => Model.RegionCode;
    public bool AutoMount => Model.AutoMount;

    public string ModeText => Model.Mode == MappingMode.OnDemandFolder ? "On-demand" : "Drive";

    /// <summary>Drive letter for drive mode, or the on-demand folder path.</summary>
    public string Location => Model.Mode == MappingMode.OnDemandFolder
        ? OnDemandSyncManager.ResolveFolderPath(Model)
        : Model.DriveTarget;

    public bool IsMounted => State == MountState.Mounted;
    public bool IsBusy => State is MountState.Mounting or MountState.Unmounting;

    public string StateText => State switch
    {
        MountState.Mounted => "Mounted",
        MountState.Mounting => "Mounting…",
        MountState.Unmounting => "Unmounting…",
        MountState.Error => "Error",
        _ => "Not mounted",
    };

    private bool CanMount() => State is MountState.Unmounted or MountState.Error;
    private bool CanUnmount() => State == MountState.Mounted;

    [RelayCommand(CanExecute = nameof(CanMount))]
    private async Task MountAsync()
    {
        try { await _controller.MountAsync(this); }
        catch (Exception ex)
        {
            ApplyStatus(MountState.Error, ex.Message);
            MessageBox.Show(ex.Message, "Mount failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUnmount))]
    private async Task UnmountAsync()
    {
        try { await _controller.UnmountAsync(this); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unmount failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Called by the controller (on the UI thread) when the mount state changes.</summary>
    public void ApplyStatus(MountState state, string? message)
    {
        State = state;
        if (!string.IsNullOrWhiteSpace(message))
            StatusMessage = message;
    }

    /// <summary>Replaces the underlying model after an edit and refreshes bound fields.</summary>
    public void UpdateModel(Mapping model)
    {
        Model = model;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(BucketName));
        OnPropertyChanged(nameof(DriveTarget));
        OnPropertyChanged(nameof(RegionCode));
        OnPropertyChanged(nameof(AutoMount));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(Location));
    }
}
