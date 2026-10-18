# Development

Sleep Timer targets .NET Framework 4.8 and uses WPF. WinForms is used only for the Windows tray icon. No third-party runtime packages are required.

## Build and checks

Install .NET SDK 8 on Windows, then run:

```powershell
./build.cmd
./scripts/check-windows.ps1
```

Portable timer checks run on Linux or Windows with Bash:

```sh
bash scripts/check-timers.sh
```

Windows checks preserve saved settings and suppress power actions. CI also checks standalone startup and uploads the executable and UI previews.

Before a release, test real power actions, global keybinds, tray positioning across monitors, and text at different Windows scaling settings.

The optional taskbar timer is a separate non-activating WPF window. TaskbarLocator reads Explorer accessibility bounds on one background worker once a second, with short confirmation probes while notification-area layout settles; TaskbarPlacement uses physical pixels to place it left of the overflow arrow or notification area, without covering buttons. Unsupported vertical/small taskbars, fullscreen apps, unavailable placement and hidden taskbars suppress it. A slow provider hides stale placement without queuing more workers. Test Windows 10/11, auto-hide, Explorer restart and mixed-DPI monitors on a real desktop.

The mini timer has a transparent background and follows the native window immediately above Explorer, rather than staying above every application. Foreground events and a cheap 100 ms native check suppress it when another app covers its area, including borderless games; accessibility placement discovery remains on a worker once a second, with short confirmation probes while notification-area layout settles. Widget menus and Quit run after the mouse event releases capture.

The widget uses alpha 1 on its hit surface so blank padding remains clickable. At-time schedules retain an absolute deadline across pause/resume; resuming a missed deadline uses the next future occurrence of the selected clock time. Duration-mode pauses still freeze the remaining duration. At-time scrolling updates both the absolute deadline and the selected clock field. Idle at-time widgets show the clock target and Ready, rather than a live countdown. Start/Search shell overlays are exempt from fullscreen suppression; while those shell surfaces are foreground, the widget sits above their transparent host without taking focus. Other applications continue to use the taskbar layer.
