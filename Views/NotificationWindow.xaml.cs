using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Numerics;
using Microsoft.UI.Xaml.Hosting;

namespace Caiqelle.Views
{
    public sealed partial class NotificationWindow : Window
    {
        private bool _isLoaded = false;
        private string? _pendingMessage = null;
        private string? _pendingGlyph = null;
        private int _pendingDurationMs = 4000;
        private DispatcherTimer _timer;

        public NotificationWindow()
        {
            this.InitializeComponent();
            var hWndForIcon = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowIdForIcon = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWndForIcon);
            var appWindowForIcon = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowIdForIcon);
            // TODO: Caiqelle用アイコンを用意したら設定する
            // appWindowForIcon.SetIcon("Assets\\favicon.ico");

            // アクリル背景（ContentBorder が全面を覆うため視覚的には不可視）
            this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();

            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            // ウィンドウ枠線の色を非表示に
            int DWMWA_BORDER_COLOR = 34;
            int DWM_BORDER_COLOR_NONE = -2;
            NotificationNativeMethods.DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref DWM_BORDER_COLOR_NONE, sizeof(int));

            // OS レベルでウィンドウ自体に角丸を適用（Windows 11）
            // ContentBorder は CornerRadius=0 で全面塗りつぶし → 黒い背景が一切露出しない
            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_ROUND = 2;
            NotificationNativeMethods.DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_ROUND, sizeof(int));

            NotificationNativeMethods.RemoveWindowBorder(hWnd);

            // 通知ウィンドウのサイズと位置を設定（画面中央）
            appWindow.Resize(new Windows.Graphics.SizeInt32(360, 64));
            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var workArea = displayArea.WorkArea;
                appWindow.Move(new Windows.Graphics.PointInt32(
                    workArea.X + (workArea.Width - 360) / 2,
                    workArea.Y + (workArea.Height - 64) / 2));
            }

            _timer = new DispatcherTimer();
            _timer.Tick += Timer_Tick;
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            if (_pendingMessage != null)
            {
                ShowMessageInternal(_pendingMessage, _pendingGlyph, _pendingDurationMs);
                _pendingMessage = null;
            }
        }

        public void ShowMessage(string message, string? glyph, int durationMs = 4000)
        {
            if (!_isLoaded)
            {
                _pendingMessage = message;
                _pendingGlyph = glyph;
                _pendingDurationMs = durationMs;

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);

                // タスクバーから除外
                var exStyle = (long)NotificationNativeMethods.GetWindowLongPtr(hwnd, NotificationNativeMethods.GWL_EXSTYLE);
                exStyle |= 0x00000080L; // WS_EX_TOOLWINDOW
                NotificationNativeMethods.SetWindowLongPtr(hwnd, NotificationNativeMethods.GWL_EXSTYLE, (IntPtr)exStyle);

                appWindow.Show();
                NotificationNativeMethods.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);
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

                // アクションに応じたアイコン配色（M3 ファンクショナルカラー）
                if (glyph == "\uE73E" || glyph == "\uE8D5") // Check / Folder
                {
                    SetIconColors("FuncGreenBgBrush", "FuncGreenBrush", Microsoft.UI.Colors.LightGreen);
                }
                else if (glyph == "\uE8C8") // Copy
                {
                    SetIconColors("FuncBlueBgBrush", "FuncBlueBrush", Microsoft.UI.Colors.DeepSkyBlue);
                }
                else if (glyph == "\uE7C8" || glyph == "\uE71A") // Record / Pause
                {
                    SetIconColors("FuncPinkBgBrush", "FuncPinkBrush", Microsoft.UI.Colors.Salmon);
                }
                else
                {
                    SetIconColors("FuncCyanBgBrush", "FuncCyanBrush", Microsoft.UI.Colors.Cyan);
                }
            }
            else
            {
                IconElement.Visibility = Visibility.Collapsed;
                if (IconContainer != null) IconContainer.Visibility = Visibility.Collapsed;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            // タスクバーから除外
            var exStyle = (long)NotificationNativeMethods.GetWindowLongPtr(hwnd, NotificationNativeMethods.GWL_EXSTYLE);
            exStyle |= 0x00000080L; // WS_EX_TOOLWINDOW
            NotificationNativeMethods.SetWindowLongPtr(hwnd, NotificationNativeMethods.GWL_EXSTYLE, (IntPtr)exStyle);

            // フォーカスを奪わず表示
            appWindow.Show();
            NotificationNativeMethods.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200);

            // アニメーション開始（レンダリング完了を待つ）
            DispatcherQueue.TryEnqueue(async () =>
            {
                await System.Threading.Tasks.Task.Delay(50);
                AnimateIn();

                _timer.Stop();
                _timer.Interval = TimeSpan.FromMilliseconds(durationMs);
                _timer.Start();
            });
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
            var compositor = ElementCompositionPreview.GetElementVisual(RootGrid).Compositor;
            var visual = ElementCompositionPreview.GetElementVisual(ContentBorder);

            var springAnim = compositor.CreateSpringVector3Animation();
            springAnim.Target = "Scale";
            springAnim.InitialValue = new Vector3(0.8f, 0.8f, 1.0f);
            springAnim.FinalValue = new Vector3(1.0f, 1.0f, 1.0f);
            springAnim.DampingRatio = 0.6f;
            springAnim.Period = TimeSpan.FromMilliseconds(50);

            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.Target = "Opacity";
            opacityAnim.InsertKeyFrame(0.0f, 0.0f);
            opacityAnim.InsertKeyFrame(1.0f, 1.0f);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(200);

            visual.CenterPoint = new Vector3((float)ContentBorder.ActualWidth / 2, (float)ContentBorder.ActualHeight / 2, 0);
            visual.StartAnimation("Scale", springAnim);
            visual.StartAnimation("Opacity", opacityAnim);

            ContentBorder.Opacity = 1;
        }

        private void AnimateOut()
        {
            var compositor = ElementCompositionPreview.GetElementVisual(RootGrid).Compositor;
            var visual = ElementCompositionPreview.GetElementVisual(ContentBorder);

            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.Target = "Opacity";
            opacityAnim.InsertKeyFrame(1.0f, 0.0f);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(300);

            var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
            visual.StartAnimation("Opacity", opacityAnim);
            batch.End();

            batch.Completed += (s, e) =>
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                appWindow.Hide();
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
