using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;

namespace SleepTimer
{
    // A separately owned process renders a Win32 child of Explorer's taskbar. No Explorer DLL or WPF DPI changes.
    sealed class NativeTaskbarHost : IDisposable
    {
        internal const uint Magic = 0x53544e31, Version = 1;
        internal const int ReadyMessage = 0x8051, InputMessage = 0x8052;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct View
        {
            public uint Magic, Version, Bytes;
            public int X, Y, Width, Height;
            public uint Visible, State, Accent, Text, Secondary, Track, Progress;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Time;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 96)] public string Action;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct CopyData { public IntPtr Tag; public int Bytes; public IntPtr Data; }
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        internal static void ParentProcess(IntPtr window, out uint process) => GetWindowThreadProcessId(window, out process);
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr window, int message,
            IntPtr wParam, ref CopyData data, uint flags, uint timeout, out IntPtr result);
        readonly Dispatcher dispatcher;
        readonly DispatcherTimer startup;
        readonly IntPtr owner;
        internal readonly IntPtr Parent;
        readonly Action changed;
        readonly Action<int, ModifierKeys> scroll;
        readonly Action toggle, cancel, menu, open;
        Process process;
        IntPtr window;
        bool disposed;
        public bool Ready => !disposed && window != IntPtr.Zero;
        public bool Starting => !disposed && process != null && window == IntPtr.Zero;
        public string Failure { get; private set; }

        internal NativeTaskbarHost(IntPtr owner, IntPtr parent, Action changed, Action toggle, Action cancel,
            Action menu, Action open, Action<int, ModifierKeys> scroll)
        {
            this.owner = owner; Parent = parent; this.changed = changed;
            this.toggle = toggle; this.cancel = cancel; this.menu = menu; this.open = open; this.scroll = scroll;
            dispatcher = Dispatcher.CurrentDispatcher;
            startup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
            startup.Tick += (s, e) => Fail("Native taskbar startup timed out");
        }

        public bool Start()
        {
            try
            {
                string executable = ExecutablePath();
                if (executable == null || !IsWindow(owner) || !IsWindow(Parent)) return false;
                process = new Process { StartInfo = new ProcessStartInfo(executable,
                    owner.ToInt64().ToString("X") + " " + Parent.ToInt64().ToString("X")) { UseShellExecute = false, CreateNoWindow = true } };
                process.Exited += OnExited;
                if (!process.Start()) return false;
                process.EnableRaisingEvents = true;
                startup.Start(); return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is Win32Exception
                || error is InvalidOperationException || error is NotSupportedException)
            { Failure = error.Message; return false; }
        }

        public bool HandleMessage(int message, IntPtr sender, IntPtr encoded)
        {
            if (disposed || process == null || (message != ReadyMessage && message != InputMessage)) return false;
            GetWindowThreadProcessId(sender, out uint senderProcess);
            if (sender == IntPtr.Zero || senderProcess != process.Id) return false;
            if (message == ReadyMessage)
            {
                if (encoded.ToInt64() != Version || GetParent(sender) != Parent) { Fail("Invalid native taskbar handshake"); return true; }
                startup.Stop(); window = sender; changed(); return true;
            }
            if (sender != window || !Ready) return false;
            uint value = unchecked((uint)encoded.ToInt64());
            int command = (int)(value & 255);
            // Run after the native mouse message has released capture, matching WPF menu/quit handling.
            dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() =>
            {
                if (disposed) return;
                switch (command)
                {
                    case 1: toggle(); break;
                    case 2: cancel(); break;
                    case 3: menu(); break;
                    case 4: open?.Invoke(); break;
                    case 5: scroll(unchecked((short)(value >> 16)), (ModifierKeys)((value >> 8) & 7)); break;
                }
            }));
            return true;
        }

        public bool Update(View view)
        {
            if (!Ready) return false;
            view.Magic = Magic; view.Version = Version; view.Bytes = (uint)Marshal.SizeOf(typeof(View));
            view.Time = Limit(view.Time, 31); view.Action = Limit(view.Action, 95);
            IntPtr memory = Marshal.AllocHGlobal((int)view.Bytes);
            try
            {
                Marshal.StructureToPtr(view, memory, false);
                var packet = new CopyData { Tag = new IntPtr(Magic), Bytes = (int)view.Bytes, Data = memory };
                if (SendMessageTimeout(window, 0x004A, owner, ref packet, 3, 40, out var result) != IntPtr.Zero
                    && result == new IntPtr(1)) return true;
                Fail("Native taskbar stopped responding"); return false;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        static string Limit(string value, int length)
        {
            value = value ?? "";
            if (value.Length <= length) return value;
            if (char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length);
        }

        static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        internal static string ExecutablePath()
        {
            if (!Environment.Is64BitOperatingSystem) return null;
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("SleepTimer.NativeTaskbar.exe"))
            {
                if (resource == null) return null; // SDK-only builds retain the WPF renderer.
                using (var contents = new MemoryStream())
                {
                    resource.CopyTo(contents); byte[] bytes = contents.ToArray(); string hash = Hash(bytes);
                    string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SleepTimer", "native", hash);
                    string path = Path.Combine(directory, "SleepTimer.Taskbar.exe");
                    if (File.Exists(path) && Hash(File.ReadAllBytes(path)) == hash) return path;
                    Directory.CreateDirectory(directory);
                    string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        File.WriteAllBytes(temporary, bytes);
                        if (File.Exists(path)) File.Delete(path);
                        File.Move(temporary, path);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    return path;
                }
            }
        }

        void OnExited(object sender, EventArgs e)
        {
            if (disposed || dispatcher.HasShutdownStarted) return;
            try { dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() => { if (!disposed) Fail("Native taskbar exited"); })); }
            catch (InvalidOperationException) { } // Dispatcher shut down between the check and the post.
        }
        void Fail(string reason)
        {
            if (disposed) return;
            Failure = reason; Dispose();
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(DispatcherPriority.Input, changed);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; startup.Stop();
            var owned = process; process = null;
            if (window != IntPtr.Zero) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            window = IntPtr.Zero;
            if (owned == null) return;
            owned.Exited -= OnExited;
            // Bound shutdown without blocking WPF. This Process owns its handle; no process-name/PID kill.
            Task.Run(() =>
            {
                try { if (!owned.WaitForExit(1500)) owned.Kill(); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                finally { owned.Dispose(); }
            });
        }
    }
}
