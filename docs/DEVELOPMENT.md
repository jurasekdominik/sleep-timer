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

The optional taskbar timer is a separate non-activating WPF window. TaskbarLocator reads Explorer accessibility bounds on one background worker at most once a second; TaskbarPlacement uses physical pixels to place it left of the overflow arrow or notification area, without covering buttons. Unsupported vertical/small taskbars, fullscreen apps, unavailable placement and hidden taskbars suppress it. A slow provider hides stale placement without queuing more workers. Test Windows 10/11, auto-hide, Explorer restart and mixed-DPI monitors on a real desktop.
