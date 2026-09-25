# iTask

A macOS-style **menu bar + floating dock** for Windows 11, written in C# / .NET 8 (WPF + Win32).
It runs alongside Explorer: it hides the native taskbar while it runs and restores it on exit.
It does not replace the Windows shell. Start, Win+R, Alt+Tab, notifications and File Explorer
keep working as usual.

> The source code lives on the **`dev`** branch.

## Features

**Top bar**
- Open apps on the left: click to focus, click again to minimize, right-click for the app's windows
- Other apps' tray icons in a dropdown on the right (they're forwarded to the real apps, so their menus work)
- Live Wi-Fi/Ethernet status, volume (scroll to adjust) and battery, from Windows APIs
- Date and time, like `25th September 13:17`
- Maximized windows sit below the bar, never underneath it

**Dock**
- Running apps grouped per app, in launch order, with sharp 256 px icons
- macOS-style hover magnification (cosine falloff), click bounce and running dots
- Smart hiding: visible on the desktop and over normal windows; slides away when the active app is
  maximized; push the cursor against the bottom edge to bring it back
- Click to focus or minimize, right-click to pick a window or close it

## Requirements

- Windows 11 (Windows 10 mostly works)
- .NET 8 SDK to build

## Build and run

```
git checkout dev
dotnet build src/iTask.csproj -c Release
src/bin/Release/net8.0-windows10.0.19041.0/iTask.exe
```

| Command | Effect |
| --- | --- |
| `iTask.exe` | Start (single instance) |
| `iTask.exe --quit` | Ask the running instance to exit cleanly |
| `iTask.exe --restore-taskbar` | Stop iTask and force the native taskbar back (crash recovery) |

You can also quit from the **⋯** menu at the top left.

## Configuration

`%APPDATA%\iTask\settings.json` is created on first run. You can set the icon size, magnification,
dock visibility (`Smart` / `AlwaysVisible`), materials (`Solid` / `Blur`), which indicators to show,
and more. Restart iTask to apply changes.

Log: `%LOCALAPPDATA%\iTask\iTask.log`

## How it works with Explorer

1. Saves the taskbar's auto-hide setting to disk, then turns auto-hide on and hides the taskbar
   window. The original setting is restored on exit, after a crash (on the next launch), or with
   `--restore-taskbar`.
2. Registers the top bar as a shell AppBar so maximized windows fit below it.
3. Hosts the notification area alongside Explorer (via [ManagedShell](https://github.com/cairoshell/ManagedShell))
   so tray icons can live in the top bar. On exit the icons are handed back to Explorer.
4. Tracks running windows the way the taskbar does (shell hooks, ManagedShell's task service).

The bars never take keyboard focus and don't appear in Alt+Tab.
