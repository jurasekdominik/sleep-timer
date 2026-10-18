using System;
using System.IO;

namespace SleepTimer
{
    static class CrashLog
    {
        // Preserve the exception for diagnosis; these handlers do not suppress a crash.
        public static void Write(Exception error, string directory = null)
        {
            try
            {
                if (directory == null)
                {
                    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    if (string.IsNullOrEmpty(local)) return;
                    directory = Path.Combine(local, "SleepTimer");
                }
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "last-crash.log"), DateTimeOffset.Now.ToString("O")
                    + "\nSleep Timer " + Program.Version + " · " + Environment.OSVersion + " · CLR " + Environment.Version
                    + "\n" + error);
            }
            catch { } // A logging failure must not replace the original exception.
        }
    }
}
