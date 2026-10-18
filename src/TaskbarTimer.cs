using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PixelRectangle = System.Drawing.Rectangle;

namespace SleepTimer
{
    sealed class TaskbarTimer : Window, IDisposable
    {
        readonly Border button;
        readonly TextBlock time = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 12, FontWeight = FontWeights.SemiBold };
        readonly TextBlock action = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 11, FontWeight = FontWeights.Normal };
        readonly MiniRing ring = new MiniRing { Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 0) };
        readonly DispatcherTimer placement = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        readonly Func<int, int, PixelRectangle?> locate;
        readonly Stopwatch probeAge = new Stopwatch();
        Task<PixelRectangle?> probe;
        HwndSource source;
        readonly WinEventProc foregroundChanged;
        IntPtr foregroundHook;
        bool disposed;
        string viewKey;
        PixelRectangle? previous;
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string name, string title);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
        delegate void WinEventProc(IntPtr hook, uint message, IntPtr window, int objectId, int childId, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module,
            WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

        public TaskbarTimer(Action toggle, Action menu, Func<int, int, PixelRectangle?> locate = null)
        {
            this.locate = locate ?? TaskbarLocator.Locate;
            Title = "Sleep Timer · taskbar timer"; Width = 124; Height = 32;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = ShowActivated = false; Topmost = true; AllowsTransparency = true; Background = Brushes.Transparent;
            UseLayoutRounding = SnapsToDevicePixels = true;
            FontFamily = new FontFamily("Segoe UI"); FontWeight = FontWeights.Normal;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(time); text.Children.Add(action);
            var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition()); content.Children.Add(ring);
            Grid.SetColumn(text, 1); content.Children.Add(text);
            button = new Border { Child = content, Background = Brushes.Transparent,
                Padding = new Thickness(5, 0, 5, 0), Cursor = Cursors.Hand };
            time.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            action.SetResourceReference(TextBlock.ForegroundProperty, "SecondBrush");
            Content = button;
            button.MouseLeftButtonUp += (s, e) => { toggle(); e.Handled = true; };
            button.MouseRightButtonUp += (s, e) => { menu(); e.Handled = true; };
            SourceInitialized += (s, e) =>
            {
                source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                // Keep the user's current app focused, including when clicking the timer.
                SetWindowLong(source.Handle, -20, (GetWindowLong(source.Handle, -20) | 0x08000080) & ~0x00040000);
                source.AddHook(WindowMessage);
            };
            foregroundChanged = (hook, message, window, objectId, childId, thread, stamp) =>
            {
                var taskbar = FindWindow("Shell_TrayWnd", null);
                if (!disposed && taskbar != IntPtr.Zero && GetAncestor(window, 2) == taskbar)
                    Dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() => KeepAbove(taskbar)));
            };
            // Explorer raises its own topmost window when the taskbar is clicked.
            // Restore our relative order without activating the timer or changing focus.
            foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundChanged, 0, 0, 2);
            Closed += (s, e) =>
            {
                disposed = true; placement.Stop(); source?.RemoveHook(WindowMessage);
                if (foregroundHook != IntPtr.Zero) { UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
            };
            placement.Tick += (s, e) => RefreshPlacement();
            new WindowInteropHelper(this).EnsureHandle();
            RefreshPlacement(); placement.Start();
        }

        IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0021) { handled = true; return (IntPtr)3; } // MA_NOACTIVATE
            return IntPtr.Zero;
        }

        void RefreshPlacement()
        {
            if (disposed) return;
            KeepAbove(FindWindow("Shell_TrayWnd", null));
            if (probe != null)
            {
                if (!probe.IsCompleted)
                {
                    // Never accumulate stuck accessibility calls, or keep stale coordinates visible.
                    if (probeAge.ElapsedMilliseconds > 1500) { Hide(); previous = null; }
                    return;
                }
                var bounds = probe.Status == TaskStatus.RanToCompletion ? probe.Result : null;
                if (probe.IsFaulted) { var ignored = probe.Exception; }
                probe = null;
                if (!bounds.HasValue) { Hide(); previous = null; }
                else if (!IsVisible || previous != bounds)
                {
                    var at = bounds.Value;
                    SetWindowPos(source.Handle, (IntPtr)(-1), at.X, at.Y, at.Width, at.Height, 0x0210);
                    if (!IsVisible) Show();
                    SetWindowPos(source.Handle, (IntPtr)(-1), at.X, at.Y, at.Width, at.Height, 0x0210);
                    previous = at;
                }
            }
            if (probeAge.IsRunning && probeAge.ElapsedMilliseconds < 1000) return;
            int width = (int)Width, height = (int)Height;
            probeAge.Restart(); probe = Task.Run(() => locate(width, height));
        }

        void KeepAbove(IntPtr taskbar)
        {
            if (disposed || !IsVisible || taskbar == IntPtr.Zero) return;
            // Only raise when Explorer actually covers us. Leave menus and other
            // topmost windows alone when the taskbar is already underneath.
            int remaining = 256; // Bound the walk if another process changes window order during it.
            for (var above = GetWindow(source.Handle, 3); above != IntPtr.Zero && remaining-- > 0; above = GetWindow(above, 3))
                if (above == taskbar)
                {
                    SetWindowPos(source.Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0213); // no move, size or activation
                    return;
                }
        }

        public void Update(string value, string actionLabel, TimerState state, double fraction, TimerTone tone)
        {
            string key = value + "|" + actionLabel + "|" + state + "|" + Math.Round(fraction * 100) + "|" + tone + "|" + Theme.Accent;
            if (viewKey == key) return; viewKey = key;
            time.Text = value; action.Text = actionLabel;
            button.ToolTip = value + " · " + actionLabel + " · " + (state == TimerState.Idle ? "Ready" : state.ToString())
                + "\nClick to start or pause · Right-click for options";
            ring.Fraction = fraction; ring.State = state; ring.Tone = tone; ring.InvalidateVisual();
        }

        public void Dispose() { if (!disposed) Close(); }

        sealed class MiniRing : FrameworkElement
        {
            public double Fraction;
            public TimerState State;
            public TimerTone Tone;
            protected override void OnRender(DrawingContext drawing)
            {
                var center = new Point(11, 11);
                drawing.DrawEllipse(null, new Pen(Theme.Brush(Theme.Border), 2), center, 9, 9);
                var pen = new Pen(Theme.Brush(Theme.TimerColor(Tone)), 2);
                double fraction = Math.Max(0, Math.Min(1, Fraction));
                if (fraction >= .999) drawing.DrawEllipse(null, pen, center, 9, 9);
                else if (fraction > 0)
                {
                    double angle = fraction * 2 * Math.PI;
                    var arc = new StreamGeometry();
                    using (var path = arc.Open())
                    {
                        path.BeginFigure(new Point(11, 2), false, false);
                        path.ArcTo(new Point(11 + 9 * Math.Sin(angle), 11 - 9 * Math.Cos(angle)),
                            new Size(9, 9), 0, fraction > .5, SweepDirection.Clockwise, true, false);
                    }
                    arc.Freeze(); drawing.DrawGeometry(null, pen, arc);
                }
                if (State == TimerState.Paused)
                {
                    drawing.DrawLine(pen, new Point(9, 7), new Point(9, 15));
                    drawing.DrawLine(pen, new Point(13, 7), new Point(13, 15));
                }
            }
        }
    }
}
