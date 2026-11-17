using Caiqelle.Services;
using Caiqelle.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Caiqelle.Views
{
    public sealed partial class FloatingWindow : Window
    {
        public FloatingViewModel ViewModel { get; }

        private readonly IntPtr _hwnd;
        private readonly AppWindow _appWindow;
        private readonly Microsoft.UI.Composition.Compositor _compositor;
        private readonly Microsoft.UI.Composition.Visual _contentVisual;
        private readonly Microsoft.UI.Composition.SpringVector3NaturalMotionAnimation _showScaleAnim;
        private readonly Microsoft.UI.Composition.ScalarKeyFrameAnimation _showOpacityAnim;
        private readonly Microsoft.UI.Composition.ScalarKeyFrameAnimation _hideOpacityAnim;

        private int _lastTargetWidth = -1;
        private int _lastTargetHeight = -1;
        private int _lastX = int.MinValue;
        private int _lastY = int.MinValue;
        private bool _isLoaded = false;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        public FloatingWindow()
        {
            var app = (App)Application.Current;
            ViewModel = app.Services.GetRequiredService<FloatingViewModel>();
            ViewModel.RequestHideWindow = HideWindow;
            ViewModel.GetCaptureManager = () => ((App)Application.Current).CaptureManager;

            this.InitializeComponent();

            // 大枠の背景を完全透過にしてカードのみを浮かせる
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

            // 透明ウィンドウの影・角丸・枠線を完全に無効化（大枠背景・影を出さない）
            int DWMWA_NCRENDERING_POLICY = 2;
            int DWMNCRP_DISABLED = 1;
            NativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref DWMNCRP_DISABLED, sizeof(int));

            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_DONOTROUND = 1;
            NativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_DONOTROUND, sizeof(int));

            // ウィンドウの白い線を完全に削除
            int DWMWA_BORDER_COLOR = 34;
            int DWM_BORDER_COLOR_NONE = -2;
            NativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref DWM_BORDER_COLOR_NONE, sizeof(int));

            // フレームをクライアント領域に拡張して枠線を非表示にする
            var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);

            NativeMethods.RemoveWindowBorder(_hwnd);

            // Composition アニメーションを事前構築してキャッシュ
            _compositor = ElementCompositionPreview.GetElementVisual(this.RootGrid).Compositor;
            _contentVisual = ElementCompositionPreview.GetElementVisual(ContentBorder);
            _contentVisual.CenterPoint = new Vector3(387, 76, 0);

            _showScaleAnim = _compositor.CreateSpringVector3Animation();
            _showScaleAnim.Target = "Scale";
            _showScaleAnim.InitialValue = new Vector3(0.94f, 0.94f, 1.0f);
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
            _hideOpacityAnim.Duration = TimeSpan.FromMilliseconds(90);

            UpdatePositionAndSize();
        }

        private void UpdatePositionAndSize()
        {
            double dpiScale = GetDpiForWindow(_hwnd) / 96.0;
            int targetWidth = (int)(774 * dpiScale);
            int targetHeight = (int)(152 * dpiScale);
            if (targetWidth != _lastTargetWidth || targetHeight != _lastTargetHeight)
            {
                _appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));
                _lastTargetWidth = targetWidth;
                _lastTargetHeight = targetHeight;
            }

            GetCursorPos(out POINT cursorPos);
            var displayArea = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(cursorPos.X, cursorPos.Y), DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var workArea = displayArea.WorkArea;
                int x = workArea.X + (workArea.Width - targetWidth) / 2;
                int y = workArea.Y + (workArea.Height - targetHeight) / 2;
                if (x != _lastX || y != _lastY)
                {
                    _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
                    _lastX = x;
                    _lastY = y;
                }
            }
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;
            ContentBorder.Opacity = 1;
        }

        public void CloseWithAnimation()
        {
            var batch = _compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
            _contentVisual.StartAnimation("Opacity", _hideOpacityAnim);
            batch.End();

            batch.Completed += (s, e) => HideWindow();
        }

        public void HideInstantlyAndClose() => HideWindow();

        public void HideWindow()
        {
            _appWindow.Hide();

            var app = (App)Application.Current;
            var hook = app.Services.GetRequiredService<HookService>();
            hook.IsFloatingUiActive = false;
        }

        public void ShowAnimated()
        {
            UpdatePositionAndSize();

            // 前回のフェードアウトで Visual.Opacity が 0 のままの場合に備えて即時リセット＆アニメーション開始
            ContentBorder.Opacity = 1;
            _appWindow.Show();
            SetForegroundWindow(_hwnd);
            this.Activate();

            if (!_isLoaded) _isLoaded = true;
            _contentVisual.StartAnimation("Scale", _showScaleAnim);
            _contentVisual.StartAnimation("Opacity", _showOpacityAnim);
        }

        private void RootGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.C:
                    ViewModel.CaptureRegionCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.W:
                    ViewModel.CaptureWindowCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.F:
                    ViewModel.CaptureFullscreenCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.P:
                    ViewModel.CapturePinCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.E:
                    ViewModel.CaptureOcrCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.R:
                    ViewModel.OpenRewindCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Escape:
                    ViewModel.HideCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }

        private static void AnimateCardScale(object sender, float scale, float damping, int periodMs)
        {
            if (sender is UIElement element)
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var compositor = visual.Compositor;

                var animation = compositor.CreateSpringVector3Animation();
                animation.Target = "Scale";
                animation.FinalValue = new Vector3(scale, scale, 1.0f);
                animation.DampingRatio = damping;
                animation.Period = TimeSpan.FromMilliseconds(periodMs);

                visual.CenterPoint = new Vector3((float)element.ActualSize.X / 2, (float)element.ActualSize.Y / 2, 0);
                visual.StartAnimation("Scale", animation);
            }
        }

        private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
            => AnimateCardScale(sender, 1.05f, 0.5f, 40);

        private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
            => AnimateCardScale(sender, 1.0f, 0.6f, 50);

        private void Card_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
            => AnimateCardScale(sender, 0.95f, 0.4f, 30);

        private void Card_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
            => AnimateCardScale(sender, 1.05f, 0.5f, 40);
    }
}
