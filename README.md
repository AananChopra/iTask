# iTask

macos-style menu bar + dock for windows 11. top bar up top, floating dock at the bottom, the whole vibe.

it sits on top of explorer instead of replacing it, so start, alt+tab, win+r, notifications, file explorer and all that keep working like normal. it hides the windows taskbar while it's running and puts it back when you quit.

> the code lives on the `dev` branch. `main` is just this readme.

## what you get

### top bar

- the apps you have open, on the left. click one to jump to it, click again to minimize, right click to see its windows
- wifi, sound, brightness and battery on the right, each with its own little frosted glass dropdown (like on a mac)
  - **wifi**: turn it on/off, see what's nearby, connect to networks you've saved
  - **sound**: volume slider, mute, switch where the sound goes (speakers, headphones, your monitor, whatever)
  - **brightness**: works on your laptop screen and on external monitors too (anything that supports ddc/ci, which is most of them)
  - **battery**: how much you've got, if it's charging, time left, battery saver
- the little arrow opens your hidden tray icons. they still work, clicks go straight to the real apps
- date and time like `28th September 13:17`. click it and you get a calendar
- the `···` at the top left has itask settings, windows settings, task manager and quit
- maximized windows stop right under the bar, they never slide under it

### dock

- your running apps, grouped per app, with nice sharp icons
- hover magnification, a little bounce when you click, dots under running apps
- three modes, pick one in settings:
  - **always**: it's just always there, and maximized apps stop above it
  - **hide over full-size apps** (the default): hangs out on the desktop and over normal windows, slides away when an app goes fullscreen or maximized
  - **auto-hide**: stays out of the way until you want it
- when it's hidden, shove your cursor against the bottom edge of the screen and it pops back up
- click to focus or minimize, right click to pick a window or close it
- got a ton of apps open or a small screen? the icons shrink so everything fits

### multiple displays

every display gets its own top bar and dock. each dock hides based on what's on *its* screen, so a maximized app on one monitor doesn't mess with the other one. plugging monitors in and out, changing scaling, mixing a 1080p monitor with a high-res laptop screen, portrait monitors, monitors in weird spots, all fine.

## itask settings

`···` at the top left, then **itask settings**. it's a normal window, shows up in the dock, comes to the front when you click it, all that. stuff applies instantly, no save button:

- start itask when windows starts
- dock mode, icon size, magnification (or turn magnification off)
- show or hide each thing in the top bar (open apps, hidden icons, wifi, sound, brightness, battery, date, time)

everything gets saved to `%APPDATA%\iTask\settings.json` if you ever wanna poke at it by hand (restart itask after editing it yourself).

## running it

no installer yet, that's coming. for now you build it yourself. you need windows 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
git clone https://github.com/AananChopra/iTask.git
cd iTask
git checkout dev
dotnet build src/iTask.csproj -c Release
src\bin\Release\net8.0-windows10.0.19041.0\iTask.exe
```

want a standalone copy that runs on a pc without .net installed?

```
dotnet publish src/iTask.csproj -c Release -r win-x64 --self-contained -o publish
```

on an arm laptop (snapdragon and friends) use `win-arm64` instead of `win-x64`. then just run `publish\iTask.exe`.

### commands

| command | what it does |
| --- | --- |
| `iTask.exe` | starts it (only one copy runs at a time) |
| `iTask.exe --quit` | tells the running one to close properly |
| `iTask.exe --restore-taskbar` | panic button. stops itask and forces the windows taskbar back |

## if something's acting weird

- **windows taskbar didn't come back?** run `iTask.exe --restore-taskbar`. itask saves your taskbar setting before it touches anything, so even after a crash it puts things back the next time it starts
- **windows stuck "always on top" after win+d.** windows 11 sometimes brings windows back from show desktop as always-on-top, which buries everything else under them. itask quietly undoes that. anything you pinned on purpose with powertoys is left alone
- **no brightness icon on one of your monitors.** that monitor doesn't do ddc/ci (some tvs, usb/displaylink docks, a few cheap panels), or it's switched off in the monitor's own menu
- **some app won't come back from the dock.** apps running as admin can ignore normal apps like itask, that's a windows security thing. the on-screen keyboard has a workaround so that one works
- **logs** live at `%LOCALAPPDATA%\iTask\iTask.log`, that's the first place to look

## how it plays with explorer

the nerdy bits, if you care:

1. saves your taskbar's auto-hide setting, turns auto-hide on and hides the taskbar window. all of it gets undone when you quit (or on the next launch after a crash)
2. registers the top bar as a shell appbar so maximized windows fit under it
3. hosts the tray area next to explorer using [ManagedShell](https://github.com/cairoshell/ManagedShell), so tray icons can live in the top bar. they get handed back to explorer when you quit
4. tracks your windows the same way the real taskbar does
5. the bars never steal focus and don't show up in alt+tab. the frosted glass is drawn by windows' own compositor, so it doesn't flicker when you switch apps

## what's where

```
src/
  AppHost.cs          starts everything up, one top bar + dock per display
  Dock/               the dock, its magnification and the auto-hide logic
  TopBar/             the top bar, plus Flyouts/ for all the dropdowns and the calendar
  SystemInfo/         wifi, sound, brightness (wmi + ddc/ci), battery, clock
  WindowsIntegration/ window tracking, focus stuff, display info, win32 glue
  ShellIntegration/   hiding the taskbar, appbars, tray hosting
  UI/                 glass windows, theming, styles, the settings window
  Configuration/      settings file + start with windows
```

## what's next

- a proper installer (one exe, sets itself up, runs on startup if you want)
- whatever breaks next lol

## credits

- [ManagedShell](https://github.com/cairoshell/ManagedShell) (apache 2.0) does the heavy lifting for tray icons and window tracking
- dock magnification is ported from a react macos dock component
