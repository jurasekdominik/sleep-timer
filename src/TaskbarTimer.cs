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
        readonly TextBlock time = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 12, FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock action = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 11, FontWeight = FontWeights.Normal,
            TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly MiniRing ring = new MiniRing { Width = 28, Height = 28, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly DispatcherTimer placement = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        readonly Func<int, int, PixelRectangle?> locate;
        readonly Func<PixelRectangle, bool> canShow;
        readonly Stopwatch probeAge = new Stopwatch();
        readonly Stopwatch settleAge = new Stopwatch();
        Task<PixelRectangle?> probe;
        HwndSource source;
        readonly WinEventProc foregroundChanged;
        IntPtr foregroundHook;
        bool disposed;
        bool foregroundRefreshQueued;
        string viewKey;
        PixelRectangle? previous;
        PixelRectangle? pending;
        bool waitingForLayout = true;
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string name, string title);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        delegate void WinEventProc(IntPtr hook, uint message, IntPtr window, int objectId, int childId, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module,
            WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

        public TaskbarTimer(Action toggle, Action cancel, Action menu, Action<int, ModifierKeys> scroll,
            Func<int, int, PixelRectangle?> locate = null,
            Func<PixelRectangle, bool> canShow = null)
        {
            this.locate = locate ?? TaskbarLocator.Locate;
            this.canShow = canShow ?? TaskbarLocator.CanShow;
            Title = "Sleep Timer · taskbar timer"; Width = 140; Height = 34;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = ShowActivated = false; AllowsTransparency = true; Background = Brushes.Transparent;
            UseLayoutRounding = SnapsToDevicePixels = true;
            FontFamily = new FontFamily("Segoe UI"); FontWeight = FontWeights.Normal;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(time); text.Children.Add(action);
            var content = new Grid(); content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); content.Children.Add(text);
            Grid.SetColumn(ring, 1); content.Children.Add(ring);
            // Alpha 1 looks transparent while preserving native hit testing over the entire rectangle.
            button = new Border { Child = content, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                Padding = new Thickness(5, 0, 5, 0), Cursor = Cursors.Hand };
            time.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            action.SetResourceReference(TextBlock.ForegroundProperty, "SecondBrush");
            Content = button;
            button.MouseLeftButtonUp += (s, e) => { toggle(); e.Handled = true; };
            button.MouseRightButtonUp += (s, e) => { cancel(); e.Handled = true; };
            button.MouseUp += (s, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() => { if (!disposed) menu(); }));
                e.Handled = true;
            };
            button.MouseWheel += (s, e) => { scroll(e.Delta, Keyboard.Modifiers); e.Handled = true; };
            SourceInitialized += (s, e) =>
            {
                source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                // This tiny transparent surface does not need a GPU-backed render target.
                // Keep its composition independent of device changes during screen capture.
                source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
                // Keep the user's current app focused, including when clicking the timer.
                SetWindowLong(source.Handle, -20, (GetWindowLong(source.Handle, -20) | 0x08000080) & ~0x00040000);
                source.AddHook(WindowMessage);
            };
            foregroundChanged = (hook, message, window, objectId, childId, thread, stamp) =>
            {
                if (disposed || Dispatcher.HasShutdownStarted || foregroundRefreshQueued) return;
                foregroundRefreshQueued = true;
                try { Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() =>
                {
                    foregroundRefreshQueued = false;
                    if (disposed) return;
                    RefreshTaskbarLayer();
                    var at = pending ?? previous;
                    if (waitingForLayout && probe == null && at.HasValue && this.canShow(at.Value)) probeAge.Reset();
                    RefreshPlacement();
                })); }
                catch (InvalidOperationException) { foregroundRefreshQueued = false; } // Dispatcher stopped during the native callback.
            };
            // Follow both taskbar activation and games becoming foreground, without taking focus.
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
            RefreshTaskbarLayer();
            if (probe != null)
            {
                if (!probe.IsCompleted)
                {
                    // Never accumulate stuck accessibility calls, or keep stale coordinates visible.
                    if (probeAge.ElapsedMilliseconds > 1500) { Hide(); previous = pending = null; waitingForLayout = true; }
                    return;
                }
                var bounds = probe.Status == TaskStatus.RanToCompletion ? probe.Result : null;
                if (probe.IsFaulted) { var ignored = probe.Exception; }
                probe = null;
                if (!bounds.HasValue) { Hide(); previous = pending = null; waitingForLayout = true; }
                else if (!canShow(bounds.Value))
                {
                    Hide(); previous = bounds; pending = null; waitingForLayout = true;
                }
                else if (waitingForLayout || previous != bounds)
                {
                    if (pending != bounds)
                    {
                        Hide(); waitingForLayout = true; pending = bounds; settleAge.Restart();
                    }
                    else if (settleAge.ElapsedMilliseconds >= 300)
                    {
                        var at = bounds.Value;
                        SetWindowPos(source.Handle, IntPtr.Zero, at.X, at.Y, at.Width, at.Height, 0x0214);
                        previous = at; pending = null; waitingForLayout = false;
                    }
                }
                RefreshTaskbarLayer();
            }
            if (probeAge.IsRunning && probeAge.ElapsedMilliseconds < (pending.HasValue ? 200 : 1000)) return;
            int width = (int)Width, height = (int)Height;
            probeAge.Restart(); probe = Task.Run(() => locate(width, height));
        }

        void RefreshTaskbarLayer()
        {
            var candidate = pending ?? previous;
            if (disposed || !candidate.HasValue) return;
            if (!canShow(candidate.Value)) { Hide(); waitingForLayout = true; pending = null; return; }
            if (waitingForLayout) return;
            if (!IsVisible)
            {
                Show();
                var at = previous.Value;
                SetWindowPos(source.Handle, IntPtr.Zero, at.X, at.Y, at.Width, at.Height, 0x0214);
            }
            FollowTaskbarLayer(FindWindow("Shell_TrayWnd", null));
        }

        void FollowTaskbarLayer(IntPtr taskbar)
        {
            if (disposed || !IsVisible || taskbar == IntPtr.Zero) return;
            var above = GetWindow(taskbar, 3); // Window immediately above Explorer in the native Z order.
            if (above == source.Handle) return;
            // Insert between the taskbar and whatever covers it; never jump above that covering app.
            SetWindowPos(source.Handle, above == IntPtr.Zero ? (IntPtr)(-1) : above, 0, 0, 0, 0, 0x0213);
        }

        public void Update(string value, string actionLabel, TimerState state, double fraction, TimerTone tone)
        {
            string key = value + "|" + actionLabel + "|" + state + "|" + Math.Round(fraction * 100) + "|" + tone + "|" + Theme.Accent;
            if (viewKey == key) return; viewKey = key;
            time.Text = value;
            time.SetResourceReference(TextBlock.ForegroundProperty, state == TimerState.Idle ? "SecondBrush" : "TextBrush");
            action.Text = actionLabel + (state == TimerState.Idle ? " · Ready" : state == TimerState.Paused ? " · Paused" : "");
            button.ToolTip = value + " · " + actionLabel + " · " + (state == TimerState.Idle ? "Ready" : state.ToString())
                + "\nClick to start or pause · Right-click to cancel · Scroll to adjust · Middle-click for menu";
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
                var center = new Point(14, 14);
                drawing.DrawEllipse(null, new Pen(Theme.Brush(Theme.Border), 3), center, 11.5, 11.5);
                var pen = new Pen(Theme.Brush(Theme.TimerColor(Tone)), 3);
                double fraction = Math.Max(0, Math.Min(1, Fraction));
                if (fraction >= .999) drawing.DrawEllipse(null, pen, center, 11.5, 11.5);
                else if (fraction > 0)
                {
                    double angle = fraction * 2 * Math.PI;
                    var arc = new StreamGeometry();
                    using (var path = arc.Open())
                    {
                        path.BeginFigure(new Point(14, 2.5), false, false);
                        path.ArcTo(new Point(14 + 11.5 * Math.Sin(angle), 14 - 11.5 * Math.Cos(angle)),
                            new Size(11.5, 11.5), 0, fraction > .5, SweepDirection.Clockwise, true, false);
                    }
                    arc.Freeze(); drawing.DrawGeometry(null, pen, arc);
                }
                if (State == TimerState.Paused)
                {
                    drawing.DrawLine(pen, new Point(11.5, 9), new Point(11.5, 19));
                    drawing.DrawLine(pen, new Point(16.5, 9), new Point(16.5, 19));
                }
            }
        }
    }
}
