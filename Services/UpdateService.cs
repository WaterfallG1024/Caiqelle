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
        private const string RepoUrl = "https://github.com/WaterfallG1024/Caiqelle-Release";
        private Timer? _periodicTimer;
        private bool _isChecking;

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

            // 起動後 10 秒後に初回チェック、その後 4 時間ごとに定期チェック
            _periodicTimer = new Timer(async _ =>
            {
                await CheckAndDownloadUpdatesSilentlyAsync();
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromHours(4));
        }

        public async Task CheckAndDownloadUpdatesSilentlyAsync()
        {
            if (!_settings.IsAutoUpdateEnabled || _isChecking) return;

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
                    
                    // アプリ終了後に Update.exe を裏で走らせて更新を適用する（再起動はしない）
                    mgr.WaitExitThenApplyUpdates(updateInfo, silent: true, restart: false);
                    
                    // 必要に応じて通知
                    _notificationService.ShowNotification(
                        "Caiqelle",
                        $"新バージョン ({updateInfo.TargetFullRelease.Version}) の準備が完了しました。次回起動時に自動適用されます。"
                    );
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

        public void Dispose()
        {
            _periodicTimer?.Dispose();
            _periodicTimer = null;
        }
    }
}
