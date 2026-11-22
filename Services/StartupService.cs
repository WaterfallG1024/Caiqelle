using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Caiqelle.Services
{
    public class StartupService
    {
        private const string TaskName = "Caiqelle";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public void SetRunAtStartup(bool enable)
        {
            try
            {
                if (enable)
                {
                    string? exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(exePath)) return;

                    string userId = GetCurrentUserId();
                    string xml = BuildScheduledTaskXml(exePath, userId);

                    // 共有の %TEMP% を避け、ユーザー固有のアプリデータ配下に一時 XML を作成する
                    string appDataDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Caiqelle");
                    Directory.CreateDirectory(appDataDir);
                    string tempXmlPath = Path.Combine(appDataDir, $"Startup_{Guid.NewGuid():N}.xml");
                    try
                    {
                        File.WriteAllText(tempXmlPath, xml, Encoding.Unicode);
                        int exitCode = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tempXmlPath}\" /F");
                        if (exitCode == 0)
                        {
                            // タスクスケジューラ登録成功時は旧 Run レジストリキーを削除して二重起動を防止
                            DeleteLegacyRunKey();
                            return;
                        }
                    }
                    finally
                    {
                        try { if (File.Exists(tempXmlPath)) File.Delete(tempXmlPath); } catch { }
                    }

                    // 万一タスク作成に失敗した場合のみ従来の Run キーへフォールバック
                    SetLegacyRunKey(exePath);
                }
                else
                {
                    RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
                    DeleteLegacyRunKey();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to configure startup task: {ex.Message}");
            }
        }

        public bool IsRunAtStartup()
        {
            try
            {
                if (RunSchtasks($"/Query /TN \"{TaskName}\"") == 0)
                {
                    return true;
                }

                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(TaskName) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Windows の Startup Delay を回避し、バッテリー駆動時でもログオン直後に即時起動するタスク XML を生成する
        /// </summary>
        public static string BuildScheduledTaskXml(string exePath, string userId)
        {
            string escapedExe = SecurityElement.Escape(exePath) ?? exePath;
            string workingDir = Path.GetDirectoryName(exePath) ?? string.Empty;
            string escapedWorkDir = SecurityElement.Escape(workingDir) ?? workingDir;
            string escapedUserId = SecurityElement.Escape(userId) ?? userId;

            return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.4"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{escapedUserId}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <UserId>{escapedUserId}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>5</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{escapedExe}</Command>
      <Arguments>--background</Arguments>
      <WorkingDirectory>{escapedWorkDir}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
        }

        private static string GetCurrentUserId()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (!string.IsNullOrWhiteSpace(identity.Name))
                {
                    return identity.Name;
                }
            }
            catch { }

            return string.IsNullOrWhiteSpace(Environment.UserDomainName)
                ? Environment.UserName
                : $@"{Environment.UserDomainName}\{Environment.UserName}";
        }

        private static int RunSchtasks(string arguments)
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            proc.Start();
            // パイプバッファ満杯によるデッドロックを防ぐため、標準出力・標準エラーを非同期で読み切る
            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(); } catch { }
                return -1;
            }
            return proc.ExitCode;
        }

        private static void SetLegacyRunKey(string exePath)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.SetValue(TaskName, $"\"{exePath}\" --background");
        }

        private static void DeleteLegacyRunKey()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                key?.DeleteValue(TaskName, false);
            }
            catch { }
        }
    }
}
