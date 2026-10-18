#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
check_dir="$(mktemp -d /tmp/sleep-timer-checks.XXXXXX)"
trap 'rm -rf "$check_dir"' EXIT
cat > "$check_dir/Checks.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup>
  <ItemGroup>
    <Compile Include="Program.cs" />
    <Compile Include="$(SleepTimerRoot)/src/Countdown.cs" />
    <Compile Include="$(SleepTimerRoot)/src/TimerDial.cs" />
    <Compile Include="$(SleepTimerRoot)/src/QuickTimers.cs" />
    <Compile Include="$(SleepTimerRoot)/src/WheelInput.cs" />
    <Compile Include="$(SleepTimerRoot)/src/AnimationTiming.cs" />
    <Compile Include="$(SleepTimerRoot)/src/PowerAction.cs" />
    <Compile Include="$(SleepTimerRoot)/src/Shortcut.cs" />
    <Compile Include="$(SleepTimerRoot)/src/ScreenPlacement.cs" />
    <Compile Include="$(SleepTimerRoot)/src/TaskbarPlacement.cs" />
  </ItemGroup>
</Project>
PROJECT
cat > "$check_dir/Program.cs" <<'CHECKS'
using System;
using SleepTimer;
using System.Linq;
using System.Drawing;
class Checks
{
    static int checks;
    static void Equal(double actual, double expected, string name)
    {
        if (Math.Abs(actual - expected) > 0.000001) throw new Exception(name + ": " + actual + " != " + expected);
        checks++;
    }
    static void Main()
    {
        Equal(TimerDial.ArcFraction(9000), .5, "2h30 half ring");
        Equal(TimerDial.ArcFraction(7199), 3599d / 3600, "rollover refills ring");
        Equal(TimerDial.ArcFraction(-1), 0, "expired arc");
        double now = 0;
        var timer = new Countdown(7200, () => now);
        timer.Start(timer.Duration);
        now = 1800;
        Equal(TimerDial.HourFill(timer.Remaining), 1.5, "running hour marks");
        Equal(TimerDial.ArcFraction(timer.Remaining), .5, "running minute position");
        timer.Pause(); now += 60;
        Equal(timer.Remaining, 5400, "paused position");
        timer.Resume(); now += 60;
        Equal(timer.Remaining, 5340, "resumed position");
        timer.Adjust(-86400);
        Equal(timer.Remaining, 10, "safe minimum on scroll");
        var presets = new QuickTimers("180,15,180,60,0,1441,bad,90");
        if (!presets.All.SequenceEqual(new[] {15,30,60,90,120,180,240})) throw new Exception("preset ordering and parsing"); checks++;
        if (!presets.Custom.SequenceEqual(new[] {15,90,180})) throw new Exception("custom deduplication"); checks++;
        if (presets.Add(30) || presets.Add(180) || presets.Add(0) || presets.Add(1441)) throw new Exception("duplicates and bounds"); checks++;
        if (!presets.Add(1440) || !presets.Add(1)) throw new Exception("valid bounds"); checks++;
        var restored = new QuickTimers(presets.Save());
        if (!restored.All.SequenceEqual(presets.All)) throw new Exception("save/reload"); checks++;
        if (!presets.Remove(180) || presets.All.Contains(180) || presets.Remove(30)) throw new Exception("custom removal preserves defaults"); checks++;
        foreach (string invalid in new[] {"", "1.5", "-1", "0", "1441", "9999999999", "hello", "1,5", "1.5h", "1,5h", "1h 30m", "1:30", "90m", "0h", "25h", "+2", "-2h", "9999999999h"})
        { if (QuickTimers.TryMinutes(invalid, out _)) throw new Exception("invalid input accepted"); checks++; }
        foreach (var input in new[] { ("1", 1), ("90", 90), ("1440", 1440), ("1h", 60), ("2h", 120), ("5h", 300), ("24h", 1440), (" 2H ", 120) })
        {
            if (!QuickTimers.TryMinutes(input.Item1, out int minutes)) throw new Exception("valid timer rejected: " + input.Item1);
            Equal(minutes, input.Item2, "whole-minute and whole-hour parsing");
        }
        foreach (var command in new[] { (false, false, "/s /t 0"), (false, true, "/s /f /t 0"),
            (true, false, "/r /t 0"), (true, true, "/r /f /t 0") })
        {
            if (PowerAction.ShutdownArguments(command.Item1, command.Item2) != command.Item3)
                throw new Exception("Power action/force arguments do not match the selected action");
            checks++;
        }
        foreach (int modifiers in new[] { 1, 2, 3, 6, 8, 15 })
        {
            if (!Shortcut.TryCreate(modifiers, 0x41, out var binding)) throw new Exception("valid shortcut rejected");
            if (!binding.MatchesMessage((0x41L << 16) | modifiers)
                || !binding.MatchesMessage((0x41L << 16) | modifiers | 0x4000)
                || binding.MatchesMessage((0x42L << 16) | modifiers)) throw new Exception("shortcut message matching");
            checks++;
        }
        foreach (var invalid in new[] { (0, 0x41), (4, 0x41), (16, 0x41), (2, 0), (2, 0x11), (2, 0x5B), (2, 0x100) })
        {
            if (Shortcut.TryCreate(invalid.Item1, invalid.Item2, out _)) throw new Exception("unsafe shortcut accepted");
            checks++;
        }
        if (default(Shortcut).IsBound || default(Shortcut).Matches(default)
            || default(Shortcut).MatchesMessage(0)) throw new Exception("unbound shortcut can match");
        checks++;
        Shortcut.TryCreate(3, 0x87, out var firstShortcut);
        Shortcut.TryCreate(3, 0x87, out var duplicateShortcut);
        Shortcut.TryCreate(3, 0x86, out var secondShortcut);
        if (!firstShortcut.Matches(duplicateShortcut) || firstShortcut.Matches(secondShortcut)) throw new Exception("shortcut collision detection");
        checks++;
        foreach (var sample in new[] {
            (1900, 1050, 224, 300, 0, 0, 1920, 1040, 1676, 740),
            (-20, 1000, 224, 300, -1920, 0, 0, 1040, -244, 700),
            (2000, 500, 320, 400, 1920, 0, 4480, 1400, 1920, 100),
            (100, -1000, 300, 500, 0, -1440, 2560, 0, 0, -1440),
            (1910, 10, 224, 300, 0, 40, 1920, 1080, 1686, 40) })
        {
            var position = ScreenPlacement.Above(sample.Item1, sample.Item2, sample.Item3, sample.Item4,
                sample.Item5, sample.Item6, sample.Item7, sample.Item8);
            Equal(position.X, sample.Item9, "tray monitor horizontal pixel positioning");
            Equal(position.Y, sample.Item10, "tray monitor vertical pixel positioning");
        }
        var taskbar = new Rectangle(0, 1040, 1920, 40);
        var desktop = new Rectangle(0, 0, 1920, 1080);
        var tray = new Rectangle(1700, 1040, 220, 40);
        var empty = Array.Empty<Rectangle>();
        var mini = TaskbarPlacement.LeftOfTray(taskbar, desktop, tray, 124, 32, 4, empty);
        if (mini != new Rectangle(1572, 1044, 124, 32)) throw new Exception("mini timer sits left of the notification arrow"); checks++;
        mini = TaskbarPlacement.LeftOfTray(new Rectangle(-1920, 1040, 1920, 40), new Rectangle(-1920, 0, 1920, 1080),
            new Rectangle(-220, 1040, 220, 40), 124, 32, 4, empty);
        if (mini != new Rectangle(-348, 1044, 124, 32)) throw new Exception("mini timer follows negative-coordinate monitor"); checks++;
        mini = TaskbarPlacement.LeftOfTray(new Rectangle(0, 1560, 2880, 60), new Rectangle(0, 0, 2880, 1620),
            new Rectangle(2550, 1560, 330, 60), 186, 48, 6, empty);
        if (mini != new Rectangle(2358, 1566, 186, 48)) throw new Exception("mini timer uses scaled physical pixels"); checks++;
        mini = TaskbarPlacement.LeftOfTray(new Rectangle(0, 0, 1920, 40), desktop, new Rectangle(1700, 0, 220, 40), 124, 32, 4, empty);
        if (mini != new Rectangle(1572, 4, 124, 32)) throw new Exception("top taskbar alignment"); checks++;
        if (TaskbarPlacement.LeftOfTray(new Rectangle(0, 1078, 1920, 40), desktop,
            new Rectangle(1700, 1078, 220, 40), 124, 32, 4, empty) != null) throw new Exception("auto-hidden taskbar must hide timer"); checks++;
        if (TaskbarPlacement.LeftOfTray(taskbar, desktop, tray, 124, 32, 4,
            new[] { new Rectangle(1570, 1040, 100, 40) }) != null) throw new Exception("mini timer must not cover an application button"); checks++;
        if (TaskbarPlacement.LeftOfTray(taskbar, desktop, tray, 124, 32, 4,
            new[] { new Rectangle(1400, 1040, 168, 40) }) == null) throw new Exception("nonoverlapping application buttons are allowed"); checks++;
        if (TaskbarPlacement.LeftOfTray(taskbar, desktop, new Rectangle(100, 1040, 1820, 40),
            124, 32, 4, empty) != null) throw new Exception("insufficient taskbar room must hide timer"); checks++;
        if (TaskbarPlacement.LeftOfTray(new Rectangle(0, 0, 40, 1080), desktop, new Rectangle(0, 900, 40, 180),
            124, 32, 4, empty) != null) throw new Exception("vertical taskbar must not be covered"); checks++;
        if (TaskbarPlacement.LeftOfTray(taskbar, desktop, tray, 124, 48, 4, empty) != null) throw new Exception("timer cannot spill above taskbar"); checks++;
        if (TaskbarPlacement.LeftOfTray(taskbar, desktop, new Rectangle(1700, 500, 220, 40),
            124, 32, 4, empty) != null) throw new Exception("stale tray bounds must be rejected"); checks++;
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
        double scheduledTick = 0;
        var wall = new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.FromHours(2));
        var scheduled = new Countdown(1800, () => scheduledTick, () => wall);
        var deadline = wall.AddHours(1);
        scheduled.StartAt(deadline, zone);
        Equal(scheduled.Remaining, 3600, "at-time starts against wall-clock deadline");
        wall = wall.AddMinutes(10); scheduledTick += 600; scheduled.Pause();
        Equal(scheduled.Remaining, 3000, "at-time pauses without losing its deadline");
        wall = wall.AddMinutes(20); scheduledTick += 1200;
        Equal(scheduled.Remaining, 3000, "paused display remains frozen");
        scheduled.Resume();
        Equal(scheduled.Remaining, 1800, "at-time resume keeps original wall-clock deadline");
        if (scheduled.TargetTime != deadline) throw new Exception("resume shifted selected clock target"); checks++;
        scheduled.Adjust(300);
        if (scheduled.TargetTime != deadline.AddMinutes(5)) throw new Exception("active wheel edit did not move deadline"); checks++;
        Equal(scheduled.Remaining, 2100, "active at-time wheel adjustment");
        scheduled.Pause(); wall = wall.AddMinutes(40); scheduledTick += 2400;
        if (scheduled.Expired) throw new Exception("paused schedule fired"); checks++;
        scheduled.Resume();
        var following = TimeText.NextOccurrence(wall, 11 * 60 + 5, zone);
        if (scheduled.TargetTime != following || scheduled.Expired) throw new Exception("missed paused target must resume at next occurrence"); checks++;
        scheduled.Reset();
        if (scheduled.TargetTime.HasValue || scheduled.State != TimerState.Idle) throw new Exception("cancel retained active deadline"); checks++;
        scheduled.StartAt(wall.AddHours(1), zone); scheduled.Start(600); scheduled.Pause();
        wall = wall.AddMinutes(5); scheduledTick += 300; scheduled.Resume();
        Equal(scheduled.Remaining, 600, "duration mode still freezes across pause");
        if (scheduled.TargetTime.HasValue) throw new Exception("duration start retained a clock deadline"); checks++;
        wall = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.FromHours(2));
        scheduled.StartAt(TimeText.NextOccurrence(wall, 150, zone), zone);
        wall = wall.AddMinutes(15); scheduledTick += 900; scheduled.Pause();
        wall = wall.AddHours(1); scheduledTick += 3600; scheduled.Resume();
        Equal(scheduled.Remaining, 2700, "resume during repeated hour uses next valid occurrence");
        wall = wall.AddMinutes(-10);
        Equal(scheduled.Remaining, 3300, "wall-clock correction retains selected at-time target");
        var fall = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.FromHours(2));
        Equal((TimeText.NextOccurrence(fall, 210, zone) - fall).TotalHours, 3, "fall clock transition");
        var spring = new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.FromHours(1));
        Equal((TimeText.NextOccurrence(spring, 210, zone) - spring).TotalHours, 1, "spring clock transition");
        var gap = TimeText.NextOccurrence(spring, 150, zone);
        Equal(gap.Hour * 60 + gap.Minute, 180, "nonexistent time advances to first valid minute");
        Equal((gap - spring).TotalMinutes, 30, "nonexistent time elapsed duration");
        Equal((TimeText.NextOccurrence(fall, 150, zone) - fall).TotalHours, 1, "first repeated time");
        var repeated = new DateTimeOffset(2026, 10, 25, 2, 45, 0, TimeSpan.FromHours(2));
        Equal((TimeText.NextOccurrence(repeated, 150, zone) - repeated).TotalMinutes, 45, "second repeated time when first has passed");
        var normal = new DateTimeOffset(2026, 10, 15, 23, 30, 0, TimeSpan.FromHours(2));
        Equal((TimeText.NextOccurrence(normal, 30, zone) - normal).TotalHours, 1, "next-day scheduling");
        Equal(TimeText.DefaultClock(spring, 3600, zone), 210, "default target crosses spring clock change");
        Equal(TimeText.DefaultClock(fall, 7200, zone), 150, "default target crosses fall clock change");
        var round = new DateTimeOffset(2026, 10, 15, 12, 0, 45, TimeSpan.Zero);
        Equal(TimeText.DefaultClock(round, 1800, TimeZoneInfo.Utc), 755, "round seconds up to five-minute boundary");
        Equal(TimeText.DefaultClock(round.AddSeconds(-45), 1800, TimeZoneInfo.Utc), 750, "exact five-minute boundary stays unchanged");
        Equal(TimeText.DefaultClock(new DateTimeOffset(2026, 10, 15, 23, 29, 59, TimeSpan.Zero).AddMilliseconds(999), 1800, TimeZoneInfo.Utc), 0, "round across midnight");
        if (QuickTimers.Label(60) != "1 hour" || QuickTimers.Label(120) != "2 hours" || QuickTimers.Label(90) != "1 h 30 min") throw new Exception("duration labels"); checks++;
        now = 0;
        timer = new Countdown(7200, () => now);
        timer.Start(timer.Duration);
        timer.Adjust(-5400);
        Equal(TimerDial.HourFill(timer.Remaining), .5, "manual decrease removes hour marks");
        Equal(TimerDial.ArcFraction(timer.Remaining), .5, "manual decrease moves minute arc");
        now += 60;
        timer.Adjust(7200);
        Equal(TimerDial.HourFill(timer.Remaining), 8940d / 3600, "manual increase adds hour marks");
        timer.Pause();
        timer.Adjust(43200);
        Equal(TimerDial.HourFill(timer.Remaining), 52140d / 3600, "paused editing adds hour marks");
        timer.Reset(); timer.SetDuration(180 * 60); timer.Start(timer.Duration);
        Equal(timer.Remaining, 10800, "quick timer replaces active countdown");
        Equal((int)timer.State, (int)TimerState.Running, "quick timer runs immediately");
        foreach (double left in new[] {0d, 10, 60, 3600})
        {
            Equal((int)TimerStatus.Tone(TimerState.Idle, left), (int)TimerTone.Idle, "idle state overrides warning");
            Equal((int)TimerStatus.Tone(TimerState.Paused, left), (int)TimerTone.Paused, "paused state overrides warning");
        }
        Equal((int)TimerStatus.Tone(TimerState.Running, 61), (int)TimerTone.Running, "normal running state");
        Equal((int)TimerStatus.Tone(TimerState.Running, 60), (int)TimerTone.Warning, "warning boundary");
        Equal((int)TimerStatus.Tone(TimerState.Running, 11), (int)TimerTone.Warning, "warning before urgent");
        Equal((int)TimerStatus.Tone(TimerState.Running, 10), (int)TimerTone.Urgent, "urgent boundary");
        Equal(TimerStatus.Complete(1), 0, "new timer completion");
        Equal(TimerStatus.Complete(.625), 37, "overall completion percentage");
        Equal(TimerStatus.Complete(0), 100, "finished completion");
        Equal(TimerStatus.Complete(1.5), 0, "bounded completion");
        Equal(TimerStatus.Complete(-.5), 100, "bounded expiration completion");
        Equal(TimerDial.HourFill(9000), 2.5, "2h30 last hour half faded");
        Equal(TimerDial.HourFill(10800), 3, "3h all hour segments full");
        Equal(TimerDial.HourFill(7200), 2, "2h two segments full at rollover");
        Equal(TimerDial.HourFill(5400), 1.5, "1h30 second hour half faded");
        Equal(TimerDial.HourFill(3600), 1, "first hour included");
        Equal(TimerDial.HourFill(1800), .5, "first hour half faded");
        Equal(TimerDial.HourFill(86400), 24, "24h fills inner circle");
        Equal(TimerDial.HourFill(0), 0, "expired hour blocks");
        Equal(TimerDial.HourFill(-1), 0, "negative hour blocks");
        Equal(TimerDial.HourFill(90000), 24, "maximum hour blocks");
        if (TimerDial.HourFill(7201) <= TimerDial.HourFill(7200) || TimerDial.HourFill(7200) <= TimerDial.HourFill(7199))
            throw new Exception("hour fading must decrease continuously across rollover");
        checks++;
        Equal(TimerDial.ArcFraction(1800.25), 1800.25 / 3600, "subsecond arc progress");
        if (TimerDial.HourFill(7200.25) <= TimerDial.HourFill(7200.1))
            throw new Exception("hour fading needs continuous subsecond progress");
        checks++;
        Equal(TimerDial.ArcFraction(3600), 1, "exact hour draws full ring");
        Equal(TimerDial.ArcFraction(3630), 30d / 3600, "forward animation wraps through top");
        Equal(TimerDial.ArcFraction(3570), 3570d / 3600, "backward animation wraps through top");
        Equal(TimerDial.ArcFraction(10830), 30d / 3600, "later revolutions wrap identically");
        Equal(TimerDial.ArcFraction(0), 0, "expired animated arc is empty");
        Equal(AnimationTiming.Normalize(600), 600, "default animation duration");
        Equal(AnimationTiming.Normalize(0), 0, "instant animation supported");
        Equal(AnimationTiming.Normalize(-1), 600, "invalid persisted duration falls back");
        Equal(AnimationTiming.Normalize(10001), 600, "oversized persisted duration falls back");
        foreach (string invalid in new[] { "", "-1", "1.5", "10001", "fast", "999999999999" })
        { if (AnimationTiming.TryParse(invalid, out _)) throw new Exception("invalid animation duration accepted"); checks++; }
        foreach (string valid in new[] { "0", "120", "600", "10000" })
        { if (!AnimationTiming.TryParse(valid, out _)) throw new Exception("valid animation duration rejected"); checks++; }
        var stop = new HourStop();
        Equal(stop.Adjust(3300, 900, 0), 300, "coarse wheel snaps to first hour");
        Equal(stop.Adjust(3600, 300, 399), 0, "hour hold lasts 400 ms");
        Equal(stop.Adjust(3600, 300, 400), 300, "hour hold ends at 400 ms");
        stop.Reset();
        Equal(stop.Adjust(3900, -1800, 0), -300, "descending hour snap");
        Equal(stop.Adjust(3600, 60, 1), 60, "reversal bypasses hour hold");
        stop.Reset();
        Equal(stop.Adjust(3300, 86400, 0), 300, "burst stops at first crossed hour");
        Equal(stop.Adjust(3600, 86400, 400), 3600, "continued burst stops at next hour");
        stop.Reset();
        Equal(stop.Adjust(3300, 300, 0), 300, "exact landing starts hold");
        Equal(stop.Adjust(3599.6, 60, 399), 0, "countdown keeps ticking during hold");
        Equal(stop.Adjust(3599.6, 60, 400), 60, "countdown drift does not retrigger same stop");
        stop.Reset();
        Equal(stop.Adjust(3600, -60, 0), -60, "scroll away from initial whole hour");
        Equal(stop.Adjust(60, -300, 500), -300, "zero is not an hour stop");
        var wheel = new WheelInput();
        for (int i = 0; i < 7; i++) Equal(wheel.Add(15, 300), 0, "partial wheel delta");
        Equal(wheel.Add(15, 300), 300, "eight small deltas make one notch");
        Equal(wheel.Add(1200, 300), 3000, "high-resolution burst preserves travel");
        Equal(wheel.Add(int.MaxValue, 3600), Countdown.MaxSet, "positive burst bounded");
        Equal(wheel.Add(int.MinValue, 3600), -Countdown.MaxSet, "negative burst bounded");
        wheel = new WheelInput(); wheel.Add(60, 300);
        Equal(wheel.Add(-120, 300), -300, "direction reversal immediate");
        wheel.Add(60, 300);
        Equal(wheel.Add(60, 60), 0, "modifier change resets partial notch");
        Equal(wheel.Add(60, 60), 60, "fine wheel after modifier change");
        wheel = new WheelInput(); double travel = 0;
        for (int i = 0; i < 100000; i++) travel += wheel.Add(12, 300);
        Equal(travel, 3000000, "sustained precision-wheel stress");
        Console.WriteLine(checks + " checks passed.");
    }
}
CHECKS
dotnet run --project "$check_dir/Checks.csproj" -c Release --property:SleepTimerRoot="$repo_root"
