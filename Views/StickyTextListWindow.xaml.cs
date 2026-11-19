using Caiqelle.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT.Interop;

namespace Caiqelle.Views
{
    public sealed class StickyTextListItemView
    {
        public string Id { get; init; } = string.Empty;
        public bool IsOpen { get; init; }
        public string PreviewText { get; init; } = string.Empty;
        public string StatusLabel { get; init; } = string.Empty;
        public Brush StatusBackground { get; init; } = new SolidColorBrush(Colors.Transparent);
        public Brush StatusForeground { get; init; } = new SolidColorBrush(Colors.White);
        public Visibility StealthBadgeVisibility { get; init; } = Visibility.Collapsed;
        public string ToggleButtonLabel { get; init; } = string.Empty;
        public string ToggleButtonToolTip { get; init; } = string.Empty;
        public Visibility DeleteButtonVisibility { get; init; } = Visibility.Visible;
    }

    public sealed partial class StickyTextListWindow : Window
    {
        private readonly StickyTextService _service;
        private readonly IntPtr _hwnd;
        private readonly AppWindow _appWindow;

        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        public StickyTextListWindow(StickyTextService service)
        {
            _service = service;
            this.InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            if (((App)Application.Current).Services.GetService(typeof(ThemeService)) is ThemeService themeService)
            {
                themeService.RegisterWindow(this, RootGrid);
            }

            _hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.IsShownInSwitchers = false;

            ConfigureWindowChrome();
            CenterOnCursorDisplay();

            _service.ItemsChanged += OnItemsChanged;
            this.Closed += (s, e) => _service.ItemsChanged -= OnItemsChanged;

            RefreshList();
        }

        private void ConfigureWindowChrome()
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
            }

            // ウィンドウの角丸を適用
            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_ROUND = 2;
            NativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_ROUND, sizeof(int));

            // 画面共有・録画から一覧ウィンドウを除外
            SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);

            var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            NativeMethods.RemoveWindowBorder(_hwnd);
        }

        public void ShowAndFocus()
        {
            CenterOnCursorDisplay();
            RefreshList();
            _appWindow.Show();
            this.Activate();
            NativeMethods.SetForegroundWindow(_hwnd);
        }

        private void CenterOnCursorDisplay()
        {
            double dpiScale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
            int targetWidth = (int)(430 * dpiScale);
            int targetHeight = (int)(500 * dpiScale);
            _appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));

            NativeMethods.GetCursorPos(out NativeMethods.POINT cursorPos);
            var displayArea = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(cursorPos.X, cursorPos.Y), DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var work = displayArea.WorkArea;
                int x = work.X + (work.Width - targetWidth) / 2;
                int y = work.Y + (work.Height - targetHeight) / 2;
                _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
            }
        }

        private void OnItemsChanged()
        {
            DispatcherQueue.TryEnqueue(RefreshList);
        }

        private void RefreshList()
        {
            var entries = _service.GetListEntries(out bool isSaveEnabled);
            var activeBg = GetResourceBrush("M3PrimaryContainerGlassBrush");
            var activeFg = GetResourceBrush("M3PrimaryBrush");
            var inactiveBg = GetResourceBrush("SubtleFillColorSecondaryBrush");
            var inactiveFg = GetResourceBrush("AfterGlowTextSecondaryBrush");

            var viewItems = new List<StickyTextListItemView>(entries.Count);
            foreach (var entry in entries)
            {
                string preview = string.IsNullOrWhiteSpace(entry.Data.Text)
                    ? "（テキスト未入力）"
                    : entry.Data.Text.Replace("\r", " ").Replace("\n", " ").Trim();

                viewItems.Add(new StickyTextListItemView
                {
                    Id = entry.Data.Id,
                    IsOpen = entry.IsCurrentlyOpen,
                    PreviewText = preview,
                    StatusLabel = entry.IsCurrentlyOpen ? "表示中" : "非表示",
                    StatusBackground = entry.IsCurrentlyOpen ? activeBg : inactiveBg,
                    StatusForeground = entry.IsCurrentlyOpen ? activeFg : inactiveFg,
                    StealthBadgeVisibility = entry.Data.IsStealth ? Visibility.Visible : Visibility.Collapsed,
                    ToggleButtonLabel = entry.IsCurrentlyOpen ? "隠す" : "表示",
                    ToggleButtonToolTip = entry.IsCurrentlyOpen ? "ウィンドウを隠す（保存データは保持）" : "画面に表示して前面へ",
                    DeleteButtonVisibility = isSaveEnabled ? Visibility.Visible : Visibility.Collapsed
                });
            }

            ItemsList.ItemsSource = viewItems;
            SubtitleText.Text = isSaveEnabled
                ? $"保存済み: {viewItems.Count} 件"
                : $"表示中: {viewItems.Count} 件（保存設定 OFF）";

            if (viewItems.Count == 0)
            {
                EmptyStatePanel.Visibility = Visibility.Visible;
                EmptyStateText.Text = isSaveEnabled
                    ? "保存されている Sticky Text はありません"
                    : "表示中の Sticky Text はありません\n（保存を有効にすると閉じたメモも一覧管理できます）";
                DeleteAllBtn.IsEnabled = false;
            }
            else
            {
                EmptyStatePanel.Visibility = Visibility.Collapsed;
                DeleteAllBtn.IsEnabled = isSaveEnabled;
            }
        }

        private static Brush GetResourceBrush(string key)
        {
            if (Application.Current.Resources.TryGetValue(key, out object val) && val is Brush brush)
            {
                return brush;
            }
            return new SolidColorBrush(Colors.Gray);
        }

        private void NewBtn_Click(object sender, RoutedEventArgs e)
        {
            _service.CreateNewStickyText();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => this.Close();

        private void ToggleShowBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                _service.ToggleOpenById(id);
            }
        }

        private void DeleteItemBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                _service.DeleteById(id);
            }
        }

        private void ConfirmDeleteAllBtn_Click(object sender, RoutedEventArgs e)
        {
            DeleteAllConfirmFlyout.Hide();
            _service.DeleteAllSaved();
        }

        private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                this.Close();
                e.Handled = true;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    }
}
