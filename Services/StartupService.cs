using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Caiqelle.Services
{
    public class StartupService
    {
        private const string AppName = "Caiqelle";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public void SetRunAtStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (key == null) return;

                if (enable)
                {
                    // Get current executable path
                    var processModule = Process.GetCurrentProcess().MainModule;
                    if (processModule != null)
                    {
                        string path = processModule.FileName;
                        // Quote the path and add --background argument
                        key.SetValue(AppName, $"\"{path}\" --background");
                    }
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set startup registry key: {ex.Message}");
            }
        }

        public bool IsRunAtStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                if (key == null) return false;

                var val = key.GetValue(AppName);
                return val != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
