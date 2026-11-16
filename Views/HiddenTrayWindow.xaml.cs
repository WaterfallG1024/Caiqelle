using Microsoft.UI.Xaml;

namespace Caiqelle.Views
{
    public sealed partial class HiddenTrayWindow : Window
    {
        public HiddenTrayWindow()
        {
            this.InitializeComponent();
            var hWndForIcon = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowIdForIcon = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWndForIcon);
            var appWindowForIcon = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowIdForIcon);
            // TODO: Caiqelle用アイコンを用意したら設定する
            // appWindowForIcon.SetIcon("Assets\\favicon.ico");

            var presenter = this.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
            if (presenter != null)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            // Hide the window off-screen
            this.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(-10000, -10000, 1, 1));
            this.AppWindow.Show(false);
            this.Hide(); // Ensure it's hidden from taskbar if possible, though ToolWindow is better
            
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            NativeMethods.RemoveWindowBorder(hwnd);
        }
    }
}
