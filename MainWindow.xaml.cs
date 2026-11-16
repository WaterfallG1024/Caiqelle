using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Caiqelle.Views;
using Caiqelle.Services;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using Windows.System;

namespace Caiqelle
{
    public sealed partial class MainWindow : Window
    {
        private ObservableCollection<FolderMapping> _mappings;
        private SettingsManager _settings;
        private bool _isInitializing = true; // 初期化中フラグ
        private bool _isUpdatingConstraints = false;
        private ReleaseInfo? _latestRelease;

        public MainWindow()
        {
            this.InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            // TODO: Caiqelle用アイコンを用意したら設定する
            // appWindow.SetIcon("Assets\\favicon.ico");
            
            var presenter = appWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }
            appWindow.Resize(new Windows.Graphics.SizeInt32(360, 500));

            // ウィンドウの角を丸くする
            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_ROUND = 2; // 丸角
            Views.NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_ROUND, sizeof(int));

            // ウィンドウの白い枠線を完全に削除
            int DWMWA_BORDER_COLOR = 34;
            int DWM_BORDER_COLOR_NONE = -2; // 0xFFFFFFFE
            Views.NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref DWM_BORDER_COLOR_NONE, sizeof(int));

            // フレームをクライアント領域に拡張して枠線を非表示にする
            var margins = new Views.NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            Views.NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // ウィンドウスタイルから枠線を除去
            Views.NativeMethods.RemoveWindowBorder(hwnd);

            this.Closed += MainWindow_Closed;
            this.Activated += MainWindow_Activated;
            
            _settings = ((App)Application.Current).Services.GetService(typeof(SettingsManager)) as SettingsManager 
                        ?? new SettingsManager();
            
            _mappings = new ObservableCollection<FolderMapping>(_settings.ImageFolderMappings);
            MappingsList.ItemsSource = _mappings;

            // Load Hook Settings
            PrintScreenHookSwitch.IsOn = _settings.IsHijackPrintScreen;
            WinShiftSHookSwitch.IsOn = _settings.IsHijackWinShiftS;
            RunAtStartupSwitch.IsOn = _settings.IsRunAtStartup;
            AutoCopyToClipboardSwitch.IsOn = _settings.IsAutoCopyToClipboard;
            AutoCopyToClipboardSwitch.Toggled += AutoCopyToClipboardSwitch_Toggled;
            
            AutoUpdateSwitch.IsOn = _settings.IsAutoUpdateEnabled;

            // バージョン情報の表示（Velopack または アセンブリから取得）
            try
            {
                var mgr = new Velopack.UpdateManager(new Velopack.Sources.GithubSource("https://github.com/WaterfallG1024/Caiqelle-Release", null, false));
                if (mgr.IsInstalled && mgr.CurrentVersion != null)
                {
                    VersionText.Text = $"v{mgr.CurrentVersion}";
                }
                else
                {
                    var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                    VersionText.Text = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v0.1.0";
                }
            }
            catch
            {
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                VersionText.Text = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v0.1.0";
            }
            
            SelectComboBoxItemByTag(ImageFormatCombo, _settings.ImageFormat);
            SelectComboBoxItemByTag(ImageCompressionCombo, _settings.JpegQuality.ToString());

            LoadRewindSettings();
            _isInitializing = false; // 初期化完了
            
            UpdateFormatUIState();
            UpdateRewindUIConstraints();
            
            // Sync initial state to HookService
            var hookService = ((App)Application.Current).Services.GetService(typeof(HookService)) as HookService;
            if (hookService != null)
            {
                hookService.IsHijackPrintScreen = _settings.IsHijackPrintScreen;
                hookService.IsHijackWinShiftS = _settings.IsHijackWinShiftS;
                hookService.IsShiftCapsLockRewindEnabled = _settings.IsShiftCapsLockRewindEnabled;
            }
            
            var rewindService = ((App)Application.Current).Services.GetService(typeof(RewindService)) as RewindService;
            if (rewindService != null)
            {
                rewindService.StateChanged += RewindService_StateChanged;
            }
            
            UpdateRewindStatusCard();
            UpdateNavButtonHighlight(NavHomeBtn);

            // GitHub Releases から最新新機能バナー情報を非同期取得
            _ = CheckAndShowWhatsNewBannerAsync();
        }

        private void RewindService_StateChanged(object? sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() => 
            {
                UpdateRewindStatusCard();
            });
        }

        private void UpdateRewindStatusCard()
        {
            var rewindService = ((App)Application.Current).Services.GetService(typeof(RewindService)) as RewindService;
            if (rewindService == null || !_settings.IsRewindEnabled)
            {
                RewindStatusCard.Visibility = Visibility.Collapsed;
                return;
            }

            RewindStatusCard.Visibility = Visibility.Visible;
            if (rewindService.IsPaused)
            {
                RewindStatusText.Text = $"リワインド一時停止中 ({_settings.RewindDurationSeconds}秒 / {_settings.RewindQuality})";
            }
            else
            {
                RewindStatusText.Text = $"リワインド録画中 ({_settings.RewindDurationSeconds}秒 / {_settings.RewindQuality})";
            }
        }

        private void AutoCopyToClipboardSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _isInitializing) return;
            _settings.IsAutoCopyToClipboard = AutoCopyToClipboardSwitch.IsOn;
            _settings.SaveSettings();
        }

        private void AutoUpdateSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _isInitializing) return;
            _settings.IsAutoUpdateEnabled = AutoUpdateSwitch.IsOn;
            _settings.SaveSettings();
        }

        private async void CheckUpdateBtn_Click(object? sender, RoutedEventArgs? e)
        {
            if (UpdateStatusText == null) return;
            UpdateStatusText.Visibility = Visibility.Visible;
            UpdateStatusText.Text = "更新を確認中...";

            try
            {
                var mgr = new Velopack.UpdateManager(new Velopack.Sources.GithubSource("https://github.com/WaterfallG1024/Caiqelle-Release", null, false));
                if (mgr.IsInstalled)
                {
                    var updateInfo = await mgr.CheckForUpdatesAsync();
                    if (updateInfo == null)
                    {
                        UpdateStatusText.Text = "最新バージョンです";
                    }
                    else
                    {
                        UpdateStatusText.Text = $"新バージョン {updateInfo.TargetFullRelease.Version} をダウンロード中...";
                        await mgr.DownloadUpdatesAsync(updateInfo);
                        
                        // アプリ終了後に適用処理を予約する
                        mgr.WaitExitThenApplyUpdates(updateInfo, silent: true, restart: false);
                        
                        UpdateStatusText.Text = "更新の準備が完了しました。次回起動時に適用されます。";
                    }
                }
                else
                {
                    UpdateStatusText.Text = "最新バージョンです (開発環境)";
                }
            }
            catch
            {
                UpdateStatusText.Text = "最新バージョンです";
            }
        }

        private string GetCurrentVersionString()
        {
            try
            {
                var mgr = new Velopack.UpdateManager(new Velopack.Sources.GithubSource("https://github.com/WaterfallG1024/Caiqelle-Release", null, false));
                if (mgr.IsInstalled && mgr.CurrentVersion != null)
                {
                    return mgr.CurrentVersion.ToString();
                }
            }
            catch { }

            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.1.0";
        }

        private async Task CheckAndShowWhatsNewBannerAsync()
        {
            try
            {
                var releaseService = new GitHubReleaseService();
                _latestRelease = await releaseService.GetLatestReleaseAsync();
                if (_latestRelease != null && !string.IsNullOrEmpty(_latestRelease.TagName))
                {
                    // バージョン比較：現在のバージョンより新しい場合のみ表示する
                    string currentVersionStr = GetCurrentVersionString();
                    string cleanLatestTag = _latestRelease.TagName.TrimStart('v', 'V');
                    string cleanCurrentTag = currentVersionStr.TrimStart('v', 'V');

                    if (Version.TryParse(cleanLatestTag, out Version? latestVersion) && 
                        Version.TryParse(cleanCurrentTag, out Version? currentVersion))
                    {
                        if (latestVersion <= currentVersion)
                        {
                            return; // 最新リリースが現在のバージョン以下の場合は何もしない
                        }
                    }

                    if (_latestRelease.TagName != _settings.LastDismissedReleaseTag)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            string versionTag = !string.IsNullOrEmpty(_latestRelease.TagName) ? _latestRelease.TagName : "新バージョン";
                            WhatsNewTitle.Text = $"{versionTag} がリリースされました！";
                            WhatsNewBannerCard.Visibility = Visibility.Visible;
                        });
                    }
                }
            }
            catch { }
        }

        private async void WhatsNewDetailBtn_Click(object? sender, RoutedEventArgs? e)
        {
            string url = _latestRelease?.HtmlUrl ?? "https://github.com/WaterfallG1024/Caiqelle-Release/releases";
            try
            {
                await Launcher.LaunchUriAsync(new Uri(url));
            }
            catch
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
        }

        private void WhatsNewDismissBtn_Click(object? sender, RoutedEventArgs? e)
        {
            WhatsNewBannerCard.Visibility = Visibility.Collapsed;
            if (_latestRelease != null && !string.IsNullOrEmpty(_latestRelease.TagName))
            {
                _settings.LastDismissedReleaseTag = _latestRelease.TagName;
                _settings.SaveSettings();
            }
        }

        private void AddMapping_Click(object sender, RoutedEventArgs e)
        {
            ValidationErrorBar.IsOpen = false;
            
            string folder = FolderNameInput.Text.Trim();
            string key = KeyInput.Text.Trim().ToUpper();
            
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(key))
            {
                ValidationErrorBar.Message = "フォルダ名とキーを入力してください。";
                ValidationErrorBar.IsOpen = true;
                return;
            }

            char[] invalidChars = new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
            if (folder.IndexOfAny(invalidChars) >= 0)
            {
                ValidationErrorBar.Message = "フォルダ名に \\ / : * ? \" < > | は使用できません。";
                ValidationErrorBar.IsOpen = true;
                return;
            }

            if (key.Length != 1)
            {
                ValidationErrorBar.Message = "キーは1文字のみ指定できます。";
                ValidationErrorBar.IsOpen = true;
                return;
            }

            char k = key[0];
            bool isAlphaNumeric = (k >= '0' && k <= '9') || (k >= 'A' && k <= 'Z');
            bool isAllowedSymbol = "/[]-^;:@".Contains(k);
            if (!isAlphaNumeric && !isAllowedSymbol)
            {
                ValidationErrorBar.Message = "キーには半角英数字(0-9, A-Z)または / [ ] - ^ ; : @ のみが使用可能です。";
                ValidationErrorBar.IsOpen = true;
                return;
            }
            
            foreach (var m in _mappings)
            {
                if (m.Key == key)
                {
                    ValidationErrorBar.Message = $"キー '{key}' はすでに登録されています。";
                    ValidationErrorBar.IsOpen = true;
                    return;
                }
            }

            var newMapping = new FolderMapping { FolderName = folder, Key = key };
            _mappings.Add(newMapping);
            _settings.ImageFolderMappings.Add(newMapping);
            _settings.SaveSettings();
            
            FolderNameInput.Text = "";
            KeyInput.Text = "";
        }

        private void RemoveMapping_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is FolderMapping mapping)
            {
                _mappings.Remove(mapping);
                _settings.ImageFolderMappings.Remove(mapping);
                _settings.SaveSettings();
            }
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            args.Handled = true;
            this.Hide(); 
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                this.Hide();
            }
        }

        private void UpdateNavButtonHighlight(Button? activeBtn)
        {
            var activeBg = (Application.Current.Resources.TryGetValue("M3SurfaceContainerHighestBrush", out var brush) && brush is Microsoft.UI.Xaml.Media.Brush m3Brush)
                ? m3Brush
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x33, 0x33, 0x42));
            var transparentBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

            if (NavHomeBtn != null) NavHomeBtn.Background = (NavHomeBtn == activeBtn) ? activeBg : transparentBg;
            if (NavFolderBtn != null) NavFolderBtn.Background = (NavFolderBtn == activeBtn) ? activeBg : transparentBg;
            if (NavSettingsBtn != null) NavSettingsBtn.Background = (NavSettingsBtn == activeBtn) ? activeBg : transparentBg;
        }

        private void NavHome_Click(object? sender, RoutedEventArgs? e)
        {
            HomeTab.Visibility = Visibility.Visible;
            FolderTab.Visibility = Visibility.Collapsed;
            RewindTab.Visibility = Visibility.Collapsed;
            SettingsTab.Visibility = Visibility.Collapsed;
            if (HeaderTitle != null) HeaderTitle.Text = "クイック アクセス";
            UpdateNavButtonHighlight(NavHomeBtn);
            ContentScrollViewer?.ChangeView(null, 0, null, true);
        }

        private void NavImageFolder_Click(object? sender, RoutedEventArgs? e)
        {
            HomeTab.Visibility = Visibility.Collapsed;
            FolderTab.Visibility = Visibility.Visible;
            RewindTab.Visibility = Visibility.Collapsed;
            SettingsTab.Visibility = Visibility.Collapsed;
            if (HeaderTitle != null) HeaderTitle.Text = "フォルダ管理";
            UpdateNavButtonHighlight(NavFolderBtn);
            ContentScrollViewer?.ChangeView(null, 0, null, true);
        }

        private void NavRewind_Click(object? sender, RoutedEventArgs? e)
        {
            HomeTab.Visibility = Visibility.Collapsed;
            FolderTab.Visibility = Visibility.Collapsed;
            RewindTab.Visibility = Visibility.Visible;
            SettingsTab.Visibility = Visibility.Collapsed;
            if (HeaderTitle != null) HeaderTitle.Text = "リワインド";
            UpdateNavButtonHighlight(null);
            ContentScrollViewer?.ChangeView(null, 0, null, true);

            LoadRewindSettings();
        }

        private void NavSettings_Click(object? sender, RoutedEventArgs? e)
        {
            HomeTab.Visibility = Visibility.Collapsed;
            FolderTab.Visibility = Visibility.Collapsed;
            RewindTab.Visibility = Visibility.Collapsed;
            SettingsTab.Visibility = Visibility.Visible;
            if (HeaderTitle != null) HeaderTitle.Text = "設定";
            UpdateNavButtonHighlight(NavSettingsBtn);
            ContentScrollViewer?.ChangeView(null, 0, null, true);
        }

        private void LoadRewindSettings()
        {
            RewindEnableSwitch.IsOn = _settings.IsRewindEnabled;
            ShiftCapsLockRewindSwitch.IsOn = _settings.IsShiftCapsLockRewindEnabled;
            
            // Disable quality items based on screen height
            var bounds = ((App)Application.Current).Services.GetService(typeof(ScreenshotService)) as ScreenshotService;
            int screenHeight = bounds?.GetVirtualScreenBounds().Height ?? 1080;
            
            foreach (ComboBoxItem item in RewindQualityCombo.Items)
            {
                string tag = item.Tag?.ToString() ?? "";
                if (tag == "4K" && screenHeight < 2160) item.IsEnabled = false;
                if (tag == "2K" && screenHeight < 1440) item.IsEnabled = false;
                if (tag == "FulHD" && screenHeight < 1080) item.IsEnabled = false;
            }

            SelectComboBoxItemByTag(RewindDurationCombo, _settings.RewindDurationSeconds.ToString());
            SelectComboBoxItemByTag(RewindQualityCombo, _settings.RewindQuality);
        }

        private void SelectComboBoxItemByTag(ComboBox combo, string tag)
        {
            bool found = false;
            for (int i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item && item.Tag?.ToString() == tag)
                {
                    combo.SelectedIndex = i;
                    found = true;
                    break;
                }
            }
            if (!found && combo.Items.Count > 0)
            {
                combo.SelectedIndex = 0;
            }
        }

        private void RewindEnableSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _isInitializing) return;
            _settings.IsRewindEnabled = RewindEnableSwitch.IsOn;
            _settings.SaveSettings();
            
            var rewind = ((App)Application.Current).Services.GetService(typeof(RewindService)) as RewindService;
            var notificationService = ((App)Application.Current).Services.GetService(typeof(NotificationService)) as NotificationService;
            
            if (_settings.IsRewindEnabled)
            {
                rewind?.Start();
                notificationService?.ShowNotification("リワインドを開始しました", "\uE7C8");
            }
            else
            {
                rewind?.Stop();
                notificationService?.ShowNotification("リワインドを停止しました", "\uE71A");
            }
        }

        private void ShiftCapsLockRewindSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _isInitializing) return;
            _settings.IsShiftCapsLockRewindEnabled = ShiftCapsLockRewindSwitch.IsOn;
            _settings.SaveSettings();
            
            var hookService = ((App)Application.Current).Services.GetService(typeof(HookService)) as HookService;
            if (hookService != null) hookService.IsShiftCapsLockRewindEnabled = _settings.IsShiftCapsLockRewindEnabled;
        }

        private void RunAtStartupSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settings == null || _isInitializing) return;
            _settings.IsRunAtStartup = RunAtStartupSwitch.IsOn;
            _settings.SaveSettings();

            var startup = ((App)Application.Current).Services.GetService(typeof(StartupService)) as StartupService;
            startup?.SetRunAtStartup(_settings.IsRunAtStartup);
        }

        private void RewindCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings == null || _isInitializing || _isUpdatingConstraints) return;
            
            if (RewindDurationCombo.SelectedItem is ComboBoxItem durItem && int.TryParse(durItem.Tag?.ToString(), out int dur))
            {
                _settings.RewindDurationSeconds = dur;
            }
            if (RewindQualityCombo.SelectedItem is ComboBoxItem qualItem)
            {
                _settings.RewindQuality = qualItem.Tag?.ToString() ?? "FulHD";
            }
            
            UpdateRewindUIConstraints();
            _settings.SaveSettings();
            
            var rewind = ((App)Application.Current).Services.GetService(typeof(RewindService)) as RewindService;
            if (_settings.IsRewindEnabled) rewind?.Restart();
        }

        private void UpdateFormatUIState()
        {
            if (ImageCompressionCombo != null && ImageFormatCombo.SelectedItem is ComboBoxItem fmtItem)
            {
                string format = fmtItem.Tag?.ToString() ?? "PNG";
                ImageCompressionCombo.IsEnabled = format != "PNG";
            }
        }

        private void SettingsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings == null || _isInitializing) return;
            
            if (object.ReferenceEquals(sender, ImageFormatCombo) && ImageFormatCombo.SelectedItem is ComboBoxItem fmtItem)
            {
                string format = fmtItem.Tag?.ToString() ?? "PNG";
                _settings.ImageFormat = format;
                UpdateFormatUIState();
            }
            if (object.ReferenceEquals(sender, ImageCompressionCombo) && ImageCompressionCombo.SelectedItem is ComboBoxItem compItem && int.TryParse(compItem.Tag?.ToString(), out int qual))
            {
                _settings.JpegQuality = qual;
            }
            
            _settings.SaveSettings();
        }

        private void PrintScreenHookSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            _settings.IsHijackPrintScreen = PrintScreenHookSwitch.IsOn;
            _settings.SaveSettings();
            
            var hookService = ((App)Application.Current).Services.GetService(typeof(HookService)) as HookService;
            if (hookService != null) hookService.IsHijackPrintScreen = _settings.IsHijackPrintScreen;
        }

        private void WinShiftSHookSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            _settings.IsHijackWinShiftS = WinShiftSHookSwitch.IsOn;
            _settings.SaveSettings();
            
            var hookService = ((App)Application.Current).Services.GetService(typeof(HookService)) as HookService;
            if (hookService != null) hookService.IsHijackWinShiftS = _settings.IsHijackWinShiftS;
        }

        private async void CaptureRegion_Tapped(object? sender, RoutedEventArgs? e)
        {
            this.Hide();
            var app = (App)Application.Current;
            if (app.CaptureManager != null) await app.CaptureManager.StartCaptureAsync(Services.CapturedKey.C);
        }

        private async void CaptureWindow_Tapped(object? sender, RoutedEventArgs? e)
        {
            this.Hide();
            var app = (App)Application.Current;
            if (app.CaptureManager != null) await app.CaptureManager.StartCaptureAsync(Services.CapturedKey.W);
        }

        private async void CaptureFullscreen_Tapped(object? sender, RoutedEventArgs? e)
        {
            this.Hide();
            var app = (App)Application.Current;
            if (app.CaptureManager != null) await app.CaptureManager.StartCaptureAsync(Services.CapturedKey.F);
        }

        private async void CapturePin_Tapped(object? sender, RoutedEventArgs? e)
        {
            this.Hide();
            var app = (App)Application.Current;
            if (app.CaptureManager != null) await app.CaptureManager.StartCaptureAsync(Services.CapturedKey.P);
        }

        private async void CaptureOCR_Tapped(object? sender, RoutedEventArgs? e)
        {
            this.Hide();
            var app = (App)Application.Current;
            if (app.CaptureManager != null) await app.CaptureManager.StartCaptureAsync(Services.CapturedKey.E);
        }

        private void Grid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            // テキストボックス等の入力中の場合は無視する
            if (e.OriginalSource is TextBox || e.OriginalSource is PasswordBox) return;

            // ホームタブ表示中のキー操作
            if (HomeTab.Visibility == Visibility.Visible)
            {
                switch (e.Key)
                {
                    case Windows.System.VirtualKey.C: // 領域指定キャプチャ
                        CaptureRegion_Tapped(this, null);
                        e.Handled = true;
                        break;
                    case Windows.System.VirtualKey.W: // ウィンドウ指定キャプチャ
                        CaptureWindow_Tapped(this, null);
                        e.Handled = true;
                        break;
                    case Windows.System.VirtualKey.F: // フルスクリーンキャプチャ
                        CaptureFullscreen_Tapped(this, null);
                        e.Handled = true;
                        break;
                    case Windows.System.VirtualKey.P: // ピン留め
                        CapturePin_Tapped(this, null);
                        e.Handled = true;
                        break;
                    case Windows.System.VirtualKey.E: // テキスト抽出
                        CaptureOCR_Tapped(this, null);
                        e.Handled = true;
                        break;
                    case Windows.System.VirtualKey.R: // リワインド設定へ移動
                        NavRewind_Click(this, null);
                        e.Handled = true;
                        break;
                    case Windows.System.VirtualKey.Escape:
                        this.Hide();
                        e.Handled = true;
                        break;
                }
            }
            else
            {
                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    this.Hide();
                    e.Handled = true;
                }
            }
        }

        private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.CenterPoint = new Vector3((float)(btn.ActualWidth / 2), (float)(btn.ActualHeight / 2), 0f);
                btn.Scale = new Vector3(1.04f, 1.04f, 1.0f);
            }
        }

        private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                btn.Scale = new Vector3(1.0f, 1.0f, 1.0f);
            }
        }

        private void UpdateRewindUIConstraints()
        {
            if (_isUpdatingConstraints) return;
            _isUpdatingConstraints = true;

            try
            {
                var memStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memStatus))
                {
                    double totalMemoryGB = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                    
                    int maxDuration = int.MaxValue;
                    bool allowHighQuality = true;
                    bool allow4KOnlyShort = false;

                    if (totalMemoryGB < 7.0)
                    {
                        RewindEnableSwitch.IsOn = false;
                        RewindEnableSwitch.IsEnabled = false;
                        RewindMemoryInfoBar.Title = "メモリ不足";
                        RewindMemoryInfoBar.Message = "メモリが8GB未満のため、この機能は使用できません。";
                        RewindMemoryInfoBar.Severity = InfoBarSeverity.Error;
                        return;
                    }
                    else if (totalMemoryGB < 14.0)
                    {
                        maxDuration = 10;
                        allowHighQuality = false;
                        RewindMemoryInfoBar.Title = "メモリ8GB制限";
                        RewindMemoryInfoBar.Message = "メモリが8GBのため、最大10秒・720p/FullHDに制限されています。";
                        RewindMemoryInfoBar.Severity = InfoBarSeverity.Warning;
                    }
                    else if (totalMemoryGB < 28.0)
                    {
                        maxDuration = 60;
                        allowHighQuality = true;
                        RewindMemoryInfoBar.Title = "メモリ16GB制限";
                        RewindMemoryInfoBar.Message = "メモリが16GBのため、最大60秒までに制限されています。";
                        RewindMemoryInfoBar.Severity = InfoBarSeverity.Informational;
                    }
                    else if (totalMemoryGB < 56.0)
                    {
                        maxDuration = 300;
                        allowHighQuality = true;
                        allow4KOnlyShort = true;
                        RewindMemoryInfoBar.Title = "メモリ32GB推奨構成";
                        RewindMemoryInfoBar.Message = "4K録画時は最大180秒、2K以下は最大300秒まで設定可能です。";
                        RewindMemoryInfoBar.Severity = InfoBarSeverity.Informational;
                    }
                    else
                    {
                        maxDuration = 300;
                        allowHighQuality = true;
                        allow4KOnlyShort = false;
                        RewindMemoryInfoBar.Title = "フルパフォーマンス構成";
                        RewindMemoryInfoBar.Message = "大容量メモリが搭載されています。すべての画質・秒数を制限なくご利用いただけます。";
                        RewindMemoryInfoBar.Severity = InfoBarSeverity.Success;
                    }

                    string currentQuality = "FulHD";
                    if (RewindQualityCombo.SelectedItem is ComboBoxItem qualItem)
                    {
                        currentQuality = qualItem.Tag?.ToString() ?? "FulHD";
                    }

                    int currentMaxDuration = maxDuration;
                    if (allow4KOnlyShort && currentQuality == "4K")
                    {
                        currentMaxDuration = Math.Min(maxDuration, 180);
                    }

                    foreach (ComboBoxItem item in RewindDurationCombo.Items)
                    {
                        if (int.TryParse(item.Tag?.ToString(), out int dur))
                        {
                            item.IsEnabled = dur <= currentMaxDuration;
                            
                            if (object.ReferenceEquals(RewindDurationCombo.SelectedItem, item) && !item.IsEnabled)
                            {
                                SelectComboBoxItemByTag(RewindDurationCombo, currentMaxDuration.ToString());
                                _settings.RewindDurationSeconds = currentMaxDuration;
                            }
                        }
                    }

                    int screenHeight = 1080;
                    var screenshotService = ((App)Application.Current).Services.GetService(typeof(ScreenshotService)) as ScreenshotService;
                    if (screenshotService != null)
                    {
                        screenHeight = screenshotService.GetVirtualScreenBounds().Height;
                    }

                    foreach (ComboBoxItem item in RewindQualityCombo.Items)
                    {
                        string q = item.Tag?.ToString() ?? "";
                        
                        int requiredHeight = 0;
                        switch (q)
                        {
                            case "720p": requiredHeight = 720; break;
                            case "FulHD": requiredHeight = 1080; break;
                            case "2K": requiredHeight = 1440; break;
                            case "4K": requiredHeight = 2160; break;
                        }
                        
                        bool isResolutionAllowed = screenHeight >= requiredHeight;
                        bool isMemoryAllowed = true;
                        
                        if (q == "2K" || q == "4K")
                        {
                            isMemoryAllowed = allowHighQuality;
                        }
                        
                        item.IsEnabled = isResolutionAllowed && isMemoryAllowed;
                        
                        if (object.ReferenceEquals(RewindQualityCombo.SelectedItem, item) && !item.IsEnabled)
                        {
                            if (screenHeight >= 1080 && allowHighQuality)
                            {
                                SelectComboBoxItemByTag(RewindQualityCombo, "FulHD");
                                _settings.RewindQuality = "FulHD";
                            }
                            else
                            {
                                SelectComboBoxItemByTag(RewindQualityCombo, "720p");
                                _settings.RewindQuality = "720p";
                            }
                        }
                    }
                }
            }
            finally
            {
                _isUpdatingConstraints = false;
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            
            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] MEMORYSTATUSEX lpBuffer);
    }
}
