using Caiqelle.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Foundation;
using WinRT.Interop;
using WinUIEx;

namespace Caiqelle.Views
{
    public sealed partial class ActionPillWindow : WindowEx
    {
        private readonly ActionService _actionService;
        private readonly HookService? _hookService;
        private readonly ThemeService? _themeService;
        private readonly IntPtr _hwnd;
        private readonly AppWindow _appWindow;
        private readonly DispatcherQueueTimer _autoHideTimer;
        private ActionDetectionContext? _currentContext;
        private bool _isClosingAnim = false;
        private bool _isPointerInside = false;
        private bool _isClosed = false;
        private volatile bool _isPillVisible = false;

        private const int WindowWidthDip = 192;
        private const int WindowHeightDip = 62;

        public bool IsPillVisible => _isPillVisible && !_isClosed;

        public ActionPillWindow(ActionService actionService, HookService? hookService = null, ThemeService? themeService = null)
        {
            _actionService = actionService;
            _hookService = hookService;
            _themeService = themeService ?? (((App)Application.Current).Services.GetService(typeof(ThemeService)) as ThemeService);
            this.InitializeComponent();

            this.SystemBackdrop = new WinUIEx.TransparentTintBackdrop(Colors.Transparent);

            _hwnd = WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
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
            this.IsAlwaysOnTop = true;

            // 外側の透明ウィンドウ枠・影・DWM非クライアント領域を完全に除去
            int DWMWA_NCRENDERING_POLICY = 2;
            int DWMNCRP_DISABLED = 1;
            DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref DWMNCRP_DISABLED, sizeof(int));

            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_DONOTROUND = 1;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_DONOTROUND, sizeof(int));

            int DWMWA_BORDER_COLOR = 34;
            int DWM_BORDER_COLOR_NONE = -2;
            DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref DWM_BORDER_COLOR_NONE, sizeof(int));

            // フォーカスを奪わず・タスクバーに表示せず・ウィンドウ枠を削除・キャプチャ対象からも除外
            ApplyWindowStyles();
            NativeMethods.RemoveWindowBorder(_hwnd);
            const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
            SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);

            UpdateThemeBrushes();
            if (_themeService != null)
            {
                _themeService.ThemeChanged += OnThemeChanged;
            }
            this.Closed += (_, _) =>
            {
                _isClosed = true;
                _isPillVisible = false;
                _autoHideTimer?.Stop();
                if (_themeService != null)
                {
                    _themeService.ThemeChanged -= OnThemeChanged;
                }
            };

            CapsuleBorder.SizeChanged += (_, _) => UpdateCapsuleRegion();
            HideCapsuleStoryboard.Completed += (_, _) =>
            {
                if (_isClosingAnim)
                {
                    _isClosingAnim = false;
                    _isPillVisible = false;
                    _appWindow.Hide();
                }
            };

            _autoHideTimer = DispatcherQueue.CreateTimer();
            _autoHideTimer.Interval = TimeSpan.FromSeconds(5);
            _autoHideTimer.Tick += (_, _) =>
            {
                _autoHideTimer.Stop();
                if (!_isPointerInside)
                {
                    _actionService.NotifyPillTimedOut();
                    HideWithAnimation();
                }
            };
        }

        private void UpdateThemeBrushes()
        {
            try
            {
                var c = _themeService != null
                    ? ThemeService.GetAccentColor(_themeService.CurrentTheme)
                    : Windows.UI.Color.FromArgb(255, 0, 255, 127);

                // 黒からテーマカラーへ滑らかに溶け込むグラデーションを背景全体へ適用
                var bgGradient = new LinearGradientBrush
                {
                    StartPoint = new Point(0.0, 0.0),
                    EndPoint = new Point(1.0, 1.0)
                };
                bgGradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xEC, 0x05, 0x06, 0x08), Offset = 0.0 });
                bgGradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xEA, (byte)(c.R * 0.22), (byte)(c.G * 0.22), (byte)(c.B * 0.22)), Offset = 0.55 });
                bgGradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xEC, (byte)(c.R * 0.38), (byte)(c.G * 0.38), (byte)(c.B * 0.38)), Offset = 1.0 });
                CapsuleBorder.Background = bgGradient;
                CapsuleBorder.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x88, c.R, c.G, c.B));
            }
            catch { }
        }

        public void ShowContext(ActionDetectionContext context)
        {
            _currentContext = context;
            _isClosingAnim = false;

            SummaryText.Text = $"Action検知：{context.Fields.Count}件";

            UpdateThemeBrushes();
            PositionNearCursor();

            _isPillVisible = true;
            _appWindow.Show(false);
            if (_hookService != null) _hookService.IsActionPillActive = true;

            ShowCapsuleStoryboard.Stop();
            HideCapsuleStoryboard.Stop();
            ShowCapsuleStoryboard.Begin();
            UpdateCapsuleRegion();

            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }

        public void TriggerApplyFromShortcut() => ApplyButton_Click(this, new RoutedEventArgs());

        public void TriggerDismissFromShortcut() => DismissButton_Click(this, new RoutedEventArgs());

        private void OnThemeChanged(AccentTheme _)
        {
            if (_isClosed) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isClosed) UpdateThemeBrushes();
            });
        }

        public void HideWithAnimation()
        {
            if (_isClosed) return;
            if (_hookService != null) _hookService.IsActionPillActive = false;
            if (!_appWindow.IsVisible || _isClosingAnim) return;
            _autoHideTimer.Stop();
            _isClosingAnim = true;
            ShowCapsuleStoryboard.Stop();
            HideCapsuleStoryboard.Begin();
        }

        public void HideImmediately()
        {
            if (_isClosed) return;
            if (_hookService != null) _hookService.IsActionPillActive = false;
            _autoHideTimer.Stop();
            _isClosingAnim = false;
            _isPillVisible = false;
            ShowCapsuleStoryboard.Stop();
            HideCapsuleStoryboard.Stop();
            _appWindow.Hide();
        }

        private void PositionNearCursor()
        {
            double dpiScale = GetDpiForWindow(_hwnd) / 96.0;
            int wPx = (int)Math.Round(WindowWidthDip * dpiScale);
            int hPx = (int)Math.Round(WindowHeightDip * dpiScale);
            _appWindow.Resize(new Windows.Graphics.SizeInt32(wPx, hPx));

            GetCursorPos(out POINT cursorPos);
            var pt = new Windows.Graphics.PointInt32(cursorPos.X, cursorPos.Y);
            var displayArea = DisplayArea.GetFromPoint(pt, DisplayAreaFallback.Primary)
                              ?? DisplayArea.Primary;

            if (displayArea != null)
            {
                var work = displayArea.WorkArea;
                int offsetAbove = (int)Math.Round(16 * dpiScale);
                int offsetBelow = (int)Math.Round(24 * dpiScale);

                int x = Math.Clamp(cursorPos.X - wPx / 2, work.X + 8, work.X + Math.Max(8, work.Width - wPx - 8));
                int y = cursorPos.Y - hPx - offsetAbove;
                if (y < work.Y + 8)
                {
                    y = Math.Min(cursorPos.Y + offsetBelow, work.Y + Math.Max(8, work.Height - hPx - 8));
                }

                _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
            }
        }

        private void UpdateCapsuleRegion()
        {
            try
            {
                double dpiScale = GetDpiForWindow(_hwnd) / 96.0;
                int wPx = (int)Math.Round(WindowWidthDip * dpiScale);
                int hPx = (int)Math.Round(WindowHeightDip * dpiScale);

                // ウィンドウ全体を 192x62 のカプセル形状に物理クリップし、外側の透明領域を完全排除
                IntPtr rgn = CreateRoundRectRgn(0, 0, wPx + 1, hPx + 1, hPx, hPx);
                if (rgn != IntPtr.Zero)
                {
                    if (SetWindowRgn(_hwnd, rgn, true) == 0)
                    {
                        DeleteObject(rgn);
                    }
                }
            }
            catch { }
        }

        private void ApplyWindowStyles()
        {
            const int GWL_EXSTYLE = -20;
            const long WS_EX_NOACTIVATE = 0x08000000L;
            const long WS_EX_TOOLWINDOW = 0x00000080L;

            IntPtr exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            long newExStyle = exStyle.ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(newExStyle));
        }

        private void CapsuleBorder_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isPointerInside = true;
            _autoHideTimer.Stop();
        }

        private void CapsuleBorder_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isPointerInside = false;
            if (_appWindow.IsVisible && !_isClosingAnim)
            {
                _autoHideTimer.Start();
            }
        }

        private async void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var ctx = _currentContext;
            HideImmediately();
            if (ctx != null)
            {
                await _actionService.ExecuteFillAsync(ctx);
            }
        }

        private void DismissButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentContext != null)
            {
                _actionService.MarkContextCompleted(_currentContext.WindowHwnd, _currentContext.WindowTitle);
            }
            HideWithAnimation();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
