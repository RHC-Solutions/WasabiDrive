using System.Globalization;
using System.Windows;
using System.Windows.Data;
using WasabiDrive.Core.Models;

namespace WasabiDrive.App.Views;

/// <summary>Friendly label for a <see cref="MappingMode"/> in the Mode dropdown.</summary>
public sealed class MappingModeToDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MappingMode.OnDemandFolder
            ? "On-demand folder (like OneDrive / Google Drive)"
            : "Drive letter (mapped drive)";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound value is non-null (used to enable Edit/Delete on selection).</summary>
public sealed class NullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps a bool to Visibility (true → Visible, false → Collapsed).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
