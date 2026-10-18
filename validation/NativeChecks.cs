using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Interop;

namespace SleepTimer
{
    static class NativeChecks
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int extended,
            string name, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect bounds);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Native.ScreenPoint point);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Fields).GetValue(instance);
        static void Check(bool okay, string message) { if (!okay) throw new Exception(message); }
        static void Until(Func<bool> ready, Action<int> pump, string message, int timeout = 5000)
        {
            var elapsed = Stopwatch.StartNew();
            while (!ready() && elapsed.ElapsedMilliseconds < timeout) pump(25);
            Check(ready(), message);
        }
        static IntPtr Parent() => CreateWindowEx(0, "STATIC", "Native taskbar test surface", 0x92000004,
            64, 64, 420, 80, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        static bool Gone(int id)
        {
            try { using (var process = Process.GetProcessById(id)) return process.HasExited; }
            catch (ArgumentException) { return true; }
        }

        internal static int OwnerExitProbe(string output, IntPtr parent, Action<int> pump)
        {
            var owner = new HwndSource(new HwndSourceParameters("Native owner exit probe") { WindowStyle = 0 });
            var host = new NativeTaskbarHost(owner.Handle, parent, () => { }, () => { }, () => { }, () => { }, () => { }, (delta, keys) => { });
            owner.AddHook((IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            { handled = host.HandleMessage(message, wParam, lParam); return IntPtr.Zero; });
            Check(host.Start(), "Owner-exit probe could not start the helper");
            Until(() => host.Ready, pump, "Owner-exit helper did not become ready");
            File.WriteAllText(output, Field<Process>(host, "process").Id.ToString());
            // Intentionally exit without Dispose: the helper must notice the owner process ending.
            GC.KeepAlive(host); GC.KeepAlive(owner);
            return 0;
        }

        internal static void Run(Action<int> pump)
        {
            Check(Marshal.SizeOf(typeof(NativeTaskbarHost.View)) == 312, "Managed/native packet layout mismatch");
            if (NativeTaskbarHost.ExecutablePath() == null)
            {
                Check(Environment.GetEnvironmentVariable("SLEEP_TIMER_NATIVE_REQUIRED") != "1", "Required native component was not embedded");
                Console.WriteLine("SKIP: native host absent in SDK-only build"); return;
            }
            Console.WriteLine("CHECK: native child rendering, input and fallback");
            var parent = Parent(); Check(parent != IntPtr.Zero, "Native taskbar stand-in could not be created");
            int toggled = 0, cancelled = 0, opened = 0, menus = 0, wheel = 0;
            ModifierKeys modifiers = ModifierKeys.None;
            var target = new Rectangle(80, 80, 140, 34);
            try
            {
                using (var widget = new TaskbarTimer(() => toggled++, () => cancelled++, () => menus++,
                    (delta, keys) => { wheel += delta; modifiers = keys; }, (width, height) => target, bounds => true,
                    () => opened++, preferNative: true, nativeParent: parent))
                {
                    widget.Update("2:25:00", "Shut down", TimerState.Running, .5, TimerTone.Running);
                    Until(() => widget.RendererDescription == "Native", pump, "Native renderer never attached");
                    var host = Field<NativeTaskbarHost>(widget, "native");
                    var child = Field<IntPtr>(host, "window");
                    Check(GetParent(child) == parent && IsWindowVisible(child) && !widget.IsVisible,
                        "Native widget must be a visible taskbar child while its WPF surface stays hidden");
                    Check(GetWindowRect(child, out var at) && at.Left == target.Left && at.Top == target.Top
                        && at.Right - at.Left == target.Width && at.Bottom - at.Top == target.Height, "Native physical placement changed");
                    var hit = Task.Run(() => WindowFromPoint(new Native.ScreenPoint { X = target.X + 2, Y = target.Y + 2 }));
                    Until(() => hit.IsCompleted, pump, "Native hit test timed out");
                    Check(hit.Result == child, "Native blank padding is click-through");
                    Directory.CreateDirectory("validation-output");
                    using (var image = new Bitmap(target.Width, target.Height))
                    {
                        using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(target.Location, Point.Empty, target.Size);
                        image.Save("validation-output/native-taskbar.png", ImageFormat.Png);
                    }
                    ShowWindow(parent, 0); Check(!IsWindowVisible(child), "Native child did not hide with its parent");
                    ShowWindow(parent, 4); Check(IsWindowVisible(child), "Native child did not return with its parent");

                    var foreground = GetForegroundWindow();
                    SendMessage(child, 0x0202, IntPtr.Zero, IntPtr.Zero); // WM_LBUTTONUP
                    pump(System.Windows.Forms.SystemInformation.DoubleClickTime + 80);
                    Check(toggled == 1 && GetForegroundWindow() == foreground, "Native single-click toggled incorrectly or took focus");
                    SendMessage(child, 0x0202, IntPtr.Zero, IntPtr.Zero);
                    SendMessage(child, 0x0203, IntPtr.Zero, IntPtr.Zero); // WM_LBUTTONDBLCLK
                    SendMessage(child, 0x0202, IntPtr.Zero, IntPtr.Zero);
                    pump(System.Windows.Forms.SystemInformation.DoubleClickTime + 80);
                    Check(opened == 1 && toggled == 1, "Native double-click changed the countdown");
                    SendMessage(child, 0x0202, IntPtr.Zero, IntPtr.Zero);
                    SendMessage(child, 0x0205, IntPtr.Zero, IntPtr.Zero); // WM_RBUTTONUP
                    pump(System.Windows.Forms.SystemInformation.DoubleClickTime + 80);
                    Check(cancelled == 1 && toggled == 1, "Native cancel did not clear a pending single-click");
                    SendMessage(child, 0x0208, IntPtr.Zero, IntPtr.Zero); pump(50);
                    Check(menus == 1, "Native middle-click did not open the menu");
                    SendMessage(child, 0x020A, new IntPtr(120 << 16), IntPtr.Zero); pump(50);
                    Check(wheel == 120, "Native wheel delta was lost");
                    // Exercise modifier decoding without altering the desktop's real keyboard state.
                    SendMessage(new WindowInteropHelper(widget).Handle, NativeTaskbarHost.InputMessage, child,
                        new IntPtr(5 | (4 << 8) | (120 << 16))); pump(50);
                    Check(wheel == 240 && modifiers == ModifierKeys.Shift, "Native modifier routing changed");

                    Console.WriteLine("CHECK: stale native failure during helper replacement");
                    host.GetType().GetMethod("Fail", Fields).Invoke(host, new object[] { "Replacement race probe" });
                    widget.GetType().GetField("native", Fields).SetValue(widget, null); // Explorer temporarily has no taskbar.
                    pump(50); // A queued callback from the removed helper must not dereference the cleared field.
                    widget.GetType().GetField("nativeAttempted", Fields).SetValue(widget, false);
                    widget.GetType().GetMethod("EnsureNative", Fields).Invoke(widget, null);
                    Until(() => widget.RendererDescription == "Native", pump, "Replacement helper was changed by an old exit callback");
                    host = Field<NativeTaskbarHost>(widget, "native");
                    Check(!Field<bool>(widget, "nativeFailed"), "Old helper failure marked the replacement as failed");

                    int id = Field<Process>(host, "process").Id;
                    using (var process = Process.GetProcessById(id)) process.Kill();
                    Until(() => widget.RendererDescription == "WPF fallback" && widget.IsVisible, pump,
                        "A native helper failure did not restore WPF");
                    pump(300); Check(Field<bool>(widget, "nativeFailed"), "Failed native helper was automatically respawned");
                }
                Console.WriteLine("CHECK: native taskbar parent-loss cleanup");
                using (var widget = new TaskbarTimer(() => { }, () => { }, () => { }, (delta, keys) => { },
                    (width, height) => target, bounds => true, preferNative: true, nativeParent: parent))
                {
                    widget.Update("1:00:00", "Sleep", TimerState.Idle, 1, TimerTone.Idle);
                    Until(() => widget.RendererDescription == "Native", pump, "Second native renderer never attached");
                    int id = Field<Process>(Field<NativeTaskbarHost>(widget, "native"), "process").Id;
                    DestroyWindow(parent); parent = IntPtr.Zero;
                    Until(() => Gone(id), pump, "Native helper survived destruction of its taskbar parent");
                }
            }
            finally { if (parent != IntPtr.Zero) DestroyWindow(parent); }

            Console.WriteLine("CHECK: native helper owner-loss cleanup");
            string report = Path.Combine(Path.GetTempPath(), "sleep-timer-owner-" + Guid.NewGuid().ToString("N") + ".txt");
            var survivingParent = Parent();
            try
            {
                using (var probe = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                    "--native-owner-test \"" + report + "\" " + survivingParent.ToInt64().ToString("X")) { UseShellExecute = false, CreateNoWindow = true }))
                {
                    try { Until(() => probe.HasExited, pump, "Native owner probe did not exit", 10000); }
                    finally { if (!probe.HasExited) probe.Kill(); }
                    Check(probe.ExitCode == 0 && File.Exists(report), "Native owner probe failed");
                }
                int child = int.Parse(File.ReadAllText(report));
                Until(() => Gone(child), pump, "Native helper survived its owner process");
            }
            finally { if (File.Exists(report)) File.Delete(report); DestroyWindow(survivingParent); }
        }
    }
}
