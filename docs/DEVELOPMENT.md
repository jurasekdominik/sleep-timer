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

Run `./scripts/check-performance.ps1` on Windows to measure idle/running CPU, working set, private memory and repeated widget/window disposal. Optional `-BaselineRef <commit>` compares usage against an earlier build. Samples run after warm-up using the real app window and dispatcher; CPU is reported both per core and across the machine. If the desktop has no accessible taskbar slot, the harness measures the real widget at a controlled location and labels those samples separately from native placement. Raw CSV data stays in `validation-output` and is uploaded by the Performance workflow.

Full Windows builds can embed an optional [native taskbar renderer](NATIVE_TASKBAR.md). Run `./scripts/build-native.ps1` before building the app or validation projects. CI includes that build. Performance checks run both renderers in fresh processes and include helper resource use; build the helper first when running those checks locally.

Before a release, test real power actions, global keybinds, tray positioning across monitors, and text at different Windows scaling settings.

The compatible taskbar renderer is a separate non-activating WPF window. Foreground and Explorer-filtered taskbar events request background accessibility discovery, with short confirmation probes while layout settles. A 30-second native geometry check recovers missed events without scanning accessibility when nothing moved. TaskbarPlacement uses physical pixels to place the widget left of the notification area, without covering buttons. Unsupported vertical/small taskbars, fullscreen apps, unavailable placement and hidden taskbars suppress it. A slow provider hides stale placement without queuing more workers. Test Windows 10/11, auto-hide, Explorer restart and mixed-DPI monitors on a real desktop.

The mini timer has a transparent background and follows the native window immediately above Explorer. Widget menus and Quit run after the mouse event releases capture. Single-click waits for Windows' double-click interval; double-click opens the main window without changing timer state. Closing the widget cancels pending clicks.

The hidden idle/paused app stops its status ticker. Running status refreshes once a second. The visible dial uses continuous countdown clocks rather than replacing them on each status update. Steady motion renders at 10 Hz (about 0.025 pixels of arc movement per frame); independent circle/text edit easing runs at the normal display frame rate. Hiding or pausing releases those clocks. Visible idle previews refresh at the next minute; finite keep-awake deadlines still expire on time. Wheel edits use a separate one-shot save timer.

The widget uses alpha 1 on its hit surface so blank padding remains clickable. Its small transparent window uses software rendering during screen-capture and display transitions. It follows the taskbar layer and may hide while Start, Search or screen clipping covers it. Foreground events are coalesced and ignored during disposal. At-time schedules retain an absolute deadline across pause/resume; resuming a missed deadline uses the next future occurrence of the selected clock time. Duration-mode pauses still freeze the remaining duration. At-time scrolling updates both the absolute deadline and the selected clock field. Idle at-time widgets show the clock target and Ready, rather than a live countdown.

Widget callbacks stop before closing begins. Close-to-tray hides the main window after cancelling its Closing event; quit confirmation runs before Close. WPF rejects Show/Hide calls while a window is closing.

Unhandled managed exceptions are recorded in `%LOCALAPPDATA%\SleepTimer\last-crash.log`. The file contains the latest exception and app/runtime versions; logging does not suppress the exception or send data anywhere. For capture-related failures, also check Windows Reliability History: native failures may not reach the managed handlers.
