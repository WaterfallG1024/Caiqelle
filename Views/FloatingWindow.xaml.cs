using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Numerics;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;

namespace Caiqelle.Views
{
    public sealed partial class FloatingWindow : Window
    {
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
            this.InitializeComponent();
            var hWndForIcon = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowIdForIcon = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWndForIcon);
            var appWindowForIcon = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowIdForIcon);
            // TODO: Caiqelle用アイコンを用意したら設定する
            // appWindowForIcon.SetIcon("Assets\\favicon.ico");

            ExtendsContentIntoTitleBar = true;
            this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();

            // Set borderless using AppWindow
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

            // ウィンドウの角を丸くする
            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_ROUND = 2; // 丸角
            NativeMethods.DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_ROUND, sizeof(int));

            // ウィンドウの白い線を完全に削除
            int DWMWA_BORDER_COLOR = 34;
            int DWM_BORDER_COLOR_NONE = -2; // 0xFFFFFFFE
            NativeMethods.DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref DWM_BORDER_COLOR_NONE, sizeof(int));

            // フレームをクライアント領域に拡張して枠線を非表示にする
            var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(hWnd, ref margins);

            // ウィンドウスタイルから枠線を除去
            NativeMethods.RemoveWindowBorder(hWnd);

            UpdatePositionAndSize();
        }

        private void UpdatePositionAndSize()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            double dpiScale = GetDpiForWindow(hWnd) / 96.0;
            int targetWidth = (int)(660 * dpiScale);
            int targetHeight = (int)(164 * dpiScale);
            appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));

            // マウスカーソルが存在するモニターを動的に取得
            GetCursorPos(out POINT cursorPos);
            var displayArea = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(cursorPos.X, cursorPos.Y), DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var workArea = displayArea.WorkArea;
                int x = workArea.X + (workArea.Width - targetWidth) / 2;
                int y = workArea.Y + (workArea.Height - targetHeight) / 2;
                appWindow.Move(new Windows.Graphics.PointInt32(x, y));
            }
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(this.RootGrid).Compositor;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ContentBorder);

            var springAnim = compositor.CreateSpringVector3Animation();
            springAnim.Target = "Scale";
            springAnim.InitialValue = new Vector3(0.95f, 0.95f, 1.0f);
            springAnim.FinalValue = new Vector3(1.0f, 1.0f, 1.0f);
            springAnim.DampingRatio = 0.7f;
            springAnim.Period = TimeSpan.FromMilliseconds(50);

            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.Target = "Opacity";
            opacityAnim.InsertKeyFrame(0.0f, 0.0f);
            opacityAnim.InsertKeyFrame(1.0f, 1.0f);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(150);

            visual.CenterPoint = new Vector3(330, 82, 0);
            visual.StartAnimation("Scale", springAnim);
            visual.StartAnimation("Opacity", opacityAnim);

            ContentBorder.Opacity = 1;
        }

        public void CloseWithAnimation()
        {
            var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(this.RootGrid).Compositor;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ContentBorder);

            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.Target = "Opacity";
            opacityAnim.InsertKeyFrame(1.0f, 0.0f);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(150);
            
            var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
            visual.StartAnimation("Opacity", opacityAnim);
            batch.End();

            batch.Completed += (s, e) =>
            {
                HideWindow();
            };
        }

        public void HideInstantlyAndClose()
        {
            HideWindow();
        }

        public void HideWindow()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.Hide();
            
            var app = (App)Application.Current;
            var hook = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Caiqelle.Services.HookService>(app.Services);
            hook.IsFloatingUiActive = false;
        }

        public void ShowAnimated()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            
            UpdatePositionAndSize();

            appWindow.Show();
            SetForegroundWindow(hwnd);
            this.Activate();

            var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(this.RootGrid).Compositor;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ContentBorder);

            var springAnim = compositor.CreateSpringVector3Animation();
            springAnim.Target = "Scale";
            springAnim.InitialValue = new Vector3(0.9f, 0.9f, 1.0f);
            springAnim.FinalValue = new Vector3(1.0f, 1.0f, 1.0f);
            springAnim.DampingRatio = 0.6f;
            springAnim.Period = TimeSpan.FromMilliseconds(50);

            var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.Target = "Opacity";
            opacityAnim.InsertKeyFrame(0.0f, 0.0f);
            opacityAnim.InsertKeyFrame(1.0f, 1.0f);
            opacityAnim.Duration = TimeSpan.FromMilliseconds(200);

            visual.CenterPoint = new Vector3(330, 82, 0);
            visual.StartAnimation("Scale", springAnim);
            visual.StartAnimation("Opacity", opacityAnim);
        }

        private void RootGrid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.C:
                    CaptureRegion_Tapped(this, null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.W:
                    CaptureWindow_Tapped(this, null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.F:
                    CaptureFullscreen_Tapped(this, null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.P:
                    CapturePin_Tapped(this, null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.E:
                    CaptureOCR_Tapped(this, null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Escape:
                    HideWindow();
                    e.Handled = true;
                    break;
            }
        }

        private async void CaptureRegion_Tapped(object? sender, RoutedEventArgs? e)
        {
            HideWindow();
            var app = (App)Application.Current;
            if (app.CaptureManager != null) await app.CaptureManager.StartCaptureAsync(Services.CapturedKey.C);
        }

        private async void CaptureWindow_Tapped(object? sender, RoutedEventArgs? e)
        {
            HideWindow();
            var app = (App)Application.Current;
            if (app.CaptureManager != null) await app.CaptureManager.StartCaptureAsync(Services.CapturedKey.W);
        }

        private async void CaptureFullscreen_Tapped(object? sender, RoutedEventArgs? e)
        {
            HideWindow();
            var app = (App)Application.Current;
            if (app.CaptureManager != null) await app.CaptureManager.StartCaptureAsync(Services.CapturedKey.F);
        }

        private async void CapturePin_Tapped(object? sender, RoutedEventArgs? e)
        {
            HideWindow();
            var app = (App)Application.Current;
            if (app.CaptureManager != null) await app.CaptureManager.StartCaptureAsync(Services.CapturedKey.P);
        }

        private async void CaptureOCR_Tapped(object? sender, RoutedEventArgs? e)
        {
            HideWindow();
            var app = (App)Application.Current;
            if (app.CaptureManager != null) await app.CaptureManager.StartCaptureAsync(Services.CapturedKey.E);
        }

        private void Card_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is UIElement element)
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var compositor = visual.Compositor;
                
                var animation = compositor.CreateSpringVector3Animation();
                animation.Target = "Scale";
                animation.FinalValue = new Vector3(1.05f, 1.05f, 1.0f);
                animation.DampingRatio = 0.5f;
                animation.Period = TimeSpan.FromMilliseconds(40);
                
                visual.CenterPoint = new Vector3((float)element.ActualSize.X / 2, (float)element.ActualSize.Y / 2, 0);
                visual.StartAnimation("Scale", animation);
            }
        }

        private void Card_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is UIElement element)
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var compositor = visual.Compositor;
                
                var animation = compositor.CreateSpringVector3Animation();
                animation.Target = "Scale";
                animation.FinalValue = new Vector3(1.0f, 1.0f, 1.0f);
                animation.DampingRatio = 0.6f;
                animation.Period = TimeSpan.FromMilliseconds(50);
                
                visual.CenterPoint = new Vector3((float)element.ActualSize.X / 2, (float)element.ActualSize.Y / 2, 0);
                visual.StartAnimation("Scale", animation);
            }
        }

        private void Card_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is UIElement element)
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var compositor = visual.Compositor;
                
                var animation = compositor.CreateSpringVector3Animation();
                animation.Target = "Scale";
                animation.FinalValue = new Vector3(0.95f, 0.95f, 1.0f);
                animation.DampingRatio = 0.4f;
                animation.Period = TimeSpan.FromMilliseconds(30);
                
                visual.CenterPoint = new Vector3((float)element.ActualSize.X / 2, (float)element.ActualSize.Y / 2, 0);
                visual.StartAnimation("Scale", animation);
            }
        }

        private void Card_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is UIElement element)
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var compositor = visual.Compositor;
                
                var animation = compositor.CreateSpringVector3Animation();
                animation.Target = "Scale";
                animation.FinalValue = new Vector3(1.05f, 1.05f, 1.0f);
                animation.DampingRatio = 0.5f;
                animation.Period = TimeSpan.FromMilliseconds(40);
                
                visual.CenterPoint = new Vector3((float)element.ActualSize.X / 2, (float)element.ActualSize.Y / 2, 0);
                visual.StartAnimation("Scale", animation);
            }
        }
    }
}
