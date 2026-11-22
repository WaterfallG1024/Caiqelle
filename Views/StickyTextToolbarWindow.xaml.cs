using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace Caiqelle.Views
{
    /// <summary>
    /// Sticky Text カードの枠外（上側または下側）にフローティング表示されるツールバーウィンドウ
    /// </summary>
    public sealed partial class StickyTextToolbarWindow : Window
    {
        private readonly StickyTextWindow _owner;
        private readonly IntPtr _hwnd;
        private readonly AppWindow _appWindow;
        private bool _isVisible = false;
        private bool _isUpdatingUi = false;

        private const uint WDA_NONE = 0x00000000;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        public bool IsFlyoutOpen { get; private set; }
        public event Action<bool>? PointerHoverChanged;

        public StickyTextToolbarWindow(StickyTextWindow owner)
        {
            _owner = owner;
            this.InitializeComponent();
            this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            if (((App)Application.Current).Services.GetService(typeof(Services.ThemeService)) is Services.ThemeService themeService)
            {
                themeService.RegisterWindow(this, RootGrid);
            }

            _hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.IsShownInSwitchers = false;

            ExtendsContentIntoTitleBar = true;

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }

            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_ROUND = 2;
            NativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_ROUND, sizeof(int));

            int DWMWA_BORDER_COLOR = 34;
            int DWM_BORDER_COLOR_NONE = -2;
            NativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref DWM_BORDER_COLOR_NONE, sizeof(int));

            var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            NativeMethods.RemoveWindowBorder(_hwnd);
            _appWindow.Hide();
        }

        public void PositionRelativeTo(Windows.Graphics.RectInt32 cardRect)
        {
            double dpiScale = NativeMethods.GetDpiForWindow(_hwnd) / 96.0;
            ToolbarBorder.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            int width = Math.Max((int)(420 * dpiScale), (int)Math.Ceiling(ToolbarBorder.DesiredSize.Width * dpiScale));
            int height = Math.Max((int)(38 * dpiScale), (int)Math.Ceiling(ToolbarBorder.DesiredSize.Height * dpiScale));

            int gap = (int)(6 * dpiScale);
            int x = cardRect.X + (cardRect.Width - width) / 2;
            int y = cardRect.Y - height - gap;

            var displayArea = DisplayArea.GetFromPoint(
                new Windows.Graphics.PointInt32(cardRect.X + cardRect.Width / 2, cardRect.Y + cardRect.Height / 2),
                DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var work = displayArea.WorkArea;
                x = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - width));
                if (y < work.Y)
                {
                    // 上側にスペースがない場合はカード下側（枠外）に配置
                    y = cardRect.Y + cardRect.Height + gap;
                    if (y + height > work.Y + work.Height)
                    {
                        y = Math.Max(work.Y, work.Y + work.Height - height);
                    }
                }
            }

            _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
            if (!_isVisible)
            {
                _appWindow.Hide();
            }
        }

        public void ShowNoActivate()
        {
            if (_isVisible) return;
            _isVisible = true;
            // WinUI 3 の XAML 描画を確実に初期化しつつフォーカスを奪わずに表示
            _appWindow.Show(false);
        }

        public void HideToolbar()
        {
            if (!_isVisible) return;
            _isVisible = false;
            _appWindow.Hide();
        }

        public void SyncFormatting(bool isBold, bool isItalic, bool isUnderline, double fontSize, Windows.UI.Color textColor)
        {
            _isUpdatingUi = true;
            try
            {
                BoldBtn.IsChecked = isBold;
                ItalicBtn.IsChecked = isItalic;
                UnderlineBtn.IsChecked = isUnderline;
                FontSizeText.Text = $"{(int)Math.Round(fontSize)}";
                TextColorPicker.Color = textColor;
                ColorPreviewBorder.Background = new SolidColorBrush(textColor);
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        public void SyncStealth(bool isStealth)
        {
            SetWindowDisplayAffinity(_hwnd, isStealth ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
            _isUpdatingUi = true;
            try
            {
                StealthBtn.IsChecked = isStealth;
                StealthIcon.Glyph = isStealth ? "\uED1A" : "\uE890";
                if (Application.Current.Resources.TryGetValue(isStealth ? "M3PrimaryBrush" : "TextFillColorPrimaryBrush", out var brushObj) && brushObj is Brush iconBrush)
                {
                    StealthIcon.Foreground = iconBrush;
                }
                ToolTipService.SetToolTip(
                    StealthBtn,
                    isStealth
                        ? "プライベート（共有時は非表示 / クリックでパブリックへ）"
                        : "パブリック（共有時でも表示 / クリックでプライベートへ）");
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        public void SyncEditing(bool isEditing)
        {
            // 編集確定ボタンは「保存」テキスト固定のためアイコン切替不要
        }

        public void SyncBackgroundLabel(string label, string tooltip)
        {
            BgModeText.Text = label;
            ToolTipService.SetToolTip(BgModeBtn, tooltip);
        }

        public void SyncSaveModeVisibility(bool isSaveEnabled)
        {
            var vis = isSaveEnabled ? Visibility.Visible : Visibility.Collapsed;
            DeleteSeparator.Visibility = vis;
            DeleteBtn.Visibility = vis;
        }

        private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e) => PointerHoverChanged?.Invoke(true);
        private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e) => PointerHoverChanged?.Invoke(false);

        private void ColorFlyout_Opened(object sender, object e) => IsFlyoutOpen = true;
        private void ColorFlyout_Closed(object sender, object e)
        {
            IsFlyoutOpen = false;
            PointerHoverChanged?.Invoke(false);
        }

        private void BoldBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_isUpdatingUi) _owner.SetBold(BoldBtn.IsChecked == true);
        }

        private void ItalicBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_isUpdatingUi) _owner.SetItalic(ItalicBtn.IsChecked == true);
        }

        private void UnderlineBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_isUpdatingUi) _owner.SetUnderline(UnderlineBtn.IsChecked == true);
        }

        private void TextColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        {
            if (_isUpdatingUi) return;
            ColorPreviewBorder.Background = new SolidColorBrush(args.NewColor);
            _owner.SetTextColor(args.NewColor);
        }

        private void FontDownBtn_Click(object sender, RoutedEventArgs e) => _owner.AdjustFontSize(-2);
        private void FontUpBtn_Click(object sender, RoutedEventArgs e) => _owner.AdjustFontSize(2);
        private void BgModeBtn_Click(object sender, RoutedEventArgs e) => _owner.CycleBackgroundStyle();

        private void StealthBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_isUpdatingUi) _owner.SetStealthMode(StealthBtn.IsChecked == true);
        }

        private void EditToggleBtn_Click(object sender, RoutedEventArgs e) => _owner.ToggleEditingMode();
        private void AddBtn_Click(object sender, RoutedEventArgs e) => _owner.RequestAddNew();
        private void ListBtn_Click(object sender, RoutedEventArgs e) => _owner.RequestOpenList();
        private void DeleteBtn_Click(object sender, RoutedEventArgs e) => _owner.RequestDelete();
        private void CloseBtn_Click(object sender, RoutedEventArgs e) => _owner.Close();

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    }
}
