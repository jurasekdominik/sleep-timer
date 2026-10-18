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
using Microsoft.Win32;

namespace SleepTimer
{
    // Runs the real app window and dispatcher. Only reflection and sampling belong to this harness.
    static class PerformanceChecks
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int flags);
        static T Field<T>(object owner, string name)
        {
            var field = owner.GetType().GetField(name, Fields);
            return field == null ? default(T) : (T)field.GetValue(owner);
        }
        static void Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Fields).Invoke(owner, args);
        static double MiB(long bytes) => bytes / 1048576d;

        static async Task Measure(MainWindow window, string name, int seconds, bool baseline, string output)
        {
            await Task.Delay(5000); // Allow JIT, layout, placement and shell providers to settle.
            using (var process = Process.GetCurrentProcess())
            {
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                int gdi = GetGuiResources(process.Handle, 0), handles = process.HandleCount;
                var working = new List<long>(); var memory = new List<long>(); var managed = new List<long>();
                var elapsed = Stopwatch.StartNew();
                for (int i = 0; i < seconds; i++)
                {
                    await Task.Delay(1000);
                    process.Refresh();
                    working.Add(process.WorkingSet64); memory.Add(process.PrivateMemorySize64); managed.Add(GC.GetTotalMemory(false));
                }
                double core = (process.TotalProcessorTime - cpu).TotalSeconds / elapsed.Elapsed.TotalSeconds * 100;
                double machine = core / Environment.ProcessorCount;
                var widget = Field<Window>(window, "taskbarTimer");
                string row = string.Join(",", name, elapsed.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture),
                    core.ToString("F3", CultureInfo.InvariantCulture), machine.ToString("F3", CultureInfo.InvariantCulture),
                    MiB(working.Min()).ToString("F2", CultureInfo.InvariantCulture), MiB(working.Max()).ToString("F2", CultureInfo.InvariantCulture),
                    MiB(memory.Min()).ToString("F2", CultureInfo.InvariantCulture), MiB(memory.Max()).ToString("F2", CultureInfo.InvariantCulture),
                    MiB(managed.Min()).ToString("F2", CultureInfo.InvariantCulture), MiB(managed.Max()).ToString("F2", CultureInfo.InvariantCulture),
                    gdi, GetGuiResources(process.Handle, 0), handles, process.HandleCount, widget?.IsVisible == true);
                File.AppendAllText(output, row + Environment.NewLine);
                Console.WriteLine("PERF " + row);
                if (!baseline && name.StartsWith("idle") && core > 2)
                    throw new Exception("Unexpected idle CPU: " + core.ToString("F2") + "% of one core");
            }
        }

        static async Task CheckCycles(MainWindow window, string output)
        {
            window.Hide(); await Task.Delay(5000);
            using (var process = Process.GetCurrentProcess())
            {
                long heap = GC.GetTotalMemory(true);
                process.Refresh();
                int handles = process.HandleCount, gdi = GetGuiResources(process.Handle, 0);
                for (int i = 0; i < 30; i++)
                {
                    Call(window, "SetTaskbarTimer", false); window.ShowWindow(); await Task.Delay(40);
                    window.Hide(); Call(window, "SetTaskbarTimer", true); await Task.Delay(200);
                }
                await Task.Delay(8000);
                long growth = GC.GetTotalMemory(true) - heap;
                process.Refresh();
                int handleGrowth = process.HandleCount - handles, gdiGrowth = GetGuiResources(process.Handle, 0) - gdi;
                string result = "30 widget/window cycles: retained managed delta " + MiB(growth).ToString("F2", CultureInfo.InvariantCulture)
                    + " MiB; handle delta " + handleGrowth + "; GDI delta " + gdiGrowth;
                Console.WriteLine("PERF " + result); File.WriteAllText(output, result + Environment.NewLine);
                if (growth > 8 * 1048576 || handleGrowth > 32 || gdiGrowth > 16)
                    throw new Exception("Unexpected retained resources after widget/window cycles");
            }
        }

        static async Task Run(MainWindow window, bool baseline, string output)
        {
            File.WriteAllText(output, "phase,seconds,cpu_one_core_percent,cpu_machine_percent,working_min_mib,working_max_mib,private_min_mib,private_max_mib,managed_min_mib,managed_max_mib,gdi_before,gdi_after,handles_before,handles_after,widget_visible\n");
            Console.WriteLine("PERF environment: " + Environment.OSVersion + "; CLR " + Environment.Version + "; logical processors " + Environment.ProcessorCount);
            await Measure(window, "idle_tray", 30, baseline, output);
            if (window.GetType().GetMethod("SetTaskbarTimer", Fields) == null) return;
            Call(window, "SetTaskbarTimer", true);
            await Measure(window, "idle_tray_widget", 30, baseline, output);
            if (baseline) return;
            Call(window, "StartPause");
            await Measure(window, "running_tray_widget", 45, false, output);
            window.ShowWindow();
            await Measure(window, "running_window_widget", 30, false, output);
            await CheckCycles(window, Path.ChangeExtension(output, ".cycles.txt"));
        }

        [STAThread] static int Main(string[] args)
        {
            bool baseline = args.Contains("--baseline");
            string output = Path.GetFullPath(args.FirstOrDefault(arg => arg.EndsWith(".csv")) ?? "performance.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\SleepTimer"))
            {
                var saved = key.GetValueNames().ToDictionary(n => n, n => Tuple.Create(key.GetValue(n), key.GetValueKind(n)));
                try
                {
                    Settings.Set("start_with_windows", false); Settings.Set("awake", false); Settings.Set("taskbar_timer", false);
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
                        finally { window.TestMode = true; window.EndSession(); window.Close(); app.Shutdown(); }
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
