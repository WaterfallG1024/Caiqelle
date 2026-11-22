using Caiqelle.Services;
using Caiqelle.ViewModels;
using Caiqelle.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using Windows.System;

namespace Caiqelle
{
    public sealed partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; }
        private bool _isSyncingNav = false;
        private bool _isModalDialogOpen = false;

        public MainWindow()
        {
            var app = (App)Application.Current;
            ViewModel = app.Services.GetRequiredService<MainViewModel>();
            ViewModel.RequestHideWindow = () => this.Hide();
            ViewModel.GetCaptureManager = () => ((App)Application.Current).CaptureManager;
            ViewModel.TabChanged += ViewModel_TabChanged;

            this.InitializeComponent();

            // Fluent 2 準拠の Mica Backdrop（BaseAlt）を適用
            this.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            app.Services.GetRequiredService<ThemeService>().RegisterWindow(this, RootGrid);

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            ConfigureWindowChrome();

            this.Closed += MainWindow_Closed;
            this.Activated += MainWindow_Activated;

            SyncTopNavSelection();
        }

        private void ConfigureWindowChrome()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Caiqelle.ico"));

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }
            double dpiScale = Views.NativeMethods.GetDpiForWindow(hwnd) / 96.0;
            appWindow.Resize(new Windows.Graphics.SizeInt32((int)(560 * dpiScale), (int)(580 * dpiScale)));

            // Windows 11 標準の角丸を適用
            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_ROUND = 2;
            Views.NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_ROUND, sizeof(int));

            // フレームをクライアント領域に拡張して MicaBackdrop をウィンドウ全面へ表示
            var margins = new Views.NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            Views.NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            Views.NativeMethods.RemoveWindowBorder(hwnd);
        }

        private void ViewModel_TabChanged(object? sender, EventArgs e)
        {
            SyncTopNavSelection();
            ContentScrollViewer?.ChangeView(null, 0, null, true);
        }

        private void SyncTopNavSelection()
        {
            if (TopNavView == null) return;
            _isSyncingNav = true;
            try
            {
                TopNavView.SelectedItem = ViewModel.CurrentTab switch
                {
                    MainTab.Home => NavHomeItem,
                    MainTab.Folder => NavFolderItem,
                    MainTab.Rewind => NavRewindItem,
                    MainTab.Action => NavActionItem,
                    MainTab.Settings => NavSettingsItem,
                    _ => NavHomeItem
                };
            }
            finally
            {
                _isSyncingNav = false;
            }
        }

        private void TopNavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (_isSyncingNav) return;
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                switch (tag)
                {
                    case "Home":
                        ViewModel.NavigateToHomeCommand.Execute(null);
                        break;
                    case "Folder":
                        ViewModel.NavigateToFolderCommand.Execute(null);
                        break;
                    case "Rewind":
                        ViewModel.NavigateToRewindCommand.Execute(null);
                        break;
                    case "Action":
                        ViewModel.NavigateToActionCommand.Execute(null);
                        break;
                    case "Settings":
                        ViewModel.NavigateToSettingsCommand.Execute(null);
                        break;
                }
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
                if (!_isModalDialogOpen)
                {
                    this.Hide();
                }
            }
            else
            {
                // ナビゲーションタブへの白枠フォーカスを防ぎ、ショートカットキーを即座に有効化する
                RootGrid.Focus(FocusState.Programmatic);
            }
        }

        private void FolderInput_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter && ViewModel.CanAddFolderMapping)
            {
                ViewModel.AddMappingCommand.Execute(null);
                e.Handled = true;
            }
        }

        private void ActionInput_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                ViewModel.AddActionMappingCommand.Execute(null);
                e.Handled = true;
            }
        }

        private void Grid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            // テキストボックス等の入力中またはモーダルダイアログ表示中は無視する
            if (_isModalDialogOpen || e.OriginalSource is TextBox || e.OriginalSource is PasswordBox) return;

            if (ViewModel.CurrentTab == MainTab.Home)
            {
                switch (e.Key)
                {
                    case VirtualKey.C:
                        ViewModel.CaptureRegionCommand.Execute(null);
                        e.Handled = true;
                        break;
                    case VirtualKey.W:
                        ViewModel.CaptureWindowCommand.Execute(null);
                        e.Handled = true;
                        break;
                    case VirtualKey.F:
                        ViewModel.CaptureFullscreenCommand.Execute(null);
                        e.Handled = true;
                        break;
                    case VirtualKey.P:
                        ViewModel.CapturePinCommand.Execute(null);
                        e.Handled = true;
                        break;
                    case VirtualKey.L:
                        ViewModel.CaptureLivePinCommand.Execute(null);
                        e.Handled = true;
                        break;
                    case VirtualKey.E:
                        ViewModel.CaptureOcrCommand.Execute(null);
                        e.Handled = true;
                        break;
                    case VirtualKey.R:
                        ViewModel.NavigateToRewindCommand.Execute(null);
                        e.Handled = true;
                        break;
                    case VirtualKey.S:
                        ViewModel.OpenStickyTextCommand.Execute(null);
                        e.Handled = true;
                        break;
                    case VirtualKey.V:
                        ViewModel.OpenStickyTextListCommand.Execute(null);
                        e.Handled = true;
                        break;
                    case VirtualKey.Escape:
                        this.Hide();
                        e.Handled = true;
                        break;
                }
            }
            else if (e.Key == VirtualKey.Escape)
            {
                this.Hide();
                e.Handled = true;
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

        private void RewindCombo_DropDownOpened(object? sender, object e)
        {
            if (sender is not ComboBox combo) return;
            ApplyComboItemEnabledState(combo);

            // 初回ドロップダウン展開時は ComboBoxItem コンテナが未生成の場合があるため、レイアウト確定後にも再適用する
            combo.DispatcherQueue?.TryEnqueue(() =>
            {
                combo.UpdateLayout();
                ApplyComboItemEnabledState(combo);
            });
        }

        private static void ApplyComboItemEnabledState(ComboBox combo)
        {
            foreach (var item in combo.Items)
            {
                if (item is SelectableOption option && combo.ContainerFromItem(item) is ComboBoxItem container)
                {
                    container.IsEnabled = option.IsEnabled;
                }
            }
        }

        private async void EditFolderMappingBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is FolderMapping mapping)
            {
                if (_isModalDialogOpen || RootGrid.XamlRoot == null) return;
                _isModalDialogOpen = true;
                try
                {
                    var folderBox = new TextBox
                    {
                        Header = "フォルダ名",
                        Text = mapping.FolderName,
                        PlaceholderText = "フォルダ名"
                    };
                    var keyBox = new TextBox
                    {
                        Header = "ショートカットキー (1文字)",
                        Text = mapping.Key,
                        PlaceholderText = "キー",
                        MaxLength = 1,
                        Width = 120,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    var errorText = new TextBlock
                    {
                        Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Visibility = Visibility.Collapsed
                    };

                    var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
                    panel.Children.Add(folderBox);
                    panel.Children.Add(keyBox);
                    panel.Children.Add(errorText);

                    var dialog = new ContentDialog
                    {
                        Title = "フォルダの編集",
                        Content = panel,
                        PrimaryButtonText = "保存",
                        CloseButtonText = "キャンセル",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = RootGrid.XamlRoot,
                        RequestedTheme = RootGrid.ActualTheme
                    };

                    dialog.PrimaryButtonClick += (_, args) =>
                    {
                        if (!ViewModel.TryUpdateFolderMappingDirect(mapping, folderBox.Text, keyBox.Text, out string err))
                        {
                            errorText.Text = err;
                            errorText.Visibility = Visibility.Visible;
                            args.Cancel = true;
                        }
                    };

                    await dialog.ShowAsync();
                }
                finally
                {
                    _isModalDialogOpen = false;
                }
            }
        }

        private async void EditActionMappingBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ActionFieldMapping mapping)
            {
                if (_isModalDialogOpen || RootGrid.XamlRoot == null) return;
                _isModalDialogOpen = true;
                try
                {
                    var keywordsBox = new TextBox
                    {
                        Header = "ラベルキーワード（, または ， で複数指定）",
                        Text = mapping.Keywords,
                        PlaceholderText = "例: 名前,氏名,お名前"
                    };
                    var valueBox = new TextBox
                    {
                        Header = "入力値",
                        Text = mapping.PlainValue,
                        PlaceholderText = "入力値"
                    };
                    var errorText = new TextBlock
                    {
                        Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Visibility = Visibility.Collapsed
                    };

                    var panel = new StackPanel { Spacing = 12, MinWidth = 340 };
                    panel.Children.Add(keywordsBox);
                    panel.Children.Add(valueBox);
                    panel.Children.Add(errorText);

                    var dialog = new ContentDialog
                    {
                        Title = "登録項目の編集",
                        Content = panel,
                        PrimaryButtonText = "保存",
                        CloseButtonText = "キャンセル",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = RootGrid.XamlRoot,
                        RequestedTheme = RootGrid.ActualTheme
                    };

                    dialog.PrimaryButtonClick += (_, args) =>
                    {
                        if (!ViewModel.TryUpdateActionMappingDirect(mapping, keywordsBox.Text, valueBox.Text, out string err))
                        {
                            errorText.Text = err;
                            errorText.Visibility = Visibility.Visible;
                            args.Cancel = true;
                        }
                    };

                    await dialog.ShowAsync();
                }
                finally
                {
                    _isModalDialogOpen = false;
                }
            }
        }

        private async void RemoveFolderMappingBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is FolderMapping mapping)
            {
                bool confirmed = await ShowConfirmDialogAsync(
                    "フォルダの登録を解除しますか？",
                    "この操作では Caiqelle からのリンク（登録）のみが解除され、PC 上のフォルダや画像ファイル自体は削除されません。");
                if (confirmed)
                {
                    ViewModel.RemoveMappingCommand.Execute(mapping);
                }
            }
        }

        private async void DeleteActionMappingBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ActionFieldMapping mapping)
            {
                bool confirmed = await ShowConfirmDialogAsync(
                    "登録項目を削除しますか？",
                    "このキーワードと登録された入力値を削除します。この操作は元に戻せません。");
                if (confirmed)
                {
                    ViewModel.DeleteActionMappingCommand.Execute(mapping);
                }
            }
        }

        private async System.Threading.Tasks.Task<bool> ShowConfirmDialogAsync(string title, string message)
        {
            if (_isModalDialogOpen || RootGrid.XamlRoot == null) return false;
            _isModalDialogOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 13,
                        LineHeight = 20
                    },
                    PrimaryButtonText = "はい",
                    CloseButtonText = "いいえ",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = RootGrid.XamlRoot,
                    RequestedTheme = RootGrid.ActualTheme
                };

                var result = await dialog.ShowAsync();
                return result == ContentDialogResult.Primary;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] ShowConfirmDialog error: {ex.Message}");
                return false;
            }
            finally
            {
                _isModalDialogOpen = false;
            }
        }

        private async void ShowTermsDialogBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isModalDialogOpen || RootGrid.XamlRoot == null) return;
            _isModalDialogOpen = true;
            try
            {
                var contentText = new TextBlock
                {
                    Text = LegalNoticeText,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    LineHeight = 20,
                    IsTextSelectionEnabled = true
                };

                var scrollViewer = new ScrollViewer
                {
                    Content = contentText,
                    MaxHeight = 360,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Padding = new Thickness(0, 4, 12, 4)
                };

                var dialog = new ContentDialog
                {
                    Title = "利用規約・免責事項",
                    Content = scrollViewer,
                    CloseButtonText = "閉じる",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = RootGrid.XamlRoot,
                    RequestedTheme = RootGrid.ActualTheme
                };

                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] ShowTermsDialog error: {ex.Message}");
            }
            finally
            {
                _isModalDialogOpen = false;
            }
        }

        private const string LegalNoticeText =
@"【利用規約】
1. ユーザーは、法令および関係各所・各サービスの規約を遵守したうえで、本ソフトウェア（Caiqelle）を利用するものとします。
2. 本ソフトウェアを不正な目的（第三者の権利侵害、スパム行為、不正アクセス、アクセス制御の回避、その他法令または公序良俗に反する行為等）で利用することを禁止します。
3. 本ソフトウェアの利用に際して発生するデータおよび環境・設定の管理は、ユーザー自身の責任で行うものとします。

【免責事項】
1. 本ソフトウェアは現状有姿（AS-IS）で提供され、動作の完全性、正確性、安全性、特定目的への適合性についていかなる保証も行いません。
2. 本ソフトウェアの利用または利用不能によって生じたいかなる損害（直接的・間接的損害、データ損失、誤動作、第三者との紛争等を含むがこれに限らない）についても、開発者は一切の責任を負いません。
3. 本ソフトウェアの内容・仕様および本規約は、予告なく変更または提供を終了する場合があります。

【商標・権利表記】
本ソフトウェアおよび関連資料に記載されている会社名・製品名・サービス名は、各社の商標または登録商標です。本ソフトウェアはこれら各社と提携、協賛、または承認されたものではありません。";
    }
}
