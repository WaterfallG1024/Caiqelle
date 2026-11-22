using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Caiqelle.Views
{
    public sealed partial class WindowSelectionOverlay : Window
    {
        public Rectangle SelectedRegion { get; private set; }
        public bool IsSelected { get; private set; }
        public OverlayWindow.CaptureAction ActionTaken { get; private set; } = OverlayWindow.CaptureAction.None;

        private Rectangle _virtualBounds;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(System.Drawing.Point Point);

        [DllImport("user32.dll", ExactSpelling = true)]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        private const uint GA_ROOT = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        private List<IntPtr> _windowsList = new List<IntPtr>();
        private uint _currentProcessId;

        public event Action? CaptureCompleted;

        private readonly IntPtr _hwnd;
        private readonly AppWindow _appWindow;
        private Rectangle _lastBounds = Rectangle.Empty;

        public WindowSelectionOverlay()
        {
            this.InitializeComponent();
            ExtendsContentIntoTitleBar = true;

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

            // ウィンドウの白い境界線を完全に削除
            int DWMWA_BORDER_COLOR = 34;
            int DWM_BORDER_COLOR_NONE = -2; // 0xFFFFFFFE
            NativeMethods.DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref DWM_BORDER_COLOR_NONE, sizeof(int));

            var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            NativeMethods.RemoveWindowBorder(_hwnd);

            _currentProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        public void UpdateAndShow(SoftwareBitmapSource frozenImage, Rectangle bounds)
        {
            _virtualBounds = bounds;
            BackgroundImage.Source = frozenImage;

            if (_lastBounds != bounds)
            {
                _lastBounds = bounds;
                _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            }

            HighlightBox.Visibility = Visibility.Collapsed;
            IsSelected = false;

            EnumerateVisibleWindows();

            _appWindow.Show();
            this.Activate();

            NativeMethods.SetForegroundWindow(_hwnd);
            DrawCanvas.Focus(FocusState.Programmatic);
        }

        public void Cancel()
        {
            IsSelected = false;
            ActionTaken = OverlayWindow.CaptureAction.None;
            this.Hide();
            CaptureCompleted?.Invoke();
        }

        private void EnumerateVisibleWindows()
        {
            _windowsList.Clear();
            IntPtr thisHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == thisHwnd) return true;
                if (!IsWindowVisible(hWnd)) return true;
                if (IsIconic(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == _currentProcessId) return true; // 自プロセスのウィンドウは除外

                var sb = new System.Text.StringBuilder(256);
                GetClassName(hWnd, sb, 256);
                string className = sb.ToString();
                if (className == "Progman" || className == "WorkerW") return true;

                if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rect, Marshal.SizeOf(typeof(RECT))) == 0)
                {
                    int w = rect.Right - rect.Left;
                    int h = rect.Bottom - rect.Top;
                    if (w > 50 && h > 50)
                    {
                        _windowsList.Add(hWnd);
                    }
                }
                return true;
            }, IntPtr.Zero);
        }

        private void Window_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var currentPoint = e.GetCurrentPoint(DrawCanvas).Position;
            int screenX = (int)currentPoint.X + _virtualBounds.X;
            int screenY = (int)currentPoint.Y + _virtualBounds.Y;

            IntPtr targetHwnd = IntPtr.Zero;
            foreach (var hWnd in _windowsList)
            {
                if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rect, Marshal.SizeOf(typeof(RECT))) == 0)
                {
                    if (screenX >= rect.Left && screenX <= rect.Right &&
                        screenY >= rect.Top && screenY <= rect.Bottom)
                    {
                        targetHwnd = hWnd;
                        HighlightWindow(rect);
                        break;
                    }
                }
            }

            if (targetHwnd == IntPtr.Zero)
            {
                HighlightBox.Visibility = Visibility.Collapsed;
            }
        }

        private void HighlightWindow(RECT rect)
        {
            int relX = rect.Left - _virtualBounds.X;
            int relY = rect.Top - _virtualBounds.Y;
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;

            Canvas.SetLeft(HighlightBox, relX);
            Canvas.SetTop(HighlightBox, relY);
            HighlightBox.Width = w;
            HighlightBox.Height = h;
            HighlightBox.Visibility = Visibility.Visible;

            SelectedRegion = new Rectangle(relX, relY, w, h);
        }

        private void Window_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (HighlightBox.Visibility == Visibility.Visible && SelectedRegion.Width > 0 && SelectedRegion.Height > 0)
            {
                IsSelected = true;
                ActionTaken = OverlayWindow.CaptureAction.Save;
                this.Hide();
                CaptureCompleted?.Invoke();
            }
        }

        private void Window_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                Cancel();
            }
        }
    }
}
