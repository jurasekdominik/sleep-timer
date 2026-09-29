# Sleep Timer

A clean, circular sleep timer for Windows.

Set a countdown or a specific time, then let your PC shut down, sleep, or restart automatically. Minimal dark UI with a matcha accent.

## Features

- **Circular countdown** with smooth progress ring
- **Two modes**
  - Duration (e.g. 30 minutes)
  - At time (e.g. 01:00 — automatically uses tomorrow if the time has already passed)
- **Actions**: Shut down / Sleep / Restart
- **Force close** apps that block shutdown (optional)
- **Mouse wheel** support over the ring (with modifier keys for different step sizes)
- **System tray** support (minimize to tray)
- **Start with Windows** (launches minimized to tray)
- Remembers last duration, mode, action, and settings
- Prevents Windows from idle-sleeping while a timer is running
- Dark title bar + consistent matcha theme

## Download

Grab the latest release from the [Releases](../../releases) page.

Single executable, no installation required (~21 MB).

## Usage

1. Run `Sleep Timer.exe`
2. Choose **Duration** or **At time**
3. Select what should happen when the timer ends (Shut down / Sleep / Restart)
4. Click **Start**

### Mouse wheel shortcuts (over the ring)

| Modifier       | Step   |
|----------------|--------|
| None           | ±5 min |
| Alt            | ±1 min |
| Shift          | ±15 min|
| Ctrl           | ±30 min|
| Ctrl + Shift   | ±60 min|

### Start with Windows

Enable the **Start with Windows (tray)** option.  
The app will launch minimized to the system tray on every login.

## Building from source

### Requirements

- Python 3.12 or 3.13
- Windows
