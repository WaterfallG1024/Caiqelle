using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.System;
using Caiqelle.Services;

namespace Caiqelle.ViewModels
{
    /// <summary>
    /// メインウィンドウの表示タブ種別
    /// </summary>
    public enum MainTab
    {
        Home,
        Folder,
        Rewind,
        Settings
    }

    /// <summary>
    /// ComboBox 用の選択項目モデル（有効/無効状態をバインディング可能）
    /// </summary>
    public partial class SelectableOption : ObservableObject
    {
        public string Label { get; }
        public string Value { get; }

        [ObservableProperty]
        public partial bool IsEnabled { get; set; } = true;

        public SelectableOption(string label, string value)
        {
            Label = label;
            Value = value;
        }
    }

    /// <summary>
    /// カラーテーマ選択用の表示モデル
    /// </summary>
    public class AccentThemeOption
    {
        public string Label { get; }
        public AccentTheme Value { get; }
        public Microsoft.UI.Xaml.Media.SolidColorBrush ColorBrush { get; }

        public AccentThemeOption(AccentThemeInfo info)
        {
            Label = info.DisplayName;
            Value = info.Theme;
            ColorBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(info.Color);
        }
    }

    /// <summary>
    /// MainWindow の状態・コマンドを管理する ViewModel
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        private readonly SettingsManager _settings;
        private readonly RewindService _rewindService;
        private readonly HookService _hookService;
        private readonly ScreenshotService _screenshotService;
        private readonly StartupService _startupService;
        private readonly NotificationService _notificationService;
        private readonly ThemeService _themeService;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

        private bool _isInitializing = true;
        private bool _isUpdatingConstraints;
        private ReleaseInfo? _latestRelease;

        /// <summary>
        /// キャプチャ開始前にウィンドウを非表示にするためのコールバック
        /// </summary>
        public Action? RequestHideWindow { get; set; }

        /// <summary>
        /// キャプチャマネージャー取得用デリゲート
        /// </summary>
        public Func<CaptureManager?>? GetCaptureManager { get; set; }

        /// <summary>
        /// タブ切替時にスクロール位置を先頭に戻すための通知イベント
        /// </summary>
        public event EventHandler? TabChanged;

        // --- ナビゲーション状態 ---

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsHomeTabVisible))]
        [NotifyPropertyChangedFor(nameof(IsFolderTabVisible))]
        [NotifyPropertyChangedFor(nameof(IsRewindTabVisible))]
        [NotifyPropertyChangedFor(nameof(IsSettingsTabVisible))]
        [NotifyPropertyChangedFor(nameof(HeaderTitle))]
        public partial MainTab CurrentTab { get; set; } = MainTab.Home;

        public bool IsHomeTabVisible => CurrentTab == MainTab.Home;
        public bool IsFolderTabVisible => CurrentTab == MainTab.Folder;
        public bool IsRewindTabVisible => CurrentTab == MainTab.Rewind;
        public bool IsSettingsTabVisible => CurrentTab == MainTab.Settings;

        public string HeaderTitle => CurrentTab switch
        {
            MainTab.Home => "クイック アクセス",
            MainTab.Folder => "フォルダ管理",
            MainTab.Rewind => "リワインド",
            MainTab.Settings => "設定",
            _ => "クイック アクセス"
        };

        // --- What's New バナー ---

        [ObservableProperty]
        public partial bool IsWhatsNewVisible { get; set; }

        [ObservableProperty]
        public partial string WhatsNewTitle { get; set; } = string.Empty;

        // --- リワインド状態カード ---

        [ObservableProperty]
        public partial bool IsRewindStatusVisible { get; set; }

        [ObservableProperty]
        public partial string RewindStatusText { get; set; } = string.Empty;

        // --- フォルダ管理タブ ---

        public ObservableCollection<FolderMapping> Mappings { get; } = new();

        [ObservableProperty]
        public partial string FolderNameInput { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string KeyInput { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsValidationErrorOpen { get; set; }

        [ObservableProperty]
        public partial string ValidationErrorMessage { get; set; } = string.Empty;

        // --- リワインド設定タブ ---

        [ObservableProperty]
        public partial bool IsRewindEnabled { get; set; }

        [ObservableProperty]
        public partial bool CanEnableRewind { get; set; } = true;

        [ObservableProperty]
        public partial bool IsShiftCapsLockRewindEnabled { get; set; }

        [ObservableProperty]
        public partial string RewindMemoryInfoTitle { get; set; } = "メモリ搭載量に応じた制限について";

        [ObservableProperty]
        public partial string RewindMemoryInfoMessage { get; set; } = string.Empty;

        [ObservableProperty]
        public partial InfoBarSeverity RewindMemoryInfoSeverity { get; set; } = InfoBarSeverity.Informational;

        public ObservableCollection<SelectableOption> RewindDurationOptions { get; } = new()
        {
            new SelectableOption("3秒", "3"),
            new SelectableOption("5秒", "5"),
            new SelectableOption("10秒", "10"),
            new SelectableOption("20秒", "20"),
            new SelectableOption("30秒", "30"),
            new SelectableOption("60秒", "60"),
            new SelectableOption("1分30秒", "90"),
            new SelectableOption("2分", "120"),
            new SelectableOption("3分", "180"),
            new SelectableOption("4分", "240"),
            new SelectableOption("5分", "300"),
            new SelectableOption("10分", "600"),
            new SelectableOption("15分", "900"),
            new SelectableOption("20分", "1200")
        };

        public ObservableCollection<SelectableOption> RewindQualityOptions { get; } = new()
        {
            new SelectableOption("低画質(720p)", "720p"),
            new SelectableOption("中画質(FullHD)", "FulHD"),
            new SelectableOption("高画質(2K)", "2K"),
            new SelectableOption("最高画質(4K)", "4K")
        };

        [ObservableProperty]
        public partial string SelectedRewindDuration { get; set; } = "30";

        [ObservableProperty]
        public partial string SelectedRewindQuality { get; set; } = "FulHD";

        // --- 全体設定タブ ---

        public ObservableCollection<SelectableOption> ImageFormatOptions { get; } = new()
        {
            new SelectableOption("PNG", "PNG"),
            new SelectableOption("JPEG", "JPEG")
        };

        public ObservableCollection<SelectableOption> ImageCompressionOptions { get; } = new()
        {
            new SelectableOption("最高画質(100)", "100"),
            new SelectableOption("高画質(90)", "90"),
            new SelectableOption("標準(70)", "70"),
            new SelectableOption("低画質(50)", "50")
        };

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsImageCompressionEnabled))]
        public partial string SelectedImageFormat { get; set; } = "PNG";

        public bool IsImageCompressionEnabled => SelectedImageFormat != "PNG";

        [ObservableProperty]
        public partial string SelectedJpegQuality { get; set; } = "90";

        [ObservableProperty]
        public partial bool IsHijackPrintScreen { get; set; }

        [ObservableProperty]
        public partial bool IsHijackWinShiftS { get; set; }

        [ObservableProperty]
        public partial bool IsRunAtStartup { get; set; }

        [ObservableProperty]
        public partial bool IsAutoCopyToClipboard { get; set; }

        [ObservableProperty]
        public partial bool IsAutoUpdateEnabled { get; set; }

        [ObservableProperty]
        public partial string VersionText { get; set; } = "v0.1.0";

        [ObservableProperty]
        public partial string UpdateStatusText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsUpdateStatusVisible { get; set; }

        public ObservableCollection<AccentThemeOption> AccentThemeOptions { get; } = new();

        [ObservableProperty]
        public partial AccentTheme SelectedAccentTheme { get; set; } = AccentTheme.Emerald;

        public int SelectedAccentThemeIndex
        {
            get => (int)SelectedAccentTheme;
            set
            {
                if (value >= 0 && value < AccentThemeOptions.Count)
                {
                    SelectedAccentTheme = AccentThemeOptions[value].Value;
                }
            }
        }

        public MainViewModel(
            SettingsManager settings,
            RewindService rewindService,
            HookService hookService,
            ScreenshotService screenshotService,
            StartupService startupService,
            NotificationService notificationService,
            ThemeService themeService)
        {
            _settings = settings;
            _rewindService = rewindService;
            _hookService = hookService;
            _screenshotService = screenshotService;
            _startupService = startupService;
            _notificationService = notificationService;
            _themeService = themeService;
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            foreach (var info in ThemeService.AvailableThemes)
            {
                AccentThemeOptions.Add(new AccentThemeOption(info));
            }

            InitializeFromSettings();

            _rewindService.StateChanged += RewindService_StateChanged;
            _settings.MappingsChanged += Settings_MappingsChanged;
            _ = CheckAndShowWhatsNewBannerAsync();
        }

        private void Settings_MappingsChanged()
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                Mappings.Clear();
                foreach (var mapping in _settings.ImageFolderMappings)
                {
                    Mappings.Add(mapping);
                }
            });
        }

        private void InitializeFromSettings()
        {
            _isInitializing = true;
            try
            {
                Mappings.Clear();
                foreach (var mapping in _settings.ImageFolderMappings)
                {
                    Mappings.Add(mapping);
                }

                IsHijackPrintScreen = _settings.IsHijackPrintScreen;
                IsHijackWinShiftS = _settings.IsHijackWinShiftS;
                IsRunAtStartup = _settings.IsRunAtStartup;
                IsAutoCopyToClipboard = _settings.IsAutoCopyToClipboard;
                IsAutoUpdateEnabled = _settings.IsAutoUpdateEnabled;

                VersionText = $"v{GetCurrentVersionString()}";

                SelectedImageFormat = string.IsNullOrEmpty(_settings.ImageFormat) ? "PNG" : _settings.ImageFormat;
                SelectedJpegQuality = _settings.JpegQuality.ToString();
                SelectedAccentTheme = _settings.AccentTheme;

                LoadRewindSettings();
                UpdateRewindUIConstraints();

                _hookService.IsHijackPrintScreen = _settings.IsHijackPrintScreen;
                _hookService.IsHijackWinShiftS = _settings.IsHijackWinShiftS;
                _hookService.IsShiftCapsLockRewindEnabled = _settings.IsShiftCapsLockRewindEnabled;

                UpdateRewindStatusCard();
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private void RewindService_StateChanged(object? sender, EventArgs e)
        {
            _dispatcherQueue?.TryEnqueue(UpdateRewindStatusCard);
        }

        private void UpdateRewindStatusCard()
        {
            if (!_settings.IsRewindEnabled)
            {
                IsRewindStatusVisible = false;
                return;
            }

            IsRewindStatusVisible = true;
            string durationLabel = FormatDuration(_settings.RewindDurationSeconds);
            RewindStatusText = _rewindService.IsPaused
                ? $"リワインド一時停止中 ({durationLabel} / {_settings.RewindQuality})"
                : $"リワインド録画中 ({durationLabel} / {_settings.RewindQuality})";
        }

        /// <summary>
        /// 秒数を表示用文字列へ変換する（61秒以上は○分○秒表記）
        /// </summary>
        public static string FormatDuration(int seconds)
        {
            if (seconds <= 60) return $"{seconds}秒";
            int mins = seconds / 60;
            int secs = seconds % 60;
            return secs == 0 ? $"{mins}分" : $"{mins}分{secs}秒";
        }

        // --- ナビゲーションコマンド ---

        [RelayCommand]
        private void NavigateToHome() => SwitchTab(MainTab.Home);

        [RelayCommand]
        private void NavigateToFolder() => SwitchTab(MainTab.Folder);

        [RelayCommand]
        private void NavigateToRewind()
        {
            SwitchTab(MainTab.Rewind);
            _isInitializing = true;
            try
            {
                LoadRewindSettings();
                UpdateRewindUIConstraints();
            }
            finally
            {
                _isInitializing = false;
            }
        }

        [RelayCommand]
        private void NavigateToSettings() => SwitchTab(MainTab.Settings);

        private void SwitchTab(MainTab tab)
        {
            CurrentTab = tab;
            TabChanged?.Invoke(this, EventArgs.Empty);
        }

        // --- キャプチャコマンド ---

        [RelayCommand]
        private Task CaptureRegionAsync() => ExecuteCaptureAsync(CapturedKey.C);

        [RelayCommand]
        private Task CaptureWindowAsync() => ExecuteCaptureAsync(CapturedKey.W);

        [RelayCommand]
        private Task CaptureFullscreenAsync() => ExecuteCaptureAsync(CapturedKey.F);

        [RelayCommand]
        private Task CapturePinAsync() => ExecuteCaptureAsync(CapturedKey.P);

        [RelayCommand]
        private Task CaptureOcrAsync() => ExecuteCaptureAsync(CapturedKey.E);

        private async Task ExecuteCaptureAsync(CapturedKey key)
        {
            RequestHideWindow?.Invoke();
            var captureManager = GetCaptureManager?.Invoke();
            if (captureManager != null)
            {
                await captureManager.StartCaptureAsync(key);
            }
        }

        // --- フォルダマッピングコマンド ---

        [RelayCommand]
        private void AddMapping()
        {
            IsValidationErrorOpen = false;

            if (!_settings.TryAddFolderMapping(FolderNameInput, KeyInput, createDirectory: false, out string errorMessage))
            {
                ShowValidationError(errorMessage);
                return;
            }

            FolderNameInput = string.Empty;
            KeyInput = string.Empty;
        }

        private void ShowValidationError(string message)
        {
            ValidationErrorMessage = message;
            IsValidationErrorOpen = true;
        }

        [RelayCommand]
        private void RemoveMapping(FolderMapping? mapping)
        {
            if (mapping == null) return;
            _settings.RemoveFolderMapping(mapping);
        }

        // --- アップデート確認・What's New ---

        [RelayCommand]
        private async Task CheckUpdateAsync()
        {
            IsUpdateStatusVisible = true;
            UpdateStatusText = "更新を確認中...";

            try
            {
                var mgr = new Velopack.UpdateManager(new Velopack.Sources.GithubSource("https://github.com/WaterfallG1024/Caiqelle-Release", null, false));
                if (mgr.IsInstalled)
                {
                    var updateInfo = await mgr.CheckForUpdatesAsync();
                    if (updateInfo == null)
                    {
                        UpdateStatusText = "最新バージョンです";
                    }
                    else
                    {
                        UpdateStatusText = $"新バージョン {updateInfo.TargetFullRelease.Version} をダウンロード中...";
                        await mgr.DownloadUpdatesAsync(updateInfo);

                        // アプリ終了後に適用処理を予約する
                        mgr.WaitExitThenApplyUpdates(updateInfo, silent: true, restart: false);

                        UpdateStatusText = "更新の準備が完了しました。次回起動時に適用されます。";
                    }
                }
                else
                {
                    UpdateStatusText = "最新バージョンです (開発環境)";
                }
            }
            catch
            {
                UpdateStatusText = "最新バージョンです";
            }
        }

        [RelayCommand]
        private async Task OpenWhatsNewDetailAsync()
        {
            if (_latestRelease != null && !string.IsNullOrEmpty(_latestRelease.HtmlUrl))
            {
                try
                {
                    await Launcher.LaunchUriAsync(new Uri(_latestRelease.HtmlUrl));
                }
                catch { }
            }
        }

        [RelayCommand]
        private void DismissWhatsNew()
        {
            IsWhatsNewVisible = false;
            if (_latestRelease != null && !string.IsNullOrEmpty(_latestRelease.TagName))
            {
                _settings.LastDismissedReleaseTag = _latestRelease.TagName;
                _settings.SaveSettings();
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
                    string currentVersionStr = GetCurrentVersionString();
                    string cleanLatestTag = _latestRelease.TagName.TrimStart('v', 'V');
                    string cleanCurrentTag = currentVersionStr.TrimStart('v', 'V');

                    if (Version.TryParse(cleanLatestTag, out Version? latestVersion) &&
                        Version.TryParse(cleanCurrentTag, out Version? currentVersion) &&
                        latestVersion <= currentVersion)
                    {
                        return;
                    }

                    if (_latestRelease.TagName != _settings.LastDismissedReleaseTag)
                    {
                        _dispatcherQueue?.TryEnqueue(() =>
                        {
                            WhatsNewTitle = $"{_latestRelease.TagName} がリリースされました！";
                            IsWhatsNewVisible = true;
                        });
                    }
                }
            }
            catch { }
        }

        // --- 設定変更フック（ObservableProperty の partial メソッド） ---

        partial void OnIsRewindEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsRewindEnabled = value;
            _settings.SaveSettings();

            if (value)
            {
                _rewindService.Start();
                _notificationService.ShowNotification("リワインドを開始しました", "\uE7C8");
            }
            else
            {
                _rewindService.Stop();
                _notificationService.ShowNotification("リワインドを停止しました", "\uE71A");
            }
        }

        partial void OnIsShiftCapsLockRewindEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsShiftCapsLockRewindEnabled = value;
            _settings.SaveSettings();
            _hookService.IsShiftCapsLockRewindEnabled = value;
        }

        partial void OnSelectedRewindDurationChanged(string value)
        {
            if (_isInitializing || _isUpdatingConstraints || string.IsNullOrEmpty(value)) return;
            if (int.TryParse(value, out int dur))
            {
                _settings.RewindDurationSeconds = dur;
            }
            ApplyRewindSelectionChange();
        }

        partial void OnSelectedRewindQualityChanged(string value)
        {
            if (_isInitializing || _isUpdatingConstraints || string.IsNullOrEmpty(value)) return;
            _settings.RewindQuality = value;
            ApplyRewindSelectionChange();
        }

        private void ApplyRewindSelectionChange()
        {
            UpdateRewindUIConstraints();
            _settings.SaveSettings();
            if (_settings.IsRewindEnabled)
            {
                _rewindService.Restart();
            }
            UpdateRewindStatusCard();
        }

        partial void OnSelectedImageFormatChanged(string value)
        {
            if (_isInitializing || string.IsNullOrEmpty(value)) return;
            _settings.ImageFormat = value;
            _settings.SaveSettings();
        }

        partial void OnSelectedJpegQualityChanged(string value)
        {
            if (_isInitializing || string.IsNullOrEmpty(value)) return;
            if (int.TryParse(value, out int quality))
            {
                _settings.JpegQuality = quality;
                _settings.SaveSettings();
            }
        }

        partial void OnIsHijackPrintScreenChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsHijackPrintScreen = value;
            _settings.SaveSettings();
            _hookService.IsHijackPrintScreen = value;
        }

        partial void OnIsHijackWinShiftSChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsHijackWinShiftS = value;
            _settings.SaveSettings();
            _hookService.IsHijackWinShiftS = value;
        }

        partial void OnIsRunAtStartupChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsRunAtStartup = value;
            _settings.SaveSettings();
            _startupService.SetRunAtStartup(value);
        }

        partial void OnIsAutoCopyToClipboardChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsAutoCopyToClipboard = value;
            _settings.SaveSettings();
        }

        partial void OnIsAutoUpdateEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsAutoUpdateEnabled = value;
            _settings.SaveSettings();
        }

        partial void OnSelectedAccentThemeChanged(AccentTheme value)
        {
            OnPropertyChanged(nameof(SelectedAccentThemeIndex));
            if (_isInitializing) return;
            _settings.AccentTheme = value;
            _settings.SaveSettings();
            _themeService.ApplyTheme(value);
        }

        // --- リワインド設定とハードウェア制約 ---

        private void LoadRewindSettings()
        {
            IsRewindEnabled = _settings.IsRewindEnabled;
            IsShiftCapsLockRewindEnabled = _settings.IsShiftCapsLockRewindEnabled;
            SelectedRewindDuration = _settings.RewindDurationSeconds.ToString();
            SelectedRewindQuality = string.IsNullOrEmpty(_settings.RewindQuality) ? "FulHD" : _settings.RewindQuality;
        }

        private void UpdateRewindUIConstraints()
        {
            if (_isUpdatingConstraints) return;
            _isUpdatingConstraints = true;

            try
            {
                var memStatus = new MEMORYSTATUSEX();
                if (!GlobalMemoryStatusEx(memStatus)) return;

                double totalMemoryGB = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);

                // メモリ帯ごとの許可画質と最大秒数マップ
                Dictionary<string, int> qualityMaxDurations;

                if (totalMemoryGB < 7.0)
                {
                    qualityMaxDurations = new Dictionary<string, int>
                    {
                        { "720p", 10 }
                    };
                    RewindMemoryInfoTitle = "メモリ8GB未満制限";
                    RewindMemoryInfoMessage = "メモリが8GB未満のため、720p・最大10秒に制限されています。";
                    RewindMemoryInfoSeverity = InfoBarSeverity.Warning;
                }
                else if (totalMemoryGB < 14.0)
                {
                    qualityMaxDurations = new Dictionary<string, int>
                    {
                        { "720p", 90 },
                        { "FulHD", 60 }
                    };
                    RewindMemoryInfoTitle = "メモリ16GB未満制限";
                    RewindMemoryInfoMessage = "メモリが16GB未満のため、720pは最大1分30秒、FullHDは最大60秒に制限されています。";
                    RewindMemoryInfoSeverity = InfoBarSeverity.Warning;
                }
                else if (totalMemoryGB < 28.0)
                {
                    qualityMaxDurations = new Dictionary<string, int>
                    {
                        { "720p", 300 },
                        { "FulHD", 180 }
                    };
                    RewindMemoryInfoTitle = "メモリ32GB未満制限";
                    RewindMemoryInfoMessage = "メモリが32GB未満のため、720pは最大5分、FullHDは最大3分に制限されています。";
                    RewindMemoryInfoSeverity = InfoBarSeverity.Informational;
                }
                else if (totalMemoryGB < 56.0)
                {
                    qualityMaxDurations = new Dictionary<string, int>
                    {
                        { "720p", 1200 },
                        { "FulHD", 1200 },
                        { "2K", 300 },
                        { "4K", 180 }
                    };
                    RewindMemoryInfoTitle = "メモリ64GB未満構成";
                    RewindMemoryInfoMessage = "720p/FullHDは最大20分、2Kは最大5分、4Kは最大3分まで設定可能です。";
                    RewindMemoryInfoSeverity = InfoBarSeverity.Informational;
                }
                else
                {
                    qualityMaxDurations = new Dictionary<string, int>
                    {
                        { "720p", 1200 },
                        { "FulHD", 1200 },
                        { "2K", 1200 },
                        { "4K", 300 }
                    };
                    RewindMemoryInfoTitle = "フルパフォーマンス構成";
                    RewindMemoryInfoMessage = "720p〜2Kは最大20分、4Kは最大5分まで設定可能です。";
                    RewindMemoryInfoSeverity = InfoBarSeverity.Success;
                }

                CanEnableRewind = true;

                int screenHeight = _screenshotService.GetVirtualScreenBounds().Height;
                foreach (var option in RewindQualityOptions)
                {
                    int requiredHeight = option.Value switch
                    {
                        "720p" => 720,
                        "FulHD" => 1080,
                        "2K" => 1440,
                        "4K" => 2160,
                        _ => 0
                    };

                    bool isResolutionAllowed = screenHeight >= requiredHeight;
                    bool isMemoryAllowed = qualityMaxDurations.ContainsKey(option.Value);
                    option.IsEnabled = isResolutionAllowed && isMemoryAllowed;

                    if (SelectedRewindQuality == option.Value && !option.IsEnabled)
                    {
                        string fallback = (screenHeight >= 1080 && qualityMaxDurations.ContainsKey("FulHD"))
                            ? "FulHD"
                            : "720p";
                        SelectedRewindQuality = fallback;
                        _settings.RewindQuality = fallback;
                    }
                }

                string currentQuality = string.IsNullOrEmpty(SelectedRewindQuality) ? "FulHD" : SelectedRewindQuality;
                int currentMaxDuration = qualityMaxDurations.TryGetValue(currentQuality, out int maxForQuality)
                    ? maxForQuality
                    : 10;

                foreach (var option in RewindDurationOptions)
                {
                    if (int.TryParse(option.Value, out int dur))
                    {
                        option.IsEnabled = dur <= currentMaxDuration;
                        if (SelectedRewindDuration == option.Value && !option.IsEnabled)
                        {
                            SelectedRewindDuration = currentMaxDuration.ToString();
                            _settings.RewindDurationSeconds = currentMaxDuration;
                        }
                    }
                }
            }
            finally
            {
                _isUpdatingConstraints = false;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
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
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    }
}
