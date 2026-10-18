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
        readonly DispatcherTimer leftClick = new DispatcherTimer(DispatcherPriority.Input);
        readonly Func<int, int, PixelRectangle?> locate;
        readonly Func<PixelRectangle, bool> canShow;
        readonly Func<PixelRectangle, bool> canEmbed;
        readonly bool preferNative;
        readonly IntPtr parentOverride;
        readonly Action toggle, cancel, menu, open;
        readonly Action<int, ModifierKeys> scroll;
        NativeTaskbarHost native;
        bool nativeAttempted, nativeFailed;
        IntPtr nativeParent;
        uint nativeParentProcess;
        public string RendererDescription => native?.Ready == true ? "Native" : preferNative ? "WPF fallback" : "WPF";
        readonly Stopwatch probeAge = new Stopwatch();
        readonly Stopwatch settleAge = new Stopwatch();
        Task<PixelRectangle?> probe;
        HwndSource source;
        readonly WinEventProc foregroundChanged;
        IntPtr foregroundHook;
        IntPtr taskbarHook;
        uint explorerProcess;
        string layoutStamp;
        bool layoutDirty = true;
        bool disposed;
        bool foregroundRefreshQueued;
        bool suppressLeftRelease;
        string viewKey;
        PixelRectangle? previous;
        PixelRectangle? pending;
        bool waitingForLayout = true;
        static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string name, string title);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
        delegate void WinEventProc(IntPtr hook, uint message, IntPtr window, int objectId, int childId, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module,
            WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);

        public TaskbarTimer(Action toggle, Action cancel, Action menu, Action<int, ModifierKeys> scroll,
            Func<int, int, PixelRectangle?> locate = null,
            Func<PixelRectangle, bool> canShow = null, Action open = null,
            bool preferNative = false, IntPtr nativeParent = default)
        {
            this.locate = locate ?? TaskbarLocator.Locate;
            this.canShow = canShow ?? TaskbarLocator.CanShow;
            canEmbed = nativeParent != IntPtr.Zero ? canShow ?? (bounds => true) : TaskbarLocator.CanEmbed;
            this.preferNative = preferNative; parentOverride = nativeParent;
            this.toggle = toggle; this.cancel = cancel; this.menu = menu; this.open = open; this.scroll = scroll;
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
            leftClick.Tick += (s, e) => { leftClick.Stop(); if (!disposed) toggle(); };
            button.MouseLeftButtonDown += (s, e) =>
            {
                suppressLeftRelease = e.ClickCount > 1;
                if (suppressLeftRelease) { leftClick.Stop(); open?.Invoke(); }
                e.Handled = true;
            };
            button.MouseLeftButtonUp += (s, e) =>
            {
                if (!suppressLeftRelease)
                {
                    leftClick.Stop();
                    leftClick.Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime);
                    leftClick.Start();
                }
                suppressLeftRelease = false; e.Handled = true;
            };
            button.MouseRightButtonUp += (s, e) => { leftClick.Stop(); cancel(); e.Handled = true; };
            button.MouseUp += (s, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle) return;
                leftClick.Stop();
                Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() => { if (!disposed) menu(); }));
                e.Handled = true;
            };
            button.MouseWheel += (s, e) => { leftClick.Stop(); scroll(e.Delta, Keyboard.Modifiers); e.Handled = true; };
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
                // Native children follow parent layering without foreground scans or window-order repairs.
                if (message == 3 && native?.Ready == true) return;
                if (message == 3 || TaskbarLocator.IsTaskbarWindow(window)) QueueForegroundRefresh();
            };
            // Follow both taskbar activation and games becoming foreground, without taking focus.
            foregroundHook = SetWinEventHook(3, 3, IntPtr.Zero, foregroundChanged, 0, 0, 2);
            Closing += (s, e) => Stop();
            Closed += (s, e) => Stop();
            placement.Tick += (s, e) =>
            {
                WatchTaskbar();
                string stamp = TaskbarLocator.LayoutStamp();
                if (layoutStamp != stamp) { layoutStamp = stamp; layoutDirty = true; probeAge.Reset(); }
                RefreshPlacement();
            };
            new WindowInteropHelper(this).EnsureHandle();
            WatchTaskbar(); RefreshPlacement();
        }

        void WatchTaskbar()
        {
            uint process = TaskbarLocator.ExplorerProcess();
            if (process == explorerProcess && (process == 0 || taskbarHook != IntPtr.Zero)) return;
            if (parentOverride == IntPtr.Zero && process != explorerProcess)
            {
                native?.Dispose(); native = null;
                nativeAttempted = nativeFailed = false;
            }
            if (taskbarHook != IntPtr.Zero) UnhookWinEvent(taskbarHook);
            explorerProcess = process;
            // Filter in Windows so activity in other applications does not wake this widget.
            taskbarHook = process == 0 ? IntPtr.Zero : SetWinEventHook(0x8000, 0x800B, IntPtr.Zero, foregroundChanged, process, 0, 2);
        }

        void QueueForegroundRefresh()
        {
            if (disposed || Dispatcher.HasShutdownStarted || foregroundRefreshQueued) return;
            foregroundRefreshQueued = true;
            try { Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)RefreshAfterForegroundChange); }
            catch (InvalidOperationException) { foregroundRefreshQueued = false; } // Dispatcher stopped during the native callback.
        }

        void RefreshAfterForegroundChange()
        {
            foregroundRefreshQueued = false;
            if (disposed) return;
            WatchTaskbar(); layoutDirty = true; probeAge.Reset();
            RefreshPlacement();
        }

        IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (native?.HandleMessage(message, wParam, lParam) == true) { handled = true; return IntPtr.Zero; }
            if (message == 0x0021) { handled = true; return (IntPtr)3; } // MA_NOACTIVATE
            if (message == TaskbarCreated || message == 0x007E || message == 0x031E) QueueForegroundRefresh();
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
                    if (probeAge.ElapsedMilliseconds > 1500)
                    { previous = pending = null; waitingForLayout = true; HideSurfaces(); placement.Interval = TimeSpan.FromSeconds(30); }
                    return;
                }
                var bounds = probe.Status == TaskStatus.RanToCompletion ? probe.Result : null;
                if (probe.IsFaulted) { var ignored = probe.Exception; }
                probe = null;
                layoutDirty = false;
                if (!bounds.HasValue) { previous = pending = null; waitingForLayout = true; HideSurfaces(); }
                else if (!CanPresent(bounds.Value))
                {
                    previous = bounds; pending = null; waitingForLayout = true; HideSurfaces();
                }
                else if (waitingForLayout || previous != bounds)
                {
                    if (pending != bounds)
                    {
                        waitingForLayout = true; pending = bounds; HideSurfaces(); settleAge.Restart();
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
            bool refresh = layoutDirty || pending.HasValue;
            placement.Interval = refresh ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(30);
            placement.Start();
            if (!refresh || (probeAge.IsRunning && probeAge.ElapsedMilliseconds < 200)) return;
            int width = (int)Width, height = (int)Height;
            probeAge.Restart(); probe = Task.Run(() => locate(width, height));
        }

        void RefreshTaskbarLayer()
        {
            var candidate = pending ?? previous;
            if (disposed || !candidate.HasValue) return;
            if (!CanPresent(candidate.Value)) { waitingForLayout = true; pending = null; HideSurfaces(); return; }
            if (waitingForLayout) return;
            EnsureNative();
            // Do not warm up WPF's widget renderer for a healthy native launch.
            // Startup is bounded; failures return here and show the compatibility surface.
            if (native?.Starting == true) { if (IsVisible) Hide(); return; }
            if (native?.Ready == true)
            {
                if (IsVisible) Hide();
                if (SyncNative(canEmbed(candidate.Value))) return;
            }
            if (!canShow(candidate.Value)) { if (IsVisible) Hide(); return; }
            if (!IsVisible)
            {
                Show();
                var at = previous.Value;
                SetWindowPos(source.Handle, IntPtr.Zero, at.X, at.Y, at.Width, at.Height, 0x0214);
            }
            FollowTaskbarLayer(FindWindow("Shell_TrayWnd", null));
        }

        bool CanPresent(PixelRectangle bounds) => (preferNative && !nativeFailed && canEmbed(bounds)) || canShow(bounds);
        void EnsureNative()
        {
            if (!preferNative || disposed) return;
            var parent = parentOverride != IntPtr.Zero ? parentOverride : FindWindow("Shell_TrayWnd", null);
            NativeTaskbarHost.ParentProcess(parent, out uint process);
            if (parent == IntPtr.Zero || (nativeAttempted && nativeParent == parent && nativeParentProcess == process)) return;
            native?.Dispose(); nativeParent = parent; nativeParentProcess = process;
            nativeAttempted = true; nativeFailed = false;
            NativeTaskbarHost created = null;
            created = new NativeTaskbarHost(source.Handle, parent, () =>
            {
                // A previous helper can finish exiting after Explorer or the renderer is replaced.
                if (disposed || native != created) return;
                nativeFailed = created.Failure != null;
                RefreshTaskbarLayer();
            }, toggle, cancel, menu, open, scroll);
            native = created;
            if (!native.Start()) { nativeFailed = true; native.Dispose(); }
        }
        bool SyncNative(bool visible)
        {
            if (native?.Ready != true) return false;
            var at = previous ?? new PixelRectangle(0, 0, 140, 34);
            var view = new NativeTaskbarHost.View {
                X = at.X, Y = at.Y, Width = at.Width, Height = at.Height, Visible = visible ? 1u : 0u,
                State = (uint)ring.State, Time = time.Text, Action = action.Text,
                Progress = (uint)Math.Round(Math.Max(0, Math.Min(1, ring.Fraction)) * 1000000),
                Accent = Rgb(Theme.TimerColor(ring.Tone)), Text = Rgb(Theme.Text), Secondary = Rgb(Theme.Second), Track = Rgb(Theme.Border)
            };
            return native.Update(view);
        }
        static uint Rgb(Color color) => (uint)(color.R << 16 | color.G << 8 | color.B);
        void HideSurfaces()
        {
            if (IsVisible) Hide();
            SyncNative(false);
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
                + "\nClick to start or pause · Double-click to open · Right-click to cancel · Scroll to adjust · Middle-click for menu";
            ring.Fraction = fraction; ring.State = state; ring.Tone = tone; ring.InvalidateVisual();
            if (native?.Ready == true) SyncNative(!waitingForLayout && previous.HasValue && canEmbed(previous.Value));
        }

        void Stop()
        {
            if (disposed) return;
            disposed = true; leftClick.Stop(); placement.Stop(); source?.RemoveHook(WindowMessage);
            native?.Dispose(); native = null;
            if (foregroundHook != IntPtr.Zero) { UnhookWinEvent(foregroundHook); foregroundHook = IntPtr.Zero; }
            if (taskbarHook != IntPtr.Zero) { UnhookWinEvent(taskbarHook); taskbarHook = IntPtr.Zero; }
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop(); Close();
        }

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
