# Plan: Sleep Timer Widget (native widget only)

Status: **plan only, nothing implemented.** Written for another agent to execute.
Line numbers refer to commit `44e1a74` (branch base).

## 1. Goal

A second executable, `SleepTimerWidget.exe`, that is only the native taskbar widget:

- No main window, no dial, no animations, no fonts UI, no WPF fallback renderer.
- Controls on the widget: **left-click** (start / pause / resume), **right-click** (cancel when active, menu when idle), **scroll** (adjust time).
- Everything else lives in the **menu**: quick timers, action, timer mode, keep awake, colour picker, settings, quit.
- Four improvements over today's widget:
  1. Helper is respawned automatically instead of being abandoned after one failure.
  2. The native widget gets a tooltip.
  3. Left-click acts instantly (no double-click wait).
  4. Scroll no longer goes through the main window's dial control.

The existing full app (`SleepTimer.exe`) must keep building, behaving and passing its checks exactly as before.

## 2. Decisions already made (do not re-open)

| Decision | Choice | Why |
|---|---|---|
| Where it lives | Same repo, new folder `widget/` with its own `SleepTimerWidget.csproj` | Own `obj/`/`bin/`; root project compiles only `src/*.cs`, so nothing leaks either way |
| Language | C# / .NET Framework 4.8, WPF used only for the popup menu and colour picker | Reuses `PopupMenu`, `ColorPicker`, `Countdown`, `TaskbarLocator`, `NativeTaskbarHost` unchanged. A pure C++ rewrite was rejected as a different project |
| Helper | The one existing `native/taskbar.cpp`, extended with opt-in behaviour | No fork of the C++ |
| Tray icon | **Kept**, same live ring icon, right-click opens the same menu | Only way to reach the menu or quit when the widget cannot be placed (full taskbar, Explorer restarting, helper failed, SDK-only build) |
| Middle-click | **Kept** as "open menu" | Right-click cancels while a timer runs, so without it the menu is unreachable on the widget during a countdown. Helper already sends it |
| Double-click | Removed in the widget app | There is no window to open; this is what makes click instant |
| Settings storage | Same registry key `HKCU\Software\SleepTimer`, same value names | Accent, presets, duration, action carry over between the two apps |
| Single instance | Same mutex as the full app (`SingleInstance` unchanged) | Running both would draw two widgets in the same slot |
| Hotkeys | **Not included** | Need a key-recorder UI; user listed only click / scroll / menu |
| Fonts, animation timings, renderer choice | Not included | Belong to the window |

## 3. File layout after the change

```
src/Theme.cs            NEW (moved out of src/Ui.cs, no code change)
src/PopupMenu.cs        NEW (moved out of src/Ui.cs, no code change)
src/TrayGraphics.cs     NEW (moved out of src/MainWindow.cs, no code change)
src/Ui.cs               now only TimerFace
src/ColorPicker.cs      + ShowAbove(...)
src/NativeTaskbarHost.cs  protocol v2, + InstantClick, + UpdateTimeout, + View.Tip
src/TaskbarTimer.cs     one line: pass Tip
native/protocol.h       protocol v2
native/taskbar.cpp      tooltip, instant mode
native/host.manifest    Common-Controls v6 dependency
native/CMakeLists.txt   link comctl32
widget/SleepTimerWidget.csproj  NEW
widget/Program.cs               NEW
widget/WidgetLogic.cs           NEW (pure functions, testable on Linux)
widget/WidgetHost.cs            NEW (placement + helper lifetime, no WPF surface)
widget/WidgetApp.cs             NEW (timer, menu, tray, power)
validation/NativeChecks.cs      packet size, instant-click check
scripts/check-timers.sh         + WidgetLogic checks
.github/workflows/validate.yml  build + startup-check + upload the widget exe
build.cmd, README.md, docs/DEVELOPMENT.md, docs/NATIVE_TASKBAR.md, CHANGELOG.md
```

## 4. Step-by-step

Do the steps in this order; each ends in a buildable state.

### Step 0 — Tooling in the Linux container

The container has no .NET SDK. C# for both projects **does** compile on Linux (verified for `SleepTimer.csproj`: 0 warnings, 0 errors):

```bash
curl -sSL -o /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 8.0 --install-dir "$HOME/dotnet"
"$HOME/dotnet/dotnet" build SleepTimer.csproj -c Release --nologo -warnaserror -p:EnableWindowsTargeting=true
```

Use the same flags for `widget/SleepTimerWidget.csproj` and `validation/WindowsChecks.csproj`. `bash scripts/check-timers.sh` needs `dotnet` on `PATH`.

The C++ helper (`/W4 /WX`, MSVC only) and anything at runtime **cannot** be checked here. See Step 9.

### Step 1 — Split shared UI out of the window files (pure moves)

No behaviour change; the root project globs `src/*.cs`, as do both validation projects, so new files are picked up automatically.

1. `src/Ui.cs` → new `src/Theme.cs`: move `static class Theme` (Ui.cs lines 18–281) with the usings it needs.
2. `src/Ui.cs` → new `src/PopupMenu.cs`: move `ThemedScrollViewer`, `MenuRow`, `PopupMenu` (Ui.cs lines 553–729).
3. `src/MainWindow.cs` → new `src/TrayGraphics.cs`: move `static class TrayGraphics` (MainWindow.cs lines 1182–1204).
4. Remove now-unused usings so `-warnaserror` stays clean.

Check: `SleepTimer.csproj` and `validation/WindowsChecks.csproj` still build; `git diff --stat` shows moves only.

### Step 2 — Protocol v2 and helper changes

**`native/protocol.h`**

- `version = 2`.
- Append `wchar_t tip[256];` as the **last** field of `View`.
- `static_assert(sizeof(View) == 824, ...)` (312 + 512).
- `valid()`: also require `std::wmemchr(view.tip, L'\0', 256)`.

**`src/NativeTaskbarHost.cs`**

- `Version = 2`.
- `View`: append `[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Tip;` as the last field.
- `Update`: `view.Tip = Limit(view.Tip, 255);` (`Limit` already handles `null`).
- Add `internal bool InstantClick;` — when true, `Start()` appends `" instant"` to the helper arguments.
- Add `internal uint UpdateTimeout = 40;` and use it in `SendMessageTimeout` (line 125) instead of the literal `40`.
- **Keep the 8-argument constructor signature**; `validation/NativeChecks.cs` calls it at lines 148, 165, 171.

**`src/TaskbarTimer.cs`** — in `SyncNative` add `Tip = tipHeading.Text` to the `View` initialiser. The full app then gets the heading as a native tooltip; nothing else changes there.

**`native/taskbar.cpp`**

- Arguments: accept `count == 3`, or `count == 4` with `arguments[3]` equal to `L"instant"`. Anything else keeps returning 3 as today. Store `host.instant`.
- Instant mode:
  - `WM_LBUTTONUP`: `if (host->instant) { host->input(taskbar::Command::toggle); return 0; }` before the existing logic.
  - `WM_LBUTTONDBLCLK`: `if (host->instant) return 0;` (the following button-up toggles again, so a fast double-click is two toggles).
  - Default mode is untouched; NativeChecks lines 210–225 assert its exact behaviour.
- Tooltip (`#include <commctrl.h>`, link `comctl32`):
  - After `CreateWindowExW` succeeds, create
    `CreateWindowExW(WS_EX_TOPMOST, TOOLTIPS_CLASSW, nullptr, WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX, CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, window, nullptr, instance, nullptr)`.
    `TTS_ALWAYSTIP` is required because the widget window is never active.
  - `TOOLINFOW info{}; info.cbSize = TTTOOLINFOW_V1_SIZE; info.uFlags = TTF_IDISHWND | TTF_SUBCLASS; info.hwnd = window; info.uId = (UINT_PTR)window; info.lpszText = LPSTR_TEXTCALLBACKW;` then `TTM_ADDTOOLW`. Use `TTTOOLINFOW_V1_SIZE`, not `sizeof`, so it works with either comctl32 version.
  - `TTM_SETMAXTIPWIDTH` = 360 (this is what enables line breaks) and `TTM_SETDELAYTIME` / `TTDT_AUTOPOP` = 30000.
  - `WM_NOTIFY` with `TTN_GETDISPINFOW`: point `lpszText` at `host->previous.tip`; if it is empty, set `lpszText` to an empty string so no tooltip shows.
  - In `Host::update`: when `wcscmp(previous.tip, view.tip) != 0`, send `TTM_UPDATE` after storing the new view.
  - **Redraw comparison** (line 163): the tip must not trigger a repaint. Change the `memcmp` length to `offsetof(View, tip) - offsetof(View, state)`.
  - Tooltip creation failing is not fatal: keep running without it.
- `selfTest()`: add a case that an unterminated `tip` is rejected (`std::wmemset(invalid.tip, L'x', 256)` → `valid` false), and set a normal tip on the valid view.

**`native/host.manifest`** — add the Common-Controls v6 dependency (`Microsoft.Windows.Common-Controls`, version `6.0.0.0`, `processorArchitecture="amd64"`, `publicKeyToken="6595b64144ccf1df"`, `language="*"`) so the tooltip uses the modern style.

**`native/CMakeLists.txt`** — add `comctl32` to `target_link_libraries`.

**`validation/NativeChecks.cs`**

- Line 161: `312` → `824`.
- Read the whole file before editing: it drives the helper through `TaskbarTimer` and `NativeTaskbarHost` with reflection (`Field<...>`). The existing assertions for default mode must keep passing unchanged, including line 249 ("Failed native helper was automatically respawned" — the **full app must still not respawn**; respawn is widget-only, Step 5).
- Add one instant-mode check next to the click checks (lines 205–230): start a `NativeTaskbarHost` with `InstantClick = true` on the stand-in parent, deliver one left click, assert the toggle arrives without pumping the double-click interval, and that a double-click yields two toggles and zero opens.

### Step 3 — Colour picker without an anchor element

`src/ColorPicker.cs`, add next to `Show(UIElement)`:

```csharp
public void ShowAbove(Native.ScreenPoint at)
```

- `popup.PlacementTarget = null; popup.Placement = PlacementMode.AbsolutePoint; popup.HorizontalOffset = popup.VerticalOffset = 0;`
- Subscribe once to `popup.Opened`: get the `HwndSource` from `popup.Child`, call `Native.PinPopup(handle, 0, IntPtr.Zero, at)` (places it above the point, clamped to that monitor's work area, physical pixels) and `Native.SetForegroundWindow(handle)` so an outside click dismisses it (`StaysOpen = false`).
- `popup.IsOpen = true`.

This mirrors what `PopupMenu.Show(..., screenPosition, activate: true)` does (PopupMenu lines 344–361 of the old Ui.cs). The picker's size is fixed (504 wide, page height 140), so one pin after opening is enough; no `WM_WINDOWPOSCHANGING` hook.

### Step 4 — `widget/SleepTimerWidget.csproj` and `widget/Program.cs`

**csproj** — copy the property group of the root `SleepTimer.csproj` with:

- `AssemblyName` = `SleepTimerWidget`, `RootNamespace` = `SleepTimer`.
- `ApplicationManifest` = `..\app.manifest`, `ApplicationIcon` = `..\assets\SleepTimer.ico`.
- `Reference` to `UIAutomationClient`, `UIAutomationTypes`.
- `Compile Include="*.cs"` plus this explicit list from `..\src\`:
  `Countdown.cs`, `TimerDial.cs`, `QuickTimers.cs`, `WheelInput.cs`, `PowerAction.cs`, `Platform.cs`, `ScreenPlacement.cs`, `CrashLog.cs`, `TaskbarLocator.cs`, `TaskbarPlacement.cs`, `NativeTaskbarHost.cs`, `Theme.cs`, `PopupMenu.cs`, `ToolTips.cs`, `AppFonts.cs`, `ColorPicker.cs`, `TrayGraphics.cs`.
- **Not** included: `MainWindow.cs`, `TaskbarTimer.cs`, `Ui.cs`, `DesignControls.cs`, `FontWindow.cs`, `AnimationTiming.cs`, `Shortcut.cs`, `Program.cs`.
- The root `Directory.Build.targets` applies automatically and embeds `native/bin/SleepTimer.Taskbar.exe` under the logical name `NativeTaskbarHost` expects. Its "not included" message is conditioned on `AssemblyName == 'SleepTimer'`; add an equivalent `Message` (not a warning — the Linux build has no helper and uses `-warnaserror`) for `SleepTimerWidget` saying the build will show only the tray icon.

If a listed file fails to compile because it references something outside the list, fix it by adding the small dependency file, not by editing shared code. Known and already covered: `Theme.Init` → `ToolTips`, `AppFonts`; `Native.PinPopup` → `ScreenPlacement`; `CrashLog` → `Program.Version`; `Startup.Set` → `Program.TrayArg`.

**`widget/Program.cs`** — same shape as `src/Program.cs`:

- Assembly attributes: title/product "Sleep Timer Widget", version `1.0.0.0`.
- `static class Program` in namespace `SleepTimer` with `public const string TrayArg = "--tray";` (accepted and ignored) and `public static readonly string Version`.
- `Main`: `Native.SetDllDirectory("")`, same AppUserModelID, `SingleInstance.Claim()`, `new Application { ShutdownMode = OnExplicitShutdown }`, the two crash handlers, `Theme.Init(app)`, `using (var widget = new WidgetApp())`.
- `--verify-startup`: construct `WidgetApp` with `TestMode = true`, pump nothing, dispose, return (exit code 0). Used by CI.
- `app.SessionEnding += widget.EndSession`.
- `SingleInstance.Listen(...)`: a second launch opens the menu at the cursor (dispatcher `BeginInvoke`).
- `app.Run()`.

### Step 5 — `widget/WidgetHost.cs` (placement and helper lifetime)

A plain `sealed class WidgetHost : IDisposable`, **not** a WPF `Window`. Derive it from `src/TaskbarTimer.cs`, keeping its logic and comments, removing the WPF surface.

Owner window:

- `new HwndSource(new HwndSourceParameters("Sleep Timer Widget") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) /* WS_POPUP */, ExtendedWindowStyle = 0x08000080 /* NOACTIVATE | TOOLWINDOW */ })`, never shown.
- It **must be a top-level window, not message-only** (`HWND_MESSAGE`): the `TaskbarCreated` broadcast, `WM_DISPLAYCHANGE` (0x007E) and `WM_DWMCOMPOSITIONCHANGED` (0x031E) are not delivered to message-only windows.
- `AddHook(WindowMessage)`: forward to `native?.HandleMessage(...)` first, then the three messages above → `QueueForegroundRefresh()`.

Keep from `TaskbarTimer` (same code, same constants):

- `WatchTaskbar()`, the Explorer-filtered `SetWinEventHook(0x8000, 0x800B, …)`, `QueueForegroundRefresh()`, `RefreshAfterForegroundChange()`.
- `RefreshPlacement()` including the 1500 ms stuck-probe guard, the 200 ms probe rate limit, the 300 ms settle wait, and the 100 ms / 30 s `placement` interval switch.
- `EnsureNative()`, `SyncNative()`, `Rgb()`, `Update(...)` with its `viewKey` de-duplication.

Remove:

- Everything visual: `button`, `time`, `action`, `ring`, `MiniRing`, tooltip, mouse handlers, `leftClick` timer, `suppressLeftRelease`, `Show`/`Hide`/`IsVisible`, `FollowTaskbarLayer`, `SetWindowPos` on the own window, `RenderMode`.
- `canShow` and the global foreground hook (`SetWinEventHook(3, 3, …)`): a native child inherits the taskbar's visibility and Z-order. Use `TaskbarLocator.CanEmbed` only.
- `preferNative`, `RendererDescription`, `RendererDetails`, `parentOverride`.
- The `open` callback.

State the view needs is held as plain fields (`timeText`, `actionText`, `tipText`, `state`, `fraction`, `tone`) instead of WPF controls. Width/height stay 140 × 34 logical.

Constructor: `WidgetHost(Action toggle, Action cancel, Action menu, Action<int, ModifierKeys> scroll)`. When creating `NativeTaskbarHost` pass `open: null`, then set `InstantClick = true` and `UpdateTimeout = 250`.

**Respawn (improvement 1)** — replaces the "give up and fall back" path:

- When the `changed` callback fires with `created.Failure != null`, dispose it, set `native = null`, and start a one-shot `DispatcherTimer` with `WidgetLogic.RespawnDelay(attempt++)` (1 s, 5 s, 30 s, then 300 s forever). On tick: clear `nativeAttempted` and call `RefreshTaskbarLayer()`.
- Reset `attempt` to 0 once a helper has been `Ready` for 60 s, and whenever Explorer's process id changes (existing `WatchTaskbar` branch).
- `NativeTaskbarHost.Start()` returning false with `ExecutablePath() == null` (helper not embedded, or 32-bit Windows) is permanent: do not schedule retries; expose `public string Failure` so `WidgetApp` can show it in the menu.
- Stop the timer in `Dispose`.

`Dispose`: stop timers, remove hook, dispose native, unhook WinEvent, dispose the `HwndSource`.

### Step 6 — `widget/WidgetLogic.cs` (pure, no WPF types)

```csharp
static class WidgetLogic
{
    public static double RespawnDelay(int attempt);        // seconds: 1, 5, 30, 300, 300, …
    public static int WheelMinutes(int modifiers);         // bit 1 Alt, 2 Ctrl, 4 Shift → 1 / 30 / 15, Ctrl+Shift 60, none 5
    public static double NextTickMilliseconds(double remainingSeconds); // to the next whole second, clamped to [16, 1000]
    public static string Tip(string time, string action, TimerState state, double? secondsLeft);
}
```

- `WheelMinutes` is the mapping from `TimerFace.Scroll` (Ui.cs lines 352–354); the bit values equal both the helper's encoding and `ModifierKeys`.
- `Tip` returns (lines joined with `\r\n`, total under 255 characters):
  - Heading: `"{time} · {action} · {Ready|Running|Paused}"`, plus `" · {TimeText.Human(secondsLeft)} left"` when `secondsLeft` is given (used in At-time mode while running, so the tooltip changes once a minute, not every second).
  - `Left-click: start / pause / resume`
  - `Right-click: cancel timer, or menu when idle`
  - `Middle-click: menu`
  - `Scroll: ±5 min · Alt 1 · Shift 15 · Ctrl 30 · Ctrl+Shift 60`

Add this file and assertions for all four functions to `scripts/check-timers.sh` (it already compiles a hand-picked file list against net8; add `widget/WidgetLogic.cs` to the `<Compile>` list and checks to its embedded `Program.cs`).

### Step 7 — `widget/WidgetApp.cs` (timer, menu, tray, power)

`sealed class WidgetApp : IDisposable`. Logic is lifted from `MainWindow.cs`; keep its error handling. Source line references are to `src/MainWindow.cs`.

**State**: `Stopwatch clock`, `Countdown countdown`, `HourStop hourStop`, `WheelInput wheel`, `QuickTimers quickTimers`, `PowerAction action`, `bool clockMode`, `int clockMinute`, `int awakeMinutes`, `double awakeEnd`, `bool screenOn`, `bool force`, `bool startWithWindows`, `bool warned, executing, disposed, sessionEnding`, `int page`, `DispatcherTimer ticker`, `durationSave`, `aborted`, `Forms.NotifyIcon tray`, `PopupMenu menu`, `ColorPicker picker`, `WidgetHost host`, `public bool TestMode`.

**Settings read at start** (same names and defaults as lines 79–90): `duration`, `clock_mode`, `action`, `awake`, `awake_screen`, `force` (default true), `start_with_windows`, `quick_timers`. If `start_with_windows` is true call `Startup.Set(true)` so the Run key points at this exe.

**Timer operations** — copy, minus dial/window calls:

| Method | Source | Notes |
|---|---|---|
| `StartPause()` | 741–758 | drop `SyncClockTarget` text field write, keep `clockMinute` sync |
| `Cancel()` | 768–773 | |
| `StartQuickTimer(int)` | 774–784 | single `action`; no `quickAction` |
| `AddTime(int delta)` | 711–739 | drop the `CompositionTarget.Rendering` batching; call `UpdateView()` directly |
| `SetAwake`, `ApplyAwake` | 932–937 | |
| `SaveDuration` | 938–942 | keep the 250 ms debounce timer |
| `Fire()`, `OnResumed()` | 1075–1114 | keep the sleep thread, the `Task.Run` shutdown, both error balloons and the 1-minute `aborted` timer |

Scroll entry point (improvement 4): `Scroll(int wheelDelta, ModifierKeys keys)` → `int seconds = wheel.Add(wheelDelta, WidgetLogic.WheelMinutes((int)keys) * 60); if (seconds != 0) AddTime(seconds);`. Ignore while `executing`.

Action: when the user picks an action write both `action` and `quick_action` so the full app stays consistent. Rows are enabled only when idle and not executing (same rule as `SetAction`, line 705).

**Tick**: one `DispatcherTimer` (Background priority).

- Running: interval `WidgetLogic.NextTickMilliseconds(countdown.Remaining)`.
- Else if `awakeMinutes > 0`: 1000 ms.
- Else: stopped (idle and paused cost nothing).
- On tick (lines 963–973): expire keep-awake, `Fire()` on `countdown.Expired`, the one-time 60-second warning, `UpdateView()`.
- Warning balloon text: `"{action.Label} in {time}. Click the taskbar timer to pause, right-click to cancel."` No click handler.

**`UpdateView()`** — computes once and pushes to both surfaces:

- `time`: `clockMode ? "At " + HH:mm : TimeText.Clock(seconds)` exactly as lines 1046–1048.
- `host.Update(time, action.Label, state, idle ? 1 : countdown.Fraction, TimerStatus.Tone(state, seconds), tip)` where `tip = WidgetLogic.Tip(...)`; the action line suffix (`" · Ready"`, `" · Paused"`) is applied as in `TaskbarTimer.Update` (line 322).
- Tray icon: `UpdateIcon` from lines 1053–1064, first block only (the 64-px window icon block is dropped). Keep the handle bookkeeping and `DestroyIcon`. `tray.Text` = `"Sleep Timer · " + time + " · " + status` (max 63 characters).
- If the menu is open, `menu.SetRows(Rows())`.
- Re-arm the ticker.

**Menu** — one `PopupMenu`, pages switched with `SetRows` and `KeepOpen` rows, same pattern as `TrayRows()` (lines 874–923).

Opening: `ShowMenu()` copies `ShowTaskbarMenu` + `ShowTrayMenu` (lines 649–657, 925–931): toggle closed if `menu.Visible || menu.JustClosed`, read `Native.GetPhysicalCursorPos`, `page = 0`, `menu.Show(Rows(), null, screenPosition: point, activate: true)`. Remember the point for the colour picker. Always invoke through `Dispatcher.BeginInvoke(DispatcherPriority.Input, …)` from widget and tray callbacks.

Right-click on the widget: active timer → `menu.Close(); Cancel();`, idle → `ShowMenu()` (lines 642–646). Tray icon: right mouse-up → `ShowMenu()`; left click → nothing.

Page 0, root:

```
Start timer                 30:00      idle: Right = duration, or "At 23:30"
   — or, when active —
Pause | Resume
Cancel timer
────────────────
30 minutes                             one row per quickTimers.All; click starts it now
1 hour
2 hours
4 hours
…custom presets
────────────────
Action              Shut down ›        → page 1
Timer mode           Duration          KeepOpen toggle: Duration ⇄ At time
Keep awake                Off ›        → page 2 (Right as in line 907)
────────────────
Settings                    ›          → page 3
Quit                                   Danger; → page 5 when a timer is active
```

- Page 1, action: back row, then `PowerAction.All` with `Selected`.
- Page 2, keep awake: back row, then `AwakeRows()` unchanged (lines 861–867).
- Page 3, settings:
  - `Accent colour` (Right = hex) → closes the menu, then `picker = new ColorPicker(Theme.Accent); picker.Picked += ApplyAccent; picker.ShowAbove(lastMenuPoint);`
  - `Save {label} as preset` — only when idle, Duration mode, whole minutes, and `quickTimers.Add` would accept it. Saves `quick_timers`.
  - `Remove preset ›` → page 4, only when `quickTimers.Custom` is non-empty.
  - `Force close apps` On/Off (KeepOpen toggle, setting `force`).
  - `Start with Windows` On/Off (KeepOpen toggle, `Startup.Set` + setting).
  - If `host.Failure != null`: a disabled row with that text.
  - Disabled row `Sleep Timer Widget {Program.Version}`.
- Page 4, remove preset: back row, one row per custom preset; click removes, saves, stays open.
- Page 5, quit confirmation: `‹ Keep timer`, `Quit and cancel timer` (Danger). Replaces the `ConfirmQuit` dialog window.

All timer-changing rows carry `Enabled = !executing`.

Timer mode toggle: copy the state changes of `SetMode` from `MainWindow` (read that method first; set `clock_mode`, and when switching to At time and the user has not touched the clock, `clockMinute = TimeText.DefaultClock(...)`). Scroll then adjusts whichever value is shown.

`ApplyAccent(Color)`: `Theme.Accent = color; Theme.UpdateResources(Application.Current.Resources);` save `accent` as `#RRGGBB`; clear the icon cache key; `UpdateView()`. (`WidgetHost.Update`'s `viewKey` already includes `Theme.Accent`.)

**Quit / dispose**: `Quit()` via `BeginInvoke(Input)`; `Dispose` mirrors lines 1167–1179: save duration, stop timers, dispose menu, close picker, dispose host, hide and dispose tray icon, `DestroyIcon`, `Native.KeepAwake(false, false)`; then `Application.Current.Shutdown()` unless `TestMode`.

`TestMode`: no balloons, no ticker, `Fire()` only resets (as line 1077).

### Step 8 — Build, CI and docs

- `build.cmd`: after the existing build, also `dotnet build widget\SleepTimerWidget.csproj -c Release -o bin\ReleaseWidget` and copy `SleepTimerWidget.exe` to the project folder. Add `SleepTimerWidget.exe` to `.gitignore`.
- `.github/workflows/validate.yml`, `windows` job, after `build-native.ps1`:
  - `dotnet build widget/SleepTimerWidget.csproj -c Release --nologo -warnaserror`
  - run `SleepTimerWidget.exe --verify-startup` with the same 15-second guard used for the main exe.
  - add the exe to the uploaded artifact.
  - Do **not** touch the release-publishing step.
- `README.md`: short "Widget-only version" section (what it is, controls, that it shares settings with the full app and cannot run at the same time).
- `docs/NATIVE_TASKBAR.md`: protocol v2, tooltip, `instant` argument, widget-only respawn.
- `docs/DEVELOPMENT.md`: the Linux compile command from Step 0; how to build the widget.
- `CHANGELOG.md`: one entry.

### Step 9 — Verification

Can be done in the Linux container:

1. `dotnet build SleepTimer.csproj … -p:EnableWindowsTargeting=true` — 0 warnings.
2. Same for `widget/SleepTimerWidget.csproj` and `validation/WindowsChecks.csproj`.
3. `bash scripts/check-timers.sh` passes with the new `WidgetLogic` checks.

Cannot be done there — must run on Windows CI (the `Validate` workflow runs on pull requests and has `workflow_dispatch`; it does not run on plain pushes to this branch). Ask the user before opening a pull request or dispatching a run:

4. `scripts/build-native.ps1` (MSVC `/W4 /WX` compile and helper self-test).
5. `scripts/check-windows.ps1` with `SLEEP_TIMER_NATIVE_REQUIRED=1` (all existing checks plus the instant-click check).
6. Widget startup check.

Needs a real Windows 11 desktop, by the user — report these as untested until they confirm:

- Widget appears left of the tray; survives Explorer restart, DPI change, Start menu, fullscreen app.
- Left-click reacts immediately; fast double-click = start then pause.
- Tooltip appears above the taskbar, is readable, disappears on click.
- Menu and colour picker open above the cursor on the right monitor and close on an outside click without stealing focus afterwards.
- Killing `SleepTimer.Taskbar.exe` in Task Manager brings the widget back within about a second.
- Real shutdown, sleep and restart; "Start with Windows" entry points at `SleepTimerWidget.exe`.

## 5. Risks to watch

- **Tooltip ownership.** The tooltip is owned by a child of Explorer's taskbar window. It should appear above the taskbar with `WS_EX_TOPMOST`; if it shows behind it or not at all, try creating it unowned (`hwndParent = nullptr`). Only a desktop test can tell.
- **Protocol bump touches the full app.** `View` size and version change for both apps at once; a mismatch makes the helper reject every update. The managed/native size check (NativeChecks line 161) and `static_assert` are the guard — keep both at 824.
- **`UpdateTimeout = 250` blocks the UI thread** for up to 250 ms when the helper is slow. Acceptable in the widget app (nothing animates); the full app keeps 40 ms.
- **Shared Run key and mutex.** Enabling "Start with Windows" in one app replaces the other's entry, and the second app to start exits silently. Document it in the README.
- **No helper, no widget.** An SDK-only or 32-bit build shows only the tray icon; the menu's failure row is the explanation.
- **Do not "fix" the full app along the way.** `TaskbarTimer`, `MainWindow` and the default helper behaviour are covered by 1,200 lines of Windows checks that assert current behaviour, including no respawn and the double-click wait.

## 6. Out of scope

Global hotkeys, fonts, animation settings, WPF fallback renderer, a pure-C++ rewrite, changing the full app's click/respawn/scroll behaviour, light-taskbar colours, publishing a release.
