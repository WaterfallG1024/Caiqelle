using Caiqelle.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;
using WinRT.Interop;

namespace Caiqelle.Views
{
    public sealed partial class FolderSelectionWindow : Window
    {
        public FolderSelectionViewModel ViewModel { get; }

        private TaskCompletionSource<bool>? _tcs;
        private readonly AppWindow _appWindow;

        public FolderSelectionWindow(App app)
        {
            ViewModel = app.Services.GetRequiredService<FolderSelectionViewModel>();
            ViewModel.RequestCloseWithResult = CloseWithResult;
            ViewModel.RequestRestoreDummyFocus = () => DummyFocus?.Focus(FocusState.Programmatic);
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(FolderSelectionViewModel.IsCreateErrorOpen))
                {
                    UpdateWindowHeight();
                }
            };

            this.InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            app.Services.GetRequiredService<Services.ThemeService>().RegisterWindow(this, RootGrid);

            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            // TODO: Caiqelle用アイコンを用意したら設定する
            _appWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Caiqelle.ico"));

            ConfigureWindowChrome(hwnd);
            CenterOnCursorDisplay(hwnd);
            this.Closed += (_, _) => _tcs?.TrySetResult(false);
        }

        private void ConfigureWindowChrome(IntPtr hwnd)
        {
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
            }

            // ウィンドウの角を丸くする
            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_ROUND = 2;
            NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_ROUND, sizeof(int));

            // OS レベルでキャプチャ除外
            const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

            var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            NativeMethods.RemoveWindowBorder(hwnd);
        }

        private void UpdateWindowHeight()
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            double dpiScale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
            int baseHeight = ViewModel.IsCreateErrorOpen ? 595 : 525;
            int targetWidth = (int)(410 * dpiScale);
            int targetHeight = (int)(baseHeight * dpiScale);
            _appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));
        }

        private void CenterOnCursorDisplay(IntPtr hwnd)
        {
            double dpiScale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
            int baseHeight = ViewModel.IsCreateErrorOpen ? 595 : 525;
            int targetWidth = (int)(410 * dpiScale);
            int targetHeight = (int)(baseHeight * dpiScale);
            _appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));

            NativeMethods.GetCursorPos(out NativeMethods.POINT cursorPos);
            var displayArea = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(cursorPos.X, cursorPos.Y), DisplayAreaFallback.Primary);
            if (displayArea != null)
            {
                var workArea = displayArea.WorkArea;
                int x = workArea.X + (workArea.Width - targetWidth) / 2;
                int y = workArea.Y + (workArea.Height - targetHeight) / 2;
                _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
            }
        }

        public void ReloadMappings() => ViewModel.ReloadMappings();

        public Task<bool> ShowAsync(byte[] imageBytes)
        {
            _tcs = new TaskCompletionSource<bool>();
            ViewModel.PrepareForShow(imageBytes);

            var hwnd = WindowNative.GetWindowHandle(this);
            CenterOnCursorDisplay(hwnd);

            _appWindow.Show();
            this.Activate();

            NativeMethods.SetForegroundWindow(hwnd);
            DummyFocus.Focus(FocusState.Programmatic);

            return _tcs.Task;
        }

        private void CloseWithResult(bool result)
        {
            _tcs?.TrySetResult(result);
            _appWindow.Hide();
        }

        public Brush GetPageIndicatorBrush(bool isErrorState)
        {
            if (isErrorState)
            {
                return new SolidColorBrush(Microsoft.UI.Colors.Red);
            }

            if (Application.Current.Resources.TryGetValue("AfterGlowTextPrimaryBrush", out var brush) && brush is Brush primaryBrush)
            {
                return primaryBrush;
            }

            return new SolidColorBrush(Microsoft.UI.Colors.White);
        }

        private void CreateInput_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                ViewModel.CreateFolderCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                DummyFocus?.Focus(FocusState.Programmatic);
                e.Handled = true;
            }
        }

        private void Grid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                ViewModel.CancelCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (e.OriginalSource is TextBox) return;

            string keyStr = e.Key.ToString();
            if (keyStr.StartsWith("Number")) keyStr = keyStr.Replace("Number", "");
            if (keyStr.StartsWith("NumberPad")) keyStr = keyStr.Replace("NumberPad", "");

            // JIS/US配列の記号キーを仮想キーコードから変換
            switch ((int)e.Key)
            {
                case 192: keyStr = "@"; break;
                case 219: keyStr = "["; break;
                case 221: keyStr = "]"; break;
                case 189: keyStr = "-"; break;
                case 222: keyStr = "^"; break;
                case 187: keyStr = ";"; break;
                case 186: keyStr = ":"; break;
                case 220: keyStr = "\\"; break;
                case 188: keyStr = ","; break;
                case 190: keyStr = "."; break;
                case 191: keyStr = "/"; break;
                case 226: keyStr = "\\"; break;
            }

            if (ViewModel.TrySelectByKey(keyStr))
            {
                e.Handled = true;
            }
        }

        private void Grid_CharacterReceived(UIElement sender, Microsoft.UI.Xaml.Input.CharacterReceivedRoutedEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;

            string charStr = ((char)e.Character).ToString();
            if (ViewModel.TrySelectByKey(charStr))
            {
                e.Handled = true;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

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

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const int GWL_STYLE = -16;
        private const int WS_BORDER = 0x00800000;
        private const int WS_DLGFRAME = 0x00400000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_CAPTION = 0x00C00000;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        public static void RemoveWindowBorder(IntPtr hwnd)
        {
            IntPtr style = GetWindowLongPtr(hwnd, GWL_STYLE);
            long newStyle = style.ToInt64() & ~(WS_BORDER | WS_DLGFRAME | WS_THICKFRAME | WS_CAPTION);
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(newStyle));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }
}
