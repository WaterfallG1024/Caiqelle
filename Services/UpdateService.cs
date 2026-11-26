using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Caiqelle.Services
{
    public class UpdateService : IDisposable
    {
        private readonly SettingsManager _settings;
        private readonly NotificationService _notificationService;
        private const string RepoUrl = "https://github.com/WaterfallG1024/Caiqelle";
        private Timer? _periodicTimer;
        private bool _isChecking;

        public UpdateInfo? PendingUpdate { get; private set; }
        public bool HasPendingUpdate => PendingUpdate != null;
        public string? PendingUpdateVersion => PendingUpdate?.TargetFullRelease.Version.ToString();

        public event Action<string>? UpdateDownloaded;

        public UpdateService(SettingsManager settings, NotificationService notificationService)
        {
            _settings = settings;
            _notificationService = notificationService;
        }

        /// <summary>
        /// アプリ起動時にバックグラウンドで静かに更新をチェック・ダウンロードする
        /// </summary>
        public void StartAutoUpdateLoop()
        {
            if (!_settings.IsAutoUpdateEnabled) return;

            // 起動後 10 秒後に初回チェック、その後 8 時間ごとに定期チェック
            _periodicTimer = new Timer(async _ =>
            {
                await CheckAndDownloadUpdatesSilentlyAsync();
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromHours(8));
        }

        public async Task CheckAndDownloadUpdatesSilentlyAsync()
        {
            if (!_settings.IsAutoUpdateEnabled || _isChecking || PendingUpdate != null) return;

            _isChecking = true;
            try
            {
                var mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
                if (!mgr.IsInstalled) return;

                var updateInfo = await mgr.CheckForUpdatesAsync();
                if (updateInfo != null)
                {
                    // バックグラウンドで更新パッケージを静かにダウンロード
                    await mgr.DownloadUpdatesAsync(updateInfo);

                    // アプリ終了後に Update.exe を裏で走らせて更新を適用する（自動再起動はしない）
                    mgr.WaitExitThenApplyUpdates(updateInfo, silent: true, restart: false);

                    string version = updateInfo.TargetFullRelease.Version.ToString();
                    PendingUpdate = updateInfo;
                    UpdateDownloaded?.Invoke(version);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateService] Silent update failed: {ex.Message}");
            }
            finally
            {
                _isChecking = false;
            }
        }

        public async Task<(bool hasUpdate, string message)> CheckAndDownloadManualAsync(Action<string>? onProgress = null)
        {
            if (PendingUpdate != null)
            {
                return (true, $"新バージョン ({PendingUpdateVersion}) の準備が完了しています。");
            }

            if (_isChecking)
            {
                return (false, "現在更新を確認中です...");
            }

            _isChecking = true;
            try
            {
                var mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
                if (!mgr.IsInstalled)
                {
                    return (false, "最新バージョンです (開発環境)");
                }

                var updateInfo = await mgr.CheckForUpdatesAsync();
                if (updateInfo == null)
                {
                    return (false, "最新バージョンです");
                }

                string version = updateInfo.TargetFullRelease.Version.ToString();
                onProgress?.Invoke($"新バージョン {version} をダウンロード中...");
                await mgr.DownloadUpdatesAsync(updateInfo);

                mgr.WaitExitThenApplyUpdates(updateInfo, silent: true, restart: false);
                PendingUpdate = updateInfo;
                UpdateDownloaded?.Invoke(version);

                return (true, $"新バージョン ({version}) の準備が完了しました。");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateService] Manual update check failed: {ex.Message}");
                return (false, "更新の確認に失敗しました。ネットワーク接続を確認してください。");
            }
            finally
            {
                _isChecking = false;
            }
        }

        /// <summary>
        /// ダウンロード済みの更新を即時適用してアプリを再起動する
        /// </summary>
        public void ApplyUpdateAndRestart()
        {
            var update = PendingUpdate;
            if (update == null) return;

            try
            {
                var mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
                mgr.ApplyUpdatesAndRestart(update);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateService] ApplyUpdatesAndRestart failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 開発環境・UIテスト用：アプリを終了して再起動する（単一インスタンスMutex解放待ち付き）
        /// </summary>
        public void RestartApplication()
        {
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath)) return;

                // 親プロセス終了後に単一インスタンス Mutex が確実に解放された状態で起動するよう約2秒待ってから起動
                // パスをコマンド文字列へ直接展開せず環境変数経由で渡すことで、メタ文字によるコマンドインジェクションを防止する
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-WindowStyle");
                psi.ArgumentList.Add("Hidden");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add("Start-Sleep -Seconds 2; Start-Process -FilePath $env:CAIQELLE_RESTART_EXE");
                psi.Environment["CAIQELLE_RESTART_EXE"] = exePath;

                System.Diagnostics.Process.Start(psi);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateService] RestartApplication failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _periodicTimer?.Dispose();
            _periodicTimer = null;
        }
    }
}
