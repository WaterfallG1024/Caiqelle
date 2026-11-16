using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using WinRT.Interop;
using Caiqelle.Services;

namespace Caiqelle.Views
{
    public sealed partial class FolderSelectionWindow : Window
    {
        public ObservableCollection<FolderMapping> CurrentPageMappings { get; } = new();
        private System.Collections.Generic.List<FolderMapping> _allMappings = new();
        private int _currentPage = 1;
        private int _totalPages = 1;
        
        private byte[]? _imageBytes;
        private readonly App _app;
        private TaskCompletionSource<bool>? _tcs;
        private readonly AppWindow _appWindow;

        public FolderSelectionWindow(App app)
        {
            this.InitializeComponent();
            var hWndForIcon = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowIdForIcon = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWndForIcon);
            var appWindowForIcon = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowIdForIcon);
            // TODO: Caiqelle用アイコンを用意したら設定する
            // appWindowForIcon.SetIcon("Assets\\favicon.ico");
            _app = app;

            ExtendsContentIntoTitleBar = true;
            this.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
            
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            
            // Set TopMost and borderless
            var presenter = _appWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsResizable = false;
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
            }

            // ウィンドウの角を丸くする
            int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            int DWMWCP_ROUND = 2; // 丸角
            NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref DWMWCP_ROUND, sizeof(int));

            // ウィンドウの白い線を完全に削除
            int DWMWA_BORDER_COLOR = 34;
            int DWM_BORDER_COLOR_NONE = -2; // 0xFFFFFFFE
            NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref DWM_BORDER_COLOR_NONE, sizeof(int));

            // OS レベルでキャプチャ除外
            const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

            var margins = new NativeMethods.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            NativeMethods.RemoveWindowBorder(hwnd);

            // Center window on screen (縦長スリム長方形)
            double dpiScale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
            int targetWidth = (int)(380 * dpiScale);
            int targetHeight = (int)(460 * dpiScale);
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

            ReloadMappings();
        }

        public void ReloadMappings()
        {
            _allMappings.Clear();
            if (_app.Services != null)
            {
                var settingsManager = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<SettingsManager>(_app.Services);
                _allMappings.AddRange(settingsManager.ImageFolderMappings);
            }
            _totalPages = Math.Max(1, (int)Math.Ceiling(_allMappings.Count / 10.0));
            LoadPage(1);
        }

        private void LoadPage(int page)
        {
            _currentPage = page;
            CurrentPageMappings.Clear();
            int startIndex = (page - 1) * 10;
            int count = Math.Min(10, _allMappings.Count - startIndex);
            for (int i = 0; i < count; i++)
            {
                CurrentPageMappings.Add(_allMappings[startIndex + i]);
            }
            PageIndicator.Text = $"{_currentPage}/{_totalPages}";
            PrevButton.Visibility = _currentPage > 1 ? Visibility.Visible : Visibility.Collapsed;
            NextButton.Visibility = _currentPage < _totalPages ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1) LoadPage(_currentPage - 1);
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < _totalPages) LoadPage(_currentPage + 1);
        }

        public Task<bool> ShowAsync(byte[] imageBytes)
        {
            _imageBytes = imageBytes;
            _tcs = new TaskCompletionSource<bool>();
            WindowTitle.Text = "保存先選択";
            FileNameTextBox.Visibility = Visibility.Visible;
            FileNameTextBox.Text = "";
            ReloadMappings();
            
            var hwnd = WindowNative.GetWindowHandle(this);
            double dpiScale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
            int targetWidth = (int)(380 * dpiScale);
            int targetHeight = (int)(460 * dpiScale);
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

            _appWindow.Show();
            this.Activate();
            
            NativeMethods.SetForegroundWindow(hwnd);
            DummyFocus.Focus(FocusState.Programmatic);

            return _tcs.Task;
        }

        private void Grid_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                CloseButton_Click(null, null);
                return;
            }
            
            if (e.OriginalSource is TextBox) return;

            string keyStr = e.Key.ToString();
            if (keyStr.StartsWith("Number")) keyStr = keyStr.Replace("Number", "");
            if (keyStr.StartsWith("NumberPad")) keyStr = keyStr.Replace("NumberPad", "");
            
            // Map common Oem keys (JIS/US layout) using raw virtual key codes
            switch ((int)e.Key)
            {
                case 192: keyStr = "@"; break; // VK_OEM_3
                case 219: keyStr = "["; break; // VK_OEM_4
                case 221: keyStr = "]"; break; // VK_OEM_6
                case 189: keyStr = "-"; break; // VK_OEM_MINUS
                case 222: keyStr = "^"; break; // VK_OEM_7
                case 187: keyStr = ";"; break; // VK_OEM_PLUS
                case 186: keyStr = ":"; break; // VK_OEM_1
                case 220: keyStr = "\\"; break; // VK_OEM_5
                case 188: keyStr = ","; break; // VK_OEM_COMMA
                case 190: keyStr = "."; break; // VK_OEM_PERIOD
                case 191: keyStr = "/"; break; // VK_OEM_2
                case 226: keyStr = "\\"; break; // VK_OEM_102 (Backslash on JIS)
            }

            // Check if key matches any mapping on CURRENT page
            foreach (var mapping in CurrentPageMappings)
            {
                if (string.Equals(mapping.Key, keyStr, StringComparison.OrdinalIgnoreCase))
                {
                    SelectFolderAndClose(mapping);
                    e.Handled = true;
                    return;
                }
            }
        }

        private void Grid_CharacterReceived(UIElement sender, Microsoft.UI.Xaml.Input.CharacterReceivedRoutedEventArgs e)
        {
            if (e.OriginalSource is TextBox) return;

            string charStr = ((char)e.Character).ToString();
            if (string.IsNullOrWhiteSpace(charStr)) return;

            foreach (var mapping in CurrentPageMappings)
            {
                if (string.Equals(mapping.Key, charStr, StringComparison.OrdinalIgnoreCase))
                {
                    SelectFolderAndClose(mapping);
                    e.Handled = true;
                    return;
                }
            }
        }

        private void CloseButton_Click(object? sender, RoutedEventArgs? e)
        {
            _tcs?.TrySetResult(false);
            _appWindow.Hide();
        }

        private void FolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is FolderMapping mapping)
            {
                SelectFolderAndClose(mapping);
            }
        }

        private void SelectFolderAndClose(FolderMapping mapping)
        {
            SaveImageToFolderAndClose(mapping.FolderName);
        }

        private void SaveImageToFolderAndClose(string folderPath)
        {
            if (_imageBytes == null || _imageBytes.Length == 0)
            {
                _tcs?.TrySetResult(false);
                _appWindow.Hide();
                return;
            }

            string inputName = FileNameTextBox.Text.Trim();
            PageIndicator.Text = "保存中...";

            Task.Run(() => 
            {
                try
                {
                    string finalFolderPath = folderPath;
                    if (!Path.IsPathRooted(finalFolderPath))
                    {
                        string myPictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                        finalFolderPath = Path.Combine(myPictures, "AGShot", folderPath);
                    }

                    if (!Directory.Exists(finalFolderPath))
                    {
                        Directory.CreateDirectory(finalFolderPath);
                    }

                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string fileName;
                    if (!string.IsNullOrEmpty(inputName))
                    {
                        // Ensure safe filename
                        foreach (char c in Path.GetInvalidFileNameChars())
                        {
                            inputName = inputName.Replace(c, '_');
                        }
                        fileName = $"{inputName}_{timestamp}.png";
                    }
                    else
                    {
                        fileName = $"Caiqelle_{timestamp}.png";
                    }
                    
                    string fullPath = Path.Combine(finalFolderPath, fileName);
                    File.WriteAllBytes(fullPath, _imageBytes);

                    var notificationService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<NotificationService>(_app.Services);
                    
                    DispatcherQueue.TryEnqueue(() => 
                    {
                        notificationService.ShowNotification($"{folderPath} に保存しました", "\uE8D5");
                        _tcs?.TrySetResult(true);
                        _appWindow.Hide();
                    });
                }
                catch (Exception ex)
                {
                    DispatcherQueue.TryEnqueue(() => 
                    {
                        PageIndicator.Text = $"エラー: {ex.Message}";
                        PageIndicator.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
                    });
                }
            });
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

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const long WS_BORDER = 0x00800000L;
        public const long WS_THICKFRAME = 0x00040000L;
        public const long WS_CAPTION = 0x00C00000L;

        /// <summary>
        /// ウィンドウの枠線スタイルを除去する
        /// </summary>
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
