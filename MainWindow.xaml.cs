using Caiqelle.Services;
using Caiqelle.ViewModels;
using Caiqelle.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Numerics;
using Windows.System;

namespace Caiqelle
{
    public sealed partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            var app = (App)Application.Current;
            ViewModel = app.Services.GetRequiredService<MainViewModel>();
            ViewModel.RequestHideWindow = () => this.Hide();
            ViewModel.GetCaptureManager = () => ((App)Application.Current).CaptureManager;
            ViewModel.TabChanged += ViewModel_TabChanged;

            this.InitializeComponent();

            // 完全透過ベースに RootGrid の半透明テーマグラデーション背景を重ねる
            this.SystemBackdrop = new WinUIEx.TransparentTintBackdrop(Microsoft.UI.Colors.Transparent);

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            ConfigureWindowChrome();

            this.Closed += MainWindow_Closed;
            this.Activated += MainWindow_Activated;

            UpdateNavButtonHighlight();
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
            appWindow.Resize(new Windows.Graphics.SizeInt32(360, 500));

            // ウィンドウの角を丸くする
            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_ROUND = 2;
            Views.NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_ROUND, sizeof(int));

            // ウィンドウの白い枠線を完全に削除
            int DWMWA_BORDER_COLOR = 34;
            int DWM_BORDER_COLOR_NONE = -2;
            Views.NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref DWM_BORDER_COLOR_NONE, sizeof(int));

            // フレームをクライアント領域に拡張して枠線を非表示にする
            var margins = new Views.NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            Views.NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

            // ウィンドウスタイルから枠線を除去
            Views.NativeMethods.RemoveWindowBorder(hwnd);
        }

        private void ViewModel_TabChanged(object? sender, EventArgs e)
        {
            UpdateNavButtonHighlight();
            ContentScrollViewer?.ChangeView(null, 0, null, true);
        }

        private void UpdateNavButtonHighlight()
        {
            Button? activeBtn = ViewModel.CurrentTab switch
            {
                MainTab.Home => NavHomeBtn,
                MainTab.Folder => NavFolderBtn,
                MainTab.Settings => NavSettingsBtn,
                _ => null
            };

            var activeBg = (Application.Current.Resources.TryGetValue("M3PrimaryContainerGlassBrush", out var brush) && brush is Microsoft.UI.Xaml.Media.Brush m3Brush)
                ? m3Brush
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x44, 0x00, 0xFF, 0x7F));
            var transparentBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

            var activeFg = (Application.Current.Resources.TryGetValue("M3PrimaryBrush", out var pBrush) && pBrush is Microsoft.UI.Xaml.Media.Brush primaryBrush)
                ? primaryBrush
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
            var defaultFg = (Application.Current.Resources.TryGetValue("AfterGlowTextPrimaryBrush", out var dBrush) && dBrush is Microsoft.UI.Xaml.Media.Brush textBrush)
                ? textBrush
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);

            ApplyNavButtonVisual(NavHomeBtn, NavHomeBtn == activeBtn, activeBg, transparentBg, activeFg, defaultFg);
            ApplyNavButtonVisual(NavFolderBtn, NavFolderBtn == activeBtn, activeBg, transparentBg, activeFg, defaultFg);
            ApplyNavButtonVisual(NavSettingsBtn, NavSettingsBtn == activeBtn, activeBg, transparentBg, activeFg, defaultFg);
        }

        private static void ApplyNavButtonVisual(
            Button? btn,
            bool isActive,
            Microsoft.UI.Xaml.Media.Brush activeBg,
            Microsoft.UI.Xaml.Media.Brush transparentBg,
            Microsoft.UI.Xaml.Media.Brush activeFg,
            Microsoft.UI.Xaml.Media.Brush defaultFg)
        {
            if (btn == null) return;
            btn.Background = isActive ? activeBg : transparentBg;
            if (btn.Content is FontIcon icon)
            {
                icon.Foreground = isActive ? activeFg : defaultFg;
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
