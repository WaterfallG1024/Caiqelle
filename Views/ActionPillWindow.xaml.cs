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
        private readonly DispatcherQueueTimer _poolTimer;
        private ActionDetectionContext? _currentContext;
        private bool _isClosingAnim = false;
        private bool _isPointerInside = false;

        // ウェーブアニメーション用ジオメトリ＆キャッシュ（DynamicCapsule 移植）
        private const int PoolWaveSteps = 12;
        private readonly PathGeometry _poolFrontGeo = new();
        private readonly PathFigure _poolFrontFigure = new() { IsClosed = true, IsFilled = true };
        private readonly LineSegment[] _poolFrontSegments = new LineSegment[PoolWaveSteps * 2 + 1];

        private readonly PathGeometry _poolBackGeo = new();
        private readonly PathFigure _poolBackFigure = new() { IsClosed = true, IsFilled = true };
        private readonly LineSegment[] _poolBackSegments = new LineSegment[PoolWaveSteps * 2 + 1];

        private readonly PathGeometry _poolCrestGeo = new();
        private readonly PathFigure _poolCrestFigure = new() { IsClosed = false, IsFilled = false };
        private readonly LineSegment[] _poolCrestSegments = new LineSegment[PoolWaveSteps];

        private RectangleGeometry? _poolClipGeometry;
        private double _lastPoolClipW = 0;
        private double _lastPoolClipH = 0;
        private double[]? _cachedOffsets;
        private double _cachedW = 0;
        private double _cachedH = 0;
        private DateTime _poolAnimStartTime;

        private const int WindowWidthDip = 192;
        private const int WindowHeightDip = 62;

        public bool IsPillVisible => _appWindow.IsVisible;

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

            InitializePoolWaveGeometries();
            UpdateThemeBrushes();
            if (_themeService != null)
            {
                _themeService.ThemeChanged += (_) => DispatcherQueue.TryEnqueue(UpdateThemeBrushes);
            }

            CapsuleBorder.SizeChanged += (_, _) => UpdateCapsuleRegion();
            HideCapsuleStoryboard.Completed += (_, _) =>
            {
                if (_isClosingAnim)
                {
                    _isClosingAnim = false;
                    _poolTimer.Stop();
                    _appWindow.Hide();
                }
            };

            _poolTimer = DispatcherQueue.CreateTimer();
            _poolTimer.Interval = TimeSpan.FromMilliseconds(36);
            _poolTimer.Tick += (_, _) =>
            {
                if (!_appWindow.IsVisible)
                {
                    _poolTimer.Stop();
                    return;
                }
                double elapsed = (DateTime.UtcNow - _poolAnimStartTime).TotalSeconds;
                UpdatePoolFrame(elapsed);
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

        private void InitializePoolWaveGeometries()
        {
            for (int i = 0; i < _poolFrontSegments.Length; i++)
            {
                _poolFrontSegments[i] = new LineSegment();
                _poolFrontFigure.Segments.Add(_poolFrontSegments[i]);
            }
            _poolFrontGeo.Figures.Add(_poolFrontFigure);
            PoolFrontWavePath.Data = _poolFrontGeo;

            for (int i = 0; i < _poolBackSegments.Length; i++)
            {
                _poolBackSegments[i] = new LineSegment();
                _poolBackFigure.Segments.Add(_poolBackSegments[i]);
            }
            _poolBackGeo.Figures.Add(_poolBackFigure);
            PoolBackWavePath.Data = _poolBackGeo;

            for (int i = 0; i < _poolCrestSegments.Length; i++)
            {
                _poolCrestSegments[i] = new LineSegment();
                _poolCrestFigure.Segments.Add(_poolCrestSegments[i]);
            }
            _poolCrestGeo.Figures.Add(_poolCrestFigure);
            PoolCrestLinePath.Data = _poolCrestGeo;
        }

        private void UpdateThemeBrushes()
        {
            try
            {
                var c = _themeService != null
                    ? ThemeService.GetAccentColor(_themeService.CurrentTheme)
                    : Windows.UI.Color.FromArgb(255, 0, 255, 127);

                byte rBright = (byte)Math.Min(255, c.R + (255 - c.R) * 0.42);
                byte gBright = (byte)Math.Min(255, c.G + (255 - c.G) * 0.42);
                byte bBright = (byte)Math.Min(255, c.B + (255 - c.B) * 0.42);

                byte rDeep = (byte)(c.R * 0.65);
                byte gDeep = (byte)(c.G * 0.65);
                byte bDeep = (byte)(c.B * 0.65);

                // 黒からテーマカラーへ滑らかに溶け込むグラデーション（DynamicCapsule 風）
                var bgGradient = new LinearGradientBrush
                {
                    StartPoint = new Point(0.0, 0.0),
                    EndPoint = new Point(1.0, 1.0)
                };
                bgGradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xEA, 0x05, 0x06, 0x08), Offset = 0.0 });
                bgGradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xE8, (byte)(c.R * 0.12), (byte)(c.G * 0.12), (byte)(c.B * 0.12)), Offset = 0.6 });
                bgGradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xEA, 0x07, 0x08, 0x0A), Offset = 1.0 });
                CapsuleBorder.Background = bgGradient;
                CapsuleBorder.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x78, c.R, c.G, c.B));

                var frontBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0.0, 0.28),
                    EndPoint = new Point(1.0, 0.72)
                };
                frontBrush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x26, rDeep, gDeep, bDeep), Offset = 0.0 });
                frontBrush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x52, c.R, c.G, c.B), Offset = 0.65 });
                frontBrush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x7A, rBright, gBright, bBright), Offset = 1.0 });

                var backBrush = new LinearGradientBrush
                {
                    StartPoint = new Point(0.0, 0.28),
                    EndPoint = new Point(1.0, 0.72)
                };
                backBrush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x16, rDeep, gDeep, bDeep), Offset = 0.0 });
                backBrush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x46, c.R, c.G, c.B), Offset = 1.0 });

                PoolFrontWavePath.Fill = frontBrush;
                PoolBackWavePath.Fill = backBrush;
                PoolCrestLinePath.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0xDC, rBright, gBright, bBright));
            }
            catch { }
        }

        private void UpdatePoolFrame(double elapsed)
        {
            try
            {
                double w = CapsuleBorder.ActualWidth > 10 ? CapsuleBorder.ActualWidth : 192.0;
                double h = CapsuleBorder.ActualHeight > 10 ? CapsuleBorder.ActualHeight : 62.0;
                const double borderInset = 2.0;
                double innerW = Math.Max(10.0, w - borderInset * 2.0);
                double innerH = Math.Max(10.0, h - borderInset * 2.0);
                double r = Math.Min(innerW, innerH) / 2.0;

                if (_poolClipGeometry == null)
                {
                    _lastPoolClipW = innerW;
                    _lastPoolClipH = innerH;
                    _poolClipGeometry = new RectangleGeometry { Rect = new Rect(0, 0, innerW, innerH) };
                    PoolLayer.Clip = _poolClipGeometry;
                }
                else if (Math.Abs(_lastPoolClipW - innerW) > 0.5 || Math.Abs(_lastPoolClipH - innerH) > 0.5)
                {
                    _lastPoolClipW = innerW;
                    _lastPoolClipH = innerH;
                    _poolClipGeometry.Rect = new Rect(0, 0, innerW, innerH);
                }

                // 出現直後に左から波がスッと満ちてくる演出
                const double riseDuration = 0.75;
                double targetFill = 0.52;
                double fillRatio;
                if (elapsed < riseDuration)
                {
                    double p = elapsed / riseDuration;
                    double easeOut = 1.0 - Math.Pow(1.0 - p, 3.0);
                    fillRatio = Math.Clamp(targetFill * easeOut + Math.Sin(p * Math.PI) * 0.05, 0.05, 0.85);
                }
                else
                {
                    double breathe = Math.Sin((elapsed - riseDuration) * 1.8) * 0.015;
                    fillRatio = Math.Clamp(targetFill + breathe, 0.05, 0.85);
                }

                double amp = 3.6;
                double k1 = 2.0 * Math.PI / 46.0;
                double k2 = 2.0 * Math.PI / 26.0;
                double baseWaterX = innerW * fillRatio;

                UpdateHorizontalPoolWaveGeometries(innerW, innerH, r, baseWaterX, amp, k1, k2, elapsed, 6.2, 4.1);
            }
            catch { }
        }

        private void UpdateHorizontalPoolWaveGeometries(
            double w, double h, double r, double baseWaterX, double amp,
            double k1, double k2, double t, double omega1, double omega2)
        {
            const int steps = PoolWaveSteps;
            double dyStep = (h - 0.8) / steps;

            if (_cachedOffsets == null || _cachedOffsets.Length != steps + 1 || Math.Abs(_cachedW - w) > 0.5 || Math.Abs(_cachedH - h) > 0.5)
            {
                _cachedW = w;
                _cachedH = h;
                _cachedOffsets = new double[steps + 1];
                for (int j = 0; j <= steps; j++)
                {
                    double y = 0.4 + j * dyStep;
                    double dy = 0.0;
                    if (y < r) dy = r - y;
                    else if (y > h - r) dy = y - (h - r);
                    double dx = Math.Sqrt(Math.Max(0.0, r * r - dy * dy));
                    _cachedOffsets[j] = dx;

                    double xLeft = r - dx;
                    var ptLeft = new Point(xLeft, y);
                    _poolFrontSegments[steps * 2 - j].Point = ptLeft;
                    _poolBackSegments[steps * 2 - j].Point = ptLeft;
                }
            }

            double invH = 1.0 / Math.Max(1.0, h);
            double slantScale = h * 0.10;

            for (int i = 0; i <= steps; i++)
            {
                double y = 0.4 + i * dyStep;
                double dx = _cachedOffsets[i];
                double xLeft = r - dx;
                double xRight = (w - r) + dx;
                if (xRight <= xLeft + 0.1) xRight = xLeft + 0.1;

                double slantOffset = ((y * invH) - 0.5) * slantScale;
                double waveFront = baseWaterX + slantOffset
                    + amp * Math.Sin(k1 * y - omega1 * t)
                    + (amp * 0.42) * Math.Cos(k2 * y + omega2 * t);
                double waveBack = baseWaterX + slantOffset * 0.88
                    + (amp * 0.88) * Math.Sin(k1 * y + omega1 * 0.82 * t + 2.1)
                    + (amp * 0.38) * Math.Sin(k2 * y - omega2 * 1.08 * t);

                var ptFront = new Point(Math.Clamp(waveFront, xLeft, xRight), y);
                var ptBack = new Point(Math.Clamp(waveBack, xLeft, xRight), y);

                if (i == 0)
                {
                    _poolFrontFigure.StartPoint = ptFront;
                    _poolBackFigure.StartPoint = ptBack;
                    _poolCrestFigure.StartPoint = ptFront;
                }
                else
                {
                    _poolFrontSegments[i - 1].Point = ptFront;
                    _poolBackSegments[i - 1].Point = ptBack;
                    _poolCrestSegments[i - 1].Point = ptFront;
                }
            }
        }

        public void ShowContext(ActionDetectionContext context)
        {
            _currentContext = context;
            _isClosingAnim = false;

            SummaryText.Text = $"Action検知：{context.Fields.Count}件";

            UpdateThemeBrushes();
            PositionNearCursor();

            _appWindow.Show(false);
            if (_hookService != null) _hookService.IsActionPillActive = true;

            ShowCapsuleStoryboard.Stop();
            HideCapsuleStoryboard.Stop();
            ShowCapsuleStoryboard.Begin();
            UpdateCapsuleRegion();

            _poolAnimStartTime = DateTime.UtcNow;
            _poolTimer.Start();

            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }

        public void TriggerApplyFromShortcut() => ApplyButton_Click(this, new RoutedEventArgs());

        public void TriggerDismissFromShortcut() => DismissButton_Click(this, new RoutedEventArgs());

        public void HideWithAnimation()
        {
            if (_hookService != null) _hookService.IsActionPillActive = false;
            if (!_appWindow.IsVisible || _isClosingAnim) return;
            _autoHideTimer.Stop();
            _isClosingAnim = true;
            ShowCapsuleStoryboard.Stop();
            HideCapsuleStoryboard.Begin();
        }

        public void HideImmediately()
        {
            if (_hookService != null) _hookService.IsActionPillActive = false;
            _autoHideTimer.Stop();
            _poolTimer.Stop();
            _isClosingAnim = false;
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
