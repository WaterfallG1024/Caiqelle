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
            // TODO: Caiqelle用アイコンを用意したら設定する
            // appWindow.SetIcon("Assets\\favicon.ico");

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }
            double dpiScale = Views.NativeMethods.GetDpiForWindow(hwnd) / 96.0;
            appWindow.Resize(new Windows.Graphics.SizeInt32((int)(500 * dpiScale), (int)(580 * dpiScale)));

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
                this.Hide();
            }
            else
            {
                // ナビゲーションタブへの白枠フォーカスを防ぎ、ショートカットキーを即座に有効化する
                RootGrid.Focus(FocusState.Programmatic);
            }
        }

        private void Grid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            // テキストボックス等の入力中の場合は無視する
            if (e.OriginalSource is TextBox || e.OriginalSource is PasswordBox) return;

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
    }
}
