<#
.SYNOPSIS
    Launch and drive the WasabiDrive WPF tray app for verification.

.DESCRIPTION
    WasabiDrive is a WPF tray app that supervises `rclone mount` child processes. There is no
    headless mode and no CLI surface worth driving, so this driver does three things:

      * lifecycle  - stop / launch a chosen build, respecting the single-instance mutex
      * inspection - read the live rclone command line and mount health (the functional check)
      * UI driving - UI Automation over the real window: dump the element tree, select rows,
                     open and READ menu contents, and take screenshots

    Reading menus through UI Automation rather than screenshots is deliberate: screenshots capture
    whatever window is on top, and on a machine someone is actually using that is rarely this app.

.EXAMPLE
    ./driver.ps1 health
    ./driver.ps1 args
    ./driver.ps1 stop
    ./driver.ps1 launch -Build release
    ./driver.ps1 shot -Focus
    ./driver.ps1 rows
    ./driver.ps1 rowmenu
    ./driver.ps1 traymenu
    ./driver.ps1 tree -Filter 'Button|DataItem'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('health', 'args', 'stop', 'launch', 'shot', 'rows', 'rowmenu', 'traymenu', 'tree')]
    [string]$Command,

    [ValidateSet('release', 'debug', 'installed')]
    [string]$Build = 'installed',

    [string]$Out,
    [string]$Filter = '.',
    [int]$Index = 0,
    [switch]$Focus
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$shotDir = Join-Path $env:TEMP 'wasabidrive-driver'
New-Item -ItemType Directory -Force $shotDir | Out-Null

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

if (-not ('Native' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Native {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  // PW_RENDERFULLCONTENT (2) captures a window's pixels even when another window covers it,
  // so a screenshot never depends on stealing the foreground.
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public const uint RDOWN = 0x0008, RUP = 0x0010;
}
'@
}

# ---------------------------------------------------------------- helpers

function Get-AppProcess {
    Get-Process WasabiDrive -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
}

function Get-ExePath([string]$which) {
    switch ($which) {
        'installed' { 'C:\Program Files\WasabiDrive\WasabiDrive.exe' }
        'release' {
            Get-ChildItem (Join-Path $repoRoot 'src\WasabiDrive.App\bin\Release') -Directory -Filter 'net8.0-windows*' |
                Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'win-x64\publish\WasabiDrive.exe' }
        }
        'debug' {
            Get-ChildItem (Join-Path $repoRoot 'src\WasabiDrive.App\bin\Debug') -Directory -Filter 'net8.0-windows*' |
                Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'WasabiDrive.exe' }
        }
    }
}

# The app window, resolved from the process handle. Resolving by Name would also match unrelated
# windows whose titles contain "WasabiDrive" (editors, Explorer windows on the cache folder).
function Get-AppWindow {
    $p = Get-AppProcess
    if (-not $p) { throw 'WasabiDrive is not running (or has no window). Run: driver.ps1 launch' }
    [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
}

# BoundingRectangle comes back as (inf, inf, -inf, -inf) for collapsed/offscreen elements, so an
# unguarded [int] cast throws "Value was either too large or too small for an Int32".
function Format-Rect($r) {
    if ([double]::IsInfinity($r.X) -or [double]::IsInfinity($r.Y)) { return 'offscreen' }
    '{0},{1} {2}x{3}' -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height
}

function Find-Descendants($root, $controlType) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    $root.FindAll('Descendants', $cond)
}

# WPF GridView rows surface as ControlType.DataItem, NOT ListItem, and their Name is the view-model
# type name. The user-visible cell values are child Text elements.
function Get-Rows {
    $win = Get-AppWindow
    $items = Find-Descendants $win ([System.Windows.Automation.ControlType]::DataItem)
    $out = @()
    for ($i = 0; $i -lt $items.Count; $i++) {
        $it = $items[$i]
        $cells = (Find-Descendants $it ([System.Windows.Automation.ControlType]::Text) |
            ForEach-Object { $_.Current.Name }) -join ' | '
        $out += [pscustomobject]@{ Index = $i; Cells = $cells; Rect = Format-Rect $it.Current.BoundingRectangle; Element = $it }
    }
    $out
}

# A WPF ContextMenu / tray menu is its own top-level popup window, so it is NOT a descendant of the
# app window and has to be found from the desktop root.
#
# It MUST be scoped by owning process id. Searching the desktop root for every MenuItem returns the
# menu bar of every running application (VS Code's File/Edit/..., Outlook's ribbon, Word's ribbon) —
# dozens of items that have nothing to do with this app.
function Get-OpenMenuItems([int]$processId) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $tops = $root.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)
    $result = @()
    foreach ($t in $tops) {
        try { if ($t.Current.ProcessId -ne $processId) { continue } } catch { continue }
        foreach ($mi in (Find-Descendants $t ([System.Windows.Automation.ControlType]::MenuItem))) {
            $result += [pscustomobject]@{ Name = $mi.Current.Name; IsEnabled = $mi.Current.IsEnabled }
        }
    }
    $result
}

# Captures the app window. Defaults to PrintWindow, which reads the window's own pixels and works
# even when the window is completely covered — essential on a machine somebody is using, and it
# never yanks their focus. -Focus forces the older screen-scrape path instead.
function Save-Shot([string]$path, [switch]$doFocus) {
    $p = Get-AppProcess
    if (-not $p) { throw 'WasabiDrive is not running.' }
    $hwnd = $p.MainWindowHandle
    $r = New-Object Native+RECT
    [Native]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top

    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $mode = 'PrintWindow'

    if ($doFocus) {
        # SetForegroundWindow is refused for a process that does not already own the foreground,
        # so this can silently capture whatever is on top instead. Prefer the default path.
        [Native]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 700
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
        $mode = 'CopyFromScreen'
    }
    else {
        $hdc = $g.GetHdc()
        $ok = [Native]::PrintWindow($hwnd, $hdc, 2)  # PW_RENDERFULLCONTENT
        $g.ReleaseHdc($hdc)
        if (-not $ok) {
            $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
            $mode = 'CopyFromScreen (PrintWindow failed)'
        }
    }

    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    "saved $path (${w}x${h}) via $mode"
}

# ---------------------------------------------------------------- commands

switch ($Command) {

    'health' {
        $p = Get-AppProcess
        "app running : $($p -ne $null)"
        if ($p) {
            "exe         : $($p.Path)"
            $v = (Get-Item $p.Path).VersionInfo
            "version     : $($v.FileVersion)"
        }
        $rc = Get-Process rclone -ErrorAction SilentlyContinue
        "rclone      : $($rc -ne $null)"
        "W: mounted  : $(Test-Path 'W:\')"
        $log = Get-ChildItem "$env:LOCALAPPDATA\WasabiDrive\logs\*.log" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime | Select-Object -Last 1
        if ($log) {
            # Match on the log's severity FIELD, not anywhere in the line: object keys in this bucket
            # contain the word "Error", which makes a naive /Error/ grep return dozens of false hits.
            $errs = Select-String -Path $log.FullName -Pattern '\s(ERROR|CRITICAL)\s+:' -ErrorAction SilentlyContinue
            "log         : $($log.Name)"
            "log errors  : $(($errs | Measure-Object).Count)"
            if ($errs) { $errs | Select-Object -Last 3 | ForEach-Object { '   ' + $_.Line.Trim() } }
        }
    }

    'args' {
        $rc = Get-CimInstance Win32_Process -Filter "Name='rclone.exe'" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $rc) { 'rclone is not running - nothing mounted.'; break }
        # The single most useful functional check: proves which flags the app actually passed and
        # that rclone accepted them (it would have exited on an unknown flag).
        $rc.CommandLine
    }

    'stop' {
        # Order matters. Killing the supervisor first stops it restarting the mount; killing rclone
        # is what makes WinFsp release the drive letter. Leaving rclone alive keeps W: mounted and
        # the next instance then refuses with "drive is already in use by another volume".
        Stop-Process -Name WasabiDrive -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
        Stop-Process -Name rclone -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 1500
        "stopped. W: present = $(Test-Path 'W:\')"
    }

    'launch' {
        # A second instance exits immediately on the single-instance mutex with a message box, so
        # always stop first rather than assuming nothing is running.
        if (Get-AppProcess) { throw 'Already running. Run: driver.ps1 stop' }
        $exe = Get-ExePath $Build
        if (-not $exe -or -not (Test-Path $exe)) { throw "Build '$Build' not found at: $exe" }
        $p = Start-Process $exe -PassThru
        Start-Sleep -Seconds 8
        if ($p.HasExited) { throw "Exited immediately with code $($p.ExitCode) (single-instance mutex? missing WinFsp?)" }
        "launched : $exe"
        "title    : $((Get-AppProcess).MainWindowTitle)"
        "W:       : $(Test-Path 'W:\')"
    }

    'shot' {
        if (-not $Out) { $Out = Join-Path $shotDir 'window.png' }
        Save-Shot $Out -doFocus:$Focus
    }

    'rows' {
        Get-Rows | ForEach-Object { "[$($_.Index)] $($_.Cells)   rect=$($_.Rect)" }
    }

    'rowmenu' {
        $rows = Get-Rows
        if ($rows.Count -eq 0) { throw 'No mapping rows in the list - add a mapping first.' }
        $row = $rows[$Index]
        $p = Get-AppProcess
        [Native]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 600

        # Select via the SelectionItem pattern instead of a synthetic mouse click: no cursor
        # hijacking, and it cannot miss the target because of DPI scaling. SetFocus() as well —
        # selecting a row does not necessarily give it keyboard focus, and Shift+F10 goes to
        # whatever has focus.
        $row.Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        try { $row.Element.SetFocus() } catch { }
        Start-Sleep -Milliseconds 400

        # Shift+F10 is the keyboard context-menu gesture; it opens the same ContextMenu a
        # right-click would, without moving the physical mouse.
        [System.Windows.Forms.SendKeys]::SendWait('+{F10}')
        Start-Sleep -Milliseconds 1200

        "row [$Index]: $($row.Cells)"
        'context menu items:'
        $items = Get-OpenMenuItems $p.Id
        if (($items | Measure-Object).Count -eq 0) { '   (none found - menu did not open)' }
        else { $items | ForEach-Object { "   - $($_.Name)  [enabled=$($_.IsEnabled)]" } }

        if ($Out) { Save-Shot $Out }
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    }

    'traymenu' {
        # Best-effort. The tray icon is a legacy Shell_NotifyIcon owned by explorer.exe, and on
        # Windows 11 third-party icons are usually tucked into the "Show hidden icons" overflow
        # flyout, which does not exist in the UI Automation tree until a human opens it. Restrict
        # the search to small explorer-owned buttons — matching on name alone across the desktop
        # picks up unrelated windows (editor tabs and status-bar items mentioning "WasabiDrive").
        $explPids = (Get-Process explorer -ErrorAction SilentlyContinue).Id
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $btns = Find-Descendants $root ([System.Windows.Automation.ControlType]::Button)
        # Exact name only. A substring match still collides with Explorer's own toolbar buttons when
        # a folder called WasabiDriveCache happens to be open ('Refresh "WasabiDriveCache" (F5)').
        # The tray icon's automation name is exactly the TaskbarIcon.ToolTipText, i.e. "WasabiDrive".
        $icon = $btns | Where-Object {
            $r = $_.Current.BoundingRectangle
            $_.Current.Name -eq 'WasabiDrive' -and
            $_.Current.ClassName -ne 'AppBarButton' -and
            $explPids -contains $_.Current.ProcessId -and
            -not [double]::IsInfinity($r.X) -and $r.Width -gt 0 -and $r.Width -lt 60
        } | Select-Object -First 1

        if (-not $icon) {
            'Tray icon is not reachable via UI Automation on this desktop.'
            ''
            'This is expected, not a driver bug: Windows 11 keeps third-party notification icons in'
            'the "Show hidden icons" overflow flyout, which is absent from the UIA tree until it is'
            'opened by hand. The taskbar (Shell_TrayWnd) also does not always enumerate as a root child.'
            ''
            'Verify the tray menu manually: click the chevron, right-click the WasabiDrive icon, and'
            'confirm the items match TrayIconFactory.Create in'
            '  src/WasabiDrive.App/Services/TrayIconFactory.cs'
            'Expected: Open / Add / Edit / Delete / Mount auto / Unmount all / Settings / About / Exit,'
            'with Edit and Delete greyed out when no mapping row is selected.'
            break
        }

        "found tray icon: '$($icon.Current.Name)' rect=$(Format-Rect $icon.Current.BoundingRectangle)"
        # Invoke() is a left-click; a tray context menu needs an actual right-click at the icon.
        $r = $icon.Current.BoundingRectangle
        [Native]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) | Out-Null
        Start-Sleep -Milliseconds 300
        [Native]::mouse_event([Native]::RDOWN, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 120
        [Native]::mouse_event([Native]::RUP, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 1500
        'tray menu items:'
        $items = Get-OpenMenuItems (Get-AppProcess).Id
        if (($items | Measure-Object).Count -eq 0) { '   (none found)' }
        else { $items | ForEach-Object { "   - $($_.Name)  [enabled=$($_.IsEnabled)]" } }
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    }

    'tree' {
        $win = Get-AppWindow
        $all = $win.FindAll('Descendants', [System.Windows.Automation.Condition]::TrueCondition)
        "descendants: $($all.Count)  (filter: $Filter)"
        foreach ($e in $all) {
            $c = $e.Current
            $type = $c.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            if ("$type $($c.Name)" -match $Filter) {
                "  {0,-12} '{1}'  rect={2}" -f $type, $c.Name, (Format-Rect $c.BoundingRectangle)
            }
        }
    }
}
