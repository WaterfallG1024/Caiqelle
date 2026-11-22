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
        Action,
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
    /// アプリテーマ（システム／ライト／ダーク）選択用の表示モデル
    /// </summary>
    public class AppThemeOption
    {
        public string Label { get; }
        public AppThemeMode Value { get; }

        public AppThemeOption(AppThemeMode value, string label)
        {
            Value = value;
            Label = label;
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
        private readonly StickyTextService _stickyTextService;
        private readonly ActionService? _actionService;
        private readonly UpdateService? _updateService;
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
        [NotifyPropertyChangedFor(nameof(IsActionTabVisible))]
        [NotifyPropertyChangedFor(nameof(IsSettingsTabVisible))]
        [NotifyPropertyChangedFor(nameof(HeaderTitle))]
        public partial MainTab CurrentTab { get; set; } = MainTab.Home;

        public bool IsHomeTabVisible => CurrentTab == MainTab.Home;
        public bool IsFolderTabVisible => CurrentTab == MainTab.Folder;
        public bool IsRewindTabVisible => CurrentTab == MainTab.Rewind;
        public bool IsActionTabVisible => CurrentTab == MainTab.Action;
        public bool IsSettingsTabVisible => CurrentTab == MainTab.Settings;

        public string HeaderTitle => CurrentTab switch
        {
            MainTab.Home => "クイック アクセス",
            MainTab.Folder => "フォルダ管理",
            MainTab.Rewind => "リワインド",
            MainTab.Action => "アクション",
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

        public string FolderMappingCountText => $"{Mappings.Count}/{SettingsManager.MaxFolderMappingCount}";
        public bool CanAddFolderMapping => Mappings.Count < SettingsManager.MaxFolderMappingCount;
        public bool IsFolderLimitReached => !CanAddFolderMapping;

        [ObservableProperty]
        public partial string FolderNameInput { get; set; } = string.Empty;

        partial void OnFolderNameInputChanged(string value)
        {
            IsValidationErrorOpen = false;
        }

        [ObservableProperty]
        public partial string KeyInput { get; set; } = string.Empty;

        partial void OnKeyInputChanged(string value)
        {
            IsValidationErrorOpen = false;
        }

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
        public partial bool IsRewindTileCompressionEnabled { get; set; } = false;

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
        public partial bool IsIgnoreOutsideSelectionClick { get; set; } = true;

        [ObservableProperty]
        public partial bool IsStickyTextStealthMode { get; set; } = false;

        [ObservableProperty]
        public partial bool IsStickyTextSaveEnabled { get; set; } = false;

        [ObservableProperty]
        public partial bool IsStickyTextRestoreOnOpen { get; set; } = true;

        [ObservableProperty]
        public partial bool IsAutoUpdateEnabled { get; set; }

        [ObservableProperty]
        public partial string VersionText { get; set; } = "v0.1.0";

        [ObservableProperty]
        public partial string UpdateStatusText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsUpdateStatusVisible { get; set; }

        [ObservableProperty]
        public partial bool IsRestartToUpdateVisible { get; set; }

        [ObservableProperty]
        public partial string PendingUpdateBannerText { get; set; } = string.Empty;

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

        public ObservableCollection<AppThemeOption> AppThemeOptions { get; } = new()
        {
            new AppThemeOption(AppThemeMode.System, "システム設定に従う"),
            new AppThemeOption(AppThemeMode.Light, "ライト"),
            new AppThemeOption(AppThemeMode.Dark, "ダーク")
        };

        [ObservableProperty]
        public partial AppThemeMode SelectedAppTheme { get; set; } = AppThemeMode.System;

        public int SelectedAppThemeIndex
        {
            get => (int)SelectedAppTheme;
            set
            {
                if (value >= 0 && value < AppThemeOptions.Count)
                {
                    SelectedAppTheme = AppThemeOptions[value].Value;
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
            ThemeService themeService,
            StickyTextService stickyTextService,
            ActionService? actionService = null,
            UpdateService? updateService = null)
        {
            _settings = settings;
            _rewindService = rewindService;
            _hookService = hookService;
            _screenshotService = screenshotService;
            _startupService = startupService;
            _notificationService = notificationService;
            _themeService = themeService;
            _stickyTextService = stickyTextService;
            _actionService = actionService;
            _updateService = updateService;
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            foreach (var info in ThemeService.AvailableThemes)
            {
                AccentThemeOptions.Add(new AccentThemeOption(info));
            }

            InitializeFromSettings();

            _rewindService.StateChanged += RewindService_StateChanged;
            _settings.MappingsChanged += Settings_MappingsChanged;
            Mappings.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(FolderMappingCountText));
                OnPropertyChanged(nameof(CanAddFolderMapping));
                OnPropertyChanged(nameof(IsFolderLimitReached));
            };
            if (_updateService != null)
            {
                _updateService.UpdateDownloaded += UpdateService_UpdateDownloaded;
                if (_updateService.HasPendingUpdate)
                {
                    ApplyPendingUpdateUi(_updateService.PendingUpdateVersion ?? string.Empty);
                }
            }
            _ = CheckAndShowWhatsNewBannerAsync();
        }

        private void UpdateService_UpdateDownloaded(string version)
        {
            _dispatcherQueue?.TryEnqueue(() => ApplyPendingUpdateUi(version));
        }

        private void ApplyPendingUpdateUi(string version)
        {
            IsUpdateStatusVisible = true;
            UpdateStatusText = string.IsNullOrEmpty(version)
                ? "更新の準備が完了しました。"
                : $"新バージョン ({version}) の準備が完了しました。";
            PendingUpdateBannerText = string.IsNullOrEmpty(version)
                ? "アップデートの適用準備が完了しました"
                : $"新バージョン ({version}) の適用準備が完了しました";
            IsRestartToUpdateVisible = true;
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
                IsIgnoreOutsideSelectionClick = _settings.IsIgnoreOutsideSelectionClick;
                IsStickyTextStealthMode = _settings.IsStickyTextStealthMode;
                IsStickyTextSaveEnabled = _settings.IsStickyTextSaveEnabled;
                IsStickyTextRestoreOnOpen = _settings.IsStickyTextRestoreOnOpen;
                IsAutoUpdateEnabled = _settings.IsAutoUpdateEnabled;

                IsActionAutoDetectEnabled = _settings.IsActionAutoDetectEnabled;
                IsActionOnlyOnBrowsers = _settings.IsActionOnlyOnBrowsers;
                ActionRestoreClipboardAfterFill = _settings.ActionRestoreClipboardAfterFill;
                ActionMappings.Clear();
                foreach (var actionItem in _settings.ActionMappings)
                {
                    ActionMappings.Add(actionItem);
                }

                VersionText = $"v{GetCurrentVersionString()}";

                SelectedImageFormat = string.IsNullOrEmpty(_settings.ImageFormat) ? "PNG" : _settings.ImageFormat;
                SelectedJpegQuality = _settings.JpegQuality.ToString();
                SelectedAccentTheme = _settings.AccentTheme;
                SelectedAppTheme = _settings.AppThemeMode;

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
        private void NavigateToAction() => SwitchTab(MainTab.Action);

        [RelayCommand]
        private void NavigateToSettings() => SwitchTab(MainTab.Settings);

        // --- Action タブ設定・コマンド ---

        public ObservableCollection<ActionFieldMapping> ActionMappings { get; } = new();

        [ObservableProperty]
        public partial bool IsActionAutoDetectEnabled { get; set; }

        [ObservableProperty]
        public partial bool IsActionOnlyOnBrowsers { get; set; } = true;

        [ObservableProperty]
        public partial bool ActionRestoreClipboardAfterFill { get; set; } = true;

        [ObservableProperty]
        public partial string NewActionKeywords { get; set; } = string.Empty;

        partial void OnNewActionKeywordsChanged(string value)
        {
            IsActionValidationErrorOpen = false;
        }

        [ObservableProperty]
        public partial string NewActionValue { get; set; } = string.Empty;

        partial void OnNewActionValueChanged(string value)
        {
            IsActionValidationErrorOpen = false;
        }

        [ObservableProperty]
        public partial bool IsActionValidationErrorOpen { get; set; }

        [ObservableProperty]
        public partial string ActionValidationErrorMessage { get; set; } = string.Empty;

        partial void OnIsActionAutoDetectEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsActionAutoDetectEnabled = value;
            _settings.SaveSettings();
            if (value)
            {
                _actionService?.ClearCompletedHistory();
                _actionService?.StartMonitoring();
            }
        }

        partial void OnIsActionOnlyOnBrowsersChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsActionOnlyOnBrowsers = value;
            _settings.SaveSettings();
        }

        partial void OnActionRestoreClipboardAfterFillChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.ActionRestoreClipboardAfterFill = value;
            _settings.SaveSettings();
        }

        [RelayCommand]
        private void AddActionMapping()
        {
            IsActionValidationErrorOpen = false;
            string keywords = NewActionKeywords?.Trim() ?? string.Empty;
            string value = NewActionValue?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(keywords) || string.IsNullOrEmpty(value))
            {
                ActionValidationErrorMessage = "ラベルキーワードと入力値を両方入力してください。";
                IsActionValidationErrorOpen = true;
                return;
            }

            // 既存項目との完全一致重複をチェック
            foreach (var existing in ActionMappings)
            {
                if (string.Equals(existing.Keywords?.Trim(), keywords, StringComparison.OrdinalIgnoreCase))
                {
                    ActionValidationErrorMessage = "同じラベルキーワードがすでに登録されています。";
                    IsActionValidationErrorOpen = true;
                    return;
                }
            }

            var mapping = new ActionFieldMapping
            {
                Keywords = keywords,
                PlainValue = value
            };

            _settings.ActionMappings.Add(mapping);
            _settings.SaveSettings();
            ActionMappings.Add(mapping);

            NewActionKeywords = string.Empty;
            NewActionValue = string.Empty;
            _actionService?.ClearCompletedHistory();
        }

        [RelayCommand]
        private void DeleteActionMapping(ActionFieldMapping? mapping)
        {
            if (mapping == null) return;
            _settings.ActionMappings.Remove(mapping);
            _settings.SaveSettings();
            ActionMappings.Remove(mapping);
        }

        public bool TryUpdateActionMappingDirect(ActionFieldMapping target, string keywordsInput, string valueInput, out string errorMessage)
        {
            errorMessage = string.Empty;
            string keywords = keywordsInput?.Trim() ?? string.Empty;
            string value = valueInput?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(keywords) || string.IsNullOrEmpty(value))
            {
                errorMessage = "ラベルキーワードと入力値を両方入力してください。";
                return false;
            }

            target.Keywords = keywords;
            target.PlainValue = value;
            int idx = ActionMappings.IndexOf(target);
            if (idx >= 0)
            {
                ActionMappings[idx] = target;
            }
            _settings.SaveSettings();
            _actionService?.ClearCompletedHistory();
            return true;
        }

        public bool TryUpdateFolderMappingDirect(FolderMapping target, string folderInput, string keyInput, out string errorMessage)
        {
            return _settings.TryUpdateFolderMapping(target, folderInput, keyInput, out errorMessage);
        }

        [RelayCommand]
        private void ResetActionCompletedHistory()
        {
            _actionService?.ClearCompletedHistory();
            _notificationService.ShowNotification("入力済み履歴をリセットしました", "\uE72C");
        }

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
        private Task CaptureLivePinAsync() => ExecuteCaptureAsync(CapturedKey.L);

        [RelayCommand]
        private Task CaptureOcrAsync() => ExecuteCaptureAsync(CapturedKey.E);

        [RelayCommand]
        private void OpenStickyText()
        {
            var mainWin = ((App)Microsoft.UI.Xaml.Application.Current).MainWindow;
            if (mainWin != null) Caiqelle.Views.WindowExtensions.Hide(mainWin);
            _stickyTextService.OpenStickyText();
        }

        [RelayCommand]
        private void OpenStickyTextList()
        {
            var mainWin = ((App)Microsoft.UI.Xaml.Application.Current).MainWindow;
            if (mainWin != null) Caiqelle.Views.WindowExtensions.Hide(mainWin);
            _stickyTextService.OpenStickyTextList();
        }

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

            if (_updateService != null)
            {
                var (hasUpdate, message) = await _updateService.CheckAndDownloadManualAsync(progress =>
                {
                    _dispatcherQueue?.TryEnqueue(() => UpdateStatusText = progress);
                });

                UpdateStatusText = message;
                if (hasUpdate)
                {
                    IsRestartToUpdateVisible = true;
                }
                return;
            }

            UpdateStatusText = "最新バージョンです (開発環境)";
        }

        [RelayCommand]
        private void RestartToUpdate()
        {
            if (_updateService?.HasPendingUpdate == true)
            {
                _updateService.ApplyUpdateAndRestart();
            }
            else
            {
                _updateService?.RestartApplication();
            }
        }

        [RelayCommand]
        private async Task OpenReleaseNotesAsync()
        {
            string targetUrl = "https://github.com/WaterfallG1024/Caiqelle-Release/releases";
            if (_latestRelease == null)
            {
                try
                {
                    var releaseService = new GitHubReleaseService();
                    _latestRelease = await releaseService.GetLatestReleaseAsync();
                }
                catch { }
            }

            if (_latestRelease != null &&
                Uri.TryCreate(_latestRelease.HtmlUrl, UriKind.Absolute, out var releaseUri) &&
                releaseUri.Scheme == Uri.UriSchemeHttps)
            {
                targetUrl = _latestRelease.HtmlUrl;
            }

            if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
            {
                try
                {
                    await Launcher.LaunchUriAsync(uri);
                }
                catch { }
            }
        }

        [RelayCommand]
        private async Task OpenWhatsNewDetailAsync() => await OpenReleaseNotesAsync();

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
                // リリースノートは通知バナーではなく設定タブの「リリースノートを見る」ボタンから参照するため、ここではURL情報のみ事前取得
                var releaseService = new GitHubReleaseService();
                _latestRelease = await releaseService.GetLatestReleaseAsync();
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

        partial void OnIsRewindTileCompressionEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsRewindTileCompressionEnabled = value;
            _settings.SaveSettings();
            if (_settings.IsRewindEnabled)
            {
                _rewindService.Restart(clearBuffer: false);
            }
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
                _rewindService.Restart(clearBuffer: false);
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

        partial void OnIsIgnoreOutsideSelectionClickChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsIgnoreOutsideSelectionClick = value;
            _settings.SaveSettings();
        }

        partial void OnIsStickyTextStealthModeChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsStickyTextStealthMode = value;
            _settings.SaveSettings();
            _stickyTextService.SetAllStealthMode(value);
        }

        partial void OnIsStickyTextSaveEnabledChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsStickyTextSaveEnabled = value;
            _settings.SaveSettings();
            _stickyTextService.SetSaveEnabled(value);
        }

        partial void OnIsStickyTextRestoreOnOpenChanged(bool value)
        {
            if (_isInitializing) return;
            _settings.IsStickyTextRestoreOnOpen = value;
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
            _themeService.ApplyTheme(value, SelectedAppTheme);
        }

        partial void OnSelectedAppThemeChanged(AppThemeMode value)
        {
            OnPropertyChanged(nameof(SelectedAppThemeIndex));
            if (_isInitializing) return;
            _settings.AppThemeMode = value;
            _settings.SaveSettings();
            _themeService.ApplyAppTheme(value);
        }

        // --- リワインド設定とハードウェア制約 ---

        private void LoadRewindSettings()
        {
            IsRewindEnabled = _settings.IsRewindEnabled;
            IsShiftCapsLockRewindEnabled = _settings.IsShiftCapsLockRewindEnabled;
            IsRewindTileCompressionEnabled = _settings.IsRewindTileCompressionEnabled;
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
