using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace SleepTimer
{
    static class TaskbarLocator
    {
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom;
            public Rectangle Value => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Bounds, Work; public uint Flags; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string name, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string title);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int count);

        // Called on a worker thread: Explorer's accessibility provider can be slow or unavailable.
        public static Rectangle? Locate(int logicalWidth, int logicalHeight)
        {
            try
            {
                var bar = FindWindow("Shell_TrayWnd", null);
                if (bar == IntPtr.Zero || !IsWindowVisible(bar) || !GetWindowRect(bar, out var rect)) return null;
                var monitor = MonitorFromWindow(bar, 2);
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (!GetMonitorInfo(monitor, ref info)) return null;
                var foreground = GetForegroundWindow();
                var foregroundClass = new StringBuilder(256);
                GetClassName(foreground, foregroundClass, foregroundClass.Capacity);
                if (foreground != IntPtr.Zero && foreground != bar
                    && foregroundClass.ToString() != "Progman" && foregroundClass.ToString() != "WorkerW"
                    && MonitorFromWindow(foreground, 2) == monitor && GetWindowRect(foreground, out var front)
                    && front.Value.Contains(info.Bounds.Value)) return null;

                var root = AutomationElement.FromHandle(bar);
                var buttons = root.FindAll(TreeScope.Descendants,
                    new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)));
                var occupied = new List<Rectangle>();
                Rectangle? anchor = null;
                foreach (AutomationElement button in buttons)
                {
                    var current = button.Current;
                    if (current.IsOffscreen || current.BoundingRectangle.IsEmpty) continue;
                    var bounds = Pixels(current.BoundingRectangle);
                    occupied.Add(bounds);
                    // Locale-independent Windows 11 overflow-button identifier.
                    if (current.AutomationId == "SystemTray.OverflowButton") anchor = bounds;
                }
                if (!anchor.HasValue)
                {
                    // Windows 10 and Windows 11 builds retaining the native notification-area window.
                    var tray = FindWindowEx(bar, IntPtr.Zero, "TrayNotifyWnd", null);
                    if (tray != IntPtr.Zero && GetWindowRect(tray, out var trayRect)) anchor = trayRect.Value;
                    else
                    {
                        var frame = root.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.SystemTrayFrame"));
                        if (frame != null && !frame.Current.IsOffscreen && !frame.Current.BoundingRectangle.IsEmpty)
                            anchor = Pixels(frame.Current.BoundingRectangle);
                    }
                }
                if (!anchor.HasValue) return null;
                double scale = Math.Max(96, GetDpiForWindow(bar)) / 96d;
                return TaskbarPlacement.LeftOfTray(rect.Value, info.Bounds.Value, anchor.Value,
                    (int)Math.Ceiling(logicalWidth * scale), (int)Math.Ceiling(logicalHeight * scale),
                    (int)Math.Ceiling(4 * scale), occupied);
            }
            catch (ElementNotAvailableException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (COMException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }

        static Rectangle Pixels(System.Windows.Rect value) => Rectangle.FromLTRB((int)Math.Floor(value.Left),
            (int)Math.Floor(value.Top), (int)Math.Ceiling(value.Right), (int)Math.Ceiling(value.Bottom));
    }
}
