using System.Drawing;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace WasabiDrive.App.Services;

/// <summary>Builds the system-tray icon and its right-click menu.</summary>
[SupportedOSPlatform("windows")]
internal static class TrayIconFactory
{
    public static TaskbarIcon Create(Action onOpen, Action onMountAuto, Action onUnmountAll, Action onExit)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Open WasabiDrive", onOpen));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Mount auto drives", onMountAuto));
        menu.Items.Add(MenuItem("Unmount all", onUnmountAll));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Exit", onExit));

        var icon = new TaskbarIcon
        {
            ToolTipText = "WasabiDrive",
            Icon = BuildIcon(),
            ContextMenu = menu,
        };
        icon.TrayMouseDoubleClick += (_, _) => onOpen();
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
