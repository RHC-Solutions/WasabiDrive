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

    /// <summary>Draws a simple teal "W" tray icon so no binary asset needs to ship in v1.</summary>
    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(Color.FromArgb(0, 150, 136)); // teal
            g.FillRectangle(bg, 0, 0, 32, 32);
            using var font = new Font("Segoe UI", 16, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.White);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("W", font, fg, new RectangleF(0, 0, 32, 32), fmt);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
