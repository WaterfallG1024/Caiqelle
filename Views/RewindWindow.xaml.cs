using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Caiqelle.Services;
using System.Threading.Tasks;

namespace Caiqelle.Views
{
    public sealed partial class RewindWindow : Window
    {
        private RewindService _rewindService;
        private RewindFrame[] _frames;
        private int _currentIndex = 0;
        private EditorWindow _editorWindow;

        public RewindWindow(RewindService rewindService, EditorWindow editorWindow)
        {
            this.InitializeComponent();
            var hWndForIcon = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowIdForIcon = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWndForIcon);
            var appWindowForIcon = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowIdForIcon);
            // TODO: Caiqelle用アイコンを用意したら設定する
            // appWindowForIcon.SetIcon("Assets\\favicon.ico");
            ExtendsContentIntoTitleBar = true;
            _rewindService = rewindService;
            _editorWindow = editorWindow;

            // プレビュー表示中はリワインド録画を一時停止（プレビュー画面自体の映り込みを防止）
            _rewindService.Pause();
            
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            var presenter = appWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
            if (presenter != null)
            {
                presenter.Maximize();
            }

            // OS レベルで本ウィンドウを画面キャプチャ・スクリーンショットから完全に除外（透過）
            const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
            SetWindowDisplayAffinity(hWnd, WDA_EXCLUDEFROMCAPTURE);

            bool isClosing = false;

            // 最小化・復帰時のリワインド録画連動
            appWindow.Changed += (s, args) =>
            {
                if (isClosing) return;

                if (args.DidPresenterChange || args.DidSizeChange || args.DidVisibilityChange)
                {
                    if (s.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op)
                    {
                        if (op.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
                        {
                            _rewindService.Resume(); // 最小化中は裏で画面録画を継続
                        }
                        else
                        {
                            _rewindService.Pause(); // 画面復帰時は映り込み防止のため一時停止
                        }
                    }
                }
            };

            this.VisibilityChanged += (s, args) =>
            {
                if (isClosing) return;

                if (!args.Visible)
                {
                    _rewindService.Resume();
                }
                else
                {
                    _rewindService.Pause();
                }
            };

            this.Closed += (s, e) => {
                isClosing = true;
                _rewindService.Resume(); // 録画を確実に再開
                _frames = Array.Empty<RewindFrame>(); // Free memory
            };

            // Load frames (newest to oldest or vice versa)
            _frames = _rewindService.GetBufferedFrames();
            if (_frames.Length > 0)
            {
                // We will sort so that index 0 is the newest (most recent)
                _frames = _frames.OrderByDescending(f => f.Timestamp).ToArray();
                _currentIndex = 0;
                UpdatePreviewAsync();
            }
            else
            {
                IndexLabel.Text = "0/0";
                TimestampLabel.Text = "録画されたフレームがありません";
            }
            // ウィンドウを確実にフォアグラウンドに表示
            BringToForeground();
        }

        private async void UpdatePreviewAsync()
        {
            if (_frames.Length == 0) return;

            IndexLabel.Text = $"{_currentIndex + 1}/{_frames.Length}";
            
            var frame = _frames[_currentIndex];
            TimestampLabel.Text = $"記録時刻: {frame.Timestamp:yyyy/MM/dd HH:mm:ss}  •  矢印キー / ホイールで移動";

            // 端のナビゲーションボタンの視覚的フィードバック
            LeftArrowBtn.Opacity = _currentIndex < _frames.Length - 1 ? 1.0 : 0.35;
            LeftArrowBtn.IsEnabled = _currentIndex < _frames.Length - 1;
            RightArrowBtn.Opacity = _currentIndex > 0 ? 1.0 : 0.35;
            RightArrowBtn.IsEnabled = _currentIndex > 0;
            
            try
            {
                if (frame.JpegBytes != null && frame.JpegBytes.Length > 0)
                {
                    // InMemoryRandomAccessStream に書き込み、先頭にシークしてから BitmapImage に渡す
                    var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    await stream.WriteAsync(frame.JpegBytes.AsBuffer());
                    stream.Seek(0);
                    
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    
                    PreviewImage.Source = bitmap;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RewindWindow] Image render error: {ex.Message}");
            }
        }

        private void LeftArrowBtn_Click(object sender, RoutedEventArgs e)
        {
            Navigate(1); // Go to older frame (higher index)
        }

        private void RightArrowBtn_Click(object sender, RoutedEventArgs e)
        {
            Navigate(-1); // Go to newer frame (lower index)
        }

        private void Window_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var properties = e.GetCurrentPoint(this.Content).Properties;
            if (properties.MouseWheelDelta > 0)
            {
                Navigate(-1); // Up -> newer frame (lower index)
            }
            else
            {
                Navigate(1); // Down -> older frame (higher index)
            }
        }

        private void Grid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Left) Navigate(1);
            else if (e.Key == Windows.System.VirtualKey.Right) Navigate(-1);
            else if (e.Key == Windows.System.VirtualKey.Enter) OpenCurrentFrameInEditor();
            else if (e.Key == Windows.System.VirtualKey.Escape) this.Close();
        }

        private void Navigate(int offset)
        {
            if (_frames.Length == 0) return;
            
            _currentIndex += offset;
            if (_currentIndex < 0) _currentIndex = 0;
            if (_currentIndex >= _frames.Length) _currentIndex = _frames.Length - 1;
            
            UpdatePreviewAsync();
        }

        private void PreviewImage_Tapped(object sender, TappedRoutedEventArgs e)
        {
            OpenCurrentFrameInEditor();
        }

        private void OpenInEditorBtn_Click(object sender, RoutedEventArgs e)
        {
            OpenCurrentFrameInEditor();
        }

        private async void OpenCurrentFrameInEditor()
        {
            if (_frames.Length > 0)
            {
                var frame = _frames[_currentIndex];
                await _editorWindow.LoadImageAsync(frame.JpegBytes);
                _editorWindow.Activate();
                this.Close();
            }
        }

        /// <summary>
        /// ウィンドウを強制的にフォアグラウンドに表示する
        /// </summary>
        private void BringToForeground()
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ShowWindow(hWnd, SW_SHOWMAXIMIZED);
            SetForegroundWindow(hWnd);
        }

        private const int SW_SHOWMAXIMIZED = 3;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
