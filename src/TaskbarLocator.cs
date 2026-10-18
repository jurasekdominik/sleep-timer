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
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
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

        // No accessibility calls here: foreground and auto-hide changes must not wait for Explorer.
        public static bool CanShow(Rectangle target)
        {
            var bar = FindWindow("Shell_TrayWnd", null);
            if (bar == IntPtr.Zero || !IsWindowVisible(bar) || !GetWindowRect(bar, out var bounds)
                || !bounds.Value.Contains(target)) return false;
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(MonitorFromWindow(bar, 2), ref info) || !info.Bounds.Value.Contains(target)) return false;
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == bar) return true;
            GetWindowThreadProcessId(foreground, out uint process);
            if (process == GetCurrentProcessId()) return true; // Our context menu may overlap the timer.
            var name = new StringBuilder(256);
            GetClassName(foreground, name, name.Capacity);
            if (name.ToString() == "Progman" || name.ToString() == "WorkerW") return true;
            // Test the actual timer area, rather than requiring an exact full-monitor window size.
            // This includes borderless games with small frame/DPI differences.
            if (DwmGetWindowAttribute(foreground, 9, out var front, Marshal.SizeOf(typeof(Rect))) != 0
                && !GetWindowRect(foreground, out front)) return true;
            return !front.Value.Contains(target);
        }

        static Rectangle Pixels(System.Windows.Rect value) => Rectangle.FromLTRB((int)Math.Floor(value.Left),
            (int)Math.Floor(value.Top), (int)Math.Ceiling(value.Right), (int)Math.Ceiling(value.Bottom));
    }
}
