# Sleep Timer

Shut down, sleep or restart your PC when a timer ends, or keep it awake.

<p align="center"><img src="assets/screenshot.png" alt="Sleep Timer" width="376"></p>

## Download

Get portable `SleepTimer.exe` from [Releases](https://github.com/jurasekdominik/Sleep-Timer/releases).

**Requirements:** Windows 10/11 and .NET Framework 4.8.

## Features

- Shut down, sleep or restart after a countdown or at a set time.
- Pause, resume and adjust the timer while it runs.
- Quick timers from the tray, with your own presets.
- Keep the PC awake, optionally with the screen on.
- Custom accent colour and optional startup with Windows.
- WPF interface with smooth animations.
- Global keybinds for Start / Cancel and the last quick timer.
- Separate timer and UI fonts, with font weight selection.
- Timer status in the title bar and progress in the taskbar icon.
- Optional native mini timer beside the hidden-icons arrow on the taskbar.

## Use

Choose an action, set a duration or a clock time, and click the circle to start. You can pause or change the time while it runs. The ring shows minutes; the smaller segments show additional hours.

<table align="center">
  <tr><th>Control</th><th>Action</th></tr>
  <tr><td>Left-click</td><td>Start, pause or resume</td></tr>
  <tr><td>Right-click</td><td>Cancel</td></tr>
  <tr><td>Scroll</td><td>±5 minutes</td></tr>
  <tr><td>Alt / Shift / Ctrl + scroll</td><td>±1 / ±15 / ±30 minutes</td></tr>
  <tr><td>Ctrl + Shift + scroll</td><td>±60 minutes</td></tr>
</table>

Keep awake prevents automatic sleep, with an option to keep the screen on. Right-click the tray icon for quick timers and keep-awake options. Add your own timer presets in Settings using whole minutes (`90`) or hours (`2h`).

Enable the mini timer in Settings → General → Taskbar timer. Click it to start, pause or resume; double-click to open the app, right-click to cancel (or open the menu when idle), middle-click for the menu, and scroll to adjust. It follows taskbar visibility and hides behind fullscreen apps or when there is no room. Compatible WPF rendering may also hide behind Start and screen clipping.

The mini timer attaches to the taskbar, including while Start is open. It uses a small helper process and falls back to WPF if unavailable. Choose Compatible (WPF) from its dropdown to switch rendering.

Closing the window hides it to the tray. Double-click the tray icon to reopen it, or choose Quit from its menu.

## Build

Install .NET SDK 8 and run `build.cmd`. It creates `SleepTimer.exe` in the project folder. SDK-only builds use WPF taskbar rendering.

To include the native taskbar timer, install Visual Studio C++ build tools, the Windows SDK and CMake, then run `build.cmd --native`. The portable executable includes the helper; users do not need those build tools. See [native taskbar development](docs/NATIVE_TASKBAR.md).

## Credits

Taskbar integration was inspired by [TaskbarWidgets](https://github.com/pfcdev/TaskbarWidgets). Sleep Timer uses its own native renderer.

## License

[MIT](LICENSE) · Dominik Jurásek
