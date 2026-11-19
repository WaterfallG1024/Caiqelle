using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT.Interop;
using Microsoft.UI;

namespace Caiqelle.Views
{
    public sealed partial class PinWindow : Window
    {
        private AppWindow _appWindow;
        private byte[] _imageBytes;

        public PinWindow(byte[] imageBytes)
        {
            this.InitializeComponent();
            if (((App)Application.Current).Services.GetService(typeof(Services.ThemeService)) is Services.ThemeService themeService)
            {
                themeService.RegisterWindow(this, RootGrid);
            }
            var hWndForIcon = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowIdForIcon = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWndForIcon);
            var appWindowForIcon = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowIdForIcon);
            // TODO: Caiqelle用アイコンを用意したら設定する
            // appWindowForIcon.SetIcon("Assets\\favicon.ico");
            _imageBytes = imageBytes;

            var hWnd = WindowNative.GetWindowHandle(this);
            WindowId wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            _appWindow = AppWindow.GetFromWindowId(wndId);

            // Hide default title bar
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(RootGrid);

            // Always on Top
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(true, false);
            }

            // Size window to fit image roughly, constrained to max size
            int targetWidth = 800;
            int targetHeight = 600;

            try
            {
                using var ms = new MemoryStream(_imageBytes);
                using var tempBmp = new Bitmap(ms);
                targetWidth = tempBmp.Width;
                targetHeight = tempBmp.Height;
            }
            catch { }

            // Add a little padding or scale if too large
            if (targetWidth > 1200) targetWidth = 1200;
            if (targetHeight > 1000) targetHeight = 1000;

            _appWindow.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));

            LoadImage();
            this.Activated += (s, e) => RootGrid.Focus(FocusState.Programmatic);
        }

        private async void LoadImage()
        {
            using var ms = new MemoryStream(_imageBytes);
            var randomAccessStream = new InMemoryRandomAccessStream();
            var outputStream = randomAccessStream.GetOutputStreamAt(0);
            var dataWriter = new DataWriter(outputStream);
            dataWriter.WriteBytes(ms.ToArray());
            await dataWriter.StoreAsync();
            await outputStream.FlushAsync();
            randomAccessStream.Seek(0);

            var bitmapImage = new BitmapImage();
            await bitmapImage.SetSourceAsync(randomAccessStream);
            PinnedImage.Source = bitmapImage;
        }

        private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            OverlayPanel.Opacity = 1.0;
        }

        private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            OverlayPanel.Opacity = 0.0;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_imageBytes != null && _imageBytes.Length > 0)
            {
                var folderWindow = new FolderSelectionWindow((App)Application.Current);
                await folderWindow.ShowAsync(_imageBytes);
            }
        }

        private async void CopyBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_imageBytes != null && _imageBytes.Length > 0)
            {
                var clipboardService = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Services.ClipboardService>(((App)Application.Current).Services);
                await clipboardService.CopyImageAsync(_imageBytes, showNotification: true, DispatcherQueue);
            }
        }

        // To allow dragging even if SetTitleBar is tricky with opacity sliders
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;

        private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // Only drag if not clicking on the overlay
            if (e.OriginalSource is Microsoft.UI.Xaml.Shapes.Rectangle || e.OriginalSource is Microsoft.UI.Xaml.Controls.Image)
            {
                var hWnd = WindowNative.GetWindowHandle(this);
                ReleaseCapture();
                SendMessage(hWnd, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }
    }
}
