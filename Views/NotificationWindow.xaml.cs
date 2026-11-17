using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

namespace Caiqelle.Views
{
    public sealed partial class NotificationWindow : Window
    {
        private bool _isLoaded = false;
        private string? _pendingMessage = null;
        private string? _pendingGlyph = null;
        private int _pendingDurationMs = 4000;
        private readonly DispatcherTimer _timer;

        private readonly IntPtr _hwnd;
        private readonly AppWindow _appWindow;
        private readonly Microsoft.UI.Composition.Compositor _compositor;
        private readonly Microsoft.UI.Composition.Visual _contentVisual;
        private readonly Microsoft.UI.Composition.SpringVector3NaturalMotionAnimation _showScaleAnim;
        private readonly Microsoft.UI.Composition.ScalarKeyFrameAnimation _showOpacityAnim;
        private readonly Microsoft.UI.Composition.ScalarKeyFrameAnimation _hideOpacityAnim;
        private int _animGeneration = 0;

        public NotificationWindow()
        {
            this.InitializeComponent();

            // 大枠のアクリル背景を削除し、完全透過ウィンドウにする
            this.SystemBackdrop = new WinUIEx.TransparentTintBackdrop(Microsoft.UI.Colors.Transparent);

            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.IsShownInSwitchers = false;

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            // 透明ウィンドウの影・角丸・枠線を完全に無効化（トースト本体のみを浮かせる）
            int DWMWA_NCRENDERING_POLICY = 2;
            int DWMNCRP_DISABLED = 1;
            NotificationNativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref DWMNCRP_DISABLED, sizeof(int));

            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_DONOTROUND = 1;
            NotificationNativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_DONOTROUND, sizeof(int));

            int DWMWA_BORDER_COLOR = 34;
            int DWM_BORDER_COLOR_NONE = -2;
            NotificationNativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref DWM_BORDER_COLOR_NONE, sizeof(int));

            NotificationNativeMethods.RemoveWindowBorder(_hwnd);

            // タスクバーから除外（コンストラクタで1回のみ設定）
            var exStyle = (long)NotificationNativeMethods.GetWindowLongPtr(_hwnd, NotificationNativeMethods.GWL_EXSTYLE);
            exStyle |= 0x00000080L; // WS_EX_TOOLWINDOW
            NotificationNativeMethods.SetWindowLongPtr(_hwnd, NotificationNativeMethods.GWL_EXSTYLE, (IntPtr)exStyle);

            // 通知ウィンドウのサイズと位置を設定（画面中央）
            _appWindow.Resize(new Windows.Graphics.SizeInt32(380, 72));
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var workArea = displayArea.WorkArea;
                _appWindow.Move(new Windows.Graphics.PointInt32(
                    workArea.X + (workArea.Width - 380) / 2,
                    workArea.Y + (workArea.Height - 72) / 2));
            }

            // Composition アニメーションを事前構築してキャッシュ
            _compositor = ElementCompositionPreview.GetElementVisual(RootGrid).Compositor;
            _contentVisual = ElementCompositionPreview.GetElementVisual(ContentBorder);

            _showScaleAnim = _compositor.CreateSpringVector3Animation();
            _showScaleAnim.Target = "Scale";
            _showScaleAnim.InitialValue = new Vector3(0.92f, 0.92f, 1.0f);
            _showScaleAnim.FinalValue = new Vector3(1.0f, 1.0f, 1.0f);
            _showScaleAnim.DampingRatio = 0.78f;
            _showScaleAnim.Period = TimeSpan.FromMilliseconds(28);

            _showOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
            _showOpacityAnim.Target = "Opacity";
            _showOpacityAnim.InsertKeyFrame(0.0f, 0.0f);
            _showOpacityAnim.InsertKeyFrame(1.0f, 1.0f);
            _showOpacityAnim.Duration = TimeSpan.FromMilliseconds(70);

            _hideOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
            _hideOpacityAnim.Target = "Opacity";
            _hideOpacityAnim.InsertKeyFrame(1.0f, 0.0f);
            _hideOpacityAnim.Duration = TimeSpan.FromMilliseconds(150);

            _timer = new DispatcherTimer();
            _timer.Tick += Timer_Tick;
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            if (_pendingMessage != null)
            {
                var msg = _pendingMessage;
                var glyph = _pendingGlyph;
                var dur = _pendingDurationMs;
                _pendingMessage = null;
                ShowMessageInternal(msg, glyph, dur);
            }
        }

        public void ShowMessage(string message, string? glyph, int durationMs = 4000)
        {
            if (!_isLoaded)
            {
                _pendingMessage = message;
                _pendingGlyph = glyph;
                _pendingDurationMs = durationMs;

                _appWindow.Show();
                NotificationNativeMethods.SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
            }
            else
            {
                ShowMessageInternal(message, glyph, durationMs);
            }
        }

        private void ShowMessageInternal(string message, string? glyph, int durationMs)
        {
            // サフィックス（「に保存しました」等）を分離し、右端に固定表示
            string target = message;
            string suffix = "";

            string[] knownSuffixes = new[]
            {
                " に保存しました",
                " にコピーしました",
                " を開始しました",
                " を停止しました",
                " を一時停止しました",
                " を再開しました"
            };

            foreach (var s in knownSuffixes)
            {
                if (message.EndsWith(s, StringComparison.Ordinal))
                {
                    target = message.Substring(0, message.Length - s.Length);
                    suffix = s;
                    break;
                }
            }

            MessageText.Text = target;
            SuffixText.Text = suffix;
            SuffixText.Visibility = string.IsNullOrEmpty(suffix) ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(RootGrid, message);

            if (!string.IsNullOrEmpty(glyph))
            {
                IconElement.Glyph = glyph;
                IconElement.Visibility = Visibility.Visible;
                if (IconContainer != null) IconContainer.Visibility = Visibility.Visible;

                // カラーテーマに合わせたアイコン配色
                SetIconColors("M3PrimaryContainerGlassBrush", "M3PrimaryBrush", Microsoft.UI.Colors.SpringGreen);
            }
            else
            {
                IconElement.Visibility = Visibility.Collapsed;
                if (IconContainer != null) IconContainer.Visibility = Visibility.Collapsed;
            }

            // フォーカスを奪わず即座に表示＆アニメーション開始（不要な50ms待機を削除）
            _animGeneration++;
            ContentBorder.Opacity = 1;
            _appWindow.Show();
            NotificationNativeMethods.SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);

            AnimateIn();

            _timer.Stop();
            _timer.Interval = TimeSpan.FromMilliseconds(durationMs);
            _timer.Start();
        }

        private void SetIconColors(string bgKey, string fgKey, Windows.UI.Color fallbackFg)
        {
            if (Application.Current.Resources.TryGetValue(bgKey, out var bg) && bg is Microsoft.UI.Xaml.Media.Brush bgBrush && IconContainer != null)
            {
                IconContainer.Background = bgBrush;
            }
            if (Application.Current.Resources.TryGetValue(fgKey, out var fg) && fg is Microsoft.UI.Xaml.Media.Brush fgBrush)
            {
                IconElement.Foreground = fgBrush;
            }
            else
            {
                IconElement.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(fallbackFg);
            }
        }

        private void Timer_Tick(object? sender, object e)
        {
            _timer.Stop();
            AnimateOut();
        }

        private void AnimateIn()
        {
            float w = (float)ContentBorder.ActualWidth;
            float h = (float)ContentBorder.ActualHeight;
            _contentVisual.CenterPoint = new Vector3(w > 0 ? w / 2 : 160, h > 0 ? h / 2 : 24, 0);
            _contentVisual.StartAnimation("Scale", _showScaleAnim);
            _contentVisual.StartAnimation("Opacity", _showOpacityAnim);
        }

        private void AnimateOut()
        {
            int gen = _animGeneration;
            var batch = _compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
            _contentVisual.StartAnimation("Opacity", _hideOpacityAnim);
            batch.End();

            batch.Completed += (s, e) =>
            {
                if (gen != _animGeneration) return;
                _appWindow.Hide();
            };
        }
    }

    internal static class NotificationNativeMethods
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const long WS_BORDER = 0x00800000L;
        public const long WS_THICKFRAME = 0x00040000L;
        public const long WS_CAPTION = 0x00C00000L;

        public static void RemoveWindowBorder(IntPtr hwnd)
        {
            var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
            style &= ~WS_BORDER;
            style &= ~WS_THICKFRAME;
            style &= ~WS_CAPTION;
            SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);
        }
    }
}
