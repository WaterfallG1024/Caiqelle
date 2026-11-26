using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace Caiqelle.Views
{
    /// <summary>
    /// ピン留め・ライブピン留めウィンドウの枠外に追従表示するフローティングツールバー
    /// </summary>
    public sealed partial class PinToolbarWindow : Window
    {
        private readonly IntPtr _hwnd;
        private readonly AppWindow _appWindow;
        private readonly DispatcherQueueTimer _hideTimer;
        private bool _isParentHovered = false;
        private bool _isSelfHovered = false;
        private bool _isClosed = false;
        private int _logicalWidth = 136;
        private const int LogicalHeight = 44;

        public event Action? CopyRequested;
        public event Action? SaveRequested;
        public event Action? CloseRequested;
        public event Action? PauseResumeRequested;
        public event Action? ZoomInRequested;
        public event Action? ZoomOutRequested;
        public event Action? ScaleResetRequested;
        public event Action? MoveDragRequested;

        public PinToolbarWindow(bool isLivePin)
        {
            this.InitializeComponent();
            this.SystemBackdrop = new WinUIEx.TransparentTintBackdrop(Colors.Transparent);

            if (((App)Application.Current).Services.GetService(typeof(Services.ThemeService)) is Services.ThemeService themeService)
            {
                themeService.RegisterWindow(this, RootGrid);
            }

            _hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.IsShownInSwitchers = false;

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
            }

            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_DONOTROUND = 1;
            NativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_DONOTROUND, sizeof(int));

            int DWMWA_BORDER_COLOR = 34;
            int DWM_BORDER_COLOR_NONE = -2;
            NativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref DWM_BORDER_COLOR_NONE, sizeof(int));

            var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            NativeMethods.RemoveWindowBorder(_hwnd);

            // タスクバーから除外
            long exStyle = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            exStyle |= 0x00000080L; // WS_EX_TOOLWINDOW
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)exStyle);

            // ライブピン・キャプチャ映り込み防止
            SetWindowDisplayAffinity(_hwnd, 0x00000011);

            Configure(isLivePin);

            MoveBtn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) =>
            {
                if (e.GetCurrentPoint(MoveBtn).Properties.IsLeftButtonPressed)
                {
                    e.Handled = true;
                    MoveDragRequested?.Invoke();
                }
            }), true);

            _hideTimer = DispatcherQueue.CreateTimer();
            _hideTimer.Interval = TimeSpan.FromMilliseconds(160);
            _hideTimer.IsRepeating = false;
            _hideTimer.Tick += (_, _) =>
            {
                if (!_isParentHovered && !_isSelfHovered && !_isClosed)
                {
                    _appWindow.Hide();
                }
            };

            this.Closed += (_, _) =>
            {
                _isClosed = true;
                _hideTimer.Stop();
            };
        }

        public void SafeClose()
        {
            if (_isClosed) return;
            _isClosed = true;
            _hideTimer.Stop();
            try { this.Close(); } catch { }
        }

        private void Configure(bool isLivePin)
        {
            var vis = isLivePin ? Visibility.Visible : Visibility.Collapsed;
            MoveBtn.Visibility = vis;
            PauseResumeBtn.Visibility = vis;
            LiveDivider1.Visibility = vis;
            ZoomOutBtn.Visibility = vis;
            ScaleResetBtn.Visibility = vis;
            ZoomInBtn.Visibility = vis;
            LiveDivider2.Visibility = vis;
            _logicalWidth = isLivePin ? 344 : 136;
        }

        public void SyncPauseState(bool isPaused)
        {
            PauseResumeIcon.Glyph = isPaused ? "\uE768" : "\uE769";
        }

        public void SyncScaleText(double scale)
        {
            ScaleText.Text = $"{scale:0.#}x";
        }

        public void UpdatePosition(RectInt32 parentBounds)
        {
            if (_isClosed) return;

            double dpi = NativeMethods.GetDpiForWindow(_hwnd);
            double dpiScale = dpi > 0 ? dpi / 96.0 : 1.0;

            int wPx = (int)Math.Round(_logicalWidth * dpiScale);
            int hPx = (int)Math.Round(LogicalHeight * dpiScale);
            int gap = (int)Math.Round(8 * dpiScale);

            int x = parentBounds.X + (parentBounds.Width - wPx) / 2;
            int y = parentBounds.Y - hPx - gap;

            var displayArea = DisplayArea.GetFromPoint(
                new PointInt32(parentBounds.X + parentBounds.Width / 2, parentBounds.Y + parentBounds.Height / 2),
                DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var work = displayArea.WorkArea;
                if (y < work.Y)
                {
                    // 上端に余裕がない場合は枠外の下側に配置
                    y = parentBounds.Y + parentBounds.Height + gap;
                }
                x = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - wPx));
                y = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - hPx));
            }

            _appWindow.MoveAndResize(new RectInt32(x, y, wPx, hPx));
        }

        public void NotifyParentPointerEntered(RectInt32 parentBounds)
        {
            if (_isClosed) return;
            _isParentHovered = true;
            _hideTimer.Stop();
            UpdatePosition(parentBounds);
            _appWindow.Show(false);
        }

        public void NotifyParentPointerExited()
        {
            _isParentHovered = false;
            ScheduleHideCheck();
        }

        private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isSelfHovered = true;
            _hideTimer.Stop();
        }

        private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isSelfHovered = false;
            ScheduleHideCheck();
        }

        private void ScheduleHideCheck()
        {
            if (_isClosed) return;
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void PauseResumeBtn_Click(object sender, RoutedEventArgs e) => PauseResumeRequested?.Invoke();
        private void ZoomOutBtn_Click(object sender, RoutedEventArgs e) => ZoomOutRequested?.Invoke();
        private void ScaleResetBtn_Click(object sender, RoutedEventArgs e) => ScaleResetRequested?.Invoke();
        private void ZoomInBtn_Click(object sender, RoutedEventArgs e) => ZoomInRequested?.Invoke();
        private void CopyBtn_Click(object sender, RoutedEventArgs e) => CopyRequested?.Invoke();
        private void SaveBtn_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke();
        private void CloseBtn_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

        private const int GWL_EXSTYLE = -20;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    }
}
