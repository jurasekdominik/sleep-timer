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
        [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int count);

        public static uint ExplorerProcess()
        {
            GetWindowThreadProcessId(FindWindow("Shell_TrayWnd", null), out uint process);
            return process;
        }

        public static bool IsTaskbarWindow(IntPtr window)
        {
            var bar = FindWindow("Shell_TrayWnd", null);
            return bar != IntPtr.Zero && (window == bar || IsChild(bar, window));
        }

        // Cheap recovery check for missed shell events; no accessibility traversal.
        public static string LayoutStamp()
        {
            var bar = FindWindow("Shell_TrayWnd", null);
            if (bar == IntPtr.Zero || !GetWindowRect(bar, out var bounds)) return "";
            var start = FindWindowEx(bar, IntPtr.Zero, "Start", null);
            GetWindowRect(start, out var startBounds);
            // Modern Start is an accessibility element. The native task-list geometry
            // also changes as centered buttons move, without querying accessibility here.
            var taskList = FindWindowEx(bar, IntPtr.Zero, "ReBarWindow32", null);
            GetWindowRect(taskList, out var taskListBounds);
            return bar + ":" + bounds.Value + ":" + startBounds.Value + ":" + taskListBounds.Value + ":" + GetDpiForWindow(bar);
        }

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
                    // Identify Start independently of the Windows display language.
                    if (current.AutomationId == "StartButton" || current.ClassName == "Start") anchor = bounds;
                }
                if (!anchor.HasValue)
                {
                    var start = FindWindowEx(bar, IntPtr.Zero, "Start", null);
                    if (start != IntPtr.Zero && IsWindowVisible(start) && GetWindowRect(start, out var startRect)
                        && startRect.Value.Width > 0 && startRect.Value.Height > 0)
                        anchor = startRect.Value;
                }
                if (!anchor.HasValue) return null;
                double scale = Math.Max(96, GetDpiForWindow(bar)) / 96d;
                return TaskbarPlacement.LeftOfStart(rect.Value, info.Bounds.Value, anchor.Value,
                    (int)Math.Ceiling(logicalWidth * scale), (int)Math.Ceiling(logicalHeight * scale),
                    (int)Math.Ceiling(4 * scale), occupied);
            }
            catch (ElementNotAvailableException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (COMException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }

        // A native child inherits its parent's visibility and Z order, including Start/fullscreen.
        public static bool CanEmbed(Rectangle target)
        {
            var bar = FindWindow("Shell_TrayWnd", null);
            if (bar == IntPtr.Zero || !IsWindowVisible(bar) || !GetWindowRect(bar, out var bounds)
                || !bounds.Value.Contains(target)) return false;
            if (DwmGetWindowAttribute(bar, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(MonitorFromWindow(bar, 2), ref info) || !info.Bounds.Value.Contains(target)) return false;
            return true;
        }

        // No accessibility calls here: foreground and auto-hide changes must not wait for Explorer.
        public static bool CanShow(Rectangle target)
        {
            if (!CanEmbed(target)) return false;
            var bar = FindWindow("Shell_TrayWnd", null);
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == bar) return true;
            GetWindowThreadProcessId(foreground, out uint process);
            if (process == GetCurrentProcessId()) return true; // Our context menu may overlap the timer.
            var name = new StringBuilder(256);
            GetClassName(foreground, name, name.Capacity);
            if (name.ToString() == "Progman" || name.ToString() == "WorkerW") return true;
            // Test the actual timer area, rather than requiring an exact full-monitor window size.
            // This includes borderless games with small frame/DPI differences.
            if (DwmGetWindowAttribute(foreground, 9, out Rect front, Marshal.SizeOf(typeof(Rect))) != 0
                && !GetWindowRect(foreground, out front)) return true;
            return !front.Value.Contains(target);
        }

        static Rectangle Pixels(System.Windows.Rect value) => Rectangle.FromLTRB((int)Math.Floor(value.Left),
            (int)Math.Floor(value.Top), (int)Math.Ceiling(value.Right), (int)Math.Ceiling(value.Bottom));
    }
}
