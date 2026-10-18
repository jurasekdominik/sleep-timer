using System;

namespace SleepTimer
{
    enum TimerTone { Idle, Running, Paused, Warning, Urgent }

    static class TimerStatus
    {
        public static TimerTone Tone(TimerState state, double remaining) => state == TimerState.Idle ? TimerTone.Idle
            : state == TimerState.Paused ? TimerTone.Paused : remaining <= 10 ? TimerTone.Urgent
            : remaining <= 60 ? TimerTone.Warning : TimerTone.Running;

        public static int Complete(double remainingFraction) => (int)Math.Floor(100 * (1 - Math.Max(0, Math.Min(1, remainingFraction))));
    }

    static class TimerDial
    {
        public static double HourFill(double remaining) => Math.Max(0, Math.Min(Countdown.MaxSet, remaining)) / 3600;

        /// <summary>Wrap an animated total-second position into the current hourly arc.</summary>
        public static double ArcFraction(double remaining)
        {
            double seconds = Math.Max(0, Math.Min(Countdown.MaxSet, remaining));
            if (seconds == 0) return 0;
            double part = seconds % 3600;
            return part == 0 ? 1 : part / 3600;
        }
    }
}
