using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace SleepTimer
{
    // Runs the real app window and dispatcher. Only reflection and sampling belong to this harness.
    static class PerformanceChecks
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static string placementMode = "none";
        static bool nativeRequested;
        static IntPtr controlledParent;
        [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int flags);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int extended,
            string name, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
        static T Field<T>(object owner, string name)
        {
            if (owner == null) return default(T);
            var field = owner.GetType().GetField(name, Fields);
            return field == null ? default(T) : (T)field.GetValue(owner);
        }
        static void Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Fields).Invoke(owner, args);
        static double MiB(long bytes) => bytes / 1048576d;
        static string Renderer(MainWindow window)
        {
            var widget = Field<Window>(window, "taskbarTimer");
            return widget?.GetType().GetProperty("RendererDescription")?.GetValue(widget) as string ?? "WPF";
        }
        static Process Helper(MainWindow window)
        {
            var native = Field<object>(Field<object>(window, "taskbarTimer"), "native");
            var owned = Field<Process>(native, "process");
            return owned == null ? null : Process.GetProcessById(owned.Id);
        }
        static bool WidgetVisible(MainWindow window)
        {
            var widget = Field<Window>(window, "taskbarTimer");
            if (widget?.IsVisible == true) return true;
            var native = Field<object>(widget, "native");
            var child = Field<IntPtr>(native, "window");
            return child != IntPtr.Zero && IsWindowVisible(child);
        }

        static async Task Measure(MainWindow window, string name, int seconds, bool baseline, string output)
        {
            await Task.Delay(5000); // Allow JIT, layout, placement and shell providers to settle.
            using (var process = Process.GetCurrentProcess())
            using (var helper = Helper(window))
            {
                var sampled = helper == null ? new[] { process } : new[] { process, helper };
                foreach (var item in sampled) item.Refresh();
                var cpu = sampled.Aggregate(TimeSpan.Zero, (sum, item) => sum + item.TotalProcessorTime);
                var helperCpu = helper?.TotalProcessorTime ?? TimeSpan.Zero;
                int gdi = sampled.Sum(item => GetGuiResources(item.Handle, 0)), handles = sampled.Sum(item => item.HandleCount);
                var working = new List<long>(); var memory = new List<long>(); var managed = new List<long>();
                var helperWorking = new List<long>();
                var elapsed = Stopwatch.StartNew();
                for (int i = 0; i < seconds; i++)
                {
                    await Task.Delay(1000);
                    foreach (var item in sampled) item.Refresh();
                    working.Add(sampled.Sum(item => item.WorkingSet64)); memory.Add(sampled.Sum(item => item.PrivateMemorySize64));
                    managed.Add(GC.GetTotalMemory(false)); helperWorking.Add(helper?.WorkingSet64 ?? 0);
                }
                double core = (sampled.Aggregate(TimeSpan.Zero, (sum, item) => sum + item.TotalProcessorTime) - cpu).TotalSeconds / elapsed.Elapsed.TotalSeconds * 100;
                double helperCore = ((helper?.TotalProcessorTime ?? TimeSpan.Zero) - helperCpu).TotalSeconds / elapsed.Elapsed.TotalSeconds * 100;
                double machine = core / Environment.ProcessorCount;
                string row = string.Join(",", name, elapsed.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture),
                    core.ToString("F3", CultureInfo.InvariantCulture), machine.ToString("F3", CultureInfo.InvariantCulture),
                    MiB(working.Min()).ToString("F2", CultureInfo.InvariantCulture), MiB(working.Max()).ToString("F2", CultureInfo.InvariantCulture),
                    MiB(memory.Min()).ToString("F2", CultureInfo.InvariantCulture), MiB(memory.Max()).ToString("F2", CultureInfo.InvariantCulture),
                    MiB(managed.Min()).ToString("F2", CultureInfo.InvariantCulture), MiB(managed.Max()).ToString("F2", CultureInfo.InvariantCulture),
                    gdi, sampled.Sum(item => GetGuiResources(item.Handle, 0)), handles, sampled.Sum(item => item.HandleCount), WidgetVisible(window), placementMode,
                    Renderer(window), helperCore.ToString("F3", CultureInfo.InvariantCulture), MiB(helperWorking.Max()).ToString("F2", CultureInfo.InvariantCulture));
                File.AppendAllText(output, row + Environment.NewLine);
                Console.WriteLine("PERF " + row);
                if (nativeRequested && name.Contains("widget") && (helper == null || Renderer(window) != "Native" || !WidgetVisible(window)))
                    throw new Exception("Native benchmark lost the visible native component");
                if (!baseline && name.Contains("idle") && core > 2)
                    throw new Exception("Unexpected idle CPU: " + core.ToString("F2") + "% of one core");
            }
        }

        static async Task EnableWidget(MainWindow window)
        {
            Call(window, "SetTaskbarTimer", true);
            await Task.Delay(2000);
            if (WidgetVisible(window) && (!nativeRequested || Renderer(window) == "Native")) { placementMode = "taskbar"; return; }
            // Hosted CI desktops may have no accessible tray slot. Measure the actual widget's rendering
            // at a controlled on-screen location, and explicitly distinguish this from native discovery.
            Call(window, "SetTaskbarTimer", false);
            await ControlledWidget(window);
        }

        static async Task ControlledWidget(MainWindow window)
        {
            Call(window, "SetTaskbarTimer", false);
            if (nativeRequested && controlledParent == IntPtr.Zero)
            {
                controlledParent = CreateWindowEx(0, "STATIC", "Native taskbar benchmark", 0x92000004,
                    0, 0, 320, 80, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (controlledParent == IntPtr.Zero) throw new Exception("Could not create native benchmark parent");
            }
            var widgetType = window.GetType().Assembly.GetType("SleepTimer.TaskbarTimer");
            object[] arguments = {
                new Action(() => { }), new Action(() => { }), new Action(() => { }),
                new Action<int, ModifierKeys>((delta, keys) => { }),
                new Func<int, int, System.Drawing.Rectangle?>((width, height) => new System.Drawing.Rectangle(16, 16, width, height)),
                new Func<System.Drawing.Rectangle, bool>(bounds => true), new Action(() => { }), nativeRequested, controlledParent
            };
            var constructor = widgetType.GetConstructors().Single();
            var widget = (Window)constructor.Invoke(arguments.Take(constructor.GetParameters().Length).ToArray());
            window.GetType().GetField("taskbarTimer", Fields).SetValue(window, widget);
            Call(window, "UpdateView", false);
            var wait = Stopwatch.StartNew();
            while ((!WidgetVisible(window) || (nativeRequested && Renderer(window) != "Native")) && wait.ElapsedMilliseconds < 5000)
                await Task.Delay(25);
            if (!WidgetVisible(window) || (nativeRequested && Renderer(window) != "Native")) throw new Exception("Controlled renderer did not become visible");
            placementMode = "controlled";
            Console.WriteLine("PERF Measuring " + Renderer(window) + " widget rendering with controlled placement.");
        }

        static async Task CheckCycles(MainWindow window, string output)
        {
            window.Hide(); await Task.Delay(5000);
            using (var process = Process.GetCurrentProcess())
            {
                long heap = GC.GetTotalMemory(true);
                process.Refresh();
                int handles = process.HandleCount, gdi = GetGuiResources(process.Handle, 0);
                int cycles = nativeRequested ? 12 : 30;
                var children = new List<int>();
                for (int i = 0; i < cycles; i++)
                {
                    Call(window, "SetTaskbarTimer", false); window.ShowWindow(); await Task.Delay(40);
                    window.Hide();
                    if (nativeRequested)
                    {
                        await ControlledWidget(window);
                        using (var child = Helper(window)) children.Add(child.Id);
                    }
                    else { Call(window, "SetTaskbarTimer", true); await Task.Delay(200); }
                }
                Call(window, "SetTaskbarTimer", false);
                await Task.Delay(8000);
                foreach (int id in children)
                {
                    try { using (var child = Process.GetProcessById(id)) if (!child.HasExited) throw new Exception("Native helper survived disposal"); }
                    catch (ArgumentException) { } // Windows has already removed the exited process.
                }
                long growth = GC.GetTotalMemory(true) - heap;
                process.Refresh();
                int handleGrowth = process.HandleCount - handles, gdiGrowth = GetGuiResources(process.Handle, 0) - gdi;
                string result = cycles + " " + (nativeRequested ? "native" : "WPF") + " widget/window cycles: retained managed delta " + MiB(growth).ToString("F2", CultureInfo.InvariantCulture)
                    + " MiB; handle delta " + handleGrowth + "; GDI delta " + gdiGrowth;
                Console.WriteLine("PERF " + result); File.WriteAllText(output, result + Environment.NewLine);
                if (growth > 8 * 1048576 || handleGrowth > 32 || gdiGrowth > 16)
                    throw new Exception("Unexpected retained resources after widget/window cycles");
            }
        }

        static async Task Run(MainWindow window, bool baseline, string output)
        {
            File.WriteAllText(output, "phase,seconds,cpu_one_core_percent,cpu_machine_percent,working_min_mib,working_max_mib,private_min_mib,private_max_mib,managed_min_mib,managed_max_mib,gdi_before,gdi_after,handles_before,handles_after,widget_visible,placement_mode,renderer,helper_cpu_one_core_percent,helper_working_max_mib\n");
            Console.WriteLine("PERF environment: " + Environment.OSVersion + "; CLR " + Environment.Version + "; logical processors " + Environment.ProcessorCount);
            await Measure(window, "idle_tray", 30, baseline, output);
            bool supportsWidget = window.GetType().GetMethod("SetTaskbarTimer", Fields) != null;
            if (supportsWidget)
            {
                await EnableWidget(window);
                await Measure(window, "idle_tray_widget", 30, baseline, output);
            }
            Call(window, "StartPause");
            await Measure(window, supportsWidget ? "running_tray_widget" : "running_tray", 45, baseline, output);
            window.ShowWindow();
            await Measure(window, supportsWidget ? "running_window_widget" : "running_window", 30, baseline, output);
            if (!baseline && supportsWidget) await CheckCycles(window, Path.ChangeExtension(output, ".cycles.txt"));
        }

        [STAThread] static int Main(string[] args)
        {
            bool baseline = args.Contains("--baseline");
            nativeRequested = args.Contains("--native");
            string output = Path.GetFullPath(args.FirstOrDefault(arg => arg.EndsWith(".csv")) ?? "performance.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\SleepTimer"))
            {
                var saved = key.GetValueNames().ToDictionary(n => n, n => Tuple.Create(key.GetValue(n), key.GetValueKind(n)));
                try
                {
                    Settings.Set("start_with_windows", false); Settings.Set("awake", false); Settings.Set("taskbar_timer", false);
                    Settings.Set("native_taskbar", nativeRequested);
                    Settings.Set("clock_mode", false); Settings.Set("duration", 86400); // Never reaches expiry in this check.
                    Settings.Set("hotkey_toggle_key", 0); Settings.Set("hotkey_quick_key", 0);
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    Theme.Init(app);
                    var window = new MainWindow(); app.MainWindow = window;
                    int result = 0;
                    app.Dispatcher.BeginInvoke((Action)(async () =>
                    {
                        try { await Run(window, baseline, output); }
                        catch (Exception error) { Console.WriteLine("::error::" + error); result = 1; }
                        finally
                        {
                            window.TestMode = true; window.EndSession(); window.Close();
                            if (controlledParent != IntPtr.Zero) DestroyWindow(controlledParent);
                            app.Shutdown();
                        }
                    }));
                    app.Run(); return result;
                }
                finally
                {
                    foreach (var name in key.GetValueNames()) key.DeleteValue(name, false);
                    foreach (var value in saved) key.SetValue(value.Key, value.Value.Item1, value.Value.Item2);
                }
            }
        }
    }
}
