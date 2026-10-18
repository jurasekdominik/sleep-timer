# Native taskbar timer

The native renderer is a Win32 child of `Shell_TrayWnd`, drawn with Direct2D and DirectWrite. It runs in `SleepTimer.Taskbar.exe` alongside the WPF app. It does not inject a DLL, modify Explorer's XAML tree, or depend on TaskbarWidgets.

Taskbar integration was inspired by [TaskbarWidgets](https://github.com/pfcdev/TaskbarWidgets). Sleep Timer's renderer was implemented independently and uses a different integration from TaskbarWidgets' private XAML approach. A child surface inherits parent visibility, clipping and Z order. It uses the existing available-space calculation rather than adding taskbar columns or moving other icons. Desktop testing has confirmed Start-menu persistence and stable native rendering on Windows 11. Screen clipping, fullscreen apps, Explorer restart and mixed-DPI monitors still need checks on each supported desktop configuration. Hosted CI validates a controlled Win32 parent, not Windows 11's taskbar compositor.

## Ownership and updates

- The WPF app owns timers, settings and power actions. The helper only draws the mini timer and sends input back.
- A versioned, fixed-size `WM_COPYDATA` message carries text, colours, progress and physical bounds. The helper validates size, ranges, terminators and owner window. The app validates the helper's process, parent and handshake.
- The helper waits on its owner process and Windows messages. There is no idle render loop. A one-shot timer distinguishes single from double-clicks.
- State changes redraw the small transparent surface. Blank padding uses alpha 1 for hit testing; DirectWrite uses grayscale antialiasing over transparency. DPI awareness is matched to the taskbar on the helper thread, leaving WPF's context unchanged.
- The app bounds startup and update waits. A failed or unresponsive helper restores WPF without repeated automatic respawns in the same taskbar session. Choosing Native again retries. Disabling the widget stops the owned helper; owner-process loss also ends it.

Both processes use the same explicit Windows AppUserModelID. The helper includes the app icon and a readable file description. These identify it as part of Sleep Timer; Task Manager grouping is controlled by Windows and is not guaranteed by the shell identity API.

The helper is embedded in the portable executable and extracted into `%LOCALAPPDATA%\SleepTimer\native\<SHA256>`. Existing bytes are checked against the embedded hash before use. It uses the static C++ runtime and Windows graphics libraries, so there is no additional runtime installer. SDK-only builds retain WPF. The bundled helper currently targets x64.

## Build and check

Install .NET SDK 8, Visual Studio C++ build tools, the Windows SDK and CMake:

```powershell
./scripts/build-native.ps1
dotnet build SleepTimer.csproj -c Release --nologo -warnaserror
$env:SLEEP_TIMER_NATIVE_REQUIRED = '1'
./scripts/check-windows.ps1
./scripts/check-performance.ps1
```

The native build runs protocol and rasterisation checks at 100%, 200% and 300% scale. Windows integration checks cover physical child placement, transparent hit testing, parent visibility, mouse controls, helper-failure fallback and owner-loss cleanup. A native widget image is included in CI artifacts.

The performance script runs WPF and native modes in fresh app processes with the same workload. CPU, working set, private memory, GDI objects and handles include the app and its owned helper; managed heap samples refer to the app. CSV files distinguish actual taskbar placement from a controlled parent. Native samples fail if the helper falls back or stops being visible. Each adjustment phase applies 400 wheel edits through the real model and animation path; the CSV records elapsed time. An optional baseline commit runs those same phases for comparison. Disposal cycles also check that owned helper processes exit.

For desktop testing, enable Taskbar timer and select Native. Reopen its dropdown to see `Rendering: Native` or `WPF fallback`. Check that Start and fullscreen follow the real taskbar, blank padding accepts clicks, text is sharp at your scaling, and no helper remains after quitting. Compatible (WPF) is available from the same dropdown.
