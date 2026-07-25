using System.Drawing;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace WasabiDrive.App.Services;

/// <summary>
/// The actions the tray menu can invoke. Mirrors the main window's header row (Add, Edit, Delete,
/// Settings, About) so both surfaces offer the same commands, plus the tray-only mount/exit items.
/// <paramref name="HasSelection"/> is queried when the menu opens so Edit/Delete grey out exactly
/// as the header buttons do when no mapping is selected.
/// </summary>
internal sealed record TrayActions(
    Action Open,
    Action Add,
    Action Edit,
    Action Delete,
    Action MountAuto,
    Action UnmountAll,
    Action Settings,
    Action About,
    Action Exit,
    Func<bool> HasSelection);

/// <summary>Builds the system-tray icon and its right-click menu.</summary>
[SupportedOSPlatform("windows")]
internal static class TrayIconFactory
{
    public static TaskbarIcon Create(TrayActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        var open = MenuItem("Open WasabiDrive", actions.Open);
        open.FontWeight = FontWeights.SemiBold;

        var edit = MenuItem("Edit mapping…", actions.Edit);
        var delete = MenuItem("Delete mapping", actions.Delete);

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("+ Add mapping…", actions.Add));
        menu.Items.Add(edit);
        menu.Items.Add(delete);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Mount auto drives", actions.MountAuto));
        menu.Items.Add(MenuItem("Unmount all", actions.UnmountAll));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Settings", actions.Settings));
        menu.Items.Add(MenuItem("About", actions.About));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Exit", actions.Exit));

        // Selection can change between openings, so re-evaluate rather than fixing it at build time.
        menu.Opened += (_, _) =>
        {
            var hasSelection = actions.HasSelection();
            edit.IsEnabled = hasSelection;
            delete.IsEnabled = hasSelection;
        };

        var icon = new TaskbarIcon
        {
            ToolTipText = "WasabiDrive",
            Icon = BuildIcon(),
            ContextMenu = menu,
        };
        icon.TrayMouseDoubleClick += (_, _) => actions.Open();
        return icon;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Loads the RHC Solutions app icon (Assets\wasabidrive.ico) shipped as a WPF resource.</summary>
    private static Icon BuildIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/wasabidrive.ico", UriKind.Absolute);
        var info = System.Windows.Application.GetResourceStream(uri);
        if (info is not null)
        {
            using var stream = info.Stream;
            return new Icon(stream, new System.Drawing.Size(32, 32));
        }
        return SystemIcons.Application;
    }
}
